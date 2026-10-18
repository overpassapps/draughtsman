using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Diffing;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Mcp.Patching;
using Draughtsman.Mcp.Tools;
using ModelContextProtocol;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// The camera path (docs/flythrough.md) through the MCP tools: written with create_document, read back in both forms,
/// left alone by every graph edit, replaced or removed as a whole by <c>setFlythrough</c> (and refused when the schema
/// refuses it), reported by diff_documents beside the graph's changes, and ignored by the renderer. Each assertion has
/// its opposite beside it, so a check that cannot fail cannot stay.
/// </summary>
public class FlythroughToolsTests
{
    private const string TourLines = "flythrough loop=true\nkeyframe k_a @ 0,0 400x250 caption=\"Start\"\nkeyframe k_b @ 500,0 400x250 duration=800 easing=cut\n";
    private const string Graph = "draughtsman 1\ntype flowchart\ntitle \"T\"\nnode n_a process \"A\" @ 0,0 120x60\n";

    private readonly FakeStore _store = new();
    private readonly FakeSidecarClient _sidecar = new();
    private readonly DraughtsmanTools _tools;

    public FlythroughToolsTests()
    {
        _tools = new DraughtsmanTools(_store, _sidecar, new FakePdfRenderer(), new FakeAssetReader(), new NullActorContext());
    }

    private static JsonObject Document()
    {
        return (JsonObject)JsonNode.Parse("""
            {
              "id": "doc_1", "schemaVersion": 1, "title": "T", "type": "flowchart",
              "nodes": [{"id":"n_a","kind":"process","label":"A","shape":"rectangle"}],
              "edges": [],
              "meta": {"createdAt":"2026-01-01T00:00:00.000Z","updatedAt":"2026-01-01T00:00:00.000Z","createdBy":"user:local"}
            }
            """)!;
    }

    private static JsonArray Ops(string json) => (JsonArray)JsonNode.Parse(json)!;

    [Fact]
    public async Task CreateDocument_from_dsl_keeps_the_keyframes_and_both_read_formats_show_them()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", Graph + TourLines, "dsl");

        Document document = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Assert.Equal(["k_a", "k_b"], document.Flythrough!.Keyframes.Select(k => k.Id));
        Assert.True(document.Flythrough.Loop);
        string dsl = await _tools.ReadDocument(created.Id, "dsl");
        Assert.Contains("keyframe k_b @ 500,0 400x250 duration=800 easing=cut", dsl, StringComparison.Ordinal);

