using System.Globalization;
using System.Net;

namespace Draughtsman.Server.Diagnostics;

/// <summary>
/// Everything the connection self-check's server-side findings rest on: what the request looked like on arrival
/// (<see cref="Snapshot"/>), what the pipeline then made of it (<see cref="FinalScheme"/>, <see cref="ThrottleAddress"/>) and the
/// settings that decided it. Plain data, so each rule is a function of it and tests state a scenario by writing the values.
/// </summary>
public sealed record ConnectionInputs(
    ForwardedSnapshot Snapshot,
    string FinalScheme,
    IPAddress? ThrottleAddress,
    bool ForceHttps,
    IReadOnlyList<string> AllowedHosts,
    bool AllowedHostsConfigured,
    IReadOnlyList<string> KnownProxies,
    string? PageScheme);

/// <summary>
/// The connection self-check's findings that only the server can make (<c>GET /api/admin/selfcheck</c>): who the server thinks
/// is talking to it, whether it believes the proxy in front of it, which scheme it will give cookies for, and whether the Host
/// header is one it answers to. The round trip through the browser (does the cookie come back, does the ETag survive, does
/// anything compress) is run by the page itself, because only a browser can see what a proxy did to a response.
/// Every finding names what was seen, in the words an operator sees in their proxy's own configuration, and says what to put
/// where for nginx, Caddy, Cloudflare and <c>draughtsman.yaml</c>.
/// </summary>
public static class ConnectionChecks
{
    public const string NotSent = "(not sent)";

    public static IReadOnlyList<DiagnosticItem> Evaluate(ConnectionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return [Proxy(inputs), Secure(inputs), Host(inputs)];
    }

    // ------------------------------------------------------------------------------------------------------ 1. proxy

