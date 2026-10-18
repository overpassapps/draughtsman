using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Draughtsman.Server.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The admin AI settings API (M3; docs/api.md "## AI settings"): provider, endpoint, model and a write-only API key
/// applied without a restart. Every rule is asserted in both directions - an admin is allowed and an editor, an API
/// token and an anonymous caller are not; the key is used on the wire and yet appears in no response, log line or
/// error body, whether one is set or not; the environment variable wins and the stored key is used when it is absent -
/// so breaking any of them fails here rather than shipping.
/// </summary>
public class AiSettingsEndpointTests
{
    private const string OpenAiYamlTemplate = "ai:\n  provider: openai\n  model: gpt-4o-mini\n  apiKeyEnvironmentVariable: {0}\n";
    private const string OllamaYaml = "ai:\n  provider: ollama\n  model: llama3.1\n  endpoint: \"http://ollama.internal:11434\"\n";
    private const string OpenAiOk = """{"choices":[{"message":{"role":"assistant","content":"OK"}}]}""";
    private const string OllamaOk = """{"message":{"role":"assistant","content":"OK"},"done":true}""";

    private static string NewKeyVariable() => "DRAUGHTSMAN_TEST_AI_KEY_" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task An_admin_can_switch_provider_and_it_applies_without_a_restart()
    {
        // Intentional change (AU-11): Anthropic may use only api.anthropic.com unless the file lists the host, so the proxy below is listed.
        using var factory = new AiSettingsFactory("ai:\n  provider: \"\"\n  allowedEndpointHosts:\n    - proxy.example.com\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();
        // The health detail (AI provider name included) is for signed-in callers; the admin is one.
        HttpClient anonymous = admin;

        JsonNode before = await ReadJson(await anonymous.GetAsync("/api/health"));
        Assert.False(before["ai"]!["enabled"]!.GetValue<bool>());

        HttpResponseMessage response = await admin.PutAsJsonAsync("/api/admin/ai", new { provider = "anthropic", model = "claude-sonnet-5-5", endpoint = "https://proxy.example.com/v1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        Assert.True(body["enabled"]!.GetValue<bool>());
        Assert.Equal("anthropic", body["effective"]!["provider"]!.GetValue<string>());
        Assert.Equal("claude-sonnet-5-5", body["effective"]!["model"]!.GetValue<string>());
        Assert.Equal("anthropic", body["overrides"]!["provider"]!.GetValue<string>());

        // Same server instance, no restart: the public health endpoint (which the SPA gates the AI panel on) flips.
        JsonNode after = await ReadJson(await anonymous.GetAsync("/api/health"));
        Assert.True(after["ai"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("anthropic", after["ai"]!["provider"]!.GetValue<string>());
    }

    [Fact]
    public async Task With_nothing_overridden_the_file_is_used_and_reset_returns_to_it()
    {
        using var factory = new AiSettingsFactory(OllamaYaml);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode initial = await ReadJson(await admin.GetAsync("/api/admin/ai"));
        Assert.Equal("ollama", initial["effective"]!["provider"]!.GetValue<string>());
        Assert.Equal("llama3.1", initial["effective"]!["model"]!.GetValue<string>());
        Assert.Null(initial["overrides"]!["model"]);

        JsonNode changed = await ReadJson(await admin.PutAsJsonAsync("/api/admin/ai", new { provider = "openai", model = "gpt-4o" }));
        Assert.Equal("openai", changed["effective"]!["provider"]!.GetValue<string>());
        // The file's Ollama endpoint must not leak into a provider it was never written for.
        Assert.Equal("https://api.openai.com/v1", changed["effective"]!["endpoint"]!.GetValue<string>().TrimEnd('/'));

        JsonNode reset = await ReadJson(await admin.PostAsync("/api/admin/ai/reset", null));
        Assert.Equal("ollama", reset["effective"]!["provider"]!.GetValue<string>());
        Assert.Equal("llama3.1", reset["effective"]!["model"]!.GetValue<string>());
        Assert.Null(reset["overrides"]!["provider"]);
    }

    [Fact]
    public async Task Only_a_cookie_admin_may_use_the_ai_settings()
    {
        using var factory = new AiSettingsFactory(OllamaYaml);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/ai")).StatusCode);

        await admin.PostAsJsonAsync("/api/users", new { email = "editor@example.com", displayName = "Editor", role = "Editor", password = "correcthorsebattery" });
        HttpClient editor = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync("/api/auth/login", new { email = "editor@example.com", password = "correcthorsebattery" })).StatusCode);

        string secret = (await ReadJson(await admin.PostAsJsonAsync("/api/tokens", new { name = "agent token" })))["token"]!.GetValue<string>();
        HttpClient bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        HttpClient anonymous = factory.CreateClient();

        foreach ((HttpClient client, HttpStatusCode expected) in new[] { (editor, HttpStatusCode.Forbidden), (bearer, HttpStatusCode.Forbidden), (anonymous, HttpStatusCode.Unauthorized) })
        {
            Assert.Equal(expected, (await client.GetAsync("/api/admin/ai")).StatusCode);
            Assert.Equal(expected, (await client.PutAsJsonAsync("/api/admin/ai", new { provider = "openai", model = "gpt-4o" })).StatusCode);
            Assert.Equal(expected, (await client.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-should-not-land" })).StatusCode);
            Assert.Equal(expected, (await client.DeleteAsync("/api/admin/ai/key")).StatusCode);
            Assert.Equal(expected, (await client.PostAsync("/api/admin/ai/reset", null)).StatusCode);
            Assert.Equal(expected, (await client.PostAsync("/api/admin/ai/test", null)).StatusCode);
        }

        // None of the refused writes took effect.
        JsonNode state = await ReadJson(await admin.GetAsync("/api/admin/ai"));
        Assert.Equal("ollama", state["effective"]!["provider"]!.GetValue<string>());
        Assert.False(state["key"]!["storedSet"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_cookie_write_without_the_csrf_header_is_refused()
    {
        using var factory = new AiSettingsFactory(OllamaYaml);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        admin.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-no-csrf" })).StatusCode);
    }

    [Theory]
    [InlineData("ftp://files.example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("https://user:hunter2@api.example.com/v1")]
    [InlineData("https://api.example.com/v1?api-key=hunter2")]
    public async Task An_endpoint_that_is_not_a_plain_http_or_https_url_is_refused_without_echoing_it(string endpoint)
    {
        using var factory = new AiSettingsFactory(yaml: null);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await admin.PutAsJsonAsync("/api/admin/ai", new { provider = "openai", model = "gpt-4o", endpoint });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        JsonNode state = await ReadJson(await admin.GetAsync("/api/admin/ai"));
        Assert.Null(state["overrides"]!["endpoint"]);
    }

    // Intentional change (AU-11): plain http to this machine or a private address is Ollama's case. A keyed provider (openai here) may use only
    // its published host unless the file lists it, and never plain http except to loopback, so those two endpoints are now Ollama's.
    [Theory]
    [InlineData("ollama", "http://localhost:11434")]
    [InlineData("openai", "https://api.openai.com/v1")]
    [InlineData("ollama", "http://192.168.1.20:8000/v1")]
    public async Task A_plain_http_or_https_endpoint_the_policy_allows_is_accepted(string provider, string endpoint)
    {
        using var factory = new AiSettingsFactory(yaml: null);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await admin.PutAsJsonAsync("/api/admin/ai", new { provider, model = "gpt-4o", endpoint });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(endpoint, (await ReadJson(response))["overrides"]!["endpoint"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_unknown_provider_and_a_malformed_key_are_refused()
    {
        using var factory = new AiSettingsFactory(yaml: null);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/ai", new { provider = "skynet", model = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-two words" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-line\r\nbreak" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = new string('k', 513) })).StatusCode);
        Assert.False((await ReadJson(await admin.GetAsync("/api/admin/ai")))["key"]!["storedSet"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "  sk-fine-key  " })).StatusCode);
    }

    /// <summary>The key-never-returned rule, for a key that is set and for one that never was.</summary>
    [Fact]
    public async Task The_key_is_in_no_response_no_log_line_and_no_error_whether_set_or_not()
    {
        const string key = "sk-live-9f8e7d6c5b4a39281706";
        string variable = NewKeyVariable();
        var logs = new CapturingLoggerProvider();
        // The provider answers 401 echoing the key masked and 500 echoing it in full: the worst a provider can do.
        var handler = new ScriptedHandler(request => request.RequestUri!.AbsolutePath.EndsWith("chat/completions", StringComparison.Ordinal)
            ? Reply(HttpStatusCode.Unauthorized, "Incorrect API key provided: " + key[..10] + "****" + key[^4..] + " (" + key + ")")
            : Reply(HttpStatusCode.InternalServerError, "boom " + key));
        using var factory = new AiSettingsFactory(string.Format(OpenAiYamlTemplate, variable), handler, logs);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        var everything = new StringBuilder();
        async Task<HttpResponseMessage> Record(Task<HttpResponseMessage> call)
        {
            HttpResponseMessage response = await call;
            everything.AppendLine(await response.Content.ReadAsStringAsync());
            foreach (var header in response.Headers)
            {
                everything.AppendLine(string.Join(",", header.Value));
            }

            return response;
        }

        // Unset first: nothing to leak, and the answers must still be well-formed.
        JsonNode unset = await ReadJson(await Record(admin.GetAsync("/api/admin/ai")));
        Assert.Equal("none", unset["key"]!["source"]!.GetValue<string>());
        JsonNode unsetTest = await ReadJson(await Record(admin.PostAsync("/api/admin/ai/test", null)));
        Assert.False(unsetTest["ok"]!.GetValue<bool>());
        Assert.Contains("No API key", unsetTest["message"]!.GetValue<string>(), StringComparison.Ordinal);

        // Now set, and exercise every route that could carry it.
        JsonNode set = await ReadJson(await Record(admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = key })));
        Assert.Equal("stored", set["key"]!["source"]!.GetValue<string>());
        Assert.True(set["key"]!["storedSet"]!.GetValue<bool>());
        await Record(admin.GetAsync("/api/admin/ai"));
        await Record(admin.PutAsJsonAsync("/api/admin/ai", new { provider = "openai", model = "gpt-4o-mini" }));
        JsonNode rejected = await ReadJson(await Record(admin.PostAsync("/api/admin/ai/test", null)));
        Assert.False(rejected["ok"]!.GetValue<bool>());
        Assert.Contains("rejected the API key", rejected["message"]!.GetValue<string>(), StringComparison.Ordinal);
        await Record(admin.GetAsync("/api/health"));

        // The normal AI endpoint, whose error detail is the provider exception's own message.
        HttpResponseMessage generate = await Record(admin.PostAsJsonAsync("/api/ai/generate-from-text", new { description = "a login flow", diagramType = "flowchart" }));
        Assert.Equal(HttpStatusCode.BadGateway, generate.StatusCode);

        // A different failure: the provider answering 500 with the key in the body.
        handler.Respond = _ => Reply(HttpStatusCode.InternalServerError, "boom " + key);
        JsonNode serverError = await ReadJson(await Record(admin.PostAsync("/api/admin/ai/test", null)));
        Assert.Contains("server error", serverError["message"]!.GetValue<string>(), StringComparison.Ordinal);
        HttpResponseMessage generate500 = await Record(admin.PostAsJsonAsync("/api/ai/generate-from-text", new { description = "a login flow", diagramType = "flowchart" }));
        Assert.Equal(HttpStatusCode.BadGateway, generate500.StatusCode);

        await Record(admin.DeleteAsync("/api/admin/ai/key"));

        AssertNoSecret(key, everything.ToString(), "response");
        AssertNoSecret(key, logs.Text, "log output");
        // Not even a recognisable slice of it.
        AssertNoSecret(key[..12], everything.ToString(), "response (prefix)");
        AssertNoSecret(key[..12], logs.Text, "log output (prefix)");
        // Sanity: the log capture is live, so its silence about the key means something.
        Assert.Contains("api key set", logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_audit_log_line_names_the_acting_admin_and_the_change_but_no_value()
    {
        const string key = "sk-audit-secret-000111222333";
        var logs = new CapturingLoggerProvider();
        using var factory = new AiSettingsFactory(yaml: null, logs: logs);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = key });
        await admin.PutAsJsonAsync("/api/admin/ai", new { provider = "openai", model = "gpt-4o-secret-model" });

        string audit = logs.Lines.First(line => line.Contains("api key set", StringComparison.Ordinal));
        Assert.Contains("AI settings changed", audit, StringComparison.Ordinal);
        Assert.Matches(@"by user \S+", audit);
        Assert.DoesNotContain("by user unknown", audit, StringComparison.Ordinal);
        AssertNoSecret(key, logs.Text, "log output");
        // Values are not logged either, only what changed.
        Assert.DoesNotContain("gpt-4o-secret-model", logs.Text, StringComparison.Ordinal);
    }

    /// <summary>Precedence, both ways: the environment variable wins over the stored key; with it absent the stored key is what goes on the wire.</summary>
    [Fact]
    public async Task The_environment_variable_wins_and_the_stored_key_is_used_when_it_is_absent()
    {
        const string storedKey = "sk-stored-key-aaaaaaaaaaaaaaaa";
        const string environmentKey = "sk-env-key-bbbbbbbbbbbbbbbbbb";
        string variable = NewKeyVariable();
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.OK, OpenAiOk));
        using var factory = new AiSettingsFactory(string.Format(OpenAiYamlTemplate, variable), handler);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = storedKey });

        try
        {
            // Absent: the stored key is used and the UI is told so.
            JsonNode stored = await ReadJson(await admin.GetAsync("/api/admin/ai"));
            Assert.Equal("stored", stored["key"]!["source"]!.GetValue<string>());
            Assert.False(stored["key"]!["environmentSet"]!.GetValue<bool>());
            Assert.Equal(variable, stored["key"]!["environmentVariable"]!.GetValue<string>());
            JsonNode first = await ReadJson(await admin.PostAsync("/api/admin/ai/test", null));
            Assert.True(first["ok"]!.GetValue<bool>(), first.ToJsonString());
            Assert.Equal("Bearer " + storedKey, handler.Requests.Last().Authorization);

            // Present: it wins, and the UI says it is in effect even though a stored key exists.
            Environment.SetEnvironmentVariable(variable, environmentKey);
            JsonNode env = await ReadJson(await admin.GetAsync("/api/admin/ai"));
            Assert.Equal("environment", env["key"]!["source"]!.GetValue<string>());
            Assert.True(env["key"]!["environmentSet"]!.GetValue<bool>());
            Assert.True(env["key"]!["storedSet"]!.GetValue<bool>());
            JsonNode second = await ReadJson(await admin.PostAsync("/api/admin/ai/test", null));
            Assert.True(second["ok"]!.GetValue<bool>(), second.ToJsonString());
            Assert.Equal("Bearer " + environmentKey, handler.Requests.Last().Authorization);
            AssertNoSecret(environmentKey, env.ToJsonString() + second.ToJsonString(), "response");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Clearing_the_key_and_resetting_both_forget_it()
    {
        string variable = NewKeyVariable();
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.OK, OpenAiOk));
        using var factory = new AiSettingsFactory(string.Format(OpenAiYamlTemplate, variable), handler);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-to-be-cleared-123456" });
        JsonNode cleared = await ReadJson(await admin.DeleteAsync("/api/admin/ai/key"));
        Assert.False(cleared["key"]!["storedSet"]!.GetValue<bool>());
        Assert.Equal("none", cleared["key"]!["source"]!.GetValue<string>());
        int before = handler.Requests.Count;
        Assert.False((await ReadJson(await admin.PostAsync("/api/admin/ai/test", null)))["ok"]!.GetValue<bool>());
        Assert.Equal(before, handler.Requests.Count); // no request goes out without a key

        await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-to-be-reset-123456" });
        JsonNode reset = await ReadJson(await admin.PostAsync("/api/admin/ai/reset", null));
        Assert.False(reset["key"]!["storedSet"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Ollama_works_with_no_key_and_sends_none()
    {
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.OK, OllamaOk));
        using var factory = new AiSettingsFactory(OllamaYaml, handler);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode state = await ReadJson(await admin.GetAsync("/api/admin/ai"));
        Assert.False(state["key"]!["required"]!.GetValue<bool>());
        Assert.Equal("none", state["key"]!["source"]!.GetValue<string>());

        JsonNode result = await ReadJson(await admin.PostAsync("/api/admin/ai/test", null));
        Assert.True(result["ok"]!.GetValue<bool>(), result.ToJsonString());
        HttpRequestMessage sent = handler.Requests.Last().Request;
        Assert.Equal("http://ollama.internal:11434/api/chat", sent.RequestUri!.ToString());
        Assert.Null(sent.Headers.Authorization);

        // Intentional change (AU-11): a key is never stored for, or sent over, plain http to a host that is not this machine, so a proxied
        // Ollama's token needs https (the same host and port). Over https it is sent as a bearer token; hosted providers, by contrast, require one.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "proxy-token-123456" })).StatusCode);
        await admin.PutAsJsonAsync("/api/admin/ai", new { provider = "ollama", model = "llama3.1", endpoint = "https://ollama.internal:11434" });
        await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "proxy-token-123456" });
        await admin.PostAsync("/api/admin/ai/test", null);
        Assert.Equal("Bearer proxy-token-123456", handler.Requests.Last().Authorization);
        await admin.PutAsJsonAsync("/api/admin/ai", new { provider = "openai", model = "gpt-4o" });
        Assert.True((await ReadJson(await admin.GetAsync("/api/admin/ai")))["key"]!["required"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Test_connection_explains_each_failure_in_plain_english()
    {
        string variable = NewKeyVariable();
        var handler = new ScriptedHandler(_ => Reply(HttpStatusCode.NotFound, "no such model"));
        // Intentional change (AU-11): the gateway is a listed host over https, and the endpoint is set before the key, because changing the
        // host forgets a key stored for the old one.
        using var factory = new AiSettingsFactory(string.Format(OpenAiYamlTemplate, variable) + "  allowedEndpointHosts:\n    - llm.internal\n", handler);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await admin.PutAsJsonAsync("/api/admin/ai", new { endpoint = "https://llm.internal:8000/v1" });
        await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = "sk-explain-123456789" });

        Assert.Contains("404", await TestMessage(admin), StringComparison.Ordinal);

        handler.Respond = _ => Reply(HttpStatusCode.TooManyRequests, "slow down");
        Assert.Contains("429", await TestMessage(admin), StringComparison.Ordinal);

        handler.Respond = _ => throw new HttpRequestException("Connection refused");
        string unreachable = await TestMessage(admin);
        Assert.Contains("Could not reach llm.internal:8000", unreachable, StringComparison.Ordinal);

        handler.Respond = _ => Reply(HttpStatusCode.OK, "<html>not json</html>");
        Assert.Contains("not in the format", await TestMessage(admin), StringComparison.Ordinal);

        handler.Respond = _ => Reply(HttpStatusCode.OK, OpenAiOk);
        JsonNode ok = await ReadJson(await admin.PostAsync("/api/admin/ai/test", null));
        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Contains("Connected", ok["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_connection_says_so_when_ai_is_not_configured()
    {
        using var factory = new AiSettingsFactory(yaml: null);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode result = await ReadJson(await admin.PostAsync("/api/admin/ai/test", null));

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Contains("provider", result["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> TestMessage(HttpClient admin)
    {
        JsonNode result = await ReadJson(await admin.PostAsync("/api/admin/ai/test", null));
        Assert.False(result["ok"]!.GetValue<bool>());
        return result["message"]!.GetValue<string>();
    }

    private static void AssertNoSecret(string secret, string text, string where)
    {
        Assert.False(text.Contains(secret, StringComparison.Ordinal), $"The secret leaked into {where}.");
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body.");
    }

    /// <summary>Stands in for every provider's HTTP endpoint: records what was sent (including the key on the wire) and answers as scripted.</summary>
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = respond;

        public ConcurrentQueue<SentRequest> RequestLog { get; } = new();

        public IReadOnlyList<SentRequest> Requests => [.. RequestLog];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? authorization = request.Headers.Authorization is { } auth ? $"{auth.Scheme} {auth.Parameter}" : null;
            RequestLog.Enqueue(new SentRequest(request, authorization));
            return Task.FromResult(Respond(request));
        }
    }

    private sealed record SentRequest(HttpRequestMessage Request, string? Authorization);

    /// <summary>Every log message, formatted, plus any exception's full text, so a leak through either shows up.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public string Text => string.Join('\n', _lines);

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(categoryName, _lines);
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue($"{category} {logLevel}: {formatter(state, exception)} {exception}");
            }
        }
    }

    private sealed class AiSettingsFactory : DraughtsmanServerFactory
    {
        private readonly ScriptedHandler? _handler;
        private readonly CapturingLoggerProvider? _logs;

        public AiSettingsFactory(string? yaml, ScriptedHandler? handler = null, CapturingLoggerProvider? logs = null)
        {
            Directory.CreateDirectory(DataFolder);
            if (yaml is not null)
            {
                File.WriteAllText(Path.Combine(DataFolder, DraughtsmanConfig.FileName), yaml, Encoding.UTF8);
            }

            _handler = handler;
            _logs = logs;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (_logs is not null)
            {
                CapturingLoggerProvider logs = _logs;
                builder.ConfigureLogging(logging => logging.AddProvider(logs));
            }

            if (_handler is not null)
            {
                ScriptedHandler handler = _handler;
                builder.ConfigureTestServices(services =>
                {
                    foreach (string name in new[] { "ollama-ai-provider", "openai-ai-provider", "anthropic-ai-provider", "azure-openai-ai-provider" })
                    {
                        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => handler);
                    }
                });
            }
        }
    }
}
