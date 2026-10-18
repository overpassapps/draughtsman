using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Sql;
using Draughtsman.Core.Models;

namespace Draughtsman.Core.Tests.Import;

public class SqlExporterTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 29, 1, 2, 3, TimeSpan.Zero);

    /// <summary>The T-SQL fixture with Audit's key to a table that is not in the script taken out: such a key has nothing to export.</summary>
    private static readonly string WithoutDanglingKey = SqlFixtures.TSql.Replace("constraint FK_Audit_Users references dbo.Users", string.Empty, StringComparison.Ordinal);

    private static Document Import(string sql, SqlDialect dialect = SqlDialect.Auto)
    {
        return SqlImporter.Import(sql, new SqlImportOptions { Dialect = dialect, Now = s_now }).Document;
    }

    /// <summary>Tables, their column lines, and relationships by table label: everything a diagram says, none of its ids.</summary>
    private static string Shape(Document document)
    {
        var lines = new List<string>();
        foreach (Node node in document.Nodes)
        {
            lines.Add($"{node.Label}: {string.Join(" | ", SqlImporterTests.Columns(document, node.Id))}");
        }

        foreach (Edge edge in document.Edges)
        {
            string from = document.Nodes.Single(n => n.Id == edge.From.Node).Label;
            string to = document.Nodes.Single(n => n.Id == edge.To.Node).Label;
            lines.Add($"{from} -> {to} [{edge.Label}] {edge.Style?.ArrowStart}/{edge.Style?.ArrowEnd}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Column names, key markers and relationships, ignoring the types (which a different dialect spells differently) and
    /// the schema prefix (which MySQL and SQLite fold into the table name).
    /// </summary>
    private static string Structure(Document document)
    {
        var lines = new List<string>();
        foreach (Node node in document.Nodes)
        {
            IEnumerable<string> columns = SqlImporterTests.Columns(document, node.Id).Select(l =>
            {
                Schemas.ErColumn c = Schemas.ErShapeLibrary.ParseColumn(l);
                return $"{c.KeyText} {c.Name} {c.NullText}".Trim();
            });
            lines.Add($"{node.Label.Replace('.', '_')}: {string.Join(" | ", columns)}");
        }

        foreach (Edge edge in document.Edges)
        {
            lines.Add($"{document.Nodes.Single(n => n.Id == edge.From.Node).Label.Replace('.', '_')} -> {document.Nodes.Single(n => n.Id == edge.To.Node).Label.Replace('.', '_')} [{edge.Label}] {edge.Style?.ArrowStart}/{edge.Style?.ArrowEnd}");
        }

        return string.Join("\n", lines);
    }

    [Fact]
    public void TSql_export_writes_tables_primary_keys_not_null_and_foreign_keys_from_the_edges()
    {
        string sql = SqlExporter.Export(Import(SqlFixtures.TSql), SqlDialect.TSql).Sql;

        Assert.Contains("create table [dbo].[Customers]", sql, StringComparison.Ordinal);
        Assert.Contains("    [CustomerId] int not null,", sql, StringComparison.Ordinal);
        Assert.Contains("    [Email] nvarchar(max) null,", sql, StringComparison.Ordinal);
        Assert.Contains("    [Balance] numeric(10, 2) not null,", sql, StringComparison.Ordinal);
        Assert.Contains("constraint [PK_Customers] primary key ([CustomerId])", sql, StringComparison.Ordinal);
        Assert.Contains("create table [journey].[OrderLines]", sql, StringComparison.Ordinal);
        Assert.Contains("constraint [PK_OrderLines] primary key ([OrderId], [LineNo])", sql, StringComparison.Ordinal);
        Assert.Contains("[Order Note] nvarchar(200) null", sql, StringComparison.Ordinal);
        Assert.Contains("alter table [journey].[Orders] add constraint [FK_Orders_Customers] foreign key ([CustomerId]) references [dbo].[Customers] ([CustomerId])", sql, StringComparison.Ordinal);
        Assert.Contains("alter table [dbo].[Shipments] add constraint [FK_Shipments_OrderLines] foreign key ([OrderId], [LineNo]) references [journey].[OrderLines] ([OrderId], [LineNo])", sql, StringComparison.Ordinal);
        Assert.Contains("\ngo\n", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(";", sql, StringComparison.Ordinal);

        // Every table is created before any foreign key refers to one.
        Assert.True(sql.LastIndexOf("create table", StringComparison.Ordinal) < sql.IndexOf("alter table", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SqlDialect.TSql)]
    [InlineData(SqlDialect.Postgres)]
    [InlineData(SqlDialect.MySql)]
    [InlineData(SqlDialect.Sqlite)]
    public void A_generated_script_is_the_same_bytes_on_every_host_so_it_never_has_a_carriage_return(SqlDialect dialect)
    {
        // StringBuilder.AppendLine writes Environment.NewLine, so only a Windows host can fail this (the CI Windows leg does).
        string sql = SqlExporter.Export(Import(SqlFixtures.TSql, SqlDialect.TSql), dialect).Sql;

        Assert.Contains('\n', sql);
        Assert.DoesNotContain('\r', sql);
    }

    [Fact]
    public void A_one_to_one_relationship_writes_a_unique_constraint_unless_the_columns_are_already_the_primary_key()
    {
        string sql = SqlExporter.Export(Import(SqlFixtures.TSql), SqlDialect.TSql).Sql;

        // Passports.CustomerId is one-to-one because it is unique: the unique constraint has to come back out.
        Assert.Contains("constraint [UQ_Passports_CustomerId] unique ([CustomerId])", sql, StringComparison.Ordinal);
        // CustomerProfiles.CustomerId is one-to-one because it is the key: no second constraint.
        Assert.DoesNotContain("UQ_CustomerProfiles", sql, StringComparison.Ordinal);
        // A plain one-to-many foreign key is not unique.
        Assert.DoesNotContain("UQ_Orders", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UQ_Shipments", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_export_import_gives_an_equivalent_diagram_for_every_dialect_in_its_own_dialect()
    {
        (string Sql, SqlDialect Dialect)[] cases =
        [
            (WithoutDanglingKey, SqlDialect.TSql),
            (SqlFixtures.Postgres, SqlDialect.Postgres),
            (SqlFixtures.MySql, SqlDialect.MySql),
            (SqlFixtures.Sqlite, SqlDialect.Sqlite),
        ];

        foreach ((string sql, SqlDialect dialect) in cases)
        {
            Document first = Import(sql, dialect);
            SqlExportResult exported = SqlExporter.Export(first, dialect);
            ImportResult second = SqlImporter.Import(exported.Sql, new SqlImportOptions { Dialect = dialect, Now = s_now });

            Assert.Equal(Shape(first), Shape(second.Document));
            Assert.Empty(second.Warnings);
            Assert.Empty(exported.Warnings);

            // And once more: the export of the re-import is the same script, so nothing drifts with repetition.
            Assert.Equal(exported.Sql, SqlExporter.Export(second.Document, dialect).Sql);
        }
    }

    [Fact]
    public void Round_tripping_through_another_dialect_keeps_every_table_key_and_relationship()
    {
        Document original = Import(WithoutDanglingKey, SqlDialect.TSql);

        foreach (SqlDialect target in new[] { SqlDialect.Postgres, SqlDialect.MySql, SqlDialect.Sqlite, SqlDialect.TSql })
        {
            SqlExportResult exported = SqlExporter.Export(original, target);
            ImportResult again = SqlImporter.Import(exported.Sql, new SqlImportOptions { Dialect = target, Now = s_now });

            // The nullability of a column the source left implicit is spelled out in T-SQL and left off elsewhere; both read back null.
            Assert.Equal(Structure(original), Structure(again.Document));
            Assert.Empty(again.Warnings);
        }
    }

    [Fact]
    public void Postgres_mysql_and_sqlite_quote_and_qualify_names_their_own_way()
    {
        Document document = Import(SqlFixtures.TSql, SqlDialect.TSql);

        string postgres = SqlExporter.Export(document, SqlDialect.Postgres).Sql;
        Assert.Contains("create table \"journey\".\"OrderLines\"", postgres, StringComparison.Ordinal);
        Assert.Contains("create table \"Customers\"", postgres, StringComparison.Ordinal);
        Assert.Contains("alter table \"journey\".\"Orders\" add constraint \"FK_Orders_Customers\" foreign key (\"CustomerId\") references \"Customers\" (\"CustomerId\")", postgres, StringComparison.Ordinal);
        Assert.Contains("\n;\n", postgres, StringComparison.Ordinal);

        string mysql = SqlExporter.Export(document, SqlDialect.MySql).Sql;
        Assert.Contains("create table `journey_OrderLines`", mysql, StringComparison.Ordinal);
        Assert.Contains("primary key (`OrderId`, `LineNo`)", mysql, StringComparison.Ordinal);
        Assert.DoesNotContain("constraint `PK_", mysql, StringComparison.Ordinal);

        string sqlite = SqlExporter.Export(document, SqlDialect.Sqlite).Sql;
        Assert.DoesNotContain("alter table", sqlite, StringComparison.Ordinal);
        Assert.Contains("constraint \"FK_Orders_Customers\" foreign key (\"CustomerId\") references \"Customers\" (\"CustomerId\")", sqlite, StringComparison.Ordinal);
    }

    [Fact]
    public void Types_are_translated_for_the_target_dialect_and_left_alone_when_they_already_suit_it()
    {
        Document document = Import("""
            create table t (
                a nvarchar(max) null, b datetime2 not null, c bit not null, d uniqueidentifier null, e nvarchar(20) null,
                f int null, g numeric(10,2) null, h money null, i varbinary(max) null, j datetimeoffset null
            )
            """, SqlDialect.TSql);

        string postgres = SqlExporter.Export(document, SqlDialect.Postgres).Sql;
        Assert.Contains("\"a\" text", postgres, StringComparison.Ordinal);
        Assert.Contains("\"b\" timestamp not null", postgres, StringComparison.Ordinal);
        Assert.Contains("\"c\" boolean not null", postgres, StringComparison.Ordinal);
        Assert.Contains("\"d\" uuid", postgres, StringComparison.Ordinal);
        Assert.Contains("\"e\" varchar(20)", postgres, StringComparison.Ordinal);
        Assert.Contains("\"g\" numeric(10, 2)", postgres, StringComparison.Ordinal);
        Assert.Contains("\"i\" bytea", postgres, StringComparison.Ordinal);
        Assert.Contains("\"j\" timestamptz", postgres, StringComparison.Ordinal);

        string mysql = SqlExporter.Export(document, SqlDialect.MySql).Sql;
        Assert.Contains("`a` longtext", mysql, StringComparison.Ordinal);
        Assert.Contains("`b` datetime(6) not null", mysql, StringComparison.Ordinal);
        Assert.Contains("`c` tinyint(1) not null", mysql, StringComparison.Ordinal);
        Assert.Contains("`d` char(36)", mysql, StringComparison.Ordinal);
        Assert.Contains("`i` longblob", mysql, StringComparison.Ordinal);

        string sqlite = SqlExporter.Export(document, SqlDialect.Sqlite).Sql;
        Assert.Contains("\"a\" text", sqlite, StringComparison.Ordinal);
        Assert.Contains("\"e\" nvarchar(20)", sqlite, StringComparison.Ordinal);

        string tsql = SqlExporter.Export(document, SqlDialect.TSql).Sql;
        Assert.Contains("[a] nvarchar(max) null", tsql, StringComparison.Ordinal);
        Assert.Contains("[b] datetime2 not null", tsql, StringComparison.Ordinal);
    }

    [Fact]
    public void Postgres_types_translate_to_tsql_including_serial_as_identity()
    {
        Document document = Import(SqlFixtures.Postgres, SqlDialect.Postgres);

        string tsql = SqlExporter.Export(document, SqlDialect.TSql).Sql;

        Assert.Contains("[id] int identity(1,1) not null", tsql, StringComparison.Ordinal);
        Assert.Contains("[email] varchar(255) not null", tsql, StringComparison.Ordinal);
        Assert.Contains("[created_at] datetimeoffset not null", tsql, StringComparison.Ordinal);
        Assert.Contains("[role] nvarchar(max) not null", tsql, StringComparison.Ordinal);
    }

    private static Document Hand(IEnumerable<Node> nodes, IEnumerable<Edge> edges)
    {
        return new Document("doc_x", 1, "Hand drawn", DocumentType.Architecture, nodes.ToList(), edges.ToList(), new Meta(s_now, s_now, "test"));
    }

    private static Node Entity(string id, string label, params string[] columns)
    {
        return new Node(id, "entity", label, "custom:er/entity")
        {
            Props = new Dictionary<string, JsonElement> { ["columns"] = JsonSerializer.SerializeToElement(columns) },
        };
    }

    private static Edge Relation(string id, string from, string to, ArrowHead start, ArrowHead end, string? label = null)
    {
        return new Edge(id, "association", new Endpoint(from), new Endpoint(to))
        {
            Label = label,
            Style = new EdgeStyle { ArrowStart = start, ArrowEnd = end },
        };
    }

    [Fact]
    public void The_crows_foot_end_is_the_referencing_table_whichever_way_the_edge_was_drawn()
    {
        Node customer = Entity("c", "Customer", "PK Id int not null");
        Node order = Entity("o", "Order", "PK Id int not null", "FK CustomerId int not null");

        // The docs' own example: one customer (start) to many orders (end).
        string forward = SqlExporter.Export(Hand([customer, order], [Relation("e1", "c", "o", ArrowHead.OneAndOnlyOne, ArrowHead.ZeroOrMany, "CustomerId")])).Sql;
        // The importer's direction: the many end (orders) at the start.
        string backward = SqlExporter.Export(Hand([customer, order], [Relation("e1", "o", "c", ArrowHead.ZeroOrMany, ArrowHead.OneAndOnlyOne, "CustomerId")])).Sql;

        const string expected = "alter table [dbo].[Order] add constraint [FK_Order_Customer] foreign key ([CustomerId]) references [dbo].[Customer] ([Id])";
        Assert.Contains(expected, forward, StringComparison.Ordinal);
        Assert.Contains(expected, backward, StringComparison.Ordinal);
        Assert.DoesNotContain("alter table [dbo].[Customer]", forward, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relationship_that_does_not_say_which_columns_gets_columns_added_and_a_warning()
    {
        Node customer = Entity("c", "Customer", "PK Id int not null");
        Node order = Entity("o", "Order", "PK Id int not null");

        SqlExportResult result = SqlExporter.Export(Hand([customer, order], [Relation("e1", "o", "c", ArrowHead.ZeroOrMany, ArrowHead.ZeroOrOne)]));

        Assert.Contains("[Customer_Id] int null", result.Sql, StringComparison.Ordinal);
        Assert.Contains("foreign key ([Customer_Id]) references [dbo].[Customer] ([Id])", result.Sql, StringComparison.Ordinal);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("Customer_Id", warning, StringComparison.Ordinal);
        Assert.Contains("-- warning:", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Foreign_key_columns_marked_fk_are_used_when_the_edge_has_no_label()
    {
        Node customer = Entity("c", "Customer", "PK Id int not null");
        Node order = Entity("o", "Order", "PK Id int not null", "FK BuyerId int not null");

        SqlExportResult result = SqlExporter.Export(Hand([customer, order], [Relation("e1", "o", "c", ArrowHead.ZeroOrMany, ArrowHead.OneAndOnlyOne)]));

        Assert.Contains("foreign key ([BuyerId]) references [dbo].[Customer] ([Id])", result.Sql, StringComparison.Ordinal);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Many_to_many_relationships_and_missing_primary_keys_are_warnings_and_write_no_constraint()
    {
        Node a = Entity("a", "A", "PK Id int not null");
        Node b = Entity("b", "B", "PK Id int not null");
        Node keyless = Entity("k", "Keyless", "Name nvarchar(10) null");

        SqlExportResult manyToMany = SqlExporter.Export(Hand([a, b], [Relation("e1", "a", "b", ArrowHead.ZeroOrMany, ArrowHead.ZeroOrMany)]));
        Assert.DoesNotContain("foreign key", manyToMany.Sql, StringComparison.Ordinal);
        Assert.Contains(manyToMany.Warnings, w => w.Contains("many-to-many", StringComparison.Ordinal));

        SqlExportResult noKey = SqlExporter.Export(Hand([a, keyless], [Relation("e1", "a", "k", ArrowHead.ZeroOrMany, ArrowHead.OneAndOnlyOne, "Id")]));
        Assert.DoesNotContain("foreign key", noKey.Sql, StringComparison.Ordinal);
        Assert.Contains(noKey.Warnings, w => w.Contains("no primary key", StringComparison.Ordinal));
    }

    [Fact]
    public void Nodes_that_are_not_entities_edges_that_are_not_associations_and_empty_entities_are_reported_not_written()
    {
        Node a = Entity("a", "A", "PK Id int not null");
        Node note = new("n", "note", "A note", "rectangle");
        Node empty = Entity("e", "Empty");
        var flow = new Edge("e1", "flow", new Endpoint("a"), new Endpoint("n"));

        SqlExportResult result = SqlExporter.Export(Hand([a, note, empty], [flow]));

        Assert.Contains("create table [dbo].[A]", result.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("[A note]", result.Sql, StringComparison.Ordinal);
        Assert.Equal(2, result.Warnings.Count(w => w.Contains("no table", StringComparison.Ordinal)));
    }

    [Fact]
    public void An_empty_diagram_exports_a_header_only()
    {
        SqlExportResult result = SqlExporter.Export(Hand([], []));

        Assert.DoesNotContain("create table", result.Sql, StringComparison.Ordinal);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Names_that_need_quoting_are_escaped_for_each_dialect()
    {
        Document document = Hand([Entity("t", "Odd]Table", "PK \"a b\" int not null", "c`d int null")], []);

        Assert.Contains("[Odd]]Table]", SqlExporter.Export(document, SqlDialect.TSql).Sql, StringComparison.Ordinal);
        Assert.Contains("`c``d`", SqlExporter.Export(document, SqlDialect.MySql).Sql, StringComparison.Ordinal);
        Assert.Contains("\"a b\" int not null", SqlExporter.Export(document, SqlDialect.Postgres).Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_to_a_table_that_is_not_in_the_diagram_is_the_one_thing_an_import_export_import_loses()
    {
        Document original = Import(SqlFixtures.TSql, SqlDialect.TSql);
        Assert.Contains("FK ActorId int null", SqlImporterTests.Columns(original, "t_Audit"));

        string sql = SqlExporter.Export(original, SqlDialect.TSql).Sql;
        Document again = Import(sql, SqlDialect.TSql);

        Assert.DoesNotContain("Users", sql, StringComparison.Ordinal);
        Assert.Contains("ActorId int null", SqlImporterTests.Columns(again, "t_Audit"));
        Assert.DoesNotContain("FK ActorId int null", SqlImporterTests.Columns(again, "t_Audit"));
    }

    [Fact]
    public void An_auto_dialect_exports_as_tsql()
    {
        Document document = Hand([Entity("t", "T", "PK Id int not null")], []);

        Assert.Equal(SqlExporter.Export(document, SqlDialect.TSql).Sql, SqlExporter.Export(document, SqlDialect.Auto).Sql);
    }
}
