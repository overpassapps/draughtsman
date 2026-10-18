using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace Draughtsman.Server.Hardening;

/// <summary>
/// Says so, once an hour, when forwarding headers arrive from a peer that <c>server.knownProxies</c> does not trust. Such headers are ignored (that is
/// the point of the setting: an untrusted caller must not choose the address the throttle keys on or the scheme a cookie's Secure flag follows), but
/// the usual cause is an operator's own reverse proxy that was never listed, and then every visitor shares the proxy's address: the sign-in throttle
/// counts them together, so other people's failed attempts can lock the owner out of a correct sign-in. Nothing in the response tells a person that,
/// so the log does. It names the peer address and the setting and nothing the request said about itself: no header value is ever logged.
/// Placed before <c>UseForwardedHeaders</c>, which is where the real peer address is still the connection's.
/// </summary>
public sealed class UntrustedForwardedHeadersMiddleware(RequestDelegate next, IOptions<ForwardedHeadersOptions> options, TimeProvider clock, ILoggerFactory loggers)
{
    /// <summary>At most one warning per this long, whichever peer it is about.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>The headers a proxy adds to say who and what it is forwarding for: the standard ones and the vendor spellings.</summary>
    private static readonly string[] s_forwardingHeaders = ["X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "Forwarded", "X-Real-IP", "CF-Connecting-IP", "True-Client-IP"];

    private readonly ILogger _logger = loggers.CreateLogger("Draughtsman.Server.ForwardedHeaders");
    private readonly Lock _gate = new();
    private DateTimeOffset _quietUntil = DateTimeOffset.MinValue;

    public Task InvokeAsync(HttpContext context)
    {
        IPAddress? peer = context.Connection.RemoteIpAddress;
        if (peer is not null && HasForwardingHeader(context.Request) && !IsTrusted(peer) && ShouldWarnNow())
        {
            // As a person writes it: an IPv4 peer a dual-stack socket reports as ::ffff:172.17.0.1 is shown as 172.17.0.1.
            IPAddress shown = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
            _logger.LogWarning(
                "Forwarding headers (X-Forwarded-For and the like) arrived from {Peer}, which is not listed in server.knownProxies, so they are ignored and every visitor behind {Peer} "
                + "shares its address: the sign-in throttle counts them together. If {Peer} is your reverse proxy, add it to server.knownProxies (Docker's default bridge is 172.17.0.0/16) "
                + "and restart; see docs/deployment.md. Logged at most once an hour.",
                shown,
                shown,
                shown);
        }

        return next(context);
    }

    private static bool HasForwardingHeader(HttpRequest request)
    {
        foreach (string name in s_forwardingHeaders)
        {
            if (request.Headers.ContainsKey(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The framework's own rule: an address listed, in a listed network, or (its default) a loopback peer. Read from the options the forwarded-headers middleware uses, so the two cannot disagree.</summary>
    private bool IsTrusted(IPAddress peer)
    {
        ForwardedHeadersOptions forwarded = options.Value;
        IPAddress address = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        foreach (IPAddress known in forwarded.KnownProxies)
        {
            if (known.Equals(address))
            {
                return true;
            }
        }

        foreach (IPNetwork network in forwarded.KnownIPNetworks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    private bool ShouldWarnNow()
    {
        DateTimeOffset now = clock.GetUtcNow();
        lock (_gate)
        {
            if (now < _quietUntil)
            {
                return false;
            }

            _quietUntil = now + Interval;
            return true;
        }
    }
}
