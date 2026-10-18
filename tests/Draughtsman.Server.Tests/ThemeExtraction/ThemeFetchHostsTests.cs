using System.Net;
using Draughtsman.Server.Theming.Extraction;

namespace Draughtsman.Server.Tests.ThemeColourExtraction;

/// <summary>The address rules of the "From a website" fetch, alone: no network, no HTTP. Each is asserted both ways (blocked and public).</summary>
public class ThemeFetchHostsTests
{
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
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
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
    [InlineData("::127.0.0.1")]
    [InlineData("::10.1.2.3")]
    [InlineData("64:ff9b::7f00:1")] // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64 of the metadata address
    [InlineData("2002:7f00:1::")] // 6to4 of 127.0.0.1
    [InlineData("2002:0a00:0001::")] // 6to4 of 10.0.0.1
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")] // Teredo
    [InlineData("2001:db8::1")]
    public void Internal_reserved_and_embedded_internal_addresses_are_blocked(string text)
    {
        Assert.True(ThemeFetchHosts.IsBlocked(IPAddress.Parse(text)), text);
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
    [InlineData("192.169.0.1")]
    [InlineData("198.17.0.1")]
    [InlineData("198.20.0.1")]
    [InlineData("223.255.255.254")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")] // NAT64 of 8.8.8.8
    [InlineData("2002:808:808::")] // 6to4 of 8.8.8.8
    public void Public_addresses_are_not_blocked(string text)
    {
        Assert.False(ThemeFetchHosts.IsBlocked(IPAddress.Parse(text)), text);
    }

    [Theory]
    [InlineData("https://2130706433/", "127.0.0.1")] // decimal
    [InlineData("https://0x7f000001/", "127.0.0.1")] // hex
    [InlineData("https://0177.0.0.1/", "127.0.0.1")] // octal
    [InlineData("https://0x7f.0.0.1/", "127.0.0.1")] // dotted hex
    [InlineData("https://127.1/", "127.0.0.1")] // short form
    [InlineData("https://2852039166/", "169.254.169.254")]
    [InlineData("https://0251.0376.0251.0376/", "169.254.169.254")]
    [InlineData("https://[::1]/", "::1")]
    [InlineData("https://[::ffff:127.0.0.1]/", "127.0.0.1")]
    [InlineData("https://[fd00:ec2::254]/", "fd00:ec2::254")]
    public void Numeric_and_bracketed_spellings_of_an_address_become_that_address_and_are_blocked(string url, string expected)
    {
        ThemeFetchHosts.HostKind kind = ThemeFetchHosts.ClassifyHost(new Uri(url), out string host, out IPAddress? address);

        Assert.Equal(ThemeFetchHosts.HostKind.Address, kind);
        Assert.NotNull(address);
        Assert.Equal(expected, host.Replace("::ffff:", string.Empty, StringComparison.Ordinal));
        Assert.True(ThemeFetchHosts.IsBlocked(address));
    }

    [Theory]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("https://Example.COM./", "example.com")]
    [InlineData("https://sub.example.co.uk:8443/x?y=1#z", "sub.example.co.uk")]
    [InlineData("https://localhost/", "localhost")]
    [InlineData("https://bücher.example/", "xn--bcher-kva.example")]
    public void Names_are_lower_case_without_a_trailing_dot_and_in_punycode(string url, string expected)
    {
        Assert.Equal(ThemeFetchHosts.HostKind.Name, ThemeFetchHosts.ClassifyHost(new Uri(url), out string host, out _));
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("https://foo.123/")]
    [InlineData("https://foo.0x7f/")]
    [InlineData("https://a.b.0xff/")]
    public void A_name_that_ends_in_digits_or_0x_is_refused_as_numeric_not_resolved(string url)
    {
        Assert.Equal(ThemeFetchHosts.HostKind.Numeric, ThemeFetchHosts.ClassifyHost(new Uri(url), out _, out _));
    }

    [Theory]
    [InlineData("http://localhost/", true)]
    [InlineData("http://localhost./", true)]
    [InlineData("http://127.0.0.1:8080/", true)]
    [InlineData("http://127.5.5.5/", true)]
    [InlineData("http://[::1]/", true)]
    [InlineData("http://example.com/", false)]
    [InlineData("http://localhost.example.com/", false)]
    [InlineData("http://10.0.0.1/", false)]
    [InlineData("http://8.8.8.8/", false)]
    public void Plain_http_is_for_localhost_and_loopback_addresses_only(string url, bool loopback)
    {
        Assert.Equal(loopback, ThemeFetchHosts.IsLoopbackHost(new Uri(url)));
    }

    [Theory]
    [InlineData("Intranet.Example.com", "intranet.example.com")]
    [InlineData("  wiki  ", "wiki")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("[::1]", "::1")]
    [InlineData("::1", "::1")]
    [InlineData("example.com.", "example.com")]
    [InlineData("https://example.com", null)]
    [InlineData("example.com:8080", null)]
    [InlineData("example.com/path", null)]
    [InlineData("*.example.com", null)]
    [InlineData("a b", null)]
    [InlineData("-bad", null)]
    [InlineData("a..b", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void An_allow_list_entry_is_exactly_a_host_or_it_is_dropped(string? entry, string? expected)
    {
        Assert.Equal(expected, ThemeFetchHosts.NormaliseAllowedHost(entry));
    }

    [Fact]
    public void A_host_is_judged_by_every_address_it_resolves_to_and_the_allow_list_lifts_the_rule()
    {
        IPAddress pub = IPAddress.Parse("93.184.216.34");
        IPAddress priv = IPAddress.Parse("10.1.2.3");

        Assert.True(ThemeFetchHosts.MayConnect([pub], allowListed: false));
        Assert.True(ThemeFetchHosts.MayConnect([pub, IPAddress.Parse("2606:2800:220:1::")], allowListed: false));
        Assert.False(ThemeFetchHosts.MayConnect([priv], allowListed: false));
        Assert.False(ThemeFetchHosts.MayConnect([pub, priv], allowListed: false)); // one bad answer among good ones
        Assert.False(ThemeFetchHosts.MayConnect([priv, pub], allowListed: false));
        Assert.True(ThemeFetchHosts.MayConnect([priv], allowListed: true));
        Assert.True(ThemeFetchHosts.MayConnect([pub, priv], allowListed: true));
        Assert.False(ThemeFetchHosts.MayConnect([], allowListed: true)); // nothing resolved is never allowed
        Assert.True(ThemeFetchHosts.IsAllowed("wiki.corp", ["wiki.corp"]));
        Assert.False(ThemeFetchHosts.IsAllowed("wiki.corp.evil.example", ["wiki.corp"]));
        Assert.False(ThemeFetchHosts.IsAllowed("evil.wiki.corp", ["wiki.corp"]));
    }
}
