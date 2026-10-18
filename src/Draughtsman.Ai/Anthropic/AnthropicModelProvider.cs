using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Draughtsman.Ai.Anthropic;

/// <summary>
/// <see cref="IModelProvider"/> over Anthropic's Messages API
/// (https://docs.anthropic.com/en/api/messages). Uses <c>POST /v1/messages</c> with a single JSON response: this
/// provider does not stream (see <see cref="IModelProvider"/>). The system prompt travels in the request's
/// top-level <c>system</c> field, not as a message (the Messages API has no system role), and the key is sent in
/// the <c>x-api-key</c> header alongside the required <c>anthropic-version</c>. The API key is read from the
/// environment variable named by <c>ai.apiKeyEnvironmentVariable</c> on every call; it is never written to
/// <c>draughtsman.yaml</c>, a log message, or an exception message.
/// </summary>
public sealed class AnthropicModelProvider : IModelProvider
{
    /// <summary>
    /// The Messages API version sent as <c>anthropic-version</c>. Pinned rather than configurable: the request and
    /// response shapes this provider knows are the ones this version defines.
    /// </summary>
    public const string AnthropicVersion = "2023-06-01";

    /// <summary>
    /// The Messages API requires <c>max_tokens</c> on every request. 8192 leaves room for a whole document's DSL
    /// without letting an accidental runaway turn into an unbounded bill.
    /// </summary>
    private const int MaxTokens = 8192;

    private readonly HttpClient _http;
    private readonly string _apiKeyEnvironmentVariable;
    private readonly Func<string, string?> _environment;

    /// <param name="environment">
    /// Reads an environment variable by name. Defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>;
    /// tests inject their own so they never mutate the process environment.
    /// </param>
    public AnthropicModelProvider(
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

    public string Provider => "anthropic";

    public string Model { get; }

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(messages);

        string? apiKey = _environment(_apiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ModelProviderException(
                ProviderErrors.MissingKey("Anthropic", _apiKeyEnvironmentVariable), reachable: false);
        }

        var request = new AnthropicMessagesRequest(
            Model,
            MaxTokens,
            systemPrompt,
            [.. messages.Select(ToAnthropicMessage)]);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "messages")
        {
            Content = JsonContent.Create(request, options: AnthropicJson.Options),
        };
        httpRequest.Headers.Add("x-api-key", apiKey);
        httpRequest.Headers.Add("anthropic-version", AnthropicVersion);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new ModelProviderException($"Could not reach Anthropic at {_http.BaseAddress}: {e.Message}", reachable: false, e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelProviderException($"Anthropic at {_http.BaseAddress} timed out.", reachable: false, e);
        }

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ModelProviderException(ProviderErrors.HttpFailure("Anthropic", response.StatusCode, response.ReasonPhrase, body, apiKey), reachable: true, statusCode: (int)response.StatusCode);
        }

        AnthropicMessagesResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<AnthropicMessagesResponse>(AnthropicJson.Options, cancellationToken);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new ModelProviderException($"Anthropic's response could not be read as JSON: {e.Message}", reachable: true, e);
        }

        // A single reply can carry several content blocks (text plus tool/thinking blocks); join the text ones so the
        // caller receives the whole answer, and ignore any block this provider does not use.
        string? text = parsed?.Content is { Count: > 0 } blocks
            ? string.Concat(blocks.Where(block => block.Type == "text").Select(block => block.Text))
            : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ModelProviderException("Anthropic returned an empty message.", reachable: true);
        }

        return text;
    }

    private static AnthropicMessage ToAnthropicMessage(ModelMessage message)
    {
        return new AnthropicMessage(message.Role == ModelRole.User ? "user" : "assistant", message.Content);
    }
}

internal static class AnthropicJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);
}

internal sealed record AnthropicMessagesRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("messages")] IReadOnlyList<AnthropicMessage> Messages);

internal sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record AnthropicMessagesResponse(
    [property: JsonPropertyName("content")] IReadOnlyList<AnthropicContentBlock>? Content);

internal sealed record AnthropicContentBlock(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] string? Text);
