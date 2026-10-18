using System.Net;
using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Hardening;

/// <summary>
/// Which Host headers the server answers to (AU-19; the audit's <c>AllowedHosts: *</c>). A server that answers to any
/// host can be reached under a name an attacker controls - DNS rebinding turns a browser on the operator's own machine
/// into a client of a loopback-only instance. The setting is <see cref="ServerConfig.AllowedHosts"/>; left empty, the
/// default follows how the server is exposed, so no install has to configure it to keep working:
/// <list type="bullet">
/// <item>configured names win, exactly as written;</item>
/// <item>a proxy named in <see cref="ServerConfig.KnownProxies"/> means the public name is not ours to guess: any host;</item>
/// <item>bound to loopback only (the default install): <c>localhost</c>, <c>127.0.0.1</c> and <c>[::1]</c>;</item>
/// <item>bound anywhere else (a container, a LAN install): any host, since the server cannot know which names point at it.</item>
/// </list>
/// A loopback server behind a proxy on the same machine that forwards the public Host header therefore needs either
/// <c>knownProxies</c> (which it needs anyway, for client addresses and Secure cookies) or <c>allowedHosts</c>.
/// </summary>
public static class HostPolicy
{
    public const string Any = "*";

    public static string[] Resolve(ServerConfig server, string? aspNetCoreUrls)
    {
        List<string> configured = server.AllowedHosts.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).ToList();
        if (configured.Count > 0)
        {
            return [.. configured];
        }

        if (server.KnownProxies.Any(p => !string.IsNullOrWhiteSpace(p)))
        {
            return [Any];
        }

        return IsLoopbackOnly(server, aspNetCoreUrls) ? ["localhost", "127.0.0.1", "[::1]"] : [Any];
    }

    /// <summary>True when every address the server will listen on is loopback.</summary>
    private static bool IsLoopbackOnly(ServerConfig server, string? aspNetCoreUrls)
    {
        if (string.IsNullOrWhiteSpace(aspNetCoreUrls))
        {
            return IsLoopbackHost(server.Host);
        }

        string[] urls = aspNetCoreUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return urls.Length > 0 && urls.All(url => Uri.TryCreate(url.Replace("://+", "://placeholder", StringComparison.Ordinal).Replace("://*", "://placeholder", StringComparison.Ordinal), UriKind.Absolute, out Uri? uri)
            && IsLoopbackHost(uri.Host));
    }

    private static bool IsLoopbackHost(string host)
    {
        string trimmed = host.Trim('[', ']');
        return IPAddress.TryParse(trimmed, out IPAddress? address)
            ? IPAddress.IsLoopback(address)
            : string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
