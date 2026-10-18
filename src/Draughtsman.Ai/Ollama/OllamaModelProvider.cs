using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Draughtsman.Ai.Ollama;

/// <summary>
/// <see cref="IModelProvider"/> over Ollama's local HTTP API (https://github.com/ollama/ollama/blob/main/docs/api.md),
/// the first-class provider an air-gapped site runs (build-spec-v0.1.md section 9: "Ollama support ... so an
/// air-gapped site loses nothing but quality"). Uses <c>POST /api/chat</c> with <c>stream: false</c>: this provider
/// does not stream (see <see cref="IModelProvider"/>), so a single JSON response is simpler than reassembling NDJSON
/// chunks. A model name ending in <c>:cloud</c> is still routed by the Ollama daemon itself to Ollama's cloud, not
/// run locally - this provider has no way to tell, so the caller is responsible for choosing a local model when
/// air-gapped operation matters.
/// </summary>
public sealed class OllamaModelProvider : IModelProvider
{
    private readonly HttpClient _http;
    private readonly bool _think;

    public OllamaModelProvider(HttpClient http, string model, bool think = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _http = http;
        Model = model;
        _think = think;
    }

    public string Provider => "ollama";

    public string Model { get; }

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(messages);

        var request = new OllamaChatRequest(
            Model,
            [new OllamaChatMessage("system", systemPrompt), .. messages.Select(ToOllamaMessage)],
            Stream: false,
            Think: _think);

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("api/chat", request, OllamaJson.Options, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new ModelProviderException($"Could not reach Ollama at {_http.BaseAddress}: {e.Message}", reachable: false, e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelProviderException($"Ollama at {_http.BaseAddress} timed out.", reachable: false, e);
        }

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ModelProviderException(ProviderErrors.HttpFailure("Ollama", response.StatusCode, response.ReasonPhrase, body, apiKey: null), reachable: true, statusCode: (int)response.StatusCode);
        }

        OllamaChatResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(OllamaJson.Options, cancellationToken);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new ModelProviderException($"Ollama's response could not be read as JSON: {e.Message}", reachable: true, e);
        }

        string? text = parsed?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ModelProviderException("Ollama returned an empty message.", reachable: true);
        }

        return text;
    }

    private static OllamaChatMessage ToOllamaMessage(ModelMessage message)
    {
        return new OllamaChatMessage(message.Role == ModelRole.User ? "user" : "assistant", message.Content);
    }
}

internal static class OllamaJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);
}

internal sealed record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OllamaChatMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream,
    [property: JsonPropertyName("think")] bool Think);

internal sealed record OllamaChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record OllamaChatResponse(
    [property: JsonPropertyName("message")] OllamaChatMessage? Message,
    [property: JsonPropertyName("done")] bool Done);
