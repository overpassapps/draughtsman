using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Draughtsman.Ai.OpenAi;

/// <summary>
/// <see cref="IModelProvider"/> over OpenAI's Chat Completions API
/// (https://platform.openai.com/docs/api-reference/chat), which every OpenAI-compatible endpoint (Azure OpenAI's
/// compatibility surface, vLLM, LiteLLM, OpenRouter and the rest) also serves. Uses <c>POST /chat/completions</c>
/// with a single JSON response: this provider does not stream (see <see cref="IModelProvider"/>). The API key is
/// read from the environment variable named by <c>ai.apiKeyEnvironmentVariable</c> on every call and sent as a
/// bearer token; it is never written to <c>draughtsman.yaml</c>, a log message, or an exception message.
/// </summary>
public sealed class OpenAiModelProvider : IModelProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKeyEnvironmentVariable;
    private readonly Func<string, string?> _environment;

    /// <param name="environment">
    /// Reads an environment variable by name. Defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>;
    /// tests inject their own so they never mutate the process environment.
    /// </param>
    public OpenAiModelProvider(
        HttpClient http,
        string model,
        string apiKeyEnvironmentVariable,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyEnvironmentVariable);
        _http = http;
        Model = model;
        _apiKeyEnvironmentVariable = apiKeyEnvironmentVariable;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public string Provider => "openai";

    public string Model { get; }

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(messages);

        string? apiKey = _environment(_apiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ModelProviderException(
                ProviderErrors.MissingKey("OpenAI", _apiKeyEnvironmentVariable), reachable: false);
        }

        var request = new OpenAiChatRequest(
            Model,
            [new OpenAiChatMessage("system", systemPrompt), .. messages.Select(ToOpenAiMessage)]);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(request, options: OpenAiJson.Options),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new ModelProviderException($"Could not reach OpenAI at {_http.BaseAddress}: {e.Message}", reachable: false, e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelProviderException($"OpenAI at {_http.BaseAddress} timed out.", reachable: false, e);
        }

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ModelProviderException(ProviderErrors.HttpFailure("OpenAI", response.StatusCode, response.ReasonPhrase, body, apiKey), reachable: true, statusCode: (int)response.StatusCode);
        }

        OpenAiChatResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<OpenAiChatResponse>(OpenAiJson.Options, cancellationToken);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new ModelProviderException($"OpenAI's response could not be read as JSON: {e.Message}", reachable: true, e);
        }

        string? text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ModelProviderException("OpenAI returned an empty message.", reachable: true);
        }

        return text;
    }

    private static OpenAiChatMessage ToOpenAiMessage(ModelMessage message)
    {
        return new OpenAiChatMessage(message.Role == ModelRole.User ? "user" : "assistant", message.Content);
    }
}

internal static class OpenAiJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);
}

internal sealed record OpenAiChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OpenAiChatMessage> Messages);

internal sealed record OpenAiChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record OpenAiChatResponse(
    [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChoice>? Choices);

internal sealed record OpenAiChoice(
    [property: JsonPropertyName("message")] OpenAiChatMessage? Message);
