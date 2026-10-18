using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Ai;

/// <summary>
/// What an admin has overridden in the AI settings page. A null field is "not overridden": it falls back to
/// draughtsman.yaml. Never carries the API key, which has its own encrypted path.
/// </summary>
internal sealed record AiOverrides(string? Provider, string? Model, string? Endpoint, string? ApiVersion)
{
    public static readonly AiOverrides None = new(null, null, null, null);

    public bool IsEmpty => Provider is null && Model is null && Endpoint is null && ApiVersion is null;
}

/// <summary>A provider configuration complete enough to make a call with. The API key is deliberately not part of it.</summary>
internal sealed record AiEffective(
    string Provider,
    string Model,
    Uri Endpoint,
    string ApiVersion,
    double? TimeoutSeconds,
    bool Think,
    string ApiKeyEnvironmentVariable);

/// <summary>
/// The outcome of layering overrides over the file: the values to show (even when not usable) and, when they are
/// usable, the <see cref="Effective"/> configuration; otherwise why AI is off. <see cref="Problem"/> never quotes a
/// configured value, so it is safe to show. <see cref="PolicyRefused"/> is set when the endpoint is refused by the AI endpoint policy
/// (<see cref="AiEndpointPolicy"/>) whatever else is missing, which is what lets the admin page refuse to save it.
/// </summary>
internal sealed record AiResolution(string Provider, string Model, string Endpoint, string ApiVersion, AiEffective? Effective, string? Problem, bool PolicyRefused = false);

internal static class AiSettingsResolver
{
    private const string DefaultOllamaEndpoint = "http://localhost:11434";
    private const string DefaultOpenAiEndpoint = "https://api.openai.com/v1";
    private const string DefaultAnthropicEndpoint = "https://api.anthropic.com/v1";

    /// <summary>The Azure OpenAI REST API version used when none is set. A current GA version.</summary>
    private const string DefaultAzureApiVersion = "2024-10-21";

    /// <summary>
    /// Each override wins over the file. The file's model, endpoint and API version only apply while the effective
    /// provider is the file's own: an admin who switches provider must not inherit an Ollama endpoint for Anthropic.
    /// </summary>
    public static AiResolution Resolve(AiConfig file, AiOverrides overrides)
    {
        string fileProvider = AiProviders.Canonical(file.Provider);
        string provider = overrides.Provider is { Length: > 0 } ? AiProviders.Canonical(overrides.Provider) : fileProvider;
        bool fileApplies = provider == fileProvider;

        string model = Pick(overrides.Model, fileApplies ? file.Model : null);
        string endpoint = Pick(overrides.Endpoint, fileApplies ? file.Endpoint : null);
        if (endpoint.Length == 0)
        {
            endpoint = DefaultEndpoint(provider);
        }

        string apiVersion = provider == AiProviders.AzureOpenAi ? Pick(overrides.ApiVersion, fileApplies ? file.ApiVersion : null) : string.Empty;
        if (provider == AiProviders.AzureOpenAi && apiVersion.Length == 0)
        {
            apiVersion = DefaultAzureApiVersion;
        }

        string? problem = Problem(provider, model, endpoint, file.AllowedEndpointHosts, out Uri? endpointUri);
        AiEffective? effective = problem is null && endpointUri is not null
            ? new AiEffective(provider, model, endpointUri, apiVersion, file.TimeoutSeconds, file.Think ?? false, file.ApiKeyEnvironmentVariable)
            : null;
        return new AiResolution(provider, model, endpoint, apiVersion, effective, problem, PolicyProblem(provider, endpoint, file.AllowedEndpointHosts) is not null);
    }

    /// <summary>
    /// Why the AI endpoint policy refuses <paramref name="endpoint"/> for <paramref name="provider"/>, or null. Null as well when the endpoint is
    /// not yet a usable URL for a known provider: those problems are reported on their own, and a half-filled form is not a policy violation.
    /// </summary>
    private static string? PolicyProblem(string provider, string endpoint, IReadOnlyList<string> allowedHosts)
    {
        if (!AiProviders.IsSupported(provider) || !Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || EndpointProblem(uri) is not null)
        {
            return null;
        }

        return AiEndpointPolicy.CheckEndpoint(provider, uri, allowedHosts);
    }

    private static string? Problem(string provider, string model, string endpoint, IReadOnlyList<string> allowedHosts, out Uri? endpointUri)
    {
        endpointUri = null;
        if (provider.Length == 0)
        {
            return "No AI provider is configured.";
        }

        if (!AiProviders.IsSupported(provider))
        {
            return "The AI provider is not one of ollama, openai, anthropic or azure-openai.";
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            return "No model is set.";
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            // Azure has no shared default host: the endpoint is the customer's own resource.
            return "No endpoint is set; set it to the Azure OpenAI resource endpoint (https://<resource>.openai.azure.com).";
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out endpointUri))
        {
            return "The endpoint is not a valid URL.";
        }

        if (EndpointProblem(endpointUri) is { } unusable)
        {
            endpointUri = null;
            return unusable;
        }

        if (PolicyProblem(provider, endpoint, allowedHosts) is { } refused)
        {
            endpointUri = null;
            return refused;
        }

        return null;
    }

    /// <summary>The provider and endpoint text <see cref="Resolve"/> would settle on for these overrides, for a caller that only needs to judge them.</summary>
    public static string? PolicyProblemFor(AiConfig file, AiOverrides overrides)
    {
        AiResolution resolution = Resolve(file, overrides);
        return resolution.PolicyRefused ? PolicyProblem(resolution.Provider, resolution.Endpoint, file.AllowedEndpointHosts) : null;
    }

    /// <summary>
    /// Why an absolute endpoint cannot be used, or null: http or https only (a <c>file:</c>, <c>ftp:</c> or <c>javascript:</c> URL is
    /// never a model server), no credentials in the URL and no fragment. Never echoes the endpoint, which may carry a secret.
    /// The one rule for draughtsman.yaml and the admin AI page (which also refuses a query string, see AiSettingsEndpoints).
    /// </summary>
    public static string? EndpointProblem(Uri endpoint)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return "The endpoint must use http or https.";
        }

        if (endpoint.UserInfo.Length > 0)
        {
            return "The endpoint must not contain a user name or password; put the API key in the key field or its environment variable.";
        }

        if (endpoint.Fragment.Length > 0)
        {
            return "The endpoint must not contain a fragment (#...).";
        }

        return string.IsNullOrEmpty(endpoint.Host) ? "The endpoint must name a host." : null;
    }

    /// <summary>The endpoint a provider uses when none is set, or empty (Azure OpenAI has none: the endpoint is the customer's own resource).</summary>
    public static string DefaultEndpointOf(string provider)
    {
        return DefaultEndpoint(provider);
    }

    private static string DefaultEndpoint(string provider)
    {
        return provider switch
        {
            AiProviders.Ollama => DefaultOllamaEndpoint,
            AiProviders.Anthropic => DefaultAnthropicEndpoint,
            AiProviders.OpenAi => DefaultOpenAiEndpoint,
            _ => string.Empty,
        };
    }

    private static string Pick(string? overridden, string? fromFile)
    {
        return !string.IsNullOrWhiteSpace(overridden) ? overridden.Trim() : (fromFile ?? string.Empty).Trim();
    }

    /// <summary>An endpoint for display: scheme, host, port and path, with any credentials or query a hand-edited file might carry dropped.</summary>
    public static string DisplayEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return string.Empty;
        }

        return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty, Query = string.Empty, Fragment = string.Empty }.Uri.ToString();
    }
}
