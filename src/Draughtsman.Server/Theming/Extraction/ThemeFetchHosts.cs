using System.Net;
using Draughtsman.Server.Hardening;

namespace Draughtsman.Server.Theming.Extraction;

/// <summary>
/// How the Themes page's "From a website" fetch reads a host: what a URL's host is (a name, an address, or a numeric trick), whether
/// it is the loopback that plain http may reach, whether the operator listed it in <c>themes.fetchFromUrl.allowedHosts</c>, and whether
/// the addresses it resolved to may be connected to. The address ranges themselves are not here: they are the shared
/// <see cref="OutboundAddressGuard"/> (<see cref="OutboundAddressGuard.IsGlobalUnicast"/>, the strict form), so a range added there lands
/// for this fetch too. A host name is judged by every address it resolves to: one blocked address among several refuses the host. An
/// operator may list a host to reach it anyway; nothing else lifts the rule.
/// <para>
/// Address-literal tricks (decimal 2130706433, octal 0177.0.0.1, hex 0x7f.1, bracketed [::1]) are resolved by the URL parser into a
/// real address before this class sees them, and a name that still looks numeric after parsing (a last label of digits or 0x…) is
/// refused as <see cref="HostKind.Numeric"/>, since no real name ends that way and a resolver might read it as an address.
/// </para>
/// </summary>
internal static class ThemeFetchHosts
{
    public enum HostKind
    {
        /// <summary>An ordinary DNS name.</summary>
        Name,

        /// <summary>An IPv4 or IPv6 address, however it was written.</summary>
        Address,

        /// <summary>Something that looks like a number rather than a name and is not a clean address: refused.</summary>
        Numeric,
    }

    /// <summary>True when connecting to <paramref name="address"/> is refused for a host that is not on the allow-list: anything but an ordinary public address, as the shared <see cref="OutboundAddressGuard"/> judges it.</summary>
    public static bool IsBlocked(IPAddress address)
    {
        return !OutboundAddressGuard.IsGlobalUnicast(address);
    }

    /// <summary>
    /// Judges a parsed URL host. An address literal comes back as <see cref="HostKind.Address"/> with the address; a name as
    /// <see cref="HostKind.Name"/> in lower case without a trailing dot; a numeric-looking non-address as <see cref="HostKind.Numeric"/>.
    /// </summary>
    public static HostKind ClassifyHost(Uri uri, out string host, out IPAddress? address)
    {
        ArgumentNullException.ThrowIfNull(uri);
        address = null;
        host = uri.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(host, out IPAddress? literal))
        {
            address = literal;
            host = literal.ToString();
            return HostKind.Address;
        }

        if (host.Length == 0 || host.Length > 253)
        {
            return HostKind.Numeric;
        }

        string last = host[(host.LastIndexOf('.') + 1)..];
        if (last.Length == 0 || last.All(char.IsAsciiDigit) || last.StartsWith("0x", StringComparison.Ordinal))
        {
            return HostKind.Numeric;
        }

        return HostKind.Name;
    }

    /// <summary>True when <paramref name="uri"/> names this machine in the one way plain http is allowed to: <c>localhost</c> or a loopback address.</summary>
    public static bool IsLoopbackHost(Uri uri)
    {
        HostKind kind = ClassifyHost(uri, out string host, out IPAddress? address);
        return kind == HostKind.Address ? address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address) : host == "localhost";
    }

    /// <summary>
    /// The form an allow-list entry is compared in: a plain host name in lower case, or an address in canonical text. Null for anything
    /// that is not exactly a host: a scheme, a port, a path, a wildcard, a space or an empty entry.
    /// </summary>
    public static string? NormaliseAllowedHost(string? entry)
    {
        string text = (entry ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        if (text.Length is 0 or > 253)
        {
            return null;
        }

        string bare = text.Trim('[', ']');
        if (IPAddress.TryParse(bare, out IPAddress? address))
        {
            return address.ToString();
        }

        bool plain = text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') && text[0] != '-' && text[0] != '.' && !text.Contains("..", StringComparison.Ordinal);
        return plain ? text : null;
    }

    /// <summary>True when <paramref name="host"/> (as <see cref="ClassifyHost"/> gives it) is one the operator listed.</summary>
    public static bool IsAllowed(string host, IReadOnlyCollection<string> normalisedAllowedHosts)
    {
        return normalisedAllowedHosts.Contains(host, StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a host that resolved to <paramref name="addresses"/> may be connected to: never when it resolved to nothing, and, unless
    /// <paramref name="allowListed"/>, never when any one of the addresses is blocked.
    /// </summary>
    public static bool MayConnect(IReadOnlyList<IPAddress> addresses, bool allowListed)
    {
        return addresses.Count > 0 && (allowListed || !addresses.Any(IsBlocked));
    }
}
