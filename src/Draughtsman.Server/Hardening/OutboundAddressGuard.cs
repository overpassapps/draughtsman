using System.Net;
using System.Net.Sockets;

namespace Draughtsman.Server.Hardening;

/// <summary>Where an IP address sits, for anything the server connects out to. <see cref="Blocked"/> is refused whoever asks and whatever they list.</summary>
internal enum AddressKind
{
    Public,
    Private,
    Loopback,

    /// <summary>Link-local, cloud-metadata, "this network", multicast and reserved ranges. Never reachable, whoever lists the host.</summary>
    Blocked,
}

/// <summary>
/// The shared outbound-address guard: the one place that says what an IP address the server is about to connect to <i>is</i>. It knows
/// nothing about hosts, providers, allow-lists, DNS or sockets; a feature that connects out (the AI endpoint, AU-11, today) combines
/// this answer with its own rules, so a range added here lands for every such feature at once and the rules are tested once, in
/// <c>OutboundAddressGuardTests</c>.
/// <para>
/// Every IPv6 spelling that carries an IPv4 address (::ffff:a.b.c.d, ::a.b.c.d, ::ffff:0:a.b.c.d, 64:ff9b::/96, 2002::/16) is judged as
/// the IPv4 address it carries, so ::ffff:169.254.169.254 is as blocked as 169.254.169.254. How a URL spelled its host (decimal, hex or
/// octal IPv4, brackets around IPv6) is the URL parser's business: by the time an <see cref="IPAddress"/> reaches this class it is one value.
/// </para>
/// <para>
/// Two questions, for two kinds of caller. <see cref="Classify"/> is the four-way answer for a feature that may legitimately reach
/// loopback or a private network (the AI endpoint's Ollama). <see cref="IsGlobalUnicast"/> is the strict yes/no for a feature that may
/// reach only an ordinary public host on the internet.
/// </para>
/// </summary>
internal static class OutboundAddressGuard
{
    /// <summary>
    /// Where <paramref name="address"/> sits. An IPv6 address that embeds an IPv4 one (::ffff:a.b.c.d, ::a.b.c.d, 64:ff9b::a.b.c.d,
    /// 2002:a.b.c.d::, ::ffff:0:a.b.c.d) is judged as that IPv4 address, so ::ffff:169.254.169.254 is as blocked as 169.254.169.254.
    /// Documentation, benchmarking and Teredo ranges are <see cref="AddressKind.Public"/> here (nothing is lost by connecting to one: it
    /// answers nowhere, and a fake-IP VPN's resolver legitimately hands out 198.18.0.0/15); <see cref="IsGlobalUnicast"/> refuses them.
    /// </summary>
    public static AddressKind Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return ClassifyV4(address.GetAddressBytes());
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return AddressKind.Blocked;
        }

        if (address.IsIPv6Multicast || address.IsIPv6LinkLocal)
        {
            return AddressKind.Blocked;
        }

        byte[] b = address.GetAddressBytes();
        if (b.All(x => x == 0))
        {
            return AddressKind.Blocked; // ::
        }

        if (address.Equals(IPAddress.IPv6Loopback))
        {
            return AddressKind.Loopback;
        }

        if (EmbeddedV4(b) is { } embedded)
        {
            return ClassifyV4(embedded);
        }

        // 64:ff9b:1::/48 is NAT64's local-use prefix: the IPv4 address sits at a position that depends on the prefix length, so refuse it whole.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4] == 0x00 && b[5] == 0x01)
        {
            return AddressKind.Blocked;
        }

        // fd00:ec2::/32: AWS's reserved range (instance metadata is fd00:ec2::254), inside the unique-local block that is otherwise "private".
        if (b[0] == 0xfd && b[1] == 0x00 && b[2] == 0x0e && b[3] == 0xc2)
        {
            return AddressKind.Blocked;
        }

        if ((b[0] & 0xfe) == 0xfc || (b[0] == 0xfe && (b[1] & 0xc0) == 0xc0))
        {
            return AddressKind.Private; // fc00::/7 unique local; fec0::/10 deprecated site-local
        }

        return AddressKind.Public;
    }

    /// <summary>
    /// True only for an address an ordinary public server can have: <see cref="AddressKind.Public"/> and also outside the documentation
    /// (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24, 2001:db8::/32), benchmarking (198.18.0.0/15), Teredo (2001::/32), discard
    /// (100::/64) and the unassigned rest of ::/8 ranges. For a feature that must never reach loopback, a private network or anything
    /// special-purpose, such as a fetch of a URL a person typed. An embedded IPv4 address is judged by the same ranges.
    /// </summary>
    public static bool IsGlobalUnicast(IPAddress address)
    {
        if (Classify(address) != AddressKind.Public)
        {
            return false;
        }

        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !IsSpecialPurposeV4(b);
        }

        // The embedded forms first: 64:ff9b::/96 starts with a zero byte, which the ::/8 rule below would otherwise refuse.
        return EmbeddedV4(b) is { } embedded ? !IsSpecialPurposeV4(embedded) : !IsSpecialPurposeV6(b);
    }

    private static byte[]? EmbeddedV4(byte[] b)
    {
        bool zeroPrefix(int count) => b.Take(count).All(x => x == 0);

        // ::ffff:a.b.c.d (mapped), ::a.b.c.d (the deprecated compatible form) and ::ffff:0:a.b.c.d (SIIT-translated).
        if (zeroPrefix(10) && b[10] == 0xff && b[11] == 0xff)
        {
            return b[12..];
        }

        if (zeroPrefix(12))
        {
            return b[12..];
        }

        if (zeroPrefix(8) && b[8] == 0xff && b[9] == 0xff && b[10] == 0 && b[11] == 0)
        {
            return b[12..];
        }

        // 64:ff9b::/96, the well-known NAT64 prefix.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0))
        {
            return b[12..];
        }

        // 2002::/16, 6to4: the IPv4 address follows the prefix.
        return b[0] == 0x20 && b[1] == 0x02 ? b[2..6] : null;
    }

    private static AddressKind ClassifyV4(byte[] b)
    {
        if (b[0] >= 224)
        {
            return AddressKind.Blocked; // multicast, reserved, broadcast
        }

        return (b[0], b[1], b[2], b[3]) switch
        {
            (0, _, _, _) => AddressKind.Blocked, // "this network", including 0.0.0.0
            (127, _, _, _) => AddressKind.Loopback,
            (169, 254, _, _) => AddressKind.Blocked, // link-local, and so the metadata address 169.254.169.254
            (100, 100, 100, 200) => AddressKind.Blocked, // Alibaba Cloud metadata, inside the carrier-grade NAT range below
            (168, 63, 129, 16) => AddressKind.Blocked, // Azure's virtual public IP (wire server, metadata helpers)
            (192, 0, 0, _) => AddressKind.Blocked, // IETF protocol assignments, which hold Oracle's legacy 192.0.0.192
            (10, _, _, _) => AddressKind.Private,
            (172, >= 16 and <= 31, _, _) => AddressKind.Private,
            (192, 168, _, _) => AddressKind.Private,
            (100, >= 64 and <= 127, _, _) => AddressKind.Private, // carrier-grade NAT, which tailnets use
            _ => AddressKind.Public,
        };
    }

    private static bool IsSpecialPurposeV4(byte[] b)
    {
        return (b[0], b[1], b[2]) is (192, 0, 2) or (198, 18 or 19, _) or (198, 51, 100) or (203, 0, 113);
    }

    private static bool IsSpecialPurposeV6(byte[] b)
    {
        return b[0] == 0x00 // the rest of ::/8, which holds nothing routable
            || (b[0] == 0x01 && b[1] == 0x00 && b[2..8].All(x => x == 0)) // 100::/64, discard-only
            || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) // 2001::/32, Teredo
            || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8); // 2001:db8::/32, documentation
    }
}
