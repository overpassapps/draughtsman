using System.Net.Http;
using Draughtsman.Ai;
using Draughtsman.Server.Ai;
using Draughtsman.Server.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

public class AiRegistrationTests
{
    private IServiceProvider CreateProvider(AiConfig config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddAi(config, NullLogger<object>.Instance);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Ollama_timeout_is_300s_by_default()
    {
        var config = new AiConfig { Provider = "ollama", Model = "llama3.1" };
        var sp = CreateProvider(config);
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("ollama-ai-provider");

        Assert.Equal(TimeSpan.FromSeconds(300), client.Timeout);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(600)]
    public void Ollama_timeout_is_as_configured(double seconds)
    {
        var config = new AiConfig { Provider = "ollama", Model = "llama3.1", TimeoutSeconds = seconds };
        var sp = CreateProvider(config);
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("ollama-ai-provider");

        Assert.Equal(TimeSpan.FromSeconds(seconds), client.Timeout);
    }

    [Fact]
    public async Task Ollama_think_setting_reaches_request_body()
    {
        // We need a fake handler to capture the request
        var handler = new FakeHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("ollama-ai-provider", c => { })
                .AddHttpMessageHandler(() => handler);

        // To ensure AiServiceRegistration doesn't overwrite our handler, we have to be careful.
        // The registration uses services.AddHttpClient(httpClientName, ...), which adds a 
        // configuration action. Our .AddHttpMessageHandler adds to the pipeline.

        var config = new AiConfig { Provider = "ollama", Model = "llama3.1", Think = true };
        services.AddAi(config, NullLogger<object>.Instance);

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IModelProvider>();

        // Mock the response so it doesn't fail
        handler.Response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"done\":true}")
        };

        await provider.CompleteAsync("sys", [new ModelMessage(ModelRole.User, "hi")]);

