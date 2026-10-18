using System.Reflection;
using System.Xml.Linq;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Mcp.Rendering;
using YamlDotNet.RepresentationModel;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// The server's picture, and so the PDF printed from it, carries the shipped diagram theme's colours for EVERY document type
/// (audit PR-07), as the client's PNG/SVG export does. The expected colours are read straight from the theme file here, not
/// through <see cref="DiagramTheme"/>, so a renderer that stopped theming fails against the file rather than against itself.
/// </summary>
public class SvgRendererThemeTests
{
    private const string NeutralFill = "#ffffff";
    private const string NeutralStroke = "#334155";

    /// <summary>The theme file the client compiles, parsed on its own.</summary>
    private static YamlMappingNode ThemeFile()
    {
        using Stream stream = typeof(DiagramTheme).Assembly.GetManifestResourceStream("Draughtsman.Mcp.Themes.diagram-overpass.yaml")!;
        var yaml = new YamlStream();
        yaml.Load(new StreamReader(stream));
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    private static string Token(string group, string kind, string field)
    {
        var root = ThemeFile();
        var kinds = (YamlMappingNode)root.Children[group];
        return (string)((YamlMappingNode)kinds.Children[kind]).Children[field]!;
    }

    private static string Root(string key) => (string)ThemeFile().Children[key]!;

    private static Document Doc(DocumentType type, IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges, IReadOnlyDictionary<string, string>? theme = null)
    {
        return new Document("doc_1", 1, "T", type, nodes, edges, new Meta(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "user:local")) { Theme = theme };
    }

    private static Node NodeOf(string id, string kind, double x, NodeStyle? style = null)
    {
        return new Node(id, kind, id, "rectangle") { Geometry = new Geometry(x, 0, 120, 60), Style = style };
    }

    private static XElement Group(XDocument xml, string id) => Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == id);

    private static string? ShapeAttribute(XDocument xml, string nodeId, string attribute) =>
        (string?)Group(xml, nodeId).Descendants().First(e => e.Name.LocalName == "rect").Attribute(attribute);

    private static string? EdgeStroke(XDocument xml, string edgeId) =>
        (string?)Group(xml, edgeId).Descendants().First(e => e.Attribute("stroke") is not null && e.Name.LocalName is "path" or "polyline" or "line").Attribute("stroke");

    private static XDocument Render(Document document) => XDocument.Parse(SvgRenderer.Render(document).Svg);

    [Theory]
    [InlineData(DocumentType.Flowchart, "process", "decision", "flow")]
    [InlineData(DocumentType.Architecture, "service", "database", "data")]
    [InlineData(DocumentType.Freeform, "sticky", "note", "default")]
    public void Every_document_type_draws_its_nodes_and_edges_in_the_themes_colours(DocumentType type, string kindA, string kindB, string edgeKind)
    {
        Node a = NodeOf("n_a", kindA, 0);
        Node b = NodeOf("n_b", kindB, 240);
        var edge = new Edge("e_ab", edgeKind, new Endpoint("n_a"), new Endpoint("n_b"));
        XDocument xml = Render(Doc(type, [a, b], [edge]));

        Assert.Equal(Token("nodes", kindA, "fill"), ShapeAttribute(xml, "n_a", "fill"));
        Assert.Equal(Token("nodes", kindA, "stroke"), ShapeAttribute(xml, "n_a", "stroke"));
        Assert.Equal(Token("nodes", kindB, "fill"), ShapeAttribute(xml, "n_b", "fill"));
        Assert.Equal(Token("nodes", kindB, "stroke"), ShapeAttribute(xml, "n_b", "stroke"));
        Assert.Equal(Token("edges", edgeKind, "stroke"), EdgeStroke(xml, "e_ab"));
        // The paper is the theme's export background, not the renderer's old neutral grey.
        Assert.Equal(Root("exportBackground"), (string?)xml.Descendants().First(e => e.Name.LocalName == "rect").Attribute("fill"));

        // The other direction: none of it is the neutral default the renderer used before it resolved the theme.
        Assert.NotEqual(NeutralFill, ShapeAttribute(xml, "n_a", "fill"));
        Assert.NotEqual(NeutralStroke, EdgeStroke(xml, "e_ab"));
    }

    [Fact]
    public void Two_kinds_the_theme_styles_differently_draw_differently()
    {
        // A renderer that painted one colour for everything would pass the test above for a single kind; two kinds must diverge.
        XDocument xml = Render(Doc(DocumentType.Architecture, [NodeOf("n_svc", "service", 0), NodeOf("n_db", "database", 240)], []));
        Assert.NotEqual(Token("nodes", "service", "fill"), Token("nodes", "database", "fill"));
        Assert.NotEqual(ShapeAttribute(xml, "n_svc", "fill"), ShapeAttribute(xml, "n_db", "fill"));
    }

