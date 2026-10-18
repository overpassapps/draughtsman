using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Draughtsman.Ai.AzureOpenAi;

/// <summary>
/// <see cref="IModelProvider"/> over Azure OpenAI's deployment-scoped Chat Completions API. Unlike the OpenAI
/// provider, Azure routes by deployment rather than by a <c>model</c> in the body, so the deployment name (from
/// <c>ai.model</c>) is part of the path and the endpoint must be the resource's own host. Uses
/// <c>POST {endpoint}/openai/deployments/{deployment}/chat/completions?api-version=…</c> with the key in the
/// <c>api-key</c> header (Azure's convention, not a bearer token) and a single JSON response: this provider does
/// not stream (see <see cref="IModelProvider"/>). The API key is read from the environment variable named by
/// <c>ai.apiKeyEnvironmentVariable</c> on every call; it is never written to <c>draughtsman.yaml</c>, a log
/// message, or an exception message.
/// </summary>
public sealed class AzureOpenAiModelProvider : IModelProvider
{
    private readonly HttpClient _http;
    private readonly string _apiKeyEnvironmentVariable;
    private readonly string _apiVersion;
    private readonly Func<string, string?> _environment;

    /// <param name="apiVersion">
    /// The Azure OpenAI REST API version that goes in the query string, e.g. <c>2024-10-21</c>. Azure requires one
    /// on every request; the registration supplies <c>ai.apiVersion</c> or its default.
    /// </param>
    /// <param name="environment">
    /// Reads an environment variable by name. Defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>;
    /// tests inject their own so they never mutate the process environment.
    /// </param>
    public AzureOpenAiModelProvider(
        HttpClient http,
        string deployment,
        string apiKeyEnvironmentVariable,
        string apiVersion,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(deployment);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeyEnvironmentVariable);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);
        _http = http;
        Model = deployment;
        _apiKeyEnvironmentVariable = apiKeyEnvironmentVariable;
        _apiVersion = apiVersion;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public string Provider => "azure-openai";

    /// <summary>The Azure OpenAI deployment name, taken from <c>ai.model</c>.</summary>
    public string Model { get; }

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(messages);

        string? apiKey = _environment(_apiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ModelProviderException(
                ProviderErrors.MissingKey("Azure OpenAI", _apiKeyEnvironmentVariable), reachable: false);
        }

        var request = new AzureChatRequest(
            Model,
            [new AzureChatMessage("system", systemPrompt), .. messages.Select(ToAzureMessage)]);

        string relativeUri =
            $"openai/deployments/{Uri.EscapeDataString(Model)}/chat/completions?api-version={Uri.EscapeDataString(_apiVersion)}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, relativeUri)
        {
            Content = JsonContent.Create(request, options: AzureJson.Options),
        };
        httpRequest.Headers.Add("api-key", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            throw new ModelProviderException($"Could not reach Azure OpenAI at {_http.BaseAddress}: {e.Message}", reachable: false, e);
        }
        catch (TaskCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelProviderException($"Azure OpenAI at {_http.BaseAddress} timed out.", reachable: false, e);
        }

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ModelProviderException(ProviderErrors.HttpFailure("Azure OpenAI", response.StatusCode, response.ReasonPhrase, body, apiKey), reachable: true, statusCode: (int)response.StatusCode);
        }

        AzureChatResponse? parsed;
        try
        {
            parsed = await response.Content.ReadFromJsonAsync<AzureChatResponse>(AzureJson.Options, cancellationToken);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new ModelProviderException($"Azure OpenAI's response could not be read as JSON: {e.Message}", reachable: true, e);
        }

        string? text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ModelProviderException("Azure OpenAI returned an empty message.", reachable: true);
        }

        return text;
    }

    private static AzureChatMessage ToAzureMessage(ModelMessage message)
    {
        return new AzureChatMessage(message.Role == ModelRole.User ? "user" : "assistant", message.Content);
    }
}

internal static class AzureJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);
}

internal sealed record AzureChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<AzureChatMessage> Messages);

internal sealed record AzureChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record AzureChatResponse(
    [property: JsonPropertyName("choices")] IReadOnlyList<AzureChoice>? Choices);

internal sealed record AzureChoice(
    [property: JsonPropertyName("message")] AzureChatMessage? Message);