    private static DiagnosticItem Proxy(ConnectionInputs i)
    {
        const string id = "proxy";
        const string title = "Reverse proxy and client address";
        ForwardedSnapshot s = i.Snapshot;
        string peer = s.Peer?.ToString() ?? "unknown";
        string throttle = i.ThrottleAddress?.ToString() ?? "unknown";
        var facts = new Dictionary<string, string>
        {
            ["Direct peer address"] = peer,
            ["Peer trusted (server.knownProxies)"] = s.PeerTrusted ? "yes" : "no",
            ["server.knownProxies"] = i.KnownProxies.Count == 0 ? "(empty: only a proxy on this machine is trusted)" : string.Join(", ", i.KnownProxies),
            ["Scheme of the connection itself"] = s.RawScheme,
            ["X-Forwarded-Proto"] = s.XForwardedProto ?? NotSent,
            ["X-Forwarded-For"] = s.XForwardedFor ?? NotSent,
            ["X-Forwarded-Host"] = s.XForwardedHost ?? NotSent,
            ["CF-Connecting-IP"] = s.CfConnectingIp ?? NotSent,
            ["Scheme the server believes"] = i.FinalScheme,
            ["Client address the sign-in throttle uses"] = throttle,
        };

        if (!s.HasForwardedHeaders)
        {
            return DiagnosticItem.Pass(
                id,
                title,
                IsLoopback(s.Peer)
                    ? "This request came straight to the server (or from a proxy on this machine that added no headers). No proxy headers were sent."
                    : $"This request came from {peer} and carried no proxy headers, so the server treats {peer} as the visitor. That is right for a direct connection. If a reverse proxy is in front of this server, it is not sending X-Forwarded-For, X-Forwarded-Proto or X-Forwarded-Host: add them (see the proxy examples below) or sign-in throttling and cookies will see only the proxy.",
                facts,
                detail: IsLoopback(s.Peer) ? null : ProxyNotSendingDetail());
        }

        if (!s.PeerTrusted)
        {
            return DiagnosticItem.Warn(
                id,
                title,
                $"Proxy headers arrived from {peer}, which is not listed in server.knownProxies, so the server IGNORES them. Every visitor looks like {peer} to the sign-in throttle (twenty failed sign-ins by anyone lock everyone out for 15 minutes), the connection is treated as {s.RawScheme}, and cookies are Secure only if server.forceHttps is on.",
                facts,
                detail:
                [
                    $"If {peer} is your reverse proxy, add it to server.knownProxies and restart.",
                    peer.StartsWith("172.", StringComparison.Ordinal) ? "A Docker bridge gateway (172.17.0.1 for the default network) is the peer when nginx runs on the host and the container's port is published; a range such as 172.17.0.0/16 keeps working if the container is recreated." : "An address or a CIDR range such as 10.0.0.0/8 both work.",
                    "Only list proxies you control: a listed address can claim any visitor address and any scheme.",
                ],
                fix: new DiagnosticFix(
                    Nginx: NginxHeaders,
                    Caddy: "Caddy's reverse_proxy already sends X-Forwarded-For, X-Forwarded-Proto and X-Forwarded-Host. Nothing to change in the Caddyfile for this; the change is in draughtsman.yaml.",
                    Cloudflare: "Nothing to change in Cloudflare for this. If Cloudflare reaches the server through another proxy, that proxy is the one to list.",
                    Yaml: KnownProxiesYaml(peer)));
        }

        // The proxy is believed. What can still go wrong is which address the throttle ends up with.
        if (s.CfConnectingIp is { } cf && IPAddress.TryParse(cf.Trim(), out IPAddress? visitor) && i.ThrottleAddress is not null && !visitor.Equals(i.ThrottleAddress))
        {
            return DiagnosticItem.Warn(
                id,
                title,
                $"Cloudflare says the visitor is {cf}, but the server will use {throttle} for the sign-in throttle. Everyone arriving through the same Cloudflare data centre shares {throttle}'s limit, so a handful of failed sign-ins can lock all of them out for 15 minutes.",
                facts,
                detail: ["Behind Cloudflare and then nginx, nginx must put the visitor's address into X-Forwarded-For (its real_ip module with CF-Connecting-IP), otherwise it appends Cloudflare's own address."],
                fix: new DiagnosticFix(
                    Nginx: NginxRealIp,
                    Caddy: CaddyRealIp,
                    Cloudflare: "Nothing to change in Cloudflare: it already sends CF-Connecting-IP.",
                    Yaml: KnownProxiesYaml(peer)));
        }

        string[] chain = SplitList(s.XForwardedFor);
        if (chain.Length > 1 && s.CfConnectingIp is null)
        {
            return DiagnosticItem.Warn(
                id,
                title,
                $"X-Forwarded-For lists {chain.Length.ToString(CultureInfo.InvariantCulture)} addresses ({string.Join(", ", chain)}) and the server takes the LAST one, {chain[^1]}, as the visitor. If {chain[^1]} is another proxy or a CDN rather than the person, all visitors behind it share one sign-in throttle limit.",
                facts,
                detail: ["Make the outermost proxy that you control the only one that appends to X-Forwarded-For, or have the first proxy replace the header with the real visitor address."],
                fix: new DiagnosticFix(Nginx: NginxRealIp, Caddy: CaddyRealIp, Yaml: KnownProxiesYaml(peer)));
        }

        return DiagnosticItem.Pass(
            id,
            title,
            $"The proxy at {peer} is trusted, so its headers are believed: visitors are told apart by address (this request counts as {throttle}) and the scheme is {i.FinalScheme}.",
            facts);
    }

    private static IReadOnlyList<string> ProxyNotSendingDetail()
    {
        return ["If this really is a direct connection, nothing needs to change."];
    }

    // ------------------------------------------------------------------------------------------------------ 2. secure cookies

