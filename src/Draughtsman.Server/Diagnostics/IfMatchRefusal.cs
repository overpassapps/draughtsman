using System.Globalization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace Draughtsman.Server.Diagnostics;

/// <summary>Why a document write was refused over its <c>If-Match</c> version tag.</summary>
public enum IfMatchReason
{
    /// <summary>428: no <c>If-Match</c> at all. A proxy that strips <c>ETag</c> from responses leaves the client with nothing to quote.</summary>
    Missing,

    /// <summary>400: a weak tag (<c>W/"..."</c>), which is what a CDN makes of the server's strong tag when it compresses the response (issue 63).</summary>
    Weak,

    /// <summary>400: more than one tag.</summary>
    List,

    /// <summary>400: <c>*</c>.</summary>
    Wildcard,

    /// <summary>400: not an entity tag at all.</summary>
    Malformed,

    /// <summary>412: a well-formed tag naming a version that is no longer current. Another writer saved first.</summary>
    Stale,
}

/// <summary>
/// Works out, from nothing but the request's <c>If-Match</c> header and the response status, whether a document write was refused over
/// its version tag and why. A separate reading of the same header that <c>BaseVersionHeader</c> resolves, deliberately not sharing
/// code with it: the log must keep working when that parser changes (a weak tag becomes acceptable), and it never needs to know the tag's
/// value, only its shape.
/// </summary>
public static class IfMatchClassifier
{
    /// <summary>The reason the write was refused, or null when the status is not a version-tag refusal (a 400 for an invalid body is not).</summary>
    public static IfMatchReason? Refusal(int status, string? ifMatch)
    {
        string raw = ifMatch?.Trim() ?? string.Empty;
        if (status == StatusCodes.Status428PreconditionRequired)
        {
            return IfMatchReason.Missing;
        }

        if (status == StatusCodes.Status412PreconditionFailed)
        {
            return IfMatchReason.Stale;
        }

        if (status != StatusCodes.Status400BadRequest || raw.Length == 0)
        {
            return null;
        }

        if (raw == "*")
        {
            return IfMatchReason.Wildcard;
        }

        if (raw.Contains(',', StringComparison.Ordinal))
        {
            return IfMatchReason.List;
        }

        if (IsWeak(raw))
        {
            return IfMatchReason.Weak;
        }

        // One well-formed strong tag with a 400 is some other validation failure.
        return IsQuoted(raw) ? null : IfMatchReason.Malformed;
    }

    /// <summary>True when the header's (first) tag is weak: it starts with <c>W/</c>.</summary>
    public static bool IsWeak(string? ifMatch)
    {
        return ifMatch is not null && ifMatch.TrimStart().StartsWith("W/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuoted(string raw)
    {
        return raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"';
    }
}

/// <summary>
/// The log line for a refused write, rate-limited so a client in a retry loop cannot flood the file: at most one Warning per route and
/// method per minute, and the next one says how many it swallowed. It records the route pattern (never the document id), the status, the
/// reason category and whether the tag was weak - never the tag itself. A weak tag that the server ACCEPTED (once the server tolerates
/// them) is an Information line, at most once an hour per peer, saying a proxy or CDN is rewriting the ETag.
/// </summary>
public sealed class IfMatchRefusalLog(ILoggerFactory loggers, TimeProvider time)
{
    public static readonly TimeSpan RefusalInterval = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan AcceptedWeakInterval = TimeSpan.FromHours(1);

    private readonly ILogger _logger = loggers.CreateLogger("Draughtsman.Server.VersionTag");
    private readonly Dictionary<string, (DateTimeOffset At, int Suppressed)> _refusals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _acceptedWeak = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Refused(string method, string route, int status, IfMatchReason reason, bool weak)
    {
        DateTimeOffset now = time.GetUtcNow();
        int suppressed;
        lock (_gate)
        {
            string key = method + " " + route;
            if (_refusals.TryGetValue(key, out (DateTimeOffset At, int Suppressed) last) && now - last.At < RefusalInterval)
            {
                _refusals[key] = (last.At, last.Suppressed + 1);
                return;
            }

            suppressed = _refusals.TryGetValue(key, out last) ? last.Suppressed : 0;
            if (_refusals.Count >= 256)
            {
                foreach (string stale in _refusals.Where(kv => now - kv.Value.At >= RefusalInterval).Select(kv => kv.Key).ToList())
                {
                    _refusals.Remove(stale);
                }
            }

            _refusals[key] = (now, 0);
        }

        _logger.LogWarning(
            "Document write refused: {Method} {Route} answered {Status} ({Reason}); the If-Match tag was {Tag}. {Hint}{Suppressed}",
            method,
            route,
            status,
            ReasonName(reason),
            weak ? "weak (W/)" : reason == IfMatchReason.Missing ? "absent" : "not weak",
            Hint(reason),
            suppressed > 0 ? string.Format(CultureInfo.InvariantCulture, " ({0} more like it in the last minute were not logged.)", suppressed) : string.Empty);
    }

    public void AcceptedWeak(string peer)
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_gate)
        {
            if (_acceptedWeak.TryGetValue(peer, out DateTimeOffset last) && now - last < AcceptedWeakInterval)
            {
                return;
            }

            if (_acceptedWeak.Count >= 256)
            {
                foreach (string stale in _acceptedWeak.Where(kv => now - kv.Value >= AcceptedWeakInterval).Select(kv => kv.Key).ToList())
                {
                    _acceptedWeak.Remove(stale);
                }
            }

            _acceptedWeak[peer] = now;
        }

        _logger.LogInformation("accepted a weakened version tag from {Peer}; a proxy or CDN is rewriting ETag (logged once an hour per address).", peer);
    }

