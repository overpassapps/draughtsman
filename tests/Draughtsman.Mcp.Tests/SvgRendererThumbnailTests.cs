using System.Globalization;
using System.Xml.Linq;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Mcp.Rendering;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// The thumbnail drawing (SvgRenderer.Thumbnail.cs): a fixed 320 by 200 box holding the document's own picture scaled to fit,
/// a boxes-and-lines form for a diagram too large to draw in full, and the neutral placeholders. None of it may change what
/// <see cref="SvgRenderer.Render"/> writes for an export or <c>render_document</c>.
/// </summary>
public class SvgRendererThumbnailTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    [Fact]
    public void A_full_thumbnail_is_always_the_fixed_box_whatever_the_diagram_is_shaped_like()
    {
        foreach ((double w, double h) in new[] { (200.0, 100.0), (4000.0, 100.0), (100.0, 4000.0), (20.0, 20.0) })
        {
            XElement root = Parse(SvgRenderer.RenderThumbnail(Document([Box("n_a", 0, 0, w, h)], []), ThumbnailDetail.Full).Svg);

            Assert.Equal("0 0 320 200", (string?)root.Attribute("viewBox"));
            Assert.Equal("320", (string?)root.Attribute("width"));
            Assert.Equal("200", (string?)root.Attribute("height"));
        }
    }

    [Fact]
    public void A_full_thumbnail_fits_the_picture_in_the_box_keeping_its_shape_and_centring_it()
    {
        // 400 x 100 of node, plus the renderer's own 32 of padding each side: a 464 x 164 picture.
        XElement root = Parse(SvgRenderer.RenderThumbnail(Document([Box("n_a", 0, 0, 400, 100)], []), ThumbnailDetail.Full).Svg);
        XElement picture = Assert.Single(root.Elements(Svg + "svg"));

        double scale = 320.0 / 464.0;
        Assert.Equal("0 0 464 164", (string?)picture.Attribute("viewBox"));
        Assert.Equal(464 * scale, Number(picture, "width"), 1);
        Assert.Equal(164 * scale, Number(picture, "height"), 1);
        Assert.Equal(0, Number(picture, "x"), 1);
        Assert.Equal((200 - (164 * scale)) / 2, Number(picture, "y"), 1);
    }

    [Fact]
    public void A_small_diagram_is_shown_a_little_larger_than_life_and_never_blown_up_to_fill_the_box()
    {
        XElement root = Parse(SvgRenderer.RenderThumbnail(Document([Box("n_a", 0, 0, 10, 10)], []), ThumbnailDetail.Full).Svg);
        XElement picture = Assert.Single(root.Elements(Svg + "svg"));

        // A 74 x 74 picture: the box would allow x2.7, the cap is x1.5.
        Assert.Equal(74 * 1.5, Number(picture, "width"), 1);
    }

    [Fact]
    public void A_full_thumbnail_wraps_the_documents_own_picture_unchanged()
    {
        Document document = Document([Box("n_a", 0, 0, 200, 100, "Alpha"), Box("n_b", 300, 0, 200, 100, "Beta")], [Edge("e_1", "n_a", "n_b")]);

        string thumbnail = SvgRenderer.RenderThumbnail(document, ThumbnailDetail.Full).Svg;
        string render = SvgRenderer.Render(document).Svg;

        // The body is everything Render draws after its root tag, byte for byte: a thumbnail is the same drawing, not a second one.
        string body = render[(render.IndexOf('\n', StringComparison.Ordinal) + 1)..render.LastIndexOf("</svg>", StringComparison.Ordinal)];
        Assert.Contains(body, thumbnail, StringComparison.Ordinal);
        Assert.Contains(">Alpha</text>", thumbnail, StringComparison.Ordinal);
        Assert.Contains(">Beta</text>", thumbnail, StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_the_thumbnail_does_not_change_what_an_export_renders()
    {
        Document document = Document([Box("n_a", 0, 0, 200, 100, "Alpha")], []);

        string render = SvgRenderer.Render(document, 2).Svg;

        // The root tag and the paper, exactly as before the thumbnail existed, at the scale asked for.
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 264 164\" width=\"528\" height=\"328\">\n<rect x=\"0\" y=\"0\" width=\"264\" height=\"164\" fill=\"#FFFFFF\"/>\n", render, StringComparison.Ordinal);
        Assert.EndsWith("</svg>\n", render, StringComparison.Ordinal);
    }

    [Fact]
    public void A_label_that_is_markup_is_text_in_a_thumbnail_never_markup()
    {
        Document document = Document([Box("n_a", 0, 0, 200, 100, "<script>alert(1)</script>")], []);

        string svg = SvgRenderer.RenderThumbnail(document, ThumbnailDetail.Full).Svg;

        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", svg, StringComparison.Ordinal);
        Assert.Null(Record.Exception(() => Parse(svg)));
    }

    [Fact]
    public void An_image_node_is_the_plain_frame_and_no_asset_is_read()
    {
        Node image = Box("n_img", 0, 0, 200, 100) with { Shape = "image", Props = new Dictionary<string, System.Text.Json.JsonElement> { ["assetId"] = System.Text.Json.JsonSerializer.SerializeToElement("asset_1") } };

        string svg = SvgRenderer.RenderThumbnail(Document([image], []), ThumbnailDetail.Full).Svg;

        Assert.DoesNotContain("data:image", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<image", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_thumbnail_is_painted_on_the_organisations_paper_both_around_and_under_the_picture()
    {
        var theme = new DiagramThemeChoice("neutral", new Dictionary<string, string>(), "#123456");

        XElement root = Parse(SvgRenderer.RenderThumbnail(Document([Box("n_a", 0, 0, 400, 100)], []), ThumbnailDetail.Full, theme: theme).Svg);

        Assert.Equal("#123456", (string?)root.Elements(Svg + "rect").First().Attribute("fill"));
        Assert.Equal("#123456", (string?)root.Element(Svg + "svg")!.Elements(Svg + "rect").First().Attribute("fill"));
    }

    [Fact]
    public void A_simplified_thumbnail_has_no_text_no_markers_and_no_routes_only_boxes_and_lines()
    {
        Document document = Document(
            [Box("n_a", 0, 0, 200, 100, "Alpha"), Box("n_b", 400, 0, 200, 100, "Beta")],
            [Edge("e_1", "n_a", "n_b", "goes to") with { Routing = new Routing(RoutingMode.Orthogonal) }]);

        string svg = SvgRenderer.RenderThumbnail(document, ThumbnailDetail.Simplified).Svg;

        Assert.DoesNotContain("<text", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("goes to", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<marker", svg, StringComparison.Ordinal);
        XElement root = Parse(svg);
        Assert.Equal("0 0 320 200", (string?)root.Attribute("viewBox"));
        XElement picture = Assert.Single(root.Elements(Svg + "svg"));
        Assert.Equal(2, picture.Descendants(Svg + "rect").Count());
        // One path for every connector, centre to centre.
        Assert.Equal("M100,50L500,50", (string?)Assert.Single(picture.Elements(Svg + "path")).Attribute("d"));
    }

    [Fact]
    public void A_simplified_thumbnail_draws_each_shape_as_its_outline_family_and_groups_nodes_by_colour()
    {
        Document document = Document(
            [
                Box("n_rect", 0, 0, 100, 60) with { Style = new NodeStyle { Fill = "#ff0000", Stroke = "#000000" } },
                Box("n_pill", 200, 0, 100, 60) with { Shape = "pill", Style = new NodeStyle { Fill = "#ff0000", Stroke = "#000000" } },
                Box("n_dia", 400, 0, 100, 60) with { Shape = "diamond", Style = new NodeStyle { Fill = "#00ff00", Stroke = "#000000" } },
                Box("n_oval", 600, 0, 100, 60) with { Shape = "ellipse", Style = new NodeStyle { Fill = "#00ff00", Stroke = "#000000" } },
            ],
            []);

        XElement picture = Parse(SvgRenderer.RenderThumbnail(document, ThumbnailDetail.Simplified).Svg).Element(Svg + "svg")!;

        XElement[] groups = picture.Elements(Svg + "g").ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Equal("#ff0000", (string?)groups[0].Attribute("fill"));
        Assert.Equal(["rect", "rect"], groups[0].Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.Equal("30", (string?)groups[0].Elements().Last().Attribute("rx"));
        Assert.Equal(["polygon", "ellipse"], groups[1].Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.Equal("450,0 500,30 450,60 400,30", (string?)groups[1].Elements().First().Attribute("points"));
    }

    [Fact]
    public void A_simplified_thumbnail_draws_a_container_before_what_it_holds()
    {
        Document document = Document(
            [
                Box("n_child", 20, 20, 60, 30) with { Parent = "n_box" },
                Box("n_box", 0, 0, 200, 120) with { Kind = "container", Style = new NodeStyle { Fill = "transparent", Stroke = "#999999" } },
            ],
            []);

        XElement picture = Parse(SvgRenderer.RenderThumbnail(document, ThumbnailDetail.Simplified).Svg).Element(Svg + "svg")!;

        XElement[] shapes = picture.Descendants(Svg + "rect").Where(e => e.Attribute("width") is not null).ToArray();
        Assert.Equal("200", (string?)shapes[0].Attribute("width"));
        Assert.Equal("60", (string?)shapes[1].Attribute("width"));
    }

    [Fact]
    public void A_simplified_thumbnail_leaves_out_an_edge_to_a_node_that_is_not_there_and_a_self_loop()
    {
        Document document = Document([Box("n_a", 0, 0, 100, 60)], [Edge("e_gone", "n_a", "n_missing"), Edge("e_loop", "n_a", "n_a")]);

        XElement picture = Parse(SvgRenderer.RenderThumbnail(document, ThumbnailDetail.Simplified).Svg).Element(Svg + "svg")!;

        Assert.Empty(picture.Elements(Svg + "path"));
    }

    [Fact]
    public void A_simplified_thumbnail_keeps_a_rotated_node_rotated()
    {
        Node tilted = Box("n_a", 0, 0, 100, 60) with { Geometry = new Geometry(0, 0, 100, 60) { Rotation = 45 } };

        string svg = SvgRenderer.RenderThumbnail(Document([tilted], []), ThumbnailDetail.Simplified).Svg;

        Assert.Contains("transform=\"rotate(45 50 30)\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_simplified_thumbnail_takes_its_colours_from_the_theme_and_the_paper_from_the_theme()
    {
        var theme = new DiagramThemeChoice("overpass", new Dictionary<string, string>(), "#abcdef");
        Node plain = Box("n_a", 0, 0, 100, 60) with { Style = null };

        string svg = SvgRenderer.RenderThumbnail(Document([plain], []), ThumbnailDetail.Simplified, theme: theme).Svg;

        XElement root = Parse(svg);
        Assert.Equal("#abcdef", (string?)root.Elements(Svg + "rect").First().Attribute("fill"));
        // The kind's themed fill, not the renderer's own white default.
        string fill = (string)root.Descendants(Svg + "g").First().Attribute("fill")!;
        Assert.Matches("^#[0-9a-fA-F]{6}$", fill);
        Assert.NotEqual("#ffffff", fill, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_simplified_thumbnail_of_a_very_large_diagram_stays_small()
    {
        Node[] nodes = Enumerable.Range(0, 2000).Select(i => Box($"n_{i}", (i % 50) * 160, (i / 50) * 100, 120, 60, $"Node number {i}")).ToArray();
        Edge[] edges = Enumerable.Range(1, 2000).Select(i => Edge($"e_{i}", $"n_{i - 1}", $"n_{i % 2000}")).ToArray();

        string svg = SvgRenderer.RenderThumbnail(Document(nodes, edges), ThumbnailDetail.Simplified).Svg;

        // A byte count, not a time: the drawing is one line per connector in a single path and one short element per node.
        Assert.True(svg.Length < 150_000, $"a 2,000-node simplified thumbnail is {svg.Length:N0} characters");
        Assert.Equal(2000, Parse(svg).Descendants(Svg + "rect").Count(e => e.Attribute("width") is { Value: "120" }));
    }

    [Fact]
    public void The_empty_placeholder_is_a_dashed_frame_the_others_are_a_grey_diagram_and_neither_has_text()
    {
        string empty = SvgRenderer.RenderThumbnailPlaceholder(ThumbnailPlaceholder.Empty);
        string tooLarge = SvgRenderer.RenderThumbnailPlaceholder(ThumbnailPlaceholder.TooLarge);
        string unavailable = SvgRenderer.RenderThumbnailPlaceholder(ThumbnailPlaceholder.Unavailable);

        Assert.Contains("stroke-dasharray", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("stroke-dasharray", tooLarge, StringComparison.Ordinal);
        Assert.Equal(tooLarge, unavailable);
        foreach (string svg in new[] { empty, tooLarge })
        {
            XElement root = Parse(svg);
            Assert.Equal("0 0 320 200", (string?)root.Attribute("viewBox"));
            Assert.Empty(root.Descendants(Svg + "text"));
            Assert.Equal("#FFFFFF", (string?)root.Elements(Svg + "rect").First().Attribute("fill"));
        }
    }

    [Fact]
    public void A_placeholder_is_painted_on_the_organisations_paper()
    {
        var theme = new DiagramThemeChoice("overpass", new Dictionary<string, string>(), "#abcdef");

        XElement root = Parse(SvgRenderer.RenderThumbnailPlaceholder(ThumbnailPlaceholder.TooLarge, theme));

        Assert.Equal("#abcdef", (string?)root.Elements(Svg + "rect").First().Attribute("fill"));
    }

    [Fact]
    public void A_simplified_thumbnail_of_a_document_with_nothing_placed_is_the_empty_placeholder()
    {
        string svg = SvgRenderer.RenderThumbnail(Document([], []), ThumbnailDetail.Simplified).Svg;

        Assert.Equal(SvgRenderer.RenderThumbnailPlaceholder(ThumbnailPlaceholder.Empty), svg);
    }

    private static XElement Parse(string svg) => XDocument.Parse(svg).Root!;

    private static double Number(XElement element, string attribute) =>
        double.Parse(element.Attribute(attribute)!.Value, CultureInfo.InvariantCulture);

    private static Node Box(string id, double x, double y, double w, double h, string label = "") =>
        new(id, "process", label, "rectangle") { Geometry = new Geometry(x, y, w, h) };

    private static Edge Edge(string id, string from, string to, string? label = null) =>
        new(id, "flow", new Endpoint(from), new Endpoint(to)) { Label = label };

    private static Document Document(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges) =>
        new("doc_1", 1, "T", DocumentType.Flowchart, nodes, edges, new Meta(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "user:local"));
}