    private static DiagnosticItem Secure(ConnectionInputs i)
    {
        const string id = "secure";
        const string title = "Secure cookies and sign-in";
        bool secure = string.Equals(i.FinalScheme, "https", StringComparison.OrdinalIgnoreCase);
        string cookieName = secure ? "__Host-ds_session (Secure)" : "ds_session (not Secure)";
        var facts = new Dictionary<string, string>
        {
            ["server.forceHttps"] = i.ForceHttps ? "on" : "off",
            ["Scheme the server believes"] = i.FinalScheme,
            ["Session cookie this request would get"] = cookieName,
            ["Scheme of the address in your browser"] = i.PageScheme ?? "(not reported)",
        };

        if (i.ForceHttps && string.Equals(i.PageScheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticItem.Fail(
                id,
                title,
                "server.forceHttps is on, so every cookie is marked Secure, but you are viewing this page over plain http://. A browser throws away a Secure cookie it receives over http, so signing in appears to work and then nothing sticks (the sign-in form can sit on \"Signing in…\" for ever).",
                facts,
                detail:
                [
                    "Use the https:// address your proxy serves, and make the proxy redirect http to https.",
                    "If you have no TLS yet (a trial on a LAN address), turn server.forceHttps off instead.",
                ],
                fix: new DiagnosticFix(
                    Nginx: "server {\n    listen 80;\n    server_name draughtsman.example.com;\n    return 301 https://$host$request_uri;\n}",
                    Caddy: "Caddy serves https automatically for a real host name and redirects http to it. If you used :80 or an IP address, give the site its host name:\ndraughtsman.example.com {\n    reverse_proxy 127.0.0.1:5180\n}",
                    Cloudflare: "SSL/TLS > Edge Certificates > Always Use HTTPS: on. In SSL/TLS > Overview use Full (strict) once the origin has a certificate; Flexible makes Cloudflare talk http to your server, which is fine only together with server.forceHttps.",
                    Yaml: "server:\n  forceHttps: false   # only while the site is reached over plain http"));
        }

        if (i.ForceHttps)
        {
            return DiagnosticItem.Pass(
                id,
                title,
                i.PageScheme is null
                    ? "server.forceHttps is on: every cookie is Secure. Your browser must therefore reach this server over https://, or it will drop the session cookie."
                    : "server.forceHttps is on and you are on https://: the session cookie is Secure and your browser keeps it.",
                facts);
        }

        if (secure)
        {
            return DiagnosticItem.Pass(id, title, "The server believes this connection is https, so the session cookie is Secure (__Host- prefixed).", facts);
        }

        if (string.Equals(i.PageScheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticItem.Warn(
                id,
                title,
                "You are on https:// but the server believes this connection is plain http, so its session cookie is NOT marked Secure (and HSTS is off). Sign-in still works, but the cookie is weaker than it should be.",
                facts,
                detail:
                [
                    i.Snapshot.XForwardedProto is { } proto && !i.Snapshot.PeerTrusted
                        ? $"The proxy sent X-Forwarded-Proto: {proto}, but it is not a trusted proxy, so the header is ignored (see Reverse proxy and client address above)."
                        : "The proxy is not telling the server the original scheme (X-Forwarded-Proto), or is not trusted.",
                    "Either list the proxy in server.knownProxies and have it send X-Forwarded-Proto, or set server.forceHttps: true once you know every visitor arrives over https.",
                ],
                fix: new DiagnosticFix(
                    Nginx: NginxHeaders,
                    Caddy: "Caddy sends X-Forwarded-Proto itself. Make sure it is the only thing in front of this server, and list its address in server.knownProxies.",
                    Cloudflare: "If Cloudflare talks http to your origin (SSL/TLS mode Flexible), the origin cannot see https at all: set server.forceHttps: true, or move to Full (strict).",
                    Yaml: "server:\n  forceHttps: true   # every visitor reaches this instance over https, directly or through your proxy"));
        }

        return DiagnosticItem.Pass(id, title, "This connection is plain http and the session cookie is not Secure. That is fine on a trusted network (a local trial); put TLS in front of it before the instance is reachable from the internet.", facts);
    }

    // ------------------------------------------------------------------------------------------------------ 3. host

    private static DiagnosticItem Host(ConnectionInputs i)
    {
        const string id = "host";
        const string title = "Host header and allowed hosts";
        string host = i.Snapshot.Host.Length == 0 ? "(none)" : i.Snapshot.Host;
        var facts = new Dictionary<string, string>
        {
            ["Host header received"] = host,
            ["server.allowedHosts"] = i.AllowedHostsConfigured ? string.Join(", ", i.AllowedHosts) : "(empty: " + (i.AllowedHosts is ["*"] ? "any host is accepted)" : "localhost names only, because the server listens on loopback with no proxy)"),
            ["Hosts accepted now"] = string.Join(", ", i.AllowedHosts),
            ["X-Forwarded-Host"] = i.Snapshot.XForwardedHost ?? NotSent,
        };

        if (!HostMatches(i.Snapshot.Host, i.AllowedHosts))
        {
            return DiagnosticItem.Fail(
                id,
                title,
                $"The Host header {host} is not one this server is set to answer to ({string.Join(", ", i.AllowedHosts)}). Other requests are being refused with 400 for the same reason.",
                facts,
                detail: ["Add the name your visitors use, or have the proxy send it as the Host header."],
                fix: new DiagnosticFix(
                    Nginx: "proxy_set_header Host $host;",
                    Caddy: "Caddy passes the original Host header to the upstream by default; nothing to change unless you set header_up Host.",
                    Yaml: $"server:\n  allowedHosts:\n    - \"{host.Split(':')[0]}\""));
        }

        string? forwardedHost = i.Snapshot.XForwardedHost?.Split(',')[0].Trim();
        if (!string.IsNullOrEmpty(forwardedHost) && !HostNamesEqual(forwardedHost, i.Snapshot.Host))
        {
            return DiagnosticItem.Warn(
                id,
                title,
                $"The server received Host: {host}, but the proxy says visitors asked for {forwardedHost} (X-Forwarded-Host). The server only checks Host. If you set server.allowedHosts, list the name the server RECEIVES; if you would rather it receive the public name, have the proxy pass it.",
                facts,
                fix: new DiagnosticFix(
                    Nginx: "proxy_set_header Host $host;   # passes the public name through",
                    Caddy: "Caddy passes the public Host through by default; remove any header_up Host line.",
                    Yaml: i.AllowedHostsConfigured ? $"server:\n  allowedHosts:\n    - \"{host.Split(':')[0]}\"" : null));
        }

        return DiagnosticItem.Pass(
            id,
            title,
            i.AllowedHosts is ["*"]
                ? $"Host: {host} was accepted. This server answers to any host name, which is right behind a proxy; set server.allowedHosts to your public name for defence in depth."
                : $"Host: {host} is one of the hosts this server answers to.",
            facts);
    }

    /// <summary>The same matching rule the framework's host filtering applies: the port is ignored, <c>*</c> is anything, <c>*.example.com</c> is any subdomain.</summary>
    public static bool HostMatches(string hostHeader, IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(hostHeader);
        ArgumentNullException.ThrowIfNull(patterns);
        string host = StripPort(hostHeader);
        foreach (string raw in patterns)
        {
            string pattern = StripPort(raw.Trim());
            if (pattern == "*" || string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal) && host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) && host.Length > pattern.Length - 1)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HostNamesEqual(string a, string b)
    {
        return string.Equals(StripPort(a), StripPort(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string StripPort(string host)
    {
        if (host.StartsWith('['))
        {
            int close = host.IndexOf(']', StringComparison.Ordinal);
            return close > 0 ? host[..(close + 1)] : host;
        }

        int colon = host.LastIndexOf(':');
        return colon > 0 && host.IndexOf(':', StringComparison.Ordinal) == colon ? host[..colon] : host;
    }

    // ------------------------------------------------------------------------------------------------------ pieces

    private static bool IsLoopback(IPAddress? address)
    {
        return address is null || IPAddress.IsLoopback(address);
    }

    private static string[] SplitList(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? [] : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>The setting that makes the server believe a proxy, with the address filled in. Quoted so a v6 address survives YAML.</summary>
    public static string KnownProxiesYaml(string peer)
    {
        return "server:\n  knownProxies:\n    - \"" + peer + "\"";
    }

    public const string NginxHeaders =
        "location / {\n" +
        "    proxy_pass http://127.0.0.1:5180;          # your Draughtsman address\n" +
        "    proxy_set_header Host $host;\n" +
        "    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;\n" +
        "    proxy_set_header X-Forwarded-Proto $scheme;\n" +
        "}";

    public const string NginxRealIp =
        "# Behind Cloudflare: take the visitor's address from CF-Connecting-IP, but only when the request really came from Cloudflare.\n" +
        "# One line per range listed at https://www.cloudflare.com/ips/ (they change; keep the list current):\n" +
        "set_real_ip_from 173.245.48.0/20;\n" +
        "real_ip_header CF-Connecting-IP;\n" +
        "# then send exactly that address on, not a growing list:\n" +
        "proxy_set_header X-Forwarded-For $remote_addr;\n" +
        "proxy_set_header X-Forwarded-Proto $scheme;";

    public const string CaddyRealIp =
        "{\n" +
        "    servers {\n" +
        "        trusted_proxies static 173.245.48.0/20   # every range at https://www.cloudflare.com/ips/\n" +
        "        client_ip_headers CF-Connecting-IP X-Forwarded-For\n" +
        "    }\n" +
        "}";
}
