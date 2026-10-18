using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Sql;
using Draughtsman.Core.Models;

namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// An ER document exports as a real Mermaid <c>erDiagram</c> (docs/api.md <c>POST /api/export/mermaid</c>): entities with their
/// columns, and a relationship per edge whose cardinality comes from the crow's-foot ends. A document that also holds anything
/// that is not an entity still degrades to a flowchart, and says so in the result's warnings. Expected text is literal.
/// </summary>
public class MermaidExporterEntityTests
{
    private static Node Table(string id, string label, string shape, params string[] columns) =>
        new(id, "entity", label, shape)
        {
            Geometry = new Geometry(0, 0, 200, 100),
            Props = new Dictionary<string, JsonElement> { ["columns"] = JsonSerializer.SerializeToElement(columns) },
        };

    private static Edge Relationship(string id, string from, string to, ArrowHead start, ArrowHead end, string? label = null, Dash? dash = null) =>
        new(id, "association", new Endpoint(from), new Endpoint(to))
        {
            Label = label,
            Style = new EdgeStyle { ArrowStart = start, ArrowEnd = end, Dash = dash },
        };

    private static Document Doc(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges, DocumentType type = DocumentType.Architecture) =>
        new("doc_er", 1, "Shop", type, nodes, edges, new Meta(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "test"));

    private static readonly Node s_customer = Table("t_customer", "Customer", "custom:er/entity", "PK Id int not null", "Name nvarchar(100) not null", "Balance decimal(10, 2) null");
    private static readonly Node s_order = Table("t_order", "Order", "custom:er/entity", "PK Id int not null", "PK FK \"Customer Id\" int not null", "Note double precision");

    [Fact]
    public void Export_writes_entities_and_a_relationship_with_cardinality_from_the_crows_feet()
    {
        Document document = Doc([s_customer, s_order], [Relationship("e_1", "t_order", "t_customer", ArrowHead.ZeroOrMany, ArrowHead.OneAndOnlyOne, "Customer Id")]);

        MermaidExportResult result = MermaidExporter.Export(document);

        Assert.Equal(
            "---\ntitle: Shop\n---\nerDiagram\n"
            + "    Customer {\n        int Id PK\n        nvarchar(100) Name\n        decimal(10_2) Balance\n    }\n"
            + "    Order {\n        int Id PK\n        int Customer_Id PK, FK\n        double_precision Note\n    }\n"
            + "    Order }o--|| Customer : \"Customer Id\"\n",
            result.Text);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData(ArrowHead.One, ArrowHead.One, "||--||")]
    [InlineData(ArrowHead.ZeroOrOne, ArrowHead.OneOrMany, "|o--|{")]
    [InlineData(ArrowHead.OneOrMany, ArrowHead.ZeroOrOne, "}|--o|")]
    [InlineData(ArrowHead.Many, ArrowHead.ZeroOrMany, "}o--o{")]
    public void Export_maps_every_crows_foot_end_to_its_mermaid_token_on_either_side(ArrowHead start, ArrowHead end, string expected)
    {
        Document document = Doc([s_customer, s_order], [Relationship("e_1", "t_customer", "t_order", start, end)]);

        Assert.Contains($"    Customer {expected} Order : \"\"\n", MermaidExporter.Export(document).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_draws_a_dashed_relationship_as_non_identifying()
    {
        Document document = Doc([s_customer, s_order], [Relationship("e_1", "t_customer", "t_order", ArrowHead.OneAndOnlyOne, ArrowHead.ZeroOrMany, dash: Dash.Dashed)]);

        Assert.Contains("    Customer ||..o{ Order : \"\"\n", MermaidExporter.Export(document).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_quotes_an_entity_name_with_spaces_and_keeps_two_same_named_entities_apart()
    {
        Node a = Table("t_a", "Order Lines", "custom:er/entity", "PK Id int");
        Node b = Table("t_b", "Order Lines", "custom:er/entity", "PK Id int");

        string text = MermaidExporter.Export(Doc([a, b], [])).Text;

        Assert.Contains("    \"Order Lines\" {\n", text, StringComparison.Ordinal);
        Assert.Contains("    \"Order Lines 2\" {\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_says_when_an_end_has_no_crows_foot_or_an_entity_is_weak()
    {
        Node weak = Table("t_w", "Line", "custom:er/weak-entity", "PK Id int");
        Edge plain = new("e_1", "association", new Endpoint("t_customer"), new Endpoint("t_w"));

        MermaidExportResult result = MermaidExporter.Export(Doc([s_customer, weak], [plain]));

        Assert.Equal(3, result.Warnings.Count);
        Assert.Contains("    %% t_w: a weak entity's double border has no Mermaid equivalent; drawn as a plain entity\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("    %% e_1: the start has no crow's-foot end; written as exactly one\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("    Customer ||--|| Line : \"\"\n", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_of_a_sql_import_is_an_er_diagram_with_a_relationship_per_foreign_key()
    {
        ImportResult imported = SqlImporter.Import(
            "CREATE TABLE Customers (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);\n"
            + "CREATE TABLE Orders (Id int NOT NULL PRIMARY KEY, CustomerId int NOT NULL REFERENCES Customers(Id));",
            new SqlImportOptions());

        string text = MermaidExporter.Export(imported.Document).Text;

        Assert.Contains("erDiagram\n", text, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^    Orders \}o--\|\| Customers : ""CustomerId""$", text);
        Assert.DoesNotContain("flowchart", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_of_entities_mixed_with_another_shape_still_degrades_to_a_flowchart_and_reports_it()
    {
        Node legend = new("legend", "container", "Legend", "rectangle") { Geometry = new Geometry(0, 0, 100, 100) };
        Node fancy = new("n_doc", "process", "Doc", "document") { Geometry = new Geometry(0, 0, 100, 100) };

        MermaidExportResult result = MermaidExporter.Export(Doc([s_customer, legend, fancy], []));

        Assert.StartsWith("---\ntitle: Shop\n---\nflowchart TD\n", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("erDiagram", result.Text, StringComparison.Ordinal);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Export_of_a_tag_coloured_sql_import_skips_the_legend_and_is_an_er_diagram_with_no_warning()
    {
        ImportResult imported = SqlImporter.Import(
            "-- [LEGACY] old\nCREATE TABLE Customers (Id int NOT NULL PRIMARY KEY);\n"
            + "-- [NEW] fresh\nCREATE TABLE Orders (Id int NOT NULL PRIMARY KEY, CustomerId int NOT NULL REFERENCES Customers(Id));",
            new SqlImportOptions());
        Assert.Contains(imported.Document.Nodes, n => n.Kind == "container" && n.Label == "Legend");

        MermaidExportResult result = MermaidExporter.Export(imported.Document);

        Assert.Contains("erDiagram\n", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("flowchart", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Legend {", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("LEGACY {", result.Text, StringComparison.Ordinal);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Export_of_an_empty_architecture_document_is_not_an_er_diagram()
    {
        Assert.DoesNotContain("erDiagram", MermaidExporter.Export(Doc([], [])).Text, StringComparison.Ordinal);
    }
}
