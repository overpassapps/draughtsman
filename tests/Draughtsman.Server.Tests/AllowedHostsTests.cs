using System.Net;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Hardening;

namespace Draughtsman.Server.Tests;

/// <summary>
/// AU-19: the server no longer answers to any Host header by default. The default follows how it is exposed (loopback
/// only, or anything), a configured list wins, and both the refusal and the acceptance are checked through the pipeline.
/// </summary>
public class AllowedHostsTests
{
    [Fact]
    public void A_loopback_install_answers_to_loopback_names_only()
    {
        string[] hosts = HostPolicy.Resolve(new ServerConfig(), aspNetCoreUrls: null);

        Assert.Equal(["localhost", "127.0.0.1", "[::1]"], hosts);
    }

    [Theory]
    [InlineData("0.0.0.0", null)]
    [InlineData("192.168.1.20", null)]
    [InlineData("127.0.0.1", "http://0.0.0.0:5180")]
    [InlineData("127.0.0.1", "http://+:5180")]
    [InlineData("127.0.0.1", "http://localhost:5180;http://*:5181")]
    public void Anything_reachable_from_elsewhere_answers_to_any_host(string host, string? urls)
    {
        Assert.Equal([HostPolicy.Any], HostPolicy.Resolve(new ServerConfig { Host = host }, urls));
    }

    [Theory]
    [InlineData("http://localhost:5180")]
    [InlineData("http://127.0.0.1:5180;http://[::1]:5180")]
    public void Urls_that_are_all_loopback_keep_the_loopback_default(string urls)
    {
        Assert.Equal(["localhost", "127.0.0.1", "[::1]"], HostPolicy.Resolve(new ServerConfig { Host = "0.0.0.0" }, urls));
    }

    [Fact]
    public void A_trusted_proxy_means_the_public_name_is_not_ours_to_guess()
    {
        Assert.Equal([HostPolicy.Any], HostPolicy.Resolve(new ServerConfig { KnownProxies = ["10.0.0.0/8"] }, aspNetCoreUrls: null));
    }

    [Fact]
    public void A_configured_list_wins_over_every_default()
    {
        var server = new ServerConfig { Host = "0.0.0.0", KnownProxies = ["10.0.0.1"], AllowedHosts = [" draughtsman.example.com ", "*.internal.example"] };

        Assert.Equal(["draughtsman.example.com", "*.internal.example"], HostPolicy.Resolve(server, aspNetCoreUrls: null));
    }

    [Fact]
    public async Task The_default_install_refuses_a_foreign_host_header_and_serves_localhost()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage local = await client.GetAsync("/api/health");
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        rebound.Headers.Host = "attacker.example";
        HttpResponseMessage foreign = await client.SendAsync(rebound);

        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
    }

    [Fact]
    public async Task A_configured_name_is_served_and_the_ones_not_listed_are_refused()
    {
        using var factory = new ConfiguredServerFactory("server:\n  allowedHosts:\n    - draughtsman.example.com\n    - \"*.lan.example\"\n");
        HttpClient client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await Get(client, "draughtsman.example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get(client, "box.lan.example:5180")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, "localhost")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(client, "attacker.example")).StatusCode);
    }

    [Fact]
    public async Task With_a_proxy_configured_any_host_is_served()
    {
        using var factory = new ConfiguredServerFactory("server:\n  knownProxies:\n    - 10.0.0.0/8\n");
        HttpClient client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await Get(client, "whatever.example")).StatusCode);
    }

    private static Task<HttpResponseMessage> Get(HttpClient client, string host)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = host;
        return client.SendAsync(request);
    }
}
