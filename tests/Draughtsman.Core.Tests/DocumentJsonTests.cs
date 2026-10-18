using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Tests;

public class DocumentJsonTests
{
    [Fact]
    public void Example_round_trips_without_loss()
    {
        string original = ContractFiles.ReadExample();

        Document document = DocumentJson.Deserialize(original);
        string reserialised = DocumentJson.Serialize(document);

        JsonNode? expected = JsonNode.Parse(original);
        JsonNode? actual = JsonNode.Parse(reserialised);
        Assert.True(JsonNode.DeepEquals(expected, actual), $"Round trip changed the JSON.{Environment.NewLine}{reserialised}");
    }

    [Fact]
    public void Indented_output_uses_a_line_feed_on_every_host_so_a_exported_file_is_the_same_bytes_everywhere()
    {
        // System.Text.Json indents with Environment.NewLine unless told otherwise, so only a Windows host can fail this.
        string indented = DocumentJson.Serialize(DocumentJson.Deserialize(ContractFiles.ReadExample()), indented: true);

        Assert.Contains('\n', indented);
        Assert.DoesNotContain('\r', indented);
        // The same on a host where the platform newline is already "\n": the option, not luck, is what holds it.
        Assert.Equal("\n", DocumentJson.IndentedOptions.NewLine);
    }

    [Fact]
    public void Example_deserialises_into_typed_model()
    {
        Document document = DocumentJson.Deserialize(ContractFiles.ReadExample());

        Assert.Equal("doc_order_service", document.Id);
        Assert.Equal(Document.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Equal(DocumentType.Architecture, document.Type);
        Assert.Equal(5, document.Nodes.Count);
        Assert.Equal(3, document.Edges.Count);

        Node api = Assert.Single(document.Nodes, n => n.Id == "n_api");
        Assert.Equal("c_vpc", api.Parent);
        Assert.Equal("platform", api.Props!["owner"].GetString());
        Assert.Equal(3, api.Ports!.Count);

        Edge e2 = Assert.Single(document.Edges, e => e.Id == "e_2");
        Assert.Equal(ArrowHead.Arrow, e2.Style!.ArrowStart);
        Assert.Equal(RoutingMode.Orthogonal, e2.Routing!.Mode);
        Assert.Equal("db", e2.From.Port);

        Edge e3 = Assert.Single(document.Edges, e => e.Id == "e_3");
        Assert.Null(e3.Label);
        Assert.Single(e3.Routing!.Waypoints!);

        AiProvenance provenance = Assert.Single(document.Meta.AiProvenance!);
        Assert.Equal(AiAction.Updated, provenance.Action);
        Assert.Equal(["n_cache", "e_3"], provenance.ElementIds);
    }

    [Fact]
    public void Sparse_style_serialises_sparse()
    {
        Document document = MinimalDocument() with
        {
            Nodes = [new Node("n_1", "process", "Step", "rounded") { Geometry = new Geometry(0, 0, 100, 50), Style = new NodeStyle { Fill = "#FFF" } }],
        };

        string json = DocumentJson.Serialize(document);
        JsonNode node = JsonNode.Parse(json)!["nodes"]![0]!;

        Assert.Equal("#FFF", node["style"]!["fill"]!.GetValue<string>());
        Assert.Null(node["style"]!["stroke"]);
        Assert.Null(node["props"]);
        Assert.Null(node["parent"]);
        Assert.Null(JsonNode.Parse(json)!["theme"]);
    }

    [Fact]
    public void Enums_are_camel_case_strings()
    {
        Document document = MinimalDocument() with
        {
            Edges = [new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b")) { Routing = new Routing(RoutingMode.Orthogonal), Style = new EdgeStyle { ArrowEnd = ArrowHead.Diamond } }],
        };

        string json = DocumentJson.Serialize(document);

        Assert.Contains("\"type\":\"flowchart\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"orthogonal\"", json, StringComparison.Ordinal);
        Assert.Contains("\"arrowEnd\":\"diamond\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Rotation_is_omitted_when_absent_and_kept_when_present()
    {
        const string json = """{ "id": "d", "schemaVersion": 1, "title": "t", "type": "flowchart", "nodes": [ { "id": "n_a", "kind": "process", "label": "A", "shape": "rounded", "geometry": { "x": 0, "y": 0, "w": 10, "h": 10 } }, { "id": "n_b", "kind": "process", "label": "B", "shape": "rounded", "geometry": { "x": 0, "y": 0, "w": 10, "h": 10, "rotation": 45 } } ], "edges": [], "meta": { "createdAt": "2026-09-20T22:00:00.000Z", "updatedAt": "2026-09-20T22:00:00.000Z", "createdBy": "x" } }""";

        Document document = DocumentJson.Deserialize(json);
        JsonNode nodes = JsonNode.Parse(DocumentJson.Serialize(document))!["nodes"]!;

        Assert.Null(document.Nodes[0].Geometry!.Rotation);
        Assert.Equal(45, document.Nodes[1].Geometry!.Rotation);
        Assert.Null(nodes[0]!["geometry"]!["rotation"]);
        Assert.Equal(45, nodes[1]!["geometry"]!["rotation"]!.GetValue<double>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(DocumentJson.Serialize(document))));
    }

    [Fact]
    public void Timestamps_round_trip_in_javascript_toisostring_form()
    {
        DateTimeOffset offset = new(2026, 9, 21, 0, 30, 0, 250, TimeSpan.FromHours(2));
        Document document = MinimalDocument() with { Meta = new Meta(offset, offset, "test") };

        string json = DocumentJson.Serialize(document);
        Document reread = DocumentJson.Deserialize(json);

        Assert.Contains("\"createdAt\":\"2026-09-20T22:30:00.250Z\"", json, StringComparison.Ordinal);
        Assert.Equal(json, DocumentJson.Serialize(reread));
        Assert.Contains("\"updatedAt\":\"2026-09-20T22:00:00.000Z\"", DocumentJson.Serialize(MinimalDocument()), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_required_member_throws()
    {
        const string json = """{ "id": "d", "schemaVersion": 1, "title": "t", "type": "flowchart", "nodes": [], "edges": [] }""";

        Assert.Throws<JsonException>(() => DocumentJson.Deserialize(json));
    }

    [Fact]
    public void Unknown_enum_value_throws()
    {
        const string json = """{ "id": "d", "schemaVersion": 1, "title": "t", "type": "mindmap", "nodes": [], "edges": [], "meta": { "createdAt": "2026-09-20T00:00:00Z", "updatedAt": "2026-09-20T00:00:00Z", "createdBy": "x" } }""";

        Assert.Throws<JsonException>(() => DocumentJson.Deserialize(json));
    }

    internal static Document MinimalDocument()
    {
        DateTimeOffset now = new(2026, 9, 20, 22, 0, 0, TimeSpan.Zero);
        return new Document("doc_min", Document.CurrentSchemaVersion, "Minimal", DocumentType.Flowchart, [], [], new Meta(now, now, "test"));
    }
}
