using System.Net;
using System.Net.Sockets;
using System.Text;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Drawio;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// The draw.io importer on files shaped like real exports (tests/Draughtsman.Core.Tests/Import/DrawioFixtures) and on small XML for each
/// rule: what is carried, what degrades to a plain box with a warning, and what is left out and said so. Geometry arrives with the file,
/// so none of it is laid out.
/// </summary>
public class DrawioImporterTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static ImportResult Import(string source, DocumentType type = DocumentType.Flowchart, int page = 1, string? title = null)
    {
        return DrawioImporter.Import(source, new DrawioImportOptions { Type = type, Page = page, Title = title, Now = s_now });
    }

    private static ImportResult ImportFixture(string name, DocumentType type = DocumentType.Flowchart, int page = 1)
    {
        return Import(DrawioFixtures.Read(name), type, page);
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

    private static string Model(params string[] cells)
    {
        return DrawioXml.File(DrawioXml.Model(cells));
    }

    private static string Box(string id, string label = "", string style = "rounded=1;whiteSpace=wrap;html=1;", string parent = "1", double x = 0, double y = 0)
    {
        return DrawioXml.Vertex(id, label, style, x, y, 100, 50, parent);
    }

    // ---- the shapes fixture --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("s1", "process", "rectangle", "Plain box", 30, 30, 120, 60)]
    [InlineData("s2", "process", "rounded", "Rounded bold\ntwo lines & more", 190, 30, 140, 60)]
    [InlineData("s3", "terminal", "pill", "Oval", 370, 30, 120, 60)]
    [InlineData("s4", "decision", "diamond", "Is it?", 530, 20, 100, 80)]
    [InlineData("s5", "stored-data", "cylinder", "Orders DB", 30, 140, 80, 90)]
    [InlineData("s6", "process", "custom:generic/user", "Customer", 127, 122, 96, 96)]
    [InlineData("s7", "text", "text", "Just some text", 230, 150, 120, 30)]
    [InlineData("s8", "process", "hexagon", "Prepare", 390, 140, 120, 70)]
    [InlineData("s9", "io", "parallelogram", "Read input", 550, 140, 120, 60)]
    [InlineData("s10", "io", "document", "Report", 30, 270, 120, 80)]
    [InlineData("s11", "process", "rectangle", "Lambda fn", 190, 270, 60, 60)]
    [InlineData("s12", "process", "rectangle", "Logo", 290, 270, 64, 64)]
    [InlineData("s13", "process", "custom:generic/cloud", "Internet", 400, 252, 120, 96)]
    [InlineData("s14", "note", "custom:basic/note", "Remember to\nreview", 560, 270, 100, 80)]
    [InlineData("s15", "process", "rectangle", "Pointy", 30, 390, 60, 80)]
    [InlineData("s16", "process", "rounded", "Loud", 130, 390, 140, 70)]
    [InlineData("s17", "process", "rectangle", "Tilted", 310, 395, 100, 50)]
    [InlineData("s18", "process", "rectangle", "Themed", 450, 395, 100, 50)]
    [InlineData("s19", "process", "rectangle", "Safe text", 590, 395, 120, 50)]
    public void Each_shape_in_the_shapes_file_becomes_the_node_its_style_describes(string drawioId, string kind, string shape, string label, double x, double y, double w, double h)
    {
        ImportResult result = ImportFixture("shapes.drawio");

        Node node = NodeOf(result, "n_" + drawioId);

        Assert.Equal(kind, node.Kind);
        Assert.Equal(shape, node.Shape);
        Assert.Equal(label, node.Label);
        Assert.Equal(x, node.Geometry!.X);
        Assert.Equal(y, node.Geometry.Y);
        Assert.Equal(w, node.Geometry.W);
        Assert.Equal(h, node.Geometry.H);
        Assert.Null(node.Parent);
    }

    [Fact]
    public void The_shapes_file_flags_exactly_the_kinds_that_were_a_judgement_and_exactly_the_shapes_it_could_not_draw()
    {
        ImportResult result = ImportFixture("shapes.drawio");

        Assert.Equal(["n_s3", "n_s6", "n_s8", "n_s13"], Warnings(result, ImportWarningCodes.KindGuessed).Select(w => w.ElementId));
        Assert.Equal(["n_s11", "n_s12", "n_s15"], Warnings(result, ImportWarningCodes.UnsupportedSyntax).Select(w => w.ElementId));
        Assert.Equal(["n_s18"], Warnings(result, ImportWarningCodes.StyleApproximated).Select(w => w.ElementId));
        Assert.Equal(8, result.Warnings.Count);
        Assert.DoesNotContain(result.Warnings, w => w.Code is ImportWarningCodes.ParseError or ImportWarningCodes.MissingReference or ImportWarningCodes.EmptyDiagram);
    }

    [Fact]
    public void A_warning_says_what_the_shape_was_and_what_it_became()
    {
        ImportResult result = ImportFixture("shapes.drawio");

        string oval = Warnings(result, ImportWarningCodes.KindGuessed).Single(w => w.ElementId == "n_s3").Message;
        string stencil = Warnings(result, ImportWarningCodes.UnsupportedSyntax).Single(w => w.ElementId == "n_s11").Message;
        string picture = Warnings(result, ImportWarningCodes.UnsupportedSyntax).Single(w => w.ElementId == "n_s12").Message;
        string triangle = Warnings(result, ImportWarningCodes.UnsupportedSyntax).Single(w => w.ElementId == "n_s15").Message;

        Assert.Contains("\"Oval\"", oval, StringComparison.Ordinal);
        Assert.Contains("ellipse", oval, StringComparison.Ordinal);
        Assert.Contains("'terminal'", oval, StringComparison.Ordinal);
        Assert.Contains("mxgraph.aws4.lambda_function", stencil, StringComparison.Ordinal);
        Assert.Contains("plain box", stencil, StringComparison.Ordinal);
        Assert.Contains("picture", picture, StringComparison.Ordinal);
        Assert.Contains("triangle", triangle, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shape_s_colours_fonts_and_stroke_are_carried_and_only_those()
    {
        ImportResult result = ImportFixture("shapes.drawio");

        Assert.Null(NodeOf(result, "n_s1").Style);
        Assert.Equal(new NodeStyle { Fill = "#dae8fc", Stroke = "#6c8ebf" }, NodeOf(result, "n_s2").Style);
        Assert.Equal(
            new NodeStyle { Fill = "transparent", Stroke = "#FF0000", StrokeWidth = 3, TextColor = "#0000FF", Font = "Courier New", FontSize = 18, Bold = true, Dash = Dash.Dashed },
            NodeOf(result, "n_s16").Style);
    }

    [Fact]
    public void No_stroke_is_a_stroke_of_no_width_and_a_text_shape_s_empty_fill_and_stroke_are_left_as_the_kind_draws_them()
    {
        ImportResult result = ImportFixture("shapes.drawio");

        Assert.Equal(new NodeStyle { Fill = "#ED7100", StrokeWidth = 0, TextColor = "#232F3E", FontSize = 12 }, NodeOf(result, "n_s11").Style);
        Assert.Null(NodeOf(result, "n_s7").Style);
    }

    [Fact]
    public void A_colour_the_model_cannot_hold_is_dropped_with_a_warning_and_a_light_dark_pair_gives_its_light_half()
    {
        ImportResult result = ImportFixture("shapes.drawio");

        Assert.Equal(new NodeStyle { Fill = "#dae8fc" }, NodeOf(result, "n_s18").Style);
        ImportWarning warning = Assert.Single(Warnings(result, ImportWarningCodes.StyleApproximated));
        Assert.Contains("lightblue", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_icon_shape_is_given_room_for_its_label_about_the_same_centre_and_a_big_one_is_left_alone()
    {
        // A 30 x 60 stick figure would stack its label one letter per line: draw.io draws the label beneath at any width, an icon node here wraps it at the box's own.
        string source = Model(
            DrawioXml.Vertex("small", "Customer", "shape=umlActor;", 100, 200, 30, 60),
            DrawioXml.Vertex("big", "Visitor", "shape=umlActor;", 300, 200, 150, 140),
            DrawioXml.Vertex("plain", "Plain", "shape=hexagon;", 500, 200, 30, 60));

        ImportResult result = Import(source);

        Assert.Equal(new Geometry(67, 182, 96, 96), NodeOf(result, "n_small").Geometry);
        Assert.Equal(new Geometry(300, 200, 150, 140), NodeOf(result, "n_big").Geometry);
        Assert.Equal(new Geometry(500, 200, 30, 60), NodeOf(result, "n_plain").Geometry);
    }

    [Fact]
    public void A_rotated_shape_keeps_its_rotation()
    {
        Assert.Equal(45, NodeOf(ImportFixture("shapes.drawio"), "n_s17").Geometry!.Rotation);
    }

    [Fact]
    public void A_label_is_the_text_of_its_html_and_nothing_else_even_when_the_html_is_hostile()
    {
        ImportResult result = ImportFixture("shapes.drawio");

        Assert.Equal("Safe text", NodeOf(result, "n_s19").Label);
        Assert.DoesNotContain(result.Document.Nodes, n => n.Label.Contains('<', StringComparison.Ordinal) && n.Id == "n_s19");
    }

    [Fact]
    public void A_label_without_html_in_its_style_keeps_its_brackets_and_its_line_break()
    {
        ImportResult result = ImportFixture("shapes.drawio");

        Assert.Equal("Remember to\nreview", NodeOf(result, "n_s14").Label);
        string plain = Model(DrawioXml.Vertex("v", "a <b>not bold</b> &amp; c", "rounded=1;", 0, 0, 10, 10));
        Assert.Equal("a <b>not bold</b> &amp; c", NodeOf(Import(plain), "n_v").Label);
    }

    [Fact]
    public void In_an_architecture_document_the_same_shapes_take_architecture_kinds()
    {
        ImportResult result = ImportFixture("shapes.drawio", DocumentType.Architecture);

        Assert.Equal(("service", "rounded"), (NodeOf(result, "n_s2").Kind, NodeOf(result, "n_s2").Shape));
        Assert.Equal(("service", "pill"), (NodeOf(result, "n_s3").Kind, NodeOf(result, "n_s3").Shape));
        Assert.Equal(("service", "diamond"), (NodeOf(result, "n_s4").Kind, NodeOf(result, "n_s4").Shape));
        Assert.Equal(("database", "cylinder"), (NodeOf(result, "n_s5").Kind, NodeOf(result, "n_s5").Shape));
        Assert.Equal(("actor", "custom:generic/user"), (NodeOf(result, "n_s6").Kind, NodeOf(result, "n_s6").Shape));
        Assert.Equal(("queue", "hexagon"), (NodeOf(result, "n_s8").Kind, NodeOf(result, "n_s8").Shape));
        Assert.Equal(("queue", "parallelogram"), (NodeOf(result, "n_s9").Kind, NodeOf(result, "n_s9").Shape));
        Assert.Equal(("service", "document"), (NodeOf(result, "n_s10").Kind, NodeOf(result, "n_s10").Shape));
        Assert.Equal(("external", "custom:generic/cloud"), (NodeOf(result, "n_s13").Kind, NodeOf(result, "n_s13").Shape));
        Assert.Equal(("note", "custom:basic/note"), (NodeOf(result, "n_s14").Kind, NodeOf(result, "n_s14").Shape));
        Assert.Equal(["n_s3", "n_s4", "n_s8", "n_s9", "n_s13"], Warnings(result, ImportWarningCodes.KindGuessed).Select(w => w.ElementId));
    }

    [Fact]
    public void In_a_freeform_document_every_shape_is_a_shape_and_no_kind_is_guessed()
    {
        ImportResult result = ImportFixture("shapes.drawio", DocumentType.Freeform);

        Assert.Equal("text", NodeOf(result, "n_s7").Kind);
        Assert.All(result.Document.Nodes.Where(n => n.Id != "n_s7"), n => Assert.Equal("shape", n.Kind));
        Assert.Equal("pill", NodeOf(result, "n_s3").Shape);
        Assert.Equal("diamond", NodeOf(result, "n_s4").Shape);
        Assert.Equal("cylinder", NodeOf(result, "n_s5").Shape);
        Assert.Equal("custom:generic/user", NodeOf(result, "n_s6").Shape);
        Assert.Empty(Warnings(result, ImportWarningCodes.KindGuessed));
        Assert.Equal(3, Warnings(result, ImportWarningCodes.UnsupportedSyntax).Count);
    }

    [Theory]
    [InlineData("trapezoid;whiteSpace=wrap;html=1;", "process", "custom:basic/trapezoid")]
    [InlineData("shape=step;perimeter=stepPerimeter;whiteSpace=wrap;html=1;fixedSize=1;", "process", "custom:basic/chevron")]
    [InlineData("shape=plus;whiteSpace=wrap;html=1;", "process", "custom:basic/cross")]
    [InlineData("shape=cross;whiteSpace=wrap;html=1;", "process", "custom:basic/cross")]
    [InlineData("shape=singleArrow;whiteSpace=wrap;html=1;", "process", "custom:basic/arrow")]
    [InlineData("shape=doubleArrow;whiteSpace=wrap;html=1;", "process", "custom:basic/double-arrow")]
    [InlineData("shape=callout;whiteSpace=wrap;html=1;", "note", "custom:basic/callout")]
    [InlineData("shape=cube;whiteSpace=wrap;html=1;", "process", "custom:basic/cube")]
    [InlineData("shape=folder;whiteSpace=wrap;html=1;", "process", "custom:basic/folder")]
    [InlineData("shape=component;whiteSpace=wrap;html=1;", "process", "custom:basic/component")]
    [InlineData("shape=module;whiteSpace=wrap;html=1;", "process", "custom:basic/component")]
    [InlineData("triangle;whiteSpace=wrap;html=1;direction=north;", "process", "custom:basic/triangle")]
    public void A_shape_the_basic_library_has_arrives_as_that_shape_with_nothing_to_warn_about(string style, string kind, string shape)
    {
        ImportResult result = Import(DrawioXml.File(DrawioXml.Model(DrawioXml.Vertex("a", "Thing", style, 10, 20, 100, 60))));

        Node node = NodeOf(result, "n_a");
        Assert.Equal((kind, shape), (node.Kind, node.Shape));
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("triangle;whiteSpace=wrap;html=1;", "triangle")]
    [InlineData("shape=step;direction=south;whiteSpace=wrap;html=1;", "step")]
    [InlineData("shape=singleArrow;direction=west;whiteSpace=wrap;html=1;", "singleArrow")]
    [InlineData("shape=curlyBracket;whiteSpace=wrap;html=1;", "curlyBracket")]
    public void A_shape_draw_io_turns_or_draws_the_other_way_round_stays_a_named_plain_box(string style, string name)
    {
        ImportResult result = Import(DrawioXml.File(DrawioXml.Model(DrawioXml.Vertex("a", "Thing", style, 10, 20, 100, 60))));

        Assert.Equal(("process", "rectangle"), (NodeOf(result, "n_a").Kind, NodeOf(result, "n_a").Shape));
        ImportWarning warning = Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax));
        Assert.Contains($"'{name}'", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_basic_shape_takes_the_kind_of_the_document_type_and_a_freeform_document_guesses_none()
    {
        string source = DrawioXml.File(DrawioXml.Model(DrawioXml.Vertex("a", "Thing", "shape=cube;whiteSpace=wrap;html=1;", 10, 20, 100, 60)));

        Assert.Equal(("service", "custom:basic/cube"), (NodeOf(Import(source, DocumentType.Architecture), "n_a").Kind, NodeOf(Import(source, DocumentType.Architecture), "n_a").Shape));
        Assert.Equal(("shape", "custom:basic/cube"), (NodeOf(Import(source, DocumentType.Freeform), "n_a").Kind, NodeOf(Import(source, DocumentType.Freeform), "n_a").Shape));
    }

    [Fact]
    public void A_folder_that_holds_shapes_stays_a_folder_as_a_container()
    {
        ImportResult result = Import(DrawioXml.File(DrawioXml.Model(
            DrawioXml.Vertex("pkg", "Orders", "shape=folder;whiteSpace=wrap;html=1;container=1;", 0, 0, 300, 200),
            DrawioXml.Vertex("a", "Order", "rounded=1;whiteSpace=wrap;html=1;", 20, 50, 100, 60, parent: "pkg"))));

        Node package = NodeOf(result, "c_pkg");
        Assert.Equal(("container", "custom:basic/folder"), (package.Kind, package.Shape));
        Assert.Equal("c_pkg", NodeOf(result, "n_a").Parent);
    }

    // ---- containers ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Containers_hold_their_children_and_geometry_is_absolute()
    {
        ImportResult result = ImportFixture("containers.drawio");

        (string Id, string? Parent, double X, double Y, double W, double H)[] expected =
        [
            ("c_c1", null, 40, 40, 400, 300),
            ("n_c1a", "c_c1", 60, 90, 120, 60),
            ("n_c1b", "c_c1", 300, 85, 100, 70),
            ("c_c2", "c_c1", 60, 180, 360, 140),
            ("n_c2a", "c_c2", 80, 220, 100, 60),
            ("c_g1", null, 500, 40, 200, 80),
            ("n_g1a", "c_g1", 500, 40, 90, 80),
            ("n_g1b", "c_g1", 610, 40, 90, 80),
            ("c_p1", null, 500, 180, 200, 120),
            ("n_p1a", "c_p1", 540, 220, 120, 30),
            ("n_obj1", null, 40, 380, 160, 60),
            ("n_obj2", null, 240, 380, 160, 60),
        ];
        Assert.Equal(expected.Length, result.Document.Nodes.Count);
        foreach ((string id, string? parent, double x, double y, double w, double h) in expected)
        {
            Node node = NodeOf(result, id);
            Assert.Equal(parent, node.Parent);
            Assert.Equal(new Geometry(x, y, w, h), node.Geometry);
        }
    }

    [Fact]
    public void A_swimlane_a_group_and_a_shape_with_children_are_all_containers_and_only_the_last_is_flagged()
    {
        ImportResult result = ImportFixture("containers.drawio");

        Assert.All(["c_c1", "c_c2", "c_g1", "c_p1"], id => Assert.Equal("container", NodeOf(result, id).Kind));
        Assert.Equal("Backend", NodeOf(result, "c_c1").Label);
        Assert.Equal(string.Empty, NodeOf(result, "c_g1").Label);
        Assert.Equal("#f5f5f5", NodeOf(result, "c_c1").Style!.Fill);
        ImportWarning guessed = Assert.Single(Warnings(result, ImportWarningCodes.KindGuessed));
        Assert.Equal("c_p1", guessed.ElementId);
        Assert.Contains("container", guessed.Message, StringComparison.Ordinal);
        Assert.Equal("text", NodeOf(result, "n_p1a").Kind);
    }

    [Fact]
    public void A_parent_is_always_written_before_its_children_so_it_is_drawn_underneath_them()
    {
        ImportResult result = ImportFixture("containers.drawio");
        List<string> order = result.Document.Nodes.Select(n => n.Id).ToList();

        Assert.All(result.Document.Nodes.Where(n => n.Parent is not null), n => Assert.True(order.IndexOf(n.Parent!) < order.IndexOf(n.Id), $"{n.Id} comes before its parent {n.Parent}"));
    }

    [Fact]
    public void A_shape_s_custom_data_is_kept_as_props_and_its_placeholders_are_filled_in()
    {
        ImportResult result = ImportFixture("containers.drawio");

        Node payments = NodeOf(result, "n_obj1");

        Assert.Equal("Payments service", payments.Label);
        Assert.Equal("Payments", payments.Props!["owner"].GetString());
        Assert.Equal("PAY-42", payments.Props["ticket"].GetString());
        Assert.Equal("Owned by payments", payments.Props["tooltip"].GetString());
        Assert.Equal("https://example.test/wiki/payments", payments.Props["url"].GetString());
        Assert.DoesNotContain("placeholders", payments.Props.Keys);
        Assert.DoesNotContain("link", payments.Props.Keys);
    }

    [Fact]
    public void A_link_that_is_not_a_web_or_mail_address_is_dropped_and_said_so()
    {
        ImportResult result = ImportFixture("containers.drawio");

        Assert.Null(NodeOf(result, "n_obj2").Props);
        ImportWarning warning = Assert.Single(Warnings(result, ImportWarningCodes.UnsupportedSyntax), w => w.ElementId == "n_obj2");
        Assert.Contains("link", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Hidden_shapes_and_hidden_layers_are_left_out_and_counted_in_one_warning()
    {
        ImportResult result = ImportFixture("containers.drawio");

        Assert.DoesNotContain(result.Document.Nodes, n => n.Label is "Secret" or "Hidden on its own");
        ImportWarning warning = Assert.Single(result.Warnings, w => w.Message.Contains("hidden", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ImportWarningCodes.UnsupportedSyntax, warning.Code);
        Assert.Contains("2 ", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void More_than_one_visible_layer_is_flattened_and_said_so()
    {
        string source = DrawioXml.File(DrawioXml.Model(
            "<mxCell id=\"L2\" value=\"Second\" parent=\"0\"/>",
            Box("a", "A"),
            Box("b", "B", parent: "L2")));

        ImportResult result = Import(source);

        Assert.Equal(2, result.Document.Nodes.Count);
        Assert.Contains(result.Warnings, w => w.Message.Contains("layers", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_shape_listed_before_the_shape_it_sits_in_is_still_written_after_it()
    {
        // Hand-edited XML and some exporters list a child first; the container must still come first so it is drawn underneath.
        string source = Model(
            Box("lone", "Lone", x: 500),
            Box("kid", "Kid", parent: "box", x: 10, y: 10),
            DrawioXml.Vertex("box", "Box", "swimlane;", 100, 100, 300, 200));

        ImportResult result = Import(source);

        Assert.Equal(["n_lone", "c_box", "n_kid"], result.Document.Nodes.Select(n => n.Id));
        Assert.Equal("c_box", NodeOf(result, "n_kid").Parent);
        Assert.Equal(new Geometry(110, 110, 100, 50), NodeOf(result, "n_kid").Geometry);
    }

    [Fact]
    public void A_cycle_of_parents_is_broken_and_nothing_loops()
    {
        string source = Model(
            Box("a", "A", parent: "c"),
            Box("b", "B", parent: "a"),
            Box("c", "C", parent: "b"),
            Box("self", "Self", parent: "self"));

        ImportResult result = Import(source);

        Assert.Equal(4, result.Document.Nodes.Count);
        Assert.Equal(2, Warnings(result, ImportWarningCodes.UnsupportedSyntax).Count(w => w.Message.Contains("parent", StringComparison.OrdinalIgnoreCase)));
        Assert.Null(result.Document.Nodes.Single(n => n.Label == "Self").Parent);
        // Whatever is left is a tree: following Parent from any node reaches the top.
        foreach (Node node in result.Document.Nodes)
        {
            string? current = node.Parent;
            for (int steps = 0; current is not null; steps++)
            {
                Assert.True(steps < 10, $"{node.Id} never reaches the top");
                current = result.Document.Nodes.Single(n => n.Id == current).Parent;
            }
        }
    }

    [Fact]
    public void A_parent_that_is_not_in_the_file_leaves_the_shape_at_the_top_with_a_warning()
    {
        ImportResult result = Import(Model(Box("a", "A", parent: "ghost", x: 5, y: 6)));

        Node node = NodeOf(result, "n_a");
        Assert.Null(node.Parent);
        Assert.Equal(new Geometry(5, 6, 100, 50), node.Geometry);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.MissingReference && w.ElementId == "n_a");
    }

    // ---- edges --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Edges_join_the_shapes_they_were_attached_to_and_keep_their_labels()
    {
        ImportResult result = ImportFixture("edges.drawio");

        Edge go = EdgeOf(result, "e_e1");
        Assert.Equal(("n_A", "n_B", "go", "flow"), (go.From.Node, go.To.Node, go.Label, go.Kind));
        Assert.Equal(("n_B", "n_C", "retry"), (EdgeOf(result, "e_e2").From.Node, EdgeOf(result, "e_e2").To.Node, EdgeOf(result, "e_e2").Label));
        Assert.Equal("c_X", EdgeOf(result, "e_e6").To.Node);
        Assert.Equal("n_X1", EdgeOf(result, "e_e7").From.Node);
        Assert.Equal(9, result.Document.Edges.Count);
    }

    [Fact]
    public void An_edge_label_that_is_a_cell_of_its_own_becomes_the_edge_label_and_is_not_a_node()
    {
        ImportResult result = ImportFixture("edges.drawio");

        Assert.Equal("retry", EdgeOf(result, "e_e2").Label);
        Assert.DoesNotContain(result.Document.Nodes, n => n.Id.Contains("e2", StringComparison.Ordinal));
        Assert.Equal(6, result.Document.Nodes.Count);
    }

    [Fact]
    public void A_second_label_on_an_edge_is_not_carried_and_the_edge_says_so()
    {
        string source = DrawioXml.File(DrawioXml.Model(
            Box("a", "A"),
            Box("b", "B", x: 200),
            DrawioXml.Edge("e", "a", "b", "first"),
            "<mxCell id=\"l1\" value=\"second\" style=\"edgeLabel;html=1;\" vertex=\"1\" connectable=\"0\" parent=\"e\"><mxGeometry relative=\"1\" as=\"geometry\"/></mxCell>"));

        ImportResult result = Import(source);

        Assert.Equal("first", EdgeOf(result, "e_e").Label);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.UnsupportedSyntax && w.ElementId == "e_e" && w.Message.Contains("second", StringComparison.Ordinal));
    }

    [Fact]
    public void An_edge_s_routing_is_the_mode_its_style_names_and_only_straight_and_curved_edges_keep_their_bends()
    {
        ImportResult result = ImportFixture("edges.drawio");

        Assert.Equal(new Routing(RoutingMode.Orthogonal), EdgeOf(result, "e_e1").Routing);
        Assert.Equal(new Routing(RoutingMode.Straight), EdgeOf(result, "e_e2").Routing);

        Routing curved = EdgeOf(result, "e_e4").Routing!;
        Assert.Equal(RoutingMode.Curved, curved.Mode);
        Assert.Equal([new Point(20, 150)], curved.Waypoints);

        Routing bent = EdgeOf(result, "e_e7").Routing!;
        Assert.Equal(RoutingMode.Straight, bent.Mode);
        Assert.Equal([new Point(420, 290), new Point(420, 130)], bent.Waypoints);
    }

    [Fact]
    public void Arrowheads_and_dashes_are_carried_as_the_difference_from_what_the_kind_already_draws()
    {
        ImportResult result = ImportFixture("edges.drawio");

        Assert.Null(EdgeOf(result, "e_e1").Style);
        Assert.Equal(new EdgeStyle { Stroke = "#FF0000", StrokeWidth = 2, Dash = Dash.Dashed, ArrowEnd = ArrowHead.Open }, EdgeOf(result, "e_e2").Style);
        Assert.Null(EdgeOf(result, "e_e3").Style);
        Assert.Equal(new EdgeStyle { ArrowStart = ArrowHead.Aggregation, ArrowEnd = ArrowHead.Circle }, EdgeOf(result, "e_e4").Style);
        Assert.Equal(new EdgeStyle { ArrowStart = ArrowHead.OneAndOnlyOne, ArrowEnd = ArrowHead.OneOrMany }, EdgeOf(result, "e_e5").Style);
        Assert.Equal(new EdgeStyle { ArrowEnd = ArrowHead.Inheritance }, EdgeOf(result, "e_e6").Style);
        Assert.Null(EdgeOf(result, "e_e7").Style);
    }

    [Fact]
    public void A_line_with_no_arrowheads_or_with_a_notation_head_is_an_association_and_any_other_is_a_flow()
    {
        ImportResult result = ImportFixture("edges.drawio");

        Assert.Equal("association", EdgeOf(result, "e_e3").Kind);
        Assert.Equal("association", EdgeOf(result, "e_e4").Kind);
        Assert.Equal("association", EdgeOf(result, "e_e5").Kind);
        Assert.Equal("association", EdgeOf(result, "e_e6").Kind);
        Assert.Equal("flow", EdgeOf(result, "e_e1").Kind);
        Assert.Equal("flow", EdgeOf(result, "e_e2").Kind);
        Assert.Equal("flow", EdgeOf(result, "e_e7").Kind);
    }

    [Fact]
    public void An_arrowhead_with_no_equivalent_becomes_the_nearest_and_the_edge_says_so_once_per_head()
    {
        ImportResult result = ImportFixture("edges.drawio");

        Assert.Equal(new EdgeStyle { ArrowStart = ArrowHead.Arrow }, EdgeOf(result, "e_e10").Style);
        List<ImportWarning> approximated = Warnings(result, ImportWarningCodes.StyleApproximated);
        Assert.Equal(2, approximated.Count);
        Assert.All(approximated, w => Assert.Equal("e_e10", w.ElementId));
        Assert.Contains(approximated, w => w.Message.Contains("box", StringComparison.Ordinal));
        Assert.Contains(approximated, w => w.Message.Contains("halfCircle", StringComparison.Ordinal));
    }

    [Fact]
    public void An_edge_that_is_not_attached_at_both_ends_to_a_shape_that_was_imported_is_left_out_with_a_missing_reference()
    {
        ImportResult result = ImportFixture("edges.drawio");

        Assert.DoesNotContain(result.Document.Edges, e => e.Id is "e_e8" or "e_e9");
        List<ImportWarning> missing = Warnings(result, ImportWarningCodes.MissingReference);
        Assert.Equal(2, missing.Count);
        Assert.Contains(missing, w => w.Message.Contains("loose end", StringComparison.OrdinalIgnoreCase) && w.Message.Contains("e8", StringComparison.Ordinal));
        Assert.Contains(missing, w => w.Message.Contains("does-not-exist", StringComparison.Ordinal));
    }

    [Fact]
    public void An_edge_from_a_shape_to_itself_is_kept()
    {
        Edge loop = EdgeOf(ImportFixture("edges.drawio"), "e_e11");

        Assert.Equal("n_D", loop.From.Node);
        Assert.Equal("n_D", loop.To.Node);
        Assert.Equal("to itself", loop.Label);
    }

    [Fact]
    public void An_edge_to_a_hidden_shape_or_to_another_edge_is_a_missing_reference()
    {
        string source = Model(
            Box("a", "A"),
            DrawioXml.Vertex("h", "Hidden", string.Empty, 0, 0, 1, 1).Replace("vertex=\"1\"", "vertex=\"1\" visible=\"0\"", StringComparison.Ordinal),
            DrawioXml.Edge("e1", "a", "h"),
            DrawioXml.Edge("e2", "a", "a"),
            DrawioXml.Edge("e3", "a", "e2"));

        ImportResult result = Import(source);

        Assert.Equal(["e_e2"], result.Document.Edges.Select(e => e.Id));
        Assert.Equal(2, Warnings(result, ImportWarningCodes.MissingReference).Count);
    }

    [Fact]
    public void In_an_architecture_document_a_dashed_flow_is_a_dependency_and_a_flow_to_a_database_is_data()
    {
        string source = Model(
            Box("svc", "Service"),
            DrawioXml.Vertex("db", "Store", "shape=cylinder3;", 300, 0, 80, 80),
            Box("other", "Other", x: 600),
            DrawioXml.Edge("dep", "svc", "other", style: "endArrow=classic;dashed=1;"),
            DrawioXml.Edge("rw", "svc", "db", style: "endArrow=classic;"),
            DrawioXml.Edge("plain", "svc", "other", style: "endArrow=classic;"));

        ImportResult result = Import(source, DocumentType.Architecture);

        Edge dependency = EdgeOf(result, "e_dep");
        Assert.Equal("dependency", dependency.Kind);
        Assert.Equal(new EdgeStyle { ArrowEnd = ArrowHead.Arrow }, dependency.Style);
        Edge data = EdgeOf(result, "e_rw");
        Assert.Equal("data", data.Kind);
        Assert.Equal(new EdgeStyle { ArrowEnd = ArrowHead.Arrow }, data.Style);
        Assert.Equal("flow", EdgeOf(result, "e_plain").Kind);
        Assert.Null(EdgeOf(result, "e_plain").Style);
    }

    // ---- pages, titles, empties --------------------------------------------------------------------------------------------------

    [Fact]
    public void A_compressed_file_imports_to_the_same_diagram_as_the_plain_page_inside_it()
    {
        ImportResult compressed = ImportFixture("compressed.drawio");

        string payload = System.Xml.Linq.XDocument.Parse(DrawioFixtures.Read("compressed.drawio")).Descendants("diagram").Single().Value.Trim();
        using var inflater = new System.IO.Compression.DeflateStream(new MemoryStream(Convert.FromBase64String(payload)), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(inflater, Encoding.UTF8);
        string plainModel = Uri.UnescapeDataString(reader.ReadToEnd());
        ImportResult plain = Import(DrawioXml.File(plainModel), title: "Order flow");

        Assert.Equal(DocumentJson.Serialize(plain.Document with { Id = "x" }), DocumentJson.Serialize(compressed.Document with { Id = "x" }));
        Assert.Equal(plain.Warnings.Select(w => (w.Code, w.Message)), compressed.Warnings.Select(w => (w.Code, w.Message)));
    }

    [Fact]
    public void The_compressed_flowchart_has_its_shapes_labels_and_branches()
    {
        ImportResult result = ImportFixture("compressed.drawio");

        Assert.Equal("Order flow", result.Document.Title);
        Assert.Equal(["n_start", "n_check", "n_ship", "n_reject", "n_end"], result.Document.Nodes.Select(n => n.Id));
        Assert.Equal("Reject & refund", NodeOf(result, "n_reject").Label);
        Assert.Equal(new NodeStyle { Fill = "#f8cecc", Stroke = "#b85450" }, NodeOf(result, "n_reject").Style);
        Assert.Equal(["", "Yes", "No", "", ""], result.Document.Edges.Select(e => e.Label ?? string.Empty));
        Assert.Equal(["n_start", "n_end"], Warnings(result, ImportWarningCodes.KindGuessed).Select(w => w.ElementId));
    }

    [Fact]
    public void The_first_page_of_a_multi_page_file_is_imported_by_default_and_the_warning_says_which_and_how_many_were_not()
    {
        ImportResult result = ImportFixture("multipage.drawio");

        Assert.Equal(["n_a", "n_b"], result.Document.Nodes.Select(n => n.Id));
        Assert.Equal("Overview", result.Document.Title);
        ImportWarning warning = Assert.Single(Warnings(result, ImportWarningCodes.PageSelected));
        Assert.Contains("Page 1", warning.Message, StringComparison.Ordinal);
        Assert.Contains("\"Overview\"", warning.Message, StringComparison.Ordinal);
        Assert.Contains("other 2 pages", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_result_lists_every_page_and_marks_the_one_that_was_imported()
    {
        ImportResult first = ImportFixture("multipage.drawio");
        ImportResult second = ImportFixture("multipage.drawio", page: 2);

        Assert.Equal(["Overview", "Details", "Scratch"], first.Pages!.Select(p => p.Name));
        Assert.Equal([1, 2, 3], first.Pages!.Select(p => p.Index));
        Assert.Equal([true, false, false], first.Pages!.Select(p => p.Selected));
        Assert.Equal([false, true, false], second.Pages!.Select(p => p.Selected));
    }

    [Fact]
    public void A_later_page_is_imported_when_asked_for_even_when_it_is_compressed()
    {
        ImportResult result = ImportFixture("multipage.drawio", page: 2);

        Assert.Equal("Details", result.Document.Title);
        Assert.Equal(["n_d1", "n_d2", "n_d3"], result.Document.Nodes.Select(n => n.Id));
        Assert.Equal(["stored-data", "decision"], result.Document.Nodes.Skip(1).Select(n => n.Kind));
        Assert.Equal("maybe", EdgeOf(result, "e_d23").Label);
        Assert.Contains("Page 2", Assert.Single(Warnings(result, ImportWarningCodes.PageSelected)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_page_is_an_empty_document_and_a_warning_not_a_failure()
    {
        ImportResult result = ImportFixture("multipage.drawio", page: 3);

        Assert.Empty(result.Document.Nodes);
        Assert.Empty(result.Document.Edges);
        Assert.Single(Warnings(result, ImportWarningCodes.EmptyDiagram));
    }

    [Fact]
    public void A_page_the_file_does_not_have_is_an_argument_error()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => ImportFixture("multipage.drawio", page: 4));

        Assert.Contains("3 pages", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_page_file_has_no_page_warning_and_a_bare_model_is_one_page_with_the_default_title()
    {
        ImportResult plain = ImportFixture("shapes.drawio");
        ImportResult bare = ImportFixture("bare-model.xml");

        Assert.Empty(Warnings(plain, ImportWarningCodes.PageSelected));
        Assert.Equal("Shapes", plain.Document.Title);
        Assert.Empty(Warnings(bare, ImportWarningCodes.PageSelected));
        Assert.Equal("Imported diagram", bare.Document.Title);
        Assert.Equal(["n_x1", "n_x2"], bare.Document.Nodes.Select(n => n.Id));
        Assert.Equal("e_xe", Assert.Single(bare.Document.Edges).Id);
        Assert.Single(bare.Pages!);
    }

    [Theory]
    [InlineData("Page-1")]
    [InlineData("Page-12")]
    [InlineData("")]
    public void The_title_is_the_page_name_unless_the_name_is_draw_io_s_own_placeholder(string name)
    {
        string source = $"<mxfile><diagram name=\"{name}\">{DrawioXml.Model(Box("a", "A"))}</diagram></mxfile>";

        Assert.Equal("Imported diagram", Import(source).Document.Title);
    }

    [Fact]
    public void A_title_the_caller_gives_wins()
    {
        Assert.Equal("Mine", Import(DrawioFixtures.Read("multipage.drawio"), title: "Mine").Document.Title);
    }

    [Fact]
    public void A_file_with_no_pages_and_a_page_with_no_cells_are_empty_documents()
    {
        ImportResult noPages = Import("<mxfile host=\"x\"/>");
        ImportResult noCells = Import(DrawioXml.File(DrawioXml.Model()));

        Assert.Empty(noPages.Document.Nodes);
        Assert.Contains(noPages.Warnings, w => w.Code == ImportWarningCodes.EmptyDiagram);
        Assert.Empty(noCells.Document.Nodes);
        Assert.Contains(noCells.Warnings, w => w.Code == ImportWarningCodes.EmptyDiagram);
    }

    // ---- what the document is ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_is_laid_out_and_nothing_is_hinted()
    {
        ImportResult result = ImportFixture("containers.drawio");

        Assert.Null(result.Direction);
        Assert.All(result.Document.Nodes, n => Assert.NotNull(n.Geometry));
        Assert.Equal(DocumentType.Flowchart, result.Document.Type);
        Assert.Equal("drawio-import", result.Document.Meta.CreatedBy);
        Assert.Equal(s_now, result.Document.Meta.CreatedAt);
        Assert.StartsWith("doc_", result.Document.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sequence_document_cannot_be_made_from_a_drawing()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => Import(Model(Box("a")), DocumentType.Sequence));

        Assert.Contains("sequence", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Parameter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_that_is_not_draw_io_is_refused()
    {
        Assert.Throws<SourceRejectedException>(() => Import("graph TD\n  A --> B"));
        Assert.Throws<SourceRejectedException>(() => Import("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"));
    }

    public static TheoryData<string, DocumentType> EveryFixtureInEveryType()
    {
        var data = new TheoryData<string, DocumentType>();
        foreach (string name in new[] { "shapes.drawio", "containers.drawio", "edges.drawio", "compressed.drawio", "multipage.drawio", "bare-model.xml" })
        {
            foreach (DocumentType type in new[] { DocumentType.Flowchart, DocumentType.Architecture, DocumentType.Freeform })
            {
                data.Add(name, type);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryFixtureInEveryType))]
    public void The_document_is_valid_against_the_schema_uses_only_kinds_its_type_has_and_refers_only_to_nodes_it_has(string fixture, DocumentType type)
    {
        Document document = ImportFixture(fixture, type).Document;

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
        Document document = ImportFixture(fixture, type).Document;

        ParseResult back = Dsl.Parse(Dsl.Print(document));

        Assert.Equal(DocumentJson.Serialize(document), DocumentJson.Serialize(back.Document));
    }

    [Theory]
    [MemberData(nameof(EveryFixtureInEveryType))]
    public void Importing_the_same_file_twice_gives_the_same_nodes_edges_and_warnings(string fixture, DocumentType type)
    {
        ImportResult first = ImportFixture(fixture, type);
        ImportResult second = ImportFixture(fixture, type);

        Assert.Equal(DocumentJson.Serialize(first.Document with { Id = "x" }), DocumentJson.Serialize(second.Document with { Id = "x" }));
        Assert.Equal(first.Warnings, second.Warnings);
    }

    // ---- ids, numbers, labels, limits ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_source_id_that_the_document_cannot_hold_is_renamed_uniquely_and_said_so()
    {
        string source = Model(Box("a b", "One"), Box("a_b", "Two"), Box("-3", "Three"), Box("café", "Four"), Box(new string('x', 200), "Long"));

        ImportResult result = Import(source);

        Assert.Equal(["n_a_b", "n_a_b_2", "n_-3", "n_caf_", $"n_{new string('x', 126)}"], result.Document.Nodes.Select(n => n.Id));
        Assert.Equal(["n_a_b", "n_caf_", $"n_{new string('x', 126)}"], Warnings(result, ImportWarningCodes.IdRenamed).Select(w => w.ElementId).Where(id => id != "n_a_b_2"));
        Assert.Contains(Warnings(result, ImportWarningCodes.IdRenamed), w => w.ElementId == "n_a_b_2" && w.Message.Contains("draw.io", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("Mermaid", StringComparison.Ordinal));
    }

    [Fact]
    public void A_second_cell_with_an_id_already_used_is_skipped_and_the_first_keeps_the_edges()
    {
        string source = Model(Box("a", "First"), Box("a", "Second", x: 300), Box("b", "B", x: 600), DrawioXml.Edge("e", "a", "b"));

        ImportResult result = Import(source);

        Assert.Equal(["First", "B"], result.Document.Nodes.Select(n => n.Label));
        Assert.Equal("n_a", Assert.Single(result.Document.Edges).From.Node);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.UnsupportedSyntax && w.Message.Contains("more than one cell", StringComparison.Ordinal));
    }

    [Fact]
    public void A_geometry_the_canvas_could_not_hold_is_dropped_with_a_warning_and_a_negative_size_is_its_size()
    {
        string source = Model(
            "<mxCell id=\"far\" value=\"Far\" style=\"\" vertex=\"1\" parent=\"1\"><mxGeometry x=\"1e12\" y=\"0\" width=\"10\" height=\"10\" as=\"geometry\"/></mxCell>",
            "<mxCell id=\"neg\" value=\"Flipped\" style=\"\" vertex=\"1\" parent=\"1\"><mxGeometry x=\"100\" y=\"100\" width=\"-40\" height=\"-20\" as=\"geometry\"/></mxCell>",
            "<mxCell id=\"none\" value=\"No position\" style=\"\" vertex=\"1\" parent=\"1\"/>");

        ImportResult result = Import(source);

        Assert.Null(NodeOf(result, "n_far").Geometry);
        Assert.Equal(new Geometry(100, 100, 40, 20), NodeOf(result, "n_neg").Geometry);
        Assert.Null(NodeOf(result, "n_none").Geometry);
        Assert.Equal(["n_far", "n_none"], Warnings(result, ImportWarningCodes.UnsupportedSyntax).Select(w => w.ElementId));
    }

    [Fact]
    public void A_label_longer_than_the_limit_is_cut_and_the_shape_says_so()
    {
        ImportResult result = Import(Model(Box("a", new string('w', DrawioLimits.MaxLabelLength + 500))));

        Assert.Equal(DrawioLimits.MaxLabelLength, NodeOf(result, "n_a").Label.Length);
        Assert.Contains(result.Warnings, w => w.ElementId == "n_a" && w.Message.Contains("shortened", StringComparison.Ordinal));
    }

    [Fact]
    public void Custom_data_is_kept_up_to_a_limit_and_a_long_value_is_cut()
    {
        string attributes = $" big=\"{new string('b', DrawioLimits.MaxPropLength + 5)}\"" + string.Concat(Enumerable.Range(0, DrawioLimits.MaxProps + 10).Select(i => $" k{i}=\"v{i}\""));
        string source = Model($"<object label=\"Data\" id=\"d\"{attributes}><mxCell style=\"\" vertex=\"1\" parent=\"1\"><mxGeometry width=\"10\" height=\"10\" as=\"geometry\"/></mxCell></object>");

        Node node = NodeOf(Import(source), "n_d");

        Assert.Equal(DrawioLimits.MaxProps, node.Props!.Count);
        Assert.All(node.Props.Values, v => Assert.True(v.GetString()!.Length <= DrawioLimits.MaxPropLength));
    }

    [Theory]
    [InlineData("fillColor=#fff", "#fff")]
    [InlineData("fillColor=#FFFFFF", "#FFFFFF")]
    [InlineData("fillColor=#ffffff80", "#ffffff80")]
    [InlineData("fillColor=none", "transparent")]
    [InlineData("fillColor=default", null)]
    [InlineData("fillColor=inherit", null)]
    [InlineData("fillColor=rgb(1,2,3)", null)]
    [InlineData("fillColor=url(javascript:alert(1))", null)]
    [InlineData("fillColor=red;", null)]
    [InlineData("fillColor=#12345", null)]
    [InlineData("fillColor=#xyz", null)]
    [InlineData("fillColor=light-dark(#ffffff,#000000)", "#ffffff")]
    [InlineData("fillColor=light-dark(red,blue)", null)]
    public void Only_hex_colours_none_and_the_light_half_of_a_light_dark_pair_are_carried(string style, string? expected)
    {
        ImportResult result = Import(Model(DrawioXml.Vertex("v", "V", "rounded=1;" + style, 0, 0, 10, 10)));

        Assert.Equal(expected, NodeOf(result, "n_v").Style?.Fill);
    }

    [Fact]
    public void More_warnings_than_the_limit_are_summarised_in_a_last_one()
    {
        int shapes = DrawioLimits.MaxWarnings + 200;
        string source = Model(string.Concat(Enumerable.Range(0, shapes).Select(i => DrawioXml.Vertex($"s{i}", "x", "shape=mxgraph.aws4.group;", i, 0, 10, 10))));

        ImportResult result = Import(source);

        Assert.Equal(shapes, result.Document.Nodes.Count);
        Assert.Equal(DrawioLimits.MaxWarnings + 1, result.Warnings.Count);
        Assert.Contains("200 more", result.Warnings[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_at_the_shape_and_connector_limit_imports_in_reasonable_time_and_keeps_every_one()
    {
        // Half the limit in shapes and half in connectors, as one long chain: every lookup (parent, endpoint, id) must be a hash lookup, not a scan.
        int shapes = DrawioLimits.MaxCells / 2;
        var cells = new List<string>(DrawioLimits.MaxCells);
        for (int i = 0; i < shapes; i++)
        {
            cells.Add(DrawioXml.Vertex($"s{i}", $"Shape {i}", "rounded=1;html=1;", i % 100 * 120, i / 100 * 80, 100, 50));
        }

        for (int i = 1; i < shapes; i++)
        {
            cells.Add(DrawioXml.Edge($"e{i}", $"s{i - 1}", $"s{i}", style: "endArrow=classic;html=1;"));
        }

        string source = Model([.. cells]);
        TimeSpan budget = LoadScale.Scaled(TimeSpan.FromSeconds(5));

        var timer = System.Diagnostics.Stopwatch.StartNew();
        ImportResult result = await Task.Run(() => Import(source));
        timer.Stop();

        Assert.Equal(shapes, result.Document.Nodes.Count);
        Assert.Equal(shapes - 1, result.Document.Edges.Count);
        Assert.Empty(result.Warnings);
        Assert.True(timer.Elapsed < budget, $"took {timer.ElapsedMilliseconds} ms, budget {budget.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task Containers_nested_thousands_deep_import_without_recursion_and_with_absolute_positions()
    {
        // Each shape sits in the one before it, 4,000 deep: nothing may recurse per level, and each is offset by every ancestor.
        const int Depth = 4_000;
        var cells = new List<string>();
        for (int i = 0; i < Depth; i++)
        {
            cells.Add(DrawioXml.Vertex($"n{i}", $"Level {i}", "swimlane;", 1, 2, 500, 500, i == 0 ? "1" : $"n{i - 1}"));
        }

        ImportResult result = await Task.Run(() => Import(Model([.. cells])));

        Assert.Equal(Depth, result.Document.Nodes.Count);
        Assert.Equal(new Geometry(Depth, Depth * 2, 500, 500), NodeOf(result, $"c_n{Depth - 1}").Geometry);
        Assert.Equal($"c_n{Depth - 2}", NodeOf(result, $"c_n{Depth - 1}").Parent);
        Assert.Equal("c_n0", result.Document.Nodes[0].Id);
    }

    [Fact]
    public void An_image_address_is_text_and_nothing_is_ever_fetched_from_it()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string source = Model(DrawioXml.Vertex("pic", "Remote", $"shape=image;image=http://127.0.0.1:{port}/logo.png;", 0, 0, 10, 10));

        ImportResult result = Import(source);

        Assert.Equal("rectangle", NodeOf(result, "n_pic").Shape);
        Assert.False(listener.Pending(), "the importer connected to an address it found in the file");
    }

    [Fact]
    public void A_shape_s_text_cannot_become_dsl_when_the_document_is_printed()
    {
        // A label that looks like another line of the DSL is a quoted string in the printed text, never a second statement.
        string source = Model(Box("a", "x\nnode n_evil process Evil @ 0,0 10x10"), Box("b", "B", x: 200));

        Document document = Import(source).Document;
        Document back = Dsl.Parse(Dsl.Print(document)).Document;

        Assert.Equal(2, back.Nodes.Count);
        Assert.Equal("x\nnode n_evil process Evil @ 0,0 10x10", back.Nodes[0].Label);
    }
}
