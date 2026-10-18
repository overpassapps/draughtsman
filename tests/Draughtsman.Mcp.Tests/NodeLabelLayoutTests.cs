using Draughtsman.Core.Models;
using Draughtsman.Core.Text;
using Draughtsman.Mcp.Rendering;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// The server's label wrapping (<see cref="NodeLabelLayout"/>) against the canvas's (web/src/canvas/text.ts <c>wrapText</c>
/// and shapes.tsx <c>labelBox</c>). The literal lines are what <c>wrapText</c> returns for the same text, width and a 13px
/// font; <c>text.test.ts</c> pins the same strings, so a change to either rule fails both suites.
/// </summary>
public class NodeLabelLayoutTests
{
    [Fact]
    public void A_single_word_wider_than_the_line_is_split_between_characters()
    {
        Assert.Equal(["Supercalifragilis", "ticexpialidociou", "s"], NodeLabelLayout.Wrap("Supercalifragilisticexpialidocious", 100));
    }

    [Fact]
    public void A_long_label_breaks_greedily_at_spaces()
    {
        Assert.Equal(
            ["Collect feeds: RSS from four crypto", "outlets, Fear & Greed index,", "CryptoPanic if keyed"],
            NodeLabelLayout.Wrap("Collect feeds: RSS from four crypto outlets, Fear & Greed index, CryptoPanic if keyed", 234));
        Assert.Equal(["Hold; check", "again next", "minute"], NodeLabelLayout.Wrap("Hold; check again next minute", 80));
    }

    [Fact]
    public void An_explicit_newline_always_breaks_and_each_paragraph_wraps_on_its_own()
    {
        Assert.Equal(["Plan", "well-formed", "and within", "bounds?"], NodeLabelLayout.Wrap("Plan well-formed\nand within bounds?", 100));
        Assert.Equal(["Plan well-formed", "and within bounds?"], NodeLabelLayout.Wrap("Plan well-formed\r\nand within bounds?", 400));
        Assert.Equal(["a", "", "b"], NodeLabelLayout.Wrap("a\n\nb", 400));
    }

    [Fact]
    public void Spaces_a_line_breaks_at_hang_and_are_left_off_but_spaces_ending_a_paragraph_stay()
    {
        Assert.Equal(["  leading  and", "trailing  "], NodeLabelLayout.Wrap("  leading  and trailing  ", 90));
    }

    [Fact]
    public void There_is_always_at_least_one_line_and_a_one_unit_width_still_makes_progress()
    {
        Assert.Equal([""], NodeLabelLayout.Wrap("", 50));
        Assert.Equal(["A"], NodeLabelLayout.Wrap("A", 1));
        Assert.Equal(["a", "b"], NodeLabelLayout.Wrap("ab", 1));
    }

    [Fact]
    public void Text_is_never_split_inside_a_surrogate_pair()
    {
        // Each emoji is two UTF-16 units and one code point at 0.55 em; a 10-unit line holds one.
        Assert.Equal(["\U0001F600", "\U0001F600"], NodeLabelLayout.Wrap("\U0001F600\U0001F600", 10));
    }

    [Theory]
    [InlineData("Plan well-formed", 106.73)]
    [InlineData("WWW", 35.1)]
    [InlineData("iii", 12.48)]
    [InlineData("0123", 30.16)]
    [InlineData("a b", 17.94)]
    public void The_width_estimate_is_the_canvas_ones_character_classes(string text, double expected)
    {
        Assert.Equal(expected, NodeLabelLayout.EstimateWidth(text, 13), 2);
    }

    [Fact]
    public void A_shapes_label_rectangle_is_the_canvas_label_box()
    {
        // 320 x 130 decision: x .19w, y h/4, .62w by h/2 (shapes.tsx 'diamond').
        AssertRect(new LabelRect(60.8, 32.5, 198.4, 65), NodeLabelLayout.RectFor("diamond", 320, 130));
        // A pill keeps its label clear of the rounded ends: h/4 in from each (shapes.tsx 'pill').
        AssertRect(new LabelRect(14, 0, 202, 56), NodeLabelLayout.RectFor("pill", 230, 56));
        AssertRect(new LabelRect(50, 0, 250, 100), NodeLabelLayout.RectFor("parallelogram", 350, 100));
        AssertRect(new LabelRect(0, 0, 238, 90), NodeLabelLayout.RectFor("stored-data", 280, 90));
        AssertRect(new LabelRect(0, 0, 160, 42), NodeLabelLayout.RectFor("document", 160, 60));
        AssertRect(new LabelRect(0, 0, 120, 60), NodeLabelLayout.RectFor("rectangle", 120, 60));
        AssertRect(new LabelRect(0, 0, 120, 60), NodeLabelLayout.RectFor(null, 120, 60));
        // A cylinder labels below its lip: 2 * min(.12h, 14) = 24 on a 100-high box.
        AssertRect(new LabelRect(0, 24, 100, 76), NodeLabelLayout.RectFor("cylinder", 100, 100));
    }

