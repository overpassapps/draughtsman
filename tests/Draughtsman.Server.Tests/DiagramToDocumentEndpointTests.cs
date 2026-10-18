using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Draughtsman.Ai;
using Draughtsman.Server.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// POST /api/ai/diagram-to-document against a fake IModelProvider (never a real Ollama call): covers the outline-only
/// prompt (the privacy rule), the per-output-kind prompt, that reading writes nothing, and the AI-off/hidden state
/// (build-spec-v0.1.md section 7/9, design/atlas/ai-panel.md). The live Ollama smoke test is separate and manual.
/// </summary>
public class DiagramToDocumentEndpointTests
{
    private const string OutlineConfigYaml = "ai:\n  provider: ollama\n  model: fake-model\n  endpoint: \"http://localhost:1\"\n";

    [Fact]
    public async Task Diagram_is_404_when_no_ai_provider_is_configured()
    {
        using var factory = new AiServerFactory(configYaml: null);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/diagram-to-document",
            new { documentId = "doc_x", outputKind = "design-doc" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Two-sided privacy: the outline (ids, kinds, labels) must reach the model, and the geometry and style that only
    // the JSON/DSL forms carry must not - the same outline every read_document format=outline shows.
    [Fact]
    public async Task Diagram_sends_only_the_outline_never_geometry_or_style()
    {
        string? capturedSystemPrompt = null;
        var fake = new FakeModelProvider((system, _) =>
        {
            capturedSystemPrompt = system;
            return Task.FromResult("# Design\n\nThe cart [n_cart] hands off to payment [n_pay].\n");
        });
        using var factory = new AiServerFactory(OutlineConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = await CreateDocument(client);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/diagram-to-document",
            new { documentId = id, outputKind = "design-doc" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(capturedSystemPrompt);
        // Present: the outline's ids, kinds and labels.
        Assert.Contains("n_cart", capturedSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Payment service", capturedSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("[process]", capturedSystemPrompt, StringComparison.Ordinal);
        // Absent: the position this node has and the fill colour it was given. A prompt built from the JSON or DSL
        // form would contain both, so their absence is what pins this to the outline form.
        Assert.DoesNotContain("1234", capturedSystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("5678", capturedSystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("#abcdef", capturedSystemPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("design-doc", "design document")]
    [InlineData("runbook", "runbook")]
    [InlineData("explanation", "plain-language explanation")]
    public async Task Diagram_sends_a_prompt_for_each_output_kind(string outputKind, string expectedInstruction)
    {
        string? capturedSystemPrompt = null;
        var fake = new FakeModelProvider((system, _) =>
        {
            capturedSystemPrompt = system;
            return Task.FromResult("# Draft\n");
        });
        using var factory = new AiServerFactory(OutlineConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = await CreateDocument(client);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/diagram-to-document",
            new { documentId = id, outputKind }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(capturedSystemPrompt);
        Assert.Contains(expectedInstruction, capturedSystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diagram_returns_the_models_markdown_and_the_provenance()
    {
        const string markdown = "# Payment runbook\n\n1. Drain the queue [n_pay].";
        var fake = new FakeModelProvider((_, _) => Task.FromResult(markdown + "\n"));
        using var factory = new AiServerFactory(OutlineConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = await CreateDocument(client);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/diagram-to-document",
            new { documentId = id, outputKind = "runbook" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        Assert.Equal(markdown, body["markdown"]!.GetValue<string>());
        Assert.Equal("ollama", body["provenance"]!["provider"]!.GetValue<string>());
        Assert.Equal("fake-model", body["provenance"]!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Diagram_writes_nothing_to_the_document()
    {
        var fake = new FakeModelProvider((_, _) => Task.FromResult("# Draft\n"));
        using var factory = new AiServerFactory(OutlineConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = await CreateDocument(client);

        HttpResponseMessage before = await client.GetAsync($"/api/documents/{id}", CancellationToken.None);
        string? etagBefore = before.Headers.ETag?.Tag;

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/diagram-to-document",
            new { documentId = id, outputKind = "explanation" }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        HttpResponseMessage after = await client.GetAsync($"/api/documents/{id}", CancellationToken.None);
        // Two-sided: the version tag is present on both reads (so an all-null comparison cannot pass this), and it
        // is unchanged by a request that only reads.
        Assert.False(string.IsNullOrEmpty(etagBefore));
        Assert.Equal(etagBefore, after.Headers.ETag?.Tag);
        JsonNode document = await ReadJson(after);
        Assert.Null(document["meta"]!["aiProvenance"]);
    }

    [Fact]
    public async Task Diagram_reports_502_when_the_model_is_unreachable()
    {
        var fake = new FakeModelProvider((_, _) => throw new ModelProviderException("connection refused", reachable: false));
        using var factory = new AiServerFactory(OutlineConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = await CreateDocument(client);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/diagram-to-document",
            new { documentId = id, outputKind = "design-doc" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        JsonNode problem = await ReadJson(response);
        Assert.Equal("The AI provider is unreachable", problem["title"]!.GetValue<string>());
        Assert.Equal("connection refused", problem["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Diagram_with_an_unknown_documentId_is_404()
    {
        var fake = new FakeModelProvider((_, _) => Task.FromResult("# Draft\n"));
        using var factory = new AiServerFactory(OutlineConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/diagram-to-document",
            new { documentId = "doc_missing", outputKind = "design-doc" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Diagram_rejects_an_unknown_output_kind()
    {
        var fake = new FakeModelProvider((_, _) => Task.FromResult("# Draft\n"));
        using var factory = new AiServerFactory(OutlineConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = await CreateDocument(client);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/diagram-to-document",
            new { documentId = id, outputKind = "poem" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A flowchart with a positioned, styled node: the geometry and style that must not reach the prompt.</summary>
    private static async Task<string> CreateDocument(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Checkout",
            type = "flowchart",
            nodes = new object[]
            {
                new { id = "n_cart", kind = "process", label = "Cart", shape = "rounded", geometry = new { x = 1234, y = 5678, w = 160, h = 80 }, style = new { fill = "#abcdef" } },
                new { id = "n_pay", kind = "service", label = "Payment service", shape = "rounded", geometry = new { x = 1500, y = 5678, w = 160, h = 80 } },
            },
            edges = new object[]
            {
                new { id = "e_1", kind = "flow", from = new { node = "n_cart" }, to = new { node = "n_pay" } },
            },
        }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await ReadJson(created))["id"]!.GetValue<string>();
    }

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        JsonNode? node = JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.NotNull(node);
        return node!;
    }

    /// <summary>A fake IModelProvider swapped in over the real (ollama-configured) DI registration, so these tests never touch the network.</summary>
    private sealed class FakeModelProvider(Func<string, IReadOnlyList<ModelMessage>, Task<string>> respond) : IModelProvider
    {
        public string Provider => "ollama";

        public string Model => "fake-model";

        public Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ModelMessage> messages, CancellationToken cancellationToken = default)
        {
            return respond(systemPrompt, messages);
        }
    }

    /// <summary>A server with an optional draughtsman.yaml and an optional fake IModelProvider swapped in over whatever ai.provider registers for real.</summary>
    private sealed class AiServerFactory : DraughtsmanServerFactory
    {
        private readonly IModelProvider? _fakeProvider;

        public AiServerFactory(string? configYaml, IModelProvider? fakeProvider = null)
        {
            Directory.CreateDirectory(DataFolder);
            if (configYaml is not null)
            {
                File.WriteAllText(Path.Combine(DataFolder, DraughtsmanConfig.FileName), configYaml, Encoding.UTF8);
            }

            _fakeProvider = fakeProvider;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (_fakeProvider is not null)
            {
                IModelProvider provider = _fakeProvider;
                builder.ConfigureTestServices(services => services.AddScoped<IModelProvider>(_ => provider));
            }
        }
    }
}
