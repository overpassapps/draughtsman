using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Sidecar;

/// <summary>
/// Spawns <c>node sidecar.js</c> once, keeps it alive, restarts it with exponential backoff when it dies, and
/// multiplexes requests over the one process by JSON-RPC id. A process that fails to start makes every request
/// fail fast with <see cref="SidecarUnavailableException"/> until the backoff elapses, so the server stays up and
/// reports degraded rather than going down with it (ADR 0002).
/// </summary>
public sealed class NodeSidecarClient : ISidecarClient, IAsyncDisposable
{
    private readonly SidecarOptions _options;
    private readonly ILogger<NodeSidecarClient> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private volatile SidecarStatus _status = new(SidecarState.Starting);
    private SidecarProcess? _process;
    private TimeSpan _restartDelay;
    private DateTime _nextStartAllowedUtc = DateTime.MinValue;
    private int _starts;
    private volatile bool _disposed;

    public NodeSidecarClient(SidecarOptions options, ILogger<NodeSidecarClient> logger)
    {
        _options = options;
        _logger = logger;
        _restartDelay = options.InitialRestartDelay;
    }

    public SidecarStatus Status => _status;

    public Task<SidecarHealth> PingAsync(CancellationToken cancellationToken = default)
    {
        return InvokeAsync<SidecarHealth>("ping", null, _options.StartupTimeout, cancellationToken);
    }

    public async Task<LayoutResult> LayoutAsync(Document document, LayoutOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Type == DocumentType.Sequence)
        {
            // Nothing to place: a sequence diagram's geometry is derived from its edge and node order on every render
            // (sequence.scene), so layout hands the document back untouched, with no warning.
            return new LayoutResult(document, []);
        }