    [Fact]
    public void A_label_in_a_small_shape_is_wrapped_to_the_rectangle_and_not_the_box()
    {
        // 80 x 60 diamond: 49.6 wide less 16 of padding leaves 33.6 for text.
        LabelRect rect = NodeLabelLayout.RectFor("diamond", 80, 60);
        IReadOnlyList<string> lines = NodeLabelLayout.Wrap("Approve order", rect.W - (NodeLabelLayout.Padding * 2));

        Assert.True(lines.Count > 1);
        Assert.All(lines, line => Assert.True(NodeLabelLayout.EstimateWidth(line, NodeLabelLayout.FontSize) <= 33.6 + 1e-9, line));
    }

    [Fact]
    public void A_label_with_its_hand_made_breaks_removed_fits_every_shape_of_the_decision_flow()
    {
        Document document = Unbroken(DecisionFlow());

        List<string> overflowing = [];
        foreach (Node node in document.Nodes.Where(n => !IsContainer(n, document)))
        {
            LabelRect rect = NodeLabelLayout.RectFor(node.Shape, node.Geometry!.W, node.Geometry!.H);
            IReadOnlyList<string> lines = NodeLabelLayout.Wrap(node.Label, rect.W - (NodeLabelLayout.Padding * 2));
            double widest = lines.Max(line => NodeLabelLayout.EstimateWidth(line, NodeLabelLayout.FontSize));
            if (widest > rect.W - (NodeLabelLayout.Padding * 2) + 1e-9 || lines.Count * LineHeight > rect.H + 1e-9)
            {
                overflowing.Add($"{node.Id}: {lines.Count} lines, widest {widest:0.#} in {rect.W:0.#} x {rect.H:0.#}");
            }
        }

        Assert.Empty(overflowing);
    }

    [Fact]
    public void A_label_hand_broken_for_the_old_renderer_is_not_wrapped_a_second_time()
    {
        Document document = DecisionFlow();

        List<string> doubled = [];
        foreach (Node node in document.Nodes.Where(n => !IsContainer(n, document)))
        {
            LabelRect rect = NodeLabelLayout.RectFor(node.Shape, node.Geometry!.W, node.Geometry!.H);
            int written = node.Label.Split('\n').Length;
            int drawn = NodeLabelLayout.Wrap(node.Label, rect.W - (NodeLabelLayout.Padding * 2)).Count;
            if (drawn != written)
            {
                doubled.Add($"{node.Id}: {written} written lines drawn on {drawn}");
            }
        }

        Assert.Empty(doubled);
    }

    /// <summary>The line step of a multi-line label (SvgRenderer's 17).</summary>
    internal const double LineHeight = 17;

    internal static Document DecisionFlow() =>
        Dsl.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "label-wrap", "decision-flow-hand-broken.dsl"))).Document;

    /// <summary>The same document with every explicit break in a label replaced by a space.</summary>
    internal static Document Unbroken(Document document) =>
        document with { Nodes = [.. document.Nodes.Select(n => n with { Label = n.Label.Replace('\n', ' ') })] };

    internal static bool IsContainer(Node node, Document document) =>
        node.Shape.StartsWith("custom:swimlane/", StringComparison.Ordinal) || document.Nodes.Any(n => n.Parent == node.Id);

    private static void AssertRect(LabelRect expected, LabelRect actual)
    {
        Assert.Equal(expected.X, actual.X, 2);
        Assert.Equal(expected.Y, actual.Y, 2);
        Assert.Equal(expected.W, actual.W, 2);
        Assert.Equal(expected.H, actual.H, 2);
    }
}
