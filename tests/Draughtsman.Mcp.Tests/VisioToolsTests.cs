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
/// MCP <c>create_document</c> with <c>format: "visio"</c>: the same reader as <c>POST /api/import/visio</c>, through the tool an agent calls. A .vsdx is binary
/// and a tool argument is text, so the file arrives base64-encoded. Positions arrive with the file, so the document is stored as the file placed it and the
/// layout engine is not asked unless the caller says so.
/// </summary>
public class VisioToolsTests
{
    private readonly FakeStore _store = new();
    private readonly FakeSidecarClient _sidecar = new();
    private readonly DraughtsmanTools _tools;

    public VisioToolsTests()
    {
        _tools = new DraughtsmanTools(_store, _sidecar, new FakePdfRenderer(), new FakeAssetReader(), new NullActorContext());
    }

    private static string Base64(byte[] file)
    {
        return Convert.ToBase64String(file);
    }

    private async Task<Document> Stored(CreateDocumentResult created)
    {
        return DocumentJson.Deserialize(await _tools.ReadDocument(created.Id, "json"));
    }

    [Fact]
    public async Task A_vsdx_file_as_base64_becomes_a_document_with_the_positions_the_file_gave_it()
    {
        CreateDocumentResult created = await _tools.CreateDocument("Connected", "flowchart", Base64(VisioFixtures.Connectors()), "visio");

        Document document = await Stored(created);
        Assert.Equal("Connected", document.Title);
        Assert.Equal(["n_1", "n_2", "n_3"], document.Nodes.Select(n => n.Id));
        Assert.Equal(new Geometry(125, 162.5, 150, 75), document.Nodes.Single(n => n.Id == "n_1").Geometry);
        Assert.Equal(["e_4", "e_5"], document.Edges.Select(e => e.Id));
    }