        CreateDocumentResult plain = await _tools.CreateDocument("T", "flowchart", Graph, "dsl");
        Assert.Null(DocumentJson.Deserialize(await _tools.ReadDocument(plain.Id, "json")).Flythrough);
        Assert.DoesNotContain("keyframe", await _tools.ReadDocument(plain.Id, "dsl"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_outline_an_agent_reads_for_its_context_carries_no_caption_or_view()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", Graph + TourLines, "dsl");

        string outline = await _tools.ReadDocument(created.Id, "outline");

        Assert.DoesNotContain("Start", outline, StringComparison.Ordinal);
        Assert.DoesNotContain("keyframe", outline, StringComparison.Ordinal);
        Assert.Contains("n_a", outline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_graph_patch_leaves_the_tour_exactly_as_it_was()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", Graph + TourLines, "dsl");
        string before = JsonSerializer.Serialize(DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json")).Flythrough, DocumentJson.Options);

        await _tools.PatchDocument(created.Id, """[{"op":"addNode","node":{"id":"n_b","kind":"process","label":"B","shape":"rectangle","geometry":{"x":300,"y":0,"w":100,"h":50}}},{"op":"restyle","target":"node","id":"n_a","style":{"fill":"#ff0000"}},{"op":"updateNode","id":"n_a","set":{"label":"A2"}}]""");

        Document after = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Assert.Equal(before, JsonSerializer.Serialize(after.Flythrough, DocumentJson.Options));
        Assert.Equal("A2", after.Nodes.Single(n => n.Id == "n_a").Label);
    }

    [Fact]
    public async Task SetFlythrough_replaces_the_whole_path_and_null_removes_it()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", Graph + TourLines, "dsl");

        PatchDocumentResult replaced = await _tools.PatchDocument(created.Id, """[{"op":"setFlythrough","flythrough":{"keyframes":[{"id":"k_only","view":{"x":1,"y":2,"w":300,"h":200},"holdMs":0}]}}]""");

        Assert.NotEmpty(replaced.CheckpointVersionId);
        Document one = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Assert.Equal("k_only", Assert.Single(one.Flythrough!.Keyframes).Id);
        Assert.Null(one.Flythrough.Loop);

        await _tools.PatchDocument(created.Id, """[{"op":"setFlythrough","flythrough":null}]""");
        Assert.Null(DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json")).Flythrough);
    }

    [Fact]
    public async Task SetFlythrough_on_a_document_with_no_path_adds_one()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", Graph, "dsl");

        await _tools.PatchDocument(created.Id, """[{"op":"setFlythrough","flythrough":{"keyframes":[{"id":"k_1","view":{"x":0,"y":0,"w":100,"h":50},"caption":"Hi"}],"loop":false}}]""");

        Document document = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Assert.Equal("Hi", Assert.Single(document.Flythrough!.Keyframes).Caption);
        Assert.False(document.Flythrough.Loop);
    }

    [Theory]
    [InlineData("""[{"op":"setFlythrough"}]""", "needs a 'flythrough' field")]
    [InlineData("""[{"op":"setFlythrough","flythrough":"soon"}]""", "must be a JSON object")]
    [InlineData("""[{"op":"setFlythrough","flythrough":{"keyframes":[]}}]""", "flythrough")]
    [InlineData("""[{"op":"setFlythrough","flythrough":{"keyframes":[{"id":"k","view":{"x":0,"y":0,"w":0,"h":10}}]}}]""", "flythrough")]
    [InlineData("""[{"op":"setFlythrough","flythrough":{"keyframes":[{"id":"k","view":{"x":0,"y":0,"w":5,"h":10},"durationMs":-1}]}}]""", "flythrough")]
    public async Task SetFlythrough_with_something_that_is_not_a_path_fails_the_whole_call_and_leaves_the_document(string ops, string expected)
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", Graph + TourLines, "dsl");
        string before = await _tools.ReadDocument(created.Id, "json");

        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.PatchDocument(created.Id, ops));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, await _tools.ReadDocument(created.Id, "json"));
    }

    [Fact]
    public void The_op_is_one_the_applier_lists_when_it_refuses_an_unknown_one()
    {
        PatchException error = Assert.Throws<PatchException>(() => PatchApplier.Apply(Document(), Ops("""[{"op":"setTour"}]""")));

        Assert.Contains("setFlythrough", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_sets_and_removes_the_member_on_the_working_copy()
    {
        JsonObject document = Document();

        PatchApplier.Apply(document, Ops("""[{"op":"setFlythrough","flythrough":{"keyframes":[{"id":"k_1","view":{"x":0,"y":0,"w":10,"h":10}}]}}]"""));
        Assert.Equal("k_1", document["flythrough"]!["keyframes"]![0]!["id"]!.GetValue<string>());
        PatchApplier.Apply(document, Ops("""[{"op":"setFlythrough","flythrough":null}]"""));

        Assert.False(document.ContainsKey("flythrough"));
    }

    [Fact]
    public async Task DiffDocuments_reports_a_changed_path_beside_the_graph_and_nothing_for_a_graph_only_change()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", Graph + TourLines, "dsl");
        // Each patch checkpoints the document as it was before: the first holds the original, the second the graph edit.
        PatchDocumentResult graphEdit = await _tools.PatchDocument(created.Id, """[{"op":"updateNode","id":"n_a","set":{"label":"A2"}}]""");
        PatchDocumentResult tourEdit = await _tools.PatchDocument(created.Id, """[{"op":"setFlythrough","flythrough":{"keyframes":[{"id":"k_a","view":{"x":0,"y":0,"w":400,"h":250},"caption":"Renamed"}]}}]""");

        DocumentDiffResult tourOnly = (await _tools.DiffDocuments(created.Id, tourEdit.CheckpointVersionId)).Diff;
        DocumentDiffResult both = (await _tools.DiffDocuments(created.Id, graphEdit.CheckpointVersionId)).Diff;
        DocumentDiffResult graphOnly = (await _tools.DiffDocuments(created.Id, graphEdit.CheckpointVersionId, tourEdit.CheckpointVersionId)).Diff;

        Assert.NotNull(tourOnly.FlythroughChange);
        Assert.Equal(2, tourOnly.FlythroughChange.From!.Keyframes.Count);
        Assert.Equal("Renamed", Assert.Single(tourOnly.FlythroughChange.To!.Keyframes).Caption);
        Assert.Empty(tourOnly.ChangedNodes);
        Assert.NotNull(both.FlythroughChange);
        Assert.Single(both.ChangedNodes);
        Assert.Null(graphOnly.FlythroughChange);
        Assert.Single(graphOnly.ChangedNodes);
    }

    [Fact]
    public void Compare_sees_no_change_for_two_paths_that_serialise_alike_and_one_for_a_removal()
    {
        Document a = Draughtsman.Core.Text.Dsl.Parse(Graph + TourLines).Document;
        Document same = Draughtsman.Core.Text.Dsl.Parse(Graph + TourLines).Document;
        Document without = a with { Flythrough = null };

        Assert.Null(DocumentDiff.Compare(a, same).FlythroughChange);
        Assert.Null(DocumentDiff.Compare(without, without).FlythroughChange);
        FlythroughChange removed = DocumentDiff.Compare(a, without).FlythroughChange!;
        Assert.NotNull(removed.From);
        Assert.Null(removed.To);
        FlythroughChange added = DocumentDiff.Compare(without, a).FlythroughChange!;
        Assert.Null(added.From);
        Assert.NotNull(added.To);
    }

    [Fact]
    public async Task Render_draws_the_same_picture_with_the_tour_as_without_it()
    {
        CreateDocumentResult withTour = await _tools.CreateDocument("T", "flowchart", Graph + TourLines, "dsl");
        CreateDocumentResult plain = await _tools.CreateDocument("T", "flowchart", Graph, "dsl");

        RenderDocumentResult tour = await _tools.RenderDocument(withTour.Id);
        RenderDocumentResult bare = await _tools.RenderDocument(plain.Id);

        Assert.Equal(bare.Svg, tour.Svg);
        Assert.DoesNotContain("Start", tour.Svg, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSchema_publishes_the_flythrough_so_an_agent_can_write_one()
    {
        GetSchemaResult schema = _tools.GetSchema();

        JsonElement definitions = schema.DocumentSchema.GetProperty("$defs");
        Assert.True(schema.DocumentSchema.GetProperty("properties").TryGetProperty("flythrough", out _));
        Assert.True(definitions.TryGetProperty("keyframe", out JsonElement keyframe));
        Assert.Equal(["cut", "linear", "easeIn", "easeOut", "easeInOut"], keyframe.GetProperty("properties").GetProperty("easing").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.True(definitions.TryGetProperty("viewRect", out _));
    }
}
