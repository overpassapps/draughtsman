using System.Net;
using Draughtsman.Server.Hardening;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The shared outbound-address guard (<see cref="OutboundAddressGuard"/>) alone: no DNS, no sockets, no feature's rules. Every range is
/// asserted from both sides, the address that must be refused and its nearest neighbour that must not be, because a boundary that
/// moves or a range that is dropped has to fail here before any feature that relies on the guard can ship with the hole.
/// <see cref="AiEndpointPolicyTests"/> and <see cref="AiConnectGuardTests"/> test what the AI endpoint does with the answer.
/// </summary>
public class OutboundAddressGuardTests
{
    [Theory]
    [InlineData("169.254.169.254")] // the instance-metadata address of AWS, Azure, GCP, DigitalOcean and OpenStack
    [InlineData("169.254.0.1")]
    [InlineData("169.254.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("100.100.100.200")] // Alibaba Cloud metadata, inside the range tailnets use
    [InlineData("168.63.129.16")] // Azure's wire server
    [InlineData("192.0.0.192")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("ff02::1")]
    [InlineData("fd00:ec2::254")] // AWS instance metadata over IPv6, inside the unique-local block
    [InlineData("fd00:ec2::1")]
    [InlineData("::ffff:169.254.169.254")] // IPv4-mapped
    [InlineData("::ffff:a9fe:a9fe")] // the same address, spelt in hex
    [InlineData("::169.254.169.254")] // IPv4-compatible
    [InlineData("::a9fe:a9fe")]
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64
    [InlineData("64:ff9b::169.254.169.254")]
    [InlineData("2002:a9fe:a9fe::1")] // 6to4
    [InlineData("::ffff:0:169.254.169.254")] // SIIT-translated
    [InlineData("64:ff9b:1::1")] // NAT64 local-use prefix: the embedded address cannot be read, so the whole prefix is refused
    [InlineData("::ffff:0.0.0.0")]
    [InlineData("::ffff:224.0.0.1")]
    public void Link_local_metadata_and_reserved_addresses_are_blocked_in_every_spelling(string address)
    {
        Assert.Equal(AddressKind.Blocked, OutboundAddressGuard.Classify(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::7f00:1")]
    public void Loopback_is_loopback_in_every_spelling(string address)
    {
        Assert.Equal(AddressKind.Loopback, OutboundAddressGuard.Classify(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.1.20")]
    [InlineData("100.64.0.1")] // carrier-grade NAT: a tailnet
    [InlineData("100.127.255.255")]
    [InlineData("100.100.100.199")] // the address next to Alibaba's metadata one is merely a tailnet host
    [InlineData("100.100.100.201")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00:ec3::1")] // the block next to AWS's reserved one
    [InlineData("fec0::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:c0a8:101::1")] // 6to4 of 192.168.1.1
    public void Private_addresses_are_private_in_every_spelling(string address)
    {
        Assert.Equal(AddressKind.Private, OutboundAddressGuard.Classify(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("93.184.216.34")]
    [InlineData("203.0.113.7")]
    [InlineData("172.15.255.255")] // just under 172.16/12
    [InlineData("172.32.0.1")] // just over it
    [InlineData("100.63.255.255")] // just under 100.64/10
    [InlineData("100.128.0.1")] // just over it
    [InlineData("169.253.255.255")] // just under link-local
    [InlineData("169.255.0.1")] // just over it
    [InlineData("223.255.255.255")] // just under multicast
    [InlineData("192.0.1.1")] // just outside 192.0.0.0/24
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2002:808:808::1")] // 6to4 of 8.8.8.8
    [InlineData("64:ff9b::808:808")]
    [InlineData("fe7f::1")] // just under fe80::/10 (and not unique-local either)
    public void An_address_beside_a_reserved_range_is_public(string address)
    {
        Assert.Equal(AddressKind.Public, OutboundAddressGuard.Classify(IPAddress.Parse(address)));
    }

    // What a feature that may reach only an ordinary public host asks (IsGlobalUnicast). The lists below are the ones the Themes page's
    // "From a website" fetch pinned for its own copy of this check, so that feature can swap to the guard and keep every one of its rules.

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("10.0.0.5")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // cloud metadata
    [InlineData("169.254.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("192.0.0.8")]
    [InlineData("192.0.2.1")] // documentation (TEST-NET-1)
    [InlineData("198.18.0.1")] // benchmarking
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.7")] // documentation (TEST-NET-2)
    [InlineData("203.0.113.9")] // documentation (TEST-NET-3)
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fe80::a9fe:a9fe")]
    [InlineData("fec0::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00::1")]
    [InlineData("fd00:ec2::254")] // cloud metadata over IPv6
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("::ffff:203.0.113.9")] // a documentation address inside a mapped one
    [InlineData("::127.0.0.1")]
    [InlineData("::10.1.2.3")]
    [InlineData("64:ff9b::7f00:1")] // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64 of the metadata address
    [InlineData("64:ff9b::cb00:7109")] // NAT64 of 203.0.113.9
    [InlineData("2002:7f00:1::")] // 6to4 of 127.0.0.1
    [InlineData("2002:0a00:0001::")] // 6to4 of 10.0.0.1
    [InlineData("2002:c633:6407::")] // 6to4 of 198.51.100.7
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")] // Teredo
    [InlineData("2001:db8::1")] // documentation
    [InlineData("100::1")] // discard-only
    [InlineData("0:1::1")] // the unassigned rest of ::/8
    public void A_global_unicast_check_refuses_internal_reserved_and_special_purpose_addresses(string address)
    {
        Assert.False(OutboundAddressGuard.IsGlobalUnicast(IPAddress.Parse(address)), address);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.255.255")] // just below the private range
    [InlineData("172.32.0.1")] // just above it
    [InlineData("100.63.255.255")] // just below shared space
    [InlineData("100.128.0.1")] // just above it
    [InlineData("169.253.1.1")]
    [InlineData("192.0.1.1")] // beside 192.0.0.0/24 and 192.0.2.0/24
    [InlineData("192.0.3.1")]
    [InlineData("192.169.0.1")]
    [InlineData("198.17.0.1")] // just below 198.18.0.0/15
    [InlineData("198.20.0.1")] // just above it
    [InlineData("198.51.99.1")] // beside 198.51.100.0/24
    [InlineData("198.51.101.1")]
    [InlineData("203.0.112.1")] // beside 203.0.113.0/24
    [InlineData("203.0.114.1")]
    [InlineData("223.255.255.254")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("2001:db9::1")] // beside 2001:db8::/32
    [InlineData("2001:1::1")] // beside Teredo's 2001::/32
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")] // NAT64 of 8.8.8.8
    [InlineData("2002:808:808::")] // 6to4 of 8.8.8.8
    public void A_global_unicast_check_accepts_an_address_beside_those_ranges(string address)
    {
        Assert.True(OutboundAddressGuard.IsGlobalUnicast(IPAddress.Parse(address)), address);
    }

    /// <summary>The two questions disagree on purpose: Classify leaves a documentation or benchmarking address public (so a fake-IP VPN's answers still work for AI), IsGlobalUnicast does not.</summary>
    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("198.18.0.1")]
    [InlineData("2001:db8::1")]
    public void Classify_keeps_special_purpose_ranges_public_while_the_strict_check_refuses_them(string address)
    {
        IPAddress parsed = IPAddress.Parse(address);
        Assert.Equal(AddressKind.Public, OutboundAddressGuard.Classify(parsed));
        Assert.False(OutboundAddressGuard.IsGlobalUnicast(parsed));
    }

    [Fact]
    public void A_missing_address_is_an_error_not_an_answer()
    {
        Assert.Throws<ArgumentNullException>(() => OutboundAddressGuard.Classify(null!));
    }
}
