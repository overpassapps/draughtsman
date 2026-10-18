using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// A Mermaid <c>erDiagram</c> imports as entities and crow's-foot association edges (docs/er-entities.md, docs/api.md
/// <c>POST /api/import/mermaid</c>), the mirror of <see cref="MermaidExporter"/>'s entity output. Fixtures are synthetic.
/// </summary>
public class MermaidErImporterTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 29, 1, 2, 3, TimeSpan.Zero);

    private static ImportResult Import(string source, DocumentType type = DocumentType.Architecture) =>
        MermaidImporter.Import(source, new MermaidImportOptions { Type = type, Now = s_now });

    private static string[] Columns(Node node) => ErShapeLibrary.ColumnLines(node.Props).ToArray();

    [Theory]
    [InlineData("||", "||", ArrowHead.OneAndOnlyOne, ArrowHead.OneAndOnlyOne)]
    [InlineData("|o", "o|", ArrowHead.ZeroOrOne, ArrowHead.ZeroOrOne)]
    [InlineData("}o", "o{", ArrowHead.ZeroOrMany, ArrowHead.ZeroOrMany)]
    [InlineData("}|", "|{", ArrowHead.OneOrMany, ArrowHead.OneOrMany)]
    [InlineData("||", "o{", ArrowHead.OneAndOnlyOne, ArrowHead.ZeroOrMany)]
    [InlineData("}|", "o|", ArrowHead.OneOrMany, ArrowHead.ZeroOrOne)]
    public void Each_cardinality_token_maps_to_its_crows_foot_end_on_its_own_side(string left, string right, ArrowHead start, ArrowHead end)
    {
        ImportResult result = Import($"erDiagram\n  A {left}--{right} B : rel\n");

        Edge edge = Assert.Single(result.Document.Edges);
        Assert.Equal("association", edge.Kind);
        Assert.Equal("t_A", edge.From.Node);
        Assert.Equal("t_B", edge.To.Node);
        Assert.Equal(start, edge.Style?.ArrowStart);
        Assert.Equal(end, edge.Style?.ArrowEnd);
        Assert.Null(edge.Style?.Dash);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_non_identifying_relationship_is_dashed_and_an_identifying_one_is_not()
    {
        ImportResult result = Import("erDiagram\n  A ||..o{ B : maybe\n  A ||--o{ C : always\n");

        Assert.Equal(Dash.Dashed, result.Document.Edges[0].Style?.Dash);
        Assert.Null(result.Document.Edges[1].Style?.Dash);
    }

    [Fact]
    public void An_entity_block_becomes_a_table_with_key_markers_names_and_types_but_no_nullability()
    {
        ImportResult result = Import("""
            erDiagram
              CUSTOMER {
                int id PK
                varchar(100) name
                string[] tags
                int owner_id FK
                int link_id PK, FK
              }
            """);

        Node node = Assert.Single(result.Document.Nodes);
        Assert.Equal("t_CUSTOMER", node.Id);
        Assert.Equal("entity", node.Kind);
        Assert.Equal("custom:er/entity", node.Shape);
        Assert.Equal("CUSTOMER", node.Label);
        Assert.Equal(["PK id int", "name varchar(100)", "tags string[]", "FK owner_id int", "PK FK link_id int"], Columns(node));
        Assert.All(Columns(node), line => Assert.Null(ErShapeLibrary.ParseColumn(line).Nullable));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void An_entity_only_named_in_a_relationship_is_created_empty_and_document_order_is_first_mention()
    {
        ImportResult result = Import("erDiagram\n  ORDER }o--|| CUSTOMER : places\n  CUSTOMER { int id PK }\n".Replace("{ int id PK }", "{\n int id PK\n }"));

        Assert.Equal(["t_ORDER", "t_CUSTOMER"], result.Document.Nodes.Select(n => n.Id));
        Assert.Empty(Columns(result.Document.Nodes[0]));
        Assert.Equal(["PK id int"], Columns(result.Document.Nodes[1]));
    }

    [Fact]
    public void An_alias_is_the_label_while_relationships_use_the_name_and_a_quoted_label_is_unquoted()
    {
        ImportResult result = Import("erDiagram\n  p[\"Person Record\"] {\n    int id PK\n  }\n  p ||--o{ \"Order Line\" : \"has #quot;many#quot;\"\n");

        Assert.Equal("Person Record", result.Document.Nodes[0].Label);
        Assert.Equal("t_p", result.Document.Nodes[0].Id);
        Node line = result.Document.Nodes[1];
        Assert.Equal("Order Line", line.Label);
        Assert.Equal("t_Order_Line", line.Id);
        Edge edge = Assert.Single(result.Document.Edges);
        Assert.Equal("has \"many\"", edge.Label);
        Assert.Equal("t_Order_Line", edge.To.Node);
    }

    [Fact]
    public void An_empty_quoted_label_is_no_label()
    {
        Assert.Null(Import("erDiagram\n  A ||--|| B : \"\"\n").Document.Edges[0].Label);
        Assert.Null(Import("erDiagram\n  A ||--|| B\n").Document.Edges[0].Label);
    }

    [Fact]
    public void An_unreadable_line_is_one_warning_and_later_lines_still_import()
    {
        ImportResult result = Import("""
            erDiagram
              %% a comment is silent
              A ||--o{ B : one
              this is not mermaid
              classDef big fill:#f00
              C ||--|| D : two
            """);

        Assert.Equal(2, result.Document.Edges.Count);
        Assert.Equal(4, result.Document.Nodes.Count);
        Assert.Collection(result.Warnings,
            w => { Assert.Equal(ImportWarningCodes.ParseError, w.Code); Assert.Equal(4, w.Line); },
            w => { Assert.Equal(ImportWarningCodes.UnsupportedSyntax, w.Code); Assert.Equal(5, w.Line); });
    }

    [Fact]
    public void A_unique_marker_and_an_attribute_comment_are_dropped_with_one_warning_each_and_the_column_stays()
    {
        ImportResult result = Import("erDiagram\n  U {\n    string email UK\n    string note \"free text\"\n    int id PK, UK\n    ??? \n    int after\n  }\n");

        Assert.Equal(["email string", "note string", "PK id int", "after int"], Columns(result.Document.Nodes[0]));
        Assert.Equal(4, result.Warnings.Count);
        Assert.Equal(3, result.Warnings.Count(w => w.Code == ImportWarningCodes.UnsupportedSyntax));
        Assert.Equal(6, result.Warnings.Single(w => w.Code == ImportWarningCodes.ParseError).Line);
    }

    [Fact]
    public void A_repeated_block_keeps_the_first_and_an_unclosed_block_is_closed_with_a_warning()
    {
        ImportResult result = Import("erDiagram\n  A {\n    int x PK\n  }\n  A {\n    int y\n  }\n  B {\n    int z\n");

        Assert.Equal(["PK x int"], Columns(result.Document.Nodes[0]));
        Assert.Equal(["z int"], Columns(result.Document.Nodes[1]));
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.DuplicateTable);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.ParseError && w.Message.Contains("never closed", StringComparison.Ordinal));
    }

    [Fact]
    public void Front_matter_title_and_direction_are_read_and_freeform_is_allowed()
    {
        ImportResult result = Import("```mermaid\n---\ntitle: Shop\n---\nerDiagram\n  direction LR\n  A ||--|| B\n```", DocumentType.Freeform);

        Assert.Equal("Shop", result.Document.Title);
        Assert.Equal(DocumentType.Freeform, result.Document.Type);
        Assert.Equal("LR", result.Direction);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_flowchart_type_is_refused_rather_than_relabelled()
    {
        Assert.Throws<ArgumentException>(() => Import("erDiagram\n  A ||--|| B\n", DocumentType.Flowchart));
        Assert.Throws<ArgumentException>(() => Import("erDiagram\n  A ||--|| B\n", DocumentType.Sequence));
    }

    [Fact]
    public void An_empty_erDiagram_warns_and_never_throws()
    {
        ImportResult result = Import("erDiagram\n");

        Assert.Empty(result.Document.Nodes);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.EmptyDiagram);
        Assert.False(result.UnsupportedDiagram);
    }

    [Fact]
    public void Export_then_import_of_a_two_table_document_is_stable()
    {
        Node Table(string id, string label, params string[] columns) => new(id, "entity", label, "custom:er/entity")
        {
            Geometry = new Geometry(0, 0, 200, 100),
            Props = new Dictionary<string, JsonElement> { ["columns"] = JsonSerializer.SerializeToElement(columns) },
        };

        var original = new Document("doc_er", 1, "Shop", DocumentType.Architecture,
            [
                Table("t_customer", "Customer", "PK Id int not null", "Name nvarchar(100) not null"),
                Table("t_order", "Order Line", "PK Id int not null", "PK FK CustomerId int not null"),
            ],
            [
                new Edge("e_1", "association", new Endpoint("t_order"), new Endpoint("t_customer"))
                {
                    Label = "places",
                    Style = new EdgeStyle { ArrowStart = ArrowHead.ZeroOrMany, ArrowEnd = ArrowHead.OneAndOnlyOne, Dash = Dash.Dashed },
                },
            ],
            new Meta(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "test"));

        string exported = MermaidExporter.Export(original).Text;
        ImportResult imported = Import(exported);

        Assert.Empty(imported.Warnings);
        Assert.Equal("Shop", imported.Document.Title);
        Assert.Equal(["Customer", "Order Line"], imported.Document.Nodes.Select(n => n.Label));
        Assert.Equal(["PK Id int", "Name nvarchar(100)"], Columns(imported.Document.Nodes[0]));
        Assert.Equal(["PK Id int", "PK FK CustomerId int"], Columns(imported.Document.Nodes[1]));
        Edge edge = Assert.Single(imported.Document.Edges);
        Assert.Equal(("t_Order_Line", "t_Customer"), (edge.From.Node, edge.To.Node));
        Assert.Equal((ArrowHead.ZeroOrMany, ArrowHead.OneAndOnlyOne, Dash.Dashed), (edge.Style?.ArrowStart, edge.Style?.ArrowEnd, edge.Style?.Dash));
        Assert.Equal("places", edge.Label);

        // And the text is a fixed point: importing what was exported and exporting again changes nothing.
        Assert.Equal(exported, MermaidExporter.Export(imported.Document).Text);
    }
}
