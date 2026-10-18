using System.Net;
using System.Net.Http.Headers;
using Draughtsman.Server.Ai;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The AI endpoint policy where it is enforced: the HTTP client's connect step, through a real <see cref="HttpClient"/> over the real
/// handler the providers use (<see cref="AiConnectGuard.CreateHandler"/>). Names resolve through a fake resolver and connect to one
/// loopback test server, so the tests show what was looked up, what was dialled and at which address, and what was refused, with a real
/// HTTP exchange and no DNS lookup or outside packet. Each rule is shown both ways, so removing it fails a test.
/// </summary>
public class AiConnectGuardTests
{
    private const string OllamaOk = """{"message":{"role":"assistant","content":"OK"},"done":true}""";

    private static HttpClient ClientFor(string provider, FakeAiNetwork network, params string[] allowedHosts)
    {
        return new HttpClient(AiConnectGuard.CreateHandler(provider, allowedHosts, network), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
    }

    private static async Task<AiEndpointRefusedException> Refused(Task<HttpResponseMessage> send)
    {
        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => send);
        return AiEndpointRefusedException.Find(error) ?? throw new Xunit.Sdk.XunitException("The request failed, but not because the policy refused it: " + error);
    }

    [Fact]
    public async Task A_name_that_resolves_to_a_metadata_address_is_refused_and_never_dialled()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        FakeAiNetwork network = new FakeAiNetwork(server).Answer("metadata.rebind.test", ["169.254.169.254"]);
        using HttpClient http = ClientFor(AiProviders.Ollama, network);

        AiEndpointRefusedException refusal = await Refused(http.GetAsync("http://metadata.rebind.test:11434/api/chat"));

        Assert.Equal(AiEndpointPolicy.BlockedMessage, refusal.Message);
        Assert.Equal(["metadata.rebind.test"], network.Resolved);
        Assert.Empty(network.Dialled);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:a9fe:a9fe")]
    [InlineData("fd00:ec2::254")]
    [InlineData("fe80::1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("100.100.100.200")]
    [InlineData("0.0.0.0")]
    public async Task A_name_that_resolves_to_any_blocked_form_is_refused_even_when_the_host_is_listed(string address)
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        FakeAiNetwork network = new FakeAiNetwork(server).Answer("gateway.corp.example", [address]);

        foreach (string provider in AiProviders.All)
        {
            using HttpClient http = ClientFor(provider, network, "gateway.corp.example");
            string scheme = provider == AiProviders.Ollama ? "http" : "https";

            await Refused(http.GetAsync($"{scheme}://gateway.corp.example/v1/chat"));
        }

        Assert.Empty(network.Dialled);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_literal_metadata_endpoint_is_refused_without_a_lookup_or_a_dial()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        var network = new FakeAiNetwork(server);
        using HttpClient http = ClientFor(AiProviders.Ollama, network);

        foreach (string url in new[] { "http://169.254.169.254/latest/meta-data/", "http://[::ffff:169.254.169.254]/", "http://[fd00:ec2::254]/", "http://2852039166/" })
        {
            await Refused(http.GetAsync(url));
        }

        Assert.Empty(network.Resolved);
        Assert.Empty(network.Dialled);
    }

    [Fact]
    public async Task A_name_with_one_blocked_address_among_good_ones_is_refused_whole()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        FakeAiNetwork network = new FakeAiNetwork(server).Answer("mixed.rebind.test", ["10.0.0.5", "169.254.169.254"]);
        using HttpClient http = ClientFor(AiProviders.Ollama, network);

        await Refused(http.GetAsync("http://mixed.rebind.test:11434/"));

        Assert.Empty(network.Dialled);
    }

    [Fact]
    public async Task An_allowed_private_name_is_dialled_at_the_address_that_was_checked_and_looked_up_once()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        FakeAiNetwork network = new FakeAiNetwork(server).Answer("ollama.internal.test", ["10.0.0.5"]);
        using HttpClient http = ClientFor(AiProviders.Ollama, network);

        using HttpResponseMessage response = await http.GetAsync("http://ollama.internal.test:11434/api/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["ollama.internal.test"], network.Resolved);
        (IPAddress address, int port) = Assert.Single(network.Dialled);
        Assert.Equal(IPAddress.Parse("10.0.0.5"), address);
        Assert.Equal(11434, port);
        Assert.Single(server.Requests);
    }

    /// <summary>
    /// The rebinding case: a name that answers with a private address and then with the metadata address. The first connection must use the
    /// answer that was checked (never a second lookup), and the next connection is judged afresh and refused.
    /// </summary>
    [Fact]
    public async Task DNS_rebinding_between_connections_is_judged_again_and_never_between_the_check_and_the_connect()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        FakeAiNetwork network = new FakeAiNetwork(server).Answer("rebind.attacker.test", ["10.0.0.5"], ["169.254.169.254"]);
        using HttpClient http = ClientFor(AiProviders.Ollama, network);

