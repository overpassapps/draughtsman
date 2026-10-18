using System.Net;
using System.Text;
using System.Text.Json;
using Draughtsman.Ai.Anthropic;

namespace Draughtsman.Ai.Tests;

public class AnthropicModelProviderTests
{
    private const string KeyVariable = "DRAUGHTSMAN_ANTHROPIC_TEST_KEY";
    private const string Key = "sk-ant-" + "secret-value";

    private static AnthropicModelProvider CreateProvider(HttpClient http, string? key)
    {
        return new AnthropicModelProvider(http, "claude-3-5-sonnet-latest", KeyVariable, _ => key);
    }

    [Fact]
    public async Task CompleteAsync_sends_the_system_prompt_as_a_top_level_field_and_returns_the_reply()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new FakeHandler(async request =>
        {
            captured = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"content":[{"type":"text","text":"draughtsman 1\ntype flowchart"}]}""");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        var provider = CreateProvider(http, Key);

        string reply = await provider.CompleteAsync("system prompt text", [new ModelMessage(ModelRole.User, "make me a diagram")]);

        Assert.Equal("draughtsman 1\ntype flowchart", reply);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", captured.RequestUri!.ToString());

        // The Messages API takes the key in x-api-key (not a bearer token) and requires the version header.
        Assert.Equal(Key, Assert.Single(captured.Headers.GetValues("x-api-key")));
        Assert.Equal("2023-06-01", Assert.Single(captured.Headers.GetValues("anthropic-version")));
        Assert.Null(captured.Headers.Authorization);

        using JsonDocument body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("claude-3-5-sonnet-latest", body.RootElement.GetProperty("model").GetString());
        // max_tokens is required by the Messages API; assert it is present and positive rather than pinned to a
        // number, since the exact ceiling is a provider policy rather than part of the wire contract.
        Assert.True(body.RootElement.GetProperty("max_tokens").GetInt32() > 0);
        // The system prompt is top-level, never one of the messages (there is no system role in this API).
        Assert.Equal("system prompt text", body.RootElement.GetProperty("system").GetString());
        JsonElement[] messages = [.. body.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("make me a diagram", messages[0].GetProperty("content").GetString());
        Assert.DoesNotContain(messages, message => message.GetProperty("role").GetString() == "system");
    }

    [Fact]
    public async Task CompleteAsync_sends_an_assistant_turn_with_the_assistant_role()
    {
        string? capturedBody = null;
        var handler = new FakeHandler(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"content":[{"type":"text","text":"ok"}]}""");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        var provider = CreateProvider(http, Key);

        await provider.CompleteAsync("system", [new ModelMessage(ModelRole.Assistant, "earlier reply")]);

        using JsonDocument body = JsonDocument.Parse(capturedBody!);
        JsonElement[] messages = [.. body.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal("assistant", messages[0].GetProperty("role").GetString());
        Assert.Equal("earlier reply", messages[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompleteAsync_joins_every_text_block_and_ignores_non_text_blocks()
    {
        var handler = new FakeHandler(_ => Task.FromResult(JsonResponse(
            """{"content":[{"type":"text","text":"first "},{"type":"thinking","thinking":"hidden"},{"type":"text","text":"second"}]}""")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        var provider = CreateProvider(http, Key);

        string reply = await provider.CompleteAsync("system", []);

        Assert.Equal("first second", reply);
    }

    [Fact]
    public async Task CompleteAsync_reports_a_rejected_key_by_status_only_and_drops_the_providers_own_text()
    {
        // Providers commonly echo the key back masked ("...sk-test-****"), which is still part of the key: an
        // authentication failure must not carry their body at all.
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("invalid x-api-key: " + Key[..8] + "****", Encoding.UTF8, "text/plain"),
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        var provider = CreateProvider(http, Key);

        ModelProviderException exception = await Assert.ThrowsAsync<ModelProviderException>(
            () => provider.CompleteAsync("system", []));

        Assert.True(exception.Reachable);
        Assert.Equal(401, exception.StatusCode);
        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.Contains("rejected", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("invalid x-api-key", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key[..8], exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_keeps_the_body_of_another_failure_but_redacts_the_key_if_it_was_echoed()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("model not found; request was authenticated with " + Key, Encoding.UTF8, "text/plain"),
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        var provider = CreateProvider(http, Key);

        ModelProviderException exception = await Assert.ThrowsAsync<ModelProviderException>(
            () => provider.CompleteAsync("system", []));

        Assert.True(exception.Reachable);
        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("model not found", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_throws_an_unreachable_ModelProviderException_when_the_connection_fails()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("Connection refused"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        var provider = CreateProvider(http, Key);

        ModelProviderException exception = await Assert.ThrowsAsync<ModelProviderException>(
            () => provider.CompleteAsync("system", []));

        // Two-sided against the status test: a connection failure must report Reachable=false, not just "some
        // ModelProviderException", or a caller could never tell "endpoint unreachable" from "endpoint said no" apart.
        Assert.False(exception.Reachable);
    }

    [Fact]
    public async Task CompleteAsync_throws_an_unreachable_ModelProviderException_when_the_api_key_is_missing()
    {
        bool called = false;
        var handler = new FakeHandler(_ =>
        {
            called = true;
            return Task.FromResult(JsonResponse("""{"content":[{"type":"text","text":"ok"}]}"""));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        var provider = CreateProvider(http, key: null);

        ModelProviderException exception = await Assert.ThrowsAsync<ModelProviderException>(
            () => provider.CompleteAsync("system", []));

        Assert.False(exception.Reachable);
        Assert.Contains(KeyVariable, exception.Message, StringComparison.Ordinal);
        // No request leaves the process when there is no key to authenticate it.
        Assert.False(called);
    }

    [Fact]
    public async Task CompleteAsync_rejects_an_empty_completion()
    {
        var handler = new FakeHandler(_ => Task.FromResult(JsonResponse("""{"content":[{"type":"text","text":"   "}]}""")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com/v1/") };
        var provider = CreateProvider(http, Key);

        await Assert.ThrowsAsync<ModelProviderException>(() => provider.CompleteAsync("system", []));
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return respond(request);
        }
    }
}
