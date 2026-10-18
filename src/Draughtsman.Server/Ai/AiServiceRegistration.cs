using Draughtsman.Ai;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Draughtsman.Server.Ai;

/// <summary>
/// Wires bring-your-own AI (build-spec-v0.1.md section 7): "ollama", "openai" (or any OpenAI-compatible endpoint),
/// "anthropic" and "azure-openai". Nothing here fixes a provider at startup. <see cref="AiSettingsService"/> layers
/// the admin's stored overrides over <c>ai</c> in draughtsman.yaml, and <see cref="AiStatus"/> and
/// <see cref="IModelProvider"/> are resolved from it per request, so a change in the AI settings page lands with no
/// restart. An empty, unrecognised or misconfigured provider leaves <see cref="AiStatus"/> off and
/// <see cref="IModelProvider"/> unresolvable - AI features hidden, never broken.
/// </summary>
public static class AiServiceRegistration
{
    public static IServiceCollection AddAi(this IServiceCollection services, AiConfig config, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        // Always: an admin can turn AI on at runtime, so the factory must exist even while the file leaves it off.
        services.AddHttpClient();
        services.TryAddSingleton<IAiEndpointNetwork, SystemAiEndpointNetwork>();

        // The endpoint policy (docs/ai-endpoint-policy.md) lives in each provider client's connect step, for all four whether or not the
        // file names one: an admin can switch provider at runtime. Redirects are never followed.
        foreach (string provider in AiProviders.All)
        {
            services.AddHttpClient(AiProviders.HttpClientName(provider))
                .ConfigurePrimaryHttpMessageHandler(sp => AiConnectGuard.CreateHandler(provider, config.AllowedEndpointHosts, sp.GetRequiredService<IAiEndpointNetwork>()));
        }

        int unusable = config.AllowedEndpointHosts.Count(entry => !AiEndpointPolicy.IsUsableEntry(entry));
        if (unusable > 0)
        {
            logger.LogWarning("{Count} entries of ai.allowedEndpointHosts in draughtsman.yaml are not a host name, an IP address or *.suffix and match nothing.", unusable);
        }

        // The startup log speaks for draughtsman.yaml alone; the live service also applies any stored overrides.
        AiResolution fromFile = AiSettingsResolver.Resolve(config, AiOverrides.None);
        if (fromFile.Effective is null)
        {
            if (fromFile.Provider.Length > 0)
            {
                logger.LogWarning("ai in draughtsman.yaml is not usable: {Problem} AI features are hidden until that is fixed here or in the admin AI settings.", fromFile.Problem);
            }
        }
        else
        {
            // The named clients keep the file's endpoint and timeout as their own configuration; the live service
            // re-applies the effective ones to each client it creates, which is what makes an override take effect.
            services.AddHttpClient(AiProviders.HttpClientName(fromFile.Effective.Provider), http => AiHttpClients.Configure(http, fromFile.Effective));
        }

        services.AddSingleton(sp => new AiSettingsService(
            config,
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetService<AiSettingsStore>(),
            (sp.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateLogger("Draughtsman.Server.Ai.Settings"),
            keyRing: sp.GetService<KeyRingStatus>()));
        services.AddTransient(sp => sp.GetRequiredService<AiSettingsService>().Status);
        services.AddTransient(sp => sp.GetRequiredService<AiSettingsService>().CreateProvider()!);
        return services.AddScoped<GenerateFromTextService>().AddScoped<DiagramToDocumentService>();
    }
}

/// <summary>How a provider's <see cref="HttpClient"/> is aimed: the endpoint (with a trailing slash so a relative path keeps its /v1) and the timeout.</summary>
internal static class AiHttpClients
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(300);

    public static void Configure(HttpClient http, AiEffective effective)
    {
        // Every provider's path is relative ("chat/completions", "messages", "api/chat"), so without the trailing
        // slash HttpClient's relative-URI merge would drop the endpoint's last segment (/v1).
        Uri endpoint = effective.Endpoint;
        http.BaseAddress = endpoint.AbsolutePath.EndsWith('/') ? endpoint : new UriBuilder(endpoint) { Path = endpoint.AbsolutePath + "/" }.Uri;
        http.Timeout = effective.TimeoutSeconds is > 0 ? TimeSpan.FromSeconds(effective.TimeoutSeconds.Value) : DefaultTimeout;
    }
}
