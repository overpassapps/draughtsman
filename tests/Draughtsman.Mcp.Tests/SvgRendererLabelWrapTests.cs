using System.Globalization;
using System.Xml.Linq;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Mcp.Rendering;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// A node's label is wrapped inside its shape's label rectangle the way the web canvas wraps it (NodeLabelLayout), so a long label
/// neither runs off its shape nor, on a dark theme, vanishes against the paper beside it. The same drawing serves the SVG export,
/// the PDF and the thumbnail.
/// </summary>
public class SvgRendererLabelWrapTests
{
    private static readonly string[] s_flowNodes = ["n_begin", "n_intake", "n_review", "n_x_done", "n_idle1", "n_labels"];

    private static Document Doc(params Node[] nodes) =>
        new("doc_1", 1, "T", DocumentType.Flowchart, nodes, [], new Meta(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "user:local"));

    private static Node Box(string id, string label, string shape = "rectangle", double w = 120, double h = 60, string kind = "process", NodeStyle? style = null) =>
        new(id, kind, label, shape) { Geometry = new Geometry(0, 0, w, h), Style = style };

    private static XElement Group(XDocument xml, string id) => Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == id);

    private static XElement LabelOf(XDocument xml, string id) => Group(xml, id).Elements().Single(e => e.Name.LocalName == "text");

    /// <summary>The lines a label was drawn on: its tspans, or its text when it is one line.</summary>
    private static string[] Lines(XElement text)
    {
        List<XElement> spans = [.. text.Elements().Where(e => e.Name.LocalName == "tspan")];
        return spans.Count == 0 ? [text.Value] : [.. spans.Select(span => span.Value)];
    }

    /// <summary>Where the node's box sits in the picture: the renderer pads and may centre the drawing, so a test reads the shape rather than assuming an origin.</summary>
    private static (double X, double Y) Origin(XDocument xml, string id)
    {
        XElement shape = Group(xml, id).Elements().First(e => e.Name.LocalName == "rect");
        return (Number(shape, "x"), Number(shape, "y"));
    }

    private static double Number(XElement element, string attribute) => double.Parse((string)element.Attribute(attribute)!, CultureInfo.InvariantCulture);

    private static double[] LineYs(XElement text)
    {
        List<XElement> spans = [.. text.Elements().Where(e => e.Name.LocalName == "tspan")];
        return spans.Count == 0 ? [Number(text, "y")] : [.. spans.Select(span => Number(span, "y"))];
    }

    private static XDocument Render(Document document) => XDocument.Parse(SvgRenderer.Render(document).Svg);

    [Fact]
    public void A_long_single_word_is_split_between_characters_inside_its_shape()
    {
        XDocument xml = Render(Doc(Box("n_a", "Supercalifragilisticexpialidocious", w: 140, h: 100)));

        // 140 wide, 8 of padding either side: 124 for text.
        string[] lines = Lines(LabelOf(xml, "n_a"));
        Assert.True(lines.Length > 1);
        Assert.Equal("Supercalifragilisticexpialidocious", string.Concat(lines));
        Assert.All(lines, line => Assert.True(NodeLabelLayout.EstimateWidth(line, NodeLabelLayout.FontSize) <= 124 + 1e-9, line));
    }

    [Fact]
    public void A_long_label_of_several_words_wraps_at_spaces_and_the_block_is_centred_on_the_shape()
    {
        XDocument xml = Render(Doc(Box("n_a", "Collect feeds: RSS from four crypto outlets, Fear & Greed index, CryptoPanic if keyed", w: 250, h: 100)));
        XElement text = LabelOf(xml, "n_a");

        Assert.Equal(["Collect feeds: RSS from four crypto", "outlets, Fear & Greed index,", "CryptoPanic if keyed"], Lines(text));
        Assert.Equal("middle", (string?)text.Attribute("text-anchor"));
        double[] ys = LineYs(text);
        // Three 17-high lines centred on the middle of the 100-high box.
        (double left, double top) = Origin(xml, "n_a");
        Assert.Equal(top + 50, ys[1], 2);
        Assert.Equal([17.0, 17.0], [ys[1] - ys[0], ys[2] - ys[1]]);
        Assert.All(text.Elements(), span => Assert.Equal(left + 125, Number(span, "x"), 2));
    }

    [Fact]
    public void An_explicit_break_is_kept_and_a_line_that_is_still_too_long_wraps_further()
    {
        XDocument xml = Render(Doc(Box("n_a", "Plan well-formed\nand within bounds?", w: 116, h: 100), Box("n_b", "Top\nBottom", w: 200, h: 100)));

        // 116 less padding is 100: each written line is on its own, and the first is wider than that.
        Assert.Equal(["Plan", "well-formed", "and within", "bounds?"], Lines(LabelOf(xml, "n_a")));
        Assert.Equal(["Top", "Bottom"], Lines(LabelOf(xml, "n_b")));
    }

    [Fact]
    public void A_label_that_fits_stays_the_single_text_element_it_always_was()
    {
        XDocument xml = Render(Doc(Box("n_a", "Short", w: 200, h: 60)));

        XElement text = LabelOf(xml, "n_a");
        Assert.Empty(text.Elements());
        Assert.Equal("Short", text.Value);
        (double left, double top) = Origin(xml, "n_a");
        Assert.Equal(left + 100, Number(text, "x"), 2);
        Assert.Equal(top + 30, Number(text, "y"), 2);
    }

    [Fact]
    public void A_label_in_a_small_shape_wraps_to_the_shapes_label_rectangle_not_its_box()
    {
        // The diamond's rectangle is .62 of the width: 49.6 of an 80-wide box, 33.6 once padded.
        XDocument xml = Render(Doc(Box("n_a", "Approve order", "diamond", w: 80, h: 60, kind: "decision")));

        string[] lines = Lines(LabelOf(xml, "n_a"));
        Assert.True(lines.Length > 1);
        Assert.Equal("Approveorder", string.Concat(lines).Replace(" ", "", StringComparison.Ordinal));
        Assert.All(lines, line => Assert.True(NodeLabelLayout.EstimateWidth(line, NodeLabelLayout.FontSize) <= 33.6 + 1e-9, line));
    }

    [Fact]
    public void A_label_on_a_dark_theme_wraps_and_keeps_the_themes_light_text()
    {
        Document document = Doc(Box("n_a", "Target, stop, max hold, plan expiry or a new flat plan hit?", "diamond", w: 220, h: 130, kind: "decision")) with
        {
            Theme = new Dictionary<string, string> { ["exportBackground"] = "#000000", ["nodes.decision.fill"] = "#10141f", ["nodes.decision.stroke"] = "#7f8aa0", ["nodes.decision.textColor"] = "#F2F2F2" },
        };

        XDocument xml = Render(document);
        XElement text = LabelOf(xml, "n_a");

        Assert.Equal("#F2F2F2", (string?)text.Attribute("fill"));
        string[] lines = Lines(text);
        Assert.True(lines.Length > 1);
        // Every line fits the diamond's rectangle (.62 of 220, padded) and the block fits its height (half of 130).
        Assert.All(lines, line => Assert.True(NodeLabelLayout.EstimateWidth(line, NodeLabelLayout.FontSize) <= (220 * 0.62) - 16 + 1e-9, line));
        Assert.True(lines.Length * 17 <= 65);
    }

    [Fact]
    public void A_containers_label_wraps_to_its_width_and_runs_down_from_the_top_left()
    {
        Node container = Box("n_c", "Deterministic code: feeds, rules, risk, broker, ledgers", "rectangle", w: 160, h: 300, kind: "container");
        Node child = Box("n_k", "Child") with { Parent = "n_c", Geometry = new Geometry(20, 80, 100, 40) };
        XDocument xml = Render(Doc(container, child));

        XElement text = LabelOf(xml, "n_c");
        string[] lines = Lines(text);
        Assert.True(lines.Length > 1);
        Assert.Equal("start", (string?)text.Attribute("text-anchor"));
        double[] ys = LineYs(text);
        // First line on the 15 below the top the container's label always sat at, the rest 17 apart below it.
        (double left, double top) = Origin(xml, "n_c");
        Assert.Equal(top + 15, ys[0], 2);
        Assert.Equal(17.0, ys[1] - ys[0], 2);
        Assert.All(text.Elements(), span => Assert.Equal(left + 8, Number(span, "x"), 2));
        Assert.All(lines, line => Assert.True(NodeLabelLayout.EstimateWidth(line, NodeLabelLayout.FontSize) <= 160 - 16 + 1e-9, line));
    }

    [Fact]
    public void An_edge_label_is_not_wrapped()
    {
        Document document = Doc(Box("n_a", "A"), Box("n_b", "B") with { Geometry = new Geometry(400, 0, 120, 60) }) with
        {
            Edges = [new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b")) { Label = "A rather long edge label that goes on and on" }],
        };

        XDocument xml = Render(document);

        Assert.Equal(["A rather long edge label that goes on and on"], Lines(Group(xml, "e_1").Descendants().Single(e => e.Name.LocalName == "text")));
    }

    [Fact]
    public void A_thumbnail_wraps_a_label_as_the_export_does()
    {
        Document document = Doc(Box("n_a", "Supercalifragilisticexpialidocious", w: 140, h: 100));

        XDocument export = Render(document);
        XDocument thumbnail = XDocument.Parse(SvgRenderer.RenderThumbnail(document, ThumbnailDetail.Full).Svg);

        Assert.Equal(Lines(LabelOf(export, "n_a")), Lines(LabelOf(thumbnail, "n_a")));
    }

    [Fact]
    public void The_decision_flow_with_its_hand_made_breaks_removed_draws_every_label_inside_its_shape()
    {
        Document document = NodeLabelLayoutTests.Unbroken(NodeLabelLayoutTests.DecisionFlow());
        XDocument xml = Render(document);

        List<string> overflowing = [];
        foreach (Node node in document.Nodes.Where(n => !NodeLabelLayoutTests.IsContainer(n, document)))
        {
            LabelRect rect = NodeLabelLayout.RectFor(node.Shape, node.Geometry!.W, node.Geometry!.H);
            string[] lines = Lines(LabelOf(xml, node.Id));
            if (lines.Max(line => NodeLabelLayout.EstimateWidth(line, NodeLabelLayout.FontSize)) > rect.W - 16 + 1e-9 || lines.Length * 17 > rect.H + 1e-9)
            {
                overflowing.Add($"{node.Id}: {string.Join(" | ", lines)}");
            }
        }

        Assert.Empty(overflowing);
        Assert.All(s_flowNodes, id => Assert.NotEmpty(Lines(LabelOf(xml, id))));
    }

    [Fact]
    public void The_decision_flow_with_its_hand_made_breaks_draws_each_written_line_once()
    {
        Document document = NodeLabelLayoutTests.DecisionFlow();
        XDocument xml = Render(document);

        foreach (Node node in document.Nodes.Where(n => !NodeLabelLayoutTests.IsContainer(n, document)))
        {
            Assert.Equal(node.Label.Split('\n'), Lines(LabelOf(xml, node.Id)));
        }
    }
}
