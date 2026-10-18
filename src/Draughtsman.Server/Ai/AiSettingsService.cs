using Draughtsman.Ai;
using Draughtsman.Ai.Anthropic;
using Draughtsman.Ai.AzureOpenAi;
using Draughtsman.Ai.Ollama;
using Draughtsman.Ai.OpenAi;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;

namespace Draughtsman.Server.Ai;

/// <summary>
/// The live AI configuration (M3; docs/api.md "## AI settings"): draughtsman.yaml's <c>ai</c> section, layered under
/// the admin's stored overrides and API key, applied on the next request with no restart. Every request that needs a
/// model asks <see cref="CreateProvider"/> and <see cref="Status"/> here rather than reading a provider registered at
/// startup, which is what lets a settings change land immediately.
/// <para>
/// The API key is write-only. It is held in memory only to send to the provider; nothing here returns, logs or throws
/// it. Precedence: the environment variable named by <c>ai.apiKeyEnvironmentVariable</c> wins over the stored key, so
/// an operator's deployment secret cannot be silently replaced from the browser; <see cref="Describe"/> says which one
/// is in effect.
/// </para>
/// </summary>
public sealed class AiSettingsService
{
    /// <summary>How long Test connection waits for the provider before giving up.</summary>
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly AiConfig _file;
    private readonly IHttpClientFactory _httpClients;
    private readonly AiSettingsStore? _store;
    private readonly KeyRingStatus? _keyRing;
    private readonly Func<string, string?> _environment;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private volatile State _state;

    internal AiSettingsService(AiConfig file, IHttpClientFactory httpClients, AiSettingsStore? store, ILogger logger, Func<string, string?>? environment = null, KeyRingStatus? keyRing = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(httpClients);
        ArgumentNullException.ThrowIfNull(logger);
        _file = file;
        _httpClients = httpClients;
        _store = store;
        _keyRing = keyRing;
        _logger = logger;
        _environment = environment ?? Environment.GetEnvironmentVariable;

        StoredAiSettings stored = store?.Load() ?? StoredAiSettings.Empty;
        _state = new State(stored.Overrides, stored.ApiKey, stored.KeyUnreadable, AiSettingsResolver.Resolve(file, stored.Overrides));
        if (!stored.Overrides.IsEmpty)
        {
            logger.LogInformation("AI settings overrides loaded from the data folder; they win over draughtsman.yaml.");
        }
    }

    /// <summary>Whether AI is on, and with which provider and model, right now.</summary>
    public AiStatus Status
    {
        get
        {
            AiEffective? effective = _state.Resolution.Effective;
            return effective is null ? AiStatus.Off : new AiStatus(true, effective.Provider, effective.Model);
        }
    }

    /// <summary>A provider for the current settings, or null while AI is off. A fresh instance each call, so a change made since the last call is used.</summary>
    public IModelProvider? CreateProvider()
    {
        AiEffective? effective = _state.Resolution.Effective;
        if (effective is null)
        {
            return null;
        }

        HttpClient http = _httpClients.CreateClient(AiProviders.HttpClientName(effective.Provider));
        AiHttpClients.Configure(http, effective);

        // The key is looked up when a call is made, not captured here, so a key saved a moment later is still used. It is chosen for this
        // provider's own endpoint, so it can never be handed to a host the endpoint was not (docs/ai-endpoint-policy.md).
        string? ResolveKey(string _) => ChooseKey(effective).Key;
        switch (effective.Provider)
        {
            case AiProviders.Ollama:
                // Most Ollama installs need no key; one fronted by a reverse proxy can still send a bearer token.
                string? key = ChooseKey(effective).Key;
                http.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(key) ? null : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                return new OllamaModelProvider(http, effective.Model, effective.Think);
            case AiProviders.OpenAi:
                return new OpenAiModelProvider(http, effective.Model, effective.ApiKeyEnvironmentVariable, ResolveKey);
            case AiProviders.Anthropic:
                return new AnthropicModelProvider(http, effective.Model, effective.ApiKeyEnvironmentVariable, ResolveKey);
            default:
                return new AzureOpenAiModelProvider(http, effective.Model, effective.ApiKeyEnvironmentVariable, effective.ApiVersion, ResolveKey);
        }
    }

