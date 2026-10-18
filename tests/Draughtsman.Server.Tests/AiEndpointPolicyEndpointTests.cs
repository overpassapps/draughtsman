using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Draughtsman.Server.Ai;
using Draughtsman.Server.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The AI endpoint policy (AU-11 option A, docs/ai-endpoint-policy.md) through the admin AI API, one test per rule and each rule shown
/// both ways: what is refused beside what is not, so removing the rule (or refusing everything) fails a test. The requests either go
/// to a scripted handler (an https endpoint, whose request and key header the test reads) or through the real guarded handler to a loopback
/// test server by way of <see cref="FakeAiNetwork"/>, which is how a name's address, and where it was dialled, are observed.
/// </summary>
public class AiEndpointPolicyEndpointTests
{
    private const string OpenAiOk = """{"choices":[{"message":{"role":"assistant","content":"OK"}}]}""";
    private const string OllamaOk = """{"message":{"role":"assistant","content":"OK"},"done":true}""";

    private static string NewKeyVariable() => "DRAUGHTSMAN_TEST_POLICY_KEY_" + Guid.NewGuid().ToString("N");

    private static string Yaml(string? provider = null, string? endpoint = null, string? keyVariable = null, params string[] allowed)
    {
        var yaml = new StringBuilder("ai:\n");
        // An empty `ai:` is a YAML null, which the config loader does not take for a section; an explicit blank provider is "AI off".
        yaml.Append(provider is null ? "  provider: \"\"\n" : $"  provider: {provider}\n  model: test-model\n");

        if (endpoint is not null)
        {
            yaml.Append($"  endpoint: \"{endpoint}\"\n");
        }

        if (keyVariable is not null)
        {
            yaml.Append($"  apiKeyEnvironmentVariable: {keyVariable}\n");
        }

        if (allowed.Length > 0)
        {
            yaml.Append("  allowedEndpointHosts:\n");
            foreach (string host in allowed)
            {
                yaml.Append($"    - \"{host}\"\n");
            }
        }

        return yaml.ToString();
    }

    private static Task<HttpResponseMessage> Put(HttpClient admin, string? provider, string? endpoint, string model = "test-model")
    {
        return admin.PutAsJsonAsync("/api/admin/ai", new { provider, model, endpoint });
    }

    private static async Task<JsonNode> Json(HttpResponseMessage response)
    {
        return JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("Empty body.");
    }

    private static async Task<JsonNode> State(HttpClient admin)
    {
        return await Json(await admin.GetAsync("/api/admin/ai"));
    }

    private static async Task<JsonArray> Events(HttpClient admin, string type)
    {
        return (await Json(await admin.GetAsync($"/api/admin/security-events?type={type}&limit=200")))["items"]!.AsArray();
    }

    private static async Task<JsonNode> TestConnection(HttpClient admin)
    {
        return await Json(await admin.PostAsync("/api/admin/ai/test", null));
    }

    // ---- Rule: link-local and cloud-metadata addresses are blocked for every provider ----

