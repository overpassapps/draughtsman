using System.Globalization;
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
/// POST /api/ai/generate-from-text against a fake IModelProvider (never a real Ollama call): covers the prompt the
/// service sends, the parse-retry-once path, aiProvenance naming the provider and model, and the AI-off/hidden
/// state (build-spec-v0.1.md section 7/9, design/atlas/ai-panel.md). The live Ollama smoke test is separate and
/// manual (README's AI section), because it needs a real local model.
/// </summary>
public class GenerateFromTextEndpointTests
{
    private const string ValidFlowchartDsl = "draughtsman 1\ntype flowchart\nnode n_start process \"Start\"\nnode n_end process \"End\"\nedge e_1 flow n_start -> n_end\n";

    [Fact]
    public async Task Generate_is_404_when_no_ai_provider_is_configured()
    {
        using var factory = new AiServerFactory(configYaml: null);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { description = "a login flow", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_reports_ai_disabled_when_no_ai_provider_is_configured()
    {
        using var factory = new AiServerFactory(configYaml: null);
        HttpClient client = await factory.CreateAuthenticatedClient();

        JsonNode health = await ReadJson(await client.GetAsync("/api/health", CancellationToken.None));

        Assert.False(health["ai"]!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Health_reports_ai_enabled_and_the_provider_when_configured()
    {
        using var factory = new AiServerFactory(OllamaConfigYaml, new FakeModelProvider((_, _) => Task.FromResult(ValidFlowchartDsl)));
        HttpClient client = await factory.CreateAuthenticatedClient();

        JsonNode health = await ReadJson(await client.GetAsync("/api/health", CancellationToken.None));

        // Two-sided with the disabled case above: the same field must flip, not just exist.
        Assert.True(health["ai"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("ollama", health["ai"]!["provider"]!.GetValue<string>());
    }

    [Fact]
    public async Task Generate_sends_a_system_prompt_naming_the_document_type_and_the_description_as_a_user_message()
    {
        string? capturedSystemPrompt = null;
        IReadOnlyList<ModelMessage>? capturedMessages = null;
        var fake = new FakeModelProvider((system, messages) =>
        {
            capturedSystemPrompt = system;
            capturedMessages = messages;
            return Task.FromResult(ValidFlowchartDsl);
        });
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { description = "a login flow with two steps", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("flowchart", capturedSystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("draughtsman 1", capturedSystemPrompt, StringComparison.Ordinal);
        ModelMessage userMessage = Assert.Single(capturedMessages!);
        Assert.Equal(ModelRole.User, userMessage.Role);
        Assert.Equal("a login flow with two steps", userMessage.Content);
    }

    [Fact]
    public async Task Generate_creates_a_document_carrying_aiProvenance_naming_the_provider_and_model()
    {
        var fake = new FakeModelProvider((_, _) => Task.FromResult(ValidFlowchartDsl));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { description = "a login flow", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        Assert.Equal("ollama", body["provenance"]!["provider"]!.GetValue<string>());
        Assert.Equal("fake-model", body["provenance"]!["model"]!.GetValue<string>());

        JsonArray provenance = body["document"]!["meta"]!["aiProvenance"]!.AsArray();
        JsonNode entry = Assert.Single(provenance)!;
        Assert.Equal("ollama", entry["agent"]!.GetValue<string>());
        Assert.Equal("fake-model", entry["model"]!.GetValue<string>());
        Assert.Equal("created", entry["action"]!.GetValue<string>());

        // The write went through the checkpointed create_document path: the document is readable back by id.
        string id = body["document"]!["id"]!.GetValue<string>();
        HttpResponseMessage read = await client.GetAsync($"/api/documents/{id}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Generate_retries_once_with_the_parse_error_and_succeeds_on_the_corrected_attempt()
    {
        int calls = 0;
        var fake = new FakeModelProvider((_, messages) =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult("this is not draughtsman dsl at all");
            }

            // The retry call must carry the failed attempt and the parser's complaint, or a real model would have
            // nothing to correct against.
            string lastUserMessage = messages[^1].Content;
            Assert.Contains("did not parse", lastUserMessage, StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(ValidFlowchartDsl);
        });
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { description = "a login flow", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Generate_reports_422_when_both_attempts_fail_to_parse()
    {
        int calls = 0;
        var fake = new FakeModelProvider((_, _) =>
        {
            calls++;
            return Task.FromResult("still not dsl");
        });
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { description = "a login flow", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        // Fails cleanly: exactly the one retry (not a loop), and a structured body naming both attempts' errors
        // rather than a bare 422 with no detail.
        Assert.Equal(2, calls);
        JsonNode problem = await ReadJson(response);
        string detail = problem["detail"]!.GetValue<string>();
        Assert.Contains("First attempt:", detail, StringComparison.Ordinal);
        Assert.Contains("Retry:", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_reports_502_when_the_model_is_unreachable()
    {
        var fake = new FakeModelProvider((_, _) => throw new ModelProviderException("connection refused", reachable: false));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { description = "a login flow", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        JsonNode problem = await ReadJson(response);
        Assert.Equal("The AI provider is unreachable", problem["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Generate_with_a_documentId_sends_the_documents_full_dsl_including_geometry_and_applies_the_reply_as_a_diff()
    {
        string? capturedSystemPrompt = null;
        string? capturedUserMessage = null;
        const string reply = "draughtsman 1\ntype flowchart\nnode n_existing process \"Existing node\" @ 100,200\nnode n_new process \"New node\"\nedge e_new flow n_existing -> n_new \"goes to\"\n";
        var fake = new FakeModelProvider((system, messages) =>
        {
            capturedSystemPrompt = system;
            capturedUserMessage = messages[0].Content;
            return Task.FromResult(reply);
        });
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Existing",
            type = "flowchart",
            nodes = new[]
            {
                new { id = "n_existing", kind = "process", label = "Existing node", shape = "rounded", geometry = new { x = 100, y = 200, w = 160, h = 80 } },
            },
            edges = Array.Empty<object>(),
        }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string id = (await ReadJson(created))["id"]!.GetValue<string>();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = id, description = "add a login step", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        Assert.Equal(id, body["document"]!["id"]!.GetValue<string>());
        // The existing node stayed (kept its geometry, since nothing asked to move it), and the generated ones were
        // added: patch (a diff), not a whole-document replace.
        JsonArray nodes = body["document"]!["nodes"]!.AsArray();
        JsonNode existingAfter = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "n_existing")!;
        Assert.Equal(100, existingAfter["geometry"]!["x"]!.GetValue<double>());
        Assert.Equal(200, existingAfter["geometry"]!["y"]!.GetValue<double>());
        Assert.Contains(nodes, n => n!["id"]!.GetValue<string>() == "n_new");
        Assert.Equal(["n_new"], body["addedNodeIds"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(0, body["changedElementCount"]!.GetValue<int>());
        Assert.Equal(0, body["removedElementCount"]!.GetValue<int>());
        // Privacy (build-spec-v0.1.md section 9): update mode sends the document's own whole DSL, not the
        // outline the old add-only prompt sent - two-sided against that: geometry, absent from the outline form,
        // must actually be present here.
        Assert.Contains("\"Existing node\" @ 100,200", capturedSystemPrompt, StringComparison.Ordinal);
        // The user message itself stays the person's plain ask; the document content lives in the system prompt.
        Assert.Equal("add a login step", capturedUserMessage);
    }

    /// <summary>
    /// The camera path is a presentation, not something a model rewrites: the prompt shows it (it is part of the document's
    /// DSL), and a reply that drops or mangles its keyframe lines must leave the author's tour exactly as it was.
    /// </summary>
    [Theory]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_existing process \"Existing node\" @ 100,200\nnode n_new process \"New node\"\n")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_existing process \"Existing node\" @ 100,200\nnode n_new process \"New node\"\nkeyframe k_1 @ 0,0 10x10 caption=\"The model rewrote this\"\n")]
    public async Task Generate_with_a_documentId_leaves_the_flythrough_exactly_as_it_was_whatever_the_reply_says_about_it(string reply)
    {
        string? prompt = null;
        var fake = new FakeModelProvider((system, _) =>
        {
            prompt = system;
            return Task.FromResult(reply);
        });
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        var flythrough = new { keyframes = new object[] { new { id = "k_keep", view = new { x = 0, y = 0, w = 400, h = 250 }, caption = "Author wrote this" } }, loop = true };
        var created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Existing",
            type = "flowchart",
            nodes = new[] { new { id = "n_existing", kind = "process", label = "Existing node", shape = "rounded", geometry = new { x = 100, y = 200, w = 160, h = 80 } } },
            edges = Array.Empty<object>(),
            flythrough,
        }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string id = (await ReadJson(created))["id"]!.GetValue<string>();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = id, description = "add a step", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode document = (await ReadJson(response))["document"]!;
        Assert.Contains(document["nodes"]!.AsArray(), n => n!["id"]!.GetValue<string>() == "n_new");
        JsonNode tour = document["flythrough"]!;
        Assert.Equal(["k_keep"], tour["keyframes"]!.AsArray().Select(k => k!["id"]!.GetValue<string>()));
        Assert.Equal("Author wrote this", tour["keyframes"]![0]!["caption"]!.GetValue<string>());
        Assert.True(tour["loop"]!.GetValue<bool>());
        // The model was shown the tour as part of the document's own DSL, so it can keep it.
        Assert.Contains("keyframe k_keep @ 0,0 400x250 caption=\"Author wrote this\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_with_a_documentId_can_group_existing_nodes_into_a_new_container_that_encloses_them()
    {
        const string reply = "draughtsman 1\ntype flowchart\nnode n_a process \"Service A\" @ 0,0\nnode n_b process \"Service B\" @ 300,0\ncontainer c_group \"Group\" { n_a n_b }\n";
        var fake = new FakeModelProvider((_, _) => Task.FromResult(reply));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Existing",
            type = "flowchart",
            nodes = new[]
            {
                new { id = "n_a", kind = "process", label = "Service A", shape = "rounded", geometry = new { x = 0, y = 0, w = 160, h = 80 } },
                new { id = "n_b", kind = "process", label = "Service B", shape = "rounded", geometry = new { x = 300, y = 0, w = 160, h = 80 } },
            },
            edges = Array.Empty<object>(),
        }, CancellationToken.None);
        string id = (await ReadJson(created))["id"]!.GetValue<string>();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = id, description = "group the services into a container", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        JsonArray nodes = body["document"]!["nodes"]!.AsArray();
        JsonNode container = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "c_group")!;
        Assert.Equal("container", container["kind"]!.GetValue<string>());
        JsonNode a = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "n_a")!;
        JsonNode b = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "n_b")!;
        Assert.Equal("c_group", a["parent"]!.GetValue<string>());
        Assert.Equal("c_group", b["parent"]!.GetValue<string>());
        // The container was given no geometry of its own (the reply left it out, as instructed): it must still be
        // fitted so it actually encloses both children, not left geometry-less or placed anywhere that would not.
        JsonNode geometry = container["geometry"]!;
        double x = geometry["x"]!.GetValue<double>();
        double y = geometry["y"]!.GetValue<double>();
        double w = geometry["w"]!.GetValue<double>();
        double h = geometry["h"]!.GetValue<double>();
        Assert.True(x <= 0 && y <= 0, "the container's box must start at or before its children's top-left corner");
        Assert.True(x + w >= 460 && y + h >= 80, "the container's box must reach at or past its children's bottom-right corner");
        Assert.Equal(["c_group"], body["addedNodeIds"]!.AsArray().Select(n => n!.GetValue<string>()));
        // Reparenting n_a and n_b is a change to each, not an add or a remove.
        Assert.Equal(2, body["changedElementCount"]!.GetValue<int>());
        Assert.Equal(0, body["removedElementCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Generate_with_a_documentId_grows_an_existing_container_that_gains_a_child()
    {
        // The container starts wider and taller than it needs to be (a hand-drawn oversized box), so a shrink-to-fit
        // implementation would visibly fail the no-shrink assertions as well as a no-fit one.
        const string reply = "draughtsman 1\ntype flowchart\nnode n_in process \"In\" @ 20,20 160x80\nnode n_out process \"Out\" @ 400,0 160x80\ncontainer c_box \"Box\" @ -200,-200 400x500 { n_in n_out }\n";
        var fake = new FakeModelProvider((_, _) => Task.FromResult(reply));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Existing",
            type = "flowchart",
            nodes = new object[]
            {
                new { id = "c_box", kind = "container", label = "Box", shape = "rectangle", geometry = new { x = -200, y = -200, w = 400, h = 500 } },
                new { id = "n_in", kind = "process", label = "In", shape = "rounded", geometry = new { x = 20, y = 20, w = 160, h = 80 }, parent = "c_box" },
                new { id = "n_out", kind = "process", label = "Out", shape = "rounded", geometry = new { x = 400, y = 0, w = 160, h = 80 } },
            },
            edges = Array.Empty<object>(),
        }, CancellationToken.None);
        string id = (await ReadJson(created))["id"]!.GetValue<string>();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = id, description = "move Out into the box", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        JsonArray nodes = body["document"]!["nodes"]!.AsArray();
        JsonNode container = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "c_box")!;
        JsonNode geometry = container["geometry"]!;
        double x = geometry["x"]!.GetValue<double>();
        double y = geometry["y"]!.GetValue<double>();
        double w = geometry["w"]!.GetValue<double>();
        double h = geometry["h"]!.GetValue<double>();
        // Grew on the side the new child needed: it must reach past n_out's padded bottom-right corner.
        Assert.True(x <= 400 - 40, "the container must contain n_out with padding on the left");
        Assert.True(x + w >= 560 + 40, "the container must contain n_out with padding on the right");
        Assert.True(y <= 0 - 40, "the container must contain n_out with padding above");
        Assert.True(y + h >= 100 + 40, "the container must contain n_out with padding below");
        // ...and did not shrink its already-oversized left/top edges.
        Assert.True(x <= -200, "growing a container must never shrink it");
        Assert.True(y <= -200, "growing a container must never shrink it");
        // The two shapes the reply did not move kept their geometry exactly.
        JsonNode inNode = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "n_in")!;
        JsonNode outNode = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "n_out")!;
        AssertGeometry(inNode["geometry"]!, 20, 20, 160, 80);
        AssertGeometry(outNode["geometry"]!, 400, 0, 160, 80);
        Assert.Equal("c_box", outNode["parent"]!.GetValue<string>());
        // Growing the container is itself a change; reparenting n_out is the other.
        Assert.Equal(2, body["changedElementCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Generate_with_a_documentId_places_a_new_connected_node_beside_its_neighbour_without_overlapping()
    {
        const string reply = "draughtsman 1\ntype flowchart\nnode n_existing process \"Existing\" @ 100,200 160x80\nnode n_new process \"New\"\nedge e_new flow n_existing -> n_new\n";
        var fake = new FakeModelProvider((_, _) => Task.FromResult(reply));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Existing",
            type = "flowchart",
            nodes = new[]
            {
                new { id = "n_existing", kind = "process", label = "Existing", shape = "rounded", geometry = new { x = 100, y = 200, w = 160, h = 80 } },
            },
            edges = Array.Empty<object>(),
        }, CancellationToken.None);
        string id = (await ReadJson(created))["id"]!.GetValue<string>();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = id, description = "add a following step", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        JsonArray nodes = body["document"]!["nodes"]!.AsArray();
        JsonNode existing = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "n_existing")!;
        JsonNode added = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "n_new")!;
        // The new node must actually have been placed, not left geometry-less (which is how it used to land on top
        // of whatever was already there).
        Assert.NotNull(added["geometry"]);
        Assert.False(Overlaps(added["geometry"]!, existing["geometry"]!));
        // ...and near its neighbour, not banished to some far corner.
        Assert.True(Math.Abs(added["geometry"]!["x"]!.GetValue<double>() - 100) <= 400, "the new node must land near the shape it connects to");
        Assert.True(Math.Abs(added["geometry"]!["y"]!.GetValue<double>() - 200) <= 400, "the new node must land near the shape it connects to");
        // The shape the reply did not touch kept its geometry exactly.
        AssertGeometry(existing["geometry"]!, 100, 200, 160, 80);
    }

    [Fact]
    public async Task Generate_with_a_documentId_places_a_new_unconnected_node_beside_the_diagram_without_overlapping()
    {
        const string reply = "draughtsman 1\ntype flowchart\nnode n_a process \"A\" @ 0,0 160x80\nnode n_b process \"B\" @ 300,0 160x80\nnode n_free process \"Free\"\n";
        var fake = new FakeModelProvider((_, _) => Task.FromResult(reply));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Existing",
            type = "flowchart",
            nodes = new[]
            {
                new { id = "n_a", kind = "process", label = "A", shape = "rounded", geometry = new { x = 0, y = 0, w = 160, h = 80 } },
                new { id = "n_b", kind = "process", label = "B", shape = "rounded", geometry = new { x = 300, y = 0, w = 160, h = 80 } },
            },
            edges = Array.Empty<object>(),
        }, CancellationToken.None);
        string id = (await ReadJson(created))["id"]!.GetValue<string>();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = id, description = "add an unconnected note", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        JsonArray nodes = body["document"]!["nodes"]!.AsArray();
        JsonNode a = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "n_a")!;
        JsonNode b = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "n_b")!;
        JsonNode free = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "n_free")!;
        Assert.NotNull(free["geometry"]);
        Assert.False(Overlaps(free["geometry"]!, a["geometry"]!));
        Assert.False(Overlaps(free["geometry"]!, b["geometry"]!));
        // Unconnected, so it goes beside the diagram's bounding box (right of n_b), not on top of it.
        Assert.True(free["geometry"]!["x"]!.GetValue<double>() >= 460, "an unconnected node must land clear of the diagram's bounding box");
        // The two shapes the reply did not touch kept their geometry exactly.
        AssertGeometry(a["geometry"]!, 0, 0, 160, 80);
        AssertGeometry(b["geometry"]!, 300, 0, 160, 80);
    }

    // S2: the reply into an empty or mostly replaced document is laid out like a create would, never walked into one long row.
    private const string BranchingReply =
        "draughtsman 1\ntype flowchart\n"
        + "node n_start process \"Start\"\nnode n_signin process \"Sign in\"\nnode n_mfa decision \"MFA ok?\"\n"
        + "node n_home process \"Home\"\nnode n_retry process \"Retry\"\nnode n_end process \"End\"\n"
        + "edge e_1 flow n_start -> n_signin\nedge e_2 flow n_signin -> n_mfa\nedge e_3 flow n_mfa -> n_home \"yes\"\n"
        + "edge e_4 flow n_mfa -> n_retry \"no\"\nedge e_5 flow n_retry -> n_signin\nedge e_6 flow n_home -> n_end\n";

    [Fact]
    public async Task Generate_into_an_empty_document_lays_the_new_nodes_out_in_two_dimensions_without_overlap()
    {
        var fake = new FakeModelProvider((_, _) => Task.FromResult(BranchingReply));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = (await ReadJson(await client.PostAsJsonAsync("/api/documents", new { title = "Empty", type = "flowchart", nodes = Array.Empty<object>(), edges = Array.Empty<object>() }, CancellationToken.None)))["id"]!.GetValue<string>();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = id, description = "sign in with mfa", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode generated = await ReadJson(response);
        List<JsonNode> boxes = [.. generated["document"]!["nodes"]!.AsArray().Select(n => n!["geometry"]!)];
        Assert.Equal(6, boxes.Count);
        // ELK's bend points are not stored: they would leave a diagonal stub where a connector attaches to a diamond's outline.
        Assert.All(generated["document"]!["edges"]!.AsArray(), e => Assert.Null(e!["routing"]?["waypoints"]));
        // A layered layout of a diagram with a branch and a loop spreads over both axes; a row (the old behaviour) has one y.
        Assert.True(boxes.Select(b => b["y"]!.GetValue<double>()).Distinct().Count() > 1, "the nodes must not sit in a single row");
        Assert.True(boxes.Select(b => b["x"]!.GetValue<double>()).Distinct().Count() > 1, "the nodes must not sit in a single column");
        for (int i = 0; i < boxes.Count; i++)
        {
            for (int j = i + 1; j < boxes.Count; j++)
            {
                Assert.False(Overlaps(boxes[i], boxes[j]), "laid out nodes must not overlap");
            }
        }
    }

    [Fact]
    public async Task Generate_that_mostly_replaces_the_document_lays_the_whole_diagram_out()
    {
        const string reply = BranchingReply + "node n_old process \"Old\" @ 5000,5000 160x80\n";
        var fake = new FakeModelProvider((_, _) => Task.FromResult(reply));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "One node",
            type = "flowchart",
            nodes = new[] { new { id = "n_old", kind = "process", label = "Old", shape = "rounded", geometry = new { x = 5000, y = 5000, w = 160, h = 80 } } },
            edges = Array.Empty<object>(),
        }, CancellationToken.None);
        string id = (await ReadJson(created))["id"]!.GetValue<string>();

        JsonNode body = await ReadJson(await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = id, description = "sign in with mfa", diagramType = "flowchart" }, CancellationToken.None));

        List<JsonNode> boxes = [.. body["document"]!["nodes"]!.AsArray().Select(n => n!["geometry"]!)];
        Assert.Equal(7, boxes.Count);
        // The whole diagram went through the layout (6 of 7 nodes were new), so nothing stays out at 5000,5000.
        Assert.All(boxes, b => Assert.True(b["x"]!.GetValue<double>() < 3000 && b["y"]!.GetValue<double>() < 3000));
    }