    /// <summary>The settings as the admin page shows them. Says whether a key is set and where it comes from; never the key or any part of it.</summary>
    public AiSettingsDescription Describe()
    {
        State state = _state;
        AiResolution file = AiSettingsResolver.Resolve(_file, AiOverrides.None);
        AiResolution now = state.Resolution;
        string variable = _file.ApiKeyEnvironmentVariable;
        bool environmentSet = !string.IsNullOrWhiteSpace(_environment(variable));
        bool storedSet = state.StoredKey is not null;

        // With AI usable, the source is the key that would really be sent (and why one is held back); with it off nothing is sent.
        KeyChoice chosen = now.Effective is { } effective ? ChooseKey(effective) : new KeyChoice(null, environmentSet ? "environment" : storedSet ? "stored" : "none", null);
        string source = chosen.Source;

        return new AiSettingsDescription(
            Enabled: now.Effective is not null,
            Problem: now.Problem,
            Effective: Values(now),
            File: Values(file),
            Overrides: new AiSettingsValues(state.Overrides.Provider, state.Overrides.Model, state.Overrides.Endpoint, state.Overrides.ApiVersion),
            Key: new AiKeyState(source, variable, environmentSet, storedSet, state.StoredKeyUnreadable, AiProviders.RequiresKey(now.Provider), chosen.Withheld),
            Providers: AiProviders.All,
            KeyRing: _keyRing?.Describe());
    }

    /// <summary>
    /// Replaces the provider/endpoint/model/API version overrides (all four at once; null clears one). Nothing changes if saving fails.
    /// A change of the endpoint's host (or port) forgets the stored key, which was entered for the host it was sent to; the admin enters
    /// it again for the new one. A key entered while no endpoint was configured stays only if the new endpoint is the provider's own
    /// default; any other host is a new destination for it. The caller has already refused an endpoint the policy does not allow.
    /// </summary>
    internal AiEndpointChange SetOverrides(AiOverrides overrides)
    {
        lock (_gate)
        {
            State current = _state;
            AiResolution next = AiSettingsResolver.Resolve(_file, overrides);
            string from = AiEndpointPolicy.AuthorityOf(current.Resolution.Endpoint);
            string to = AiEndpointPolicy.AuthorityOf(next.Endpoint);
            bool moved = from != to;
            bool entirelyNew = from.Length == 0 && to == AiEndpointPolicy.AuthorityOf(AiSettingsResolver.DefaultEndpointOf(next.Provider));
            bool clearKey = moved && !entirelyNew && (current.StoredKey is not null || current.StoredKeyUnreadable);
            string? key = clearKey ? null : current.StoredKey;

            _store?.Save(overrides, key);
            _state = current with { Overrides = overrides, Resolution = next, StoredKey = key, StoredKeyUnreadable = !clearKey && current.StoredKeyUnreadable };
            return new AiEndpointChange(from, to, clearKey);
        }
    }

    /// <summary>The refusal (the host and port it was about to point at, the provider, and the reason in fixed words) when these overrides would make the endpoint one the policy does not allow; null when they would not.</summary>
    internal AiPolicyRefusal? PolicyRefusalFor(AiOverrides overrides)
    {
        AiResolution resolution = AiSettingsResolver.Resolve(_file, overrides);
        return AiSettingsResolver.PolicyProblemFor(_file, overrides) is { } reason
            ? new AiPolicyRefusal(resolution.Provider, AiEndpointPolicy.AuthorityOf(resolution.Endpoint), reason)
            : null;
    }

    /// <summary>Why a key cannot be stored for the endpoint in effect, or null: a key is never carried over plain http except to this machine.</summary>
    internal string? KeyRefusalFor()
    {
        return Uri.TryCreate(_state.Resolution.Endpoint, UriKind.Absolute, out Uri? endpoint) && !AiEndpointPolicy.MayCarryKey(endpoint)
            ? "The endpoint uses plain http, and an API key is never sent over plain http except to this machine. Use an https endpoint first."
            : null;
    }

