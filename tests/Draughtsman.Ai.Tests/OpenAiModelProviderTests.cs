using System.Net;
using System.Text;
using System.Text.Json;
using Draughtsman.Ai.OpenAi;

namespace Draughtsman.Ai.Tests;

public class OpenAiModelProviderTests
{
    private const string KeyVariable = "DRAUGHTSMAN_OPENAI_TEST_KEY";
    private const string Key = "sk-test-secret-value";

    private static OpenAiModelProvider CreateProvider(HttpClient http, string? key)
    {
        return new OpenAiModelProvider(http, "gpt-4o-mini", KeyVariable, _ => key);
    }

    [Fact]
    public async Task CompleteAsync_sends_the_system_prompt_and_messages_and_returns_the_reply()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new FakeHandler(async request =>
        {
            captured = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"draughtsman 1\ntype flowchart"}}]}""");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var provider = CreateProvider(http, Key);

        string reply = await provider.CompleteAsync("system prompt text", [new ModelMessage(ModelRole.User, "make me a diagram")]);

        Assert.Equal("draughtsman 1\ntype flowchart", reply);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://api.openai.com/v1/chat/completions", captured.RequestUri!.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal(Key, captured.Headers.Authorization!.Parameter);
        using JsonDocument body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("gpt-4o-mini", body.RootElement.GetProperty("model").GetString());
        JsonElement[] messages = [.. body.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system prompt text", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("make me a diagram", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompleteAsync_sends_an_assistant_turn_with_the_assistant_role()
    {
        string? capturedBody = null;
        var handler = new FakeHandler(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var provider = CreateProvider(http, Key);

        await provider.CompleteAsync("system", [new ModelMessage(ModelRole.Assistant, "earlier reply")]);

        using JsonDocument body = JsonDocument.Parse(capturedBody!);
        JsonElement[] messages = [.. body.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("earlier reply", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task CompleteAsync_reports_a_rejected_key_by_status_only_and_drops_the_providers_own_text()
    {
        // Providers commonly echo the key back masked ("...sk-test-****"), which is still part of the key: an
        // authentication failure must not carry their body at all.
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Incorrect API key provided: " + Key[..8] + "****", Encoding.UTF8, "text/plain"),
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var provider = CreateProvider(http, Key);

        ModelProviderException exception = await Assert.ThrowsAsync<ModelProviderException>(
            () => provider.CompleteAsync("system", []));

        Assert.True(exception.Reachable);
        Assert.Equal(401, exception.StatusCode);
        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.Contains("rejected", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Incorrect API key provided", exception.Message, StringComparison.Ordinal);
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
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
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
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
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
            return Task.FromResult(JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"ok"}}]}"""));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
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
        var handler = new FakeHandler(_ => Task.FromResult(JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"   "}}]}""")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
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
