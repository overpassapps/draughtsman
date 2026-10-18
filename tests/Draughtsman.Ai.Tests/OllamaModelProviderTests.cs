using System.Net;
using System.Text;
using System.Text.Json;
using Draughtsman.Ai.Ollama;

namespace Draughtsman.Ai.Tests;

public class OllamaModelProviderTests
{
    [Fact]
    public async Task CompleteAsync_sends_the_system_prompt_and_messages_and_returns_the_reply()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new FakeHandler(async request =>
        {
            captured = request;
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"message":{"role":"assistant","content":"draughtsman 1\ntype flowchart"},"done":true}""");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var provider = new OllamaModelProvider(http, "llama3.1");

        string reply = await provider.CompleteAsync("system prompt text", [new ModelMessage(ModelRole.User, "make me a diagram")]);

        Assert.Equal("draughtsman 1\ntype flowchart", reply);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("http://localhost:11434/api/chat", captured.RequestUri!.ToString());
        using JsonDocument body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("llama3.1", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.GetProperty("think").GetBoolean());
        JsonElement[] messages = [.. body.RootElement.GetProperty("messages").EnumerateArray()];
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("system prompt text", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("make me a diagram", messages[1].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompleteAsync_sends_the_correct_think_value(bool think)
    {
        string? capturedBody = null;
        var handler = new FakeHandler(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"message":{"role":"assistant","content":"ok"},"done":true}""");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var provider = new OllamaModelProvider(http, "llama3.1", think);

        await provider.CompleteAsync("system", []);

        using JsonDocument body = JsonDocument.Parse(capturedBody!);
        Assert.Equal(think, body.RootElement.GetProperty("think").GetBoolean());
    }

    [Fact]
    public async Task CompleteAsync_throws_a_reachable_ModelProviderException_on_a_non_success_status()
    {
        var handler = new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("model \"missing\" not found", Encoding.UTF8, "text/plain"),
        }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var provider = new OllamaModelProvider(http, "missing");

        ModelProviderException exception = await Assert.ThrowsAsync<ModelProviderException>(
            () => provider.CompleteAsync("system", []));

        Assert.True(exception.Reachable);
        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteAsync_throws_an_unreachable_ModelProviderException_when_the_connection_fails()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("Connection refused"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var provider = new OllamaModelProvider(http, "llama3.1");

        ModelProviderException exception = await Assert.ThrowsAsync<ModelProviderException>(
            () => provider.CompleteAsync("system", []));

        // Two-sided against the previous test: a connection failure must report Reachable=false, not just "some
        // ModelProviderException", or a caller could never tell "model unreachable" from "model said no" apart.
        Assert.False(exception.Reachable);
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