    /// <summary>Stores <paramref name="apiKey"/> encrypted, replacing any earlier one. The caller has already validated it.</summary>
    internal void SetApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(apiKey);
        lock (_gate)
        {
            State current = _state;
            _store?.Save(current.Overrides, apiKey);
            _state = current with { StoredKey = apiKey, StoredKeyUnreadable = false };
        }
    }

    internal void ClearApiKey()
    {
        lock (_gate)
        {
            State current = _state;
            _store?.Save(current.Overrides, null);
            _state = current with { StoredKey = null, StoredKeyUnreadable = false };
        }
    }

    /// <summary>Back to draughtsman.yaml: every override is dropped and so is the stored key, which was meant for the provider that is no longer in use.</summary>
    internal AiEndpointChange Reset()
    {
        lock (_gate)
        {
            State current = _state;
            AiResolution next = AiSettingsResolver.Resolve(_file, AiOverrides.None);
            _store?.Save(AiOverrides.None, null);
            _state = new State(AiOverrides.None, null, false, next);
            return new AiEndpointChange(AiEndpointPolicy.AuthorityOf(current.Resolution.Endpoint), AiEndpointPolicy.AuthorityOf(next.Endpoint), current.StoredKey is not null || current.StoredKeyUnreadable);
        }
    }

    /// <summary>
    /// Makes one minimal request with the current settings and says, in plain English, whether it worked. The reply
    /// is built from the outcome (reachable? which status?), never from the provider's own text, so it cannot carry
    /// the key or anything else the provider echoed.
    /// </summary>
    public async Task<AiConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        AiResolution resolution = _state.Resolution;
        if (resolution.Effective is not { } effective)
        {
            return new AiConnectionTestResult(false, resolution.Problem ?? "AI is not configured.");
        }

        KeyChoice choice = ChooseKey(effective);
        if (AiProviders.RequiresKey(effective.Provider) && string.IsNullOrWhiteSpace(choice.Key))
        {
            return new AiConnectionTestResult(false, choice.Withheld ?? $"No API key is set. Paste one here, or set the environment variable {effective.ApiKeyEnvironmentVariable}.");
        }

        IModelProvider provider = CreateProvider()!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TestTimeout);
        try
        {
            await provider.CompleteAsync("Reply with the single word OK.", [new ModelMessage(ModelRole.User, "ping")], timeout.Token);
            return new AiConnectionTestResult(true, $"Connected: {effective.Provider} answered using model {effective.Model}.");
        }
        catch (ModelProviderException e)
        {
            return new AiConnectionTestResult(false, AiEndpointRefusedException.Find(e) is { } refused ? $"The connection was refused by the AI endpoint policy: {refused.Message}" : Explain(e, effective));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AiConnectionTestResult(false, $"{Authority(effective.Endpoint)} did not answer within {TestTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Type only: an unexpected exception's text is not vetted the way ModelProviderException's is.
            _logger.LogWarning("AI connection test failed unexpectedly ({Reason}).", e.GetType().Name);
            return new AiConnectionTestResult(false, "The test failed unexpectedly; see the server log.");
        }
    }

    /// <summary>The key a request to <paramref name="effective"/> may carry, which one it is, and why one that exists is held back.</summary>
    private readonly record struct KeyChoice(string? Key, string Source, string? Withheld);

    /// <summary>
    /// Picks the key for a request to <paramref name="effective"/>'s endpoint (docs/ai-endpoint-policy.md). A key never travels over plain
    /// http except to this machine. The environment key (it wins when it may be used) goes only to the host in draughtsman.yaml, or the
    /// provider's own default when the file names none, so an admin cannot send a deployment secret somewhere new from the browser. The
    /// stored key goes only while the endpoint is still the one it was entered for; changing the host forgets it (<see cref="SetOverrides"/>).
    /// </summary>
    private KeyChoice ChooseKey(AiEffective effective)
    {
        string? fromEnvironment = _environment(_file.ApiKeyEnvironmentVariable);
        bool environmentSet = !string.IsNullOrWhiteSpace(fromEnvironment);
        string? stored = AiEndpointPolicy.AuthorityOf(_state.Resolution.Endpoint) == AiEndpointPolicy.AuthorityOf(effective.Endpoint) ? _state.StoredKey : null;

        if (!AiEndpointPolicy.MayCarryKey(effective.Endpoint))
        {
            return new KeyChoice(null, "none", environmentSet || stored is not null ? "A key is never sent over plain http except to this machine, so none is sent to this endpoint. Use an https endpoint." : null);
        }

        string? withheld = null;
        if (environmentSet)
        {
            if (AiEndpointPolicy.AuthorityOf(effective.Endpoint) == TrustedEnvironmentAuthority(effective.Provider))
            {
                return new KeyChoice(fromEnvironment, "environment", null);
            }

            withheld = $"The environment variable {_file.ApiKeyEnvironmentVariable} is set, but it is sent only to the host in draughtsman.yaml (or the provider's own when the file names none), and this endpoint is another host. Enter a key here for it, or change the endpoint back.";
        }

        return stored is not null ? new KeyChoice(stored, "stored", withheld) : new KeyChoice(null, "none", withheld);
    }

    /// <summary>The host and port the environment key may be sent to for <paramref name="provider"/>: the file's own endpoint while the file's provider is in use, else the provider's default; empty when neither exists.</summary>
    private string TrustedEnvironmentAuthority(string provider)
    {
        string fileEndpoint = AiProviders.Canonical(_file.Provider) == provider && !string.IsNullOrWhiteSpace(_file.Endpoint) ? _file.Endpoint.Trim() : AiSettingsResolver.DefaultEndpointOf(provider);
        return AiEndpointPolicy.AuthorityOf(fileEndpoint);
    }

    private static string Explain(ModelProviderException e, AiEffective effective)
    {
        if (!e.Reachable)
        {
            return $"Could not reach {Authority(effective.Endpoint)}. Check the endpoint, and that the server is running and reachable from this machine.";
        }

        return e.StatusCode switch
        {
            >= 300 and < 400 => $"The endpoint answered with a redirect (HTTP {e.StatusCode}), which is not followed. Set the endpoint to the address it redirects to.",
            401 or 403 => "The provider rejected the API key. Check it is valid for this provider and can use the model.",
            404 => "The provider answered 404 Not Found. Check the endpoint and the model (or Azure deployment) name.",
            429 => "The provider is rate limiting this key or it is out of quota (HTTP 429).",
            >= 500 => $"The provider reported a server error (HTTP {e.StatusCode}). Try again shortly.",
            int status => $"The provider refused the request (HTTP {status}). Check the model name and endpoint.",
            _ => "The provider answered, but not in the format Draughtsman expects. Check the endpoint points at the provider's chat API.",
        };
    }

    private static string Authority(Uri endpoint)
    {
        return endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";
    }

    private static AiSettingsValues Values(AiResolution resolution)
    {
        return new AiSettingsValues(
            resolution.Provider.Length == 0 ? null : resolution.Provider,
            resolution.Model.Length == 0 ? null : resolution.Model,
            resolution.Endpoint.Length == 0 ? null : AiSettingsResolver.DisplayEndpoint(resolution.Endpoint),
            resolution.ApiVersion.Length == 0 ? null : resolution.ApiVersion);
    }

    private sealed record State(AiOverrides Overrides, string? StoredKey, bool StoredKeyUnreadable, AiResolution Resolution);
}

