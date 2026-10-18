using Draughtsman.Server.Ai;
using Draughtsman.Server.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// <c>ai.endpoint</c> in draughtsman.yaml is where the AI key is sent, so it is held to what the admin AI page already
/// requires, not just "an absolute URI": http or https only, no credentials in the URL, no fragment. A refusal never
/// repeats the endpoint (it may carry a password). Who may point it where (the AU-11 policy, docs/ai-endpoint-policy.md) is
/// <see cref="AiEndpointPolicyTests"/> and <see cref="AiEndpointPolicyEndpointTests"/>; the cases here pick endpoints the policy allows.
/// </summary>
public class AiEndpointConfigTests
{
    private static AiResolution Resolve(string endpoint, string provider = "openai")
    {
        return AiSettingsResolver.Resolve(new AiConfig { Provider = provider, Model = "m", Endpoint = endpoint }, AiOverrides.None);
    }

    [Theory]
    [InlineData("ftp://files.example.com/v1", "http or https")]
    [InlineData("file:///etc/passwd", "http or https")]
    [InlineData("javascript:alert(1)", "http or https")]
    [InlineData("ws://llm.example.com/v1", "http or https")]
    [InlineData("https://user:hunter2@api.example.com/v1", "user name or password")]
    [InlineData("https://hunter2@api.example.com/v1", "user name or password")]
    [InlineData("https://api.example.com/v1#hunter2", "fragment")]
    public void An_endpoint_that_is_not_plain_http_or_https_or_carries_credentials_or_a_fragment_is_unusable(string endpoint, string reason)
    {
        AiResolution resolution = Resolve(endpoint);

        Assert.Null(resolution.Effective);
        Assert.NotNull(resolution.Problem);
        Assert.Contains(reason, resolution.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", resolution.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", resolution.Problem, StringComparison.Ordinal);
    }

    // Intentional change (AU-11): each endpoint is paired with a provider the policy lets use it. Plain http to this machine or a private
    // address is Ollama's case; a keyed provider is held to its published host unless the file lists the host.
    [Theory]
    [InlineData("ollama", "http://localhost:11434")]
    [InlineData("ollama", "http://192.168.1.20:8000/v1")]
    [InlineData("openai", "https://api.openai.com/v1")]
    [InlineData("azure-openai", "https://my-resource.openai.azure.com")]
    public void A_plain_http_or_https_endpoint_is_still_used_as_given(string provider, string endpoint)
    {
        AiResolution resolution = Resolve(endpoint, provider);

        Assert.Null(resolution.Problem);
        Assert.NotNull(resolution.Effective);
        Assert.Equal(new Uri(endpoint), resolution.Effective.Endpoint);
    }

    [Fact]
    public void A_bad_endpoint_in_the_file_is_not_usable_even_through_the_provider_override_path_that_inherits_it()
    {
        // The file's endpoint applies to the file's own provider only; for that provider it is still refused.
        AiResolution inherited = AiSettingsResolver.Resolve(new AiConfig { Provider = "openai", Model = "m", Endpoint = "ftp://x.example.com" }, new AiOverrides(null, "other-model", null, null));
        Assert.Null(inherited.Effective);

        // An override that supplies a good one wins, as it always did.
        // (Intentional change, AU-11: a keyed provider's other host has to be listed in the file.)
        AiResolution overridden = AiSettingsResolver.Resolve(new AiConfig { Provider = "openai", Model = "m", Endpoint = "ftp://x.example.com", AllowedEndpointHosts = ["good.example.com"] }, new AiOverrides(null, null, "https://good.example.com/v1", null));
        Assert.NotNull(overridden.Effective);
    }

    [Fact]
    public void A_server_started_with_such_an_endpoint_hides_AI_and_names_the_problem_without_the_secret()
    {
        var log = new KeyRingTestSupport.CapturingLogger();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAi(new AiConfig { Provider = "openai", Model = "m", Endpoint = "https://admin:hunter2@evil.example.com/v1" }, log);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.False(provider.GetRequiredService<AiStatus>().Enabled);
        Assert.Contains("must not contain a user name or password", log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", log.All, StringComparison.Ordinal);

        // The opposite: a plain endpoint is enabled.
        var good = new ServiceCollection();
        good.AddLogging();
        good.AddAi(new AiConfig { Provider = "openai", Model = "m", Endpoint = "https://api.openai.com/v1" }, new KeyRingTestSupport.CapturingLogger());
        using ServiceProvider goodProvider = good.BuildServiceProvider();
        Assert.True(goodProvider.GetRequiredService<AiStatus>().Enabled);
    }
}
