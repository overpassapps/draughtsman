using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Server.Tests;

/// <summary>
/// A bare, unconfigured <c>WebApplicationFactory&lt;Program&gt;</c> here used to share the default (unset
/// <c>DRAUGHTSMAN_DATA</c>) data folder with every other bare instance in the same test run - harmless before local
/// sign-in, since every request was independent and idempotent, but the first-run/login round trip this class's
/// tests now make through <see cref="AuthTestHelpers.CreateAuthenticatedClient"/> raced concurrently-running test
/// classes' own migrations against that same physical file ("table 'users' already exists"). This is
/// <see cref="DraughtsmanServerFactory"/> now, like every sibling test class, for its own isolated temp folder.
/// </summary>
public class LayoutEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public LayoutEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Layout_returns_the_document_with_geometry_on_every_node()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        JsonNode input = ExampleWithoutGeometry();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/layout", input, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement envelope = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Empty(envelope.GetProperty("warnings").EnumerateArray());
        Document document = ReadDocument(envelope);
        Assert.Equal(5, document.Nodes.Count);
        Assert.All(document.Nodes, n => Assert.True(LaidOut(n).W > 0 && LaidOut(n).H > 0, n.Id));
        Assert.Equal("c_vpc", document.Nodes.Single(n => n.Id == "n_api").Parent);
        Assert.Equal(3, document.Edges.Count);
    }

    [Fact]
    public async Task Layout_reports_what_it_could_not_route_as_warnings()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        JsonNode input = ExampleWithoutGeometry();
        input["edges"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "e_ghost",
            ["kind"] = "flow",
            ["from"] = new JsonObject { ["node"] = "n_web" },
            ["to"] = new JsonObject { ["node"] = "n_ghost" },
        });

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/layout", input, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement envelope = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        string warning = Assert.Single(envelope.GetProperty("warnings").EnumerateArray()).GetString()!;
        Assert.Contains("n_ghost", warning, StringComparison.Ordinal);
        Assert.Equal(4, ReadDocument(envelope).Edges.Count);
    }

    [Fact]
    public async Task Layout_accepts_algorithm_and_direction_and_rejects_unknown_values()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        JsonNode input = ExampleWithoutGeometry();

        HttpResponseMessage stress = await client.PostAsJsonAsync("/api/layout?algorithm=stress", input, CancellationToken.None);
        HttpResponseMessage right = await client.PostAsJsonAsync("/api/layout?algorithm=layered&direction=right", input, CancellationToken.None);
        HttpResponseMessage tree = await client.PostAsJsonAsync("/api/layout?algorithm=tree", input, CancellationToken.None);
        HttpResponseMessage badAlgorithm = await client.PostAsJsonAsync("/api/layout?algorithm=circular", input, CancellationToken.None);
        HttpResponseMessage badDirection = await client.PostAsJsonAsync("/api/layout?direction=sideways", input, CancellationToken.None);
        HttpResponseMessage wrongCase = await client.PostAsJsonAsync("/api/layout?algorithm=Stress", input, CancellationToken.None);
        HttpResponseMessage numeric = await client.PostAsJsonAsync("/api/layout?direction=3", input, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, stress.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal(HttpStatusCode.OK, tree.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badAlgorithm.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badDirection.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongCase.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, numeric.StatusCode);
        JsonElement problem = await wrongCase.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Contains("layered, stress or tree", problem.GetProperty("title").GetString(), StringComparison.Ordinal);
        Document laidOutRight = ReadDocument(await right.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None));
        Geometry web = LaidOut(laidOutRight.Nodes.Single(n => n.Id == "n_web"));
        Geometry api = LaidOut(laidOutRight.Nodes.Single(n => n.Id == "n_api"));
        Assert.True(web.X + web.W <= api.X);
    }

    [Fact]
    public async Task Layout_rejects_bodies_that_are_not_documents()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        JsonNode nodesNotAnArray = ExampleWithoutGeometry();
        nodesNotAnArray["nodes"] = 1;
        JsonNode nodeWithoutLabel = ExampleWithoutGeometry();
        nodeWithoutLabel["nodes"]![0]!.AsObject().Remove("label");

        HttpResponseMessage notADocument = await client.PostAsJsonAsync("/api/layout", nodesNotAnArray, CancellationToken.None);
        HttpResponseMessage bareNodes = await client.PostAsJsonAsync("/api/layout", new { nodes = new[] { new { id = "a" } }, edges = Array.Empty<object>() }, CancellationToken.None);
        HttpResponseMessage unlabelled = await client.PostAsJsonAsync("/api/layout", nodeWithoutLabel, CancellationToken.None);
        HttpResponseMessage notAnObject = await client.PostAsJsonAsync("/api/layout", new[] { 1, 2 }, CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, notADocument.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bareNodes.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unlabelled.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notAnObject.StatusCode);
        JsonElement problem = await notADocument.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Contains("nodes", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        JsonElement unlabelledProblem = await unlabelled.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Contains("label", unlabelledProblem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("createdAt", "createdAt")]
    [InlineData("ports", "Port")]
    [InlineData("routing", "routing")]
    [InlineData("schemaVersion", "schemaVersion")]
    public async Task Layout_rejects_members_the_model_cannot_read_as_the_callers_fault(string member, string namedInDetail)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        JsonNode input = ExampleWithoutGeometry();
        switch (member)
        {
            case "schemaVersion":
                input["schemaVersion"] = 2;
                break;
            case "createdAt":
                input["meta"]!["createdAt"] = "yesterday";
                break;
            case "ports":
                input["nodes"]![0]!["ports"] = new JsonArray(new JsonObject { ["id"] = "p" });
                break;
            case "routing":
                input["edges"]![0]!["routing"] = new JsonObject { ["mode"] = "bezier" };
                break;
        }

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/layout", input, CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Contains(namedInDetail, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    /// <summary>Geometry of a node the endpoint has laid out; every node leaves layout with one.</summary>
    private static Geometry LaidOut(Node node)
    {
        Assert.True(node.Geometry is not null, $"{node.Id} came back without geometry");
        return node.Geometry!;
    }

    private static Document ReadDocument(JsonElement envelope)
    {
        return DocumentJson.Deserialize(envelope.GetProperty("document").GetRawText());
    }

    [Fact]
    public async Task Layout_of_a_sequence_document_returns_it_unchanged_with_no_warning_and_no_invented_geometry()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        JsonNode input = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "checkout-sequence.json")))!;

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/layout", input, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement envelope = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        Assert.Empty(envelope.GetProperty("warnings").EnumerateArray());
        Document returned = ReadDocument(envelope);
        Assert.Equal(DocumentType.Sequence, returned.Type);
        Assert.All(returned.Nodes, n => Assert.Null(n.Geometry));
        // Every field of every node and edge, and the order of the edges, is what went in.
        Assert.Equal(
            DocumentJson.Serialize(DocumentJson.Deserialize(input.ToJsonString())),
            DocumentJson.Serialize(returned));
    }

    private static JsonNode ExampleWithoutGeometry()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "contract", "order-service.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        foreach (JsonNode? node in root["nodes"]!.AsArray())
        {
            node!.AsObject().Remove("geometry");
        }

        return root;
    }
}
