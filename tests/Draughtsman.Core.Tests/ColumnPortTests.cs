using System.Text.Json;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Sql;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Tests.Import;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests;

/// <summary>
/// Column ports (docs/er-entities.md `Column ports`): an ER entity's rows as edge attachment points, addressed as
/// <c>col:&lt;encoded name&gt;</c> and derived from the rows, never stored. The id spelling and the row numbers are pinned
/// with the same literals as web/src/model/columnPorts.test.ts, so the two implementations cannot drift; then the DSL
/// (parse, print, round trip, warnings), the JSON schema, the critique rule and the SQL importer.
/// </summary>
public class ColumnPortTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 29, 1, 2, 3, TimeSpan.Zero);

    private static Node Table(string id, double x, double y, params string[] columns)
    {
        return new Node(id, "entity", id, "custom:er/entity")
        {
            Geometry = new Geometry(x, y, 270, 92),
            Props = new Dictionary<string, JsonElement> { ["columns"] = JsonSerializer.SerializeToElement(columns) },
        };
    }

    private static Document Doc(IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
    {
        return new Document("doc_cols", Document.CurrentSchemaVersion, "Cols", DocumentType.Architecture, nodes, edges, new Meta(s_now, s_now, "test"));
    }

    private static Edge Fk(string? fromPort, string? toPort, string toNode = "t_customer")
    {
        return new Edge("e_fk", "association", new Endpoint("t_order") { Port = fromPort }, new Endpoint(toNode) { Port = toPort }) { Routing = new Routing(RoutingMode.Orthogonal) };
    }

    private static readonly Node s_order = Table("t_order", 0, 0, "PK Id int not null", "FK CustomerId int not null", "Placed datetime2 not null");
    private static readonly Node s_customer = Table("t_customer", 500, 200, "PK Id int not null", "Name nvarchar(100) not null", "Email nvarchar(100) null");

    [Theory]
    [InlineData("CustomerId", "col:CustomerId")]
    [InlineData("Customer Id", "col:Customer%20Id")]
    [InlineData("a.b", "col:a%2Eb")]
    [InlineData("100%", "col:100%25")]
    [InlineData("café", "col:caf%C3%A9")]
    [InlineData("x:y", "col:x%3Ay")]
    [InlineData("日本", "col:%E6%97%A5%E6%9C%AC")]
    public void A_column_name_is_escaped_into_an_id_that_decodes_back(string name, string expected)
    {
        string? id = ErShapeLibrary.ColumnPortId(name);

        Assert.Equal(expected, id);
        Assert.Equal(name, ErShapeLibrary.ColumnPortName(id));
        Assert.Matches("^col:[A-Za-z0-9_%-]+$", id!);
    }

    [Fact]
    public void An_empty_name_or_one_whose_id_passes_128_characters_has_no_id()
    {
        Assert.Null(ErShapeLibrary.ColumnPortId(""));
        Assert.Equal(128, ErShapeLibrary.ColumnPortId(new string('x', 124))!.Length);
        Assert.Null(ErShapeLibrary.ColumnPortId(new string('x', 125)));
        Assert.Null(ErShapeLibrary.ColumnPortId(new string(' ', 42)));
    }

    [Theory]
    [InlineData("left")]
    [InlineData("col:")]
    [InlineData("col:bad%2")]
    [InlineData("col:%FF")]
    public void An_id_that_is_not_a_well_formed_column_port_names_no_column(string id)
    {
        Assert.Null(ErShapeLibrary.ColumnPortName(id));
    }

    [Fact]
    public void Rows_sit_under_the_28_unit_name_band_18_units_apart_the_first_inset_by_5()
    {
        Assert.Equal(42, ErShapeLibrary.ColumnRowCentre(s_order, "col:Id"));
        Assert.Equal(60, ErShapeLibrary.ColumnRowCentre(s_order, "col:CustomerId"));
        Assert.Equal(78, ErShapeLibrary.ColumnRowCentre(s_order, "col:Placed"));
    }

    [Fact]
    public void A_missing_column_or_a_node_that_is_not_an_entity_has_no_row()
    {
        Assert.Null(ErShapeLibrary.ColumnRowCentre(s_order, "col:Missing"));
        Assert.Null(ErShapeLibrary.ColumnRowCentre(s_order, "left"));
        Assert.Null(ErShapeLibrary.ColumnRowCentre(s_order with { Shape = "rectangle" }, "col:Id"));
        Assert.Null(ErShapeLibrary.ColumnRowCentre(s_order with { Props = null }, "col:Id"));
    }

    [Fact]
    public void The_port_is_the_row_as_a_fraction_of_the_box_on_the_side_asked_for()
    {
        Assert.Equal(new Port("col:Id", 1, 42.0 / 92), ErShapeLibrary.ColumnPort(s_order, "col:Id", right: true));
        Assert.Equal(new Port("col:Placed", 0, 78.0 / 92), ErShapeLibrary.ColumnPort(s_order, "col:Placed", right: false));
        Node taller = s_order with { Geometry = new Geometry(0, 0, 270, 184) };
        Assert.Equal(78.0 / 184, ErShapeLibrary.ColumnPort(taller, "col:Placed", right: true)!.Y);
        Assert.Null(ErShapeLibrary.ColumnPort(s_order, "col:Missing", right: true));
    }

    private const string ColumnDsl = """
        draughtsman 1
        type architecture
        title "Shop"

        node t_customer entity "Customer" @ 500,200 270x92 columns=["PK Id int not null","Name nvarchar(100) not null"]
        node t_order entity "Order" @ 0,0 270x92 columns=["PK Id int not null","FK \"Customer Id\" int not null"]
        edge e_1 association t_order.col:Customer%20Id -> t_customer.col:Id routing=orthogonal
        """;

    [Fact]
    public void The_dsl_reads_a_column_port_after_the_dot_and_prints_it_back_unchanged()
    {
        ParseResult result = Dsl.Parse(ColumnDsl);

        Edge edge = Assert.Single(result.Document.Edges);
        Assert.Equal(new Endpoint("t_order") { Port = "col:Customer%20Id" }, edge.From);
        Assert.Equal(new Endpoint("t_customer") { Port = "col:Id" }, edge.To);
        Assert.Empty(result.Warnings);
        string printed = Dsl.Print(result.Document);
        Assert.Contains("edge e_1 association t_order.col:Customer%20Id -> t_customer.col:Id", printed, StringComparison.Ordinal);
        ParseResult again = Dsl.Parse(printed);
        Assert.Equal(edge.From, again.Document.Edges[0].From);
        Assert.Equal(edge.To, again.Document.Edges[0].To);
    }

    [Fact]
    public void The_dsl_warns_about_a_column_the_table_lacks_and_about_a_column_port_on_a_node_that_is_not_a_table()
    {
        string missing = ColumnDsl.Replace("col:Id", "col:Nope", StringComparison.Ordinal);
        DslWarning warning = Assert.Single(Dsl.Parse(missing).Warnings);
        Assert.Contains("column 'Nope'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("attaches to the table", warning.Message, StringComparison.Ordinal);

        string notATable = ColumnDsl + "\nnode n_box service \"Box\"\nedge e_2 flow n_box.col:Id -> t_customer";
        Assert.Contains(Dsl.Parse(notATable).Warnings, w => w.Message.Contains("n_box", StringComparison.Ordinal) && w.Message.Contains("column", StringComparison.Ordinal));
    }

    [Fact]
    public void A_column_port_on_a_node_whose_id_holds_dots_still_splits_at_the_port()
    {
        string dsl = """
            draughtsman 1
            type architecture
            title "Dots"

            node db.orders entity "Orders" columns=["PK Id int not null"]
            node db.customers entity "Customers" columns=["PK Id int not null"]
            edge e_1 association db.orders.col:Id -> db.customers.col:Id
            """;

        Edge edge = Assert.Single(Dsl.Parse(dsl).Document.Edges);

        Assert.Equal(new Endpoint("db.orders") { Port = "col:Id" }, edge.From);
        Assert.Equal(new Endpoint("db.customers") { Port = "col:Id" }, edge.To);
    }

    [Fact]
    public void Only_a_column_port_widens_the_port_spelling_the_dsl_and_schema_accept()
    {
        Assert.Throws<DslParseException>(() => Dsl.Parse(ColumnDsl.Replace("col:Id", "bad:Id", StringComparison.Ordinal)));
        Assert.Throws<DslParseException>(() => Dsl.Parse(ColumnDsl.Replace("col:Id", "col:", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => Dsl.Print(Doc([s_order, s_customer], [Fk("bad:Id", null)])));
    }

    [Fact]
    public void The_document_schema_admits_a_column_port_and_still_refuses_any_other_port_with_a_colon()
    {
        string valid = DocumentJson.Serialize(Doc([s_order, s_customer], [Fk("col:CustomerId", "col:Id")]));
        Assert.True(ContractFiles.Evaluate(valid).IsValid, ContractFiles.DescribeErrors(ContractFiles.Evaluate(valid)));
        Assert.False(ContractFiles.Evaluate(valid.Replace("col:Id", "bad:Id", StringComparison.Ordinal)).IsValid);
        Assert.False(ContractFiles.Evaluate(valid.Replace("col:Id", "col:", StringComparison.Ordinal)).IsValid);
    }

    [Fact]
    public void Critique_warns_about_an_end_on_a_column_the_table_lacks_naming_the_node_and_the_edge()
    {
        var missing = Doc([s_order, s_customer], [Fk("col:CustomerId", "col:Nope")]);

        CritiqueFinding finding = Assert.Single(CritiqueAnalyser.Analyse(missing), f => f.Rule == CritiqueAnalyser.Rules.UnknownColumn);

        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(["t_customer"], finding.NodeIds);
        Assert.Equal(["e_fk"], finding.EdgeIds);
        Assert.Contains("'Nope'", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Critique_stays_silent_when_every_column_exists_and_for_ends_with_an_ordinary_port_or_none()
    {
        Assert.DoesNotContain(CritiqueAnalyser.Analyse(Doc([s_order, s_customer], [Fk("col:CustomerId", "col:Id")])), f => f.Rule == CritiqueAnalyser.Rules.UnknownColumn);
        Assert.DoesNotContain(CritiqueAnalyser.Analyse(Doc([s_order, s_customer], [Fk(null, null)])), f => f.Rule == CritiqueAnalyser.Rules.UnknownColumn);
    }

    [Fact]
    public void Critique_flags_a_column_port_on_a_node_that_is_not_a_table()
    {
        var document = Doc([s_order, s_customer with { Shape = "rectangle", Kind = "service" }], [Fk("col:CustomerId", "col:Id")]);

        Assert.Contains(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.UnknownColumn && f.NodeIds.SequenceEqual(["t_customer"]));
    }

    private static ImportResult Import(string sql)
    {
        return SqlImporter.Import(sql, new SqlImportOptions { Now = s_now });
    }

    [Fact]
    public void A_foreign_key_edge_runs_from_the_fk_row_of_the_child_to_the_referenced_key_row_of_the_parent()
    {
        Document document = Import(SqlFixtures.TSql).Document;

        // No column list: the parent's primary key.
        Edge byPrimaryKey = SqlImporterTests.EdgeBetween(document, "t_journey_Orders", "t_Customers");
        Assert.Equal("col:CustomerId", byPrimaryKey.From.Port);
        Assert.Equal("col:CustomerId", byPrimaryKey.To.Port);
        // A named column in the parent that is not the first column of the table.
        Edge named = SqlImporterTests.EdgeBetween(document, "t_journey_Orders", "t_Carriers");
        Assert.Equal("col:CarrierId", named.From.Port);
        Assert.Equal("col:CarrierId", named.To.Port);
    }

    [Fact]
    public void A_composite_key_attaches_at_its_first_column_pair_and_the_label_still_names_them_all()
    {
        Document document = Import(SqlFixtures.TSql).Document;

        Edge edge = SqlImporterTests.EdgeBetween(document, "t_Shipments", "t_journey_OrderLines");

        Assert.Equal("col:OrderId", edge.From.Port);
        Assert.Equal("col:OrderId", edge.To.Port);
        Assert.Equal("OrderId, LineNo", edge.Label);
    }

    [Fact]
    public void Every_imported_column_port_resolves_to_a_row_of_its_own_table()
    {
        Document document = Import(SqlFixtures.TSql).Document;
        Dictionary<string, Node> byId = document.Nodes.ToDictionary(n => n.Id);

        Assert.All(document.Edges, edge =>
        {
            Assert.NotNull(ErShapeLibrary.ColumnRowCentre(byId[edge.From.Node], edge.From.Port));
            Assert.NotNull(ErShapeLibrary.ColumnRowCentre(byId[edge.To.Node], edge.To.Port));
        });
        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.UnknownColumn);
    }

    [Fact]
    public void A_column_name_with_a_space_or_a_case_difference_still_resolves_to_its_row()
    {
        Document document = Import("""
            create table Parent ([Parent Id] int not null primary key);
            create table Child (Id int not null primary key, [Parent Id] int not null references Parent ([parent id]));
            """).Document;

        Edge edge = SqlImporterTests.EdgeBetween(document, "t_Child", "t_Parent");

        Assert.Equal("col:Parent%20Id", edge.From.Port);
        Assert.Equal("col:Parent%20Id", edge.To.Port);
        Assert.NotNull(ErShapeLibrary.ColumnRowCentre(document.Nodes.Single(n => n.Id == "t_Parent"), edge.To.Port));
    }

    [Fact]
    public void A_self_referencing_key_uses_two_rows_of_the_same_table()
    {
        Document document = Import("create table Emp (Id int not null primary key, ManagerId int null references Emp (Id));").Document;

        Edge edge = Assert.Single(document.Edges);

        Assert.Equal("col:ManagerId", edge.From.Port);
        Assert.Equal("col:Id", edge.To.Port);
    }

    [Fact]
    public void A_key_naming_a_column_the_table_lacks_falls_back_to_the_table_at_that_end_only()
    {
        Document document = Import("""
            create table Parent (Id int not null primary key);
            create table Child (Id int not null primary key, ParentId int null, foreign key (ParentId) references Parent (Ghost));
            """).Document;

        Edge edge = Assert.Single(document.Edges);

        Assert.Equal("col:ParentId", edge.From.Port);
        Assert.Null(edge.To.Port);
    }

    [Fact]
    public void Exporting_the_imported_document_back_to_ddl_is_unchanged_by_the_column_ports()
    {
        Document withPorts = Import(SqlFixtures.TSql).Document;
        Document withoutPorts = withPorts with
        {
            Edges = withPorts.Edges.Select(e => e with { From = e.From with { Port = null }, To = e.To with { Port = null } }).ToList(),
        };

        Assert.Equal(SqlExporter.Export(withoutPorts).Sql, SqlExporter.Export(withPorts).Sql);
    }
}
