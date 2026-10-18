using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Documents;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Drives the MCP tools over the real streamable-HTTP transport (build-spec-v0.1.md section 9): a genuine
/// <see cref="McpClient"/> talking JSON-RPC to the server's <c>/mcp</c> endpoint, backed by the same real sidecar
/// bundle the other server tests use (AGENTS.md), not the tools called in-process. Draughtsman.Mcp.Tests covers the
/// tool logic itself (lossless round trip, unaddressable targets, sidecar-down) against fakes; this class exists to
/// prove the wiring - names, argument shapes, transport - actually works end to end.
/// </summary>
public class McpHttpToolsTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public McpHttpToolsTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Lists_the_eight_tools_the_build_spec_names()
    {
        await using McpClient client = await Connect();

        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: CancellationToken.None);

        string[] names = tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "create_document", "critique_document", "diff_documents", "get_schema", "list_documents", "patch_document", "read_document", "render_document" },
            names);
    }

    [Fact]
    public async Task Create_patch_read_and_render_round_trip_over_the_real_transport()
    {
        await using McpClient client = await Connect();

        CallToolResult created = await client.CallToolAsync("create_document", new Dictionary<string, object?>
        {
            ["title"] = "Checkout flow",
            ["type"] = "flowchart",
            ["format"] = "dsl",
            ["content"] = "draughtsman 1\ntype flowchart\ntitle \"Ignored\"\nnode n_a process \"Start\" @ 0,0 120x60\n",
        }, cancellationToken: CancellationToken.None);
        AssertSucceeded(created);
        string id = TextOf(created).GetProperty("id").GetString()!;
        Assert.StartsWith("doc_", id, StringComparison.Ordinal);

        CallToolResult patched = await client.CallToolAsync("patch_document", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["ops"] = """[{"op":"addNode","node":{"id":"n_b","kind":"process","label":"End","shape":"rectangle","geometry":{"x":200,"y":0,"w":120,"h":60}}}]""",
        }, cancellationToken: CancellationToken.None);
        AssertSucceeded(patched);
        JsonElement patchedJson = TextOf(patched);
        Assert.NotEmpty(patchedJson.GetProperty("checkpointVersionId").GetString() ?? string.Empty);

        // patch_document's embedded Document must use the document model's JSON conventions, not the SDK defaults.
        JsonElement patchedDocument = patchedJson.GetProperty("document");
        Assert.Equal("flowchart", patchedDocument.GetProperty("type").GetString());
        Assert.EndsWith("Z", patchedDocument.GetProperty("meta").GetProperty("updatedAt").GetString() ?? string.Empty, StringComparison.Ordinal);

        CallToolResult read = await client.CallToolAsync("read_document", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["format"] = "json",
        }, cancellationToken: CancellationToken.None);
        JsonElement document = JsonDocument.Parse(TextContentOf(read)).RootElement;
        JsonElement[] nodes = document.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(2, nodes.Length);
        Assert.Contains(nodes, n => n.GetProperty("id").GetString() == "n_b");

        // patch_document did not lay the new node out (layout defaults to "none"); render must lay it out itself
        // before it can draw it, chaining to the real sidecar the way the /try page does.
        CallToolResult rendered = await client.CallToolAsync("render_document", new Dictionary<string, object?>
        {
            ["id"] = id,
        }, cancellationToken: CancellationToken.None);
        AssertSucceeded(rendered);
        JsonElement renderedJson = TextOf(rendered);
        string svg = renderedJson.GetProperty("svg").GetString() ?? string.Empty;
        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("data-id=\"n_b\"", svg, StringComparison.Ordinal);
        Assert.Empty(renderedJson.GetProperty("warnings").EnumerateArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Patch_document_ops_may_be_a_json_array_or_a_string_of_json(bool asArray)
    {
        await using McpClient client = await Connect();
        CallToolResult created = await client.CallToolAsync("create_document", new Dictionary<string, object?>
        {
            ["title"] = "Ops shapes",
            ["type"] = "flowchart",
            ["format"] = "dsl",
            ["content"] = "draughtsman 1\ntype flowchart\ntitle \"Ignored\"\nnode n_a process \"Start\" @ 0,0 120x60\n",
        }, cancellationToken: CancellationToken.None);
        string id = TextOf(created).GetProperty("id").GetString()!;
        const string OpsJson = """[{"op":"updateNode","id":"n_a","set":{"label":"Renamed"}}]""";

        CallToolResult patched = await client.CallToolAsync("patch_document", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["ops"] = asArray ? JsonDocument.Parse(OpsJson).RootElement : OpsJson,
        }, cancellationToken: CancellationToken.None);

        AssertSucceeded(patched);
        Assert.Equal("Renamed", TextOf(patched).GetProperty("document").GetProperty("nodes")[0].GetProperty("label").GetString());
    }

    [Theory]
    [InlineData("{\"op\":\"updateNode\"}")]
    [InlineData("not json")]
    public async Task Patch_document_refuses_ops_that_are_not_an_array(string ops)
    {
        await using McpClient client = await Connect();
        CallToolResult created = await client.CallToolAsync("create_document", new Dictionary<string, object?>
        {
            ["title"] = "Bad ops",
            ["type"] = "flowchart",
            ["format"] = "dsl",
            ["content"] = "draughtsman 1\ntype flowchart\ntitle \"Ignored\"\nnode n_a process \"Start\" @ 0,0 120x60\n",
        }, cancellationToken: CancellationToken.None);
        string id = TextOf(created).GetProperty("id").GetString()!;

        CallToolResult patched = await client.CallToolAsync("patch_document", new Dictionary<string, object?> { ["id"] = id, ["ops"] = ops }, cancellationToken: CancellationToken.None);

        Assert.True(patched.IsError);
        Assert.Contains("ops", TextContentOf(patched), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sequence_diagram_is_created_patched_read_and_rendered_through_the_real_sidecar()
    {
        using var licensed = new DraughtsmanServerFactory();
        await using McpClient client = await Connect(await licensed.CreateAuthenticatedClient());
        CallToolResult created = await client.CallToolAsync("create_document", new Dictionary<string, object?>
        {
            ["title"] = "Checkout",
            ["type"] = "sequence",
            ["format"] = "dsl",
            ["content"] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "checkout-sequence.dsl")),
            ["layout"] = true,
        }, cancellationToken: CancellationToken.None);
        AssertSucceeded(created);
        string id = TextOf(created).GetProperty("id").GetString()!;

        // Insert a message mid-stream, then read the outline back: the new message is numbered third.
        CallToolResult patched = await client.CallToolAsync("patch_document", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["ops"] = """[{"op":"addEdge","before":"m_3","edge":{"id":"m_a","kind":"sync","label":"validate","from":{"node":"p_api"},"to":{"node":"p_api"}}}]""",
        }, cancellationToken: CancellationToken.None);
        AssertSucceeded(patched);
        string outline = TextContentOf(await client.CallToolAsync("read_document", new Dictionary<string, object?> { ["id"] = id, ["format"] = "outline" }, cancellationToken: CancellationToken.None));
        Assert.Contains("  3. m_a: p_api -> p_api [sync] \"validate\"\n  4. m_3:", outline, StringComparison.Ordinal);

        // Undo the insert with moveEdge's sibling (removeEdge) so the render below is the hand-computed checkout picture.
        AssertSucceeded(await client.CallToolAsync("patch_document", new Dictionary<string, object?> { ["id"] = id, ["ops"] = """[{"op":"removeEdge","id":"m_a"}]""" }, cancellationToken: CancellationToken.None));

        CallToolResult rendered = await client.CallToolAsync("render_document", new Dictionary<string, object?> { ["id"] = id }, cancellationToken: CancellationToken.None);

        AssertSucceeded(rendered);
        JsonElement renderedJson = TextOf(rendered);
        Assert.Empty(renderedJson.GetProperty("warnings").EnumerateArray());
        System.Xml.Linq.XDocument xml = System.Xml.Linq.XDocument.Parse(renderedJson.GetProperty("svg").GetString()!);
        // The numbers are the ones worked out by hand in web/src/model/sequenceScene.test.ts, drawn by the real sidecar's scene.
        Assert.Equal("0 0 580 378", (string?)xml.Root!.Attribute("viewBox"));
        System.Xml.Linq.XElement lifeline = xml.Descendants().Single(e => (string?)e.Attribute("data-lifeline") == "p_api" && e.Name.LocalName == "line");
        Assert.Equal(["360", "60", "378"], new[] { "x1", "y1", "y2" }.Select(a => (string?)lifeline.Attribute(a)));
        System.Xml.Linq.XElement bar = xml.Descendants().Single(e => (string?)e.Attribute("data-lifeline") == "p_api" && e.Name.LocalName == "rect");
        Assert.Equal(["355", "144", "10", "144"], new[] { "x", "y", "width", "height" }.Select(a => (string?)bar.Attribute(a)));
        System.Xml.Linq.XElement m2 = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "m_2").Elements().Single(e => e.Name.LocalName == "line");
        Assert.Equal(["220", "144", "355", "144"], new[] { "x1", "y1", "x2", "y2" }.Select(a => (string?)m2.Attribute(a)));
        System.Xml.Linq.XElement m4 = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "m_4").Elements().Single(e => e.Name.LocalName == "line");
        Assert.Equal("6 4", (string?)m4.Attribute("stroke-dasharray"));
        Assert.Equal("360,326 390,326 390,342 360,342", (string?)xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "m_7").Elements().Single(e => e.Name.LocalName == "polyline").Attribute("points"));
    }

    [Fact]
    public async Task A_sequence_with_a_fragment_a_note_and_a_created_participant_renders_the_same_numbers_the_canvas_scene_pins()
    {
        using var licensed = new DraughtsmanServerFactory();
        await using McpClient client = await Connect(await licensed.CreateAuthenticatedClient());
        CallToolResult created = await client.CallToolAsync("create_document", new Dictionary<string, object?>
        {
            ["title"] = "Checkout",
            ["type"] = "sequence",
            ["format"] = "dsl",
            ["content"] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "checkout-fragments.dsl")),
            ["layout"] = true,
        }, cancellationToken: CancellationToken.None);
        AssertSucceeded(created);
        string id = TextOf(created).GetProperty("id").GetString()!;

        CallToolResult rendered = await client.CallToolAsync("render_document", new Dictionary<string, object?> { ["id"] = id }, cancellationToken: CancellationToken.None);

        AssertSucceeded(rendered);
        JsonElement renderedJson = TextOf(rendered);
        Assert.Empty(renderedJson.GetProperty("warnings").EnumerateArray());
        System.Xml.Linq.XDocument xml = System.Xml.Linq.XDocument.Parse(renderedJson.GetProperty("svg").GetString()!);
        IEnumerable<string?> Attributes(System.Xml.Linq.XElement element, params string[] names) => names.Select(name => (string?)element.Attribute(name));
        // These are the literals web/src/model/sequenceFragments.test.ts pins for the same document: the canvas and the server draw one scene.
        Assert.Equal("0 0 720 544", (string?)xml.Root!.Attribute("viewBox"));

        // The frame, its divider under the second guard, the tab (a cut-corner path) and the operator and both guards.
        System.Xml.Linq.XElement frame = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "f_1");
        Assert.Equal(["200", "198", "315", "160"], Attributes(frame.Elements().First(e => e.Name.LocalName == "rect"), "x", "y", "width", "height"));
        System.Xml.Linq.XElement divider = frame.Elements().Single(e => e.Name.LocalName == "line");
        Assert.Equal(["200", "292", "515", "292"], Attributes(divider, "x1", "y1", "x2", "y2"));
        Assert.Equal("6 4", (string?)divider.Attribute("stroke-dasharray"));
        Assert.Equal("M 200 198 H 231 V 210 L 225 216 H 200 Z", (string?)frame.Elements().Single(e => e.Name.LocalName == "path").Attribute("d"));
        Assert.Equal(["alt", "[card ok]", "[declined]"], frame.Elements().Where(e => e.Name.LocalName == "text").Select(e => e.Value));
        System.Xml.Linq.XElement guard = frame.Elements().First(e => e.Name.LocalName == "text" && e.Value == "[declined]");
        Assert.Equal(["208", "301"], Attributes(guard, "x", "y"));

        // The note is a box across p_web to p_api with its label centred in it, and is not also drawn as a line.
        System.Xml.Linq.XElement note = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "n_1");
        Assert.Equal("M 210 166 H 362 L 370 174 V 194 H 210 Z", (string?)note.Elements().First(e => e.Name.LocalName == "path").Attribute("d"));
        Assert.Equal("Idempotent by key", note.Elements().Single(e => e.Name.LocalName == "text").Value);
        Assert.DoesNotContain(note.Descendants(), e => e.Name.LocalName is "line" or "polyline");

        // The created participant's header is at its create row and its lifeline starts under it and stops at the X.
        System.Xml.Linq.XElement mailer = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "p_mail");
        Assert.Equal(["580", "408"], Attributes(mailer.Elements().First(e => e.Name.LocalName == "rect"), "x", "y"));
        System.Xml.Linq.XElement lifeline = xml.Descendants().Single(e => (string?)e.Attribute("data-lifeline") == "p_mail" && e.Name.LocalName == "line");
        Assert.Equal(["640", "448", "506"], Attributes(lifeline, "x1", "y1", "y2"));
        System.Xml.Linq.XElement cross = xml.Descendants().Single(e => (string?)e.Attribute("data-lifeline") == "p_mail" && e.Name.LocalName == "path");
        Assert.Equal("M 632 498 L 648 514 M 648 498 L 632 514", (string?)cross.Attribute("d"));
        System.Xml.Linq.XElement create = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "m_7").Elements().Single(e => e.Name.LocalName == "line");
        Assert.Equal(["220", "428", "580", "428"], Attributes(create, "x1", "y1", "x2", "y2"));
        Assert.Equal("6 4", (string?)create.Attribute("stroke-dasharray"));

        // The overpass theme on the export paper, the literals web/src/canvas/sequenceDiagram.test.tsx pins for the client's export.
        Assert.Equal("#FFFFFF", (string?)xml.Root!.Elements().First(e => e.Name.LocalName == "rect").Attribute("fill"));
        Assert.Equal(["#7f8aa0", "#FFFFFF"], Attributes(frame.Elements().First(e => e.Name.LocalName == "rect"), "stroke").Concat(Attributes(frame.Elements().Single(e => e.Name.LocalName == "path"), "fill")));
        Assert.All(frame.Elements().Where(e => e.Name.LocalName == "text"), text => Assert.Equal("#111111", (string?)text.Attribute("fill")));
        Assert.Equal("#7f8aa0", (string?)lifeline.Attribute("stroke"));
        Assert.Equal(["#FFFFFF", "#7f8aa0"], Attributes(note.Elements().First(e => e.Name.LocalName == "path"), "fill", "stroke"));

        // The actor is a stick figure (head at 80, 25.52) with its name at 80, 53 under it.
        System.Xml.Linq.XElement actor = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "p_user");
        Assert.Equal(["80", "25.52"], Attributes(actor.Elements().Single(e => e.Name.LocalName == "circle"), "cx", "cy"));
        Assert.Equal(["80", "53"], Attributes(actor.Elements().Single(e => e.Name.LocalName == "text"), "x", "y"));
    }

    [Fact]
    public async Task A_sequence_with_an_interaction_use_renders_the_ref_frame_the_canvas_scene_pins()
    {
        using var licensed = new DraughtsmanServerFactory();
        await using McpClient client = await Connect(await licensed.CreateAuthenticatedClient());
        CallToolResult created = await client.CallToolAsync("create_document", new Dictionary<string, object?>
        {
            ["title"] = "Ref",
            ["type"] = "sequence",
            ["format"] = "dsl",
            ["content"] = """
                draughtsman 1
                type sequence
                title "Ref"

                node p_a participant "A"
                node p_b participant "B"

                edge m_1 sync p_a -> p_b "hello"
                edge u_1 interaction-use p_a -> p_b "Authenticate"
                edge m_2 return p_b -> p_a "ok"
                """,
            ["layout"] = true,
        }, cancellationToken: CancellationToken.None);
        AssertSucceeded(created);
        string id = TextOf(created).GetProperty("id").GetString()!;

        CallToolResult rendered = await client.CallToolAsync("render_document", new Dictionary<string, object?> { ["id"] = id }, cancellationToken: CancellationToken.None);

        AssertSucceeded(rendered);
        JsonElement renderedJson = TextOf(rendered);
        Assert.Empty(renderedJson.GetProperty("warnings").EnumerateArray());
        System.Xml.Linq.XDocument xml = System.Xml.Linq.XDocument.Parse(renderedJson.GetProperty("svg").GetString()!);
        // web/src/model/sequenceFragments.test.ts ("an interaction use") pins these: a 60 row after m_1 holds a 48 high frame 70..230.
        Assert.Equal("0 0 300 242", (string?)xml.Root!.Attribute("viewBox"));
        System.Xml.Linq.XElement frame = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "u_1");
        Assert.Equal("interaction-use", (string?)frame.Attribute("data-kind"));
        Assert.Equal(["70", "132", "160", "48"], new[] { "x", "y", "width", "height" }.Select(a => (string?)frame.Elements().First(e => e.Name.LocalName == "rect").Attribute(a)));
        Assert.Equal("M 70 132 H 101 V 144 L 95 150 H 70 Z", (string?)frame.Elements().Single(e => e.Name.LocalName == "path").Attribute("d"));
        System.Xml.Linq.XElement name = frame.Elements().Single(e => e.Name.LocalName == "text" && e.Value == "Authenticate");
        Assert.Equal(["150", "165"], new[] { "x", "y" }.Select(a => (string?)name.Attribute(a)));
        System.Xml.Linq.XElement next = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "m_2").Elements().Single(e => e.Name.LocalName == "line");
        Assert.Equal("204", (string?)next.Attribute("y1"));
    }

    [Fact]
    public async Task Render_routes_an_edge_between_table_column_rows_through_the_real_sidecar()
    {
        await using McpClient client = await Connect();
        CallToolResult created = await client.CallToolAsync("create_document", new Dictionary<string, object?>
        {
            ["title"] = "Shop",
            ["type"] = "architecture",
            ["format"] = "dsl",
            ["content"] = """
                draughtsman 1
                type architecture
                title "Ignored"

                node t_order entity "Order" @ 0,0 270x92 columns=["PK Id int not null","FK CustomerId int not null","Placed datetime2 not null"]
                node t_customer entity "Customer" @ 500,200 270x92 columns=["PK Id int not null","Name nvarchar(100) not null","Email nvarchar(100) null"]
                edge e_fk association t_order.col:CustomerId -> t_customer.col:Id routing=orthogonal
                edge e_ghost association t_order.col:Nope -> t_customer.col:Name routing=orthogonal
                """,
        }, cancellationToken: CancellationToken.None);
        AssertSucceeded(created);
        string id = TextOf(created).GetProperty("id").GetString()!;

        CallToolResult rendered = await client.CallToolAsync("render_document", new Dictionary<string, object?> { ["id"] = id }, cancellationToken: CancellationToken.None);

        AssertSucceeded(rendered);
        System.Xml.Linq.XDocument xml = System.Xml.Linq.XDocument.Parse(TextOf(rendered).GetProperty("svg").GetString()!);
        System.Xml.Linq.XElement order = xml.Descendants().Single(e => (string?)e.Attribute("data-id") == "t_order");
        System.Xml.Linq.XElement box = order.Elements().First(e => e.Name.LocalName == "rect");
        double shiftX = double.Parse((string)box.Attribute("x")!, System.Globalization.CultureInfo.InvariantCulture);
        double shiftY = double.Parse((string)box.Attribute("y")!, System.Globalization.CultureInfo.InvariantCulture);
        (double X, double Y)[] Route(string edge) => xml.Descendants().Single(e => (string?)e.Attribute("data-id") == edge)
            .Elements().Single(e => e.Name.LocalName == "polyline").Attribute("points")!.Value.Split(' ')
            .Select(pair => pair.Split(',')).Select(pair => (double.Parse(pair[0], System.Globalization.CultureInfo.InvariantCulture), double.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        // The same row numbers the editor's route and the client export assert: FK row 60 down, key row 200 + 42, on the facing sides.
        (double X, double Y)[] onRow = Route("e_fk");
        Assert.Equal((270 + shiftX, 60 + shiftY), onRow[0]);
        Assert.Equal((500 + shiftX, 242 + shiftY), onRow[^1]);
        // A column the table lacks falls back to the table for that end.
        (double X, double Y)[] fallback = Route("e_ghost");
        Assert.NotEqual(60 + shiftY, fallback[0].Y);
        Assert.Equal((500 + shiftX, 260 + shiftY), fallback[^1]);
    }

    [Fact]
    public async Task Reading_an_unknown_document_fails_the_tool_call_rather_than_answering_empty()
    {
        await using McpClient client = await Connect();

        CallToolResult result = await client.CallToolAsync("read_document", new Dictionary<string, object?>
        {
            ["id"] = "doc_does_not_exist",
        }, cancellationToken: CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("doc_does_not_exist", TextContentOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_schema_returns_the_committed_schema_file_verbatim()
    {
        await using McpClient client = await Connect();
        string committed = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "contract", "document.schema.json"),
            CancellationToken.None);

        CallToolResult result = await client.CallToolAsync("get_schema", cancellationToken: CancellationToken.None);

        JsonElement documentSchema = TextOf(result).GetProperty("documentSchema");
        using JsonDocument expected = JsonDocument.Parse(committed);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, documentSchema), "get_schema must return the committed document.schema.json verbatim, not a restated copy.");
    }

    [Fact]
    public async Task Critique_returns_findings_with_evidence_over_the_real_transport()
    {
        await using McpClient client = await Connect();

        CallToolResult created = await client.CallToolAsync("create_document", new Dictionary<string, object?>
        {
            ["title"] = "Single branch",
            ["type"] = "flowchart",
            ["format"] = "dsl",
            ["content"] = "draughtsman 1\ntype flowchart\ntitle \"Ignored\"\nnode n_d decision \"Ok?\" @ 0,0 120x120\nnode n_p process \"Do it\" @ 200,0 120x60\nedge e_1 flow n_d -> n_p \"Yes\"\n",
        }, cancellationToken: CancellationToken.None);
        AssertSucceeded(created);
        string id = TextOf(created).GetProperty("id").GetString()!;

        CallToolResult critiqued = await client.CallToolAsync("critique_document", new Dictionary<string, object?>
        {
            ["id"] = id,
        }, cancellationToken: CancellationToken.None);
        AssertSucceeded(critiqued);

        JsonElement[] findings = TextOf(critiqued).GetProperty("findings").EnumerateArray().ToArray();
        JsonElement singleBranch = Assert.Single(findings, f => f.GetProperty("rule").GetString() == "decision-single-branch");
        Assert.Equal("warning", singleBranch.GetProperty("severity").GetString());
        Assert.Equal("n_d", Assert.Single(singleBranch.GetProperty("nodeIds").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task A_patch_over_mcp_moves_the_version_an_open_editor_polls_and_saves_from()
    {
        // The server half of M2 checklist item 30: the browser holds the ETag it loaded, polls with it, and saves with it.
        HttpClient http = await _factory.CreateAuthenticatedClient();
        HttpResponseMessage created = await http.PostAsync(
            "/api/documents",
            new StringContent("""{"title":"Live","type":"flowchart","nodes":[{"id":"n_a","kind":"process","label":"Start","shape":"rectangle","geometry":{"x":0,"y":0,"w":120,"h":60}}],"edges":[]}""", Encoding.UTF8, "application/json"),
            CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string id = JsonDocument.Parse(await created.Content.ReadAsStringAsync(CancellationToken.None)).RootElement.GetProperty("id").GetString()!;
        string loaded = created.Headers.ETag!.ToString();
        Assert.Equal(HttpStatusCode.NotModified, (await Poll(http, id, loaded)).StatusCode);

        await using McpClient client = await Connect();
        CallToolResult patched = await client.CallToolAsync("patch_document", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["ops"] = """[{"op":"updateNode","id":"n_a","set":{"label":"Start (agent)"}}]""",
        }, cancellationToken: CancellationToken.None);
        AssertSucceeded(patched);

        HttpResponseMessage polled = await Poll(http, id, loaded);
        Assert.Equal(HttpStatusCode.OK, polled.StatusCode);
        string current = polled.Headers.ETag!.ToString();
        Assert.NotEqual(loaded, current);
        string body = await polled.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Equal("Start (agent)", JsonDocument.Parse(body).RootElement.GetProperty("nodes")[0].GetProperty("label").GetString());

        // A save from the loaded version is refused; the same save from the version the poll handed out lands.
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await SaveFrom(http, id, body, loaded)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SaveFrom(http, id, body, current)).StatusCode);
    }

    private static Task<HttpResponseMessage> Poll(HttpClient http, string id, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        return http.SendAsync(request, CancellationToken.None);
    }

    private static Task<HttpResponseMessage> SaveFrom(HttpClient http, string id, string body, string etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{id}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        return http.SendAsync(request, CancellationToken.None);
    }

    [Theory]
    [InlineData("create_document", "title", """{"type":"flowchart","format":"dsl","content":"x"}""")]
    [InlineData("read_document", "id", """{}""")]
    [InlineData("render_document", "id", """{"format":"svg"}""")]
    public async Task A_call_missing_a_required_argument_names_it(string tool, string missing, string argumentsJson)
    {
        await using McpClient client = await Connect();

        CallToolResult result = await client.CallToolAsync(tool, Arguments(argumentsJson), cancellationToken: CancellationToken.None);

        Assert.True(result.IsError);
        string text = TextContentOf(result);
        Assert.Contains($"'{missing}'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("An error occurred invoking", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_type_is_named_and_an_unknown_format_lists_the_accepted_values_while_valid_calls_still_succeed()
    {
        await using McpClient client = await Connect();
        CallToolResult created = await client.CallToolAsync("create_document", new Dictionary<string, object?>
        {
            ["title"] = "Args",
            ["type"] = "flowchart",
            ["format"] = "dsl",
            ["content"] = "draughtsman 1\ntype flowchart\ntitle \"Ignored\"\nnode n_a process \"Start\" @ 0,0 120x60\n",
        }, cancellationToken: CancellationToken.None);
        AssertSucceeded(created);
        string id = TextOf(created).GetProperty("id").GetString()!;

        CallToolResult wrongType = await client.CallToolAsync("list_documents", new Dictionary<string, object?> { ["limit"] = "many" }, cancellationToken: CancellationToken.None);
        Assert.True(wrongType.IsError);
        Assert.Contains("'limit'", TextContentOf(wrongType), StringComparison.Ordinal);
        Assert.Contains("integer", TextContentOf(wrongType), StringComparison.Ordinal);

        CallToolResult badFormat = await client.CallToolAsync("read_document", new Dictionary<string, object?> { ["id"] = id, ["format"] = "yaml" }, cancellationToken: CancellationToken.None);
        Assert.True(badFormat.IsError);
        Assert.Contains("'dsl', 'json', 'outline' or 'mermaid'", TextContentOf(badFormat), StringComparison.Ordinal);

        CallToolResult ok = await client.CallToolAsync("read_document", new Dictionary<string, object?> { ["id"] = id, ["format"] = "outline" }, cancellationToken: CancellationToken.None);
        AssertSucceeded(ok);
        CallToolResult mermaid = await client.CallToolAsync("read_document", new Dictionary<string, object?> { ["id"] = id, ["format"] = "mermaid" }, cancellationToken: CancellationToken.None);
        AssertSucceeded(mermaid);
        Assert.Contains("flowchart TD", TextContentOf(mermaid), StringComparison.Ordinal);
        AssertSucceeded(await client.CallToolAsync("list_documents", new Dictionary<string, object?> { ["limit"] = 5 }, cancellationToken: CancellationToken.None));
    }

    private static Dictionary<string, object?> Arguments(string json)
    {
        return JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    /// <summary>CallToolResult.IsError is null (not false) on a successful call; only true marks a failure.</summary>
    private static void AssertSucceeded(CallToolResult result)
    {
        Assert.True(result.IsError != true, string.Join(" ", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text)));
    }

    // The X-Draughtsman-Actor header is honoured on the REST routes for a token request (docs/api.md), but /mcp never reads it:
    // the tools' own `actor` argument is the only label there. A header naming an agent must change nothing on /mcp, while the
    // argument still does, and neither can make the call anyone but the token's owner.
    [Fact]
    public async Task The_actor_header_is_not_read_on_mcp_and_only_the_actor_argument_labels_the_writer()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        (_, string secret) = await admin.CreateToken("mcp-header-probe");
        using HttpClient bearer = _factory.Bearer(secret);
        bearer.DefaultRequestHeaders.Add(ActorHeader.Name, "agent:header-bot/model-x");
        await using McpClient client = await Connect(bearer);
        Dictionary<string, object?> Create(string title, string? actor = null)
        {
            var arguments = new Dictionary<string, object?> { ["title"] = title, ["type"] = "flowchart", ["format"] = "dsl", ["content"] = "draughtsman 1\ntype flowchart\nnode n_a process \"Start\" @ 0,0 120x60\n" };
            if (actor is not null)
            {
                arguments["actor"] = actor;
            }

            return arguments;
        }

        // Header only: the writer is the token's owner, a user; the header's agent appears nowhere.
        CallToolResult headerOnly = await client.CallToolAsync("create_document", Create("Header only"), cancellationToken: CancellationToken.None);
        AssertSucceeded(headerOnly);
        string headerOnlyId = TextOf(headerOnly).GetProperty("id").GetString()!;
        JsonNode headerOnlyAudit = JsonNode.Parse(await admin.GetStringAsync($"/api/admin/audit?documentId={headerOnlyId}"))!;
        Assert.Equal("user", headerOnlyAudit["items"]![0]!["actorKind"]!.GetValue<string>());
        Assert.DoesNotContain("header-bot", headerOnlyAudit.ToJsonString(), StringComparison.Ordinal);
        string headerOnlyJson = TextContentOf(await client.CallToolAsync("read_document", new Dictionary<string, object?> { ["id"] = headerOnlyId }, cancellationToken: CancellationToken.None));
        Assert.DoesNotContain("header-bot", headerOnlyJson, StringComparison.Ordinal);

        // The opposite: the argument does label the writer, as an agent on top of the same owner.
        CallToolResult labelled = await client.CallToolAsync("create_document", Create("Labelled", "agent:arg-bot/model-y"), cancellationToken: CancellationToken.None);
        AssertSucceeded(labelled);
        string labelledId = TextOf(labelled).GetProperty("id").GetString()!;
        JsonNode labelledAudit = JsonNode.Parse(await admin.GetStringAsync($"/api/admin/audit?documentId={labelledId}"))!;
        Assert.Equal("agent", labelledAudit["items"]![0]!["actorKind"]!.GetValue<string>());
        Assert.Contains("arg-bot", labelledAudit.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("header-bot", labelledAudit.ToJsonString(), StringComparison.Ordinal);
    }

    private async Task<McpClient> Connect(HttpClient? httpClient = null)
    {
        httpClient ??= await _factory.CreateAuthenticatedClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            httpClient);
        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }

    private static string TextContentOf(CallToolResult result)
    {
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    private static JsonElement TextOf(CallToolResult result)
    {
        return JsonDocument.Parse(TextContentOf(result)).RootElement;
    }
}