    public static string ReasonName(IfMatchReason reason)
    {
        return reason switch
        {
            IfMatchReason.Missing => "missing If-Match",
            IfMatchReason.Weak => "weak tag",
            IfMatchReason.List => "list of tags",
            IfMatchReason.Wildcard => "wildcard",
            IfMatchReason.Malformed => "malformed tag",
            _ => "stale version",
        };
    }

    private static string Hint(IfMatchReason reason)
    {
        return reason switch
        {
            IfMatchReason.Weak => "A proxy or CDN between the browser and this server has probably rewritten the document's strong ETag into a weak one, as Cloudflare does when it compresses a response. This build accepts a weakened tag, so the weakening is not what refused this write; if you see this on a build older than the weak-tag fix (issue 63), upgrade. Admin > Connection check tests for it.",
            IfMatchReason.Missing => "The client sent no If-Match header. A save must quote the ETag of the read it started from; a proxy that strips ETag from responses leaves the client with nothing to quote. Admin > Connection check tests for it.",
            IfMatchReason.List or IfMatchReason.Wildcard or IfMatchReason.Malformed => "If-Match must be exactly one quoted ETag, as returned by a read of this document.",
            _ => "The document changed after the client read it (another editor or an agent saved first), so the client has to reload and merge. Not a fault unless it repeats for one editor.",
        };
    }
}

/// <summary>
/// Watches document writes after the endpoint has answered. It looks at the route pattern, the status and the shape of <c>If-Match</c>
/// only, so it needs no cooperation from the endpoints it describes.
/// </summary>
public sealed class IfMatchRefusalMiddleware(RequestDelegate next, IfMatchRefusalLog log)
{
    private const string DocumentRoutes = "/api/documents";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        await next(context);
        if (!IsWrite(context.Request.Method) || context.GetEndpoint() is not RouteEndpoint endpoint || endpoint.RoutePattern.RawText is not { } route
            || !route.StartsWith(DocumentRoutes, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? ifMatch = context.Request.Headers.TryGetValue("If-Match", out StringValues values) ? values.ToString() : null;
        int status = context.Response.StatusCode;
        if (IfMatchClassifier.Refusal(status, ifMatch) is { } reason)
        {
            log.Refused(context.Request.Method, route, status, reason, IfMatchClassifier.IsWeak(ifMatch));
        }
        else if (status is >= 200 and < 300 && IfMatchClassifier.IsWeak(ifMatch))
        {
            log.AcceptedWeak(context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        }
    }

    private static bool IsWrite(string method)
    {
        return HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsPost(method) || HttpMethods.IsDelete(method);
    }
}