    [Fact]
    public async Task What_could_not_be_carried_is_a_warning_an_agent_can_read_and_nothing_else_is_an_error()
    {
        CreateDocumentResult created = await _tools.CreateDocument("Shapes", "flowchart", Base64(VisioFixtures.Shapes()), "visio");

        Assert.Contains(created.Warnings, w => w.StartsWith("kind-guessed: ", StringComparison.Ordinal) && w.Contains("\"Oval\"", StringComparison.Ordinal));
        Assert.Contains(created.Warnings, w => w.StartsWith("unsupported-syntax: 1 shape has a custom outline", StringComparison.Ordinal));
        CreateDocumentResult connected = await _tools.CreateDocument("Connected", "flowchart", Base64(VisioFixtures.Connectors()), "visio");
        Assert.Contains(connected.Warnings, w => w.StartsWith("missing-reference: ", StringComparison.Ordinal) && w.Contains("a loose end in Visio", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Whitespace_and_a_data_uri_prefix_around_the_base64_are_tolerated()
    {
        string encoded = Base64(VisioFixtures.MasterLess());
        string wrapped = "data:application/vnd.ms-visio.drawing;base64,\n" + string.Join("\r\n", Enumerable.Range(0, (encoded.Length / 76) + 1).Select(i => encoded.Substring(i * 76, Math.Min(76, encoded.Length - (i * 76)))));

        CreateDocumentResult created = await _tools.CreateDocument("Wrapped", "flowchart", wrapped, "visio");

        Assert.Equal(2, (await Stored(created)).Nodes.Count);
    }

    [Fact]
    public async Task The_layout_engine_is_asked_only_when_layout_is_true()
    {
        // A sidecar that fails would add a warning to anything that asked it to lay out: its absence is the proof it was not asked.
        _sidecar.Failure = new SidecarUnavailableException("sidecar down");

        CreateDocumentResult untouched = await _tools.CreateDocument("Placed", "flowchart", Base64(VisioFixtures.MasterLess()), "visio");
        CreateDocumentResult asked = await _tools.CreateDocument("Asked", "flowchart", Base64(VisioFixtures.MasterLess()), "visio", layout: true);

        Assert.DoesNotContain(untouched.Warnings, w => w.Contains("sidecar", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(asked.Warnings, w => w.Contains("sidecar", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new Geometry(125, 162.5, 150, 75), (await Stored(untouched)).Nodes[0].Geometry);
    }

    [Fact]
    public async Task The_type_argument_decides_the_vocabulary()
    {
        CreateDocumentResult created = await _tools.CreateDocument("Arch", "architecture", Base64(VisioFixtures.Shapes()), "visio");

        Document document = await Stored(created);
        Assert.Equal(Draughtsman.Core.Enums.DocumentType.Architecture, document.Type);
        // In an architecture a box and a diamond are services, a parallelogram is a queue, and a label with no outline stays text.
        Assert.Equal("service", document.Nodes.Single(n => n.Id == "n_1").Kind);
        Assert.Equal("service", document.Nodes.Single(n => n.Id == "n_4").Kind);
        Assert.Equal("queue", document.Nodes.Single(n => n.Id == "n_7").Kind);
        Assert.Equal("text", document.Nodes.Single(n => n.Id == "n_5").Kind);
    }

    [Fact]
    public async Task A_sequence_document_cannot_be_made_from_a_drawing_and_the_error_says_what_to_use()
    {
        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Seq", "sequence", Base64(VisioFixtures.Shapes()), "visio"));

        Assert.Contains("sequenceDiagram", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_of_a_multi_page_file_is_chosen_with_the_page_argument()
    {
        CreateDocumentResult first = await _tools.CreateDocument("One", "flowchart", Base64(VisioFixtures.MultiPage()), "visio");
        CreateDocumentResult second = await _tools.CreateDocument("Two", "flowchart", Base64(VisioFixtures.MultiPage()), "visio", page: 2);

        Assert.Equal(["n_1", "n_2"], (await Stored(first)).Nodes.Select(n => n.Id));
        Assert.Equal("Only on page two", Assert.Single((await Stored(second)).Nodes).Label);
        Assert.Contains(first.Warnings, w => w.StartsWith("page-selected: Page 1", StringComparison.Ordinal) && w.Contains("other 2 pages", StringComparison.Ordinal));
        Assert.Contains(second.Warnings, w => w.StartsWith("page-selected: Page 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_page_the_file_does_not_have_is_an_error_that_says_how_many_it_has()
    {
        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Nope", "flowchart", Base64(VisioFixtures.MultiPage()), "visio", page: 7));

        Assert.Contains("3 pages", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Content_that_is_not_base64_is_an_error_that_says_how_to_encode_the_file()
    {
        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Bad", "flowchart", "this is not base64 !!!", "visio"));

        Assert.Contains("base64", error.Message, StringComparison.Ordinal);
        Assert.Contains("base64 -i diagram.vsdx", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Base64_that_is_too_long_is_refused_before_it_is_decoded()
    {
        // Valid base64 all the way but for its last character: a decoder that was started would say so, so the size message proves it never was.
        string tooLong = new string('A', 4_300_000) + "!";

        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Big", "flowchart", tooLong, "visio"));

        Assert.Contains("larger than 3,000,000 bytes", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PCFET0NUWVBFIHggWzwhRU5USVRZIGEgIkFBQSI+XT48bXhmaWxlLz4=", "Import draw.io")]
    [InlineData("0M8R4KGxGuEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "Save As")]
    [InlineData("AQIDBAUGBwg=", "not a ZIP package")]
    public async Task A_file_that_is_not_a_vsdx_is_an_error_naming_why_and_nothing_is_stored(string base64, string expected)
    {
        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Bad", "flowchart", base64, "visio"));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Empty((await _store.ListDocuments(new DocumentQuery(), default)).Items);
    }

    [Fact]
    public async Task A_hostile_package_is_refused_in_words()
    {
        List<KeyValuePair<string, byte[]>> parts = new VisioDocBuilder().Page("P", VisioDocBuilder.Shape(1, 2, 6, 1, 1)).Parts();
        byte[] file = VisioDocBuilder.Replace(parts, "visio/pages/page1.xml", "<!DOCTYPE x [<!ENTITY a \"AAA\">]><PageContents/>");

        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("Hostile", "flowchart", Base64(file), "visio"));

        Assert.Contains("DOCTYPE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_format_names_visio_among_the_accepted_ones()
    {
        McpException error = await Assert.ThrowsAsync<McpException>(() => _tools.CreateDocument("T", "flowchart", "x", "lucid"));

        Assert.Contains("'visio'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tool_description_and_arguments_teach_the_visio_format_and_say_the_file_is_base64()
    {
        MethodInfo method = typeof(DraughtsmanTools).GetMethods().Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == "create_document");
        string description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        ParameterInfo format = method.GetParameters().Single(p => p.Name == "format");
        ParameterInfo content = method.GetParameters().Single(p => p.Name == "content");
        ParameterInfo page = method.GetParameters().Single(p => p.Name == "page");

        Assert.Contains("visio is a Visio (.vsdx) drawing", description, StringComparison.Ordinal);
        Assert.Contains("BASE64-ENCODED", description, StringComparison.Ordinal);
        Assert.Contains(".vsd", description, StringComparison.Ordinal);
        Assert.Contains("visio", format.GetCustomAttribute<DescriptionAttribute>()!.Description, StringComparison.Ordinal);
        Assert.Contains("base64", content.GetCustomAttribute<DescriptionAttribute>()!.Description, StringComparison.Ordinal);
        Assert.Contains("visio", page.GetCustomAttribute<DescriptionAttribute>()!.Description, StringComparison.Ordinal);
    }
}
