using System.ComponentModel;
using System.Reflection;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Draughtsman.Mcp.Tools;
using Draughtsman.Sidecar;
using Draughtsman.Tests.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// MCP <c>create_document</c> with <c>format: "drawio"</c>: the same reader as <c>POST /api/import/drawio</c>, through the tool an agent calls.
/// Positions arrive with the file, so the document is stored as the file placed it and the layout engine is not asked unless the caller says so.
/// </summary>
public class DrawioToolsTests
{
    private readonly FakeStore _store = new();
    private readonly FakeSidecarClient _sidecar = new();
    private readonly DraughtsmanTools _tools;

    public DrawioToolsTests()
    {
        _tools = new DraughtsmanTools(_store, _sidecar, new FakePdfRenderer(), new FakeAssetReader(), new NullActorContext());
    }

    private async Task<Document> Stored(CreateDocumentResult created)
    {
        return DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
    }

    [Fact]
    public async Task A_drawio_file_becomes_a_document_with_the_positions_the_file_gave_it()
    {
        CreateDocumentResult created = await _tools.CreateDocument("Order flow", "flowchart", DrawioFixtures.Read("compressed.drawio"), "drawio");

        Document document = await Stored(created);
        Assert.Equal("Order flow", document.Title);
        Assert.Equal(["n_start", "n_check", "n_ship", "n_reject", "n_end"], document.Nodes.Select(n => n.Id));
        Assert.Equal(new Geometry(330, 130, 140, 90), document.Nodes.Single(n => n.Id == "n_check").Geometry);
        Assert.Equal(5, document.Edges.Count);
        Assert.Equal("decision", document.Nodes.Single(n => n.Id == "n_check").Kind);
    }

    [Fact]
    public async Task A_kind_that_was_a_judgement_is_a_warning_an_agent_can_read_and_nothing_else_is_an_error()
    {
        CreateDocumentResult created = await _tools.CreateDocument("Shapes", "flowchart", DrawioFixtures.Read("shapes.drawio"), "drawio");

        Assert.Contains(created.Warnings, w => w.StartsWith("kind-guessed: ", StringComparison.Ordinal) && w.Contains("\"Oval\"", StringComparison.Ordinal));
        Assert.Contains(created.Warnings, w => w.StartsWith("unsupported-syntax: ", StringComparison.Ordinal) && w.Contains("mxgraph.aws4.lambda_function", StringComparison.Ordinal));
        Assert.Contains(created.Warnings, w => w.StartsWith("style-approximated: ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_layout_engine_is_asked_only_when_layout_is_true()
    {
        // A sidecar that fails would add a warning to anything that asked it to lay out: its absence is the proof it was not asked.
        _sidecar.Failure = new SidecarUnavailableException("sidecar down");

        CreateDocumentResult untouched = await _tools.CreateDocument("Placed", "flowchart", DrawioFixtures.Read("bare-model.xml"), "drawio");
        CreateDocumentResult asked = await _tools.CreateDocument("Asked", "flowchart", DrawioFixtures.Read("bare-model.xml"), "drawio", layout: true);

        Assert.DoesNotContain(untouched.Warnings, w => w.Contains("sidecar", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(asked.Warnings, w => w.Contains("sidecar", StringComparison.OrdinalIgnoreCase));
        Document document = await Stored(untouched);
        Assert.Equal(new Geometry(20, 20, 160, 60), document.Nodes[0].Geometry);
    }

    [Fact]
    public async Task The_type_argument_decides_the_vocabulary_and_the_caller_s_title_wins()
    {
        CreateDocumentResult created = await _tools.CreateDocument("Mine", "architecture", DrawioFixtures.Read("shapes.drawio"), "drawio");

        Document document = await Stored(created);
        Assert.Equal("Mine", document.Title);
        Assert.Equal(Draughtsman.Core.Enums.DocumentType.Architecture, document.Type);
        Assert.Equal("database", document.Nodes.Single(n => n.Id == "n_s5").Kind);
        Assert.Equal("actor", document.Nodes.Single(n => n.Id == "n_s6").Kind);
    }

    [Fact]
    public async Task A_sequence_document_cannot_be_made_from_a_drawing_and_the_error_says_what_to_use()
    {
        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Seq", "sequence", DrawioFixtures.Read("bare-model.xml"), "drawio"));

        Assert.Contains("sequenceDiagram", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_of_a_multi_page_file_is_chosen_with_the_page_argument()
    {
        CreateDocumentResult first = await _tools.CreateDocument("One", "flowchart", DrawioFixtures.Read("multipage.drawio"), "drawio");
        CreateDocumentResult second = await _tools.CreateDocument("Two", "flowchart", DrawioFixtures.Read("multipage.drawio"), "drawio", page: 2);

        Assert.Equal(["n_a", "n_b"], (await Stored(first)).Nodes.Select(n => n.Id));
        Assert.Equal(["n_d1", "n_d2", "n_d3"], (await Stored(second)).Nodes.Select(n => n.Id));
        Assert.Contains(first.Warnings, w => w.StartsWith("page-selected: Page 1", StringComparison.Ordinal) && w.Contains("other 2 pages", StringComparison.Ordinal));
        Assert.Contains(second.Warnings, w => w.StartsWith("page-selected: Page 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_page_the_file_does_not_have_is_an_error_that_says_how_many_it_has()
    {
        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Nope", "flowchart", DrawioFixtures.Read("multipage.drawio"), "drawio", page: 7));

        Assert.Contains("3 pages", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!DOCTYPE x [<!ENTITY a \"AAA\">]><mxfile><diagram name=\"&a;\"/></mxfile>", "DOCTYPE")]
    [InlineData("graph TD\n  A --> B", "draw.io")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", "draw.io")]
    public async Task A_source_that_is_refused_is_an_error_naming_why_and_nothing_is_stored(string source, string expected)
    {
        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Bad", "flowchart", source, "drawio"));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Empty((await _store.ListDocuments(new DocumentQuery(), default)).Items);
    }

    [Fact]
    public async Task An_unknown_format_names_drawio_among_the_accepted_ones()
    {
        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("T", "flowchart", "x", "lucid"));

        Assert.Contains("'drawio'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tool_description_and_arguments_teach_the_drawio_format()
    {
        MethodInfo method = typeof(DraughtsmanTools).GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "create_document");
        string description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        ParameterInfo format = method.GetParameters().Single(p => p.Name == "format");
        ParameterInfo page = method.GetParameters().Single(p => p.Name == "page");

        Assert.Contains("drawio", description, StringComparison.Ordinal);
        Assert.Contains("page", description, StringComparison.Ordinal);
        Assert.Contains("drawio", format.GetCustomAttribute<DescriptionAttribute>()!.Description, StringComparison.Ordinal);
        Assert.Equal(1, page.DefaultValue);
        Assert.Contains("from 1", page.GetCustomAttribute<DescriptionAttribute>()!.Description, StringComparison.Ordinal);
    }
}
