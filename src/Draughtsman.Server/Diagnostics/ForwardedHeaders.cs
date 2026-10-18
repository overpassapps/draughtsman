using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Primitives;

namespace Draughtsman.Server.Diagnostics;

/// <summary>
/// What a request looked like on arrival, before the forwarded-headers middleware consumed it: the direct peer, the scheme the
/// connection really used, and the raw values of the headers a proxy or CDN adds. The middleware removes
/// <c>X-Forwarded-*</c> from a request it believes (moving them to <c>X-Original-*</c>), so by the time an endpoint runs the
/// raw values are gone; <see cref="ForwardedHeaderObserver"/> takes this snapshot just ahead of it. Header values are kept
/// verbatim (an address or a scheme, never a credential) but capped, since they come from an unauthenticated caller.
/// </summary>
public sealed record ForwardedSnapshot(
    IPAddress? Peer,
    string RawScheme,
    string Host,
    string? XForwardedProto,
    string? XForwardedFor,
    string? XForwardedHost,
    string? CfConnectingIp,
    string? Forwarded,
    bool PeerTrusted,
    string? XRealIp = null,
    string? TrueClientIp = null)
{
    /// <summary>True when the request carried any header a proxy or CDN adds (the same set the untrusted-proxy warning looks for).</summary>
    public bool HasForwardedHeaders => XForwardedProto is not null || XForwardedFor is not null || XForwardedHost is not null || CfConnectingIp is not null || Forwarded is not null || XRealIp is not null || TrueClientIp is not null;

    public const string ItemKey = "draughtsman.forwarded-snapshot";

    public static ForwardedSnapshot? Of(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ItemKey, out object? value) ? value as ForwardedSnapshot : null;
    }
}

/// <summary>
/// Whether a peer address is one the forwarded-headers middleware believes, worked out exactly as the framework does it from the
/// options it was configured with (so the answer is what really happens, not what <c>server.knownProxies</c> looks like it
/// should mean): with neither list populated every peer is believed; otherwise the peer (or the IPv4 address an IPv6-mapped one
/// stands for) must be one of the proxies or inside one of the networks.
/// </summary>
public static class ForwardedTrust
{
    public static bool IsTrusted(IPAddress? peer, ForwardedHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        bool checkKnown = options.KnownProxies.Count > 0 || options.KnownIPNetworks.Count > 0;
        if (!checkKnown)
        {
            return true;
        }

        return peer is not null && Known(peer, options);
    }

    private static bool Known(IPAddress address, ForwardedHeadersOptions options)
    {
        if (address.IsIPv4MappedToIPv6 && Known(address.MapToIPv4(), options))
        {
            return true;
        }

        if (options.KnownProxies.Contains(address))
        {
            return true;
        }

        foreach (System.Net.IPNetwork network in options.KnownIPNetworks)
        {
            if (network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Takes the <see cref="ForwardedSnapshot"/> of every request, ahead of <c>UseForwardedHeaders</c>, and feeds the in-memory
/// <see cref="ForwardedObservations"/>. Reads headers and the peer address only; it changes nothing about the request and cannot
/// refuse one.
/// </summary>
public sealed class ForwardedHeaderObserver(RequestDelegate next, Microsoft.Extensions.Options.IOptions<ForwardedHeadersOptions> options, ForwardedObservations observations)
{
    private const int MaxHeaderLength = 512;

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        IPAddress? peer = context.Connection.RemoteIpAddress;
        bool trusted = ForwardedTrust.IsTrusted(peer, options.Value);
        IHeaderDictionary headers = context.Request.Headers;
        var snapshot = new ForwardedSnapshot(
            peer,
            context.Request.Scheme,
            Clip(context.Request.Host.Value) ?? string.Empty,
            Read(headers, "X-Forwarded-Proto"),
            Read(headers, "X-Forwarded-For"),
            Read(headers, "X-Forwarded-Host"),
            Read(headers, "CF-Connecting-IP"),
            Read(headers, "Forwarded"),
            trusted,
            Read(headers, "X-Real-IP"),
            Read(headers, "True-Client-IP"));
        context.Items[ForwardedSnapshot.ItemKey] = snapshot;
        observations.Record(snapshot);
        return next(context);
    }

    private static string? Read(IHeaderDictionary headers, string name)
    {
        return headers.TryGetValue(name, out StringValues value) && !StringValues.IsNullOrEmpty(value) ? Clip(value.ToString()) : null;
    }

    private static string? Clip(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        string text = value.Length > MaxHeaderLength ? value[..MaxHeaderLength] : value;
        // A header value is attacker-controlled text that ends up on an admin page and in a log line: control characters out.
        return new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]);
    }
}
