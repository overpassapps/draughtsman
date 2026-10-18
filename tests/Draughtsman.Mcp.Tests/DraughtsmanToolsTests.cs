using System.Text.Json;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Diffing;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Draughtsman.Mcp.Tools;
using Draughtsman.Sidecar;
using ModelContextProtocol;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// Exercises the tools the way stdio calls them: no <see cref="IActorContext"/> identity, so the <c>actor</c>
/// argument is fully self-reported, exactly as it always has been for this transport. The HTTP-authenticated path
/// (an argument that may only annotate an agent on top of a real caller) is <c>DraughtsmanToolsHttpActorTests</c>.
/// </summary>
public class DraughtsmanToolsTests
{
    private readonly FakeStore _store = new();
    private readonly FakeSidecarClient _sidecar = new();
    private readonly FakePdfRenderer _pdf = new();
    private readonly FakeAssetReader _assets = new();
    private readonly DraughtsmanTools _tools;

    public DraughtsmanToolsTests()
    {
        _tools = new DraughtsmanTools(_store, _sidecar, _pdf, _assets, new NullActorContext());
    }

    [Fact]
    public async Task ListDocuments_rejects_invalid_paging_parameters()
    {
        McpException negativeOffset = await Assert.ThrowsAsync<McpException>(() => _tools.ListDocuments(offset: -1));
        Assert.Contains("offset", negativeOffset.Message, StringComparison.OrdinalIgnoreCase);

        McpException zeroLimit = await Assert.ThrowsAsync<McpException>(() => _tools.ListDocuments(limit: 0));
        Assert.Contains("limit", zeroLimit.Message, StringComparison.OrdinalIgnoreCase);

        McpException excessiveLimit = await Assert.ThrowsAsync<McpException>(() => _tools.ListDocuments(limit: 201));
        Assert.Contains("limit", excessiveLimit.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateDocument_rejects_overlong_actor()
    {
        string longActor = "user:" + new string('n', 257);
        McpException ex = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl", actor: longActor));
        Assert.Contains("actor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadDocument_outline_escapes_quotes_in_labels()
    {
        string dsl = "draughtsman 1\ntype flowchart\ntitle \"T\"\nnode n_a process \"He said \\\"hi\\\"\" @ 0,0 120x60\nedge e_1 flow n_a -> n_a \"She replied \\\"bye\\\"\"\n";
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", dsl, "dsl");

        string outline = await _tools.ReadDocument(created.Id, "outline");
        Assert.Contains("\"He said \\\"hi\\\"\"", outline, StringComparison.Ordinal);
        Assert.Contains("\"She replied \\\"bye\\\"\"", outline, StringComparison.Ordinal);
    }

    private const string ShopSql = "create table Customers (Id int primary key);\ncreate table Orders (Id int primary key, CustomerId int not null references Customers (Id), RepId int null references Reps (Id));";

    [Fact]
    public async Task CreateDocument_sql_draws_entities_and_relationships_laid_out_even_when_layout_is_not_asked_for()
    {
        CreateDocumentResult created = await _tools.CreateDocument("Shop", "architecture", ShopSql, "sql");

        Document document = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Assert.Equal(new[] { "Customers", "Orders" }, document.Nodes.Select(n => n.Label));
        Assert.All(document.Nodes, n => Assert.NotNull(n.Geometry));
        Assert.Equal("entity", document.Nodes[0].Kind);
        Assert.Equal("CustomerId", Assert.Single(document.Edges).Label);
        Assert.Contains(created.Warnings, w => w.StartsWith("missing-reference (line 2)", StringComparison.Ordinal) && w.Contains("Reps", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateDocument_sql_refuses_a_type_that_cannot_hold_tables_and_an_unknown_format_names_sql()
    {
        McpException flowchart = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Shop", "flowchart", ShopSql, "sql"));
        Assert.Contains("architecture or freeform", flowchart.Message, StringComparison.Ordinal);

        McpException format = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Shop", "architecture", ShopSql, "ddl"));
        Assert.Contains("'sql'", format.Message, StringComparison.Ordinal);
    }

    private const string ShopEr = "erDiagram\n  Customers {\n    int Id PK\n  }\n  Orders {\n    int Id PK\n    int CustomerId FK\n  }\n  Orders }o--|| Customers : CustomerId\n  unreadable line\n";

    [Fact]
    public async Task CreateDocument_mermaid_erDiagram_draws_entities_and_relationships_laid_out_even_when_layout_is_not_asked_for()
    {
        CreateDocumentResult created = await _tools.CreateDocument("Shop", "architecture", ShopEr, "mermaid");

        Document document = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Assert.Equal(new[] { "Customers", "Orders" }, document.Nodes.Select(n => n.Label));
        Assert.All(document.Nodes, n => Assert.NotNull(n.Geometry));
        Assert.All(document.Nodes, n => Assert.Equal("custom:er/entity", n.Shape));
        Edge edge = Assert.Single(document.Edges);
        Assert.Equal((ArrowHead.ZeroOrMany, ArrowHead.OneAndOnlyOne), (edge.Style?.ArrowStart, edge.Style?.ArrowEnd));
        Assert.Contains(created.Warnings, w => w.StartsWith("parse-error (line 10)", StringComparison.Ordinal));

        McpException flowchart = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Shop", "flowchart", ShopEr, "mermaid"));
        Assert.Contains("erDiagram", flowchart.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadDocument_outline_carries_no_image_data()
    {
        // Privacy (build-spec-v0.1.md section 9): the outline is what the diary/critique prompts send to a model, so an
        // image node must contribute its id and kind and nothing that points at the picture or its bytes.
        const string assetId = "asset_private123";
        var node = new Node("n_img", "image", "Logo", "image")
        {
            Geometry = new Geometry(0, 0, 240, 120),
            Props = new Dictionary<string, JsonElement> { ["assetId"] = JsonDocument.Parse($"\"{assetId}\"").RootElement },
        };
        await _store.CreateDocument(new Document("doc_img", 1, "T", DocumentType.Architecture, [node], [], new Meta(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "user:local")));

        string outline = await _tools.ReadDocument("doc_img", "outline");

        Assert.Contains("n_img", outline, StringComparison.Ordinal);
        Assert.DoesNotContain(assetId, outline, StringComparison.Ordinal);
        Assert.DoesNotContain("data:", outline, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", outline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RenderDocument_inlines_an_image_asset_as_a_data_uri()
    {
        byte[] bytes = [9, 8, 7];
        var node = new Node("n_img", "image", "", "image")
        {
            Geometry = new Geometry(0, 0, 100, 50),
            Props = new Dictionary<string, JsonElement> { ["assetId"] = JsonDocument.Parse("\"asset_1\"").RootElement },
        };
        await _store.CreateDocument(new Document("doc_img2", 1, "T", DocumentType.Architecture, [node], [], new Meta(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "user:local")));
        _assets.Add("asset_1", "image/png", bytes);

        RenderDocumentResult rendered = await _tools.RenderDocument("doc_img2", "svg");

        Assert.Contains("<image", rendered.Svg, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64," + Convert.ToBase64String(bytes), rendered.Svg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_then_read_round_trips_losslessly()
    {
        string dsl = "draughtsman 1\ntype flowchart\ntitle \"Ignored\"\nnode n_a process \"A\" @ 0,0 120x60\nnode n_b process \"B\" @ 200,0 120x60\nedge e_1 flow n_a -> n_b\n";

        CreateDocumentResult created = await _tools.CreateDocument("Order flow", "flowchart", dsl, "dsl");

        string json = await _tools.ReadDocument(created.Id, "json");
        Document read = DocumentJson.Deserialize(json);

        Assert.Equal("Order flow", read.Title);
        Assert.Equal(2, read.Nodes.Count);
        Assert.Equal("n_a", read.Nodes[0].Id);
        Assert.Equal(120, read.Nodes[0].Geometry!.W);

        string dslBack = await _tools.ReadDocument(created.Id, "dsl");
        Assert.Contains("n_a", dslBack, StringComparison.Ordinal);
        Assert.Contains("n_b", dslBack, StringComparison.Ordinal);

        string outline = await _tools.ReadDocument(created.Id, "outline");
        Assert.Contains("n_a -> n_b", outline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_from_json_requires_no_layout_when_layout_is_false()
    {
        var document = new Document("ignored", 1, "T", DocumentType.Flowchart,
            [new Node("n_a", "process", "A", "rectangle")], [], new Meta(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "user:local"));
        string json = DocumentJson.Serialize(document);

        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", json, "json", layout: false);

        Document stored = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Assert.Null(stored.Nodes[0].Geometry);
    }

    [Fact]
    public async Task Patch_addNode_then_read_round_trips_the_new_node_losslessly()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");

        string ops = """[{"op":"addNode","node":{"id":"n_c","kind":"process","label":"C","shape":"rectangle","geometry":{"x":10,"y":20,"w":100,"h":50}}}]""";
        PatchDocumentResult patchResult = await _tools.PatchDocument(created.Id, ops);
        Assert.Empty(patchResult.Warnings);
        Assert.NotEmpty(patchResult.CheckpointVersionId);

        Document document = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Node added = Assert.Single(document.Nodes, n => n.Id == "n_c");
        Assert.Equal("C", added.Label);
        Assert.Equal(10, added.Geometry!.X);
    }

    [Fact]
    public async Task Patch_restyle_merges_onto_existing_style()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");

        await _tools.PatchDocument(created.Id, """[{"op":"restyle","target":"node","id":"n_a","style":{"fill":"#ff0000"}}]""");
        await _tools.PatchDocument(created.Id, """[{"op":"restyle","target":"node","id":"n_a","style":{"stroke":"#00ff00"}}]""");

        Document document = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        NodeStyle? style = document.Nodes.Single(n => n.Id == "n_a").Style;
        Assert.Equal("#ff0000", style?.Fill);
        Assert.Equal("#00ff00", style?.Stroke);
    }

    [Fact]
    public async Task Patch_on_unknown_document_fails_loudly()
    {
        McpException ex = await Assert.ThrowsAsync<McpException>(() => _tools.PatchDocument("doc_missing", "[]"));
        Assert.Contains("doc_missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_on_unknown_document_fails_loudly()
    {
        await Assert.ThrowsAsync<McpException>(() => _tools.ReadDocument("doc_missing"));
    }

    [Fact]
    public async Task Patch_with_an_unaddressable_target_fails_the_whole_call_rather_than_no_opping()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");

        string ops = """[{"op":"updateNode","id":"n_does_not_exist","set":{"label":"X"}}]""";
        McpException ex = await Assert.ThrowsAsync<McpException>(() => _tools.PatchDocument(created.Id, ops));
        Assert.Contains("n_does_not_exist", ex.Message, StringComparison.Ordinal);

        // Nothing was applied: no checkpoint was left behind by the failed call.
        Document unchanged = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Assert.DoesNotContain(unchanged.Nodes, n => n.Id == "n_does_not_exist");
    }

    [Fact]
    public async Task Patch_with_an_unknown_op_fails_loudly()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");

        McpException ex = await Assert.ThrowsAsync<McpException>(
            () => _tools.PatchDocument(created.Id, """[{"op":"deleteEverything"}]"""));
        Assert.Contains("deleteEverything", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_with_layout_degrades_honestly_when_the_sidecar_is_unavailable()
    {
        _sidecar.Failure = new SidecarUnavailableException("fake sidecar is down");
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl", layout: true);

        Assert.Contains(created.Warnings, w => w.Contains("sidecar", StringComparison.OrdinalIgnoreCase));

        // The document was still created, just without layout - a degrade, not a failure of the whole call.
        Document document = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Assert.NotEmpty(document.Nodes);
    }

    [Fact]
    public async Task Render_fails_loudly_when_geometry_is_missing_and_the_sidecar_is_unavailable()
    {
        var document = new Document("ignored", 1, "T", DocumentType.Flowchart,
            [new Node("n_a", "process", "A", "rectangle")], [], new Meta(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "user:local"));
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", DocumentJson.Serialize(document), "json", layout: false);

        _sidecar.Failure = new SidecarUnavailableException("fake sidecar is down");
        McpException ex = await Assert.ThrowsAsync<McpException>(() => _tools.RenderDocument(created.Id));
        Assert.Contains("layout", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Render_returns_svg_for_a_laid_out_document()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");
        RenderDocumentResult rendered = await _tools.RenderDocument(created.Id);

        Assert.StartsWith("<svg", rendered.Svg, StringComparison.Ordinal);
        Assert.Contains("n_a", rendered.Svg, StringComparison.Ordinal);
        Assert.Empty(rendered.Warnings);
    }

    private static string OrthogonalDsl() => "draughtsman 1\ntype freeform\ntitle \"T\"\nnode n_a rectangle \"A\" @ 0,0 100x60\nnode n_b rectangle \"B\" @ 400,300 100x60\nedge e_1 flow n_a -> n_b routing=orthogonal\n";

    [Fact]
    public async Task Render_draws_orthogonal_edges_along_the_routes_the_sidecar_computes()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "freeform", OrthogonalDsl(), "dsl", layout: false);
        _sidecar.Routes["e_1"] = new EdgeRoute("routed", [new Point(100, 30), new Point(250, 30), new Point(250, 330), new Point(400, 330)]);

        RenderDocumentResult rendered = await _tools.RenderDocument(created.Id);

        Assert.Equal(1, _sidecar.RouteCalls);
        Assert.Contains("<polyline", rendered.Svg, StringComparison.Ordinal);
        Assert.Empty(rendered.Warnings);
    }

    [Fact]
    public async Task Render_does_not_ask_the_sidecar_to_route_a_document_with_nothing_orthogonal()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");
        await _tools.RenderDocument(created.Id);
        Assert.Equal(0, _sidecar.RouteCalls);
    }

    [Fact]
    public async Task Render_says_so_when_orthogonal_edges_could_not_be_routed_rather_than_drawing_them_straight_silently()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "freeform", OrthogonalDsl(), "dsl", layout: false);
        _sidecar.Failure = new SidecarUnavailableException("fake sidecar is down");

        RenderDocumentResult rendered = await _tools.RenderDocument(created.Id);

        Assert.StartsWith("<svg", rendered.Svg, StringComparison.Ordinal);
        Assert.Contains(rendered.Warnings, w => w.Contains("orthogonal connectors are drawn as straight lines", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Render_rejects_unsupported_formats_rather_than_pretending_to_produce_them()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");
        await Assert.ThrowsAsync<McpException>(() => _tools.RenderDocument(created.Id, "png"));
    }

    [Fact]
    public async Task Render_pdf_returns_base64_pdf_when_the_component_is_available()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");

        RenderDocumentResult rendered = await _tools.RenderDocument(created.Id, "pdf");

        Assert.NotEmpty(rendered.Pdf);
        Assert.Contains("%PDF", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(rendered.Pdf)), StringComparison.Ordinal);
        Assert.Empty(rendered.Warnings);
    }

    [Fact]
    public async Task Render_pdf_names_the_component_when_it_is_absent()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");
        _pdf.IsAvailable = false;

        McpException ex = await Assert.ThrowsAsync<McpException>(() => _tools.RenderDocument(created.Id, "pdf"));

        Assert.Contains("Chromium", ex.Message, StringComparison.Ordinal);
        Assert.Contains("export.chromium.enabled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diff_reports_added_removed_and_changed_elements()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");

        await _tools.PatchDocument(created.Id, """
            [{"op":"addNode","node":{"id":"n_c","kind":"process","label":"C","shape":"rectangle","geometry":{"x":10,"y":20,"w":100,"h":50}}},
             {"op":"updateNode","id":"n_a","set":{"label":"A renamed"}}]
            """);

        DiffDocumentsResult noopDiff = await _tools.DiffDocuments(created.Id, "current", "current");
        Assert.Empty(noopDiff.Diff.AddedNodes);

        Document current = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Document before = DocumentJson.Deserialize(MinimalDslAsJson());
        DocumentDiffResult diff = DocumentDiff.Compare(before, current);

        Assert.Single(diff.AddedNodes);
        Assert.Equal("n_c", diff.AddedNodes[0].Id);
        Assert.Single(diff.ChangedNodes);
        Assert.Equal("n_a", diff.ChangedNodes[0].Id);
        Assert.Contains(diff.ChangedNodes[0].Fields, f => f.Field == "label");
    }

    [Fact]
    public void GetSchema_returns_the_committed_json_schema_and_the_kind_vocabulary()
    {
        GetSchemaResult schema = _tools.GetSchema("flowchart");
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.DocumentSchema.GetProperty("$schema").GetString());
        Assert.Equal("flowchart", schema.KindVocabulary!.Value.GetProperty("type").GetString());
        // The generic icon library comes back for any named type, so an agent can place custom:generic/<name> shapes.
        Assert.Equal("generic", schema.GenericIcons!.Value.GetProperty("library").GetString());
        Assert.Contains(schema.GenericIcons.Value.GetProperty("icons").EnumerateArray(),
            icon => icon.GetProperty("name").GetString() == "server");

        // The state shape library comes back too, so an agent can place custom:state/<name> shapes and knows their kinds.
        Assert.Equal("state", schema.StateShapes!.Value.GetProperty("library").GetString());
        Assert.Contains(schema.StateShapes.Value.GetProperty("shapes").EnumerateArray(),
            shape => shape.GetProperty("name").GetString() == "composite-state" && shape.GetProperty("kind").GetString() == "composite-state");

        // The basic shape library comes back too, so an agent can place custom:basic/<name> shapes on any kind.
        Assert.Equal("basic", schema.BasicShapes!.Value.GetProperty("library").GetString());
        Assert.Contains(schema.BasicShapes.Value.GetProperty("shapes").EnumerateArray(),
            shape => shape.GetProperty("name").GetString() == "callout" && shape.GetProperty("annotation").GetBoolean());

        // The BPMN library comes back too: every shape with the kind that names it, and the glyph line art.
        Assert.Equal("bpmn", schema.BpmnShapes!.Value.GetProperty("library").GetString());
        Assert.Contains(schema.BpmnShapes.Value.GetProperty("shapes").EnumerateArray(),
            shape => shape.GetProperty("name").GetString() == "user-task" && shape.GetProperty("kind").GetString() == "bpmn-user-task");
        Assert.True(schema.BpmnShapes.Value.GetProperty("glyphs").TryGetProperty("message", out _));
        // The flowchart vocabulary teaches each BPMN kind and the message-flow edge kind.
        Assert.Contains(schema.KindVocabulary.Value.GetProperty("nodeKinds").EnumerateArray(),
            kind => kind.GetProperty("name").GetString() == "bpmn-exclusive-gateway" && kind.GetProperty("description").GetString()!.Contains("label every outgoing flow", StringComparison.Ordinal));
        Assert.Contains(schema.KindVocabulary.Value.GetProperty("edgeKinds").EnumerateArray(),
            kind => kind.GetProperty("name").GetString() == "message-flow");

        // The UML class shapes come back too, with the metrics and the members protocol in the library's description.
        Assert.Equal("uml", schema.UmlShapes!.Value.GetProperty("library").GetString());
        Assert.Contains(schema.UmlShapes.Value.GetProperty("shapes").EnumerateArray(),
            shape => shape.GetProperty("name").GetString() == "class" && shape.GetProperty("kind").GetString() == "class");
        Assert.Contains("props.attributes", schema.UmlShapes.Value.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(18, schema.UmlShapes.Value.GetProperty("metrics").GetProperty("rowHeight").GetInt32());

        // The ER entity shapes come back too: an agent can write a table with columns from get_schema alone, so the
        // library's description carries the column line format and the kind vocabulary carries the same one-liner.
        Assert.Equal("er", schema.ErShapes!.Value.GetProperty("library").GetString());
        Assert.Contains(schema.ErShapes.Value.GetProperty("shapes").EnumerateArray(),
            shape => shape.GetProperty("name").GetString() == "weak-entity" && shape.GetProperty("kind").GetString() == "weak-entity");
        string erDescription = schema.ErShapes.Value.GetProperty("description").GetString()!;
        Assert.Contains("props.columns", erDescription, StringComparison.Ordinal);
        Assert.Contains("[PK] [FK] name type [not null | null]", erDescription, StringComparison.Ordinal);
        Assert.True(schema.ErShapes.Value.GetProperty("examples").GetArrayLength() > 10);
        Assert.Contains(_tools.GetSchema("architecture").KindVocabulary!.Value.GetProperty("nodeKinds").EnumerateArray(),
            kind => kind.GetProperty("name").GetString() == "entity" && kind.GetProperty("description").GetString()!.Contains("props.columns", StringComparison.Ordinal));
    }

    [Fact]
    public void GetSchema_without_a_type_returns_only_the_document_schema()
    {
        GetSchemaResult schema = _tools.GetSchema();
        Assert.Null(schema.KindVocabulary);
        Assert.Null(schema.GenericIcons);
        Assert.Null(schema.StateShapes);
        Assert.Null(schema.BpmnShapes);
        Assert.Null(schema.UmlShapes);
        Assert.Null(schema.ErShapes);
        Assert.Null(schema.BasicShapes);
    }

    [Fact]
    public async Task Patch_document_deleted_between_read_and_checkpoint_fails_cleanly()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");
        _store.OnBeforeCreateVersionAsync = id => _store.DeleteDocument(id);

        McpException ex = await Assert.ThrowsAsync<McpException>(() => _tools.PatchDocument(created.Id, "[]"));
        Assert.Contains("deleted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Patch_written_from_a_base_version_another_writer_moved_on_writes_nothing()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");
        _store.OnBeforeCreateVersionAsync = async id =>
        {
            Document current = (await _store.GetDocument(id))!;
            await _store.ReplaceDocument(current with { Title = "Saved in between" }, DocumentVersionTag.Compute(current));
        };

        McpException ex = await Assert.ThrowsAsync<McpException>(
            () => _tools.PatchDocument(created.Id, """[{"op":"updateNode","id":"n_a","set":{"label":"Patched"}}]"""));
        Assert.Contains("changed by another writer", ex.Message, StringComparison.OrdinalIgnoreCase);

        Document stored = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id));
        Assert.Equal("Saved in between", stored.Title);
        Assert.DoesNotContain(stored.Nodes, n => n.Label == "Patched");
    }

    [Fact]
    public async Task Agent_writes_preserve_provenance_through_json_and_dsl()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl", actor: "agent:claude/model");
        Document first = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id));
        AiProvenance initial = Assert.Single(first.Meta.AiProvenance!);
        Assert.Equal("claude", initial.Agent);
        Assert.Equal("model", initial.Model);
        Assert.Equal(AiAction.Created, initial.Action);
        Assert.Equal(new[] { "n_a" }, initial.ElementIds);

        await _tools.PatchDocument(created.Id, """[{"op":"updateNode","id":"n_a","set":{"label":"Changed"}}]""", actor: "agent:editor/model2");
        Document patched = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id));
        Assert.Equal(2, patched.Meta.AiProvenance!.Count);
        AiProvenance update = patched.Meta.AiProvenance[1];
        Assert.Equal("editor", update.Agent);
        Assert.Equal("model2", update.Model);
        Assert.Equal(AiAction.Updated, update.Action);
        Assert.Equal(new[] { "n_a" }, update.ElementIds);
        Document roundTrip = Draughtsman.Core.Text.Dsl.Parse(await _tools.ReadDocument(created.Id, "dsl")).Document;
        Assert.Equal(DocumentJson.Serialize(patched), DocumentJson.Serialize(roundTrip));

        await _tools.PatchDocument(created.Id, "[]", actor: "user:local");
        Assert.Equal(2, DocumentJson.Deserialize(await _tools.ReadDocument(created.Id)).Meta.AiProvenance!.Count);
    }

    [Fact]
    public async Task Human_writes_do_not_fabricate_provenance()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl", actor: "user:local");
        Assert.Null(DocumentJson.Deserialize(await _tools.ReadDocument(created.Id)).Meta.AiProvenance);
        PatchDocumentResult patched = await _tools.PatchDocument(created.Id, """[{"op":"updateNode","id":"n_a","set":{"label":"Changed"}}]""", actor: "user:local");
        Assert.Null(patched.Document.Meta.AiProvenance);
    }

    [Fact]
    public async Task Json_create_and_patch_reject_unknown_fields_before_writing()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(MinimalDslAsJson())!;
        json["nodes"]![0]!["unexpected"] = true;
        await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("T", "flowchart", json.ToJsonString(), "json"));
        Assert.Empty((await _tools.ListDocuments()).Items);

        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");
        string before = await _tools.ReadDocument(created.Id);
        await Assert.ThrowsAsync<McpException>(() => _tools.PatchDocument(created.Id, """[{"op":"restyle","target":"node","id":"n_a","style":{"strokeColour":"red"}}]"""));
        Assert.Equal(before, await _tools.ReadDocument(created.Id));
        Assert.Empty((await _store.ListVersions(created.Id, new VersionQuery())).Items);
        Assert.Single(await _store.ListAudit(created.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task Render_rejects_invalid_scale_before_loading_document(double scale)
    {
        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.RenderDocument("missing", scale: scale));
        Assert.Contains("scale", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patch_clears_optional_fields_and_style_overrides()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");
        await _tools.PatchDocument(created.Id, """
            [{"op":"updateNode","id":"n_a","set":{"parent":"n_container","ports":[]}},
             {"op":"restyle","target":"node","id":"n_a","style":{"fill":"red","stroke":"blue"}},
             {"op":"addEdge","edge":{"id":"e_1","kind":"flow","from":{"node":"n_a"},"to":{"node":"n_a"},"label":"loop","routing":{"mode":"curved"},"style":{"arrowStart":"arrow"}}}]
            """);
        PatchDocumentResult result = await _tools.PatchDocument(created.Id, """
            [{"op":"updateNode","id":"n_a","set":{"parent":null,"ports":null,"geometry":null}},
             {"op":"restyle","target":"node","id":"n_a","style":{"fill":null}},
             {"op":"updateEdge","id":"e_1","set":{"label":null,"routing":null}},
             {"op":"restyle","target":"edge","id":"e_1","style":{"arrowStart":null}}]
            """);
        Node node = Assert.Single(result.Document.Nodes);
        Assert.Null(node.Parent);
        Assert.Null(node.Ports);
        Assert.Null(node.Geometry);
        Assert.Null(node.Style!.Fill);
        Assert.Equal("blue", node.Style.Stroke);
        Edge edge = Assert.Single(result.Document.Edges);
        Assert.Null(edge.Label);
        Assert.Null(edge.Routing);
        Assert.Null(edge.Style);
    }

    [Theory]
    [InlineData("updateNode", "set", "label")]
    [InlineData("updateNode", "set", "unknown")]
    [InlineData("restyle", "style", "unknown")]
    public async Task Patch_rejects_null_required_or_unknown_fields(string operation, string payload, string field)
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");
        string before = await _tools.ReadDocument(created.Id);
        string ops = $$$"""[{"op":"{{{operation}}}","target":"node","id":"n_a","{{{payload}}}":{"{{{field}}}":null}}]""";
        await Assert.ThrowsAsync<McpException>(() => _tools.PatchDocument(created.Id, ops));
        Assert.Equal(before, await _tools.ReadDocument(created.Id));
        Assert.Empty((await _store.ListVersions(created.Id, new VersionQuery())).Items);
    }

    [Theory]
    [InlineData("nodes")]
    [InlineData("edges")]
    public async Task Create_rejects_duplicate_element_ids_without_writing(string collection)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(MinimalDslAsJson())!;
        var elements = json[collection]!.AsArray();
        if (collection == "edges")
        {
            elements.Add(System.Text.Json.Nodes.JsonNode.Parse("""{"id":"e_1","kind":"flow","from":{"node":"n_a"},"to":{"node":"n_a"}}"""));
        }

        elements.Add(elements[0]!.DeepClone());
        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("T", "flowchart", json.ToJsonString(), "json"));
        Assert.Contains("duplicate", error.Message, StringComparison.Ordinal);
        Assert.Empty((await _tools.ListDocuments()).Items);
    }

    [Fact]
    public async Task Provenance_lists_an_id_shared_by_a_node_and_an_edge_once()
    {
        string dsl = "draughtsman 1\ntype flowchart\ntitle \"T\"\nnode x process \"A\" @ 0,0 120x60\nnode y process \"B\" @ 200,0 120x60\nedge x flow x -> y\n";

        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", dsl, "dsl", actor: "agent:claude/model");

        Document stored = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id));
        AiProvenance entry = Assert.Single(stored.Meta.AiProvenance!);
        Assert.Equal(new[] { "x", "y" }, entry.ElementIds);

        Document roundTrip = Draughtsman.Core.Text.Dsl.Parse(await _tools.ReadDocument(created.Id, "dsl")).Document;
        Assert.Equal(DocumentJson.Serialize(stored), DocumentJson.Serialize(roundTrip));
    }

    [Fact]
    public async Task A_multi_op_patch_records_one_provenance_entry_for_every_element_it_touched()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl", actor: "agent:claude/model");

        string ops = """
            [{"op":"addNode","node":{"id":"n_b","kind":"process","label":"B","shape":"rectangle","geometry":{"x":200,"y":0,"w":120,"h":60}}},
             {"op":"addEdge","edge":{"id":"e_1","kind":"flow","from":{"node":"n_a"},"to":{"node":"n_b"}}},
             {"op":"updateNode","id":"n_a","set":{"label":"Changed"}}]
            """;
        await _tools.PatchDocument(created.Id, ops, actor: "agent:claude/model");

        Document stored = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id));
        Assert.Equal(2, stored.Meta.AiProvenance!.Count);
        AiProvenance patch = stored.Meta.AiProvenance[1];
        Assert.Equal(new[] { "n_b", "e_1", "n_a" }, patch.ElementIds);
    }

    [Fact]
    public async Task Outline_names_every_edge_so_a_patch_can_address_it()
    {
        string dsl = "draughtsman 1\ntype flowchart\ntitle \"T\"\nnode n_a process \"A\" @ 0,0 120x60\nnode n_b process \"B\" @ 200,0 120x60\nedge e_1 flow n_a -> n_b\nedge e_2 flow n_a -> n_b\n";
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", dsl, "dsl");

        string outline = await _tools.ReadDocument(created.Id, "outline");
        Assert.Contains("e_1", outline, StringComparison.Ordinal);
        Assert.Contains("e_2", outline, StringComparison.Ordinal);

        await _tools.PatchDocument(created.Id, """[{"op":"removeEdge","id":"e_2"}]""");
        Assert.DoesNotContain("e_2", await _tools.ReadDocument(created.Id, "outline"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_and_diff_report_duplicate_ids_a_stored_document_carries()
    {
        var node = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 120, 60) };
        var document = new Document("doc_dupe", 1, "T", DocumentType.Flowchart, [node, node with { Geometry = new Geometry(200, 0, 120, 60) }], [],
            new Meta(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "user:local"));
        await _store.CreateDocument(document);

        McpException render = await Assert.ThrowsAsync<McpException>(() => _tools.RenderDocument(document.Id));
        Assert.Contains("n_a", render.Message, StringComparison.Ordinal);

        McpException diff = await Assert.ThrowsAsync<McpException>(() => _tools.DiffDocuments(document.Id, "current"));
        Assert.Contains("n_a", diff.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListDocuments_rejects_a_workspace_that_does_not_exist()
    {
        await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl");

        McpException ex = await Assert.ThrowsAsync<McpException>(() => _tools.ListDocuments(workspace: "marketing"));
        Assert.Contains("workspace", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Single((await _tools.ListDocuments(workspace: "default")).Items);
        Assert.Single((await _tools.ListDocuments()).Items);
    }

    [Fact]
    public async Task Outline_keeps_a_multi_line_title_on_one_header_line()
    {
        CreateDocumentResult created = await _tools.CreateDocument("Checkout flow\nv2", "flowchart", MinimalDsl(), "dsl");

        string outline = await _tools.ReadDocument(created.Id, "outline");

        Assert.Equal("Checkout flow\\nv2 (flowchart)", outline.Split('\n')[0]);
        Assert.Equal("nodes:", outline.Split('\n')[1]);
    }

    [Fact]
    public async Task Create_with_layout_says_it_timed_out_rather_than_blaming_availability()
    {
        _sidecar.Failure = new SidecarTimeoutException("no answer in 15s");
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl", layout: true);

        string warning = Assert.Single(created.Warnings, w => w.Contains("layout was requested", StringComparison.Ordinal));
        Assert.Contains("timed out", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unavailable", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_with_layout_says_the_document_was_rejected_rather_than_blaming_availability()
    {
        _sidecar.Failure = new SidecarRpcException(SidecarRpcException.LayoutFailedCode, "ELK could not route the graph", null);
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", MinimalDsl(), "dsl", layout: true);

        string warning = Assert.Single(created.Warnings, w => w.Contains("layout was requested", StringComparison.Ordinal));
        Assert.Contains("rejected", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ELK could not route the graph", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Render_says_layout_timed_out_rather_than_blaming_availability()
    {
        var document = new Document("ignored", 1, "T", DocumentType.Flowchart,
            [new Node("n_a", "process", "A", "rectangle")], [], new Meta(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "user:local"));
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", DocumentJson.Serialize(document), "json", layout: false);

        _sidecar.Failure = new SidecarTimeoutException("no answer in 15s");
        McpException ex = await Assert.ThrowsAsync<McpException>(() => _tools.RenderDocument(created.Id));

        Assert.Contains("layout", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timed out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Outline_keeps_a_multi_line_kind_on_one_element_line()
    {
        var document = new Document("ignored", 1, "T", DocumentType.Flowchart,
            [new Node("n_a", "pro\ncess", "A", "rectangle") { Geometry = new Geometry(0, 0, 120, 60) }],
            [new Edge("e_1", "fl\now", new Endpoint("n_a"), new Endpoint("n_a"))],
            new Meta(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "user:local"));
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", DocumentJson.Serialize(document), "json");

        string[] lines = (await _tools.ReadDocument(created.Id, "outline")).Split('\n');

        Assert.Equal("  n_a [pro\\ncess] \"A\"", lines[2]);
        Assert.Equal("edges:", lines[3]);
        Assert.Equal("  e_1: n_a -> n_a [fl\\now]", lines[4]);
    }

    [Fact]
    public async Task Render_reports_the_edge_a_removed_node_left_dangling()
    {
        string dsl = "draughtsman 1\ntype flowchart\ntitle \"T\"\nnode n_a process \"A\" @ 0,0 120x60\nnode n_b process \"B\" @ 200,0 120x60\nedge e_1 flow n_a -> n_b\n";
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", dsl, "dsl");

        PatchDocumentResult patched = await _tools.PatchDocument(created.Id, """[{"op":"removeNode","id":"n_b"}]""");
        Assert.NotEmpty(patched.Warnings);

        RenderDocumentResult rendered = await _tools.RenderDocument(created.Id);

        Assert.DoesNotContain("data-id=\"e_1\"", rendered.Svg, StringComparison.Ordinal);
        string warning = Assert.Single(rendered.Warnings);
        Assert.Contains("e_1", warning, StringComparison.Ordinal);
        Assert.Contains("n_b", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Critique_returns_the_defects_of_a_stored_document()
    {
        string dsl = "draughtsman 1\ntype flowchart\ntitle \"T\"\nnode n_d decision \"Ok?\" @ 0,0 120x120\nnode n_p process \"Do\" @ 200,0 120x60\nedge e_1 flow n_d -> n_p \"Yes\"\n";
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", dsl, "dsl");

        CritiqueDocumentResult result = await _tools.CritiqueDocument(created.Id);

        Assert.Contains(result.Findings, f => f.Rule == CritiqueAnalyser.Rules.DecisionSingleBranch);
    }

    [Fact]
    public async Task Critique_reports_no_finding_caused_by_a_connected_palette_icon()
    {
        // Every generic icon, placed with the kind the palette gives it and connected to a partner, must review clean:
        // the unknown-kind lint never fires and no structural rule blames the icon.
        var nodes = new List<Node> { new("n_partner", "service", "Partner", "rounded") { Geometry = new Geometry(0, 0, 160, 80) } };
        var edges = new List<Edge>();
        int index = 0;
        foreach (GenericIcon icon in GenericIconLibrary.All)
        {
            string id = $"n_icon_{index}";
            nodes.Add(new Node(id, icon.PaletteKind, icon.Label, GenericIconLibrary.Prefix + icon.Name) { Geometry = new Geometry(300, index * 100, 160, 80) });
            edges.Add(new Edge($"e_{index}", "flow", new Endpoint("n_partner"), new Endpoint(id)));
            // A boundary icon is a container; a note inside keeps it from being (correctly) reported as empty.
            if (icon.PaletteKind == "container")
            {
                nodes.Add(new Node($"n_inside_{index}", "note", "Inside", "rectangle") { Geometry = new Geometry(320, index * 100 + 20, 60, 30), Parent = id });
            }

            index++;
        }

        Document document = new("doc_icons", Document.CurrentSchemaVersion, "Icons", DocumentType.Architecture, nodes, edges,
            new Meta(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test"));
        CreateDocumentResult created = await _tools.CreateDocument("Icons", "architecture", DocumentJson.Serialize(document), "json", layout: false);

        CritiqueDocumentResult result = await _tools.CritiqueDocument(created.Id);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Critique_on_unknown_document_fails_loudly()
    {
        await Assert.ThrowsAsync<McpException>(() => _tools.CritiqueDocument("doc_missing"));
    }

    [Fact]
    public async Task Critique_reports_no_finding_caused_by_a_connected_palette_state_shape()
    {
        // Every state shape, placed with the kind the palette gives it and connected to a partner, must review clean:
        // the unknown-kind lint never fires and no structural rule blames the shape.
        var nodes = new List<Node> { new("n_partner", "process", "Partner", "rounded") { Geometry = new Geometry(0, 0, 160, 80) } };
        var edges = new List<Edge>();
        int index = 0;
        foreach (StateShape shape in StateShapeLibrary.All)
        {
            string id = $"n_state_{index}";
            nodes.Add(new Node(id, shape.PaletteKind, shape.Label, StateShapeLibrary.ShapeName(shape)) { Geometry = new Geometry(300, index * 100, 160, 80) });
            edges.Add(new Edge($"e_{index}", "flow", new Endpoint("n_partner"), new Endpoint(id)));
            index++;
        }

        Document document = new("doc_states", Document.CurrentSchemaVersion, "States", DocumentType.Flowchart, nodes, edges,
            new Meta(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test"));
        CreateDocumentResult created = await _tools.CreateDocument("States", "flowchart", DocumentJson.Serialize(document), "json", layout: false);

        CritiqueDocumentResult result = await _tools.CritiqueDocument(created.Id);

        Assert.Empty(result.Findings);
    }

    private static string MinimalDsl()
    {
        return "draughtsman 1\ntype flowchart\ntitle \"T\"\nnode n_a process \"A\" @ 0,0 120x60\n";
    }

    private static string MinimalDslAsJson()
    {
        return DocumentJson.Serialize(Draughtsman.Core.Text.Dsl.Parse(MinimalDsl()).Document);
    }
}