    [Fact]
    public async Task Generate_adding_a_few_connected_nodes_to_a_larger_diagram_lays_them_out_as_a_block_beside_it_and_keeps_the_rest()
    {
        string existingNodes = string.Join("\n", Enumerable.Range(0, 6).Select(i => $"node n_{i} process \"N{i}\" @ {i * 200},0 160x80"));
        string reply = "draughtsman 1\ntype flowchart\n" + existingNodes
            + "\nnode x_a process \"A\"\nnode x_b process \"B\"\nnode x_c process \"C\"\nnode x_d process \"D\"\n"
            + "edge e_x1 flow x_a -> x_b\nedge e_x2 flow x_a -> x_c\nedge e_x3 flow x_b -> x_d\nedge e_x4 flow x_c -> x_d\n";
        var fake = new FakeModelProvider((_, _) => Task.FromResult(reply));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Six nodes",
            type = "flowchart",
            nodes = Enumerable.Range(0, 6).Select(i => new { id = $"n_{i}", kind = "process", label = $"N{i}", shape = "rounded", geometry = new { x = i * 200, y = 0, w = 160, h = 80 } }).ToArray(),
            edges = Array.Empty<object>(),
        }, CancellationToken.None);
        string id = (await ReadJson(created))["id"]!.GetValue<string>();

        JsonNode body = await ReadJson(await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = id, description = "add a fan-out", diagramType = "flowchart" }, CancellationToken.None));

        JsonArray nodes = body["document"]!["nodes"]!.AsArray();
        for (int i = 0; i < 6; i++)
        {
            AssertGeometry(Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == $"n_{i}")!["geometry"]!, i * 200, 0, 160, 80);
        }

        List<JsonNode> added = [.. nodes.Where(n => n!["id"]!.GetValue<string>().StartsWith("x_", StringComparison.Ordinal)).Select(n => n!["geometry"]!)];
        Assert.Equal(4, added.Count);
        // Beside the existing diagram (its right edge is 1160), clear of it, and spread over more than one row.
        Assert.All(added, b => Assert.True(b["x"]!.GetValue<double>() >= 1160, "the block must sit clear of the existing diagram"));
        Assert.True(added.Select(b => b["y"]!.GetValue<double>()).Distinct().Count() > 1, "the block must not be a single row");
        Assert.Equal(0, body["changedElementCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task Generate_with_a_documentId_places_a_new_node_inside_the_existing_container_it_belongs_to()
    {
        const string reply = "draughtsman 1\ntype flowchart\nnode n_a process \"A\" @ 20,20 160x80\nnode n_new process \"New\"\ncontainer c_box \"Box\" @ 0,0 400x400 { n_a n_new }\n";
        var fake = new FakeModelProvider((_, _) => Task.FromResult(reply));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Existing",
            type = "flowchart",
            nodes = new object[]
            {
                new { id = "c_box", kind = "container", label = "Box", shape = "rectangle", geometry = new { x = 0, y = 0, w = 400, h = 400 } },
                new { id = "n_a", kind = "process", label = "A", shape = "rounded", geometry = new { x = 20, y = 20, w = 160, h = 80 }, parent = "c_box" },
            },
            edges = Array.Empty<object>(),
        }, CancellationToken.None);
        string id = (await ReadJson(created))["id"]!.GetValue<string>();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = id, description = "add a node to the box", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        JsonArray nodes = body["document"]!["nodes"]!.AsArray();
        JsonNode box = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "c_box")!;
        JsonNode a = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "n_a")!;
        JsonNode added = Assert.Single(nodes, n => n!["id"]!.GetValue<string>() == "n_new")!;
        Assert.NotNull(added["geometry"]);
        Assert.Equal("c_box", added["parent"]!.GetValue<string>());
        Assert.False(Overlaps(added["geometry"]!, a["geometry"]!));
        // It belongs to the container, so it must be placed inside the container's own box (not pushed outside by
        // that box counting as an obstacle). Checked against the container's ORIGINAL box, (0,0,400,400), because the
        // container is also grown to keep enclosing the placed children and would hide an outside placement.
        double gx = added["geometry"]!["x"]!.GetValue<double>();
        double gy = added["geometry"]!["y"]!.GetValue<double>();
        double gw = added["geometry"]!["w"]!.GetValue<double>();
        double gh = added["geometry"]!["h"]!.GetValue<double>();
        Assert.True(gx >= 0 && gy >= 0, "the new child must sit inside its container");
        Assert.True(gx + gw <= 400 && gy + gh <= 400, "the new child must sit inside its container");
        // n_a was not moved; the container still encloses everything.
        AssertGeometry(a["geometry"]!, 20, 20, 160, 80);
        Assert.True(box["geometry"]!["x"]!.GetValue<double>() <= 0 && box["geometry"]!["x"]!.GetValue<double>() + box["geometry"]!["w"]!.GetValue<double>() >= 400, "the container must still enclose its children");
    }

    [Fact]
    public async Task Generate_retries_with_a_message_that_quotes_the_exact_offending_line()
    {
        const string malformed = "draughtsman 1\ntype flowchart\nnode n_bad process \"Label\" \"App Router\"\n";
        int calls = 0;
        var fake = new FakeModelProvider((_, messages) =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(malformed);
            }

            string retryMessage = messages[^1].Content;
            Assert.Contains("node n_bad process \"Label\" \"App Router\"", retryMessage, StringComparison.Ordinal);
            Assert.Contains("must be written as a quoted string: \"App Router\"", retryMessage, StringComparison.Ordinal);
            return Task.FromResult(ValidFlowchartDsl);
        });
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { description = "a login flow", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Generate_with_an_unknown_documentId_is_404()
    {
        var fake = new FakeModelProvider((_, _) => Task.FromResult(ValidFlowchartDsl));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = "doc_missing", description = "add a login step", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Generate_rejects_freeform_because_it_has_no_kind_vocabulary_to_generate_against()
    {
        var fake = new FakeModelProvider((_, _) => Task.FromResult(ValidFlowchartDsl));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { description = "a login flow", diagramType = "freeform" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static string SequenceReply(string order)
    {
        var dsl = new StringBuilder("draughtsman 1\ntype sequence\nnode p_a participant \"A\"\nnode p_b participant \"B\"\n");
        foreach (string id in order.Split(' '))
        {
            dsl.Append(CultureInfo.InvariantCulture, $"edge {id} sync p_a -> p_b \"{id}\"\n");
        }

        return dsl.ToString();
    }

    [Fact]
    public async Task Generate_a_sequence_teaches_edge_order_with_sequence_examples_and_never_asks_the_sidecar_to_lay_it_out()
    {
        string? capturedSystemPrompt = null;
        var fake = new FakeModelProvider((system, _) =>
        {
            capturedSystemPrompt = system;
            return Task.FromResult(SequenceReply("m_1 m_2 m_3"));
        });
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { description = "A calls B three times", diagramType = "sequence" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        Assert.Equal("sequence", body["document"]!["type"]!.GetValue<string>());
        Assert.Equal(["m_1", "m_2", "m_3"], body["document"]!["edges"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
        Assert.All(body["document"]!["nodes"]!.AsArray(), n => Assert.Null(n!["geometry"]));
        Assert.Contains("ORDER OF THE EDGES ARRAY IS TIME ORDER", capturedSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("activate=true", capturedSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("write every edge line in the order the messages happen", capturedSystemPrompt, StringComparison.Ordinal);
        // Milestone 2: fragments, notes and create/destroy are taught, with membership on the message.
        Assert.Contains("in=\"<fragment id>\"", capturedSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("edge n_1 note", capturedSystemPrompt, StringComparison.Ordinal);
        // Two-sided: the flowchart cheat-sheet must not be what a sequence prompt teaches.
        Assert.DoesNotContain("node n_login process", capturedSystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_a_flowchart_does_not_get_the_sequence_teaching()
    {
        string? capturedSystemPrompt = null;
        var fake = new FakeModelProvider((system, _) =>
        {
            capturedSystemPrompt = system;
            return Task.FromResult(ValidFlowchartDsl);
        });
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();

        await client.PostAsJsonAsync("/api/ai/generate-from-text", new { description = "a login flow", diagramType = "flowchart" }, CancellationToken.None);

        Assert.Contains("node n_login process", capturedSystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("in=\"<fragment id>\"", capturedSystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("write every edge line in the order the messages happen", capturedSystemPrompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("m_1 m_2 m_3 m_x", "m_x")]
    [InlineData("m_x m_1 m_2 m_3", "m_x")]
    [InlineData("m_1 m_x m_2 m_3", "m_x")]
    [InlineData("m_3 m_1 m_2", "")]
    [InlineData("m_2 m_3 m_1", "")]
    [InlineData("m_3 m_2 m_1", "")]
    [InlineData("m_1 m_3 m_2", "")]
    [InlineData("m_3 m_x m_1 m_y m_2", "m_x m_y")]
    [InlineData("m_x m_y m_3 m_2 m_1", "m_x m_y")]
    [InlineData("m_2 m_x m_1", "m_x")]
    [InlineData("m_2 m_1 m_3", "")]
    [InlineData("m_2 m_3 m_x m_1 m_y", "m_x m_y")]
    [InlineData("m_3 m_1 m_x m_2", "m_x")]
    public async Task Updating_a_sequence_puts_every_message_where_the_reply_wrote_it(string order, string added)
    {
        var fake = new FakeModelProvider((_, _) => Task.FromResult(SequenceReply(order)));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Talk",
            type = "sequence",
            nodes = new[]
            {
                new { id = "p_a", kind = "participant", label = "A", shape = "rectangle" },
                new { id = "p_b", kind = "participant", label = "B", shape = "rectangle" },
            },
            edges = new[] { "m_1", "m_2", "m_3" }.Select(id => new { id, kind = "sync", label = id, from = new { node = "p_a" }, to = new { node = "p_b" } }).ToArray(),
        }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string id = (await ReadJson(created))["id"]!.GetValue<string>();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = id, description = "reorder", diagramType = "sequence" }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        Assert.Equal(order.Split(' '), body["document"]!["edges"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
        Assert.Equal(added.Length == 0 ? [] : added.Split(' '), body["addedEdgeIds"]!.AsArray().Select(e => e!.GetValue<string>()));
        // A sequence has no stored geometry: an update must not invent a position for anything it touched.
        Assert.All(body["document"]!["nodes"]!.AsArray(), n => Assert.Null(n!["geometry"]));
    }

    [Fact]
    public async Task Updating_a_sequence_that_the_reply_leaves_alone_changes_nothing_and_moves_nothing()
    {
        var fake = new FakeModelProvider((_, _) => Task.FromResult(SequenceReply("m_1 m_2 m_3")));
        using var factory = new AiServerFactory(OllamaConfigYaml, fake);
        HttpClient client = await factory.CreateAuthenticatedClient();
        var created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Talk",
            type = "sequence",
            nodes = new[]
            {
                new { id = "p_a", kind = "participant", label = "A", shape = "rectangle" },
                new { id = "p_b", kind = "participant", label = "B", shape = "rectangle" },
            },
            edges = new[] { "m_1", "m_2", "m_3" }.Select(id => new { id, kind = "sync", label = id, from = new { node = "p_a" }, to = new { node = "p_b" } }).ToArray(),
        }, CancellationToken.None);
        string id = (await ReadJson(created))["id"]!.GetValue<string>();

        JsonNode body = await ReadJson(await client.PostAsJsonAsync("/api/ai/generate-from-text",
            new { documentId = id, description = "nothing", diagramType = "sequence" }, CancellationToken.None));

        Assert.Empty(body["addedEdgeIds"]!.AsArray());
        Assert.Equal(0, body["changedElementCount"]!.GetValue<int>());
        Assert.Equal(0, body["removedElementCount"]!.GetValue<int>());
        Assert.Equal(["m_1", "m_2", "m_3"], body["document"]!["edges"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()));
    }

    private const string OllamaConfigYaml = "ai:\n  provider: ollama\n  model: fake-model\n  endpoint: \"http://localhost:1\"\n";

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        JsonNode? node = JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.NotNull(node);
        return node!;
    }

    private static void AssertGeometry(JsonNode geometry, double x, double y, double w, double h)
    {
        Assert.Equal(x, geometry["x"]!.GetValue<double>());
        Assert.Equal(y, geometry["y"]!.GetValue<double>());
        Assert.Equal(w, geometry["w"]!.GetValue<double>());
        Assert.Equal(h, geometry["h"]!.GetValue<double>());
    }

    private static bool Overlaps(JsonNode a, JsonNode b)
    {
        double ax = a["x"]!.GetValue<double>();
        double ay = a["y"]!.GetValue<double>();
        double bx = b["x"]!.GetValue<double>();
        double by = b["y"]!.GetValue<double>();
        return ax < bx + b["w"]!.GetValue<double>()
            && bx < ax + a["w"]!.GetValue<double>()
            && ay < by + b["h"]!.GetValue<double>()
            && by < ay + a["h"]!.GetValue<double>();
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
