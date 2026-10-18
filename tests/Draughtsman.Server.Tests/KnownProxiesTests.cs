using System.Net;
using System.Net.Http.Json;
using Draughtsman.Server.Hardening;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Tests;

/// <summary>
/// AU-02: server.knownProxies takes CIDR ranges as well as exact addresses, and reports an entry it cannot read instead of
/// dropping it in silence. The pipeline tests watch what trust decides: a login from a proxy that says <c>X-Forwarded-For: A</c>
/// is recorded in the security events as coming from A when the proxy is trusted, and from the proxy itself when it is not.
/// </summary>
public class KnownProxiesTests
{
    [Fact]
    public void Addresses_ranges_and_rejects_are_told_apart()
    {
        ForwardedProxies.Parsed parsed = ForwardedProxies.Parse(["10.0.0.1", " ::1 ", "172.18.0.0/16", "fd00::/8", "proxy.internal", "10.0.0.5/8", "10.0.0.0/33", "", "nonsense/8"]);

        Assert.Equal(["10.0.0.1", "::1"], parsed.Addresses.Select(a => a.ToString()));
        // 10.0.0.5/8 names the network the address is in, which is what an operator typing it means.
        Assert.Equal(["172.18.0.0/16", "fd00::/8", "10.0.0.0/8"], parsed.Networks.Select(n => n.ToString()));
        Assert.Equal(3, parsed.Rejected.Count);
        Assert.Contains(parsed.Rejected, r => r.StartsWith("proxy.internal", StringComparison.Ordinal) && r.Contains("IP address", StringComparison.Ordinal));
        Assert.Contains(parsed.Rejected, r => r.StartsWith("10.0.0.0/33", StringComparison.Ordinal));
        Assert.Contains(parsed.Rejected, r => r.StartsWith("nonsense/8", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_from_inside_a_trusted_range_is_believed_about_who_it_forwards_for_and_one_from_outside_is_not()
    {
        using var factory = new FakeIpServerFactory("server:\n  knownProxies:\n    - 172.18.0.0/16\n");

        bool inside = await Trusts(factory, "172.18.4.9");
        bool outside = await Trusts(factory, "172.19.0.1");

        Assert.True(inside, "the proxy in 172.18.0.0/16 should be trusted");
        Assert.False(outside, "an address outside every listed range must not be believed");
    }

    [Fact]
    public async Task An_exact_address_still_works_beside_a_range()
    {
        using var factory = new FakeIpServerFactory("server:\n  knownProxies:\n    - 203.0.113.7\n    - 10.0.0.0/8\n");

        Assert.True(await Trusts(factory, "203.0.113.7"));
        Assert.True(await Trusts(factory, "10.200.1.1"));
        Assert.False(await Trusts(factory, "203.0.113.8"));
    }

    [Fact]
    public async Task With_nothing_listed_a_remote_caller_cannot_forge_the_scheme()
    {
        using var factory = new FakeIpServerFactory();

        Assert.False(await Trusts(factory, "198.51.100.23"));
    }

    [Fact]
    public async Task A_proxy_on_the_same_machine_is_trusted_with_nothing_listed_which_is_the_frameworks_own_default()
    {
        // Kept deliberately: a Caddy or nginx on the same host reaches a loopback-bound server from 127.0.0.1, and trusting
        // it needs no setting. Anything that is not loopback has to be listed.
        using var factory = new FakeIpServerFactory();

        Assert.True(await Trusts(factory, "127.0.0.1"));
        Assert.False(await Trusts(factory, "10.1.2.3"));
    }

    [Fact]
    public async Task An_entry_that_cannot_be_read_is_logged_by_name_and_does_not_stop_the_good_ones()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new FakeIpServerFactory("server:\n  knownProxies:\n    - proxy.internal\n    - 10.0.0.0/33\n    - 10.0.0.0/8\n", logs);

        bool trusted = await Trusts(factory, "10.9.9.9");

        Assert.True(trusted);
        List<string> warnings = logs.Lines.Where(l => l.Level == LogLevel.Warning && l.Category.Contains("KnownProxies", StringComparison.Ordinal)).Select(l => l.Message).ToList();
        Assert.Contains(warnings, w => w.Contains("proxy.internal", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("10.0.0.0/33", StringComparison.Ordinal));
        Assert.Equal(2, warnings.Count);
    }

    /// <summary>True when a sign-in arriving from <paramref name="remoteIp"/> that claims <c>X-Forwarded-For: A</c> is recorded as coming from A.</summary>
    private static async Task<bool> Trusts(FakeIpServerFactory factory, string remoteIp)
    {
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string claimed = $"198.18.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = AuthTestHelpers.DefaultEmail, password = AuthTestHelpers.DefaultPassword }),
        };
        login.Headers.Add(FakeIpServerFactory.Header, remoteIp);
        login.Headers.Add("X-Forwarded-For", claimed);
        HttpResponseMessage response = await factory.CreateClient().SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        HttpResponseMessage events = await admin.GetAsync($"/api/admin/security-events?ip={claimed}");
        return (await events.Json())["total"]!.GetValue<int>() > 0;
    }
}