        using (HttpResponseMessage first = await http.GetAsync("http://rebind.attacker.test:11434/"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        Assert.Equal(["rebind.attacker.test"], network.Resolved); // one lookup for the first connection: nothing was resolved a second time to connect
        Assert.Equal(IPAddress.Parse("10.0.0.5"), Assert.Single(network.Dialled).Address);

        // The server closes every connection, so this one is a new connection, and it is looked up and judged again.
        await Refused(http.GetAsync("http://rebind.attacker.test:11434/"));
        Assert.Equal(2, network.Resolved.Count);
        Assert.Single(network.Dialled); // still only the first
    }

    [Fact]
    public async Task An_unlisted_ollama_name_that_resolves_to_a_public_address_is_refused_and_a_listed_one_is_not()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        FakeAiNetwork network = new FakeAiNetwork(server).Answer("ollama.example.test", ["203.0.113.7"]);

        using (HttpClient unlisted = ClientFor(AiProviders.Ollama, network))
        {
            AiEndpointRefusedException refusal = await Refused(unlisted.GetAsync("http://ollama.example.test:11434/"));
            Assert.Equal(AiEndpointPolicy.OllamaPublicMessage, refusal.Message);
        }

        Assert.Empty(network.Dialled);

        using HttpClient listed = ClientFor(AiProviders.Ollama, network, "ollama.example.test");
        using HttpResponseMessage response = await listed.GetAsync("http://ollama.example.test:11434/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(IPAddress.Parse("203.0.113.7"), Assert.Single(network.Dialled).Address);
    }

    [Fact]
    public async Task A_keyed_provider_is_refused_a_host_that_is_neither_published_nor_listed_before_any_lookup()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        FakeAiNetwork network = new FakeAiNetwork(server).Answer("evil.example.test", ["203.0.113.7"]);
        using HttpClient http = ClientFor(AiProviders.OpenAi, network);

        AiEndpointRefusedException refusal = await Refused(http.PostAsync("https://evil.example.test/v1/chat/completions", new StringContent("{}")));

        Assert.Contains("ai.allowedEndpointHosts", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(network.Resolved);
        Assert.Empty(network.Dialled);
    }

    [Fact]
    public async Task A_listed_localhost_may_carry_a_key_over_http_and_a_localhost_that_resolves_elsewhere_may_not()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        FakeAiNetwork network = new FakeAiNetwork(server).Answer("localhost", ["127.0.0.1", "::1"]).Answer("gateway.localhost", ["10.0.0.5"]);
        using HttpClient http = ClientFor(AiProviders.OpenAi, network, "localhost", "gateway.localhost");

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:8080/v1/chat/completions") { Content = new StringContent("{}") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sk-never-leaves-this-machine");
        using HttpResponseMessage ok = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(IPAddress.Parse("127.0.0.1"), network.Dialled[0].Address);

        // A name that claims to be this machine but is not: the key must not go over plain http to what it resolves to.
        await Refused(http.PostAsync("http://gateway.localhost:8080/v1/chat/completions", new StringContent("{}")));
        Assert.Single(network.Dialled);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_a_key_header_is_never_carried_to_a_second_host()
    {
        using var target = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        string away = $"http://127.0.0.1:{target.Port}/stolen";
        using var first = new LoopbackHttpServer(_ => LoopbackHttpServer.Redirect(away));
        using var http = new HttpClient(AiConnectGuard.CreateHandler(AiProviders.Ollama, [], new SystemAiEndpointNetwork()), disposeHandler: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{first.Port}/start");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sk-must-not-be-forwarded");

        using HttpResponseMessage response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(away, response.Headers.Location?.ToString());
        Assert.Single(first.Requests);
        Assert.Empty(target.Requests); // the second place was never asked, so nothing (the key included) could have reached it

        // The control: the framework's own default handler does follow, so this test is able to see a redirect being followed.
        using var following = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true }, disposeHandler: true);
        using HttpResponseMessage followed = await following.GetAsync($"http://127.0.0.1:{first.Port}/start");
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
        Assert.Single(target.Requests);
    }

    [Fact]
    public async Task A_redirect_to_a_metadata_address_is_a_plain_answer_not_a_request()
    {
        using var first = new LoopbackHttpServer(_ => LoopbackHttpServer.Redirect("http://169.254.169.254/latest/meta-data/"));
        var network = new FakeAiNetwork(first);
        using var http = new HttpClient(AiConnectGuard.CreateHandler(AiProviders.Ollama, [], network), disposeHandler: true);

        using HttpResponseMessage response = await http.GetAsync("http://127.0.0.1:" + first.Port + "/");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(first.Requests);
        Assert.Single(network.Dialled); // only the first, to the address that was named; the redirect's address was never dialled
        Assert.Equal(IPAddress.Loopback, network.Dialled[0].Address);
    }

    [Fact]
    public async Task Through_a_proxy_the_target_name_is_still_judged_and_the_proxy_is_what_is_dialled()
    {
        using var proxy = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        FakeAiNetwork network = new FakeAiNetwork(proxy)
            .Answer("proxy.corp.test", ["10.0.0.9"])
            .Answer("good.internal.test", ["10.0.0.5"])
            .Answer("metadata.rebind.test", ["169.254.169.254"]);
        SocketsHttpHandler handler = AiConnectGuard.CreateHandler(AiProviders.Ollama, [], network);
        handler.UseProxy = true;
        handler.Proxy = new WebProxy("http://proxy.corp.test:3128");
        using var http = new HttpClient(handler, disposeHandler: true);

        await Refused(http.GetAsync("http://metadata.rebind.test:11434/"));
        Assert.Empty(network.Dialled); // the target was judged, so neither it nor the proxy was dialled

        using HttpResponseMessage response = await http.GetAsync("http://good.internal.test:11434/api/chat");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        (IPAddress address, int port) = Assert.Single(network.Dialled);
        Assert.Equal(IPAddress.Parse("10.0.0.9"), address); // the proxy
        Assert.Equal(3128, port);
        Assert.StartsWith("GET http://good.internal.test:11434/api/chat", Assert.Single(proxy.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_an_ordinary_connection_failure_not_a_policy_refusal()
    {
        var network = new FakeAiNetwork();
        using HttpClient http = ClientFor(AiProviders.Ollama, network);

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://nobody.example.test:11434/"));

        Assert.Null(AiEndpointRefusedException.Find(error));
        Assert.Empty(network.Dialled);
    }
}