    [Theory]
    [InlineData("ollama", "http://169.254.169.254/latest/meta-data/")]
    [InlineData("ollama", "http://[::ffff:169.254.169.254]:11434")]
    [InlineData("ollama", "http://[::ffff:a9fe:a9fe]/")]
    [InlineData("ollama", "http://[fd00:ec2::254]/")]
    [InlineData("ollama", "http://[fe80::1]:11434")]
    [InlineData("ollama", "http://2852039166/")]
    [InlineData("openai", "https://169.254.169.254/v1")]
    [InlineData("anthropic", "https://[::ffff:169.254.169.254]/v1")]
    [InlineData("azure-openai", "https://169.254.169.254")]
    public async Task A_metadata_or_link_local_endpoint_is_refused_for_every_provider_even_when_the_file_lists_it(string provider, string endpoint)
    {
        using var factory = new PolicyFactory(Yaml(null, null, null, "169.254.169.254", "2852039166", "fe80::1", "fd00:ec2::254", "*.openai.azure.com"));
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage refused = await Put(admin, provider, endpoint);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        JsonNode problem = await Json(refused);
        Assert.Equal("Endpoint not allowed", problem["title"]!.GetValue<string>());
        Assert.Equal(AiEndpointPolicy.BlockedMessage, problem["detail"]!.GetValue<string>());
        Assert.Null((await State(admin))["overrides"]!["endpoint"]); // nothing was saved
        JsonObject refusal = Assert.Single(await Events(admin, "ai.endpoint_refused"))!.AsObject();
        Assert.Contains("provider " + provider, refusal["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(AuthTestHelpers.DefaultEmail, refusal["actorLabel"]!.GetValue<string>());

        // And the same page still accepts an address that is merely private.
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "ollama", "http://10.0.0.5:11434")).StatusCode);
    }

    // ---- Rule: a hostname is judged by what it resolves to, when it is connected to ----

    [Fact]
    public async Task A_hostname_that_resolves_to_a_blocked_address_is_refused_and_one_that_resolves_to_a_private_address_is_not()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        FakeAiNetwork network = new FakeAiNetwork(server).Answer("ollama.rebind.test", ["169.254.169.254"]);
        using var factory = new PolicyFactory(Yaml("ollama"), network);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "ollama", "http://ollama.rebind.test:11434")).StatusCode); // a name is judged when it is connected to

        JsonNode blocked = await TestConnection(admin);

        Assert.False(blocked["ok"]!.GetValue<bool>());
        Assert.Contains(AiEndpointPolicy.BlockedMessage, blocked["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("refused by the AI endpoint policy", blocked["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(network.Dialled);
        Assert.Empty(server.Requests);

        // The same name answering with a private address is allowed, and is dialled at that address.
        network.Answer("ollama.rebind.test", ["10.0.0.5"]);
        JsonNode allowed = await TestConnection(admin);
        Assert.True(allowed["ok"]!.GetValue<bool>(), allowed.ToJsonString());
        Assert.Equal((IPAddress.Parse("10.0.0.5"), 11434), Assert.Single(network.Dialled));
    }

    // ---- Rule: a keyed provider may use only its published host unless the file lists the host ----

    [Fact]
    public async Task A_keyed_provider_is_refused_a_host_that_is_not_its_own_and_the_admin_page_cannot_widen_the_list()
    {
        using var factory = new PolicyFactory(Yaml());
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage refused = await Put(admin, "openai", "https://evil.example.com/v1");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        string detail = (await Json(refused))["detail"]!.GetValue<string>();
        Assert.Contains("ai.allowedEndpointHosts", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.example.com", detail, StringComparison.Ordinal);
        Assert.Null((await State(admin))["overrides"]!["endpoint"]);

        // The list is the file's: a body that tries to add to it changes nothing.
        HttpResponseMessage smuggled = await admin.PutAsJsonAsync("/api/admin/ai", new { provider = "openai", model = "m", endpoint = "https://evil.example.com/v1", allowedEndpointHosts = new[] { "evil.example.com" } });
        Assert.Equal(HttpStatusCode.BadRequest, smuggled.StatusCode);

        // Each provider's own published host is fine, and only its own.
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "openai", "https://api.openai.com/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "anthropic", "https://api.anthropic.com/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "azure-openai", "https://my-resource.openai.azure.com")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, "anthropic", "https://api.openai.com/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, "openai", "https://api.openai.com.evil.example.com/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, "azure-openai", "https://my-resource.openai.azure.com.evil.example.com")).StatusCode);
        Assert.Equal(5, (await Events(admin, "ai.endpoint_refused")).Count); // every refusal above is on the security log; the three accepted ones are not
    }

    [Fact]
    public async Task A_host_listed_in_the_file_is_accepted_for_a_keyed_provider_and_only_that_host()
    {
        using var factory = new PolicyFactory(Yaml(null, null, null, "llm.corp.example", "*.gw.corp.example"));
        HttpClient admin = await factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "openai", "https://llm.corp.example/v1")).StatusCode);
        Assert.Equal("https://llm.corp.example/v1", (await State(admin))["effective"]!["endpoint"]!.GetValue<string>().TrimEnd('/'));
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "anthropic", "https://llm.corp.example/v1")).StatusCode); // listed for any keyed provider
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "openai", "https://eu.gw.corp.example/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, "openai", "https://gw.corp.example/v1")).StatusCode); // the wildcard is for subdomains
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, "openai", "https://other.corp.example/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, "openai", "https://llm.corp.example.evil.test/v1")).StatusCode);
    }

    [Fact]
    public async Task An_endpoint_in_draughtsman_yaml_that_is_not_allowed_leaves_ai_off_with_the_reason_and_listing_it_turns_ai_on()
    {
        using var refused = new PolicyFactory(Yaml("openai", "https://llm.corp.example/v1"));
        HttpClient admin = await refused.CreateAuthenticatedClient();

        JsonNode off = await State(admin);

        Assert.False(off["enabled"]!.GetValue<bool>());
        Assert.Contains("ai.allowedEndpointHosts", off["problem"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("llm.corp.example", off["problem"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False((await Json(await admin.GetAsync("/api/health")))["ai"]!["enabled"]!.GetValue<bool>());

        using var listed = new PolicyFactory(Yaml("openai", "https://llm.corp.example/v1", null, "llm.corp.example"));
        HttpClient listedAdmin = await listed.CreateAuthenticatedClient();
        Assert.True((await State(listedAdmin))["enabled"]!.GetValue<bool>());
        Assert.True((await Json(await listedAdmin.GetAsync("/api/health")))["ai"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_listed_host_works_end_to_end_through_the_real_handler_and_an_unlisted_one_never_gets_a_request()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OpenAiOk));
        FakeAiNetwork network = new FakeAiNetwork(server).Answer("localhost", ["127.0.0.1"]);
        using var factory = new PolicyFactory(Yaml(null, null, NewKeyVariable(), "localhost"), network);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await Put(admin, "openai", "http://localhost:8080/v1", "gpt-test");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-listed-host-key-123456" })).StatusCode);

        JsonNode ok = await TestConnection(admin);

        Assert.True(ok["ok"]!.GetValue<bool>(), ok.ToJsonString());
        Assert.Equal((IPAddress.Parse("127.0.0.1"), 8080), Assert.Single(network.Dialled));
        string request = Assert.Single(server.Requests);
        Assert.StartsWith("POST /v1/chat/completions", request, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer sk-listed-host-key-123456", request, StringComparison.Ordinal);

        // Without the entry the same page refuses the same endpoint, and nothing is sent to it.
        using var unlisted = new PolicyFactory(Yaml(), network);
        HttpClient other = await unlisted.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(other, "openai", "http://localhost:8080/v1", "gpt-test")).StatusCode);
        Assert.Single(server.Requests);
    }

    // ---- Rule: http with a key is refused unless the host is loopback ----

    [Fact]
    public async Task Plain_http_with_a_key_is_refused_unless_the_host_is_loopback()
    {
        using var factory = new PolicyFactory(Yaml(null, null, null, "llm.corp.example", "localhost", "127.0.0.1", "10.0.0.5"));
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage refused = await Put(admin, "openai", "http://llm.corp.example/v1");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("plain http", (await Json(refused))["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, "anthropic", "http://10.0.0.5:8000/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, "azure-openai", "http://my-resource.openai.azure.com")).StatusCode);

        // https to the same host is fine, and so is http to this machine.
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "openai", "https://llm.corp.example/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "openai", "http://localhost:8080/v1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "openai", "http://127.0.0.1:8080/v1")).StatusCode);
    }

    [Fact]
    public async Task A_key_is_not_stored_for_and_not_sent_over_plain_http_to_a_private_ollama_but_is_over_https()
    {
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.OK, OllamaOk));
        using var factory = new PolicyFactory(Yaml("ollama", "http://ollama.internal:11434"), handler: handler);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        // No key is needed, and none can be stored for a plain-http host.
        Assert.True((await TestConnection(admin))["ok"]!.GetValue<bool>());
        Assert.Null(handler.Requests.Last().Authorization);
        HttpResponseMessage refused = await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "proxy-token-123456" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False((await State(admin))["key"]!["storedSet"]!.GetValue<bool>());

        // Over https (the same host and port) a proxy token is stored and sent.
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "ollama", "https://ollama.internal:11434", "llama3.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "proxy-token-123456" })).StatusCode);
        Assert.True((await TestConnection(admin))["ok"]!.GetValue<bool>());
        Assert.Equal("Bearer proxy-token-123456", handler.Requests.Last().Authorization);

        // Back to plain http on the same host: the key is still stored, but it is not sent, and the page says why.
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "ollama", "http://ollama.internal:11434", "llama3.1")).StatusCode);
        JsonNode downgraded = await State(admin);
        Assert.True(downgraded["key"]!["storedSet"]!.GetValue<bool>());
        Assert.Equal("none", downgraded["key"]!["source"]!.GetValue<string>());
        Assert.Contains("plain http", downgraded["key"]!["withheld"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True((await TestConnection(admin))["ok"]!.GetValue<bool>());
        Assert.Null(handler.Requests.Last().Authorization);
    }

    // ---- Rule: redirects are not followed ----

    [Fact]
    public async Task A_redirect_from_the_endpoint_is_reported_and_not_followed()
    {
        using var target = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        using var redirecting = new LoopbackHttpServer(_ => LoopbackHttpServer.Redirect($"http://127.0.0.1:{target.Port}/elsewhere"));
        using var factory = new PolicyFactory(Yaml("ollama", $"http://127.0.0.1:{redirecting.Port}"), new FakeAiNetwork(redirecting));
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode result = await TestConnection(admin);

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Contains("redirect", result["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("not followed", result["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Single(redirecting.Requests);
        Assert.Empty(target.Requests);
    }

    // ---- Rule: changing the host clears the stored key ----

    [Fact]
    public async Task Changing_the_host_clears_the_stored_key_and_records_who_did_it_while_other_changes_keep_it()
    {
        const string key = "sk-bound-to-openai-0123456789";
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.OK, OpenAiOk));
        using var factory = new PolicyFactory(Yaml("openai", null, NewKeyVariable(), "proxy.corp.example"), handler: handler);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string adminId = (await Json(await admin.GetAsync("/api/auth/status")))["user"]!["id"]!.GetValue<string>();
        await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = key });
        Assert.True((await TestConnection(admin))["ok"]!.GetValue<bool>());
        Assert.Equal("Bearer " + key, handler.Requests.Last().Authorization);

        // A change that leaves the host alone keeps the key and logs no host change.
        JsonNode sameHost = await Json(await Put(admin, "openai", null, "gpt-4o"));
        Assert.True(sameHost["key"]!["storedSet"]!.GetValue<bool>());
        Assert.Empty(await Events(admin, "ai.endpoint_host_changed"));

        // A new host: the key is gone, the change is on the security log with the acting admin, and nothing is sent without a key.
        JsonNode moved = await Json(await Put(admin, "openai", "https://proxy.corp.example/v1", "gpt-4o"));
        Assert.False(moved["key"]!["storedSet"]!.GetValue<bool>());
        Assert.Equal("none", moved["key"]!["source"]!.GetValue<string>());
        JsonObject changed = Assert.Single(await Events(admin, "ai.endpoint_host_changed"))!.AsObject();
        Assert.Equal(adminId, changed["actorUserId"]!.GetValue<string>());
        Assert.Equal("from api.openai.com:443 to proxy.corp.example:443; stored key cleared", changed["detail"]!.GetValue<string>());
        int before = handler.Requests.Count;
        JsonNode noKey = await TestConnection(admin);
        Assert.False(noKey["ok"]!.GetValue<bool>());
        Assert.Contains("No API key", noKey["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(before, handler.Requests.Count);
        Assert.DoesNotContain("protectedApiKey", File.ReadAllText(Path.Combine(factory.DataFolder, "ai", "settings.json")), StringComparison.Ordinal); // gone from disk too
        Assert.DoesNotContain(key, (await Json(await admin.GetAsync("/api/admin/security-events?limit=200"))).ToJsonString(), StringComparison.Ordinal);

        // Entering it again works for the new host, and so does a change of port, which is a different host as far as the key is concerned.
        await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = key + "-for-proxy" });
        Assert.True((await TestConnection(admin))["ok"]!.GetValue<bool>());
        Assert.Equal("Bearer " + key + "-for-proxy", handler.Requests.Last().Authorization);
        Assert.Equal("https://proxy.corp.example/v1", handler.Requests.Last().Request.RequestUri!.GetLeftPart(UriPartial.Path)[..^"/chat/completions".Length]);
        JsonNode otherPort = await Json(await Put(admin, "openai", "https://proxy.corp.example:8443/v1", "gpt-4o"));
        Assert.False(otherPort["key"]!["storedSet"]!.GetValue<bool>());
        Assert.Equal(2, (await Events(admin, "ai.endpoint_host_changed")).Count);
    }

    [Fact]
    public async Task A_key_entered_before_any_endpoint_stays_only_for_the_providers_own_default_host()
    {
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.OK, OpenAiOk));
        using var factory = new PolicyFactory(Yaml(), handler: handler);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-entered-first-0123456789" });

        // Choosing the provider (its default host) is the ordinary order of things: the key stays.
        JsonNode chosen = await Json(await Put(admin, "openai", null));
        Assert.True(chosen["key"]!["storedSet"]!.GetValue<bool>());
        Assert.Equal("from none to api.openai.com:443; no stored key was affected", Assert.Single(await Events(admin, "ai.endpoint_host_changed"))!["detail"]!.GetValue<string>());

        // A key held while there was no endpoint at all is not carried to a host the admin names afterwards (Azure has no default host).
        await Put(admin, "azure-openai", null);
        await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-entered-while-unconfigured-0123" });
        JsonNode named = await Json(await Put(admin, "azure-openai", "https://my-resource.openai.azure.com"));
        Assert.False(named["key"]!["storedSet"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Switching_provider_to_one_with_another_host_and_resetting_to_the_file_both_clear_the_key_and_are_logged()
    {
        using var factory = new PolicyFactory(Yaml("openai", null, NewKeyVariable()), handler: new ScriptedHandler(_ => Reply(HttpStatusCode.OK, OpenAiOk)));
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-for-openai-0123456789" });

        JsonNode switched = await Json(await Put(admin, "anthropic", null, "claude-sonnet-5-5"));

        Assert.False(switched["key"]!["storedSet"]!.GetValue<bool>());
        Assert.Equal("from api.openai.com:443 to api.anthropic.com:443; stored key cleared", Assert.Single(await Events(admin, "ai.endpoint_host_changed"))!["detail"]!.GetValue<string>());

        await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-for-anthropic-0123456789" });
        JsonNode reset = await Json(await admin.PostAsync("/api/admin/ai/reset", null));
        Assert.False(reset["key"]!["storedSet"]!.GetValue<bool>());
        JsonArray events = await Events(admin, "ai.endpoint_host_changed");
        Assert.Equal(2, events.Count);
        Assert.Contains("from api.anthropic.com:443 to api.openai.com:443; stored key cleared", events.Select(e => e!["detail"]!.GetValue<string>()));
    }

    // ---- Rule: the environment key is not sent to a host other than the file's or the provider's own ----

    [Fact]
    public async Task The_environment_key_goes_only_to_the_host_in_the_file_or_the_providers_own_and_a_stored_key_is_used_for_any_other()
    {
        const string environmentKey = "sk-deployment-secret-0123456789";
        string variable = NewKeyVariable();
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.OK, OpenAiOk));
        using var factory = new PolicyFactory(Yaml("openai", null, variable, "proxy.corp.example"), handler: handler);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        Environment.SetEnvironmentVariable(variable, environmentKey);
        try
        {
            // At the provider's own host the environment key is used, and the page says so.
            JsonNode own = await State(admin);
            Assert.Equal("environment", own["key"]!["source"]!.GetValue<string>());
            Assert.Null(own["key"]!["withheld"]);
            Assert.True((await TestConnection(admin))["ok"]!.GetValue<bool>());
            Assert.Equal("Bearer " + environmentKey, handler.Requests.Last().Authorization);

            // Another host (one the file lists, so the endpoint itself is allowed): the environment key is not sent.
            JsonNode moved = await Json(await Put(admin, "openai", "https://proxy.corp.example/v1"));
            Assert.Equal("none", moved["key"]!["source"]!.GetValue<string>());
            Assert.True(moved["key"]!["environmentSet"]!.GetValue<bool>());
            Assert.Contains(variable, moved["key"]!["withheld"]!.GetValue<string>(), StringComparison.Ordinal);
            int before = handler.Requests.Count;
            JsonNode refused = await TestConnection(admin);
            Assert.False(refused["ok"]!.GetValue<bool>());
            Assert.Contains("another host", refused["message"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.DoesNotContain(environmentKey, refused.ToJsonString(), StringComparison.Ordinal);
            Assert.Equal(before, handler.Requests.Count); // not even a request without it

            // A key entered here for that host is the one used there; the environment key still is not.
            await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-entered-for-the-proxy-0123" });
            Assert.True((await TestConnection(admin))["ok"]!.GetValue<bool>());
            Assert.Equal("Bearer sk-entered-for-the-proxy-0123", handler.Requests.Last().Authorization);
            Assert.All(handler.Requests.Skip(1), sent => Assert.NotEqual("Bearer " + environmentKey, sent.Authorization));
            JsonNode both = await State(admin);
            Assert.Equal("stored", both["key"]!["source"]!.GetValue<string>());
            Assert.NotNull(both["key"]!["withheld"]);

            // Back to the provider's own host: the environment key again.
            await Put(admin, "openai", null);
            Assert.True((await TestConnection(admin))["ok"]!.GetValue<bool>());
            Assert.Equal("Bearer " + environmentKey, handler.Requests.Last().Authorization);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task The_environment_key_is_sent_to_the_host_the_file_names()
    {
        const string environmentKey = "sk-deployment-secret-9876543210";
        string variable = NewKeyVariable();
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.OK, OpenAiOk));
        using var factory = new PolicyFactory(Yaml("openai", "https://proxy.corp.example/v1", variable, "proxy.corp.example", "other.corp.example"), handler: handler);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        Environment.SetEnvironmentVariable(variable, environmentKey);
        try
        {
            Assert.True((await TestConnection(admin))["ok"]!.GetValue<bool>());
            Assert.Equal("Bearer " + environmentKey, handler.Requests.Last().Authorization);
            Assert.Equal("proxy.corp.example", handler.Requests.Last().Request.RequestUri!.Host);

            // The file's host is the only one: a sibling the file also lists does not get it.
            await Put(admin, "openai", "https://other.corp.example/v1");
            int before = handler.Requests.Count;
            Assert.False((await TestConnection(admin))["ok"]!.GetValue<bool>());
            Assert.Equal(before, handler.Requests.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task The_environment_key_of_another_provider_is_never_sent_to_an_azure_resource_the_file_did_not_name()
    {
        const string environmentKey = "azure-deployment-secret-0123456789";
        string variable = NewKeyVariable();
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.OK, OpenAiOk));
        using var factory = new PolicyFactory(Yaml("azure-openai", "https://our-resource.openai.azure.com", variable), handler: handler);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        Environment.SetEnvironmentVariable(variable, environmentKey);
        try
        {
            Assert.True((await TestConnection(admin))["ok"]!.GetValue<bool>());
            Assert.Equal(environmentKey, handler.Requests.Last().Request.Headers.GetValues("api-key").Single());

            // Any *.openai.azure.com host passes the endpoint rule, so somebody else's Azure resource would otherwise receive the key.
            Assert.Equal(HttpStatusCode.OK, (await Put(admin, "azure-openai", "https://attackers-resource.openai.azure.com")).StatusCode);
            int before = handler.Requests.Count;
            Assert.False((await TestConnection(admin))["ok"]!.GetValue<bool>());
            Assert.Equal(before, handler.Requests.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    // ---- Rule: ollama may use loopback and private addresses ----

    [Fact]
    public async Task Ollama_on_a_loopback_or_private_address_still_works_and_one_on_a_public_address_does_not()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        FakeAiNetwork network = new FakeAiNetwork(server)
            .Answer("ollama.lan.test", ["192.168.1.9"])
            .Answer("ollama.tailnet.test", ["100.64.3.4"])
            .Answer("ollama.public.test", ["203.0.113.7"]);
        using var factory = new PolicyFactory(Yaml("ollama"), network);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        foreach ((string endpoint, string address, int port) in new[]
        {
            ("http://10.1.2.3:11434", "10.1.2.3", 11434),
            ("http://192.168.1.20:8000", "192.168.1.20", 8000),
            ("http://127.0.0.1:11434", "127.0.0.1", 11434),
            ("http://[::1]:11434", "::1", 11434),
            ("http://ollama.lan.test:11434", "192.168.1.9", 11434),
            ("http://ollama.tailnet.test:11434", "100.64.3.4", 11434),
        })
        {
            Assert.Equal(HttpStatusCode.OK, (await Put(admin, "ollama", endpoint, "llama3.1")).StatusCode);
            JsonNode result = await TestConnection(admin);
            Assert.True(result["ok"]!.GetValue<bool>(), endpoint + ": " + result.ToJsonString());
            Assert.Equal((IPAddress.Parse(address), port), network.Dialled.Last());
        }

        Assert.Equal(6, server.Requests.Count);
        Assert.All(server.Requests, request => Assert.DoesNotContain("Authorization:", request, StringComparison.OrdinalIgnoreCase));

        // Public: refused as a literal when it is saved, and as a name when it is connected to.
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(admin, "ollama", "http://203.0.113.7:11434", "llama3.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "ollama", "http://ollama.public.test:11434", "llama3.1")).StatusCode);
        JsonNode publicName = await TestConnection(admin);
        Assert.False(publicName["ok"]!.GetValue<bool>());
        Assert.Contains(AiEndpointPolicy.OllamaPublicMessage, publicName["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(6, server.Requests.Count);
    }

    [Fact]
    public async Task A_public_ollama_host_works_once_the_file_lists_it()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackHttpServer.Ok(OllamaOk));
        FakeAiNetwork network = new FakeAiNetwork(server).Answer("ollama.public.test", ["203.0.113.7"]);
        using var factory = new PolicyFactory(Yaml("ollama", null, null, "ollama.public.test"), network);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.OK, (await Put(admin, "ollama", "http://ollama.public.test:11434", "llama3.1")).StatusCode);

        JsonNode result = await TestConnection(admin);

        Assert.True(result["ok"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal(IPAddress.Parse("203.0.113.7"), Assert.Single(network.Dialled).Address);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>Stands in for every provider's HTTP endpoint (https ones the test cannot reach for real): records the request and the key on it.</summary>
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<SentRequest> _requests = new();

        public IReadOnlyList<SentRequest> Requests => [.. _requests];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? authorization = request.Headers.Authorization is { } auth ? $"{auth.Scheme} {auth.Parameter}" : null;
            _requests.Enqueue(new SentRequest(request, authorization));
            return Task.FromResult(respond(request));
        }
    }

    private sealed record SentRequest(HttpRequestMessage Request, string? Authorization);

    private sealed class PolicyFactory : DraughtsmanServerFactory
    {
        private readonly IAiEndpointNetwork? _network;
        private readonly ScriptedHandler? _handler;

        public PolicyFactory(string yaml, IAiEndpointNetwork? network = null, ScriptedHandler? handler = null)
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(Path.Combine(DataFolder, DraughtsmanConfig.FileName), yaml, Encoding.UTF8);
            _network = network;
            _handler = handler;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                if (_network is not null)
                {
                    services.AddSingleton(_network);
                }

                if (_handler is not null)
                {
                    ScriptedHandler handler = _handler;
                    foreach (string name in AiProviders.HttpClientNames)
                    {
                        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);
                    }
                }
            });
        }
    }
}
