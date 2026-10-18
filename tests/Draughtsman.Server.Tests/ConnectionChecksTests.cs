using System.Net;
using Draughtsman.Server.Diagnostics;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The connection self-check's server-side rules, each in both directions: what a good setup says and what each common mistake says, with
/// the numbers and the fix text a person would act on. The rules are pure over <see cref="ConnectionInputs"/>, so a scenario here is just
/// the values a real request would have produced.
/// </summary>
public class ConnectionChecksTests
{
    private static ConnectionInputs Request(
        string peer = "203.0.113.5",
        bool trusted = false,
        string rawScheme = "http",
        string finalScheme = "http",
        string? xForwardedProto = null,
        string? xForwardedFor = null,
        string? xForwardedHost = null,
        string? cf = null,
        string host = "draughtsman.example.com",
        bool forceHttps = false,
        string[]? allowedHosts = null,
        bool allowedConfigured = false,
        string[]? knownProxies = null,
        string? pageScheme = null,
        string? throttle = null)
    {
        var snapshot = new ForwardedSnapshot(IPAddress.Parse(peer), rawScheme, host, xForwardedProto, xForwardedFor, xForwardedHost, cf, null, trusted);
        return new ConnectionInputs(snapshot, finalScheme, IPAddress.Parse(throttle ?? peer), forceHttps, allowedHosts ?? ["*"], allowedConfigured, knownProxies ?? [], pageScheme);
    }

    private static DiagnosticItem Item(ConnectionInputs inputs, string id)
    {
        return ConnectionChecks.Evaluate(inputs).Single(i => i.Id == id);
    }

    // ------------------------------------------------------------------------------------------------------ proxy

    [Fact]
    public void A_direct_connection_with_no_proxy_headers_passes()
    {
        DiagnosticItem item = Item(Request(), "proxy");

        Assert.Equal(DiagnosticStatus.Pass, item.Status);
        Assert.Equal("203.0.113.5", item.Facts!["Direct peer address"]);
        Assert.Equal(ConnectionChecks.NotSent, item.Facts["X-Forwarded-For"]);
    }

    [Fact]
    public void Proxy_headers_from_a_peer_that_is_not_trusted_warn_and_name_the_address_to_list()
    {
        DiagnosticItem item = Item(Request(peer: "172.17.0.1", xForwardedProto: "https", xForwardedFor: "198.51.100.7"), "proxy");

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
        Assert.Contains("172.17.0.1", item.Summary, StringComparison.Ordinal);
        Assert.Contains("IGNORES", item.Summary, StringComparison.Ordinal);
        Assert.Equal("no", item.Facts!["Peer trusted (server.knownProxies)"]);
        // The raw header values the server received are shown, not only its verdict.
        Assert.Equal("https", item.Facts["X-Forwarded-Proto"]);
        Assert.Equal("198.51.100.7", item.Facts["X-Forwarded-For"]);
        // The exact lines to paste, with the peer filled in, for each place the fix could live.
        Assert.Contains("- \"172.17.0.1\"", item.Fix!.Yaml, StringComparison.Ordinal);
        Assert.Contains("proxy_set_header X-Forwarded-Proto $scheme;", item.Fix.Nginx, StringComparison.Ordinal);
        Assert.NotNull(item.Fix.Caddy);
        Assert.NotNull(item.Fix.Cloudflare);
    }

