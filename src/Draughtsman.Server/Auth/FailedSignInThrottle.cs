namespace Draughtsman.Server.Auth;

/// <summary>
/// In-memory sliding-window throttle for failed sign-ins, keyed independently per account and per caller IP
/// (build-spec-v0.1.md section 7), so a distributed guess against one account and a single IP guessing many accounts
/// are both caught. Deliberately not persisted: it only needs to survive this process's own uptime, and every
/// storage provider would otherwise pay a write on every failed attempt for state nothing else ever reads back.
/// A restart clears it, the same as an in-memory rate limiter would.
/// An attempt is charged before its password is verified (so a concurrent burst cannot all pass the check first) and
/// <see cref="Refund"/>ed if it turns out to be correct, so only failures ever stay counted.
/// </summary>
public sealed class FailedSignInThrottle(TimeProvider? time = null)
{
    /// <summary>Above this many buckets, a failure also sweeps the expired ones: a caller spraying many addresses or emails must not grow the map for ever (AU-18).</summary>
    private const int SweepThreshold = 1024;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Budget for one account (the (email, IP) pair) and for first-run setup-code guesses.</summary>
    public const int MaxAttempts = 5;

    /// <summary>
    /// Budget for one caller IP across all accounts. Higher than <see cref="MaxAttempts"/> because an office NAT, or
    /// anyone behind a reverse proxy not listed in <c>server.knownProxies</c>, shares one address among many people.
    /// Still bounds a single source to a handful of guesses per account, which is what makes the spray useless.
    /// </summary>
    public const int IpMaxAttempts = 20;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _reportedTrips = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _nextTicket;

    public bool IsBlocked(string key, int maxAttempts = MaxAttempts)
    {
        lock (_gate)
        {
            return _buckets.TryGetValue(key, out Bucket? bucket) && Prune(bucket) >= maxAttempts;
        }
    }

    /// <summary>Charges one attempt and returns a ticket that <see cref="Refund"/> can hand back.</summary>
    public long RecordFailure(string key)
    {
        lock (_gate)
        {
            if (!_buckets.TryGetValue(key, out Bucket? bucket))
            {
                bucket = new Bucket();
                _buckets[key] = bucket;
            }

            Prune(bucket);
            if (_buckets.Count > SweepThreshold)
            {
                foreach (string stale in _buckets.Where(kv => kv.Value != bucket && Prune(kv.Value) == 0).Select(kv => kv.Key).ToList())
                {
                    _buckets.Remove(stale);
                }
            }

            long ticket = ++_nextTicket;
            bucket.Entries.Add((ticket, _time.GetUtcNow()));
            return ticket;
        }
    }

    /// <summary>
    /// Removes exactly the one charge <paramref name="ticket"/> made - never the rest of the bucket, so a success
    /// cannot be used to reset an attacker's own accumulated failures.
    /// </summary>
    public void Refund(string key, long ticket)
    {
        lock (_gate)
        {
            if (_buckets.TryGetValue(key, out Bucket? bucket))
            {
                bucket.Entries.RemoveAll(e => e.Ticket == ticket);
                if (bucket.Entries.Count == 0)
                {
                    _buckets.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// True the first time a blocked <paramref name="key"/> is reported in a window, false for every refused request
    /// after it, so the security event log records that the throttle tripped once rather than once per refused
    /// request (an attacker hammering a blocked key must not be able to grow the log table).
    /// </summary>
    public bool TryReportTrip(string key)
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_reportedTrips.TryGetValue(key, out DateTimeOffset at) && now - at < Window)
            {
                return false;
            }

            _reportedTrips[key] = now;
            if (_reportedTrips.Count > 1024)
            {
                foreach (string stale in _reportedTrips.Where(kv => now - kv.Value >= Window).Select(kv => kv.Key).ToList())
                {
                    _reportedTrips.Remove(stale);
                }
            }

            return true;
        }
    }

    /// <summary>How many buckets are held; for the eviction test.</summary>
    internal int BucketCount
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    public void Clear(string key)
    {
        lock (_gate)
        {
            _buckets.Remove(key);
        }
    }

    private int Prune(Bucket bucket)
    {
        DateTimeOffset cutoff = _time.GetUtcNow() - Window;
        bucket.Entries.RemoveAll(e => e.At < cutoff);
        return bucket.Entries.Count;
    }

    private sealed class Bucket
    {
        public List<(long Ticket, DateTimeOffset At)> Entries { get; } = [];
    }
}
