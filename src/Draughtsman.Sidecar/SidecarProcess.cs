using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Draughtsman.Core.Hosting;
using Draughtsman.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Sidecar;

/// <summary>
/// One spawned sidecar process: writes JSON-RPC frames to its stdin, pumps its stdout into pending requests by id,
/// relays its stderr to the log, and fails every in-flight request when it exits. Owned by <see cref="NodeSidecarClient"/>,
/// which decides when to start another one.
/// </summary>
internal sealed class SidecarProcess : IAsyncDisposable
{
    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly TimeSpan s_gracefulExitWait = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly ILogger _logger;
    private readonly BlockingCollection<PendingWrite> _writes = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private readonly Task _writer;
    private long _nextId;
    private int _responsesRead;
    private int _killed;
    private bool _disposed;

    private SidecarProcess(Process process, ILogger logger)
    {
        _process = process;
        _logger = logger;
        _stdin = process.StandardInput;
        _stdin.AutoFlush = false;
        // The three pipe loops each own a thread: a redirected pipe is a synchronous handle on Windows, so an "async" read
        // or write on it is a blocking call parked on a thread-pool thread. Two permanent readers per sidecar plus every
        // write queued behind the pool's other work starved the pool on a busy host (a loaded 2-vCPU Windows runner took
        // 13 to 19 s to get a first ping through, with the answer already read), so none of them may borrow pool threads.
        _stdoutPump = Task.Factory.StartNew(PumpStdout, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _stderrPump = Task.Factory.StartNew(PumpStderr, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _writer = Task.Factory.StartNew(WriteFrames, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _ = WatchExitAsync();
    }

    public int Id => _process.Id;

    /// <summary>How many answers the stdout thread has matched to a waiting request. A test reads it to know an answer arrived without the pool's help.</summary>
    internal int ResponsesRead => Volatile.Read(ref _responsesRead);

    public bool IsRunning => Volatile.Read(ref _killed) == 0 && !_exited.Task.IsCompleted;

    /// <summary>Completes with the exit code once the process has gone, whatever the reason.</summary>
    public Task<int> Exited => _exited.Task;

    /// <summary>Spawns node with the script as its only argument; never through a shell (ADR 0001).</summary>
    public static SidecarProcess Start(string nodePath, string scriptPath, ILogger logger)
    {
        var startInfo = new ProcessStartInfo(nodePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = s_utf8NoBom,
            StandardOutputEncoding = s_utf8NoBom,
            StandardErrorEncoding = s_utf8NoBom,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? string.Empty,
        };
        startInfo.ArgumentList.Add(scriptPath);

        // The sidecar lays out diagrams and merges Yjs updates; it has no use for the server's secrets (AI key, key-ring
        // encryption key), so it gets an allow-listed environment instead of inheriting them all (AU-10).
        startInfo.Environment.Clear();
        foreach (KeyValuePair<string, string> variable in ScrubbedEnvironment.FromCurrentProcess())
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        try
        {
            Process process = Process.Start(startInfo) ?? throw new SidecarUnavailableException($"Process.Start returned nothing for {nodePath}.");
            return new SidecarProcess(process, logger);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new SidecarUnavailableException($"Could not start {nodePath} {scriptPath}: {ex.Message}", ex);
        }
    }

    /// <summary>Sends one request and waits for its response, killing the process on timeout because a stuck layout blocks every other request too.</summary>
    public async Task<JsonElement> InvokeAsync(string method, object? parameters, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsRunning)
        {
            throw new SidecarUnavailableException("The sidecar is not running.");
        }

        long id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        string frame = JsonSerializer.Serialize(new JsonRpcRequest("2.0", id, method, parameters), DocumentJson.Options);
        string seconds = timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

        // The timeout covers the write as well as the answer: a sidecar that has stopped reading stdin blocks the
        // write itself once the pipe buffer is full, and the frame cannot be abandoned half-written, so killing the
        // process is what frees the write and the writer thread behind it. The registration is what reaches a request
        // stuck in that write; one that is merely waiting kills from the catch below. A caller that cancels kills nothing.
        // An answer that has already been read is an answer: when the pool is too busy to run this method's continuation
        // before the timer fires, the sidecar did not fail to answer, this method was late to notice, so neither path kills it.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using CancellationTokenRegistration onTimeout = timeoutSource.Token.Register(() =>
        {
            if (!cancellationToken.IsCancellationRequested && !completion.Task.IsCompletedSuccessfully)
            {
                Kill($"{method} did not answer within {seconds} s");
            }
        });
        timeoutSource.CancelAfter(timeout);

        var write = new PendingWrite(frame);
        try
        {
            try
            {
                _writes.Add(write, CancellationToken.None);
            }
            catch (InvalidOperationException ex)
            {
                throw new ObjectDisposedException(nameof(SidecarProcess), ex);
            }

            await write.Done.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTimedOut(ex, timeoutSource.Token, cancellationToken))
        {
            _pending.TryRemove(id, out _);
            if (completion.Task.IsCompletedSuccessfully)
            {
                return completion.Task.Result;
            }

            Kill($"{method} did not answer within {seconds} s");
            throw new SidecarTimeoutException($"The sidecar did not answer {method} within {seconds} s and has been stopped; it restarts on the next request.");
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new SidecarUnavailableException("Could not write to the sidecar; it has exited.", ex);
        }
        finally
        {
            write.Abandon();
        }
    }

    /// <summary>The timeout fired rather than the caller cancelling: either the wait was cut short or the kill it triggered failed the write.</summary>
    private static bool IsTimedOut(Exception exception, CancellationToken timeoutToken, CancellationToken cancellationToken)
    {
        return timeoutToken.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested
            && exception is OperationCanceledException or IOException or ObjectDisposedException;
    }

    public void Kill(string reason)
    {
        if (Interlocked.Exchange(ref _killed, 1) != 0)
        {
            return;
        }

        _logger.LogWarning("Stopping sidecar pid {Pid}: {Reason}", SafeId(), reason);
        try
        {
            _process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone; the exit watcher settles the pending requests.
            _logger.LogDebug(ex, "Sidecar pid {Pid} had already exited", SafeId());
        }
    }

    /// <summary>Closes stdin so the sidecar exits on its own, then kills it if it lingers.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writes.CompleteAdding();
        try
        {
            _stdin.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Sidecar stdin was already closed");
        }

        try
        {
            await _exited.Task.WaitAsync(s_gracefulExitWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Kill("did not exit after stdin closed");
            await _exited.Task.ConfigureAwait(false);
        }

        await Task.WhenAll(_stdoutPump, _stderrPump, _writer).ConfigureAwait(false);
        _process.Dispose();
        _writes.Dispose();
    }

    /// <summary>Runs on its own thread (see the constructor): a blocking read is what a synchronous pipe offers anyway.</summary>
    private void PumpStdout()
    {
        try
        {
            while (_process.StandardOutput.ReadLine() is { } line)
            {
                HandleLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Sidecar stdout closed");
        }
    }

    private void PumpStderr()
    {
        try
        {
            while (_process.StandardError.ReadLine() is { } line)
            {
                if (line.Length > 0)
                {
                    _logger.LogWarning("sidecar stderr: {Line}", line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Sidecar stderr closed");
        }
    }

    /// <summary>Writes request frames one at a time, in the order they were queued, skipping any whose caller has given up.</summary>
    private void WriteFrames()
    {
        foreach (PendingWrite write in _writes.GetConsumingEnumerable())
        {
            if (write.IsAbandoned)
            {
                write.Done.TrySetResult();
                continue;
            }

            try
            {
                _stdin.Write(write.Frame);
                _stdin.Write('\n');
                _stdin.Flush();
                write.Done.TrySetResult();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                write.Done.TrySetException(ex);
            }
        }
    }

    private async Task WatchExitAsync()
    {
        int code;
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            code = _process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Could not read the sidecar exit code");
            code = -1;
        }

        // Let the last responses drain before failing what is left: a clean exit answers everything it was asked.
        await _stdoutPump.ConfigureAwait(false);
        foreach (long id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out TaskCompletionSource<JsonElement>? pending))
            {
                pending.TrySetException(new SidecarUnavailableException($"The sidecar exited with code {code} while the request was in flight."));
            }
        }

        _exited.TrySetResult(code);
    }

    private void HandleLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Sidecar wrote a line that is not JSON ({Reason}): {Line}", ex.Message, Truncate(line));
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out JsonElement idElement)
                || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt64(out long id))
            {
                _logger.LogWarning("Sidecar wrote a frame without a request id: {Line}", Truncate(line));
                return;
            }

