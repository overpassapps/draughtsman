using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Visio;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;
using Draughtsman.Tests.Shared;
using static Draughtsman.Tests.Shared.VisioDocBuilder;

namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// The Visio importer on files built the way Visio writes them (tests/Shared/VisioFixtures.cs) and on small packages for each rule: what is
/// carried (text, size, position with Visio's bottom-left origin turned over, rotation, groups, connectors and their line ends), what degrades to a
/// plain box with one aggregated warning, and what is left out and said so. Positions arrive with the file, so none of it is laid out.
/// </summary>
public class VisioImporterTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static ImportResult Import(byte[] file, DocumentType type = DocumentType.Flowchart, int page = 1, string? title = null)
    {
        return VisioImporter.Import(file, new VisioImportOptions { Type = type, Page = page, Title = title, Now = s_now });
    }

    private static ImportResult ImportPage(string shapes, string connects = "", DocumentType type = DocumentType.Flowchart, string sheetExtra = "", double pageHeight = 8)
    {
        return Import(new VisioDocBuilder { PageHeight = pageHeight }.Page("P", shapes, connects, sheetExtra).Build(), type);
    }

    private static Node NodeOf(ImportResult result, string id)
    {
        return result.Document.Nodes.Single(n => n.Id == id);
    }

    private static Edge EdgeOf(ImportResult result, string id)
    {
        return result.Document.Edges.Single(e => e.Id == id);
    }

    private static List<ImportWarning> Warnings(ImportResult result, string code)
    {
        return result.Warnings.Where(w => w.Code == code).ToList();
    }

    private static void AssertGeometry(Node node, double x, double y, double w, double h, double? rotation = null)
    {
        Assert.NotNull(node.Geometry);
        Assert.Equal(x, node.Geometry.X, 2);
        Assert.Equal(y, node.Geometry.Y, 2);
        Assert.Equal(w, node.Geometry.W, 2);
        Assert.Equal(h, node.Geometry.H, 2);
        Assert.Equal(rotation, node.Geometry.Rotation);
    }

    // ---- shapes: position, size, kind -------------------------------------------------------------------------------------------

    [Fact]
    public void A_page_of_shapes_keeps_every_shape_with_its_label_size_and_position_turned_so_y_points_down()
    {
        ImportResult result = Import(VisioFixtures.Shapes());

        Assert.Equal(10, result.Document.Nodes.Count);
        Assert.Equal("Shapes", result.Document.Title);
        Assert.Equal(DocumentType.Flowchart, result.Document.Type);
        Assert.Equal("visio-import", result.Document.Meta.CreatedBy);
        // 2 inches along and 1 inch down from the top is (200, 100) on the canvas: Visio's origin is the bottom-left of the page and its y axis points up.
        Node plain = NodeOf(result, "n_1");
        Assert.Equal("Plain box", plain.Label);
        AssertGeometry(plain, 125, 62.5, 150, 75);
        AssertGeometry(NodeOf(result, "n_7"), 125, 460, 150, 80);
        Assert.All(result.Document.Nodes, n => Assert.NotNull(n.Geometry));
    }

    [Theory]
    [InlineData("n_1", "process", "rectangle")]
    [InlineData("n_2", "process", "rounded")]
    [InlineData("n_3", "terminal", "pill")]
    [InlineData("n_4", "decision", "diamond")]
    [InlineData("n_5", "text", "text")]
    [InlineData("n_6", "process", "rectangle")]
    [InlineData("n_7", "io", "parallelogram")]
    [InlineData("n_8", "process", "hexagon")]
    public void Each_outline_a_shape_draws_becomes_the_kind_and_shape_a_flowchart_gives_it(string id, string kind, string shape)
    {
        ImportResult result = Import(VisioFixtures.Shapes());

        Node node = NodeOf(result, id);
        Assert.Equal(kind, node.Kind);
        Assert.Equal(shape, node.Shape);
    }

    [Fact]
    public void Only_a_kind_that_was_a_judgement_is_flagged_for_review()
    {
        ImportResult result = Import(VisioFixtures.Shapes());

        // An ellipse might be a start, an end, a state or a system; a hexagon might be a preparation or anything. A box, a diamond and a parallelogram are not guesses.
        Assert.Equal(["n_3", "n_8"], Warnings(result, ImportWarningCodes.KindGuessed).Select(w => w.ElementId ?? string.Empty).Order().ToArray());
        Assert.Contains("\"Oval\" is drawn as an ellipse, so it was given the kind 'terminal'", Warnings(result, ImportWarningCodes.KindGuessed)[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_outline_the_model_has_no_shape_for_is_a_plain_box_and_is_said_once_with_a_count()
    {
        string triangles = Shape(1, 2, 6, 1, 1, "A", geometry: Triangle) + Shape(2, 4, 6, 1, 1, "B", geometry: Triangle) + Shape(3, 6, 6, 1, 1, "C");
        ImportResult result = ImportPage(triangles);

        Assert.Equal("rectangle", NodeOf(result, "n_1").Shape);
        ImportWarning custom = Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax));
        Assert.Contains("2 shapes have a custom outline", custom.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shape_that_draws_nothing_and_says_nothing_is_left_out_with_the_others_that_have_no_size()
    {
        string shapes = Shape(1, 2, 6, 1, 1, "Kept") + Shape(2, 4, 6, 1, 1, geometry: Invisible) + Shape(3, 6, 6, 0, 1, "No width") + Shape(4, 8, 6, 1, 1, geometry: null);
        ImportResult result = ImportPage(shapes);

        Assert.Equal(["n_1"], result.Document.Nodes.Select(n => n.Id).ToArray());
        Assert.Contains("3 shapes have no size, or nothing to draw", Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_text_label_with_no_outline_becomes_a_text_node_and_keeps_its_position()
    {
        ImportResult result = Import(VisioFixtures.Shapes());

        Node text = NodeOf(result, "n_5");
        Assert.Equal("Just text", text.Label);
        AssertGeometry(text, 350, 280, 200, 40);
    }

    [Fact]
    public void A_rectangle_written_with_absolute_rows_in_inches_is_a_box_too()
    {
        ImportResult result = ImportPage(Shape(1, 2, 6, 1.5, 0.75, "Box", geometry: AbsoluteRect(1.5, 0.75)));

        Assert.Equal("rectangle", NodeOf(result, "n_1").Shape);
        Assert.Empty(Warnings(result, ImportWarningCodes.UnsupportedSyntax));
    }

    [Theory]
    [InlineData("Process", "process", "rectangle")]
    [InlineData("Process.12", "process", "rectangle")]
    [InlineData("Decision", "decision", "diamond")]
    [InlineData("Start/End", "terminal", "pill")]
    [InlineData("Document", "io", "document")]
    [InlineData("Data", "io", "parallelogram")]
    [InlineData("Database", "stored-data", "cylinder")]
    [InlineData("Subprocess", "subprocess", "predefined-process")]
    [InlineData("Manual input", "io", "manual-input")]
    [InlineData("Off-page reference", "process", "off-page-connector")]
    [InlineData("Delay", "delay", "delay")]
    [InlineData("Preparation", "process", "hexagon")]
    [InlineData("Note", "note", "custom:basic/note")]
    [InlineData("Actor", "process", "custom:generic/user")]
    [InlineData("com.lucidchart.ProcessBlock21.f71da818dccc6284be74c036b29d4164", "process", "rectangle")]
    [InlineData("com.lucidchart.DecisionBlock3.0123456789abcdef", "decision", "diamond")]
    [InlineData("com.lucidchart.TerminatorBlock.9", "terminal", "pill")]
    public void The_name_of_a_master_decides_what_a_shape_is_before_its_outline_does(string masterName, string kind, string shape)
    {
        // The shape's own outline is a triangle, which would be a custom drawing: the name wins.
        byte[] file = new VisioDocBuilder()
            .Master(2, masterName, Shape(1, 1, 1, 1, 1, geometry: Triangle))
            .Page("P", Instance(1, 2, 3, 3, "Thing"))
            .Build();

        ImportResult result = Import(file);

        Node node = NodeOf(result, "n_1");
        Assert.Equal(kind, node.Kind);
        Assert.Equal(shape, node.Shape);
        Assert.Empty(Warnings(result, ImportWarningCodes.UnsupportedSyntax));
    }

    [Fact]
    public void The_same_shapes_are_mapped_through_the_architecture_and_freeform_vocabularies()
    {
        byte[] file = new VisioDocBuilder()
            .Page("P", Shape(1, 2, 6, 1.4, 1, "Choice", geometry: Diamond) + Shape(2, 5, 6, 1.2, 0.8, "Oval", geometry: Ellipse) + Shape(3, 8, 6, 1, 1, "Plain"))
            .Build();

        ImportResult architecture = Import(file, DocumentType.Architecture);
        ImportResult freeform = Import(file, DocumentType.Freeform);

        Assert.Equal(["service", "service", "service"], architecture.Document.Nodes.Select(n => n.Kind).ToArray());
        Assert.Equal(["diamond", "pill", "rectangle"], architecture.Document.Nodes.Select(n => n.Shape).ToArray());
        Assert.Equal(["shape", "shape", "shape"], freeform.Document.Nodes.Select(n => n.Kind).ToArray());
        // Freeform has no vocabulary, so there is nothing to guess and nothing to flag.
        Assert.Empty(Warnings(freeform, ImportWarningCodes.KindGuessed));
        Assert.NotEmpty(Warnings(architecture, ImportWarningCodes.KindGuessed));
    }

    [Fact]
    public void A_sequence_document_is_refused_because_it_is_made_from_mermaid()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => Import(VisioFixtures.Shapes(), DocumentType.Sequence));

        Assert.Contains("sequenceDiagram", error.Message, StringComparison.Ordinal);
    }

    // ---- rotation, text, style ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_shape_turned_thirty_degrees_anticlockwise_in_visio_is_minus_thirty_clockwise_on_the_canvas()
    {
        ImportResult result = Import(VisioFixtures.Shapes());

        Node tilted = NodeOf(result, "n_10");
        // Visio's Angle is in radians, counter-clockwise, on a y-up page; the canvas turns clockwise in degrees on a y-down one.
        AssertGeometry(tilted, 125, 662.5, 150, 75, rotation: -30);
    }

    [Fact]
    public void A_shape_turned_about_a_pin_that_is_not_its_centre_moves_its_box_as_visio_does()
    {
        // 2 wide by 1 high, pinned at its left end (LocPinX 0, LocPinY 0.5) at (3, 4), turned 90 degrees anticlockwise: it stands on its end and its centre is
        // one inch above the pin. Centre (3, 5) on the page is (300, 300) on the canvas.
        string shape = $"<Shape ID='1' Type='Shape'>{Cell("PinX", 3)}{Cell("PinY", 4)}{Cell("Width", 2)}{Cell("Height", 1)}{Cell("LocPinX", 0)}{Cell("LocPinY", 0.5)}{Cell("Angle", Math.PI / 2)}{Rect}<Text>Standing</Text></Shape>";

        ImportResult result = ImportPage(shape);

        AssertGeometry(NodeOf(result, "n_1"), 200, 250, 200, 100, rotation: -90);
    }

    [Fact]
    public void A_label_keeps_its_paragraphs_and_line_breaks_and_loses_its_run_markers_and_trailing_newline()
    {
        string shape = Shape(1, 2, 6, 2, 1, geometry: Rect).Replace("</Shape>", "<Text><cp IX='0'/>First line\nSecond \u2028third<pp IX='0'/> <fld IX='0' Format='x'>Page 1</fld>\n</Text></Shape>", StringComparison.Ordinal);

        ImportResult result = ImportPage(shape);

        Assert.Equal("First line\nSecond \nthird Page 1", NodeOf(result, "n_1").Label);
    }

    [Fact]
    public void A_label_longer_than_ten_thousand_characters_is_cut_and_says_so()
    {
        ImportResult result = ImportPage(Shape(1, 2, 6, 2, 1, new string('x', 10_050)));

        Assert.Equal(10_000, NodeOf(result, "n_1").Label.Length);
        Assert.Contains("was shortened to 10,000 characters", Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fill_line_and_text_style_a_shape_set_are_carried_as_basic_style()
    {
        ImportResult result = Import(VisioFixtures.Shapes());

        NodeStyle style = Assert.IsType<NodeStyle>(NodeOf(result, "n_9").Style);
        Assert.Equal("#DAE8FC", style.Fill);
        Assert.Equal("#6C8EBF", style.Stroke);
        Assert.Equal(2, style.StrokeWidth);
        Assert.Equal(Dash.Dashed, style.Dash);
        Assert.Null(NodeOf(result, "n_1").Style);
    }

    [Fact]
    public void Text_colour_size_and_bold_come_from_the_first_character_run()
    {
        ImportResult result = Import(VisioFixtures.MasterLess());

        NodeStyle style = Assert.IsType<NodeStyle>(NodeOf(result, "n_1").Style);
        Assert.Equal("#FFF2CC", style.Fill);
        Assert.Equal("#333333", style.TextColor);
        Assert.Equal(16.67, style.FontSize);
        Assert.True(style.Bold);
    }

    [Fact]
    public void A_colour_named_by_number_is_looked_up_in_the_documents_table_and_one_the_theme_chooses_is_left_to_the_theme()
    {
        string shapes =
            Shape(1, 2, 6, 1, 1, "Red", cells: TextCell("FillForegnd", "2"))
            + Shape(2, 4, 6, 1, 1, "Custom", cells: TextCell("FillForegnd", "25"))
            + Shape(3, 6, 6, 1, 1, "Themed", cells: TextCell("FillForegnd", "Themed"))
            + Shape(4, 8, 6, 1, 1, "Unknown index", cells: TextCell("FillForegnd", "99"))
            + Shape(5, 2, 4, 1, 1, "Not filled", cells: TextCell("FillForegnd", "#FF0000") + Cell("FillPattern", 0))
            + Shape(6, 4, 4, 1, 1, "No line", cells: Cell("LinePattern", 0))
            + Shape(7, 6, 4, 1, 1, "Dotted", cells: Cell("LinePattern", 3));
        byte[] file = new VisioDocBuilder { Colours = "<ColorEntry IX='25' RGB='#00B050'/>" }.Page("P", shapes).Build();

        ImportResult result = Import(file);

        Assert.Equal("#FF0000", NodeOf(result, "n_1").Style!.Fill);
        Assert.Equal("#00B050", NodeOf(result, "n_2").Style!.Fill);
        Assert.Null(NodeOf(result, "n_3").Style);
        Assert.Null(NodeOf(result, "n_4").Style);
        Assert.Equal("transparent", NodeOf(result, "n_5").Style!.Fill);
        Assert.Equal(0, NodeOf(result, "n_6").Style!.StrokeWidth);
        Assert.Equal(Dash.Dotted, NodeOf(result, "n_7").Style!.Dash);
    }

    // ---- masters -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_instance_of_a_master_takes_its_size_outline_and_fill_from_the_master()
    {
        ImportResult result = Import(VisioFixtures.Masters());

        // The page stores only a position and text for "Start": 1.5 by 0.75 and the rectangle come from the master "Process", and so does its white fill.
        Node start = NodeOf(result, "n_1");
        Assert.Equal("Start", start.Label);
        Assert.Equal("process", start.Kind);
        AssertGeometry(start, 125, 162.5, 150, 75);
        Assert.Equal("#FFFFFF", start.Style!.Fill);
        Node decision = NodeOf(result, "n_2");
        Assert.Equal("decision", decision.Kind);
        AssertGeometry(decision, 525, 150, 150, 100);
    }

    [Fact]
    public void A_colour_a_master_asked_the_theme_for_is_not_inherited_because_it_was_worked_out_for_another_theme()
    {
        string master = Shape(1, 1, 1, 1, 1, cells: TextCell("FillForegnd", "#5B9BD5", "THEMEGUARD(THEMEVAL(\"FillColor\"))") + TextCell("LineColor", "#FF0000"));
        byte[] file = new VisioDocBuilder().Master(2, "Process", master).Page("P", Instance(1, 2, 3, 3, "X")).Build();

        ImportResult result = Import(file);

        NodeStyle style = Assert.IsType<NodeStyle>(NodeOf(result, "n_1").Style);
        Assert.Null(style.Fill);
        Assert.Equal("#FF0000", style.Stroke);
        Assert.Contains("1 shape takes its colours or line from a Visio theme", Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stencil_master_that_is_a_group_is_one_plain_box_named_once_and_connectors_glued_to_its_parts_reach_the_box()
    {
        ImportResult result = Import(VisioFixtures.Masters());

        // "Core Switch" is a group of two shapes in the master; its two children are not nodes. The box is the group's own size at its own place.
        Assert.Equal(["n_1", "n_2", "n_3"], result.Document.Nodes.Select(n => n.Id).ToArray());
        Node box = NodeOf(result, "n_3");
        Assert.Equal("Core switch", box.Label);
        Assert.Equal("rectangle", box.Shape);
        AssertGeometry(box, 350, 450, 100, 100);
        ImportWarning stencil = Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax), w => w.Message.Contains("stencil", StringComparison.Ordinal));
        Assert.Contains("1 shape is an instance of the Visio master 'Core Switch' (a stencil shape)", stencil.Message, StringComparison.Ordinal);
        Assert.Contains("\"Core switch\"", stencil.Message, StringComparison.Ordinal);
        // Connector 7 is glued to a part (shape 4) of the group; the group's box is what it reaches.
        Edge reaches = EdgeOf(result, "e_7");
        Assert.Equal("n_2", reaches.From.Node);
        Assert.Equal("n_3", reaches.To.Node);
    }

    [Fact]
    public void Many_shapes_of_one_unknown_master_are_one_warning_with_a_count_and_examples()
    {
        string master = Shape(1, 1, 1, 1, 1, geometry: Triangle);
        string page = Instance(1, 2, 2, 6, "Alpha") + Instance(2, 2, 4, 6, "Beta") + Instance(3, 2, 6, 6, "Gamma") + Instance(4, 2, 8, 6, "Delta");
        byte[] file = new VisioDocBuilder().Master(2, "Server", master).Page("P", page).Build();

        ImportResult result = Import(file);

        ImportWarning warning = Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax));
        Assert.Contains("4 shapes are instances of the Visio master 'Server' (a stencil shape)", warning.Message, StringComparison.Ordinal);
        Assert.Contains("for example \"Alpha\", \"Beta\", \"Gamma\"", warning.Message, StringComparison.Ordinal);
        Assert.Equal(4, result.Document.Nodes.Count);
    }

    [Fact]
    public void A_lucidchart_shape_a_group_of_one_outline_part_with_its_text_on_the_group_is_one_node_of_the_part_s_outline()
    {
        // Lucidchart writes every shape as a group instance of a master named com.lucidchart.<Block>; the outline is one child covering the group, and the text is on the group.
        string master = Shape(1, 1, 1, 2, 1, type: "Group", geometry: null, children: Shape(2, 1, 0.5, 2, 1, geometry: Diamond));
        string page = Instance(5, 2, 4, 5, "Approved?", type: "Group", children: MasterChild(6, 2));
        byte[] file = new VisioDocBuilder().Master(2, "com.lucidchart.CustomBlock7.deadbeef", master).Page("P", page).Build();

        ImportResult result = Import(file);

        Node node = Assert.Single(result.Document.Nodes);
        Assert.Equal("Approved?", node.Label);
        Assert.Equal("decision", node.Kind);
        Assert.Equal("diamond", node.Shape);
        Assert.Empty(Warnings(result, ImportWarningCodes.UnsupportedSyntax));
    }

    [Fact]
    public void A_lucidchart_process_block_with_a_corner_radius_is_a_rounded_box_and_one_without_is_square()
    {
        string master = Shape(1, 1, 1, 1.5, 0.75, type: "Group", geometry: null, cells: Cell("Rounding", 0.003125));
        string page = Instance(1, 2, 3, 6, "Rounded", cells: Cell("Rounding", 0.0375), type: "Group") + Instance(2, 2, 6, 6, "Square", type: "Group");
        byte[] file = new VisioDocBuilder().Master(2, "com.lucidchart.ProcessBlock21.f71da818", master).Page("P", page).Build();

        ImportResult result = Import(file);

        Assert.Equal("rounded", NodeOf(result, "n_1").Shape);
        Assert.Equal("rectangle", NodeOf(result, "n_2").Shape);
        Assert.Empty(Warnings(result, ImportWarningCodes.UnsupportedSyntax));
    }

    [Fact]
    public void An_unknown_lucidchart_block_with_a_custom_outline_is_named_by_its_short_name_in_the_one_warning()
    {
        string master = Shape(1, 1, 1, 1, 1, type: "Group", geometry: null, children: Shape(2, 0.5, 0.5, 1, 1, geometry: Triangle));
        byte[] file = new VisioDocBuilder().Master(2, "com.lucidchart.ShieldBlock4.cafe", master).Page("P", Instance(1, 2, 3, 3, "Guard", type: "Group", children: MasterChild(2, 2))).Build();

        ImportResult result = Import(file);

        Assert.Contains("the Visio master 'Lucidchart ShieldBlock'", Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shape_the_page_marks_deleted_is_left_out()
    {
        // Del="1" is how a page hides the master's shape of that id from one of its instances: the shape is not there, whatever its cells say.
        ImportResult result = ImportPage(Shape(1, 2, 6, 1, 1, "Kept") + Shape(2, 4, 6, 1, 1, "Deleted", attributes: "Del='1'"));

        Assert.Equal("Kept", Assert.Single(result.Document.Nodes).Label);
    }

    [Fact]
    public void A_part_that_does_not_cover_a_stencil_group_is_not_taken_for_its_outline()
    {
        // A badge icon: a small diamond in the corner of a 2 by 1 group. The diamond is one of its parts, not the shape the group is.
        string master = Shape(1, 1, 1, 2, 1, type: "Group", geometry: null, children: Shape(2, 1.6, 0.8, 0.4, 0.4, geometry: Diamond));
        byte[] file = new VisioDocBuilder().Master(2, "com.lucidchart.BadgeBlock1.abc", master).Page("P", Instance(5, 2, 4, 5, "Badge", type: "Group", children: MasterChild(6, 2))).Build();

        ImportResult result = Import(file);

        Node node = Assert.Single(result.Document.Nodes);
        Assert.Equal("process", node.Kind);
        Assert.Equal("rectangle", node.Shape);
        Assert.Contains("the Visio master 'Lucidchart BadgeBlock'", Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax)).Message, StringComparison.Ordinal);
    }

    // ---- groups ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_group_becomes_a_container_and_each_child_is_placed_in_its_groups_space_all_the_way_down()
    {
        ImportResult result = Import(VisioFixtures.Group());

        Assert.Equal(["c_1", "n_2", "n_3", "c_4", "n_5"], result.Document.Nodes.Select(n => n.Id).ToArray());
        Node group = NodeOf(result, "c_1");
        Assert.Equal("container", group.Kind);
        Assert.Equal("Backend", group.Label);
        Assert.Null(group.Parent);
        AssertGeometry(group, 200, 200, 600, 400);
        AssertGeometry(NodeOf(result, "n_2"), 250, 270, 200, 100);
        AssertGeometry(NodeOf(result, "n_3"), 550, 270, 200, 100);
        Node inner = NodeOf(result, "c_4");
        Assert.Equal("container", inner.Kind);
        Assert.Equal("c_1", inner.Parent);
        AssertGeometry(inner, 250, 440, 500, 120);
        Node database = NodeOf(result, "n_5");
        Assert.Equal("c_4", database.Parent);
        Assert.Equal("c_1", NodeOf(result, "n_2").Parent);
        // Geometry is absolute even inside a container: the grandchild's page position is the sum of both groups' origins and its own.
        AssertGeometry(database, 300, 460, 150, 80);
        // A group is a container by Visio's own word for it: it is not a judgement, so nothing is flagged.
        Assert.DoesNotContain(Warnings(result, ImportWarningCodes.KindGuessed), w => w.ElementId is "c_1" or "c_4");
    }

    [Fact]
    public void A_turned_group_carries_its_turn_to_its_children_positions_and_its_children_turn_with_it()
    {
        // A group at (5, 4), 4 by 2, turned 90 degrees anticlockwise: a child 1 inch right of the group's centre ends up 1 inch above it.
        string child = Shape(2, 3, 1, 1, 1, "Child");
        string group = Shape(1, 5, 4, 4, 2, "Turned", type: "Group", geometry: null, angle: Math.PI / 2, children: child);

        ImportResult result = ImportPage(group);

        AssertGeometry(NodeOf(result, "c_1"), 300, 300, 400, 200, rotation: -90);
        // Local (3, 1) is 1 right and 0 above the group's centre (2, 1); turned, it is 1 above: page (5, 5), the canvas's (500, 300).
        AssertGeometry(NodeOf(result, "n_2"), 450, 250, 100, 100, rotation: -90);
    }

    [Fact]
    public void A_swimlane_master_with_children_is_a_container_whose_children_keep_their_own_masters()
    {
        string lane = Shape(1, 1, 1, 6, 3, geometry: Rect, type: "Group", children: Shape(2, 3, 1.5, 1, 0.5, geometry: Rect));
        string page = Instance(10, 2, 5, 4, "Lane A", type: "Group", children: MasterChild(11, 2, "Task"));
        byte[] file = new VisioDocBuilder().Master(2, "Swimlane", lane).Page("P", page).Build();

        ImportResult result = Import(file);

        Assert.Equal("container", NodeOf(result, "c_10").Kind);
        Assert.Equal("c_10", NodeOf(result, "n_11").Parent);
    }

    [Fact]
    public void A_group_with_nothing_imported_inside_it_is_an_ordinary_shape()
    {
        string group = Shape(1, 5, 4, 4, 2, "Empty group", type: "Group", children: Shape(2, 3, 1, 1, 1, geometry: Invisible));

        ImportResult result = ImportPage(group);

        Assert.Equal("process", NodeOf(result, "n_1").Kind);
    }

    // ---- connectors --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_glued_connector_becomes_an_edge_from_the_shape_at_its_start_to_the_shape_at_its_end_with_its_label_and_arrow()
    {
        ImportResult result = Import(VisioFixtures.Connectors());

        Edge edge = EdgeOf(result, "e_4");
        Assert.Equal("n_1", edge.From.Node);
        Assert.Equal("n_2", edge.To.Node);
        Assert.Equal("yes", edge.Label);
        Assert.Equal("flow", edge.Kind);
        Assert.Null(edge.Style);
        Assert.Equal(RoutingMode.Orthogonal, edge.Routing!.Mode);
        Assert.Null(edge.Routing.Waypoints);
    }

    [Fact]
    public void A_hollow_triangle_end_makes_a_generalisation_and_a_dashed_line_is_kept_dashed()
    {
        ImportResult result = Import(VisioFixtures.Connectors());

        Edge edge = EdgeOf(result, "e_5");
        Assert.Equal("association", edge.Kind);
        Assert.Equal(ArrowHead.Inheritance, edge.Style!.ArrowEnd);
        Assert.Equal(Dash.Dashed, edge.Style.Dash);
        Assert.Equal("n_2", edge.From.Node);
        Assert.Equal("n_3", edge.To.Node);
    }

    [Theory]
    [InlineData(0, ArrowHead.None)]
    [InlineData(1, ArrowHead.Open)]
    [InlineData(4, ArrowHead.Arrow)]
    [InlineData(13, ArrowHead.Arrow)]
    [InlineData(14, ArrowHead.Inheritance)]
    [InlineData(10, ArrowHead.Circle)]
    [InlineData(11, ArrowHead.Diamond)]
    [InlineData(22, ArrowHead.Aggregation)]
    [InlineData(24, ArrowHead.One)]
    [InlineData(27, ArrowHead.Many)]
    [InlineData(29, ArrowHead.ZeroOrMany)]
    [InlineData(30, ArrowHead.ZeroOrOne)]
    public void Visios_numbered_line_ends_become_the_nearest_arrowhead(int number, ArrowHead expected)
    {
        ArrowHead head = VisioStyleMapper.Head(number.ToString(System.Globalization.CultureInfo.InvariantCulture), atStart: false, out bool resolved, _ => Assert.Fail("a known number must not be approximated"));

        Assert.True(resolved);
        Assert.Equal(expected, head);
    }

    [Fact]
    public void A_line_end_number_past_visios_table_is_a_plain_arrowhead_and_says_so()
    {
        string shapes = Shape(1, 2, 6, 1, 1, "A") + Shape(2, 6, 6, 1, 1, "B") + Connector(3, 2.5, 6, 5.5, 6, cells: Cell("EndArrow", 77), attributes: string.Empty);

        ImportResult result = ImportPage(shapes, Glue(3, 1, 2));

        Assert.Equal("flow", EdgeOf(result, "e_3").Kind);
        Assert.Contains("the arrowhead number 77 at its end, which has no equivalent", Assert.Single(Warnings(result, ImportWarningCodes.StyleApproximated)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_connector_from_the_dynamic_connector_master_whose_arrow_the_theme_chooses_gets_an_arrow_and_one_warning_says_so()
    {
        ImportResult result = Import(VisioFixtures.Masters());

        // The master and the connector say nothing of EndArrow (the document's style says "Themed"): an arrow at the end, as a flowchart's connector has.
        Assert.Equal("flow", EdgeOf(result, "e_6").Kind);
        Assert.Equal("go", EdgeOf(result, "e_6").Label);
        Assert.Contains("2 connectors draw their arrowheads from Visio's theme, which is not read, so each was given an arrow at its end",
            Warnings(result, ImportWarningCodes.UnsupportedSyntax).Select(w => w.Message).Single(m => m.Contains("arrowheads", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_connector_that_is_not_an_instance_of_a_master_and_names_no_arrow_has_none_and_is_an_association()
    {
        string shapes = Shape(1, 2, 6, 1, 1, "A") + Shape(2, 6, 6, 1, 1, "B") + Connector(3, 2.5, 6, 5.5, 6, attributes: string.Empty);

        ImportResult result = ImportPage(shapes, Glue(3, 1, 2));

        Edge edge = EdgeOf(result, "e_3");
        Assert.Equal("association", edge.Kind);
        Assert.Equal(RoutingMode.Straight, edge.Routing!.Mode);
    }

    [Theory]
    [InlineData("<Cell N='ConLineRouteExt' V='2'/>", RoutingMode.Curved)]
    [InlineData("<Cell N='ConLineRouteExt' V='1'/>", RoutingMode.Straight)]
    [InlineData("<Cell N='ShapeRouteStyle' V='16'/>", RoutingMode.Straight)]
    [InlineData("<Cell N='ShapeRouteStyle' V='1'/>", RoutingMode.Orthogonal)]
    [InlineData("", RoutingMode.Orthogonal)]
    public void A_connectors_routing_follows_its_route_settings_and_defaults_to_right_angles(string cells, RoutingMode expected)
    {
        string shapes = Shape(1, 2, 6, 1, 1, "A") + Shape(2, 6, 6, 1, 1, "B") + Connector(3, 2.5, 6, 5.5, 6, cells: cells + Cell("EndArrow", 13));

        ImportResult result = ImportPage(shapes, Glue(3, 1, 2));

        Assert.Equal(expected, EdgeOf(result, "e_3").Routing!.Mode);
    }

    [Fact]
    public void An_architecture_connector_to_a_database_is_data_and_a_dashed_one_is_a_dependency()
    {
        string shapes = Shape(1, 2, 6, 1, 1, "App") + Shape(2, 6, 6, 1, 1, "DB", nameU: "Database") + Shape(3, 4, 3, 1, 1, "Cache")
            + Connector(4, 2.5, 6, 5.5, 6, cells: Cell("EndArrow", 13)) + Connector(5, 2.5, 5.5, 4, 3.5, cells: Cell("EndArrow", 13) + Cell("LinePattern", 2));

        ImportResult result = ImportPage(shapes, Glue(4, 1, 2) + Glue(5, 1, 3), DocumentType.Architecture);

        Assert.Equal("database", NodeOf(result, "n_2").Kind);
        Assert.Equal("data", EdgeOf(result, "e_4").Kind);
        Assert.Equal("dependency", EdgeOf(result, "e_5").Kind);
    }

    [Fact]
    public void A_connector_with_a_loose_end_or_glued_to_a_shape_the_page_does_not_have_is_left_out_and_said_so()
    {
        ImportResult result = Import(VisioFixtures.Connectors());

        Assert.Equal(["e_4", "e_5"], result.Document.Edges.Select(e => e.Id).ToArray());
        List<ImportWarning> missing = Warnings(result, ImportWarningCodes.MissingReference);
        Assert.Equal(2, missing.Count);
        Assert.Contains("is not attached to a shape at its end (a loose end in Visio)", missing.Single(w => w.Message.Contains("Visio id 6", StringComparison.Ordinal)).Message, StringComparison.Ordinal);
        Assert.Contains("is attached at its end to a shape, '99', that is not on the page", missing.Single(w => w.Message.Contains("Visio id 7", StringComparison.Ordinal)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_loose_at_both_ends_is_one_warning_not_two()
    {
        string shapes = Shape(1, 2, 6, 1, 1, "A") + Connector(2, 3, 6, 5, 6, attributes: string.Empty);

        ImportResult result = ImportPage(shapes);

        ImportWarning warning = Assert.Single(Warnings(result, ImportWarningCodes.MissingReference));
        Assert.Contains("is not attached to a shape at either end (a loose line in Visio), and a Draughtsman connector joins two shapes, so it was left out.", warning.Message, StringComparison.Ordinal);
        Assert.Empty(result.Document.Edges);
    }

    [Fact]
    public void A_connector_glued_to_another_connector_or_to_a_hidden_shape_is_left_out_with_its_reason()
    {
        string shapes = Shape(1, 2, 6, 1, 1, "A") + Shape(2, 6, 6, 1, 1, "Hidden", cells: TextCell("LayerMember", "0"))
            + Connector(3, 2.5, 6, 5.5, 6, attributes: string.Empty) + Connector(4, 3, 5, 3, 4, attributes: string.Empty);
        string sheet = Layers((0, "Hidden", false), (1, "Shown", true));

        ImportResult result = ImportPage(shapes, Glue(3, 1, 2) + Glue(4, 1, 3), sheetExtra: sheet);

        List<ImportWarning> missing = Warnings(result, ImportWarningCodes.MissingReference);
        Assert.Contains(missing, w => w.Message.Contains("attached at its end to a shape that was hidden or left out", StringComparison.Ordinal));
        Assert.Contains(missing, w => w.Message.Contains("attached at its end to another connector", StringComparison.Ordinal));
    }

    [Fact]
    public void A_connector_may_join_a_shape_to_itself()
    {
        string shapes = Shape(1, 2, 6, 1, 1, "Loop") + Connector(2, 2.5, 6, 2.5, 6.5, cells: Cell("EndArrow", 13), attributes: string.Empty);

        ImportResult result = ImportPage(shapes, Glue(2, 1, 1));

        Edge edge = Assert.Single(result.Document.Edges);
        Assert.Equal("n_1", edge.From.Node);
        Assert.Equal("n_1", edge.To.Node);
    }

    // ---- what is not carried ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Shape_data_themes_and_flips_are_each_one_aggregated_warning_with_a_count()
    {
        string shapes =
            Shape(1, 2, 6, 1, 1, "A", cells: ShapeData())
            + Shape(2, 4, 6, 1, 1, "B", cells: ShapeData())
            + Shape(3, 6, 6, 1, 1, "C", cells: TextCell("FillForegnd", "#ab9ac0", "THEMEGUARD(THEMEVAL(\"VariantColor3\"))"))
            + Shape(4, 8, 6, 1, 1, "D", cells: Cell("FlipX", 1));

        ImportResult result = ImportPage(shapes);

        List<string> messages = Warnings(result, ImportWarningCodes.UnsupportedSyntax).Select(w => w.Message).ToList();
        Assert.Equal(3, messages.Count);
        Assert.Contains("2 shapes carry shape data (custom properties), which is not imported.", messages);
        Assert.Contains(messages, m => m.StartsWith("1 shape takes its colours or line from a Visio theme.", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("1 shape is flipped in Visio.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_picture_is_a_plain_box_and_pictures_are_counted_together()
    {
        string picture = "<Shape ID='1' Type='Foreign'>" + Cell("PinX", 2) + Cell("PinY", 6) + Cell("Width", 1) + Cell("Height", 1) + "<ForeignData ForeignType='Bitmap'><Rel r:id='rId1' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'/></ForeignData></Shape>";

        ImportResult result = ImportPage(picture + picture.Replace("ID='1'", "ID='2'", StringComparison.Ordinal));

        Assert.Equal(2, result.Document.Nodes.Count);
        Assert.Contains("2 pictures or embedded objects were not imported", Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shape_on_a_hidden_layer_is_left_out_and_counted_and_several_layers_are_noted()
    {
        string shapes = Shape(1, 2, 6, 1, 1, "Shown", cells: TextCell("LayerMember", "1")) + Shape(2, 4, 6, 1, 1, "Hidden", cells: TextCell("LayerMember", "0")) + Shape(3, 6, 6, 1, 1, "Both", cells: TextCell("LayerMember", "0;1"));
        string sheet = Layers((0, "Notes", false), (1, "Main", true));

        ImportResult result = ImportPage(shapes, sheetExtra: sheet);

        Assert.Equal(["n_1", "n_3"], result.Document.Nodes.Select(n => n.Id).ToArray());
        List<string> messages = Warnings(result, ImportWarningCodes.UnsupportedSyntax).Select(w => w.Message).ToList();
        Assert.Contains("1 shape or connector on a hidden layer was left out.", messages);
        Assert.Contains(messages, m => m.StartsWith("The page has 2 layers. Draughtsman has no layers", StringComparison.Ordinal));
    }

    [Fact]
    public void A_background_page_is_named_as_not_imported()
    {
        byte[] file = new VisioDocBuilder()
            .Page("Background", Shape(1, 5, 4, 2, 1, "Watermark"), pageAttrs: "Background='1'")
            .Page("Main", Shape(1, 5, 4, 2, 1, "Content"), pageAttrs: "BackPage='0'")
            .Build();

        ImportResult result = Import(file, page: 2);

        Assert.Contains("The page sits on a background page (\"Background\"), whose shapes are not imported.", Warnings(result, ImportWarningCodes.UnsupportedSyntax).Select(w => w.Message));
        Assert.Equal("Content", Assert.Single(result.Document.Nodes).Label);
    }

    [Fact]
    public void A_repeated_shape_id_imports_the_first_and_skips_the_later_one()
    {
        ImportResult result = ImportPage(Shape(1, 2, 6, 1, 1, "First") + Shape(1, 4, 6, 1, 1, "Second"));

        Assert.Equal("First", Assert.Single(result.Document.Nodes).Label);
        Assert.Contains("The id '1' is used by more than one shape on the page", Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shape_placed_too_far_from_the_origin_for_a_canvas_has_no_position_and_says_so()
    {
        ImportResult result = ImportPage(Shape(1, 2, 6, 1, 1, "Far") + Shape(2, 1e9, 6, 1, 1, "Farther"));

        Assert.Null(NodeOf(result, "n_2").Geometry);
        Assert.Contains("is placed too far from the origin for a canvas", Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_that_does_not_give_its_height_is_as_tall_as_its_content()
    {
        byte[] file = new VisioDocBuilder { PageHeight = -1 }.Page("P", Shape(1, 2, 3, 2, 1, "Low") + Shape(2, 2, 5, 2, 1, "High")).Build();

        ImportResult result = Import(file);

        // The top of the highest shape (5.5 inches) becomes y 0; the lower one is 2 inches below it.
        Assert.Equal(0, NodeOf(result, "n_2").Geometry!.Y, 2);
        Assert.Equal(200, NodeOf(result, "n_1").Geometry!.Y, 2);
    }

    // ---- pages -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_multi_page_file_imports_the_first_page_and_names_the_others_in_the_first_warning()
    {
        ImportResult result = Import(VisioFixtures.MultiPage());

        Assert.Equal("Overview", result.Document.Title);
        Assert.Equal(2, result.Document.Nodes.Count);
        Assert.Equal(ImportWarningCodes.PageSelected, result.Warnings[0].Code);
        Assert.Equal("Page 1 (\"Overview\") of 3 was imported; the other 2 pages were not. Choose another page to import it instead.", result.Warnings[0].Message);
        Assert.Equal(
            [new ImportPage(1, "Overview", true), new ImportPage(2, "Details", false), new ImportPage(3, "Page-3", false)],
            result.Pages);
    }

    [Fact]
    public void A_later_page_imports_on_its_own_with_its_own_title()
    {
        ImportResult result = Import(VisioFixtures.MultiPage(), page: 2);

        Assert.Equal("Details", result.Document.Title);
        Assert.Equal("Only on page two", Assert.Single(result.Document.Nodes).Label);
        Assert.Equal([false, true, false], result.Pages!.Select(p => p.Selected).ToArray());
    }

    [Fact]
    public void A_page_with_visios_own_default_name_is_untitled_and_an_empty_page_says_so()
    {
        ImportResult result = Import(VisioFixtures.MultiPage(), page: 3);

        Assert.Equal("Imported diagram", result.Document.Title);
        Assert.Empty(result.Document.Nodes);
        Assert.Equal("The page has no shapes.", Assert.Single(Warnings(result, ImportWarningCodes.EmptyDiagram)).Message);
    }

    [Fact]
    public void A_title_the_caller_gives_wins_over_the_pages_name()
    {
        Assert.Equal("Mine", Import(VisioFixtures.MultiPage(), title: "Mine").Document.Title);
    }

    [Theory]
    [InlineData(0, "Pages are numbered from 1")]
    [InlineData(4, "The file has 3 pages; page 4 does not exist")]
    public void A_page_the_file_does_not_have_is_the_callers_mistake_not_a_refused_source(int page, string message)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => Import(VisioFixtures.MultiPage(), page: page));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stencil_with_no_pages_is_an_empty_diagram_that_says_what_it_is()
    {
        List<KeyValuePair<string, byte[]>> parts = new VisioDocBuilder().Page("P", Shape(1, 1, 1, 1, 1)).Parts();
        byte[] stencil = VisioDocBuilder.Zip(parts.Where(p => !p.Key.StartsWith("visio/pages/", StringComparison.Ordinal)).Select(p => p.Key == "visio/_rels/document.xml.rels"
            ? new KeyValuePair<string, byte[]>(p.Key, Utf8(Relationships(("rId9", "windows", "windows.xml"))))
            : p));

        ImportResult result = Import(stencil);

        Assert.Empty(result.Document.Nodes);
        Assert.Contains("a Visio stencil holds shapes, not a drawing", Assert.Single(Warnings(result, ImportWarningCodes.EmptyDiagram)).Message, StringComparison.Ordinal);
        Assert.Empty(result.Pages!);
    }

    // ---- a Lucid-style file ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_file_with_no_masters_at_all_imports_from_its_own_geometry_and_explicit_line_ends()
    {
        ImportResult result = Import(VisioFixtures.MasterLess());

        Assert.Equal(["n_1", "n_2"], result.Document.Nodes.Select(n => n.Id).ToArray());
        Assert.Equal("decision", NodeOf(result, "n_2").Kind);
        Edge edge = EdgeOf(result, "e_3");
        Assert.Equal("n_1", edge.From.Node);
        Assert.Equal("n_2", edge.To.Node);
        Assert.Equal("flow", edge.Kind);
        Assert.Equal(RoutingMode.Straight, edge.Routing!.Mode);
        Assert.Empty(Warnings(result, ImportWarningCodes.MissingReference));
    }

    // ---- the result as a document ------------------------------------------------------------------------------------------------

    public static TheoryData<string, DocumentType> EveryFixtureInEveryType()
    {
        var data = new TheoryData<string, DocumentType>();
        foreach (string fixture in new[] { "Shapes", "Group", "Connectors", "MultiPage", "MasterLess", "Masters" })
        {
            foreach (DocumentType type in new[] { DocumentType.Flowchart, DocumentType.Architecture, DocumentType.Freeform })
            {
                data.Add(fixture, type);
            }
        }

        return data;
    }

    private static byte[] Fixture(string name)
    {
        return name switch
        {
            "Shapes" => VisioFixtures.Shapes(),
            "Group" => VisioFixtures.Group(),
            "Connectors" => VisioFixtures.Connectors(),
            "MultiPage" => VisioFixtures.MultiPage(),
            "MasterLess" => VisioFixtures.MasterLess(),
            "Masters" => VisioFixtures.Masters(),
            _ => throw new ArgumentException(name),
        };
    }

    [Theory]
    [MemberData(nameof(EveryFixtureInEveryType))]
    public void The_document_is_valid_against_the_schema_uses_only_kinds_its_type_has_and_refers_only_to_nodes_it_has(string fixture, DocumentType type)
    {
        Document document = Import(Fixture(fixture), type).Document;

        SchemaCheck schema = ContractFiles.Evaluate(DocumentJson.Serialize(document));
        Assert.True(schema.IsValid, ContractFiles.DescribeErrors(schema));
        var ids = document.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(document.Nodes.Count, ids.Count);
        Assert.Equal(document.Edges.Count, document.Edges.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(document.Edges, e => Assert.True(ids.Contains(e.From.Node) && ids.Contains(e.To.Node), $"{e.Id} refers to a node that is not there"));
        Assert.All(document.Nodes.Where(n => n.Parent is not null), n => Assert.Equal("container", document.Nodes.Single(p => p.Id == n.Parent).Kind));
        if (type != DocumentType.Freeform)
        {
            KindVocabulary vocabulary = KindVocabulary.For(type);
            Assert.All(document.Nodes, n => Assert.True(vocabulary.IsKnownNodeKind(n.Kind), $"{n.Id} has unknown kind {n.Kind}"));
            Assert.All(document.Edges, e => Assert.True(vocabulary.IsKnownEdgeKind(e.Kind), $"{e.Id} has unknown kind {e.Kind}"));
        }
    }

    [Theory]
    [MemberData(nameof(EveryFixtureInEveryType))]
    public void The_document_survives_being_printed_as_dsl_and_read_back(string fixture, DocumentType type)
    {
        Document document = Import(Fixture(fixture), type).Document;

        ParseResult back = Dsl.Parse(Dsl.Print(document));

        Assert.Equal(DocumentJson.Serialize(document), DocumentJson.Serialize(back.Document));
    }

    [Theory]
    [MemberData(nameof(EveryFixtureInEveryType))]
    public void Importing_the_same_file_twice_gives_the_same_nodes_edges_and_warnings(string fixture, DocumentType type)
    {
        ImportResult first = Import(Fixture(fixture), type);
        ImportResult second = Import(Fixture(fixture), type);

        Assert.Equal(DocumentJson.Serialize(first.Document with { Id = "x" }), DocumentJson.Serialize(second.Document with { Id = "x" }));
        Assert.Equal(first.Warnings, second.Warnings);
    }

    [Fact]
    public void Importing_never_lays_the_diagram_out_so_every_position_is_the_files_own()
    {
        Document document = Import(VisioFixtures.Group()).Document;

        Assert.All(document.Nodes, n => Assert.NotNull(n.Geometry));
        Assert.All(document.Edges, e => Assert.Null(e.Routing?.Waypoints));
    }

    [Fact]
    public void Warnings_are_capped_with_a_last_one_that_counts_the_rest()
    {
        // 600 connectors with a loose end each is 600 warnings, and the empty page makes the 601st.
        string shapes = string.Concat(Enumerable.Range(1, 600).Select(i => Connector(i, 1, 1, 2, 2, attributes: string.Empty)));

        ImportResult result = ImportPage(shapes);

        Assert.Equal(501, result.Warnings.Count);
        Assert.Contains("...and 101 more warnings like these were not listed.", result.Warnings[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stream_imports_the_same_as_the_bytes_and_a_stream_over_the_size_limit_is_refused_while_it_is_read()
    {
        byte[] file = VisioFixtures.Shapes();

        using var stream = new MemoryStream(file);
        Assert.Equal(10, VisioImporter.Import(stream, new VisioImportOptions { Now = s_now }).Document.Nodes.Count);

        using var tooBig = new MemoryStream(new byte[VisioImporter.MaxFileBytes + 1]);
        SourceRejectedException error = Assert.Throws<SourceRejectedException>(() => VisioImporter.Import(tooBig));
        Assert.Contains("larger than 3,000,000 bytes", error.Message, StringComparison.Ordinal);
    }
}
