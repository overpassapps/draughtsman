using System.Text.Json;
using System.Xml.Linq;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Mcp.Rendering;

namespace Draughtsman.Mcp.Tests;

public class SvgRendererTests
{
    [Fact]
    public void Render_paints_an_opaque_background_rectangle_over_the_whole_picture_first()
    {
        // render_document's svg is never transparent (QA round two suspected it was): the first drawn element is a full-size, opaque rect.
        var node = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 200, 100) };
        SvgRenderResult result = SvgRenderer.Render(Document([node], []));
        XDocument xml = XDocument.Parse(result.Svg);
        XElement paper = Assert.Single(xml.Root!.Elements(), e => e.Name.LocalName == "rect" && (string?)e.Attribute("x") == "0" && (string?)e.Attribute("y") == "0");
        Assert.Equal(paper, xml.Root!.Elements().First(e => e.Name.LocalName == "rect"));
        Assert.Matches("^#[0-9a-fA-F]{6}$", (string?)paper.Attribute("fill"));
        Assert.Equal((string?)xml.Root!.Attribute("width"), (string?)paper.Attribute("width"));
        Assert.Equal((string?)xml.Root!.Attribute("height"), (string?)paper.Attribute("height"));
    }

    [Theory]
    [InlineData("document", "path", "d", "M32,32 H232 V117 C182,87 82,147 32,117 Z", "67")]
    [InlineData("predefined-process", "path", "d", "M56,32 V132 M208,32 V132", "82")]
    [InlineData("manual-input", "polygon", "points", "32,57 232,32 232,132 32,132", "94.5")]
    [InlineData("off-page-connector", "polygon", "points", "32,32 232,32 232,97 132,132 32,97", "64.5")]
    public void Render_draws_flowchart_outline_and_places_label_inside(string shape, string tag, string attribute, string expected, string labelY)
    {
        var node = new Node("n_shape", "process", "Label", shape) { Geometry = new Geometry(0, 0, 200, 100) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_shape");
        XElement outline = Assert.Single(group.Elements(), e => e.Name.LocalName == tag);
        Assert.Equal(expected, (string?)outline.Attribute(attribute));
        XElement text = Assert.Single(group.Elements(), e => e.Name.LocalName == "text");
        Assert.Equal(labelY, (string?)text.Attribute("y"));
        Assert.Equal("Label", text.Value);
    }

    [Theory]
    [InlineData("delay", "path", "d", "M32,32 H132 A100,50 0 0 1 132,132 H32 Z", "112", "82")]
    [InlineData("manual-operation", "polygon", "points", "32,32 232,32 192,132 72,132", "132", "82")]
    [InlineData("display", "path", "d", "M82,32 H232 V132 H82 A50,50 0 0 0 82,32 Z", "157", "82")]
    [InlineData("stored-data", "path", "d", "M32,32 H182 C232,32 232,57 232,82 C232,107 232,132 182,132 H32 Z", "117", "82")]
    [InlineData("multi-document", "path", "d", "M72,32 H232 V100 C192,76 112,124 72,100 Z", "112", "84")]
    [InlineData("merge", "polygon", "points", "32,32 232,32 132,132", "132", "62")]
    [InlineData("extract", "polygon", "points", "132,32 232,132 32,132", "132", "102")]
    [InlineData("on-page-connector", "ellipse", "rx", "100", "132", "82")]
    [InlineData("summing-junction", "ellipse", "rx", "100", "132", "82")]
    [InlineData("or", "ellipse", "rx", "100", "132", "82")]
    [InlineData("collate", "polygon", "points", "32,32 232,32 32,132 232,132", "132", "82")]
    [InlineData("sort", "polygon", "points", "132,32 232,82 132,132 32,82", "132", "82")]
    [InlineData("loop-limit", "polygon", "points", "72,32 192,32 232,72 232,132 32,132 32,72", "132", "87")]
    [InlineData("internal-storage", "rect", "width", "200", "152", "92")]
    [InlineData("card", "polygon", "points", "72,32 232,32 232,132 32,132 32,72", "132", "82")]
    public void Render_draws_every_iso_symbol_outline_and_places_its_label_inside(string shape, string tag, string attribute, string expected, string labelX, string labelY)
    {
        var node = new Node("n_shape", "process", "Label", shape) { Geometry = new Geometry(0, 0, 200, 100) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_shape");

        // multi-document stacks three pages; the rest draw one outline, so match on any element rather than Single.
        Assert.Contains(group.Elements(), e => e.Name.LocalName == tag && (string?)e.Attribute(attribute) == expected);

        // The label is drawn inside the node's box (drawn at 32..232 by 32..132), at the shape's own anchor.
        XElement text = Assert.Single(group.Elements(), e => e.Name.LocalName == "text");
        Assert.Equal(labelX, (string?)text.Attribute("x"));
        Assert.Equal(labelY, (string?)text.Attribute("y"));
    }

    /// <summary>
    /// Every shape the schema admits must draw something other than the fallback rectangle: a shape added to the
    /// schema and not to <see cref="SvgRenderer"/> would otherwise silently render as a plain box, the exact failure
    /// this guards. `custom:*` shapes fall back by design, and `image` has always drawn as a plain frame server-side
    /// (the web canvas draws the picture placeholder; the server renderer does not, tracked in AGENTS.md), so both are
    /// left out. The web side (`shapes.test.tsx`) runs the same sweep without the image exception.
    /// </summary>
    [Fact]
    public void Render_draws_every_schema_shape_as_more_than_the_fallback_rectangle()
    {
        string fallback = NodeShapeMarkup("rectangle");
        foreach (string shape in SchemaShapeAlternatives().Where(s => !s.StartsWith("custom:", StringComparison.Ordinal) && s != "image"))
        {
            if (shape == "rectangle")
            {
                Assert.Equal(fallback, NodeShapeMarkup(shape));
                continue;
            }

            Assert.NotEqual(fallback, NodeShapeMarkup(shape));
        }
    }

    /// <summary>
    /// Every icon in the generic library draws as an icon (not the fallback rectangle) in the server renderer, and an
    /// unknown <c>custom:generic/*</c> name still falls back. The web canvas runs the matching sweep in
    /// <c>genericIcons.test.tsx</c>; both read the same library file, so this proves the server draws every one.
    /// </summary>
    [Fact]
    public void Render_draws_every_generic_icon_as_more_than_the_fallback_rectangle()
    {
        string fallback = NodeShapeMarkup("rectangle");
        Assert.NotEmpty(GenericIconLibrary.All);
        foreach (GenericIcon icon in GenericIconLibrary.All)
        {
            string shape = GenericIconLibrary.Prefix + icon.Name;
            string markup = NodeShapeMarkup(shape);
            Assert.NotEqual(fallback, markup);
            Assert.Contains("class=\"icon\"", markup, StringComparison.Ordinal);
            Assert.Contains("scale(", markup, StringComparison.Ordinal);
        }

        // The two-sided case: a name the library does not carry is not an icon and draws as the fallback.
        Assert.Equal(fallback, NodeShapeMarkup("custom:generic/not-a-real-icon"));
    }

    [Fact]
    public void Render_places_a_generic_icon_label_in_the_box_s_bottom_band()
    {
        var node = new Node("n_icon", "server", "Label", "custom:generic/server") { Geometry = new Geometry(0, 0, 200, 100) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_icon");

        // The node box is drawn at 32..232 by 32..132; the label anchors at the band's centre, 0.85 down.
        XElement text = Assert.Single(group.Elements(), e => e.Name.LocalName == "text");
        Assert.Equal("132", (string?)text.Attribute("x"));
        Assert.Equal("117", (string?)text.Attribute("y"));

        // The hit rectangle never paints but is hit-testable (a fill other than "none").
        XElement hit = Assert.Single(group.Elements(), e => (string?)e.Attribute("class") == "icon-hit");
        Assert.NotEqual("none", (string?)hit.Attribute("fill"));
    }

    [Fact]
    public void Render_draws_a_boundary_icon_as_a_labelled_box_with_a_corner_glyph()
    {
        var node = new Node("n_boundary", "container", "VPC", "custom:generic/vpc") { Geometry = new Geometry(0, 0, 200, 100) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_boundary");

        // The whole box is the boundary rect, and the artwork still draws as an icon inside it.
        Assert.Contains(group.Elements(), e => e.Name.LocalName == "rect" && (string?)e.Attribute("class") != "icon-hit" && (string?)e.Attribute("width") == "200" && (string?)e.Attribute("height") == "100");
        Assert.Single(group.Elements(), e => (string?)e.Attribute("class") == "icon");

        // The label is top-left (the box is drawn at 32..232 by 32..132), not centred in a bottom band.
        XElement text = Assert.Single(group.Elements(), e => e.Name.LocalName == "text");
        Assert.Equal("start", (string?)text.Attribute("text-anchor"));
        Assert.Equal("40", (string?)text.Attribute("x"));
        Assert.Equal("47", (string?)text.Attribute("y"));

        // Two-sided: a leaf icon keeps a centred label in its bottom band and draws no full-box boundary rect.
        var leaf = new Node("n_leaf", "server", "Server", "custom:generic/server") { Geometry = new Geometry(0, 0, 200, 100) };
        XElement leafGroup = Assert.Single(XDocument.Parse(SvgRenderer.Render(Document([leaf], [])).Svg).Descendants(), e => (string?)e.Attribute("data-id") == "n_leaf");
        XElement leafText = Assert.Single(leafGroup.Elements(), e => e.Name.LocalName == "text");
        Assert.Equal("middle", (string?)leafText.Attribute("text-anchor"));
        Assert.Equal("132", (string?)leafText.Attribute("x"));
        Assert.Equal("117", (string?)leafText.Attribute("y"));
        Assert.DoesNotContain(leafGroup.Elements(), e => e.Name.LocalName == "rect" && (string?)e.Attribute("class") != "icon-hit");
    }

    /// <summary>
    /// Every shape in the state library draws as something other than the fallback rectangle in the server renderer, and
    /// an unknown <c>custom:state/*</c> name still falls back. The web canvas runs the matching sweep against the same
    /// library (web/src/canvas/stateShapes.test.tsx), so this proves the server draws every one.
    /// </summary>
    [Fact]
    public void Render_draws_every_state_shape_as_more_than_the_fallback_rectangle()
    {
        string fallback = NodeShapeMarkup("rectangle");
        Assert.NotEmpty(StateShapeLibrary.All);
        foreach (StateShape shape in StateShapeLibrary.All)
        {
            Assert.NotEqual(fallback, NodeShapeMarkup(StateShapeLibrary.ShapeName(shape)));
        }

        // The two-sided case: a name the library does not carry is not a state shape and draws as the fallback.
        Assert.Equal(fallback, NodeShapeMarkup("custom:state/not-a-real-shape"));
    }

    [Theory]
    [InlineData("initial", "circle", "r", "22")]
    [InlineData("final", "circle", "r", "30")]
    [InlineData("choice", "polygon", "points", "132,32 232,82 132,132 32,82")]
    [InlineData("fork-join", "rect", "height", "14")]
    [InlineData("shallow-history", "circle", "r", "28")]
    [InlineData("entry-point", "circle", "r", "24")]
    [InlineData("composite-state", "rect", "rx", "8")]
    public void Render_draws_each_state_shape_with_its_own_geometry(string name, string tag, string attribute, string expected)
    {
        string shape = StateShapeLibrary.Prefix + name;
        var node = new Node("n_state", name, "Label", shape) { Geometry = new Geometry(0, 0, 200, 100) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_state");

        Assert.Contains(group.Elements(), e => e.Name.LocalName == tag && (string?)e.Attribute(attribute) == expected);
    }

    [Fact]
    public void Render_hides_a_pseudostate_label_and_places_a_composite_state_label_in_its_name_compartment()
    {
        // The pseudostates draw no label of their own: no <text> carries the node's label. A history marker does draw
        // an H/H* glyph as text, so the check is on the label text, not on the element's presence.
        foreach (string name in new[] { "initial", "final", "choice", "fork-join", "shallow-history", "deep-history", "entry-point", "exit-point" })
        {
            var node = new Node("n_state", name, "Label", StateShapeLibrary.Prefix + name) { Geometry = new Geometry(0, 0, 200, 100) };
            XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
            XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_state");
            Assert.DoesNotContain(group.Elements(), e => e.Name.LocalName == "text" && e.Value == "Label");
        }

        // The composite state names itself in the top compartment: the divider is at y = 32 + 28, the label centred in
        // the band above it.
        var composite = new Node("n_cs", "composite-state", "Order", StateShapeLibrary.Prefix + "composite-state") { Geometry = new Geometry(0, 0, 200, 100) };
        XDocument rendered = XDocument.Parse(SvgRenderer.Render(Document([composite], [])).Svg);
        XElement cs = Assert.Single(rendered.Descendants(), e => (string?)e.Attribute("data-id") == "n_cs");
        Assert.Contains(cs.Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M32,60 H232");
        XElement text = Assert.Single(cs.Elements(), e => e.Name.LocalName == "text");
        Assert.Equal("132", (string?)text.Attribute("x"));
        Assert.Equal("46", (string?)text.Attribute("y"));
        Assert.Equal("Order", text.Value);
    }

    /// <summary>
    /// Every basic shape draws as something other than the fallback rectangle in the server renderer, and an unknown
    /// <c>custom:basic/*</c> name still falls back. The web canvas runs the matching sweep against the same library
    /// (web/src/canvas/basicShapes.test.tsx), so this proves the server draws every one.
    /// </summary>
    [Fact]
    public void Render_draws_every_basic_shape_as_more_than_the_fallback_rectangle()
    {
        string fallback = NodeShapeMarkup("rectangle");
        Assert.NotEmpty(BasicShapeLibrary.All);
        foreach (BasicShape shape in BasicShapeLibrary.All)
        {
            Assert.NotEqual(fallback, NodeShapeMarkup(BasicShapeLibrary.ShapeName(shape)));
        }

        Assert.Equal(fallback, NodeShapeMarkup("custom:basic/not-a-real-shape"));
    }

    /// <summary>The same outlines, at the same box, as the web canvas's test (basicShapes.test.tsx), offset by the renderer's 32 px margin.</summary>
    [Theory]
    [InlineData("triangle", 120, 100, "M92,32 L152,132 L32,132 Z")]
    [InlineData("note", 140, 90, "M32,32 L156,32 L172,48 L172,122 L32,122 Z")]
    [InlineData("folder", 160, 100, "M32,32 L96,32 L96,50 L192,50 L192,132 L32,132 Z")]
    public void Render_draws_a_basic_shape_outline_from_the_library_path_data(string name, double w, double h, string expected)
    {
        var node = new Node("n_basic", "process", "Label", BasicShapeLibrary.Prefix + name) { Geometry = new Geometry(0, 0, w, h) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_basic");

        XElement outline = group.Elements().First(e => e.Name.LocalName == "path");
        Assert.Equal(expected, (string?)outline.Attribute("d"));
        Assert.NotEqual("none", (string?)outline.Attribute("fill"));
    }

    [Fact]
    public void Render_draws_the_notes_fold_and_the_braces_line_with_no_fill_and_the_components_glyph_with_one()
    {
        XElement Group(string name, double w, double h)
        {
            var node = new Node("n_basic", "note", "Label", BasicShapeLibrary.Prefix + name) { Geometry = new Geometry(0, 0, w, h) };
            return Assert.Single(XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg).Descendants(), e => (string?)e.Attribute("data-id") == "n_basic");
        }

        XElement note = Group("note", 140, 90);
        XElement fold = note.Elements().Where(e => e.Name.LocalName == "path").ElementAt(1);
        Assert.Equal("M156,32 L156,48 L172,48", (string?)fold.Attribute("d"));
        Assert.Equal("none", (string?)fold.Attribute("fill"));

        XElement brace = Group("brace", 140, 120);
        XElement line = Assert.Single(brace.Elements(), e => e.Name.LocalName == "path");
        Assert.Equal("none", (string?)line.Attribute("fill"));

        XElement component = Group("component", 160, 90);
        string[] fills = component.Elements().Where(e => e.Name.LocalName == "path").Select(e => (string?)e.Attribute("fill") ?? "").ToArray();
        Assert.Equal(4, fills.Length);
        Assert.DoesNotContain("none", fills);
    }

    [Fact]
    public void Render_centres_a_basic_shapes_label_in_its_label_box_and_puts_a_containers_label_top_left_in_it()
    {
        // The triangle's label box is [0.25, 0.45, 0.75, 0.95] of 120 x 100: centred at (60, 70), drawn 32 px in.
        var triangle = new Node("n_tri", "process", "Tri", BasicShapeLibrary.Prefix + "triangle") { Geometry = new Geometry(0, 0, 120, 100) };
        XElement text = Assert.Single(Assert.Single(XDocument.Parse(SvgRenderer.Render(Document([triangle], [])).Svg).Descendants(), e => (string?)e.Attribute("data-id") == "n_tri").Elements(), e => e.Name.LocalName == "text");
        Assert.Equal("middle", (string?)text.Attribute("text-anchor"));
        Assert.Equal("92", (string?)text.Attribute("x"));
        Assert.Equal("102", (string?)text.Attribute("y"));

        // A folder that holds a node is a container: it names itself top-left in its label box, which starts under the tab.
        var folder = new Node("n_folder", "container", "Package", BasicShapeLibrary.Prefix + "folder") { Geometry = new Geometry(0, 0, 200, 140) };
        var inner = new Node("n_inner", "process", "Inner", "rounded") { Geometry = new Geometry(20, 40, 100, 50), Parent = "n_folder" };
        XElement packageText = Assert.Single(Assert.Single(XDocument.Parse(SvgRenderer.Render(Document([folder, inner], [])).Svg).Descendants(), e => (string?)e.Attribute("data-id") == "n_folder").Elements(), e => e.Name.LocalName == "text");
        Assert.Equal("start", (string?)packageText.Attribute("text-anchor"));
        Assert.Equal("46", (string?)packageText.Attribute("x"));
        Assert.Equal("69", (string?)packageText.Attribute("y"));
    }

    [Fact]
    public void The_basic_ellipse_is_an_outline_a_connector_attaches_to_like_a_bpmn_event_and_the_triangle_is_not()
    {
        Assert.Equal(EdgeAttachment.Outline.Ellipse, EdgeAttachment.OutlineOf(BasicShapeLibrary.Prefix + "ellipse"));
        Assert.Equal(EdgeAttachment.Outline.None, EdgeAttachment.OutlineOf(BasicShapeLibrary.Prefix + "triangle"));
    }

    /// <summary>
    /// Every org-chart box draws as an org box (not the fallback rectangle) in the server renderer, and an unknown
    /// <c>custom:org/*</c> name still falls back. The web canvas runs the matching sweep in <c>orgChart.test.tsx</c>;
    /// both read the same library file, so this proves the server draws every one.
    /// </summary>
    [Fact]
    public void Render_draws_every_org_box_as_more_than_the_fallback_rectangle()
    {
        string fallback = NodeShapeMarkup("rectangle");
        Assert.NotEmpty(OrgShapeLibrary.All);
        foreach (OrgShape box in OrgShapeLibrary.All)
        {
            string shape = OrgShapeLibrary.Prefix + box.Name;
            string markup = NodeShapeMarkup(shape);
            Assert.NotEqual(fallback, markup);
            Assert.Contains("class=\"shape org\"", markup, StringComparison.Ordinal);
        }

        // The two-sided case: a name the library does not carry is not an org box and draws as the fallback.
        Assert.Equal(fallback, NodeShapeMarkup("custom:org/not-a-real-box"));
    }

    [Fact]
    public void Render_draws_the_org_box_variants_chrome()
    {
        // Executive: the outer border plus the inset second border.
        string executive = NodeShapeMarkup("custom:org/executive");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(executive, "<rect ").Count);
        // Assistant: the left spine.
        Assert.Contains("<line ", NodeShapeMarkup("custom:org/assistant"), StringComparison.Ordinal);
        // Vacancy: a dashed outline.
        Assert.Contains("stroke-dasharray=\"6 4\"", NodeShapeMarkup("custom:org/vacancy"), StringComparison.Ordinal);
        // Role: the plain box, neither dashed nor decorated.
        string role = NodeShapeMarkup("custom:org/role");
        Assert.DoesNotContain("stroke-dasharray", role, StringComparison.Ordinal);
        Assert.DoesNotContain("<line ", role, StringComparison.Ordinal);
        Assert.DoesNotContain("shape-detail", role, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every swimlane shape draws as more than the fallback rectangle in the server renderer, and a name outside the
    /// set still falls back. The web canvas runs the matching sweep in web/src/canvas/swimlaneShapes.test.tsx.
    /// </summary>
    [Fact]
    public void Render_draws_every_swimlane_shape_as_more_than_the_fallback_rectangle()
    {
        string fallback = NodeShapeMarkup("rectangle");
        Assert.Equal(2, SwimlaneShapes.Shapes.Count);
        foreach (string shape in SwimlaneShapes.Shapes)
        {
            string markup = NodeShapeMarkup(shape);
            Assert.NotEqual(fallback, markup);
            Assert.Contains("class=\"shape swimlane\"", markup, StringComparison.Ordinal);
        }

        // The two-sided case: a name the set does not carry is not a swimlane and draws as the fallback.
        Assert.Equal(fallback, NodeShapeMarkup("custom:swimlane/not-a-real-shape"));
    }

    [Fact]
    public void Render_places_a_swimlane_label_top_left_below_the_band_divider()
    {
        var pool = new Node("n_pool", SwimlaneShapes.PoolKind, "Pool", SwimlaneShapes.PoolShape) { Geometry = new Geometry(0, 0, 200, 100) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([pool], [])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_pool");

        // The node box is drawn at 32..232 by 32..132; the band divider sits at 32 + PoolHeaderHeight(100) = 56.
        Assert.Contains(group.Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M32,56 H232");
        XElement text = Assert.Single(group.Elements(), e => e.Name.LocalName == "text");
        Assert.Equal("start", (string?)text.Attribute("text-anchor"));
        Assert.Equal("40", (string?)text.Attribute("x"));
        Assert.Equal("47", (string?)text.Attribute("y"));
    }

    [Fact]
    public void Render_breaks_a_role_box_label_onto_two_lines()
    {
        var node = new Node("n_org", "role", "Name\nTitle", "custom:org/role") { Geometry = new Geometry(0, 0, 200, 100) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_org");
        XElement text = Assert.Single(group.Elements(), e => e.Name.LocalName == "text");
        string[] lines = text.Elements().Where(e => e.Name.LocalName == "tspan").Select(e => e.Value).ToArray();
        Assert.Equal(["Name", "Title"], lines);

        // Two-sided: a single-line label keeps the one text element it always had, with no tspans.
        var single = new Node("n_one", "role", "Name", "custom:org/role") { Geometry = new Geometry(0, 0, 200, 100) };
        XDocument singleXml = XDocument.Parse(SvgRenderer.Render(Document([single], [])).Svg);
        XElement singleGroup = Assert.Single(singleXml.Descendants(), e => (string?)e.Attribute("data-id") == "n_one");
        XElement singleText = Assert.Single(singleGroup.Elements(), e => e.Name.LocalName == "text");
        Assert.DoesNotContain(singleText.Elements(), e => e.Name.LocalName == "tspan");
    }

    /// <summary>
    /// Every BPMN shape draws as a BPMN shape (not the fallback rectangle) in the server renderer, with a glyph exactly
    /// when the library gives it one, and an unknown <c>custom:bpmn/*</c> name still falls back. The web canvas runs the
    /// matching sweep in <c>bpmnShapes.test.tsx</c>; both read the same library file.
    /// </summary>
    [Fact]
    public void Render_draws_every_bpmn_shape_as_more_than_the_fallback_rectangle()
    {
        Assert.Equal(35, BpmnShapeLibrary.All.Count);
        foreach (BpmnShape shape in BpmnShapeLibrary.All)
        {
            string name = BpmnShapeLibrary.ShapeName(shape);
            var node = new Node("n_shape", shape.Kind, "Label", name) { Geometry = new Geometry(0, 0, shape.Width, shape.Height) };
            XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
            XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_shape");
            string markup = string.Concat(group.Elements().Select(e => e.ToString(SaveOptions.DisableFormatting)));

            Assert.Contains("class=\"shape bpmn\"", markup, StringComparison.Ordinal);
            Assert.Equal(shape.Glyph is null ? 0 : 1, group.Descendants().Count(e => (string?)e.Attribute("class") == "icon bpmn-glyph"));
        }

        // The two-sided case: a name the library does not carry is not a BPMN shape and draws as the fallback.
        Assert.Equal(NodeShapeMarkup("rectangle"), NodeShapeMarkup("custom:bpmn/not-a-real-shape"));
    }

    [Fact]
    public void Render_separates_start_intermediate_and_end_events_by_ring_count_and_border_weight()
    {
        static XElement[] Rings(string shape)
        {
            var node = new Node("n_shape", "x", "L", shape) { Geometry = new Geometry(0, 0, 40, 40) };
            XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
            XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_shape");
            return group.Elements().Where(e => e.Name.LocalName == "ellipse").ToArray();
        }

        XElement[] start = Rings("custom:bpmn/start");
        XElement[] intermediate = Rings("custom:bpmn/intermediate");
        XElement[] end = Rings("custom:bpmn/end");
        Assert.Single(start);
        Assert.Equal(2, intermediate.Length);
        Assert.Single(end);
        double thin = double.Parse((string)start[0].Attribute("stroke-width")!, System.Globalization.CultureInfo.InvariantCulture);
        double thick = double.Parse((string)end[0].Attribute("stroke-width")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(1.5, thin);
        Assert.Equal(1.5 * BpmnShapeLibrary.Layout.ThickBorder, thick, 5);
        Assert.True(double.Parse((string)intermediate[1].Attribute("rx")!, System.Globalization.CultureInfo.InvariantCulture) < double.Parse((string)intermediate[0].Attribute("rx")!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Render_fills_a_throwing_glyph_and_leaves_the_catching_glyph_an_outline()
    {
        static XElement Body(string shape)
        {
            var node = new Node("n_shape", "x", "L", shape) { Geometry = new Geometry(0, 0, 40, 40) };
            XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
            XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_shape");
            return group.Descendants().First(e => e.Name.LocalName == "g" && (string?)e.Attribute("class") == "icon bpmn-glyph").Elements().First(e => e.Name.LocalName == "rect");
        }

        Assert.Null(Body("custom:bpmn/intermediate-message").Attribute("fill"));
        Assert.Equal(DiagramTheme.Default.Node("x", null).Stroke, (string?)Body("custom:bpmn/intermediate-message-throw").Attribute("fill"));
    }

    [Fact]
    public void Render_places_an_event_label_below_the_circle_and_widens_the_picture_to_hold_it()
    {
        var node = new Node("n_event", "bpmn-start", "Order received", "custom:bpmn/start") { Geometry = new Geometry(0, 0, 40, 40) };
        SvgRenderResult rendered = SvgRenderer.Render(Document([node], []));
        XDocument xml = XDocument.Parse(rendered.Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_event");
        XElement text = Assert.Single(group.Elements(), e => e.Name.LocalName == "text");

        // The label band is 120 wide, 40 wider than the circle, so the picture grows 40 to the left: the circle's box is
        // drawn at x 72..112 (40 + padding 32) and y 32..72, and the band's centre is 4 + 16 below the box bottom.
        Assert.Equal("92", (string?)text.Attribute("x"));
        Assert.Equal("92", (string?)text.Attribute("y"));

        // The picture must reach the bottom of the label band and the padding under it.
        double height = double.Parse((string)xml.Root!.Attribute("height")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(height >= 40 + BpmnShapeLibrary.Layout.LabelGap + BpmnShapeLibrary.Layout.LabelBand + 64, $"height {height} cuts the label off");

        // Two-sided: the same node as a task keeps its label inside and needs no extra room.
        var task = new Node("n_task", "bpmn-task", "Order received", "custom:bpmn/task") { Geometry = new Geometry(0, 0, 40, 40) };
        double taskHeight = double.Parse((string)XDocument.Parse(SvgRenderer.Render(Document([task], [])).Svg).Root!.Attribute("height")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(taskHeight < height);
    }

    [Fact]
    public void Render_keeps_an_activity_label_clear_of_its_marker()
    {
        static double LabelY(string shape)
        {
            var node = new Node("n_shape", "x", "Label", shape) { Geometry = new Geometry(0, 0, 100, 70) };
            XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
            XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_shape");
            return double.Parse((string)Assert.Single(group.Elements(), e => e.Name.LocalName == "text").Attribute("y")!, System.Globalization.CultureInfo.InvariantCulture);
        }

        double plain = LabelY("custom:bpmn/task");
        Assert.Equal(67, plain);
        Assert.True(LabelY("custom:bpmn/user-task") > plain, "a corner marker moves the label down");
        Assert.True(LabelY("custom:bpmn/sub-process") < plain, "a bottom marker moves the label up");
    }

    [Fact]
    public void Render_stops_an_unnamed_edge_end_on_an_event_s_circle_and_a_gateway_s_diamond()
    {
        static (double X, double Y) LineStart(string shape, double size)
        {
            var from = new Node("n_from", "x", "F", shape) { Geometry = new Geometry(0, 0, size, size) };
            var to = new Node("n_to", "bpmn-task", "T", "custom:bpmn/task") { Geometry = new Geometry(100, 100, 100, 70) };
            var edge = new Edge("e_1", "flow", new Endpoint("n_from"), new Endpoint("n_to"));
            XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([from, to], [edge])).Svg);
            XElement edgeGroup = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "e_1");
            XElement line = Assert.Single(edgeGroup.Elements(), e => e.Name.LocalName == "line");
            double Number(XElement element, string name) => double.Parse((string)element.Attribute(name)!, System.Globalization.CultureInfo.InvariantCulture);

            // The picture is padded and may be widened, so find the from-node's own top-left in it from its outline.
            XElement nodeGroup = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_from");
            XElement outline = nodeGroup.Elements().First(e => e.Name.LocalName is "ellipse" or "polygon" or "rect");
            (double originX, double originY) = outline.Name.LocalName switch
            {
                "ellipse" => (Number(outline, "cx") - (size / 2), Number(outline, "cy") - (size / 2)),
                "polygon" => (double.Parse(((string)outline.Attribute("points")!).Split(' ')[0].Split(',')[0], System.Globalization.CultureInfo.InvariantCulture) - (size / 2), double.Parse(((string)outline.Attribute("points")!).Split(' ')[0].Split(',')[1], System.Globalization.CultureInfo.InvariantCulture)),
                _ => (Number(outline, "x"), Number(outline, "y")),
            };
            return (Number(line, "x1") - originX, Number(line, "y1") - originY);
        }

        (double cx, double cy) = LineStart("custom:bpmn/start", 40);
        Assert.Equal(20, Math.Sqrt(Math.Pow(cx - 20, 2) + Math.Pow(cy - 20, 2)), 1);
        (double dx, double dy) = LineStart("custom:bpmn/exclusive-gateway", 50);
        Assert.Equal(25, Math.Abs(dx - 25) + Math.Abs(dy - 25), 1);
        // The other side: a plain rectangle stops at the box, well outside the circle.
        (double bx, double by) = LineStart("rounded", 40);
        Assert.True(Math.Sqrt(Math.Pow(bx - 20, 2) + Math.Pow(by - 20, 2)) > 21);
    }

    /// <summary>
    /// Every UML class shape draws as a UML shape (not the fallback rectangle) in the server renderer, and an unknown
    /// <c>custom:uml/*</c> name still falls back. The web canvas runs the matching sweep in <c>umlClass.test.tsx</c>.
    /// </summary>
    [Fact]
    public void Render_draws_every_uml_shape_as_more_than_the_fallback_rectangle()
    {
        string fallback = NodeShapeMarkup("rectangle");
        Assert.Equal(4, UmlShapeLibrary.All.Count);
        foreach (UmlShape shape in UmlShapeLibrary.All)
        {
            string markup = NodeShapeMarkup(UmlShapeLibrary.Prefix + shape.Name);
            Assert.NotEqual(fallback, markup);
            Assert.Contains("shape uml", markup, StringComparison.Ordinal);
        }

        Assert.Equal(fallback, NodeShapeMarkup("custom:uml/not-a-shape"));
    }

    private static XElement UmlGroup(Node node)
    {
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        return Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == node.Id);
    }

    private static Dictionary<string, JsonElement> Props(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    [Fact]
    public void Render_cuts_a_class_into_compartments_and_draws_name_and_members_in_them()
    {
        var node = new Node("n_cls", "class", "Order", "custom:uml/class")
        {
            Geometry = new Geometry(0, 0, 200, 130),
            Props = Props("""{"attributes":["- id: int","+ total: money"],"operations":["+ pay(): void","refund()"]}"""),
        };
        XElement group = UmlGroup(node);

        // The box is drawn at 32,32: the name band ends 28 down and the attributes 46 further on.
        Assert.Contains(group.Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M32,60 H232 M32,106 H232");
        XElement[] texts = group.Elements().Where(e => e.Name.LocalName == "text").ToArray();
        Assert.Equal(["Order", "-", "id: int", "+", "total: money", "+", "pay(): void", "refund()"], texts.Select(t => t.Value).ToArray());
        // The name is centred and bold on the first row; a member starts at the padding, its text after the glyph column.
        Assert.Equal(("132", "46", "bold"), (texts[0].Attribute("x")!.Value, texts[0].Attribute("y")!.Value, texts[0].Attribute("font-weight")!.Value));
        Assert.Equal(("40", "74"), (texts[1].Attribute("x")!.Value, texts[1].Attribute("y")!.Value));
        Assert.Equal(("52", "74"), (texts[2].Attribute("x")!.Value, texts[2].Attribute("y")!.Value));
        // The operations compartment starts 74 down: its first row centres at 32 + 74 + 5 + 9.
        Assert.Equal("120", texts[6].Attribute("y")!.Value);
        // An unglyphed member beside a glyphed one lines up after the column; the name is not one of the members.
        Assert.Equal("52", texts[7].Attribute("x")!.Value);
        Assert.Null(texts[1].Attribute("font-weight"));
        // The class names itself in its markup: no second, plain label.
        Assert.DoesNotContain(texts, t => t.Value == "Order" && t.Attribute("font-weight") is null);
    }

    [Fact]
    public void Render_puts_the_interface_stereotype_above_its_name_and_a_stereotype_prop_over_it()
    {
        var iface = new Node("n_if", "interface", "Repo", "custom:uml/interface")
        {
            Geometry = new Geometry(0, 0, 200, 102),
            Props = Props("""{"operations":["+ find(): Order"]}"""),
        };
        XElement group = UmlGroup(iface);
        Assert.Equal(["\u00abinterface\u00bb", "Repo", "+", "find(): Order"], group.Elements().Where(e => e.Name.LocalName == "text").Select(e => e.Value).ToArray());
        Assert.Contains(group.Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M32,78 H232 M32,106 H232");

        var custom = new Node("n_cls", "class", "Order", "custom:uml/class") { Geometry = new Geometry(0, 0, 200, 102), Props = Props("""{"stereotype":"abstract"}""") };
        Assert.Equal("\u00ababstract\u00bb", UmlGroup(custom).Elements().First(e => e.Name.LocalName == "text").Value);
        // A plain class shows no stereotype row at all.
        var plain = new Node("n_cls", "class", "Order", "custom:uml/class") { Geometry = new Geometry(0, 0, 200, 84) };
        Assert.Equal(["Order"], UmlGroup(plain).Elements().Where(e => e.Name.LocalName == "text").Select(e => e.Value).ToArray());
    }

    [Fact]
    public void Render_gives_extra_height_to_the_operations_compartment_only()
    {
        var props = Props("""{"attributes":["- a"],"operations":["+ b()"]}""");
        XElement tight = UmlGroup(new Node("n_cls", "class", "C", "custom:uml/class") { Geometry = new Geometry(0, 0, 200, 84), Props = props });
        XElement tall = UmlGroup(new Node("n_cls", "class", "C", "custom:uml/class") { Geometry = new Geometry(0, 0, 200, 300), Props = props });

        Assert.Contains(tight.Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M32,60 H232 M32,88 H232");
        Assert.Contains(tall.Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M32,60 H232 M32,88 H232");
        Assert.Equal("300", Assert.Single(tall.Elements(), e => e.Name.LocalName == "rect" && (string?)e.Attribute("class") == "shape uml").Attribute("height")!.Value);
    }

    [Fact]
    public void Render_draws_a_lollipop_as_a_ball_on_a_stem_and_a_socket_as_an_arc_with_the_label_under_it()
    {
        var lollipop = new Node("n_lp", "provided-interface", "IOrder", "custom:uml/lollipop") { Geometry = new Geometry(0, 0, 72, 48) };
        XElement group = UmlGroup(lollipop);
        // The symbol fills the top 24 units: the stem runs at y = 32 + 12 to the ball, whose radius is 9.6.
        Assert.Contains(group.Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M32,44 H84.8");
        XElement ball = Assert.Single(group.Elements(), e => e.Name.LocalName == "circle");
        Assert.Equal(("94.4", "44", "9.6"), (ball.Attribute("cx")!.Value, ball.Attribute("cy")!.Value, ball.Attribute("r")!.Value));
        XElement label = Assert.Single(group.Elements(), e => e.Name.LocalName == "text");
        Assert.Equal(("IOrder", "68", "68"), (label.Value, label.Attribute("x")!.Value, label.Attribute("y")!.Value));

        var socket = new Node("n_sk", "required-interface", "IOrder", "custom:uml/socket") { Geometry = new Geometry(0, 0, 72, 48) };
        XElement socketGroup = UmlGroup(socket);
        Assert.Contains(socketGroup.Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M32,44 H94.4");
        Assert.Contains(socketGroup.Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M104,34.4 A9.6,9.6 0 0 0 104,53.6");
        Assert.DoesNotContain(socketGroup.Elements(), e => e.Name.LocalName == "circle");
    }

    /// <summary>
    /// Every ER entity shape draws as an ER shape (not the fallback rectangle) in the server renderer, and an unknown
    /// <c>custom:er/*</c> name still falls back. The web canvas runs the matching sweep in <c>erEntity.test.tsx</c>.
    /// </summary>
    [Fact]
    public void Render_draws_every_er_shape_as_more_than_the_fallback_rectangle()
    {
        string fallback = NodeShapeMarkup("rectangle");
        Assert.Equal(2, ErShapeLibrary.All.Count);
        foreach (ErShape shape in ErShapeLibrary.All)
        {
            string markup = NodeShapeMarkup(ErShapeLibrary.Prefix + shape.Name);
            Assert.NotEqual(fallback, markup);
            Assert.Contains("shape er", markup, StringComparison.Ordinal);
        }

        Assert.Equal(fallback, NodeShapeMarkup("custom:er/not-a-shape"));
    }

    private static readonly string[] s_entityColumns = ["PK Id int not null", "Name nvarchar(100) not null", "Notes nvarchar(max) null"];

    private static Node Entity(string shape, string[] columns, double h)
    {
        return new Node("n_ent", "entity", "TableName", shape)
        {
            Geometry = new Geometry(0, 0, 264, h),
            Props = Props(JsonSerializer.Serialize(new { columns })),
        };
    }

    [Fact]
    public void Render_draws_an_entity_as_a_name_band_over_aligned_left_anchored_column_cells()
    {
        XElement group = UmlGroup(Entity("custom:er/entity", s_entityColumns, 92));
        XElement[] texts = group.Elements().Where(e => e.Name.LocalName == "text").ToArray();

        Assert.Equal(["TableName", "PK", "Id", "int", "NOT NULL", "Name", "nvarchar(100)", "NOT NULL", "Notes", "nvarchar(max)", "NULL"], texts.Select(t => t.Value).ToArray());
        // The box is drawn at 32,32. The name is centred and bold on the first row; every row cell is start-anchored.
        Assert.Equal(("164", "46", "middle", "bold"), (texts[0].Attribute("x")!.Value, texts[0].Attribute("y")!.Value, texts[0].Attribute("text-anchor")!.Value, texts[0].Attribute("font-weight")!.Value));
        Assert.All(texts.Skip(1), t => Assert.Equal("start", t.Attribute("text-anchor")!.Value));
        // One x per column, however many rows: key 8, name 36.5, type 86, nullability 195.5 (plus the 32 the box sits at).
        string XOf(string value) => texts.First(t => t.Value == value).Attribute("x")!.Value;
        Assert.Equal(["40"], new[] { XOf("PK") });
        Assert.Equal(new[] { "68.5" }, texts.Where(t => t.Value is "Id" or "Name" or "Notes").Select(t => t.Attribute("x")!.Value).Distinct().ToArray());
        Assert.Equal(new[] { "118" }, texts.Where(t => t.Value is "int" or "nvarchar(100)" or "nvarchar(max)").Select(t => t.Attribute("x")!.Value).Distinct().ToArray());
        Assert.Equal(new[] { "227.5" }, texts.Where(t => t.Value.EndsWith("NULL", StringComparison.Ordinal)).Select(t => t.Attribute("x")!.Value).Distinct().ToArray());
        // Rows step one row height down from the middle of the first row (32 + 28 + 5 + 9).
        Assert.Equal(["74", "92", "110"], texts.Where(t => t.Value is "Id" or "Name" or "Notes").Select(t => t.Attribute("y")!.Value).ToArray());
        // The entity names itself in its own markup: no second, plain label.
        Assert.DoesNotContain(texts, t => t.Value == "TableName" && t.Attribute("font-weight") is null);
    }

    [Fact]
    public void Render_rules_off_the_name_band_and_the_leading_primary_key_rows_only()
    {
        Assert.Contains(UmlGroup(Entity("custom:er/entity", s_entityColumns, 92)).Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M32,60 H296 M32,83 H296");
        Assert.Contains(UmlGroup(Entity("custom:er/entity", ["A int", "B int"], 92)).Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M32,60 H296");
        Assert.Contains(UmlGroup(Entity("custom:er/entity", ["PK A int", "PK B int"], 92)).Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M32,60 H296");
    }

    [Fact]
    public void Render_gives_a_weak_entity_an_inner_border_that_a_plain_entity_lacks()
    {
        XElement weak = UmlGroup(Entity("custom:er/weak-entity", s_entityColumns, 92));
        XElement inner = Assert.Single(weak.Elements(), e => e.Name.LocalName == "rect" && (string?)e.Attribute("class") == "shape-detail");
        Assert.Equal(("35", "35", "258", "86"), (inner.Attribute("x")!.Value, inner.Attribute("y")!.Value, inner.Attribute("width")!.Value, inner.Attribute("height")!.Value));
        Assert.Contains(weak.Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M35,60 H293 M35,83 H293");

        XElement plain = UmlGroup(Entity("custom:er/entity", s_entityColumns, 92));
        Assert.DoesNotContain(plain.Elements(), e => e.Name.LocalName == "rect" && (string?)e.Attribute("class") == "shape-detail");
    }

    [Fact]
    public void Render_gives_extra_entity_height_to_the_bottom_only()
    {
        XElement tight = UmlGroup(Entity("custom:er/entity", s_entityColumns, 92));
        XElement tall = UmlGroup(Entity("custom:er/entity", s_entityColumns, 300));

        Assert.Contains(tall.Elements(), e => e.Name.LocalName == "path" && (string?)e.Attribute("d") == "M32,60 H296 M32,83 H296");
        Assert.Equal("300", Assert.Single(tall.Elements(), e => e.Name.LocalName == "rect" && (string?)e.Attribute("class") == "shape er").Attribute("height")!.Value);
        Assert.Equal("92", Assert.Single(tight.Elements(), e => e.Name.LocalName == "rect" && (string?)e.Attribute("class") == "shape er").Attribute("height")!.Value);
    }

    private static IEnumerable<string> SchemaShapeAlternatives()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "document.schema.json")));
        string pattern = schema.RootElement
            .GetProperty("$defs").GetProperty("node").GetProperty("properties").GetProperty("shape").GetProperty("pattern")
            .GetString()!;
        string body = pattern.TrimStart('^');
        body = body.EndsWith("\\z", StringComparison.Ordinal) ? body[..^2] : body.TrimEnd('$');
        return body.Trim('(', ')').Split('|');
    }

    private static string NodeShapeMarkup(string shape)
    {
        var node = new Node("n_shape", "process", "Label", shape) { Geometry = new Geometry(0, 0, 200, 100) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_shape");
        return string.Concat(group.Elements().Select(e => e.ToString(SaveOptions.DisableFormatting)));
    }

    [Theory]
    [InlineData("document", 0.5, 0.85)]
    [InlineData("document", 0.2265625, 0.934375)]
    [InlineData("document", 0.7734375, 0.765625)]
    [InlineData("document", 1, 0.5)]
    [InlineData("manual-input", 0.5, 0.125)]
    [InlineData("manual-input", 0.2, 0.2)]
    [InlineData("off-page-connector", 0.75, 0.825)]
    [InlineData("off-page-connector", 0.5, 1)]
    [InlineData("predefined-process", 1, 0.75)]
    [InlineData("rectangle", 1, 0.75)]
    public void Render_clips_unnamed_endpoints_to_flowchart_outlines(string shape, double x, double y)
    {
        foreach (double rotation in new double[] { 0, 37, 90 })
        {
            foreach (bool reverse in new[] { false, true })
            {
                foreach (bool waypoint in new[] { false, true })
                {
                    foreach (bool declared in new[] { false, true })
                    {
                        double angle = rotation * Math.PI / 180;
                        Point World(double px, double py) => new(
                            110 + (px - 0.5) * 200 * Math.Cos(angle) - (py - 0.5) * 100 * Math.Sin(angle),
                            70 + (px - 0.5) * 200 * Math.Sin(angle) + (py - 0.5) * 100 * Math.Cos(angle));
                        Point expected = World(declared ? 0.2 : x, declared ? 0.3 : y);
                        Point aim = World(0.5 + (x - 0.5) * 5, 0.5 + (y - 0.5) * 5);
                        var a = new Node("a", "process", "", shape)
                        {
                            Geometry = new Geometry(10, 20, 200, 100) { Rotation = rotation },
                            Ports = [new Port("p", 0.2, 0.3)],
                        };
                        var b = new Node("b", "process", "", "rectangle") { Geometry = new Geometry(aim.X - 10, aim.Y - 10, 20, 20) };
                        var frame = new Node("frame", "process", "", "rectangle") { Geometry = new Geometry(-2000, -2000, 4000, 4000) };
                        var endpoint = new Endpoint("a") { Port = declared ? "p" : null };
                        var edge = new Edge("e", "flow", reverse ? new Endpoint("b") : endpoint, reverse ? endpoint : new Endpoint("b"))
                        {
                            Routing = new Routing(RoutingMode.Straight) { Waypoints = waypoint ? [aim] : [] },
                        };
                        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([a, b, frame], [edge])).Svg);
                        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "e");
                        XElement line = Assert.Single(group.Elements());
                        double actualX;
                        double actualY;
                        if (waypoint)
                        {
                            string[] points = line.Attribute("points")!.Value.Split(' ');
                            string[] point = points[reverse ? ^1 : 0].Split(',');
                            actualX = double.Parse(point[0], System.Globalization.CultureInfo.InvariantCulture);
                            actualY = double.Parse(point[1], System.Globalization.CultureInfo.InvariantCulture);
                        }
                        else
                        {
                            actualX = Number(line, reverse ? "x2" : "x1");
                            actualY = Number(line, reverse ? "y2" : "y1");
                        }
                        Assert.Equal(expected.X + 2032, actualX, 7);
                        Assert.Equal(expected.Y + 2032, actualY, 7);
                    }
                }
            }
        }
    }

    [Fact]
    public void Render_attaches_to_declared_outline_ports_instead_of_the_box()
    {
        var from = new Node("n_doc", "io", "", "document")
        {
            Geometry = new Geometry(0, 0, 200, 100),
            Ports = [new Port("bottom", 0.5, 0.85)],
        };
        var to = new Node("n_input", "io", "", "manual-input")
        {
            Geometry = new Geometry(0, 200, 200, 100),
            Ports = [new Port("top", 0.5, 0.125)],
        };
        var edge = new Edge("e", "flow", new Endpoint("n_doc") { Port = "bottom" }, new Endpoint("n_input") { Port = "top" });
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([from, to], [edge])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "e");
        XElement line = Assert.Single(group.Elements(), e => e.Name.LocalName == "line");
        Assert.Equal("117", (string?)line.Attribute("y1"));
        Assert.Equal("244.5", (string?)line.Attribute("y2"));
    }

    [Fact]
    public void Render_throws_when_a_node_has_no_geometry()
    {
        var document = Document([new Node("n_a", "process", "A", "rectangle")], []);
        Assert.Throws<InvalidOperationException>(() => SvgRenderer.Render(document));
    }

    [Fact]
    public void Render_draws_every_node_and_edge()
    {
        var a = new Node("n_a", "process", "Start", "rounded") { Geometry = new Geometry(0, 0, 120, 60) };
        var b = new Node("n_b", "process", "End", "diamond") { Geometry = new Geometry(200, 0, 120, 60) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b")) { Label = "next" };
        var document = Document([a, b], [edge]);

        string svg = SvgRenderer.Render(document).Svg;

        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("data-id=\"n_a\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-id=\"n_b\"", svg, StringComparison.Ordinal);
        Assert.Contains("data-id=\"e_1\"", svg, StringComparison.Ordinal);
        Assert.Contains("Start", svg, StringComparison.Ordinal);
        Assert.Contains("next", svg, StringComparison.Ordinal);
        Assert.Contains("<polygon", svg, StringComparison.Ordinal); // the diamond
    }

    [Fact]
    public void Render_draws_an_opaque_plate_behind_an_edge_label_so_the_line_never_runs_through_the_text()
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 100, 50) };
        var b = a with { Id = "n_b", Geometry = new Geometry(300, 0, 100, 50) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b")) { Label = "handles" };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([a, b], [edge])).Svg);

        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "e_1");
        XElement[] children = group.Elements().ToArray();
        XElement rect = Assert.Single(children, e => e.Name.LocalName == "rect");
        XElement text = Assert.Single(children, e => e.Name.LocalName == "text");

        // The plate is opaque (never "none" or a transparent colour) and drawn before the text so it sits under it.
        string fill = rect.Attribute("fill")!.Value;
        Assert.NotEqual("none", fill, StringComparer.OrdinalIgnoreCase);
        Assert.True(Array.IndexOf(children, rect) < Array.IndexOf(children, text));

        // The plate covers the text's anchor point with a little room either side (the "small padding" the brief asks for).
        double textX = Number(text, "x");
        double textY = Number(text, "y");
        double rectX = Number(rect, "x");
        double rectY = Number(rect, "y");
        double rectW = Number(rect, "width");
        double rectH = Number(rect, "height");
        Assert.InRange(textX, rectX + 1, rectX + rectW - 1);
        Assert.InRange(textY, rectY + 1, rectY + rectH - 1);
    }

    [Fact]
    public void Render_nudges_a_later_labels_box_clear_of_an_earlier_one_on_a_different_connector()
    {
        // Two horizontal connectors stacked close enough that same-length midpoint labels would overlap.
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 80, 40) };
        var b = a with { Id = "n_b", Geometry = new Geometry(400, 0, 80, 40) };
        var c = a with { Id = "n_c", Geometry = new Geometry(0, 12, 80, 40) };
        var d = a with { Id = "n_d", Geometry = new Geometry(400, 12, 80, 40) };
        var first = new Edge("e_first", "flow", new Endpoint("n_a"), new Endpoint("n_b")) { Label = "process-order" };
        var second = new Edge("e_second", "flow", new Endpoint("n_c"), new Endpoint("n_d")) { Label = "process-order" };

        (double X, double Y, double W, double H) BoxOf(XDocument xml, string edgeId)
        {
            XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == edgeId);
            XElement rect = Assert.Single(group.Elements(), e => e.Name.LocalName == "rect");
            return (Number(rect, "x"), Number(rect, "y"), Number(rect, "width"), Number(rect, "height"));
        }

        static bool Overlaps((double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b) =>
            a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y;

        XDocument listedFirst = XDocument.Parse(SvgRenderer.Render(Document([a, b, c, d], [first, second])).Svg);
        (double X, double Y, double W, double H) firstBoxBase = BoxOf(listedFirst, "e_first");
        (double X, double Y, double W, double H) secondBoxNudged = BoxOf(listedFirst, "e_second");
        Assert.False(Overlaps(firstBoxBase, secondBoxNudged)); // the required property: the two plates no longer overlap

        // Listing the edges the other way round proves it is document order, not anything about the edge itself,
        // that decides which one stayed put: now e_second is first, and its plate is the one that never moves.
        XDocument secondListedFirst = XDocument.Parse(SvgRenderer.Render(Document([a, b, c, d], [second, first])).Svg);
        (double X, double Y, double W, double H) secondBoxBase = BoxOf(secondListedFirst, "e_second");
        (double X, double Y, double W, double H) firstBoxNudged = BoxOf(secondListedFirst, "e_first");
        Assert.False(Overlaps(secondBoxBase, firstBoxNudged));

        // Whichever edge is *not* listed first actually moved (this is the assertion that can fail: comment it out
        // and swap in `Assert.Equal` to see both directions break the same way `placeLabels`'s web-side test does).
        Assert.NotEqual(firstBoxBase.X, firstBoxNudged.X, 6);
        Assert.NotEqual(secondBoxNudged.X, secondBoxBase.X, 6);
    }

    [Fact]
    public void Render_nudges_a_labels_box_clear_of_a_node_that_is_not_its_own_endpoint()
    {
        // "start" and "to" sit either side of "blocker", which straddles the connector's midpoint (M2 final Finding C).
        var start = new Node("from", "process", "From", "rectangle") { Geometry = new Geometry(0, 0, 40, 20) };
        var to = start with { Id = "to", Geometry = new Geometry(400, 0, 40, 20) };
        var blocker = start with { Id = "blocker", Label = "", Geometry = new Geometry(150, -20, 140, 60) };
        var edge = new Edge("e_1", "flow", new Endpoint("from"), new Endpoint("to")) { Label = "process-order" };

        static bool Overlaps((double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b) =>
            a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y;
        (double X, double Y, double W, double H) BoxOf(XDocument xml, string dataId)
        {
            XElement element = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == dataId);
            XElement rect = Assert.Single(element.Elements(), e => e.Name.LocalName == "rect");
            return (Number(rect, "x"), Number(rect, "y"), Number(rect, "width"), Number(rect, "height"));
        }

        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([start, to, blocker], [edge])).Svg);
        (double X, double Y, double W, double H) labelBox = BoxOf(xml, "e_1");
        (double X, double Y, double W, double H) blockerBox = BoxOf(xml, "blocker");
        Assert.False(Overlaps(labelBox, blockerBox)); // the required property: the label clears the third node's box
    }

    [Fact]
    public void Render_never_nudges_a_label_away_from_its_own_connectors_endpoint_node()
    {
        // "to" straddles the label's default midpoint; it must never be treated as an obstacle for its own edge, so
        // the label is left sitting over it rather than nudged clear (the opposite of the previous test's "blocker").
        var start = new Node("from", "process", "From", "rectangle") { Geometry = new Geometry(0, 0, 40, 20) };
        var to = start with { Id = "to", Geometry = new Geometry(60, -10, 40, 20) };
        var edge = new Edge("e_1", "flow", new Endpoint("from"), new Endpoint("to")) { Label = "process-order" };

        static bool Overlaps((double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b) =>
            a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y;
        (double X, double Y, double W, double H) BoxOf(XDocument xml, string dataId)
        {
            XElement element = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == dataId);
            XElement rect = Assert.Single(element.Elements(), e => e.Name.LocalName == "rect");
            return (Number(rect, "x"), Number(rect, "y"), Number(rect, "width"), Number(rect, "height"));
        }

        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([start, to], [edge])).Svg);
        Assert.True(Overlaps(BoxOf(xml, "e_1"), BoxOf(xml, "to"))); // the required property: its own endpoint never counts as an obstacle
    }

    [Fact]
    public void Render_nudges_a_label_off_the_border_of_its_own_endpoint_node()
    {
        // A pinned connector whose long middle run skims just above the top edge of "to": its plate at the midpoint
        // would straddle that outline (QA: an FK label on a table's header line).
        var start = new Node("from", "process", "From", "rectangle") { Geometry = new Geometry(0, 0, 40, 20) };
        var to = start with { Id = "to", Geometry = new Geometry(100, 0, 400, 80) };
        var edge = new Edge("e_1", "flow", new Endpoint("from"), new Endpoint("to")) { Label = "customer", Routing = new Routing(RoutingMode.Straight) { Waypoints = [new Point(70, -5), new Point(560, -5)] } };

        static bool Overlaps((double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b) =>
            a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y;
        (double X, double Y, double W, double H) BoxOf(XDocument xml, string dataId)
        {
            XElement element = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == dataId);
            XElement rect = Assert.Single(element.Elements(), e => e.Name.LocalName == "rect");
            return (Number(rect, "x"), Number(rect, "y"), Number(rect, "width"), Number(rect, "height"));
        }

        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([start, to], [edge])).Svg);
        (double X, double Y, double W, double H) plate = BoxOf(xml, "e_1");
        (double X, double Y, double W, double H) table = BoxOf(xml, "to");
        // Either wholly outside the table or wholly inside it, never across its border.
        bool inside = plate.X >= table.X && plate.Y >= table.Y && plate.X + plate.W <= table.X + table.W && plate.Y + plate.H <= table.Y + table.H;
        Assert.True(inside || !Overlaps(plate, table));
    }

    [Fact]
    public void Render_omits_an_edge_with_a_missing_endpoint_and_says_so()
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 100, 50) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_ghost"));

        SvgRenderResult rendered = SvgRenderer.Render(Document([a], [edge]));

        Assert.DoesNotContain("data-id=\"e_1\"", rendered.Svg, StringComparison.Ordinal);
        string warning = Assert.Single(rendered.Warnings);
        Assert.Contains("e_1", warning, StringComparison.Ordinal);
        Assert.Contains("n_ghost", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("n_a", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_warns_once_naming_both_missing_endpoints()
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 100, 50) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_gone"), new Endpoint("n_ghost"));

        string warning = Assert.Single(SvgRenderer.Render(Document([a], [edge])).Warnings);

        Assert.Contains("n_gone", warning, StringComparison.Ordinal);
        Assert.Contains("n_ghost", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_reports_no_warnings_for_an_ordinary_document()
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 120, 60) };
        var b = a with { Id = "n_b", Geometry = new Geometry(200, 0, 120, 60) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b"));

        Assert.Empty(SvgRenderer.Render(Document([a, b], [edge])).Warnings);
    }

    [Fact]
    public void Render_draws_a_curved_edge_as_a_smooth_path_through_its_waypoints()
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 100, 60) };
        var b = a with { Id = "n_b", Geometry = new Geometry(400, 0, 100, 60) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b"))
        {
            Routing = new Routing(RoutingMode.Curved) { Waypoints = [new Point(250, -40), new Point(400, 20)] },
        };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([a, b], [edge])).Svg);

        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "e_1");
        XElement path = Assert.Single(group.Elements(), e => e.Name.LocalName == "path");
        Assert.DoesNotContain(group.Elements(), e => e.Name.LocalName == "polyline");
        Assert.Equal("none", (string?)path.Attribute("fill"));
        // A curve has at least one C segment, so the smooth spline is not a plain polyline.
        string d = path.Attribute("d")!.Value;
        Assert.Contains(" C ", d, StringComparison.Ordinal);
        Assert.StartsWith("M ", d, StringComparison.Ordinal);
        // The arrowhead stays on the end, oriented by the marker's orient, not dropped by the curve.
        Assert.Equal("url(#arrow-arrow)", (string?)path.Attribute("marker-end"));
    }

    [Fact]
    public void Render_follows_the_waypoints_elk_routed_the_edge_through()
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 100, 60) };
        var b = a with { Id = "n_b", Geometry = new Geometry(400, 0, 100, 60) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b"))
        {
            Routing = new Routing(RoutingMode.Orthogonal) { Waypoints = [new Point(250, 30), new Point(250, 200), new Point(450, 200)] },
        };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([a, b], [edge])).Svg);

        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "e_1");
        XElement polyline = Assert.Single(group.Elements(), e => e.Name.LocalName == "polyline");
        Assert.DoesNotContain(group.Elements(), e => e.Name.LocalName == "line");
        Assert.Equal("none", (string?)polyline.Attribute("fill"));

        (double X, double Y)[] points = Points(polyline);
        Assert.Equal(5, points.Length);

        // n_a is drawn at model (0,0), so where its rectangle landed is the shift from model to viewBox coordinates.
        (double X, double Y) shift = DrawnOrigin(xml, "n_a");
        Assert.Equal((250 + shift.X, 30 + shift.Y), points[1]);
        Assert.Equal((250 + shift.X, 200 + shift.Y), points[2]);
        Assert.Equal((450 + shift.X, 200 + shift.Y), points[3]);

        // Both ends still clip to their node's boundary, aimed at the nearest waypoint rather than the far centre.
        Assert.Equal((100 + shift.X, 30 + shift.Y), points[0]);
        Assert.Equal((450 + shift.X, 60 + shift.Y), points[^1]);

        foreach ((double x, double y) in points)
        {
            Assert.InRange(x, 0, Number(xml.Root!, "width"));
            Assert.InRange(y, 0, Number(xml.Root!, "height"));
        }
    }

    [Fact]
    public void Render_puts_a_bent_edges_label_on_the_route()
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 100, 60) };
        var b = a with { Id = "n_b", Geometry = new Geometry(400, 0, 100, 60) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b"))
        {
            Label = "retry",
            Routing = new Routing(RoutingMode.Orthogonal) { Waypoints = [new Point(250, 30), new Point(250, 200), new Point(450, 200)] },
        };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([a, b], [edge])).Svg);

        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "e_1");
        XElement text = Assert.Single(group.Elements(), e => e.Name.LocalName == "text");
        (double X, double Y)[] points = Points(Assert.Single(group.Elements(), e => e.Name.LocalName == "polyline"));

        Assert.Equal(0, DistanceToRoute((Number(text, "x"), Number(text, "y")), points), 6);

        // Halving the straight source-to-target line - what the renderer used to do - would put it off the route.
        (double X, double Y) straight = ((points[0].X + points[^1].X) / 2, (points[0].Y + points[^1].Y) / 2);
        Assert.True(DistanceToRoute(straight, points) > 1);
    }

    [Fact]
    public void Render_puts_a_curved_edges_label_on_the_curve_by_arc_length()
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 100, 60) };
        var b = a with { Id = "n_b", Geometry = new Geometry(400, 0, 100, 60) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b"))
        {
            Label = "retry",
            Routing = new Routing(RoutingMode.Curved) { Waypoints = [new Point(300, -150), new Point(300, 150)] },
        };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([a, b], [edge])).Svg);

        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "e_1");
        XElement text = Assert.Single(group.Elements(), e => e.Name.LocalName == "text");
        string d = Assert.Single(group.Elements(), e => e.Name.LocalName == "path").Attribute("d")!.Value;

        // The label sits on the drawn spline.
        Assert.True(DistanceToCurve(d, (Number(text, "x"), Number(text, "y"))) < 1);

        // The polyline midpoint through the same anchors - what a straight-route label would use - is measurably off it.
        Assert.True(DistanceToCurve(d, MidpointOfAnchors(d)) > 1);
    }

    /// <summary>The polyline midpoint by length through the curve's anchors (the `M` and each `C`'s end point).</summary>
    private static (double X, double Y) MidpointOfAnchors(string d)
    {
        var anchors = new List<(double X, double Y)>();
        string[] tokens = d.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is "M" or "L")
            {
                anchors.Add(Pair(tokens[i + 1]));
                i++;
            }
            else if (tokens[i] == "C")
            {
                anchors.Add(Pair(tokens[i + 3]));
                i += 3;
            }
        }

        double total = 0;
        for (int i = 1; i < anchors.Count; i++)
        {
            total += Length(anchors[i - 1], anchors[i]);
        }

        double remaining = total / 2;
        for (int i = 1; i < anchors.Count; i++)
        {
            double segment = Length(anchors[i - 1], anchors[i]);
            if (segment >= remaining)
            {
                double t = segment == 0 ? 0 : remaining / segment;
                return (anchors[i - 1].X + (anchors[i].X - anchors[i - 1].X) * t, anchors[i - 1].Y + (anchors[i].Y - anchors[i - 1].Y) * t);
            }

            remaining -= segment;
        }

        return anchors.Count == 0 ? (0, 0) : anchors[^1];
    }

    /// <summary>The nearest distance from `point` to the curve drawn by `d`, sampling every cubic finely.</summary>
    private static double DistanceToCurve(string d, (double X, double Y) point)
    {
        var anchors = new List<(double X, double Y)>();
        var cubics = new List<((double X, double Y) P0, (double X, double Y) C1, (double X, double Y) C2, (double X, double Y) P3)>();
        string[] tokens = d.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is "M" or "L")
            {
                anchors.Add(Pair(tokens[i + 1]));
                i++;
            }
            else if (tokens[i] == "C")
            {
                (double X, double Y) p0 = anchors[^1];
                (double X, double Y) c1 = Pair(tokens[i + 1]);
                (double X, double Y) c2 = Pair(tokens[i + 2]);
                (double X, double Y) p3 = Pair(tokens[i + 3]);
                cubics.Add((p0, c1, c2, p3));
                anchors.Add(p3);
                i += 3;
            }
        }

        double nearest = double.PositiveInfinity;
        foreach (((double X, double Y) p0, (double X, double Y) c1, (double X, double Y) c2, (double X, double Y) p3) in cubics)
        {
            for (int i = 0; i <= 400; i++)
            {
                double t = (double)i / 400;
                double mt = 1 - t;
                double a = mt * mt * mt;
                double b = 3 * mt * mt * t;
                double c = 3 * mt * t * t;
                double e = t * t * t;
                var at = (a * p0.X + b * c1.X + c * c2.X + e * p3.X, a * p0.Y + b * c1.Y + c * c2.Y + e * p3.Y);
                nearest = Math.Min(nearest, Length(point, at));
            }
        }

        return nearest;
    }

    private static (double X, double Y) Pair(string token)
    {
        string[] parts = token.Split(',');
        return (double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static double Length((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    private static readonly Point[] DiagonalRoute = [new(100, 30), new(250, 30), new(250, 330), new(400, 330)];

    private static (XDocument Xml, XElement Group) RenderRoutedEdge(RoutingMode mode, IReadOnlyDictionary<string, IReadOnlyList<Point>>? routes)
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 100, 60) };
        var b = a with { Id = "n_b", Geometry = new Geometry(400, 300, 100, 60) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b")) { Routing = new Routing(mode) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([a, b], [edge]), 1, null, routes).Svg);
        return (xml, Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "e_1"));
    }

    [Fact]
    public void Render_draws_an_orthogonal_edge_along_the_route_it_is_given()
    {
        var routes = new Dictionary<string, IReadOnlyList<Point>> { ["e_1"] = DiagonalRoute };
        (XDocument xml, XElement group) = RenderRoutedEdge(RoutingMode.Orthogonal, routes);

        XElement polyline = Assert.Single(group.Elements(), e => e.Name.LocalName == "polyline");
        (double X, double Y)[] points = Points(polyline);
        (double X, double Y) shift = DrawnOrigin(xml, "n_a");
        Assert.Equal(DiagonalRoute.Select(p => (p.X + shift.X, p.Y + shift.Y)), points);
        for (int i = 1; i < points.Length; i++)
        {
            Assert.True(points[i].X == points[i - 1].X || points[i].Y == points[i - 1].Y, $"segment {i} is not axis-aligned");
        }
    }

    [Fact]
    public void Render_draws_a_straight_edge_straight_even_when_a_route_exists_for_its_id()
    {
        var routes = new Dictionary<string, IReadOnlyList<Point>> { ["e_1"] = DiagonalRoute };
        (_, XElement group) = RenderRoutedEdge(RoutingMode.Straight, routes);

        XElement line = Assert.Single(group.Elements(), e => e.Name.LocalName == "line");
        Assert.NotEqual((string?)line.Attribute("x1"), (string?)line.Attribute("x2"));
        Assert.NotEqual((string?)line.Attribute("y1"), (string?)line.Attribute("y2"));
        Assert.DoesNotContain(group.Elements(), e => e.Name.LocalName == "polyline");
    }

    [Fact]
    public void Render_grows_the_picture_to_hold_a_route_that_leaves_the_nodes_box()
    {
        Point[] detour = [new(100, 30), new(700, 30), new(700, 330), new(400, 330)];
        var routes = new Dictionary<string, IReadOnlyList<Point>> { ["e_1"] = detour };
        (XDocument xml, XElement group) = RenderRoutedEdge(RoutingMode.Orthogonal, routes);

        foreach ((double x, double y) in Points(Assert.Single(group.Elements(), e => e.Name.LocalName == "polyline")))
        {
            Assert.InRange(x, 0, Number(xml.Root!, "width"));
            Assert.InRange(y, 0, Number(xml.Root!, "height"));
        }
    }

    [Fact]
    public void Render_puts_a_containers_label_top_left_in_its_header_band_clear_of_its_rows()
    {
        var container = new Node("c_users", "container", "Users", "rectangle") { Geometry = new Geometry(400, 100, 300, 120) };
        var firstRow = new Node("n_id", "rectangle", "PK  Id  int", "text") { Parent = "c_users", Geometry = new Geometry(408, 134, 284, 22) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([container, firstRow], [])).Svg);
        (double X, double Y) shift = DrawnOrigin(xml, "c_users");
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "c_users");
        XElement text = Assert.Single(group.Elements(), e => e.Name.LocalName == "text");

        // Anchored at its start, inside the header band (above the first row's top edge), not centred in the box.
        Assert.Equal("start", (string?)text.Attribute("text-anchor"));
        Assert.InRange(Number(text, "x"), shift.X, shift.X + 20);
        double labelY = Number(text, "y");
        double firstRowTop = shift.Y + (134 - 100);
        Assert.InRange(labelY, shift.Y, firstRowTop);
        Assert.True(labelY + 13 / 2.0 <= firstRowTop, "the label's text must end above the first child row");
    }

    [Fact]
    public void Render_puts_the_label_of_a_node_that_holds_children_in_the_header_even_when_it_is_not_a_container_kind()
    {
        var parent = new Node("n_p", "process", "Parent", "rectangle") { Geometry = new Geometry(0, 0, 300, 120) };
        var child = new Node("n_c", "process", "Child", "rectangle") { Parent = "n_p", Geometry = new Geometry(20, 50, 100, 40) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([parent, child], [])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_p");
        Assert.Equal("start", (string?)Assert.Single(group.Elements(), e => e.Name.LocalName == "text").Attribute("text-anchor"));
    }

    /// <summary>Where a node whose model position is (0,0) was drawn: the shift from model to viewBox coordinates.</summary>
    private static Node Table(string id, double x, double y, params string[] columns)
    {
        return new Node(id, "entity", id, "custom:er/entity")
        {
            Geometry = new Geometry(x, y, 270, 92),
            Props = new Dictionary<string, JsonElement> { ["columns"] = JsonSerializer.SerializeToElement(columns) },
        };
    }

    private static readonly Node s_orderTable = Table("t_order", 0, 0, "PK Id int not null", "FK CustomerId int not null", "Placed datetime2 not null");
    private static readonly Node s_customerTable = Table("t_customer", 500, 200, "PK Id int not null", "Name nvarchar(100) not null", "Email nvarchar(100) null");

    /// <summary>Where a table's outer box was drawn: the shift from model to viewBox coordinates.</summary>
    private static (double X, double Y) TableOrigin(XDocument xml, string nodeId)
    {
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == nodeId);
        XElement rect = group.Elements().First(e => e.Name.LocalName == "rect" && (string?)e.Attribute("class") == "shape er");
        return (Number(rect, "x"), Number(rect, "y"));
    }

    private static (double X, double Y)[] EdgePoints(XDocument xml, string edgeId)
    {
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == edgeId);
        XElement line = Assert.Single(group.Elements(), e => e.Name.LocalName is "polyline" or "line");
        if (line.Name.LocalName == "polyline")
        {
            return Points(line);
        }

        return [(Number(line, "x1"), Number(line, "y1")), (Number(line, "x2"), Number(line, "y2"))];
    }

    private static Edge ColumnEdge(string? fromPort, string? toPort, RoutingMode mode = RoutingMode.Straight)
    {
        return new Edge("e_fk", "association", new Endpoint("t_order") { Port = fromPort }, new Endpoint("t_customer") { Port = toPort }) { Routing = new Routing(mode) };
    }

    [Fact]
    public void Render_draws_a_straight_edge_from_the_fk_row_to_the_key_row_on_the_facing_sides()
    {
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([s_orderTable, s_customerTable], [ColumnEdge("col:CustomerId", "col:Id")])).Svg);
        (double X, double Y) shift = TableOrigin(xml, "t_order");

        (double X, double Y)[] points = EdgePoints(xml, "e_fk");

        // Order's CustomerId row is 60 below its top, on its right edge (x = 270); Customer's Id row is 200 + 42, on its left (x = 500).
        Assert.Equal((270 + shift.X, 60 + shift.Y), points[0]);
        Assert.Equal((500 + shift.X, 242 + shift.Y), points[^1]);
    }

    [Fact]
    public void Render_ends_a_straight_column_edge_on_the_same_side_when_the_tables_overlap_horizontally()
    {
        // Mirrors columnConnectors.test.ts: a at x 100..370, b below it at x 20..290. Both ends take the right side
        // (equal widths tie to the right) rather than facing sides, which would hook round the tables.
        Node top = Table("t_order", 100, 0, "PK Id int not null", "FK CustomerId int not null", "Placed datetime2 not null");
        Node bottom = Table("t_customer", 20, 260, "PK Id int not null", "Name nvarchar(100) not null", "Email nvarchar(100) null");
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([top, bottom], [ColumnEdge("col:CustomerId", "col:Id")])).Svg);
        (double X, double Y) origin = TableOrigin(xml, "t_order");
        (double X, double Y) shift = (origin.X - 100, origin.Y);

        (double X, double Y)[] points = EdgePoints(xml, "e_fk");

        Assert.Equal((370 + shift.X, 60 + shift.Y), points[0]);
        Assert.Equal((290 + shift.X, 302 + shift.Y), points[^1]);
    }

    [Fact]
    public void Render_takes_the_far_side_when_the_other_table_is_on_the_other_side()
    {
        var edge = new Edge("e_fk", "association", new Endpoint("t_customer") { Port = "col:Name" }, new Endpoint("t_order") { Port = "col:Placed" }) { Routing = new Routing(RoutingMode.Straight) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([s_orderTable, s_customerTable], [edge])).Svg);
        (double X, double Y) shift = TableOrigin(xml, "t_order");

        (double X, double Y)[] points = EdgePoints(xml, "e_fk");

        Assert.Equal((500 + shift.X, 260 + shift.Y), points[0]);
        Assert.Equal((270 + shift.X, 78 + shift.Y), points[^1]);
    }

    [Fact]
    public void Render_attaches_an_end_to_the_table_when_its_column_does_not_exist_and_only_that_end()
    {
        XDocument missing = XDocument.Parse(SvgRenderer.Render(Document([s_orderTable, s_customerTable], [ColumnEdge("col:Nope", "col:Id")])).Svg);
        XDocument plain = XDocument.Parse(SvgRenderer.Render(Document([s_orderTable, s_customerTable], [ColumnEdge(null, "col:Id")])).Svg);
        (double X, double Y) shift = TableOrigin(missing, "t_order");

        (double X, double Y)[] points = EdgePoints(missing, "e_fk");

        // The unresolved end is exactly where an edge with no port at all leaves the table, and not on any row.
        Assert.Equal(EdgePoints(plain, "e_fk")[0], points[0]);
        Assert.NotEqual(60 + shift.Y, points[0].Y);
        Assert.Equal((500 + shift.X, 242 + shift.Y), points[^1]);
    }

    [Fact]
    public void Render_keeps_a_pinned_edges_column_end_on_the_side_its_first_waypoint_is_on()
    {
        Edge pinned = ColumnEdge("col:CustomerId", "col:Id", RoutingMode.Orthogonal) with
        {
            Routing = new Routing(RoutingMode.Orthogonal) { Waypoints = [new Point(-50, 60), new Point(-50, 242)] },
        };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([s_orderTable, s_customerTable], [pinned])).Svg);
        (double X, double Y) shift = TableOrigin(xml, "t_order");

        (double X, double Y)[] points = EdgePoints(xml, "e_fk");

        // The waypoint is left of Order, so the FK row is entered on Order's left edge (x = 0) at the row's y.
        Assert.Equal((0 + shift.X, 60 + shift.Y), points[0]);
    }

    private static (double X, double Y) DrawnOrigin(XDocument xml, string nodeId)
    {
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == nodeId);
        XElement rect = Assert.Single(group.Elements(), e => e.Name.LocalName == "rect");
        return (Number(rect, "x"), Number(rect, "y"));
    }

    private static double DistanceToRoute((double X, double Y) point, (double X, double Y)[] route)
    {
        double nearest = double.MaxValue;
        for (int i = 1; i < route.Length; i++)
        {
            (double X, double Y) a = route[i - 1];
            (double X, double Y) b = route[i];
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double lengthSquared = (dx * dx) + (dy * dy);
            double t = lengthSquared == 0 ? 0 : Math.Clamp((((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared, 0, 1);
            double ox = point.X - (a.X + (dx * t));
            double oy = point.Y - (a.Y + (dy * t));
            nearest = Math.Min(nearest, Math.Sqrt((ox * ox) + (oy * oy)));
        }

        return nearest;
    }

    [Theory]
    [InlineData("#0B1F44", "#F2F2F2")] // dark fill: the theme's own light text already reads, so it stays
    [InlineData("#E5B83A", "#0f172a")] // light fill: the default stays
    [InlineData("#ffffff", "#0f172a")]
    public void Node_text_follows_a_chosen_fill_when_it_names_no_text_colour(string fill, string expectedText)
    {
        // QA-06: dark-on-dark and light-on-light labels are unreadable; mirrors web/src/theme/contrast.ts readableTextOn.
        var node = new Node("n_a", "process", "Label", "rectangle") { Geometry = new Geometry(0, 0, 200, 100), Style = new NodeStyle { Fill = fill } };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement text = Assert.Single(Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_a").Elements(), e => e.Name.LocalName == "text");
        Assert.Equal(expectedText, (string?)text.Attribute("fill"));
    }

    [Fact]
    public void Node_text_colour_the_document_names_is_kept_whatever_the_fill()
    {
        var node = new Node("n_a", "process", "Label", "rectangle") { Geometry = new Geometry(0, 0, 200, 100), Style = new NodeStyle { Fill = "#0B1F44", TextColor = "#112233" } };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement text = Assert.Single(Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_a").Elements(), e => e.Name.LocalName == "text");
        Assert.Equal("#112233", (string?)text.Attribute("fill"));
    }

    [Theory]
    [InlineData("e_yes", 0, 100)]
    [InlineData("e_no", 16, 90)]
    [InlineData("e_maybe", -16, 90)]
    [InlineData("e_bent", 20, 87.5)]
    [InlineData("e_audit", 69.8412698, 43.6507937)]
    public void Decision_flow_edges_start_on_the_diamond_outline(string edgeId, double dx, double dy)
    {
        // QA-01: the same document and the same offsets from the diamond's top vertex as web/src/canvas/decisionOutline.test.ts,
        // so the canvas and this renderer cannot disagree about where a connector leaves a decision.
        Document document = DocumentJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "decision-flow.json")));
        XDocument xml = XDocument.Parse(SvgRenderer.Render(document).Svg);

        XElement diamond = Assert.Single(Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_check").Elements(), e => e.Name.LocalName == "polygon");
        (double X, double Y)[] corners = Points(diamond);
        (double X, double Y) top = corners[0];
        (double X, double Y) centre = (top.X, top.Y + 50);

        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == edgeId);
        // A two-point edge is drawn as a <line>, one with bends as a <polyline>.
        XElement drawn = Assert.Single(group.Elements(), e => e.Name.LocalName is "line" or "polyline");
        (double X, double Y) start = drawn.Name.LocalName == "line"
            ? (double.Parse((string)drawn.Attribute("x1")!, System.Globalization.CultureInfo.InvariantCulture), double.Parse((string)drawn.Attribute("y1")!, System.Globalization.CultureInfo.InvariantCulture))
            : Points(drawn)[0];

        Assert.Equal(top.X + dx, start.X, 4);
        Assert.Equal(top.Y + dy, start.Y, 4);
        Assert.Equal(1, (Math.Abs(start.X - centre.X) / 80) + (Math.Abs(start.Y - centre.Y) / 50), 4);
    }

    private static (double X, double Y)[] Points(XElement polyline)
    {
        return polyline.Attribute("points")!.Value.Split(' ')
            .Select(pair => pair.Split(','))
            .Select(pair => (double.Parse(pair[0], System.Globalization.CultureInfo.InvariantCulture),
                             double.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
    }

    [Theory]
    [InlineData("rectangle")]
    [InlineData("cylinder")]
    public void Render_keeps_style_strings_inside_xml_attributes(string shape)
    {
        const string payload = "red\" onmouseover=\"alert(1)&<>";
        var node = new Node("n_a", "process", "A", shape)
        {
            Geometry = new Geometry(0, 0, 120, 60),
            Style = new NodeStyle { Fill = payload, Stroke = payload, TextColor = payload, Dash = Dash.Dashed },
        };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_a"))
        {
            Style = new EdgeStyle { Stroke = payload, Dash = Dash.Dotted },
        };
        var xml = System.Xml.Linq.XDocument.Parse(SvgRenderer.Render(Document([node], [edge])).Svg);
        Assert.DoesNotContain(xml.Descendants().Attributes(), a => a.Name.LocalName == "onmouseover");
        Assert.True(xml.Descendants().Attributes("fill").Count(a => a.Value == payload) >= 2);
        Assert.True(xml.Descendants().Attributes("stroke").Count(a => a.Value == payload) >= 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_draws_a_visible_curve_for_coincident_endpoints(bool distinctNodes)
    {
        var node = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 120, 60) };
        var other = node with { Id = "n_b" };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint(distinctNodes ? "n_b" : "n_a"));
        var xml = System.Xml.Linq.XDocument.Parse(SvgRenderer.Render(Document(distinctNodes ? [node, other] : [node], [edge])).Svg);
        var group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "e_1");
        var path = Assert.Single(group.Elements());
        Assert.Equal("path", path.Name.LocalName);
        Assert.Equal("M 152 56 C 176 32 176 110 152 86", (string?)path.Attribute("d"));
        Assert.Equal("none", (string?)path.Attribute("fill"));
        Assert.Equal("url(#arrow-arrow)", (string?)path.Attribute("marker-end"));
    }

    [Theory]
    [InlineData("image", Dash.Dashed, "6 4")]
    [InlineData("image", Dash.Dotted, "2 3")]
    [InlineData("custom:lib/item", Dash.Dashed, "6 4")]
    [InlineData("custom:lib/item", Dash.Dotted, "2 3")]
    [InlineData("image", Dash.Solid, null)]
    [InlineData("custom:lib/item", Dash.Solid, null)]
    public void Render_placeholder_shapes_have_one_explicit_dash(string shape, Dash dash, string? expected)
    {
        var node = new Node("n_a", "process", "A", shape)
        {
            Geometry = new Geometry(0, 0, 120, 60),
            Style = new NodeStyle { Dash = dash },
        };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_a");
        XElement rect = Assert.Single(group.Elements(), e => e.Name.LocalName == "rect");
        Assert.Equal(expected, (string?)rect.Attribute("stroke-dasharray"));
    }

    [Theory]
    [InlineData(ArrowHead.Arrow)]
    [InlineData(ArrowHead.Open)]
    public void Render_source_arrow_uses_reversing_marker(ArrowHead arrow)
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 120, 60) };
        var b = a with { Id = "n_b", Geometry = new Geometry(200, 0, 120, 60) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b"))
        {
            Style = new EdgeStyle { ArrowStart = arrow, ArrowEnd = ArrowHead.None },
        };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([a, b], [edge])).Svg);
        XElement line = Assert.Single(xml.Descendants(), e => e.Name.LocalName == "line");
        Assert.Null(line.Attribute("marker-end"));
        string reference = line.Attribute("marker-start")!.Value;
        XElement marker = Assert.Single(xml.Descendants(), e => e.Name.LocalName == "marker" && reference == $"url(#{e.Attribute("id")!.Value})");
        Assert.Equal("auto-start-reverse", (string?)marker.Attribute("orient"));
    }

    [Theory]
    [InlineData(ArrowHead.One)]
    [InlineData(ArrowHead.Many)]
    [InlineData(ArrowHead.ZeroOrOne)]
    [InlineData(ArrowHead.OneOrMany)]
    [InlineData(ArrowHead.ZeroOrMany)]
    [InlineData(ArrowHead.OneAndOnlyOne)]
    [InlineData(ArrowHead.Inheritance)]
    [InlineData(ArrowHead.Aggregation)]
    [InlineData(ArrowHead.Composition)]
    public void Render_notation_end_draws_a_defined_marker_at_either_end(ArrowHead head)
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 120, 60) };
        var b = a with { Id = "n_b", Geometry = new Geometry(200, 0, 120, 60) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b"))
        {
            Style = new EdgeStyle { ArrowStart = head, ArrowEnd = head },
        };
        string text = SvgRenderer.Render(Document([a, b], [edge])).Svg;
        XDocument xml = XDocument.Parse(text);
        // Both ends reference a marker whose id is stamped with the head's name and whose element is defined.
        string markerName = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(head.ToString());
        XElement line = Assert.Single(xml.Descendants(), e => e.Name.LocalName == "line");
        Assert.Contains(markerName, line.Attribute("marker-start")!.Value);
        Assert.Contains(markerName, line.Attribute("marker-end")!.Value);
        Assert.Contains(xml.Descendants().Where(e => e.Name.LocalName == "marker"),
            m => $"arrow-{markerName}" == (string?)m.Attribute("id"));
    }

    [Fact]
    public void Render_rotates_node_and_label_with_unclipped_bounds()
    {
        var node = new Node("n_a", "process", "A", "rectangle")
        {
            Geometry = new Geometry(0, 0, 120, 60) { Rotation = 90 },
        };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_a");
        Assert.Equal("rotate(90 62 92)", (string?)group.Attribute("transform"));
        Assert.Contains(group.Elements(), e => e.Name.LocalName == "rect");
        Assert.Contains(group.Elements(), e => e.Name.LocalName == "text");
        Assert.Equal(124, double.Parse(xml.Root!.Attribute("width")!.Value, System.Globalization.CultureInfo.InvariantCulture), 8);
        Assert.Equal(184, double.Parse(xml.Root.Attribute("height")!.Value, System.Globalization.CultureInfo.InvariantCulture), 8);
    }

    [Fact]
    public void Render_paints_nested_ancestors_before_descendants()
    {
        var outer = new Node("n_outer", "process", "Outer", "rectangle") { Geometry = new Geometry(0, 0, 300, 300) };
        var inner = outer with { Id = "n_inner", Parent = outer.Id, Geometry = new Geometry(20, 20, 200, 200) };
        var child = outer with { Id = "n_child", Parent = inner.Id, Geometry = new Geometry(40, 40, 100, 100) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([inner, outer, child], [])).Svg);
        string[] ids = xml.Root!.Elements().Where(e => e.Attribute("data-id") is not null).Select(e => e.Attribute("data-id")!.Value).ToArray();
        Assert.Equal(new[] { "n_outer", "n_inner", "n_child" }, ids);
    }

    [Fact]
    public void Render_connector_touches_rotated_rectangle()
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 120, 60) { Rotation = 90 } };
        var b = a with { Id = "n_b", Geometry = new Geometry(200, 0, 120, 60) };
        var edge = new Edge("e_1", "flow", new Endpoint(a.Id), new Endpoint(b.Id));
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([a, b], [edge])).Svg);
        XElement line = Assert.Single(xml.Descendants(), e => e.Name.LocalName == "line");
        Assert.Equal(92, Number(line, "x1"), 8);
        Assert.Equal(92, Number(line, "y1"), 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void Render_loop_and_label_fit_export_bounds(double rotation)
    {
        var node = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 120, 60) { Rotation = rotation } };
        var edge = new Edge("e_1", "flow", new Endpoint(node.Id), new Endpoint(node.Id)) { Label = "Retry request" };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [edge])).Svg);
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == edge.Id);
        XElement background = Assert.Single(group.Elements(), e => e.Name.LocalName == "rect");
        Assert.InRange(Number(background, "x"), 0, Number(xml.Root!, "width") - Number(background, "width"));
        Assert.InRange(Number(background, "y"), 0, Number(xml.Root!, "height") - Number(background, "height"));
        XElement path = Assert.Single(group.Elements(), e => e.Name.LocalName == "path");
        double[] points = path.Attribute("d")!.Value.Split(' ').Where(v => v != "M" && v != "C").Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        for (int index = 0; index < points.Length; index += 2)
        {
            Assert.InRange(points[index], 0, Number(xml.Root!, "width"));
            Assert.InRange(points[index + 1], 0, Number(xml.Root!, "height"));
        }

        if (rotation == 90)
        {
            Assert.Equal(30, points[0] - points[6], 8);
            Assert.Equal(points[1], points[7], 8);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(45)]
    public void Render_fits_a_node_label_wider_than_its_node(double rotation)
    {
        var node = new Node("n_a", "process", "Send confirmation email to the customer", "rectangle")
        {
            Geometry = new Geometry(0, 0, 160, 80) { Rotation = rotation },
        };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement text = Assert.Single(xml.Descendants(), e => e.Name.LocalName == "text");

        // The label is centred on the point its group rotates about, so its ends turn with the node.
        double radians = rotation * Math.PI / 180;
        double halfWidth = node.Label.Length * 3.5;
        foreach (int end in new[] { -1, 1 })
        {
            Assert.InRange(Number(text, "x") + end * halfWidth * Math.Cos(radians), 0, Number(xml.Root!, "width"));
            Assert.InRange(Number(text, "y") + end * halfWidth * Math.Sin(radians), 0, Number(xml.Root!, "height"));
        }
    }

    [Fact]
    public void Render_paints_an_arrowhead_in_the_edge_stroke_colour()
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 120, 60) };
        var b = a with { Id = "n_b", Geometry = new Geometry(200, 0, 120, 60) };
        var red = new Edge("e_red", "flow", new Endpoint("n_a"), new Endpoint("n_b")) { Style = new EdgeStyle { Stroke = "#ff0000" } };
        var plain = new Edge("e_plain", "flow", new Endpoint("n_b"), new Endpoint("n_a"));
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([a, b], [red, plain])).Svg);

        Assert.Equal("#ff0000", MarkerPaint(xml, "e_red"));
        // A plain edge takes the diagram theme's colour for its kind, not a neutral default.
        Assert.Equal(DiagramTheme.Default.Edge("flow", null).Stroke, MarkerPaint(xml, "e_plain"));
    }

    [Fact]
    public void Render_emits_well_formed_xml_for_a_label_with_a_control_character()
    {
        string label = "A" + (char)1 + "B" + (char)0 + "C";
        var node = new Node("n_a", "process", label, "rectangle") { Geometry = new Geometry(0, 0, 120, 60) };
        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement text = Assert.Single(xml.Descendants(), e => e.Name.LocalName == "text");
        Assert.Equal("ABC", text.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_rejects_duplicate_ids(bool duplicateNodes)
    {
        var a = new Node("n_a", "process", "A", "rectangle") { Geometry = new Geometry(0, 0, 120, 60) };
        var b = a with { Id = duplicateNodes ? "n_a" : "n_b", Geometry = new Geometry(200, 0, 120, 60) };
        var edge = new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint(b.Id));
        Edge[] edges = duplicateNodes ? [edge] : [edge, edge with { From = new Endpoint("n_b"), To = new Endpoint("n_a") }];

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SvgRenderer.Render(Document([a, b], edges)));
        Assert.Contains(duplicateNodes ? "n_a" : "e_1", ex.Message, StringComparison.Ordinal);
    }

    private static string MarkerPaint(XDocument xml, string edgeId)
    {
        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == edgeId);
        string reference = group.Elements().Single(e => e.Attribute("marker-end") is not null).Attribute("marker-end")!.Value;
        XElement marker = Assert.Single(xml.Descendants(), e => e.Name.LocalName == "marker" && reference == $"url(#{e.Attribute("id")!.Value})");
        return marker.Elements().Single().Attribute("fill")!.Value;
    }

    [Fact]
    public void Render_inlines_an_image_node_as_a_data_uri_when_an_asset_resolver_supplies_it()
    {
        byte[] bytes = [1, 2, 3, 4];
        var node = new Node("n_img", "image", "", "image")
        {
            Geometry = new Geometry(0, 0, 240, 120),
            Props = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["assetId"] = System.Text.Json.JsonDocument.Parse("\"asset_1\"").RootElement,
            },
        };

        XDocument xml = XDocument.Parse(SvgRenderer.Render(Document([node], []), 1, id => id == "asset_1" ? new AssetImage("image/png", bytes) : null).Svg);

        XElement group = Assert.Single(xml.Descendants(), e => (string?)e.Attribute("data-id") == "n_img");
        XElement image = Assert.Single(group.Elements(), e => e.Name.LocalName == "image");
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(bytes), (string?)image.Attribute("href"));

        // Without a resolver (or for an unreadable asset) the node draws its placeholder frame, never a broken image
        // reference, so an offline render still produces a well-formed picture.
        XDocument plain = XDocument.Parse(SvgRenderer.Render(Document([node], [])).Svg);
        XElement plainGroup = Assert.Single(plain.Descendants(), e => (string?)e.Attribute("data-id") == "n_img");
        Assert.DoesNotContain(plainGroup.Elements(), e => e.Name.LocalName == "image");
        Assert.Single(plainGroup.Elements(), e => e.Name.LocalName == "rect");
    }

    private static double Number(XElement element, string attribute) =>
        double.Parse(element.Attribute(attribute)!.Value, System.Globalization.CultureInfo.InvariantCulture);

    private static Document Document(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
    {
        return new Document("doc_1", 1, "T", DocumentType.Flowchart, nodes, edges,
            new Meta(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "user:local"));
    }
}
