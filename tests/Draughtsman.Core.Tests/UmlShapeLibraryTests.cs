using System.Text.Json;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests;

/// <summary>The UML class library is data shared by both renderers and the layout sidecar; it must stay well formed,
/// agree with the architecture kinds that name a shape as their default, keep member lists intact through JSON and the
/// DSL, and compute the same compartment numbers as web/src/model/umlShapes.test.ts asserts.</summary>
public class UmlShapeLibraryTests
{
    private static Dictionary<string, JsonElement> Props(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    private static UmlShape Shape(string name)
    {
        Assert.True(UmlShapeLibrary.TryGet(UmlShapeLibrary.Prefix + name, out UmlShape? shape));
        return shape!;
    }

    [Fact]
    public void The_library_covers_the_batch_s_shapes_in_order_with_unique_well_formed_names()
    {
        Assert.Equal(["class", "interface", "lollipop", "socket"], UmlShapeLibrary.All.Select(shape => shape.Name).ToArray());
        foreach (UmlShape shape in UmlShapeLibrary.All)
        {
            Assert.Matches("^[A-Za-z0-9_.-]+$", shape.Name);
            Assert.False(string.IsNullOrWhiteSpace(shape.Label));
            Assert.False(string.IsNullOrWhiteSpace(shape.DefaultLabel));
            Assert.Contains(shape.Form, new[] { "compartments", "symbol" });
            // A symbol carries its own fixed size; a box is sized from its members and so has none.
            Assert.Equal(!shape.IsCompartments, shape.DefaultWidth is not null && shape.DefaultHeight is not null);
        }
    }

    [Theory]
    [InlineData("custom:uml/class", true)]
    [InlineData("custom:uml/socket", true)]
    [InlineData("custom:uml/not-a-shape", false)]
    [InlineData("custom:org/class", false)]
    [InlineData("class", false)]
    public void Only_a_name_in_the_library_is_a_uml_shape(string shape, bool expected)
    {
        Assert.Equal(expected, UmlShapeLibrary.IsUmlShape(shape));
    }

    [Fact]
    public void Each_kind_default_shape_names_its_shape_and_no_other_kind_claims_a_uml_shape()
    {
        KindVocabulary architecture = KindVocabulary.For(DocumentType.Architecture);
        HashSet<string> declared = UmlShapeLibrary.All.Select(shape => shape.Kind).ToHashSet(StringComparer.Ordinal);
        HashSet<string> actual = architecture.NodeKinds
            .Where(kind => kind.DefaultShape?.StartsWith(UmlShapeLibrary.Prefix, StringComparison.Ordinal) == true)
            .Select(kind => kind.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared, actual);
        foreach (UmlShape shape in UmlShapeLibrary.All)
        {
            KindDefinition kind = Assert.Single(architecture.NodeKinds, k => k.Name == shape.Kind);
            Assert.Equal(UmlShapeLibrary.Prefix + shape.Name, kind.DefaultShape);
            Assert.False(string.IsNullOrWhiteSpace(kind.Description));
        }

        // The two box kinds teach the member protocol, so an agent reading get_schema knows where members go.
        Assert.Contains("props.attributes", Assert.Single(architecture.NodeKinds, k => k.Name == "class").Description, StringComparison.Ordinal);
        Assert.Contains("props.operations", Assert.Single(architecture.NodeKinds, k => k.Name == "class").Description, StringComparison.Ordinal);
        Assert.Contains("operations", Assert.Single(architecture.NodeKinds, k => k.Name == "interface").Props!.Keys);
    }

    [Theory]
    [InlineData("+ save(): void", "+", "save(): void")]
    [InlineData("-id: int", "-", "id: int")]
    [InlineData("# helper()", "#", "helper()")]
    [InlineData("~ shared: bool", "~", "shared: bool")]
    [InlineData("  name: string  ", "", "name: string")]
    [InlineData("a + b", "", "a + b")]
    public void A_member_line_splits_into_its_visibility_glyph_and_text(string line, string visibility, string text)
    {
        Assert.Equal(new UmlMember(visibility, text), UmlShapeLibrary.ParseMember(line));
    }

    [Fact]
    public void Member_lines_are_the_non_blank_strings_of_the_list_in_order()
    {
        Assert.Equal(["a", "b"], UmlShapeLibrary.MemberLines(Props("""{"attributes":["a","","  ",3,null,"b"]}"""), "attributes"));
        Assert.Empty(UmlShapeLibrary.MemberLines(Props("""{"attributes":"a"}"""), "attributes"));
        Assert.Empty(UmlShapeLibrary.MemberLines(null, "operations"));
        Assert.Empty(UmlShapeLibrary.MemberLines(Props("""{"attributes":["x"]}"""), "operations"));
    }

    [Fact]
    public void Compartments_follow_the_content_with_the_numbers_the_web_canvas_asserts()
    {
        // Mirrors 'stacks three compartments whose heights follow the content' in umlShapes.test.ts.
        UmlClassLayout three = UmlShapeLibrary.LayoutClass(Shape("class"), "Order", Props("""{"attributes":["- id: int","- total: money","- paid: bool"],"operations":["+ pay(): void"]}"""), 0);
        Assert.Equal(28, three.HeaderHeight);
        Assert.Equal((28, 64), (three.Attributes.Top, three.Attributes.Height));
        Assert.Equal((92, 28), (three.Operations.Top, three.Operations.Height));
        Assert.Equal(120, three.NaturalHeight);

        // An empty compartment keeps one row, and a stereotype adds a row to the name band.
        Assert.Equal(84, UmlShapeLibrary.LayoutClass(Shape("class"), "Marker", null, 0).NaturalHeight);
        UmlClassLayout iface = UmlShapeLibrary.LayoutClass(Shape("interface"), "Repo", null, 0);
        Assert.Equal((46, 102), (iface.HeaderHeight, iface.NaturalHeight));

        // Extra height goes to the operations compartment; a shorter box never squeezes below the content.
        var props = Props("""{"attributes":["- a"],"operations":["+ b()"]}""");
        Assert.Equal(144, UmlShapeLibrary.LayoutClass(Shape("class"), "C", props, 200).Operations.Height);
        Assert.Equal(28, UmlShapeLibrary.LayoutClass(Shape("class"), "C", props, 40).Operations.Height);
    }

    [Fact]
    public void Width_follows_the_longest_line_the_glyph_column_and_the_bold_name_with_a_minimum()
    {
        Assert.Equal(120, UmlShapeLibrary.LayoutClass(Shape("class"), "A", null, 0).NaturalWidth);
        Assert.Equal(156, UmlShapeLibrary.LayoutClass(Shape("class"), "ClassName", Props("""{"attributes":["- attribute: Type"],"operations":["+ operation(): void"]}"""), 0).NaturalWidth);
        Assert.Equal(Math.Ceiling(16 + 12 + (31 * 7.5)), UmlShapeLibrary.LayoutClass(Shape("class"), "A", Props("""{"operations":["+ findByCustomerAndStatus(): void"]}"""), 0).NaturalWidth);
        Assert.Equal(Math.Ceiling(16 + (31 * 7.5)), UmlShapeLibrary.LayoutClass(Shape("class"), "A", Props("""{"operations":["findByCustomerAndStatus(): void"]}"""), 0).NaturalWidth);
        Assert.Equal(Math.Ceiling((24 * 7.5 * 1.1) + 16), UmlShapeLibrary.LayoutClass(Shape("class"), "AVeryLongClassNameIndeed", null, 0).NaturalWidth);
    }

    [Fact]
    public void A_stereotype_prop_replaces_the_default_and_an_empty_one_hides_it()
    {
        Assert.Equal("interface", UmlShapeLibrary.StereotypeOf(Shape("interface"), null));
        Assert.Null(UmlShapeLibrary.StereotypeOf(Shape("class"), null));
        Assert.Equal("abstract", UmlShapeLibrary.StereotypeOf(Shape("class"), Props("""{"stereotype":"abstract"}""")));
        Assert.Null(UmlShapeLibrary.StereotypeOf(Shape("interface"), Props("""{"stereotype":""}""")));
    }

    [Fact]
    public void A_class_with_members_round_trips_through_json_and_the_dsl_as_plain_readable_props()
    {
        var node = new Node("n_order", "class", "Order", "custom:uml/class")
        {
            Geometry = new Geometry(0, 0, 200, 120),
            Props = Props("""{"attributes":["- id: UUID","+ total: money"],"operations":["+ pay(): void","# audit(entry: \"x\"): Log"]}"""),
        };
        Document original = DocumentJsonTests.MinimalDocument() with { Type = DocumentType.Architecture, Nodes = [node] };
        string json = DocumentJson.Serialize(original);
        Assert.True(ContractFiles.Evaluate(json).IsValid);

        string text = Dsl.Print(original);
        // One node line: the kind's default shape is elided, and the members are line lists a person can read and edit.
        Assert.Contains("node n_order class \"Order\" @ 0,0 200x120 attributes=[\"- id: UUID\",\"+ total: money\"] operations=[\"+ pay(): void\",\"# audit(entry: \\\"x\\\"): Log\"]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("shape=", text, StringComparison.Ordinal);

        ParseResult parsed = Dsl.Parse(text);
        Assert.Empty(parsed.Warnings);
        Assert.Equal(json, DocumentJson.Serialize(parsed.Document));
        Assert.Equal(text, Dsl.Print(parsed.Document));
    }

    [Fact]
    public void A_hand_written_architecture_class_takes_its_shape_from_its_kind_and_needs_no_geometry()
    {
        const string source =
            "draughtsman 1\ntype architecture\ntitle \"Domain\"\n\n"
            + "node c_order class \"Order\" attributes=[\"- id: UUID\",\"+ total: money\"] operations=[\"+ pay(): void\"]\n"
            + "node c_repo interface \"OrderRepo\" operations=[\"+ find(id: UUID): Order\"]\n"
            + "edge e_impl dependency c_order -> c_repo\n";

        Document document = Dsl.Parse(source).Document;

        Node order = document.Nodes[0];
        Assert.Null(order.Geometry);
        Assert.Equal("custom:uml/class", order.Shape);
        Assert.Equal(["- id: UUID", "+ total: money"], UmlShapeLibrary.MemberLines(order.Props, "attributes"));
        Assert.Equal("custom:uml/interface", document.Nodes[1].Shape);
    }

    [Fact]
    public void The_dsl_example_in_the_compartment_note_parses_and_reprints_identically()
    {
        // The block shown in docs/uml-class-compartments.md; keep the two in step.
        const string source =
            "draughtsman 1\ntype architecture\ntitle \"Orders\"\n\n"
            + "node c_order class \"Order\" @ 0,0 156x120 attributes=[\"- id: UUID\",\"+ total: money\"] operations=[\"+ pay(): void\",\"# audit(): Log\"]\n"
            + "node c_repo interface \"OrderRepo\" operations=[\"+ find(id: UUID): Order\"]\n"
            + "node c_ip provided-interface \"IOrder\"\n"
            + "edge e_impl dependency c_order -> c_ip\n";

        ParseResult parsed = Dsl.Parse(source);

        Assert.Empty(parsed.Warnings);
        Assert.Equal(["custom:uml/class", "custom:uml/interface", "custom:uml/lollipop"], parsed.Document.Nodes.Select(n => n.Shape).ToArray());
        Assert.Equal(Dsl.Print(parsed.Document), Dsl.Print(Dsl.Parse(Dsl.Print(parsed.Document)).Document));
    }

    [Fact]
    public void A_freeform_class_names_its_shape_because_freeform_has_no_kind_vocabulary_to_infer_it_from()
    {
        // Freeform kinds are free text, so a kind alone draws a plain rectangle; the palette places the shape
        // explicitly and the printer writes it out, which is the spelling an agent uses in a freeform document.
        Node kindOnly = Dsl.Parse("draughtsman 1\ntype freeform\nnode c_order class \"Order\"\n").Document.Nodes[0];
        Assert.Equal("rectangle", kindOnly.Shape);

        const string explicitShape = "draughtsman 1\ntype freeform\nnode c_order class \"Order\" shape=custom:uml/class operations=[\"+ pay(): void\"]\n";
        Document document = Dsl.Parse(explicitShape).Document;
        Assert.Equal("custom:uml/class", document.Nodes[0].Shape);
        Assert.Contains("shape=custom:uml/class", Dsl.Print(document), StringComparison.Ordinal);
        Assert.Equal(Dsl.Print(document), Dsl.Print(Dsl.Parse(Dsl.Print(document)).Document));
    }

    [Fact]
    public void Placing_and_connecting_every_uml_shape_draws_no_critique_finding_about_it()
    {
        foreach (UmlShape shape in UmlShapeLibrary.All)
        {
            var uml = new Node("n_uml", shape.Kind, shape.DefaultLabel, UmlShapeLibrary.Prefix + shape.Name) { Geometry = new Geometry(0, 0, 160, 80) };
            var partner = new Node("n_svc", "service", "Service", "rounded") { Geometry = new Geometry(0, 200, 160, 80) };
            var edge = new Edge("e_1", "association", new Endpoint("n_uml"), new Endpoint("n_svc"));
            Document document = DocumentJsonTests.MinimalDocument() with { Type = DocumentType.Architecture, Nodes = [uml, partner], Edges = [edge] };

            Assert.Empty(CritiqueAnalyser.Analyse(document));
        }
    }

    [Fact]
    public void The_raw_library_json_is_available_verbatim()
    {
        string json = UmlShapeLibrary.RawJson;

        Assert.Contains("\"uml\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lollipop\"", json, StringComparison.Ordinal);
    }
}
