using System.Text.Json;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests;

/// <summary>The ER entity library is data shared by both renderers and the layout sidecar; it must stay well formed,
/// agree with the architecture kinds that name a shape as their default, parse column lines exactly as
/// web/src/model/erShapes.test.ts does, keep column lists intact through JSON and the DSL, and compute the same row
/// numbers the web canvas asserts.</summary>
public class ErShapeLibraryTests
{
    private static Dictionary<string, JsonElement> Props(string json)
    {
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    private static readonly string[] s_defaultColumns = ["PK Id int not null", "Name nvarchar(100) not null", "Notes nvarchar(max) null"];

    [Fact]
    public void The_library_has_an_entity_and_a_weak_entity_with_unique_well_formed_names_and_only_the_weak_one_double_bordered()
    {
        Assert.Equal([("entity", false), ("weak-entity", true)], ErShapeLibrary.All.Select(shape => (shape.Name, shape.Double)).ToArray());
        foreach (ErShape shape in ErShapeLibrary.All)
        {
            Assert.Matches("^[A-Za-z0-9_.-]+$", shape.Name);
            Assert.False(string.IsNullOrWhiteSpace(shape.Label));
            Assert.False(string.IsNullOrWhiteSpace(shape.DefaultLabel));
            Assert.Equal(JsonValueKind.Array, shape.DefaultProps.GetProperty("columns").ValueKind);
        }
    }

    [Theory]
    [InlineData("custom:er/entity", true)]
    [InlineData("custom:er/weak-entity", true)]
    [InlineData("custom:er/not-a-shape", false)]
    [InlineData("custom:uml/entity", false)]
    [InlineData("entity", false)]
    public void Only_a_name_in_the_library_is_an_er_shape(string shape, bool expected)
    {
        Assert.Equal(expected, ErShapeLibrary.IsErShape(shape));
    }

    [Fact]
    public void Each_kind_default_shape_names_its_shape_and_no_other_kind_claims_an_er_shape()
    {
        KindVocabulary architecture = KindVocabulary.For(DocumentType.Architecture);
        HashSet<string> declared = ErShapeLibrary.All.Select(shape => shape.Kind).ToHashSet(StringComparer.Ordinal);
        HashSet<string> actual = architecture.NodeKinds
            .Where(kind => kind.DefaultShape?.StartsWith(ErShapeLibrary.Prefix, StringComparison.Ordinal) == true)
            .Select(kind => kind.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared, actual);
        foreach (ErShape shape in ErShapeLibrary.All)
        {
            KindDefinition kind = Assert.Single(architecture.NodeKinds, k => k.Name == shape.Kind);
            Assert.Equal(ErShapeLibrary.Prefix + shape.Name, kind.DefaultShape);
            Assert.False(string.IsNullOrWhiteSpace(kind.Description));
            Assert.Contains("columns", kind.Props!.Keys);
        }
    }

    [Fact]
    public void The_entity_kind_description_teaches_the_column_line_format_and_the_crows_foot_ends_on_its_own()
    {
        string description = Assert.Single(KindVocabulary.For(DocumentType.Architecture).NodeKinds, k => k.Name == "entity").Description;

        Assert.Contains("props.columns", description, StringComparison.Ordinal);
        Assert.Contains("[PK] [FK] name type [not null | null]", description, StringComparison.Ordinal);
        Assert.Contains("PK FK OrderId int not null", description, StringComparison.Ordinal);
        Assert.Contains("crow", description, StringComparison.Ordinal);
        foreach (string end in new[] { "one", "zeroOrOne", "oneAndOnlyOne", "many", "zeroOrMany", "oneOrMany" })
        {
            Assert.Contains(end, description, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_example_of_the_library_file_parses_the_way_the_file_says()
    {
        using JsonDocument library = JsonDocument.Parse(ErShapeLibrary.RawJson);
        JsonElement[] examples = library.RootElement.GetProperty("examples").EnumerateArray().ToArray();
        Assert.True(examples.Length > 10);

        foreach (JsonElement example in examples)
        {
            string line = example.GetProperty("line").GetString()!;
            ErColumn parsed = ErShapeLibrary.ParseColumn(line);
            string?[] expectedKeys = example.GetProperty("keys").EnumerateArray().Select(key => key.GetString()).ToArray();
            JsonElement nullable = example.GetProperty("nullable");

            Assert.True(expectedKeys.SequenceEqual(parsed.Keys), line);
            Assert.Equal(example.GetProperty("name").GetString(), parsed.Name);
            Assert.Equal(example.GetProperty("type").GetString(), parsed.Type);
            Assert.Equal(
                nullable.ValueKind == JsonValueKind.Null ? null : nullable.GetString(),
                parsed.Nullable switch { ErNullability.Null => "null", ErNullability.NotNull => "notnull", _ => null });
        }
    }

    [Fact]
    public void A_column_line_keeps_a_sql_server_type_whole_and_reads_both_keys_in_either_order()
    {
        Assert.Equal(("FK", "UserId", "nvarchar(50)", ErNullability.NotNull), Flat(ErShapeLibrary.ParseColumn("FK UserId nvarchar(50) not null")));
        Assert.Equal(("", "Amount", "decimal(18, 4)", ErNullability.Null), Flat(ErShapeLibrary.ParseColumn("Amount decimal(18, 4) null")));
        Assert.Equal(["PK", "FK"], ErShapeLibrary.ParseColumn("FK PK OrderId int not null").Keys);
        Assert.Equal(["PK"], ErShapeLibrary.ParseColumn("PK PK int").Keys);
        Assert.Equal("PK", ErShapeLibrary.ParseColumn("PK PK int").Name);
        Assert.Equal("nullable_thing", ErShapeLibrary.ParseColumn("A nullable_thing").Type);
        Assert.Null(ErShapeLibrary.ParseColumn("A int").Nullable);
        Assert.Equal(ErNullability.NotNull, ErShapeLibrary.ParseColumn("A int not  NULL").Nullable);
        Assert.Equal("PK FK", ErShapeLibrary.ParseColumn("PK FK A int").KeyText);
        Assert.Equal("NOT NULL", ErShapeLibrary.ParseColumn("A int not null").NullText);
        Assert.Equal("NULL", ErShapeLibrary.ParseColumn("A int null").NullText);
        Assert.Equal("", ErShapeLibrary.ParseColumn("A int").NullText);
    }

    [Fact]
    public void Column_lines_are_the_non_blank_strings_of_the_list_in_order()
    {
        Assert.Equal(["A int", "B int"], ErShapeLibrary.ColumnLines(Props("""{"columns":["A int","","  ",3,null,"B int"]}""")));
        Assert.Empty(ErShapeLibrary.ColumnLines(Props("""{"columns":"A int"}""")));
        Assert.Empty(ErShapeLibrary.ColumnLines(null));
    }

    [Fact]
    public void Rows_align_at_the_widest_cell_with_the_numbers_the_web_canvas_asserts()
    {
        // Mirrors 'stacks a name band over one row per column' in erShapes.test.ts.
        ErLayout layout = ErShapeLibrary.LayoutEntity("TableName", Props(ColumnsJson(s_defaultColumns)), 0);

        Assert.Equal(28, layout.HeaderHeight);
        Assert.Equal((28, 64), (layout.BodyTop, layout.BodyHeight));
        Assert.Equal(92, layout.NaturalHeight);
        Assert.Equal(new ErColumnOffsets(8, 36.5, 86, 195.5), layout.Offsets);
        Assert.Equal(270, layout.NaturalWidth);
    }

    [Fact]
    public void The_key_divider_follows_only_a_leading_run_of_primary_key_rows()
    {
        Assert.Equal(69, ErShapeLibrary.LayoutEntity("T", Props(ColumnsJson("PK A int", "PK B int", "C int")), 0).KeyDivider);
        Assert.Null(ErShapeLibrary.LayoutEntity("T", Props(ColumnsJson("PK A int", "PK B int")), 0).KeyDivider);
        Assert.Null(ErShapeLibrary.LayoutEntity("T", Props(ColumnsJson("A int", "B int")), 0).KeyDivider);
        Assert.Null(ErShapeLibrary.LayoutEntity("T", Props(ColumnsJson("A int", "PK B int")), 0).KeyDivider);
        Assert.Null(ErShapeLibrary.LayoutEntity("T", Props(ColumnsJson("FK A int", "B int")), 0).KeyDivider);
    }

    [Fact]
    public void A_column_no_row_fills_is_left_out_and_an_empty_entity_keeps_one_row_and_the_minimum_width()
    {
        ErLayout plain = ErShapeLibrary.LayoutEntity("T", Props(ColumnsJson("Id int", "Name text")), 0);
        Assert.Equal(new ErColumnOffsets(null, 8, 8 + (4 * 7.5) + 12, null), plain.Offsets);

        ErLayout empty = ErShapeLibrary.LayoutEntity("T", null, 0);
        Assert.Equal(56, empty.NaturalHeight);
        Assert.Equal(120, empty.NaturalWidth);
        Assert.Equal(Math.Ceiling((24 * 7.5 * 1.1) + 16), ErShapeLibrary.LayoutEntity("AVeryLongTableNameIndeed", Props(ColumnsJson("Id int")), 0).NaturalWidth);
    }

    [Fact]
    public void Extra_height_goes_to_the_row_area_and_a_shorter_box_is_never_squeezed()
    {
        var props = Props(ColumnsJson("PK Id int not null"));
        Assert.Equal((28, 172), (ErShapeLibrary.LayoutEntity("T", props, 200).BodyTop, ErShapeLibrary.LayoutEntity("T", props, 200).BodyHeight));
        Assert.Equal(28, ErShapeLibrary.LayoutEntity("T", props, 10).BodyHeight);
    }

    [Fact]
    public void A_table_with_composite_keys_round_trips_through_json_and_the_dsl_as_plain_readable_props()
    {
        string[] columns =
        [
            "PK FK OrderId int not null",
            "PK FK ProductId int not null",
            "PK \"Line No\" int not null",
            "Quantity smallint not null",
            "UnitPrice decimal(10, 2) null",
            "RowVer rowversion not null",
            "Note nvarchar(max) null",
        ];
        var node = new Node("t_line", "entity", "OrderLine", "custom:er/entity")
        {
            Geometry = new Geometry(0, 0, 264, 154),
            Props = Props(ColumnsJson(columns)),
        };
        Document original = DocumentJsonTests.MinimalDocument() with { Type = DocumentType.Architecture, Nodes = [node] };
        string json = DocumentJson.Serialize(original);
        Assert.True(ContractFiles.Evaluate(json).IsValid);

        string text = Dsl.Print(original);
        // One node line: the kind's default shape is elided, and each column is one string a program can emit or read.
        Assert.Contains("node t_line entity \"OrderLine\" @ 0,0 264x154 columns=[\"PK FK OrderId int not null\",\"PK FK ProductId int not null\",\"PK \\\"Line No\\\" int not null\",", text, StringComparison.Ordinal);
        Assert.DoesNotContain("shape=", text, StringComparison.Ordinal);

        ParseResult parsed = Dsl.Parse(text);
        Assert.Empty(parsed.Warnings);
        Assert.Equal(json, DocumentJson.Serialize(parsed.Document));
        Assert.Equal(text, Dsl.Print(parsed.Document));
        Assert.Equal(columns, ErShapeLibrary.ColumnLines(parsed.Document.Nodes[0].Props));
    }

    [Fact]
    public void A_hand_written_architecture_entity_takes_its_shape_from_its_kind_and_needs_no_geometry()
    {
        const string source =
            "draughtsman 1\ntype architecture\ntitle \"Shop\"\n\n"
            + "node t_user entity \"User\" columns=[\"PK Id uniqueidentifier not null\",\"Email nvarchar(100) null\"]\n"
            + "node t_order entity \"Order\" columns=[\"PK Id int not null\",\"FK UserId uniqueidentifier not null\",\"Placed datetime2 not null\"]\n"
            + "node t_line weak-entity \"OrderLine\" columns=[\"PK FK OrderId int not null\",\"PK Line int not null\"]\n"
            + "edge e_places association t_user -> t_order\n";

        Document document = Dsl.Parse(source).Document;

        Assert.All(document.Nodes, node => Assert.Null(node.Geometry));
        Assert.Equal(["custom:er/entity", "custom:er/entity", "custom:er/weak-entity"], document.Nodes.Select(n => n.Shape).ToArray());
        Assert.Equal(["PK Id uniqueidentifier not null", "Email nvarchar(100) null"], ErShapeLibrary.ColumnLines(document.Nodes[0].Props));
        Assert.Equal(Dsl.Print(document), Dsl.Print(Dsl.Parse(Dsl.Print(document)).Document));
    }

    [Fact]
    public void The_dsl_example_in_the_entity_note_parses_and_reprints_identically()
    {
        // The block shown in docs/er-entities.md; keep the two in step.
        const string source =
            "draughtsman 1\ntype architecture\ntitle \"Shop\"\n\n"
            + "node t_customer entity \"Customer\" @ 0,0 270x92 columns=[\"PK Id int not null\",\"Name nvarchar(100) not null\",\"Email nvarchar(100) null\"]\n"
            + "node t_order entity \"Order\" @ 400,0 270x92 columns=[\"PK Id int not null\",\"FK CustomerId int not null\",\"Placed datetime2 not null\"]\n"
            + "edge e_places association t_customer -> t_order style.arrowStart=oneAndOnlyOne style.arrowEnd=zeroOrMany\n";

        ParseResult parsed = Dsl.Parse(source);

        Assert.Empty(parsed.Warnings);
        Assert.Equal(["custom:er/entity", "custom:er/entity"], parsed.Document.Nodes.Select(n => n.Shape).ToArray());
        Assert.Equal(ArrowHead.OneAndOnlyOne, parsed.Document.Edges[0].Style?.ArrowStart);
        Assert.Equal(ArrowHead.ZeroOrMany, parsed.Document.Edges[0].Style?.ArrowEnd);
        Assert.Equal(Dsl.Print(parsed.Document), Dsl.Print(Dsl.Parse(Dsl.Print(parsed.Document)).Document));
    }

    [Fact]
    public void A_freeform_entity_names_its_shape_because_freeform_has_no_kind_vocabulary_to_infer_it_from()
    {
        Node kindOnly = Dsl.Parse("draughtsman 1\ntype freeform\nnode t_user entity \"User\"\n").Document.Nodes[0];
        Assert.Equal("rectangle", kindOnly.Shape);

        const string explicitShape = "draughtsman 1\ntype freeform\nnode t_user entity \"User\" shape=custom:er/entity columns=[\"PK Id int not null\"]\n";
        Document document = Dsl.Parse(explicitShape).Document;
        Assert.Equal("custom:er/entity", document.Nodes[0].Shape);
        Assert.Contains("shape=custom:er/entity", Dsl.Print(document), StringComparison.Ordinal);
        Assert.Equal(Dsl.Print(document), Dsl.Print(Dsl.Parse(Dsl.Print(document)).Document));
    }

    [Fact]
    public void Two_related_tables_used_as_intended_draw_no_critique_finding_and_an_unconnected_one_still_does()
    {
        foreach (ErShape shape in ErShapeLibrary.All)
        {
            var table = new Node("t_a", shape.Kind, shape.DefaultLabel, ErShapeLibrary.Prefix + shape.Name) { Geometry = new Geometry(0, 0, 264, 92), Props = Props(ColumnsJson(s_defaultColumns)) };
            var partner = new Node("t_b", "entity", "Other", "custom:er/entity") { Geometry = new Geometry(0, 300, 264, 92), Props = Props(ColumnsJson("PK Id int not null")) };
            var edge = new Edge("e_1", "association", new Endpoint("t_a"), new Endpoint("t_b"));
            Document document = DocumentJsonTests.MinimalDocument() with { Type = DocumentType.Architecture, Nodes = [table, partner], Edges = [edge] };

            Assert.Empty(CritiqueAnalyser.Analyse(document));

            // Two-sided: with the relationship removed the analyser does speak up, so the empty result above means something.
            Assert.NotEmpty(CritiqueAnalyser.Analyse(document with { Edges = [] }));
        }
    }

    [Fact]
    public void The_raw_library_json_is_available_verbatim()
    {
        string json = ErShapeLibrary.RawJson;

        Assert.Contains("\"er\"", json, StringComparison.Ordinal);
        Assert.Contains("\"weak-entity\"", json, StringComparison.Ordinal);
    }

    private static (string Keys, string Name, string Type, ErNullability? Nullable) Flat(ErColumn column)
    {
        return (column.KeyText, column.Name, column.Type, column.Nullable);
    }

    private static string ColumnsJson(params string[] columns)
    {
        return JsonSerializer.Serialize(new { columns });
    }
}