    [Fact]
    public void The_same_headers_from_a_trusted_peer_pass_and_say_which_address_the_throttle_will_use()
    {
        DiagnosticItem item = Item(Request(peer: "172.17.0.1", trusted: true, xForwardedProto: "https", xForwardedFor: "198.51.100.7", finalScheme: "https", throttle: "198.51.100.7"), "proxy");

        Assert.Equal(DiagnosticStatus.Pass, item.Status);
        Assert.Equal("yes", item.Facts!["Peer trusted (server.knownProxies)"]);
        Assert.Equal("198.51.100.7", item.Facts["Client address the sign-in throttle uses"]);
        Assert.Contains("198.51.100.7", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Behind_cloudflare_the_visitor_cloudflare_names_must_be_the_visitor_the_throttle_uses()
    {
        // Trusted proxy, but the throttle address is a Cloudflare edge: every visitor on that edge shares one bucket.
        DiagnosticItem shared = Item(Request(peer: "10.0.0.2", trusted: true, xForwardedFor: "198.51.100.7, 172.70.1.2", cf: "198.51.100.7", throttle: "172.70.1.2"), "proxy");
        // Same setup with nginx's real_ip in place: the throttle address is the visitor Cloudflare reported.
        DiagnosticItem fixedUp = Item(Request(peer: "10.0.0.2", trusted: true, xForwardedFor: "198.51.100.7", cf: "198.51.100.7", throttle: "198.51.100.7"), "proxy");

        Assert.Equal(DiagnosticStatus.Warn, shared.Status);
        Assert.Contains("172.70.1.2", shared.Summary, StringComparison.Ordinal);
        Assert.Contains("real_ip_header CF-Connecting-IP", shared.Fix!.Nginx, StringComparison.Ordinal);
        Assert.Contains("client_ip_headers CF-Connecting-IP", shared.Fix.Caddy, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStatus.Pass, fixedUp.Status);
    }

    [Fact]
    public void A_forwarded_chain_of_several_addresses_warns_that_the_last_one_is_taken_as_the_visitor()
    {
        DiagnosticItem item = Item(Request(peer: "10.0.0.2", trusted: true, xForwardedFor: "198.51.100.7, 172.70.1.2", throttle: "172.70.1.2"), "proxy");

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
        Assert.Contains("LAST one, 172.70.1.2", item.Summary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------ secure

    [Fact]
    public void Force_https_while_you_are_on_plain_http_fails_because_the_secure_cookie_is_dropped()
    {
        DiagnosticItem item = Item(Request(forceHttps: true, finalScheme: "https", pageScheme: "http"), "secure");

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Contains("plain http://", item.Summary, StringComparison.Ordinal);
        Assert.Contains("Signing in…", item.Summary, StringComparison.Ordinal);
        Assert.Equal("__Host-ds_session (Secure)", item.Facts!["Session cookie this request would get"]);
        Assert.Contains("forceHttps: false", item.Fix!.Yaml, StringComparison.Ordinal);
        Assert.Contains("return 301 https://$host$request_uri;", item.Fix.Nginx, StringComparison.Ordinal);
    }

    [Fact]
    public void Force_https_on_an_https_page_passes()
    {
        DiagnosticItem item = Item(Request(forceHttps: true, finalScheme: "https", pageScheme: "https"), "secure");

        Assert.Equal(DiagnosticStatus.Pass, item.Status);
        Assert.Contains("keeps it", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_https_page_that_the_server_believes_is_http_warns_that_the_cookie_is_not_secure()
    {
        DiagnosticItem item = Item(Request(pageScheme: "https"), "secure");

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
        Assert.Equal("ds_session (not Secure)", item.Facts!["Session cookie this request would get"]);
        Assert.Contains("forceHttps: true", item.Fix!.Yaml, StringComparison.Ordinal);
        Assert.Contains("X-Forwarded-Proto", item.Fix.Nginx, StringComparison.Ordinal);
    }

    [Fact]
    public void An_https_page_with_an_untrusted_proxy_saying_https_names_that_as_the_reason()
    {
        DiagnosticItem item = Item(Request(peer: "172.17.0.1", xForwardedProto: "https", pageScheme: "https"), "secure");

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
        Assert.Contains("X-Forwarded-Proto: https", string.Join(' ', item.Detail!), StringComparison.Ordinal);
        Assert.Contains("not a trusted proxy", string.Join(' ', item.Detail!), StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_that_believes_https_passes_without_force_https()
    {
        DiagnosticItem item = Item(Request(peer: "10.0.0.2", trusted: true, xForwardedProto: "https", finalScheme: "https", pageScheme: "https"), "secure");

        Assert.Equal(DiagnosticStatus.Pass, item.Status);
        Assert.Equal("__Host-ds_session (Secure)", item.Facts!["Session cookie this request would get"]);
    }

    [Fact]
    public void Plain_http_everywhere_passes_with_a_note()
    {
        DiagnosticItem item = Item(Request(pageScheme: "http"), "secure");

        Assert.Equal(DiagnosticStatus.Pass, item.Status);
        Assert.Contains("not Secure", item.Summary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------ host

    [Fact]
    public void A_host_header_that_matches_the_allowed_hosts_passes()
    {
        DiagnosticItem item = Item(Request(host: "draughtsman.example.com", allowedHosts: ["draughtsman.example.com"], allowedConfigured: true), "host");

        Assert.Equal(DiagnosticStatus.Pass, item.Status);
        Assert.Equal("draughtsman.example.com", item.Facts!["Host header received"]);
    }

    [Fact]
    public void A_host_header_the_server_would_refuse_fails_and_gives_the_line_to_add()
    {
        DiagnosticItem item = Item(Request(host: "draughtsman.example.com", allowedHosts: ["localhost", "127.0.0.1"], allowedConfigured: false), "host");

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Contains("- \"draughtsman.example.com\"", item.Fix!.Yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_proxy_that_forwards_a_different_public_name_than_the_host_it_sends_warns()
    {
        DiagnosticItem item = Item(Request(peer: "10.0.0.2", trusted: true, host: "draughtsman-app:5180", xForwardedHost: "draughtsman.example.com", allowedHosts: ["draughtsman-app"], allowedConfigured: true), "host");

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
        Assert.Contains("draughtsman.example.com", item.Summary, StringComparison.Ordinal);
        Assert.Contains("draughtsman-app:5180", item.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost:5180", "localhost", true)]
    [InlineData("LOCALHOST", "localhost", true)]
    [InlineData("[::1]:5180", "[::1]", true)]
    [InlineData("a.example.com", "*.example.com", true)]
    [InlineData("example.com", "*.example.com", false)]
    [InlineData("evil.example.org", "*.example.com", false)]
    [InlineData("anything", "*", true)]
    [InlineData("draughtsman.example.com", "localhost", false)]
    public void Host_matching_follows_the_framework_rules(string host, string pattern, bool expected)
    {
        Assert.Equal(expected, ConnectionChecks.HostMatches(host, [pattern]));
    }

    // ------------------------------------------------------------------------------------------------------ trust, worked out like the framework

    [Fact]
    public void Trust_is_decided_exactly_as_the_forwarded_headers_middleware_decides_it()
    {
        var options = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions();
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        // With neither list populated the framework believes every peer.
        Assert.True(ForwardedTrust.IsTrusted(IPAddress.Parse("203.0.113.9"), options));

        options.KnownProxies.Add(IPAddress.Parse("203.0.113.7"));
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.18.0.0/16"));
        Assert.True(ForwardedTrust.IsTrusted(IPAddress.Parse("203.0.113.7"), options));
        Assert.True(ForwardedTrust.IsTrusted(IPAddress.Parse("172.18.9.9"), options));
        Assert.True(ForwardedTrust.IsTrusted(IPAddress.Parse("172.18.9.9").MapToIPv6(), options));
        Assert.False(ForwardedTrust.IsTrusted(IPAddress.Parse("203.0.113.8"), options));
        Assert.False(ForwardedTrust.IsTrusted(IPAddress.Parse("172.19.0.1"), options));
        Assert.False(ForwardedTrust.IsTrusted(null, options));
    }
}