        var request = handler.CapturedRequest;
        Assert.NotNull(request);
        string body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"think\":true", body);
    }

    [Fact]
    public void Openai_base_address_defaults_to_api_openai_v1_with_a_trailing_slash()
    {
        var config = new AiConfig { Provider = "openai", Model = "gpt-4o-mini" };
        var sp = CreateProvider(config);
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("openai-ai-provider");

        // Must keep the trailing slash: HttpClient would otherwise drop the /v1 segment when merging the relative
        // "chat/completions" path.
        Assert.Equal(new Uri("https://api.openai.com/v1/"), client.BaseAddress);
    }

    [Fact]
    public void Openai_configured_endpoint_gets_a_trailing_slash()
    {
        // Intentional change (AU-11): a keyed provider may use another host only when the file lists it.
        var config = new AiConfig { Provider = "openai", Model = "gpt-4o-mini", Endpoint = "http://localhost:8080/v1", AllowedEndpointHosts = ["localhost"] };
        var sp = CreateProvider(config);
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("openai-ai-provider");

        Assert.Equal(new Uri("http://localhost:8080/v1/"), client.BaseAddress);
    }

    [Fact]
    public void Openai_timeout_is_300s_by_default()
    {
        var config = new AiConfig { Provider = "openai", Model = "gpt-4o-mini" };
        var sp = CreateProvider(config);
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("openai-ai-provider");

        Assert.Equal(TimeSpan.FromSeconds(300), client.Timeout);
    }

    [Fact]
    public void Openai_registers_an_openai_model_provider()
    {
        var config = new AiConfig { Provider = "openai", Model = "gpt-4o-mini" };
        var sp = CreateProvider(config);

        var provider = sp.GetRequiredService<IModelProvider>();

        Assert.Equal("openai", provider.Provider);
        Assert.Equal("gpt-4o-mini", provider.Model);
    }

    [Fact]
    public void Anthropic_base_address_defaults_to_api_anthropic_v1_with_a_trailing_slash()
    {
        var config = new AiConfig { Provider = "anthropic", Model = "claude-3-5-sonnet-latest" };
        var sp = CreateProvider(config);
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("anthropic-ai-provider");

        // Must keep the trailing slash so "messages" merges under /v1 rather than replacing it.
        Assert.Equal(new Uri("https://api.anthropic.com/v1/"), client.BaseAddress);
    }

    [Fact]
    public void Anthropic_configured_endpoint_gets_a_trailing_slash()
    {
        // Intentional change (AU-11): a keyed provider may use another host only when the file lists it.
        var config = new AiConfig { Provider = "anthropic", Model = "claude-3-5-sonnet-latest", Endpoint = "https://anthropic-proxy.internal/v1", AllowedEndpointHosts = ["anthropic-proxy.internal"] };
        var sp = CreateProvider(config);
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("anthropic-ai-provider");

        Assert.Equal(new Uri("https://anthropic-proxy.internal/v1/"), client.BaseAddress);
    }

    [Fact]
    public void Anthropic_timeout_is_300s_by_default()
    {
        var config = new AiConfig { Provider = "anthropic", Model = "claude-3-5-sonnet-latest" };
        var sp = CreateProvider(config);
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("anthropic-ai-provider");

        Assert.Equal(TimeSpan.FromSeconds(300), client.Timeout);
    }

    [Fact]
    public void Anthropic_registers_an_anthropic_model_provider()
    {
        var config = new AiConfig { Provider = "anthropic", Model = "claude-3-5-sonnet-latest" };
        var sp = CreateProvider(config);

        var provider = sp.GetRequiredService<IModelProvider>();

        Assert.Equal("anthropic", provider.Provider);
        Assert.Equal("claude-3-5-sonnet-latest", provider.Model);
    }

    [Fact]
    public void Azure_openai_registers_an_azure_model_provider_with_the_deployment_as_its_model()
    {
        var config = new AiConfig { Provider = "azure-openai", Model = "my-deployment", Endpoint = "https://my-resource.openai.azure.com" };
        var sp = CreateProvider(config);

        var provider = sp.GetRequiredService<IModelProvider>();

        Assert.Equal("azure-openai", provider.Provider);
        Assert.Equal("my-deployment", provider.Model);
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        Assert.Equal(new Uri("https://my-resource.openai.azure.com/"), factory.CreateClient("azure-openai-ai-provider").BaseAddress);
    }

    [Fact]
    public void Azure_openai_accepts_the_documented_azureOpenAI_spelling()
    {
        var config = new AiConfig { Provider = "azureOpenAI", Model = "my-deployment", Endpoint = "https://my-resource.openai.azure.com" };
        var sp = CreateProvider(config);

        Assert.Equal("azure-openai", sp.GetRequiredService<IModelProvider>().Provider);
    }

    [Fact]
    public void Azure_openai_without_an_endpoint_hides_ai_rather_than_guessing_a_host()
    {
        var config = new AiConfig { Provider = "azure-openai", Model = "my-deployment" };
        var sp = CreateProvider(config);

        Assert.Null(sp.GetService<IModelProvider>());
        Assert.False(sp.GetRequiredService<AiStatus>().Enabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-01-01-preview")]
    public async Task Azure_openai_sends_the_configured_or_default_api_version_in_the_request_url(string? configuredVersion)
    {
        const string keyVariable = "DRAUGHTSMAN_TEST_AZURE_REGISTRATION_KEY";
        Environment.SetEnvironmentVariable(keyVariable, "registration-test-key");
        try
        {
            var handler = new FakeHandler
            {
                Response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"ok"}}]}""", System.Text.Encoding.UTF8, "application/json"),
                },
            };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpClient("azure-openai-ai-provider").AddHttpMessageHandler(() => handler);
            services.AddAi(
                new AiConfig
                {
                    Provider = "azure-openai",
                    Model = "my-deployment",
                    Endpoint = "https://my-resource.openai.azure.com",
                    ApiKeyEnvironmentVariable = keyVariable,
                    ApiVersion = configuredVersion ?? string.Empty,
                },
                NullLogger<object>.Instance);

            var provider = services.BuildServiceProvider().GetRequiredService<IModelProvider>();
            await provider.CompleteAsync("system", [new ModelMessage(ModelRole.User, "hi")]);

            // Two-sided: an unset ai.apiVersion must fall back to the provider default, not an empty query value.
            string expectedVersion = configuredVersion ?? "2024-10-21";
            Assert.Equal(
                $"https://my-resource.openai.azure.com/openai/deployments/my-deployment/chat/completions?api-version={expectedVersion}",
                handler.CapturedRequest!.RequestUri!.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyVariable, null);
        }
    }

    private sealed class FakeHandler : DelegatingHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }
        public HttpResponseMessage Response { get; set; } = new(System.Net.HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Response;
        }
    }
}