        LayoutEnvelope envelope = await InvokeAsync<LayoutEnvelope>("layout.elk", new { document, options }, _options.LayoutTimeout, cancellationToken).ConfigureAwait(false);
        return new LayoutResult(ReadDocument(envelope.Document, "layout.elk"), envelope.Warnings ?? []);
    }

    public async Task<IReadOnlyDictionary<string, EdgeRoute>> RouteAsync(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        RouteEnvelope envelope = await InvokeAsync<RouteEnvelope>("route.document", new { document }, _options.LayoutTimeout, cancellationToken).ConfigureAwait(false);
        return envelope.Routes ?? new Dictionary<string, EdgeRoute>();
    }

    public async Task<SequenceScene> SequenceSceneAsync(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        SequenceSceneEnvelope envelope = await InvokeAsync<SequenceSceneEnvelope>("sequence.scene", new { document }, _options.LayoutTimeout, cancellationToken).ConfigureAwait(false);
        return envelope.Scene ?? throw new SidecarException("The sidecar's answer to sequence.scene held no scene.");
    }

    public async Task<Document> YjsToJsonAsync(byte[]? state, IReadOnlyList<byte[]>? updates = null, CancellationToken cancellationToken = default)
    {
        if (state is null && (updates is null || updates.Count == 0))
        {
            throw new ArgumentException("Either a state or at least one update is required.", nameof(state));
        }

        var parameters = new { state = ToBase64(state), updates = updates?.Select(Convert.ToBase64String).ToArray() };
        DocumentEnvelope envelope = await InvokeAsync<DocumentEnvelope>("yjs.toJson", parameters, _options.YjsTimeout, cancellationToken).ConfigureAwait(false);
        return ReadDocument(envelope.Document, "yjs.toJson");
    }

    public async Task<byte[]> YjsFromJsonAsync(Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        StateEnvelope envelope = await InvokeAsync<StateEnvelope>("yjs.fromJson", new { document }, _options.YjsTimeout, cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(envelope.State);
    }

    public async Task<YjsState> ApplyUpdatesAsync(byte[]? state, IReadOnlyList<byte[]> updates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var parameters = new { state = ToBase64(state), updates = updates.Select(Convert.ToBase64String).ToArray() };
        StateEnvelope envelope = await InvokeAsync<StateEnvelope>("yjs.applyUpdates", parameters, _options.YjsTimeout, cancellationToken).ConfigureAwait(false);
        return new YjsState(Convert.FromBase64String(envelope.State), Convert.FromBase64String(envelope.StateVector));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is not null)
            {
                await _process.DisposeAsync().ConfigureAwait(false);
                _process = null;
            }
        }
        finally
        {
            _startGate.Release();
        }

        _status = new SidecarStatus(SidecarState.Stopped) { Restarts = RestartCount() };
        _startGate.Dispose();
        _stopping.Dispose();
    }

    private async Task<TResult> InvokeAsync<TResult>(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SidecarProcess process = await EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
        JsonElement result = await process.InvokeAsync(method, parameters, timeout, cancellationToken).ConfigureAwait(false);
        return Deserialize<TResult>(result, method);
    }

    /// <summary>
    /// Hands out the running process or starts one. While a backoff runs, requests fail fast with the recorded
    /// error rather than queueing behind it; only the exit watcher, which sleeps the backoff out first, waits.
    /// </summary>
    private async Task<SidecarProcess> EnsureRunningAsync(CancellationToken cancellationToken, bool failFastDuringBackoff = true)
    {
        SidecarProcess? current = _process;
        if (current is { IsRunning: true })
        {
            return current;
        }

        if (failFastDuringBackoff)
        {
            ThrowIfBackingOff();
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            current = _process;
            if (current is { IsRunning: true })
            {
                return current;
            }

            if (failFastDuringBackoff)
            {
                ThrowIfBackingOff();
            }

            if (current is not null)
            {
                await current.DisposeAsync().ConfigureAwait(false);
                _process = null;
            }

            return await StartLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void ThrowIfBackingOff()
    {
        if (DateTime.UtcNow < _nextStartAllowedUtc)
        {
            throw new SidecarUnavailableException(_status.Error ?? "The sidecar is not available.");
        }
    }

    /// <summary>Resolves node and the bundle, spawns, and pings. Called with the start gate held.</summary>
    private async Task<SidecarProcess> StartLockedAsync(CancellationToken cancellationToken)
    {
        string nodePath;
        string scriptPath;
        SidecarProcess process;
        try
        {
            nodePath = SidecarLocator.ResolveNode(_options.NodePath, _options.BaseDirectory);
            scriptPath = SidecarLocator.ResolveScript(_options.ScriptPath, _options.BaseDirectory);
            process = SidecarProcess.Start(nodePath, scriptPath, _logger);
        }
        catch (SidecarUnavailableException ex)
        {
            MarkUnavailable(ex.Message);
            throw;
        }

        _starts++;
        _ = WatchAsync(process);

        try
        {
            JsonElement pong = await process.InvokeAsync("ping", null, _options.StartupTimeout, cancellationToken).ConfigureAwait(false);
            SidecarHealth health = Deserialize<SidecarHealth>(pong, "ping");
            _process = process;
            _restartDelay = _options.InitialRestartDelay;
            _nextStartAllowedUtc = DateTime.MinValue;
            _status = new SidecarStatus(SidecarState.Running)
            {
                Version = health.Version,
                NodeVersion = health.Node,
                Restarts = RestartCount(),
                ProcessId = process.Id,
                NodePath = nodePath,
                ScriptPath = scriptPath,
            };
            _logger.LogInformation("Sidecar {Version} running on Node {NodeVersion} (pid {Pid}) from {Script}", health.Version, health.Node, process.Id, scriptPath);
            return process;
        }
        catch (Exception ex) when (ex is SidecarException or OperationCanceledException)
        {
            await process.DisposeAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            string message = $"The sidecar started from {scriptPath} but did not answer ping: {ex.Message}";
            MarkUnavailable(message);
            throw new SidecarUnavailableException(message, ex);
        }
    }

    /// <summary>Notices the accepted process going away; anything but a deliberate stop schedules a restart after the backoff.</summary>
    private async Task WatchAsync(SidecarProcess process)
    {
        int pid = process.Id;
        int code = await process.Exited.ConfigureAwait(false);
        if (_disposed || _stopping.IsCancellationRequested)
        {
            return;
        }

        // Under the gate, so a request that is already replacing this process wins and this watcher steps aside
        // rather than marking a healthy sidecar unavailable or reading a backoff another start has just reset.
        try
        {
            await _startGate.WaitAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed || !ReferenceEquals(_process, process))
            {
                return;
            }

            _logger.LogWarning("Sidecar pid {Pid} exited with code {Code}; restarting in {Delay}", pid, code, _restartDelay);
            MarkUnavailable($"The sidecar exited with code {code}.");
        }
        finally
        {
            _startGate.Release();
        }

        TimeSpan delay = _nextStartAllowedUtc - DateTime.UtcNow;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _stopping.Token).ConfigureAwait(false);
            }

            await EnsureRunningAsync(_stopping.Token, failFastDuringBackoff: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Server shutting down.
        }
        catch (SidecarUnavailableException)
        {
            // Already logged and recorded in Status; the next request, or the next exit, tries again.
        }
    }

    private void MarkUnavailable(string message)
    {
        _nextStartAllowedUtc = DateTime.UtcNow + _restartDelay;
        TimeSpan doubled = _restartDelay + _restartDelay;
        _restartDelay = doubled < _options.MaxRestartDelay ? doubled : _options.MaxRestartDelay;
        _status = new SidecarStatus(SidecarState.Unavailable)
        {
            Error = message,
            Restarts = RestartCount(),
            NodePath = _status.NodePath,
            ScriptPath = _status.ScriptPath,
        };
        _logger.LogWarning("Sidecar unavailable: {Message}", message);
    }

    private int RestartCount()
    {
        return Math.Max(0, _starts - 1);
    }

    private static TResult Deserialize<TResult>(JsonElement element, string method)
    {
        try
        {
            return JsonSerializer.Deserialize<TResult>(element, DocumentJson.Options)
                ?? throw new SidecarException($"The sidecar's answer to {method} was empty.");
        }
        catch (JsonException ex)
        {
            throw new SidecarException($"The sidecar's answer to {method} could not be read: {ex.Message}", ex);
        }
    }

    private static Document ReadDocument(JsonElement element, string method)
    {
        try
        {
            return JsonSerializer.Deserialize<Document>(element, DocumentJson.Options)
                ?? throw new SidecarException($"The sidecar's answer to {method} held no document.");
        }
        catch (JsonException ex)
        {
            throw new SidecarException($"The document returned by {method} does not match the document model: {ex.Message}", ex);
        }
    }

    private static string? ToBase64(byte[]? bytes)
    {
        return bytes is null ? null : Convert.ToBase64String(bytes);
    }

    private sealed record LayoutEnvelope(JsonElement Document)
    {
        public List<string>? Warnings { get; init; }
    }

    private sealed record RouteEnvelope(Dictionary<string, EdgeRoute>? Routes);

    private sealed record SequenceSceneEnvelope(SequenceScene? Scene);

    private sealed record DocumentEnvelope(JsonElement Document);

    private sealed record StateEnvelope(string State, string StateVector);
}