    [Fact]
    public void A_nodes_own_style_beats_the_theme_and_a_document_override_moves_the_theme()
    {
        Node styled = NodeOf("n_own", "service", 0, new NodeStyle { Fill = "#123456", Stroke = "#654321" });
        Node plain = NodeOf("n_plain", "service", 240);
        var theme = new Dictionary<string, string> { ["nodes.service.stroke"] = "#0a0b0c", ["exportBackground"] = "#eeeeee" };
        XDocument xml = Render(Doc(DocumentType.Architecture, [styled, plain], [], theme));

        // The element's own colours win, field by field.
        Assert.Equal("#123456", ShapeAttribute(xml, "n_own", "fill"));
        Assert.Equal("#654321", ShapeAttribute(xml, "n_own", "stroke"));
        // The document's token overrides reach every node that has no style of its own, and the paper.
        Assert.Equal("#0a0b0c", ShapeAttribute(xml, "n_plain", "stroke"));
        Assert.Equal(Token("nodes", "service", "fill"), ShapeAttribute(xml, "n_plain", "fill"));
        Assert.Equal("#eeeeee", (string?)xml.Descendants().First(e => e.Name.LocalName == "rect").Attribute("fill"));
    }

    [Fact]
    public void A_token_reference_in_a_node_style_reads_the_themes_palette()
    {
        Node node = NodeOf("n_a", "service", 0, new NodeStyle { Fill = "token:amber" });
        XDocument xml = Render(Doc(DocumentType.Architecture, [node], []));
        Assert.Equal("#E0A526", ShapeAttribute(xml, "n_a", "fill"));
    }

    [Fact]
    public void An_organisation_theme_recolours_the_picture_and_no_theme_leaves_the_overpass_default()
    {
        Node node = NodeOf("n_a", "service", 0);
        Document document = Doc(DocumentType.Architecture, [node], []);
        string overpassStroke = Token("nodes", "service", "stroke");
        var acme = new DiagramThemeChoice("overpass", new Dictionary<string, string> { ["red"] = "#0000ff", ["nope"] = "#123456" }, "#eeeeee");

        XDocument plain = XDocument.Parse(SvgRenderer.Render(document).Svg);
        XDocument branded = XDocument.Parse(SvgRenderer.Render(document, theme: acme).Svg);

        // Without a theme: the shipped brand red, the shipped paper.
        Assert.Equal(overpassStroke, ShapeAttribute(plain, "n_a", "stroke"));
        Assert.Equal(Root("exportBackground"), (string?)plain.Descendants().First(e => e.Name.LocalName == "rect").Attribute("fill"));
        // With one: the palette swap reaches every style that used the colour, and its paper wins.
        Assert.Equal("#0000ff", ShapeAttribute(branded, "n_a", "stroke"));
        Assert.Equal("#eeeeee", (string?)branded.Descendants().First(e => e.Name.LocalName == "rect").Attribute("fill"));
        // A colour the swap did not name is untouched, and a node's own style still beats the theme.
        Assert.Equal(Token("nodes", "service", "fill"), ShapeAttribute(branded, "n_a", "fill"));
        XDocument own = XDocument.Parse(SvgRenderer.Render(Doc(DocumentType.Architecture, [NodeOf("n_b", "service", 0, new NodeStyle { Stroke = "#654321" })], []), theme: acme).Svg);
        Assert.Equal("#654321", ShapeAttribute(own, "n_b", "stroke"));
    }

    [Fact]
    public void An_organisation_theme_on_another_base_and_an_unknown_base_fall_back_safely()
    {
        Document document = Doc(DocumentType.Architecture, [NodeOf("n_a", "service", 0)], []);
        var none = new Dictionary<string, string>();

        string mono = ShapeAttribute(XDocument.Parse(SvgRenderer.Render(document, theme: new DiagramThemeChoice("mono", none, null)).Svg), "n_a", "stroke")!;
        string unknown = ShapeAttribute(XDocument.Parse(SvgRenderer.Render(document, theme: new DiagramThemeChoice("no-such-base", none, null)).Svg), "n_a", "stroke")!;

        Assert.NotEqual(Token("nodes", "service", "stroke"), mono);
        Assert.Equal(Token("nodes", "service", "stroke"), unknown);
    }
}
