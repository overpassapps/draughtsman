using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Diffing;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Draughtsman.Core.Text;
using Draughtsman.Mcp.Patching;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Mcp.Tools;
using Draughtsman.Sidecar;
using ModelContextProtocol;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// The agent path for a sequence document: create from DSL, patch mid-stream, diff a reorder, outline in time order,
/// critique, and render from the sidecar's derived scene (against fakes; Draughtsman.Server.Tests runs the same through
/// the real sidecar).
/// </summary>
public class SequenceToolsTests
{
    private static readonly string s_dsl = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "checkout-sequence.dsl"));

    private readonly FakeStore _store = new();
    private readonly FakeSidecarClient _sidecar = new();
    private readonly DraughtsmanTools _tools;

    public SequenceToolsTests()
    {
        _tools = new DraughtsmanTools(_store, _sidecar, new FakePdfRenderer(), new FakeAssetReader(), new NullActorContext());
    }

    private async Task<string> CreateCheckout(bool layout = false)
    {
        CreateDocumentResult created = await _tools.CreateDocument("Checkout", "sequence", s_dsl, "dsl", layout);
        return created.Id;
    }

    private async Task<string[]> EdgeOrder(string id) => [.. DocumentJson.Deserialize(await _tools.ReadDocument(id, "json")).Edges.Select(e => e.Id)];

    // ---- create / layout ----

    [Fact]
    public async Task CreateDocument_takes_a_sequence_from_dsl_with_no_geometry_and_never_lays_it_out()
    {
        string id = await CreateCheckout(layout: true);

        Document stored = DocumentJson.Deserialize(await _tools.ReadDocument(id, "json"));

        Assert.Equal(DocumentType.Sequence, stored.Type);
        // The fake sidecar gives every node without one a geometry when asked to lay a document out, so none having one
        // proves layout=true was not honoured for this type.
        Assert.All(stored.Nodes, n => Assert.Null(n.Geometry));
    }

    [Fact]
    public async Task CreateDocument_rejects_an_unknown_type_naming_sequence_among_the_valid_ones()
    {
        McpException ex = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("T", "sequnce", s_dsl, "dsl"));

        Assert.Contains("'sequence'", ex.Message, StringComparison.Ordinal);
    }

    private const string Mermaid = "sequenceDiagram\n  actor U as Customer\n  participant API as Orders API\n  U->>+API: POST /orders\n  autonumber\n  API-->>-U: 201 Created\n";

    [Fact]
    public async Task CreateDocument_takes_a_mermaid_sequenceDiagram_as_a_sequence_and_reports_what_it_skipped()
    {
        CreateDocumentResult created = await _tools.CreateDocument("Orders", "sequence", Mermaid, "mermaid", layout: true);

        Document stored = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Assert.Equal(DocumentType.Sequence, stored.Type);
        Assert.Equal("Orders", stored.Title);
        Assert.Equal(["p_U", "p_API"], stored.Nodes.Select(n => n.Id));
        Assert.Equal(["POST /orders", "201 Created"], stored.Edges.Select(e => e.Label));
        Assert.Equal(["sync", "return"], stored.Edges.Select(e => e.Kind));
        Assert.All(stored.Nodes, n => Assert.Null(n.Geometry));
        string warning = Assert.Single(created.Warnings);
        Assert.StartsWith("unsupported-syntax (line 5):", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_document_as_mermaid_gives_the_sequence_text_in_time_order_and_refuses_an_unknown_format()
    {
        string id = await CreateCheckout();

        string mermaid = await _tools.ReadDocument(id, "mermaid");

        Assert.StartsWith("sequenceDiagram\n    title Checkout\n    actor user as Customer\n", mermaid, StringComparison.Ordinal);
        Assert.Contains("\n    web->>+api: POST /orders\n    api->>+pay: charge(total)\n    pay-->>-api: receipt\n", mermaid, StringComparison.Ordinal);
        McpException unknown = await Assert.ThrowsAsync<McpException>(() => _tools.ReadDocument(id, "svg"));
        Assert.Contains("'mermaid'", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mermaid_alt_block_with_a_note_becomes_a_fragment_the_outline_and_critique_understand()
    {
        string source = "sequenceDiagram\n  A->>B: charge\n  alt card ok\n    B-->>A: receipt\n    Note over A,B: PCI scope\n  else declined\n    B-->>A: declined\n  end\n";
        CreateDocumentResult created = await _tools.CreateDocument("Alt", "sequence", source, "mermaid");

        string outline = await _tools.ReadDocument(created.Id, "outline");
        CritiqueDocumentResult critique = await _tools.CritiqueDocument(created.Id);

        Assert.Empty(created.Warnings);
        Assert.Contains("  f_1 [fragment] \"alt\"\n", outline, StringComparison.Ordinal);
        Assert.Contains("  2. m_2: p_B -> p_A [return] \"receipt\" in f_1\n", outline, StringComparison.Ordinal);
        Assert.Contains("  3. n_1: p_A -> p_B [note] \"PCI scope\" in f_1\n", outline, StringComparison.Ordinal);
        Assert.Contains("  4. m_3: p_B -> p_A [return] \"declined\" in f_1 operand 1\n", outline, StringComparison.Ordinal);
        Assert.Empty(critique.Findings);
    }

    [Fact]
    public async Task A_mermaid_sequence_reads_back_in_time_order_and_a_stray_plus_is_critiqued()
    {
        string source = "sequenceDiagram\n  A->>+B: opens a bar nothing closes\n  B-->>A: reply\n";
        CreateDocumentResult created = await _tools.CreateDocument("Open bar", "sequence", source, "mermaid");

        string outline = await _tools.ReadDocument(created.Id, "outline");
        CritiqueDocumentResult critique = await _tools.CritiqueDocument(created.Id);

        Assert.Contains("  1. m_1: p_A -> p_B [sync] \"opens a bar nothing closes\" +activate\n", outline, StringComparison.Ordinal);
        Assert.Contains("  2. m_2: p_B -> p_A [return] \"reply\"\n", outline, StringComparison.Ordinal);
        Assert.Equal(["m_1"], Assert.Single(critique.Findings, f => f.Rule == CritiqueAnalyser.Rules.UnbalancedActivation).EdgeIds);
    }

    [Theory]
    [InlineData("flowchart")]
    [InlineData("architecture")]
    [InlineData("freeform")]
    public async Task CreateDocument_refuses_a_mermaid_sequenceDiagram_typed_as_anything_else_and_stores_nothing(string type)
    {
        McpException ex = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Orders", type, Mermaid, "mermaid"));

        Assert.Contains("sequenceDiagram", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'sequence'", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, (await _store.ListDocuments(new DocumentQuery())).Total);
    }

    [Fact]
    public async Task CreateDocument_refuses_a_mermaid_flowchart_typed_sequence_and_stores_nothing()
    {
        McpException ex = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Flow", "sequence", "flowchart TD\n  A --> B\n", "mermaid"));

        Assert.Contains("not one", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, (await _store.ListDocuments(new DocumentQuery())).Total);
    }

    [Fact]
    public async Task GetSchema_type_sequence_returns_the_vocabulary_that_teaches_edge_order()
    {
        GetSchemaResult schema = _tools.GetSchema("sequence");

        Assert.NotNull(schema.KindVocabulary);
        Assert.Contains("ORDER OF THE EDGES ARRAY IS TIME ORDER", schema.KindVocabulary!.Value.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(["sync", "async", "return", "note", "interaction-use", "create", "destroy"], schema.KindVocabulary.Value.GetProperty("edgeKinds").EnumerateArray().Select(k => k.GetProperty("name").GetString()!));
    }

    // ---- patch_document: addEdge before/after, moveEdge ----

    [Fact]
    public async Task PatchDocument_addEdge_before_inserts_mid_stream_and_after_puts_it_after()
    {
        string id = await CreateCheckout();

        await _tools.PatchDocument(id, """[{"op":"addEdge","before":"m_3","edge":{"id":"m_a","kind":"sync","label":"validate","from":{"node":"p_api"},"to":{"node":"p_api"}}}]""");
        Assert.Equal(["m_1", "m_2", "m_a", "m_3", "m_4", "m_5", "m_6", "m_7"], await EdgeOrder(id));

        await _tools.PatchDocument(id, """[{"op":"addEdge","after":"m_7","edge":{"id":"m_z","kind":"async","label":"audit-done","from":{"node":"p_api"},"to":{"node":"p_user"}}}]""");
        Assert.Equal(["m_1", "m_2", "m_a", "m_3", "m_4", "m_5", "m_6", "m_7", "m_z"], await EdgeOrder(id));
    }

    [Fact]
    public async Task PatchDocument_moveEdge_reorders_and_changes_nothing_else()
    {
        string id = await CreateCheckout();
        Document before = DocumentJson.Deserialize(await _tools.ReadDocument(id, "json"));

        PatchDocumentResult result = await _tools.PatchDocument(id, """[{"op":"moveEdge","id":"m_5","before":"m_2"}]""");

        Assert.Equal(["m_1", "m_5", "m_2", "m_3", "m_4", "m_6", "m_7"], result.Document.Edges.Select(e => e.Id));
        string[] Json(IEnumerable<Edge> edges) => [.. edges.OrderBy(e => e.Id, StringComparer.Ordinal).Select(e => JsonSerializer.Serialize(e, DocumentJson.Options))];
        Assert.Equal(Json(before.Edges), Json(result.Document.Edges));
        Assert.Equal(JsonSerializer.Serialize(before.Nodes, DocumentJson.Options), JsonSerializer.Serialize(result.Document.Nodes, DocumentJson.Options));
    }

    [Fact]
    public void AddEdge_at_a_position_lands_there_in_the_working_json()
    {
        JsonObject document = Working();

        PatchApplier.Apply(document, Ops("""[{"op":"addEdge","after":"e_1","edge":{"id":"e_x","kind":"sync","from":{"node":"n_a"},"to":{"node":"n_b"}}}]"""));

        Assert.Equal(["e_1", "e_x", "e_2", "e_3"], EdgeIds(document));
    }

    [Theory]
    [InlineData("""[{"op":"addEdge","before":"e_ghost","edge":{"id":"e_x","kind":"sync","from":{"node":"n_a"},"to":{"node":"n_b"}}}]""", "e_ghost")]
    [InlineData("""[{"op":"addEdge","after":"e_ghost","edge":{"id":"e_x","kind":"sync","from":{"node":"n_a"},"to":{"node":"n_b"}}}]""", "e_ghost")]
    [InlineData("""[{"op":"addEdge","before":"e_1","after":"e_2","edge":{"id":"e_x","kind":"sync","from":{"node":"n_a"},"to":{"node":"n_b"}}}]""", "not both")]
    [InlineData("""[{"op":"addEdge","before":"","edge":{"id":"e_x","kind":"sync","from":{"node":"n_a"},"to":{"node":"n_b"}}}]""", "must be the id")]
    [InlineData("""[{"op":"addEdge","before":7,"edge":{"id":"e_x","kind":"sync","from":{"node":"n_a"},"to":{"node":"n_b"}}}]""", "must be the id")]
    public void AddEdge_with_a_bad_position_fails_the_op_rather_than_appending(string ops, string expected)
    {
        JsonObject document = Working();

        PatchException ex = Assert.Throws<PatchException>(() => PatchApplier.Apply(document, Ops(ops)));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{"op":"moveEdge","id":"e_3","before":"e_1"}]""", new[] { "e_3", "e_1", "e_2" })]
    [InlineData("""[{"op":"moveEdge","id":"e_1","after":"e_3"}]""", new[] { "e_2", "e_3", "e_1" })]
    [InlineData("""[{"op":"moveEdge","id":"e_1","after":"e_2"}]""", new[] { "e_2", "e_1", "e_3" })]
    [InlineData("""[{"op":"moveEdge","id":"e_2","before":"e_2"}]""", null)]
    [InlineData("""[{"op":"moveEdge","id":"e_2"}]""", null)]
    [InlineData("""[{"op":"moveEdge","id":"e_ghost","before":"e_1"}]""", null)]
    [InlineData("""[{"op":"moveEdge","id":"e_1","after":"e_ghost"}]""", null)]
    [InlineData("""[{"op":"moveEdge","id":"e_1","before":"e_2","after":"e_3"}]""", null)]
    public void MoveEdge_puts_an_edge_next_to_another_and_refuses_anything_that_names_no_valid_place(string ops, string[]? expected)
    {
        JsonObject document = Working();
        string[] original = EdgeIds(document);

        if (expected is null)
        {
            Assert.Throws<PatchException>(() => PatchApplier.Apply(document, Ops(ops)));
            // A refused op leaves the array whole and in its original order.
            Assert.Equal(original, EdgeIds(document));
        }
        else
        {
            PatchApplier.Apply(document, Ops(ops));
            Assert.Equal(expected, EdgeIds(document));
        }
    }

    [Fact]
    public void An_unknown_op_names_moveEdge_among_the_ones_it_expected()
    {
        PatchException ex = Assert.Throws<PatchException>(() => PatchApplier.Apply(Working(), Ops("""[{"op":"moveNode","id":"n_a"}]""")));

        Assert.Contains("moveEdge", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatchDocument_records_the_moved_edge_in_the_provenance_of_an_agent_write()
    {
        string id = await CreateCheckout();

        PatchDocumentResult result = await _tools.PatchDocument(id, """[{"op":"moveEdge","id":"m_5","after":"m_1"}]""", actor: "agent:test/model");

        Assert.Contains("m_5", result.Document.Meta.AiProvenance!.Last().ElementIds);
    }

    // ---- diff_documents: a reorder is a change ----

    [Fact]
    public async Task DiffDocuments_reports_a_pure_reorder_as_the_moved_edge_and_where_it_now_sits()
    {
        string id = await CreateCheckout();
        PatchDocumentResult patched = await _tools.PatchDocument(id, """[{"op":"moveEdge","id":"m_5","after":"m_1"}]""");

        DiffDocumentsResult diff = await _tools.DiffDocuments(id, patched.CheckpointVersionId, "current");

        // Nothing was added, removed or changed by field: only the order differs, and that must not read as "no change".
        Assert.Empty(diff.Diff.AddedEdges);
        Assert.Empty(diff.Diff.RemovedEdges);
        Assert.Empty(diff.Diff.ChangedEdges);
        ElementMove move = Assert.Single(diff.Diff.MovedEdges);
        Assert.Equal(new ElementMove("m_5", "m_1"), move);
        Assert.Empty(diff.Diff.MovedNodes);
    }

    [Fact]
    public void Compare_names_the_fewest_moves_and_is_silent_for_an_identical_order()
    {
        Document document = DocumentJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "checkout-sequence.json")));
        Edge[] e = [.. document.Edges];

        Assert.Empty(DocumentDiff.Compare(document, document).MovedEdges);

        // One message jumping from the last row to the first is one move, not the six it jumped over.
        Document lastToFirst = document with { Edges = [e[6], .. e[..6]] };
        Assert.Equal([new ElementMove("m_7", null)], DocumentDiff.Compare(document, lastToFirst).MovedEdges);

        // Swapping two neighbours is one move too (the later one now follows the earlier's old predecessor).
        Document swapped = document with { Edges = [e[0], e[2], e[1], .. e[3..]] };
        DocumentDiffResult diff = DocumentDiff.Compare(document, swapped);
        Assert.Single(diff.MovedEdges);
        Assert.Empty(diff.ChangedEdges);

        // An added or removed edge is not a move, and the shared ones keep their relative order.
        Document withoutOne = document with { Edges = [.. e.Where(edge => edge.Id != "m_3")] };
        Assert.Empty(DocumentDiff.Compare(document, withoutOne).MovedEdges);
        Assert.Single(DocumentDiff.Compare(document, withoutOne).RemovedEdges);

        // Participant order is a reorder too.
        Document nodesSwapped = document with { Nodes = [document.Nodes[1], document.Nodes[0], .. document.Nodes.Skip(2)] };
        Assert.Single(DocumentDiff.Compare(document, nodesSwapped).MovedNodes);
    }

    // ---- outline / critique ----

    [Fact]
    public async Task ReadDocument_outline_numbers_the_messages_in_time_order_and_shows_the_activation_marks()
    {
        string id = await CreateCheckout();

        string outline = await _tools.ReadDocument(id, "outline");

        Assert.Contains("edges (in time order):", outline, StringComparison.Ordinal);
        Assert.Contains("  1. m_1: p_user -> p_web [sync] \"Place order\"\n", outline, StringComparison.Ordinal);
        Assert.Contains("  2. m_2: p_web -> p_api [sync] \"POST /orders\" +activate\n", outline, StringComparison.Ordinal);
        Assert.Contains("  4. m_4: p_pay -> p_api [return] \"receipt\" +deactivate\n", outline, StringComparison.Ordinal);
        Assert.Contains("  7. m_7: p_api -> p_api [sync] \"audit()\"\n", outline, StringComparison.Ordinal);
    }

    [Fact]
    public void Outline_of_any_other_type_keeps_its_unnumbered_edges()
    {
        Document flowchart = Dsl.Parse("draughtsman 1\ntype flowchart\ntitle \"T\"\nnode n_a process \"A\" @ 0,0 120x60\nnode n_b process \"B\" @ 200,0 120x60\nedge e_1 flow n_a -> n_b\n").Document;

        string outline = Outline.Render(flowchart);

        Assert.Contains("edges:\n  e_1: n_a -> n_b [flow]\n", outline, StringComparison.Ordinal);
        Assert.DoesNotContain("time order", outline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CritiqueDocument_flags_a_stray_deactivate_on_a_stored_sequence()
    {
        string id = await CreateCheckout();
        await _tools.PatchDocument(id, """[{"op":"setProp","target":"edge","id":"m_1","key":"deactivate","value":true}]""");

        CritiqueDocumentResult result = await _tools.CritiqueDocument(id);

        CritiqueFinding finding = Assert.Single(result.Findings, f => f.Rule == CritiqueAnalyser.Rules.UnbalancedActivation);
        Assert.Equal(["m_1"], finding.EdgeIds);
        Assert.Equal(["p_user"], finding.NodeIds);
    }

    // ---- render_document ----

    private static SequenceScene SceneOfCheckout() => new(
        [new SequenceHeader("p_user", 20, 20, 120, 40, 80), new SequenceHeader("p_web", 160, 20, 120, 40, 220)],
        [new SequenceLifeline("p_user", 80, 60, 200), new SequenceLifeline("p_web", 220, 60, 200)],
        [
            new SequenceMessage("m_1", [new Point(80, 108), new Point(220, 108)], 108, false, 90, 126, new Point(150, 94), 60),
            new SequenceMessage("m_4", [new Point(220, 150), new Point(80, 150)], 150, false, 132, 168, new Point(150, 136), 40),
        ],
        [new SequenceBar("p_web", "m_1", 0, 215, 108, 10, 42)],
        [],
        [],
        new SceneRect(0, 0, 300, 220));

    private static Document TwoParticipants()
    {
        Document full = DocumentJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "checkout-sequence.json")));
        return full with
        {
            Nodes = [.. full.Nodes.Take(2)],
            Edges = [full.Edges[0], full.Edges[3] with { From = new Endpoint("p_web"), To = new Endpoint("p_user"), Props = null }],
        };
    }

    [Fact]
    public void SvgRenderer_draws_a_sequence_from_its_scene_with_no_layout_of_its_own()
    {
        SvgRenderResult result = SvgRenderer.Render(TwoParticipants(), sequence: SceneOfCheckout());
        XDocument xml = XDocument.Parse(result.Svg);

        Assert.Empty(result.Warnings);
        Assert.Equal("0 0 300 220", (string?)xml.Root!.Attribute("viewBox"));
        XElement[] lifelines = [.. xml.Descendants().Where(e => e.Name.LocalName == "line" && e.Attribute("data-lifeline") is not null)];
        Assert.Equal(["p_user", "p_web"], lifelines.Select(l => (string?)l.Attribute("data-lifeline")));
        Assert.Equal(["80", "60", "200"], new[] { "x1", "y1", "y2" }.Select(a => (string?)lifelines[0].Attribute(a)));
        Assert.All(lifelines, l => Assert.Equal("6 4", (string?)l.Attribute("stroke-dasharray")));
        XElement bar = Assert.Single(xml.Descendants(), e => e.Name.LocalName == "rect" && (string?)e.Attribute("data-lifeline") == "p_web");
        Assert.Equal(["215", "108", "10", "42"], new[] { "x", "y", "width", "height" }.Select(a => (string?)bar.Attribute(a)));

        // A participant is a header box at the scene's position, with its label.
        XElement header = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "p_web");
        Assert.Equal("160", (string?)header.Elements().First(e => e.Name.LocalName is "rect").Attribute("x"));
        Assert.Contains(header.Descendants(), e => e.Name.LocalName == "text" && e.Value == "Web app");
    }

    [Fact]
    public void SvgRenderer_draws_a_message_along_the_scenes_points_in_the_kinds_own_look_with_its_label_above()
    {
        XDocument xml = XDocument.Parse(SvgRenderer.Render(TwoParticipants(), sequence: SceneOfCheckout()).Svg);
        XElement sync = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "m_1");
        XElement reply = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "m_4");

        // sync: solid, filled head; return: dashed, open head (the theme's kind styles, which the vocabulary's defaultStyle mirrors).
        XElement syncLine = sync.Elements().Single(e => e.Name.LocalName == "line");
        XElement replyLine = reply.Elements().Single(e => e.Name.LocalName == "line");
        Assert.Equal(["80", "108", "220", "108"], new[] { "x1", "y1", "x2", "y2" }.Select(a => (string?)syncLine.Attribute(a)));
        Assert.Null(syncLine.Attribute("stroke-dasharray"));
        Assert.Contains("arrow-arrow", (string?)syncLine.Attribute("marker-end"), StringComparison.Ordinal);
        Assert.Equal(["220", "150", "80", "150"], new[] { "x1", "y1", "x2", "y2" }.Select(a => (string?)replyLine.Attribute(a)));
        Assert.Equal("6 4", (string?)replyLine.Attribute("stroke-dasharray"));
        Assert.Contains("arrow-open", (string?)replyLine.Attribute("marker-end"), StringComparison.Ordinal);

        // The label is centred where the scene put it (above the line at y=108), not on it.
        XElement label = Assert.Single(sync.Descendants(), e => e.Name.LocalName == "text");
        Assert.Equal("150", (string?)label.Attribute("x"));
        Assert.Equal("94", (string?)label.Attribute("y"));
        Assert.Equal("136", (string?)Assert.Single(reply.Descendants(), e => e.Name.LocalName == "text").Attribute("y"));
    }

    [Fact]
    public void SvgRenderer_lets_an_edges_own_style_win_over_its_kinds_default()
    {
        Document document = TwoParticipants();
        document = document with { Edges = [document.Edges[0], document.Edges[1] with { Style = new EdgeStyle { Dash = Dash.Solid, Stroke = "#ff0000" } }] };

        XDocument xml = XDocument.Parse(SvgRenderer.Render(document, sequence: SceneOfCheckout()).Svg);

        XElement replyLine = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "m_4").Elements().Single(e => e.Name.LocalName == "line");
        Assert.Null(replyLine.Attribute("stroke-dasharray"));
        Assert.Equal("#ff0000", (string?)replyLine.Attribute("stroke"));
        Assert.Contains("arrow-open", (string?)replyLine.Attribute("marker-end"), StringComparison.Ordinal);
    }

    [Fact]
    public void SvgRenderer_paints_a_sequence_with_the_shipped_diagram_themes_colours_on_its_export_paper()
    {
        XDocument xml = XDocument.Parse(SvgRenderer.Render(TwoParticipants(), sequence: SceneOfCheckout()).Svg);
        static string? Attr(XElement element, string name) => (string?)element.Attribute(name);
        XElement Group(string id) => Assert.Single(xml.Descendants(), e => Attr(e, "data-id") == id);

        // The overpass theme, as web/src/canvas/sequenceDiagram.test.tsx pins for the client's export of the same kinds.
        Assert.Equal("#FFFFFF", Attr(xml.Root!.Elements().First(e => e.Name.LocalName == "rect"), "fill"));
        XElement web = Group("p_web").Elements().First(e => e.Name.LocalName == "rect");
        Assert.Equal(["#202a42", "#7f8aa0"], [Attr(web, "fill")!, Attr(web, "stroke")!]);
        Assert.Equal("#F2F2F2", Attr(Group("p_web").Descendants().Single(e => e.Name.LocalName == "text"), "fill"));
        Assert.Equal("#7f8aa0", Attr(xml.Descendants().First(e => e.Name.LocalName == "line" && Attr(e, "data-lifeline") == "p_web"), "stroke"));
        XElement bar = xml.Descendants().Single(e => e.Name.LocalName == "rect" && Attr(e, "data-lifeline") == "p_web");
        Assert.Equal(["#202a42", "#7f8aa0"], [Attr(bar, "fill")!, Attr(bar, "stroke")!]);
        Assert.Equal("#7f8aa0", Attr(Group("m_1").Elements().Single(e => e.Name.LocalName == "line"), "stroke"));
        // An edge label sits over a plate of the paper's colour, in the text colour that reads on it.
        Assert.Equal("#FFFFFF", Attr(Group("m_1").Elements().Single(e => e.Name.LocalName == "rect"), "fill"));
        Assert.Equal("#111111", Attr(Group("m_1").Elements().Single(e => e.Name.LocalName == "text"), "fill"));
    }

    [Fact]
    public void SvgRenderer_lays_a_documents_token_overrides_over_the_theme_and_reads_a_dark_paper_with_light_text()
    {
        Document document = TwoParticipants() with
        {
            Theme = new Dictionary<string, string> { ["palette.brand"] = "#123456", ["nodes.participant.fill"] = "token:brand", ["exportBackground"] = "#000000" },
        };

        XDocument xml = XDocument.Parse(SvgRenderer.Render(document, sequence: SceneOfCheckout()).Svg);

        Assert.Equal("#000000", (string?)xml.Root!.Elements().First(e => e.Name.LocalName == "rect").Attribute("fill"));
        XElement web = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "p_web");
        Assert.Equal("#123456", (string?)web.Elements().First(e => e.Name.LocalName == "rect").Attribute("fill"));
        XElement m1 = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "m_1");
        Assert.Equal("#000000", (string?)m1.Elements().Single(e => e.Name.LocalName == "rect").Attribute("fill"));
        Assert.Equal("#F2F2F2", (string?)m1.Elements().Single(e => e.Name.LocalName == "text").Attribute("fill"));
    }

    [Fact]
    public void SvgRenderer_draws_an_actor_as_a_stick_figure_with_its_name_under_it()
    {
        XDocument xml = XDocument.Parse(SvgRenderer.Render(TwoParticipants(), sequence: SceneOfCheckout()).Svg);
        XElement actor = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "p_user");
        static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

        // The header is 120 by 40 at (20, 20); the figure is 22 high (top 26 less 2 either side) centred on x 80, the name centred in the 14 under it (80, 53).
        XElement box = actor.Elements().First(e => e.Name.LocalName == "rect");
        Assert.Equal(["transparent", "none"], [Attr(box, "fill")!, Attr(box, "stroke")!]);
        XElement head = actor.Elements().Single(e => e.Name.LocalName == "circle");
        Assert.Equal(["80", "25.52", "3.52"], [Attr(head, "cx")!, Attr(head, "cy")!, Attr(head, "r")!]);
        Assert.Equal(["#151e33", "#7f8aa0"], [Attr(head, "fill")!, Attr(head, "stroke")!]);
        Assert.Equal("M80,29.04 V36.3 M72.08,31.68 H87.92 M73.84,44 L80,36.3 L86.16,44", Attr(actor.Elements().Single(e => e.Name.LocalName == "path"), "d"));
        XElement name = actor.Elements().Single(e => e.Name.LocalName == "text");
        Assert.Equal(["80", "53", "Customer"], [Attr(name, "x")!, Attr(name, "y")!, name.Value]);
        // A participant of another kind stays a box.
        Assert.DoesNotContain(xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "p_web").Elements(), e => e.Name.LocalName == "circle");
    }

    [Fact]
    public void SvgRenderer_draws_an_interaction_use_as_a_ref_frame_with_a_tab_and_the_name_under_it()
    {
        Document document = TwoParticipants();
        document = document with { Edges = [.. document.Edges, new Edge("u_1", "interaction-use", new Endpoint("p_user"), new Endpoint("p_web")) { Label = "Authenticate" }] };
        SequenceScene scene = SceneOfCheckout() with
        {
            Messages = [.. SceneOfCheckout().Messages, new SequenceMessage("u_1", [new Point(70, 132), new Point(230, 132), new Point(230, 180), new Point(70, 180), new Point(70, 132)], 156, false, 126, 186, new Point(150, 165), 80, new SequenceNote(70, 132, 160, 48, 31))],
        };

        SvgRenderResult result = SvgRenderer.Render(document, sequence: scene);
        XDocument xml = XDocument.Parse(result.Svg);
        XElement frame = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "u_1");
        static string? Attr(XElement element, string name) => (string?)element.Attribute(name);

        // Drawn as a frame by the sequence layer and not also as a line by the edge loop; the same literals the canvas test pins.
        Assert.Empty(result.Warnings);
        Assert.Equal("interaction-use", Attr(frame, "data-kind"));
        Assert.DoesNotContain(frame.Descendants(), e => e.Name.LocalName is "line" or "polyline");
        XElement box = frame.Elements().First(e => e.Name.LocalName == "rect");
        Assert.Equal(["70", "132", "160", "48", "#FFFFFF", "#7f8aa0"], [Attr(box, "x")!, Attr(box, "y")!, Attr(box, "width")!, Attr(box, "height")!, Attr(box, "fill")!, Attr(box, "stroke")!]);
        Assert.Equal("M 70 132 H 101 V 144 L 95 150 H 70 Z", Attr(frame.Elements().Single(e => e.Name.LocalName == "path"), "d"));
        XElement[] texts = [.. frame.Elements().Where(e => e.Name.LocalName == "text")];
        Assert.Equal(["ref", "Authenticate"], texts.Select(t => t.Value));
        Assert.Equal(["85.5", "141"], [Attr(texts[0], "x")!, Attr(texts[0], "y")!]);
        Assert.Equal(["150", "165"], [Attr(texts[1], "x")!, Attr(texts[1], "y")!]);
    }

    [Fact]
    public void SvgRenderer_refuses_a_sequence_it_was_given_no_scene_for()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SvgRenderer.Render(TwoParticipants()));

        Assert.Contains("sequence.scene", ex.Message, StringComparison.Ordinal);
    }

    private const string TwoDsl = "draughtsman 1\ntype sequence\ntitle \"Two\"\nnode p_user actor \"Customer\"\nnode p_web participant \"Web app\"\nedge m_1 sync p_user -> p_web \"Place order\"\nedge m_4 return p_web -> p_user \"receipt\"\n";

    [Fact]
    public async Task RenderDocument_asks_the_sidecar_for_the_scene_and_draws_it()
    {
        string id = (await _tools.CreateDocument("Two", "sequence", TwoDsl, "dsl")).Id;
        _sidecar.Scene = SceneOfCheckout();

        RenderDocumentResult rendered = await _tools.RenderDocument(id);

        Assert.Equal(1, _sidecar.SceneCalls);
        Assert.Contains("data-lifeline=\"p_web\"", rendered.Svg, StringComparison.Ordinal);
        Assert.Empty(rendered.Warnings);
    }

    [Fact]
    public async Task RenderDocument_refuses_a_sequence_when_the_sidecar_is_down_and_never_draws_a_guess()
    {
        string id = await CreateCheckout();
        _sidecar.Failure = new SidecarUnavailableException("node is not installed");

        McpException ex = await Assert.ThrowsAsync<McpException>(() => _tools.RenderDocument(id));

        Assert.Contains("sequence diagram", ex.Message, StringComparison.Ordinal);
        Assert.Contains("sidecar is unavailable", ex.Message, StringComparison.Ordinal);
        Assert.Contains("node is not installed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RenderDocument_names_a_sidecar_timeout_and_a_rejection_differently()
    {
        string id = await CreateCheckout();

        _sidecar.Failure = new SidecarTimeoutException("no answer in 30s");
        McpException timeout = await Assert.ThrowsAsync<McpException>(() => _tools.RenderDocument(id));
        Assert.Contains("timed out", timeout.Message, StringComparison.Ordinal);

        _sidecar.Failure = new SidecarRpcException(-32602, "document.type must be 'sequence'", null);
        McpException rejected = await Assert.ThrowsAsync<McpException>(() => _tools.RenderDocument(id));
        Assert.Contains("rejected the document", rejected.Message, StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static JsonObject Working() => JsonNode.Parse("""
        {"nodes":[{"id":"n_a","kind":"participant","label":"A","shape":"rectangle"},{"id":"n_b","kind":"participant","label":"B","shape":"rectangle"}],
         "edges":[{"id":"e_1","kind":"sync","from":{"node":"n_a"},"to":{"node":"n_b"}},{"id":"e_2","kind":"sync","from":{"node":"n_b"},"to":{"node":"n_a"}},{"id":"e_3","kind":"async","from":{"node":"n_a"},"to":{"node":"n_b"}}]}
        """)!.AsObject();

    private static JsonArray Ops(string json) => JsonNode.Parse(json)!.AsArray();

    private static string[] EdgeIds(JsonObject document) => [.. document["edges"]!.AsArray().Select(e => e!["id"]!.GetValue<string>())];
}