            if (!_pending.TryRemove(id, out TaskCompletionSource<JsonElement>? pending))
            {
                _logger.LogDebug("Sidecar answered request {Id} after it was abandoned", id);
                return;
            }

            Interlocked.Increment(ref _responsesRead);

            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
            {
                int code = error.TryGetProperty("code", out JsonElement codeElement) && codeElement.TryGetInt32(out int parsedCode) ? parsedCode : 0;
                string message = error.TryGetProperty("message", out JsonElement messageElement) && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString() ?? string.Empty
                    : "unknown error";
                JsonElement? data = error.TryGetProperty("data", out JsonElement dataElement) ? dataElement.Clone() : null;
                pending.TrySetException(new SidecarRpcException(code, message, data));
                return;
            }

            pending.TrySetResult(root.TryGetProperty("result", out JsonElement result) ? result.Clone() : default);
        }
    }

    private string SafeId()
    {
        try
        {
            return _process.Id.ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    private static string Truncate(string line)
    {
        const int max = 200;
        return line.Length <= max ? line : line[..max] + "...";
    }

    /// <summary>One frame waiting for the writer thread. <see cref="Done"/> completes once it is flushed or has failed to be.</summary>
    private sealed class PendingWrite(string frame)
    {
        private int _abandoned;

        public string Frame { get; } = frame;

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAbandoned => Volatile.Read(ref _abandoned) != 0;

        public void Abandon()
        {
            Volatile.Write(ref _abandoned, 1);
        }
    }

    /// <summary>Serialised with the document JSON options: camelCase member names, params omitted when null.</summary>
    private sealed record JsonRpcRequest(string Jsonrpc, long Id, string Method, object? Params);
}