/// <summary>
/// Body of every AI settings response. Nothing in it is, or contains, the API key. <c>KeyRing</c> says how the key
/// ring that encrypts the stored key is protected at rest, so the page can warn while it is not.
/// </summary>
public sealed record AiSettingsDescription(
    bool Enabled,
    string? Problem,
    AiSettingsValues Effective,
    AiSettingsValues File,
    AiSettingsValues Overrides,
    AiKeyState Key,
    IReadOnlyList<string> Providers,
    KeyRingState? KeyRing = null);

public sealed record AiSettingsValues(string? Provider, string? Model, string? Endpoint, string? ApiVersion);

/// <summary>
/// Where the key in effect comes from: <c>environment</c> (wins), <c>stored</c> (the encrypted one saved here) or
/// <c>none</c>. Presence only. <see cref="Required"/> is false for Ollama, which runs with no key. <see cref="Withheld"/> says, in plain
/// English, why a key that exists is not being sent to this endpoint (an environment key is sent only to the host in draughtsman.yaml or
/// the provider's own; no key goes over plain http except to this machine), and is absent otherwise.
/// </summary>
public sealed record AiKeyState(string Source, string EnvironmentVariable, bool EnvironmentSet, bool StoredSet, bool StoredUnreadable, bool Required, string? Withheld = null);

/// <summary>An endpoint the policy refused: which provider, which host and port, and why (fixed text, safe to show).</summary>
internal sealed record AiPolicyRefusal(string Provider, string Authority, string Reason);

/// <summary>
/// What a settings change did to the endpoint's host: the host and port before and after (empty for none) and whether the stored key was
/// forgotten because of it. <see cref="Moved"/> is what the security log records.
/// </summary>
internal sealed record AiEndpointChange(string From, string To, bool KeyCleared)
{
    public bool Moved => From != To;
}

public sealed record AiConnectionTestResult(bool Ok, string Message);
