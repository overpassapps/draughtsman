using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Mcp.Patching;
using Draughtsman.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// What the MCP tools say they do is what they do: a tool description lists every op the applier accepts, an unknown op
/// is refused rather than ignored, and where create_document is given a title or type as well as content that has its
/// own, neither is silently overridden. Each rule is checked with its opposite beside it.
/// </summary>
public class McpToolHonestyTests
{
    private const string FlowchartDsl = "draughtsman 1\ntype flowchart\ntitle \"From the content\"\nnode n_a process \"Start\" @ 0,0 120x60\n";

    private readonly FakeStore _store = new();
    private readonly DraughtsmanTools _tools;

    public McpToolHonestyTests()
    {
        _tools = new DraughtsmanTools(_store, new FakeSidecarClient(), new FakePdfRenderer(), new FakeAssetReader(), new NullActorContext());
    }

    private static string DescriptionOf(string tool)
    {
        MethodInfo method = typeof(DraughtsmanTools).GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == tool);
        return method.GetCustomAttribute<DescriptionAttribute>()!.Description;
    }

    /// <summary>The op kinds <see cref="PatchApplier"/> itself says it accepts, read from its own refusal of an unknown one.</summary>
    private static string[] OpsThePatchApplierAccepts()
    {
        var document = new JsonObject { ["nodes"] = new JsonArray(), ["edges"] = new JsonArray() };
        PatchException refusal = Assert.Throws<PatchException>(() => PatchApplier.Apply(document, [new JsonObject { ["op"] = "no-such-op" }]));
        string list = refusal.Message[(refusal.Message.IndexOf("Expected one of: ", StringComparison.Ordinal) + "Expected one of: ".Length)..].TrimEnd('.');
        return list.Split(", ");
    }

    [Fact]
    public void Patch_document_describes_every_op_the_applier_accepts_setFlythrough_included()
    {
        string description = DescriptionOf("patch_document");
        string[] accepted = OpsThePatchApplierAccepts();

        Assert.Contains("setFlythrough", accepted);
        foreach (string op in accepted)
        {
            Assert.True(description.Contains(op, StringComparison.Ordinal), $"patch_document's description does not list the '{op}' op");
        }

        // The check can fail: a description that lacks an op is caught.
        Assert.DoesNotContain("setFlythrough", description.Replace("setFlythrough", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Patch_document_refuses_an_unknown_op_kind_naming_the_valid_ones_and_writes_nothing()
    {
        CreateDocumentResult created = await _tools.CreateDocument("T", "flowchart", FlowchartDsl, "dsl");
        string before = await _tools.ReadDocument(created.Id, "json");

        McpException unknown = await Assert.ThrowsAsync<McpException>(() => _tools.PatchDocument(created.Id, """[{"op":"rotateNode","id":"n_a"}]"""));

        Assert.Contains("Unknown patch op 'rotateNode'", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("addNode", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("setFlythrough", unknown.Message, StringComparison.Ordinal);
        Assert.Equal(before, await _tools.ReadDocument(created.Id, "json"));

        // One good op followed by a bad one fails the whole call: the good one is not applied either.
        await Assert.ThrowsAsync<McpException>(() => _tools.PatchDocument(created.Id, """[{"op":"updateNode","id":"n_a","set":{"label":"Changed"}},{"op":"nope"}]"""));
        Assert.Equal(before, await _tools.ReadDocument(created.Id, "json"));

        // The opposite: a known op works.
        PatchDocumentResult ok = await _tools.PatchDocument(created.Id, """[{"op":"updateNode","id":"n_a","set":{"label":"Changed"}}]""");
        Assert.Equal("Changed", ok.Document.Nodes.Single().Label);
    }

    [Fact]
    public async Task Create_document_refuses_dsl_whose_own_type_differs_from_the_type_argument()
    {
        McpException conflict = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("T", "architecture", FlowchartDsl, "dsl"));

        Assert.Contains("type flowchart", conflict.Message, StringComparison.Ordinal);
        Assert.Contains("architecture", conflict.Message, StringComparison.Ordinal);
        Assert.Empty((await _tools.ListDocuments()).Items);

        // The opposite: the same content with the matching type is created as a flowchart.
        CreateDocumentResult ok = await _tools.CreateDocument("T", "flowchart", FlowchartDsl, "dsl");
        Assert.Equal(DocumentType.Flowchart, DocumentJson.Deserialize(await _tools.ReadDocument(ok.Id, "json")).Type);
    }

    [Fact]
    public async Task Create_document_lets_the_title_argument_win_over_the_content_title_and_says_so()
    {
        CreateDocumentResult replaced = await _tools.CreateDocument("The argument", "flowchart", FlowchartDsl, "dsl");
        Assert.Equal("The argument", DocumentJson.Deserialize(await _tools.ReadDocument(replaced.Id, "json")).Title);
        string warning = Assert.Single(replaced.Warnings);
        Assert.Contains("\"From the content\" was replaced by the title argument \"The argument\"", warning, StringComparison.Ordinal);

        // No warning when they agree, or when the content has no title of its own.
        Assert.Empty((await _tools.CreateDocument("From the content", "flowchart", FlowchartDsl, "dsl")).Warnings);
        Assert.Empty((await _tools.CreateDocument("Anything", "flowchart", "draughtsman 1\ntype flowchart\nnode n_a process \"Start\" @ 0,0 120x60\n", "dsl")).Warnings);
    }

    [Fact]
    public async Task Create_document_json_takes_the_same_body_as_POST_api_documents_and_fills_what_it_fills()
    {
        // No id, schemaVersion or meta, and a claimed provenance entry: the server fills the first three and drops the last.
        const string Body = """
            {
              "title": "From JSON", "type": "architecture",
              "nodes": [ { "id": "a", "kind": "service", "label": "A", "shape": "rounded" } ],
              "edges": [],
              "meta": { "aiProvenance": [ { "agent": "someone-else", "at": "2026-01-01T00:00:00.000Z", "action": "created", "ids": ["a"] } ] }
            }
            """;

        CreateDocumentResult created = await _tools.CreateDocument("From JSON", "architecture", Body, "json");

        Document stored = DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
        Assert.Equal(Document.CurrentSchemaVersion, stored.SchemaVersion);
        Assert.StartsWith("doc_", stored.Id, StringComparison.Ordinal);
        Assert.Equal("agent:mcp-client/unknown", stored.Meta.CreatedBy);
        Assert.DoesNotContain(stored.Meta.AiProvenance ?? [], p => p.Agent == "someone-else");

        // A body without title and type takes them from the arguments (they are arguments here).
        CreateDocumentResult bare = await _tools.CreateDocument("Named by argument", "freeform", """{ "nodes": [], "edges": [] }""", "json");
        Document bareStored = DocumentJson.Deserialize(await _tools.ReadDocument(bare.Id, "json"));
        Assert.Equal("Named by argument", bareStored.Title);
        Assert.Equal(DocumentType.Freeform, bareStored.Type);

        // A body id is not kept (POST /api/documents would keep it): a new one is always assigned.
        CreateDocumentResult withId = await _tools.CreateDocument("Has id", "freeform", """{ "id": "doc_mine", "nodes": [], "edges": [] }""", "json");
        Assert.NotEqual("doc_mine", withId.Id);
    }

    [Fact]
    public async Task Create_document_json_compares_its_own_type_and_title_with_the_arguments_too()
    {
        const string Body = """{ "title": "Body title", "type": "architecture", "nodes": [], "edges": [] }""";

        McpException conflict = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("T", "flowchart", Body, "json"));
        Assert.Contains("type architecture", conflict.Message, StringComparison.Ordinal);

        CreateDocumentResult replaced = await _tools.CreateDocument("Argument title", "architecture", Body, "json");
        Assert.Contains("\"Body title\" was replaced by the title argument \"Argument title\"", Assert.Single(replaced.Warnings), StringComparison.Ordinal);
        Assert.Empty((await _tools.CreateDocument("Body title", "architecture", Body, "json")).Warnings);

        // Not an object, or invalid against the schema: refused with the document message, nothing stored.
        Assert.Contains("not a valid document", (await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("T", "flowchart", "[1]", "json"))).Message, StringComparison.Ordinal);
        Assert.Contains("not valid", (await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("T", "architecture", """{ "nodes": [ { "id": "a", "kind": "service", "label": "A" } ], "edges": [] }""", "json"))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_descriptions_state_the_precedence_the_tests_above_pin()
    {
        string create = DescriptionOf("create_document");
        Assert.Contains("replaced by the title argument", create, StringComparison.Ordinal);
        Assert.Contains("is refused", create, StringComparison.Ordinal);
        Assert.Contains("POST /api/documents", create, StringComparison.Ordinal);
        Assert.Contains("id is never kept", create, StringComparison.Ordinal);

        // And the actor argument says the header is not read on /mcp.
        MethodInfo createMethod = typeof(DraughtsmanTools).GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "create_document");
        string actor = createMethod.GetParameters().Single(p => p.Name == "actor").GetCustomAttribute<DescriptionAttribute>()!.Description;
        Assert.Contains("X-Draughtsman-Actor header is not read on /mcp", actor, StringComparison.Ordinal);
    }
}
