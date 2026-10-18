using System.Text.Json;
using System.Text.Json.Serialization;

namespace Draughtsman.Server.Diagnostics;

/// <summary>What the server has seen from one direct peer since it started: how many requests, how many carried proxy headers, and whether the peer was believed.</summary>
public sealed record PeerObservation(
    string Peer,
    bool Trusted,
    long Requests,
    long ForwardedRequests,
    long WithProto,
    long WithFor,
    long WithHost,
    long WithCfConnectingIp,
    IReadOnlyList<string> ProtoValues,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);

/// <summary>The whole in-memory picture, as written to <c>diagnostics/forwarded-observations.json</c> and read back by <c>support-bundle</c>.</summary>
public sealed record ForwardedObservationsReport(DateTimeOffset Since, DateTimeOffset At, IReadOnlyList<PeerObservation> Peers, long OtherPeerRequests);

/// <summary>
/// Counts, per direct peer, the requests that arrived with proxy headers and whether the peer was one the server believes
/// (<c>server.knownProxies</c>). Kept in memory only: it exists so that "headers arrive from an address that is not listed" can be
/// seen by <c>doctor</c> and put in a support bundle (the log line that says so once an hour is
/// <see cref="Draughtsman.Server.Hardening.UntrustedForwardedHeadersMiddleware"/>'s), which a stopped server must still be able to produce, so a copy is
/// written to <c>&lt;data&gt;/diagnostics/forwarded-observations.json</c> by <see cref="ForwardedObservationsFlusher"/>. It holds addresses
/// and counts, never a header's content beyond the <c>X-Forwarded-Proto</c> scheme word, a cookie or a request.
/// </summary>
public sealed class ForwardedObservations
{
    /// <summary>Peers tracked individually; later ones count into <see cref="ForwardedObservationsReport.OtherPeerRequests"/>, so a scan from many addresses cannot grow this for ever.</summary>
    public const int MaxPeers = 32;

    public const string FolderName = "diagnostics";

    public const string FileName = "forwarded-observations.json";

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, Counter> _peers = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _since;
    private long _other;
    private bool _dirty;

    public ForwardedObservations(TimeProvider time)
    {
        _time = time;
        _since = time.GetUtcNow();
    }

    /// <summary>True when something changed since the last <see cref="TakeIfDirty"/>.</summary>
    public bool Dirty
    {
        get
        {
            lock (_gate)
            {
                return _dirty;
            }
        }
    }

    public void Record(ForwardedSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string peer = snapshot.Peer?.ToString() ?? "unknown";
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_peers.TryGetValue(peer, out Counter? counter))
            {
                if (_peers.Count >= MaxPeers)
                {
                    _other++;
                    _dirty = true;
                    return;
                }

                counter = new Counter { FirstSeen = now };
                _peers[peer] = counter;
            }

            counter.Trusted = snapshot.PeerTrusted;
            counter.Requests++;
            counter.LastSeen = now;
            if (snapshot.HasForwardedHeaders)
            {
                counter.ForwardedRequests++;
                counter.WithProto += snapshot.XForwardedProto is null ? 0 : 1;
                counter.WithFor += snapshot.XForwardedFor is null ? 0 : 1;
                counter.WithHost += snapshot.XForwardedHost is null ? 0 : 1;
                counter.WithCf += snapshot.CfConnectingIp is null ? 0 : 1;
                if (snapshot.XForwardedProto is { } proto && ProtoWord(proto) is { } word)
                {
                    counter.ProtoValues.Add(word);
                }

            }

            _dirty = true;
        }
    }

    /// <summary>The current picture. Safe to call from any thread.</summary>
    public ForwardedObservationsReport Report()
    {
        lock (_gate)
        {
            PeerObservation[] peers =
            [
                .. _peers.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new PeerObservation(
                    kv.Key,
                    kv.Value.Trusted,
                    kv.Value.Requests,
                    kv.Value.ForwardedRequests,
                    kv.Value.WithProto,
                    kv.Value.WithFor,
                    kv.Value.WithHost,
                    kv.Value.WithCf,
                    [.. kv.Value.ProtoValues.Order(StringComparer.Ordinal)],
                    kv.Value.FirstSeen,
                    kv.Value.LastSeen)),
            ];
            return new ForwardedObservationsReport(_since, _time.GetUtcNow(), peers, _other);
        }
    }

    /// <summary>The report if anything changed since the last call, else null; clears the flag.</summary>
    public ForwardedObservationsReport? TakeIfDirty()
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                return null;
            }

            _dirty = false;
        }

        return Report();
    }

    /// <summary>Writes the report beside the data, atomically, never throwing: a read-only folder must not take the server with it.</summary>
    public static void Write(string dataFolder, ForwardedObservationsReport report)
    {
        try
        {
            string folder = Path.Combine(dataFolder, FolderName);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, FileName);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(report, s_json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Diagnostics only.
        }
    }

    /// <summary>Reads what a (possibly stopped) server last wrote; null when there is no readable file.</summary>
    public static ForwardedObservationsReport? Read(string dataFolder)
    {
        try
        {
            string path = Path.Combine(dataFolder, FolderName, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<ForwardedObservationsReport>(File.ReadAllText(path), s_json) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Only a scheme word is kept from <c>X-Forwarded-Proto</c>: the value is caller-controlled and goes on an admin page.</summary>
    private static string? ProtoWord(string value)
    {
        string first = value.Split(',')[0].Trim().ToLowerInvariant();
        return first is "http" or "https" ? first : "other";
    }

    private sealed class Counter
    {
        public bool Trusted { get; set; }

        public long Requests { get; set; }

        public long ForwardedRequests { get; set; }

        public long WithProto { get; set; }

        public long WithFor { get; set; }

        public long WithHost { get; set; }

        public long WithCf { get; set; }

        public SortedSet<string> ProtoValues { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset FirstSeen { get; set; }

        public DateTimeOffset LastSeen { get; set; }
    }
}

/// <summary>Writes <see cref="ForwardedObservations"/> to the data folder every half minute while it has changed, and once more when the host stops, so a stopped server's last picture is on disk for <c>support-bundle</c>.</summary>
public sealed class ForwardedObservationsFlusher(ForwardedObservations observations, Draughtsman.Server.DataFolder data) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                Flush();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Flush();
    }

    private void Flush()
    {
        if (observations.TakeIfDirty() is { } report)
        {
            ForwardedObservations.Write(data.Path, report);
        }
    }
}
