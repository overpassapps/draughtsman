using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Sql;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Core.Tests.Import;

public class SqlImporterTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 29, 1, 2, 3, TimeSpan.Zero);

    private static ImportResult Import(string sql, SqlDialect dialect = SqlDialect.Auto, DocumentType type = DocumentType.Architecture)
    {
        return SqlImporter.Import(sql, new SqlImportOptions { Dialect = dialect, Type = type, Now = s_now });
    }

    internal static string[] Columns(Document document, string nodeId)
    {
        Node node = document.Nodes.Single(n => n.Id == nodeId);
        return node.Props!["columns"].EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    internal static Edge EdgeBetween(Document document, string from, string to)
    {
        return document.Edges.Single(e => e.From.Node == from && e.To.Node == to);
    }

    [Fact]
    public void TSql_script_imports_one_entity_per_table_with_the_default_schema_dropped()
    {
        Document document = Import(SqlFixtures.TSql).Document;

        Assert.Equal(
            new[] { "Customers", "journey.Orders", "Carriers", "journey.OrderLines", "Shipments", "CustomerProfiles", "Passports", "Audit", "Notes" },
            document.Nodes.Select(n => n.Label));

        // OrderLines is the one weak entity: its composite primary key (OrderId, LineNo) includes a foreign key. Shipments'
        // foreign key columns are not in its (single-column) key, and CustomerProfiles' key is one column that is also a foreign key.
        Assert.All(document.Nodes.Where(n => n.Label != "journey.OrderLines"), n => Assert.Equal("entity", n.Kind));
        Assert.All(document.Nodes.Where(n => n.Label != "journey.OrderLines"), n => Assert.Equal("custom:er/entity", n.Shape));
        Node weak = document.Nodes.Single(n => n.Label == "journey.OrderLines");
        Assert.Equal("weak-entity", weak.Kind);
        Assert.Equal("custom:er/weak-entity", weak.Shape);
        Assert.All(document.Nodes, n => Assert.Null(n.Geometry));
        Assert.Equal(DocumentType.Architecture, document.Type);
    }

    [Fact]
    public void TSql_columns_carry_keys_types_and_nullability_in_the_entity_line_format()
    {
        Document document = Import(SqlFixtures.TSql).Document;

        Assert.Equal(
            new[]
            {
                "PK CustomerId int not null",
                "Name nvarchar(100) not null",
                "Email nvarchar(max) null",
                "CreatedAt datetime2 not null",
                "Balance numeric(10, 2) not null",
                "Nickname nvarchar(50) null",
            },
            Columns(document, "t_Customers"));
        Assert.Equal(
            new[] { "PK OrderId int not null", "FK CustomerId int not null", "FK CarrierId int null", "\"Order Note\" nvarchar(200) null" },
            Columns(document, "t_journey_Orders"));
        Assert.Equal(new[] { "PK CarrierId int not null", "Label nvarchar(40) not null" }, Columns(document, "t_Carriers"));
    }

    [Fact]
    public void A_column_that_is_both_primary_and_foreign_key_carries_both_markers_and_a_composite_key_is_one_pk_row_per_column()
    {
        Document document = Import(SqlFixtures.TSql).Document;

        Assert.Equal(
            new[] { "PK FK OrderId int not null", "PK LineNo int not null", "Sku nvarchar(20) not null" },
            Columns(document, "t_journey_OrderLines"));
        Assert.Equal(new[] { "PK FK CustomerId int not null", "Bio nvarchar(max) null" }, Columns(document, "t_CustomerProfiles"));
        Assert.Equal(new[] { "PK ShipmentId int not null", "FK OrderId int not null", "FK LineNo int not null" }, Columns(document, "t_Shipments"));
    }

    [Fact]
    public void Every_listed_foreign_key_becomes_one_edge_from_the_referencing_table_labelled_with_its_columns()
    {
        Document document = Import(SqlFixtures.TSql).Document;

        Assert.Equal(7, document.Edges.Count);
        Assert.Equal(Enumerable.Range(1, 7).Select(i => $"e_{i}"), document.Edges.Select(e => e.Id));
        Assert.All(document.Edges, e => Assert.Equal("association", e.Kind));
        Assert.All(document.Edges, e => Assert.Equal(RoutingMode.Orthogonal, e.Routing?.Mode));

        Assert.Equal("CustomerId", EdgeBetween(document, "t_journey_Orders", "t_Customers").Label);
        Assert.Equal("CarrierId", EdgeBetween(document, "t_journey_Orders", "t_Carriers").Label);
        Assert.Equal("OrderId", EdgeBetween(document, "t_journey_OrderLines", "t_journey_Orders").Label);
        Assert.Equal("OrderId, LineNo", EdgeBetween(document, "t_Shipments", "t_journey_OrderLines").Label);
    }

    [Fact]
    public void A_reference_with_no_column_list_targets_the_primary_key_of_the_referenced_table()
    {
        Document document = Import(SqlFixtures.TSql).Document;

        Edge edge = EdgeBetween(document, "t_journey_Orders", "t_Customers");
        Assert.Equal("t_Customers", edge.To.Node);
        Assert.Equal("CustomerId", edge.Label);
    }

    [Fact]
    public void A_foreign_key_to_a_table_that_is_not_in_the_source_is_a_warning_and_draws_no_edge()
    {
        ImportResult result = Import(SqlFixtures.TSql);

        Assert.DoesNotContain(result.Document.Edges, e => e.From.Node == "t_Audit" || e.To.Node == "t_Audit");
        ImportWarning warning = Assert.Single(result.Warnings, w => w.Code == ImportWarningCodes.MissingReference);
        Assert.Contains("Audit", warning.Message);
        Assert.Contains("Users", warning.Message);
        Assert.Equal("t_Audit", warning.ElementId);
        // The column is still a foreign key column; only the drawing is skipped.
        Assert.Contains("FK ActorId int null", Columns(result.Document, "t_Audit"));
    }

    [Fact]
    public void Foreign_keys_to_tables_that_are_present_draw_edges_the_other_way_round_too()
    {
        // The two directions of one property: the same foreign key, once with its target present and once without.
        const string present = "create table a (id int primary key); create table b (id int primary key, a_id int not null references a);";
        const string absent = "create table b (id int primary key, a_id int not null references a);";

        Assert.Single(Import(present).Document.Edges);
        ImportResult withoutTarget = Import(absent);
        Assert.Empty(withoutTarget.Document.Edges);
        Assert.Contains(withoutTarget.Warnings, w => w.Code == ImportWarningCodes.MissingReference);
    }

    [Fact]
    public void A_plain_foreign_key_is_one_to_many_and_one_on_a_key_or_unique_constraint_is_one_to_one()
    {
        Document document = Import(SqlFixtures.TSql).Document;

        // Plain: many referencing rows, exactly one referenced row.
        EdgeStyle plain = EdgeBetween(document, "t_journey_Orders", "t_Customers").Style!;
        Assert.Equal(ArrowHead.ZeroOrMany, plain.ArrowStart);
        Assert.Equal(ArrowHead.OneAndOnlyOne, plain.ArrowEnd);

        // The foreign key columns are exactly the child's primary key.
        EdgeStyle viaPrimaryKey = EdgeBetween(document, "t_CustomerProfiles", "t_Customers").Style!;
        Assert.Equal(ArrowHead.ZeroOrOne, viaPrimaryKey.ArrowStart);
        Assert.Equal(ArrowHead.OneAndOnlyOne, viaPrimaryKey.ArrowEnd);

        // The foreign key columns are exactly a unique constraint of the child.
        EdgeStyle viaUnique = EdgeBetween(document, "t_Passports", "t_Customers").Style!;
        Assert.Equal(ArrowHead.ZeroOrOne, viaUnique.ArrowStart);
        Assert.Equal(ArrowHead.OneAndOnlyOne, viaUnique.ArrowEnd);

        // Part of a composite primary key is not the whole key, so it stays one-to-many.
        EdgeStyle partOfKey = EdgeBetween(document, "t_journey_OrderLines", "t_journey_Orders").Style!;
        Assert.Equal(ArrowHead.ZeroOrMany, partOfKey.ArrowStart);
    }

    [Fact]
    public void A_nullable_foreign_key_makes_the_referenced_end_optional()
    {
        Document document = Import(SqlFixtures.TSql).Document;

        Assert.Equal(ArrowHead.ZeroOrOne, EdgeBetween(document, "t_journey_Orders", "t_Carriers").Style!.ArrowEnd);
        Assert.Equal(ArrowHead.OneAndOnlyOne, EdgeBetween(document, "t_journey_Orders", "t_Customers").Style!.ArrowEnd);
    }

    [Fact]
    public void Alter_table_add_constraint_foreign_key_adds_an_edge_and_the_check_constraint_follow_up_is_silent()
    {
        ImportResult result = Import(SqlFixtures.TSql);

        Edge edge = EdgeBetween(result.Document, "t_Notes", "t_journey_Orders");
        Assert.Equal("OrderId", edge.Label);
        Assert.Contains("FK OrderId int not null", Columns(result.Document, "t_Notes"));
        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("CHECK CONSTRAINT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Statements_the_importer_does_not_read_are_warnings_naming_the_statement_and_its_line_and_go_and_comments_are_not()
    {
        ImportResult result = Import(SqlFixtures.TSql);

        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.UnsupportedSyntax && w.Message.Contains("SET ANSI_NULLS ON", StringComparison.Ordinal) && w.Line == 2);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.UnsupportedSyntax && w.Message.Contains("SET QUOTED_IDENTIFIER ON", StringComparison.Ordinal) && w.Line == 4);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.UnsupportedSyntax && w.Message.Contains("create index IX_Orders_Customer on journey.Orders", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("GO", StringComparison.Ordinal) && w.Code == ImportWarningCodes.UnsupportedSyntax);
        Assert.DoesNotContain(result.Warnings, w => w.Message.Contains("Object:", StringComparison.Ordinal));

        // Exactly the three skipped statements and the missing reference, in source order.
        Assert.Equal(
            new[] { ImportWarningCodes.UnsupportedSyntax, ImportWarningCodes.UnsupportedSyntax, ImportWarningCodes.MissingReference, ImportWarningCodes.UnsupportedSyntax },
            result.Warnings.Select(w => w.Code));
        Assert.Equal(result.Warnings.Select(w => w.Line).Order(), result.Warnings.Select(w => w.Line));
    }

    [Fact]
    public void Imported_documents_are_schema_valid_and_resolve_every_endpoint()
    {
        foreach (string sql in new[] { SqlFixtures.TSql, SqlFixtures.Postgres, SqlFixtures.MySql, SqlFixtures.Sqlite })
        {
            Document document = Import(sql).Document;

            SchemaCheck schema = ContractFiles.Evaluate(DocumentJson.Serialize(document));
            Assert.True(schema.IsValid, ContractFiles.DescribeErrors(schema));
            var ids = document.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(document.Nodes.Count, ids.Count);
            Assert.All(document.Edges, e => Assert.Contains(e.From.Node, ids));
            Assert.All(document.Edges, e => Assert.Contains(e.To.Node, ids));
            Assert.True(KindVocabulary.For(DocumentType.Architecture).IsKnownNodeKind("entity"));
            Assert.All(document.Nodes, n => Assert.All(ErColumnsOf(n), line => Assert.False(string.IsNullOrWhiteSpace(line))));
        }
    }

    private static IEnumerable<string> ErColumnsOf(Node node)
    {
        return ErShapeLibrary.ColumnLines(node.Props);
    }

    [Fact]
    public void Postgres_dump_reads_serial_casts_arrays_inline_references_and_alter_table_only()
    {
        ImportResult result = Import(SqlFixtures.Postgres, SqlDialect.Postgres);
        Document document = result.Document;

        Assert.Equal(new[] { "users", "posts" }, document.Nodes.Select(n => n.Label));
        Assert.Equal(
            new[]
            {
                "PK id serial not null",
                "email character varying(255) not null",
                "created_at timestamp(3) with time zone not null",
                "role text not null",
                "tags text[] null",
                "score numeric(5, 2) null",
            },
            Columns(document, "t_users"));
        Assert.Equal(
            new[] { "PK id bigserial not null", "FK author_id integer not null", "FK editor_id integer null", "title varchar(200) null" },
            Columns(document, "t_posts"));

        // Two foreign keys from posts to users: the inline one and the ALTER TABLE ONLY one.
        Assert.Equal(new[] { "author_id", "editor_id" }, document.Edges.Select(e => e.Label));
        Assert.All(document.Edges, e => Assert.Equal(("t_posts", "t_users"), (e.From.Node, e.To.Node)));
        Assert.Equal(ArrowHead.OneAndOnlyOne, document.Edges[0].Style!.ArrowEnd);
        Assert.Equal(ArrowHead.ZeroOrOne, document.Edges[1].Style!.ArrowEnd);
        Assert.Contains(result.Warnings, w => w.Message.Contains("CREATE INDEX posts_author_idx", StringComparison.Ordinal));
    }

    [Fact]
    public void MySql_dump_reads_backticks_unsigned_enums_indexes_and_table_options()
    {
        Document document = Import(SqlFixtures.MySql, SqlDialect.MySql).Document;

        Assert.Equal(new[] { "accounts", "invoices" }, document.Nodes.Select(n => n.Label));
        Assert.Equal(
            new[]
            {
                "PK id int(11) unsigned not null",
                "name varchar(64) not null",
                "status enum('active', 'closed') not null",
                "updated timestamp not null",
            },
            Columns(document, "t_accounts"));
        Assert.Equal(new[] { "PK id int not null", "FK account_id int(11) unsigned not null", "memo text null" }, Columns(document, "t_invoices"));
        Edge edge = Assert.Single(document.Edges);
        Assert.Equal(("t_invoices", "t_accounts", "account_id"), (edge.From.Node, edge.To.Node, edge.Label));
    }

    [Fact]
    public void Sqlite_schema_reads_autoincrement_typeless_columns_and_deferrable_references()
    {
        Document document = Import(SqlFixtures.Sqlite, SqlDialect.Sqlite).Document;

        Assert.Equal(new[] { "PK id INTEGER not null", "name TEXT not null" }, Columns(document, "t_artists"));
        Assert.Equal(new[] { "PK id INTEGER not null", "FK artist_id INTEGER not null", "title null" }, Columns(document, "t_albums"));
        Edge edge = Assert.Single(document.Edges);
        Assert.Equal(("t_albums", "t_artists", "artist_id"), (edge.From.Node, edge.To.Node, edge.Label));
    }

    [Theory]
    [InlineData("create table \"Order Items\" (\"Order Id\" int primary key, \"pk\" int)", "Order Items", new[] { "PK \"Order Id\" int not null", "\"pk\" int null" })]
    [InlineData("create table `t` (`a b` int not null)", "t", new[] { "\"a b\" int not null" })]
    [InlineData("create table [my].[t] ([a]] b] int not null)", "my.t", new[] { "\"a] b\" int not null" })]
    [InlineData("create table if not exists s.t (a int)", "s.t", new[] { "a int null" })]
    [InlineData("CREATE TABLE public.t (a int)", "t", new[] { "a int null" })]
    [InlineData("create table db.dbo.t (a int)", "t", new[] { "a int null" })]
    public void Identifier_forms_are_unquoted_and_names_with_spaces_are_requoted_for_the_column_line(string sql, string label, string[] columns)
    {
        Document document = Import(sql).Document;

        Node node = Assert.Single(document.Nodes);
        Assert.Equal(label, node.Label);
        Assert.Equal(columns, Columns(document, node.Id));
    }

    [Theory]
    [InlineData("a int default 'x, (y' not null", "a int not null")]
    [InlineData("a int default (1 + (2 * 3)) not null", "a int not null")]
    [InlineData("a text default 'it''s' not null", "a text not null")]
    [InlineData("a text default 'x'::text not null", "a text not null")]
    [InlineData("a text default 'x'::character varying(10) not null", "a text not null")]
    [InlineData("a int default -1 not null", "a int not null")]
    [InlineData("a datetime default getdate() not null", "a datetime not null")]
    [InlineData("a datetime default getdate() null", "a datetime null")]
    [InlineData("a int default null", "a int null")]
    [InlineData("a int not null default next value for dbo.Seq", "a int not null")]
    [InlineData("a varchar(20) character set utf8 collate utf8_bin not null", "a varchar(20) not null")]
    [InlineData("a int check (a > 0 and a is not null) null", "a int null")]
    [InlineData("a int generated always as identity (start with 5)", "a int not null")]
    [InlineData("a int generated by default as identity", "a int not null")]
    [InlineData("a int not null comment 'not null, really'", "a int not null")]
    [InlineData("a int masked with (function = 'default()') null", "a int null")]
    public void Column_modifiers_are_skipped_safely_and_only_real_nullability_is_read(string column, string expected)
    {
        Document document = Import($"create table t ({column}, b int)").Document;

        Assert.Equal(new[] { expected, "b int null" }, Columns(document, "t_t"));
    }

    [Fact]
    public void Types_keep_their_sizes_and_read_multi_word_forms()
    {
        Document document = Import("""
            create table t (
                a nvarchar(50) not null, b numeric(10,2), c nvarchar(max), d double precision, e timestamp without time zone,
                f dbo.MyType, g "char", h bit varying(8), i int unsigned zerofill, j integer[3], k national character varying(5)
            )
            """).Document;

        Assert.Equal(
            new[]
            {
                "a nvarchar(50) not null",
                "b numeric(10, 2) null",
                "c nvarchar(max) null",
                "d double precision null",
                "e timestamp without time zone null",
                "f dbo.MyType null",
                "g char null",
                "h bit varying(8) null",
                "i int unsigned zerofill null",
                "j integer[3] null",
                "k national character varying(5) null",
            },
            Columns(document, "t_t"));
    }

    [Fact]
    public void Identity_and_serial_columns_are_never_null_and_primary_key_columns_are_never_null()
    {
        Document document = Import("create table t (a int identity, b serial, c int primary key null, d int auto_increment, e int)").Document;

        Assert.Equal(new[] { "a int not null", "b serial not null", "PK c int not null", "d int not null", "e int null" }, Columns(document, "t_t"));
    }

    [Fact]
    public void Table_level_foreign_key_with_clauses_and_a_named_index_word_is_read()
    {
        Document document = Import("""
            create table p (id int primary key);
            create table c (
                id int primary key,
                p_id int,
                foreign key fk_c_p (p_id) references p (id) on delete set null on update cascade not deferrable
            );
            """).Document;

        Edge edge = Assert.Single(document.Edges);
        Assert.Equal("p_id", edge.Label);
        Assert.Equal("t_p", edge.To.Node);
    }

    [Fact]
    public void A_self_referencing_key_draws_a_self_edge()
    {
        Document document = Import("create table Employee (Id int primary key, ManagerId int null references Employee (Id))").Document;

        Edge edge = Assert.Single(document.Edges);
        Assert.Equal(("t_Employee", "t_Employee"), (edge.From.Node, edge.To.Node));
    }

    [Fact]
    public void The_same_foreign_key_stated_twice_is_one_edge()
    {
        Document document = Import("""
            create table p (id int primary key);
            create table c (id int primary key, p_id int not null references p (id));
            alter table c add constraint fk_dup foreign key (p_id) references p (id);
            """).Document;

        Assert.Single(document.Edges);
    }

    [Fact]
    public void Table_names_resolve_ignoring_case_and_the_default_schema()
    {
        Document document = Import("""
            create table dbo.Parent (Id int primary key);
            create table Child (Id int primary key, ParentId int not null references [PARENT] (Id));
            create table Other (Id int primary key, ParentId int not null references public.parent);
            """).Document;

        Assert.Equal(2, document.Edges.Count);
    }

    [Fact]
    public void A_table_in_another_schema_is_not_the_same_table_as_one_with_the_same_name_in_the_default_schema()
    {
        ImportResult result = Import("""
            create table journey.Parent (Id int primary key);
            create table Child (Id int primary key, ParentId int not null references Parent (Id));
            """);

        Assert.Empty(result.Document.Edges);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.MissingReference);
    }

    [Fact]
    public void A_table_defined_twice_keeps_the_first_definition_and_warns()
    {
        ImportResult result = Import("create table t (a int); create table T (b int);");

        Assert.Equal(new[] { "a int null" }, Columns(result.Document, "t_t"));
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.DuplicateTable);
    }

    [Fact]
    public void Temporary_tables_and_create_table_forms_without_a_column_list_are_skipped_with_a_warning()
    {
        ImportResult result = Import("""
            create table #scratch (a int);
            create temporary table tmp (a int);
            create table copy as select * from real;
            create table real (a int);
            """);

        Assert.Equal(new[] { "real" }, result.Document.Nodes.Select(n => n.Label));
        Assert.Equal(3, result.Warnings.Count(w => w.Code == ImportWarningCodes.UnsupportedSyntax));
    }

    [Fact]
    public void Statements_without_terminators_are_split_where_the_next_statement_starts()
    {
        Document document = Import("""
            create table a (id int primary key)
            create table b (id int primary key, a_id int not null references a)
            create table c (id int primary key)
            """).Document;

        Assert.Equal(new[] { "a", "b", "c" }, document.Nodes.Select(n => n.Label));
        Assert.Single(document.Edges);
    }

    [Fact]
    public void T_sql_procedures_run_to_the_next_go_so_their_bodies_are_not_read_as_tables()
    {
        ImportResult result = Import("""
            create table real (id int primary key)
            go
            create procedure dbo.Fill as
            begin
                create table shadow (id int);
                insert into real values (1);
            end
            go
            create table also_real (id int primary key)
            go
            """);

        Assert.Equal(new[] { "real", "also_real" }, result.Document.Nodes.Select(n => n.Label));
    }

    [Fact]
    public void Postgres_dollar_quoted_function_bodies_are_one_string()
    {
        ImportResult result = Import("""
            CREATE FUNCTION f() RETURNS trigger AS $$
            BEGIN
                CREATE TABLE inside_function (a int);
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TABLE real (id int PRIMARY KEY);
            """);

        Assert.Equal(new[] { "real" }, result.Document.Nodes.Select(n => n.Label));
    }

    [Fact]
    public void Freeform_is_accepted_and_flowchart_is_refused()
    {
        Document freeform = Import("create table t (a int)", type: DocumentType.Freeform).Document;

        Assert.Equal(DocumentType.Freeform, freeform.Type);
        Assert.Equal("custom:er/entity", freeform.Nodes.Single().Shape);
        Assert.Throws<ArgumentException>(() => Import("create table t (a int)", type: DocumentType.Flowchart));
    }

    [Fact]
    public void Source_with_no_tables_is_an_empty_diagram_warning_not_a_failure()
    {
        ImportResult result = Import("-- nothing here\nselect 1;");

        Assert.Empty(result.Document.Nodes);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.EmptyDiagram);
    }

    [Fact]
    public void The_title_option_is_used_and_defaults_to_imported_schema()
    {
        Assert.Equal("Imported schema", Import("create table t (a int)").Document.Title);
        Assert.Equal("Shop", SqlImporter.Import("create table t (a int)", new SqlImportOptions { Title = "Shop" }).Document.Title);
    }

    [Fact]
    public void Garbage_between_valid_statements_yields_warnings_and_the_valid_tables_still_import()
    {
        string[] garbage =
        [
            "))) ((( ;;",
            "@@@ ### %%% !!!",
            "select * from nowhere where x = 'terminated'",
            "create table (",
            "alter table",
            "insert into t values (1, 2, 3);",
            "create table broken (a int, , b, 12 34, );",
            "create table half (a int, b",
            "alter table Alpha add constraint x foreign key (",
            "grant select on Alpha to public",
        ];

        for (int seed = 0; seed < 200; seed++)
        {
            var random = new Random(seed);
            var sql = new System.Text.StringBuilder();
            sql.AppendLine("create table Alpha (Id int primary key, Name nvarchar(20) not null);");
            sql.AppendLine(garbage[random.Next(garbage.Length)] + ";");
            sql.AppendLine("go");
            sql.AppendLine("create table Beta (Id int primary key, AlphaId int not null references Alpha (Id));");
            sql.AppendLine(garbage[random.Next(garbage.Length)]);
            sql.AppendLine("go");
            sql.AppendLine("create table Gamma (Id int primary key);");

            ImportResult result = Import(sql.ToString());

            string[] labels = result.Document.Nodes.Select(n => n.Label).ToArray();
            Assert.True(labels.Contains("Alpha") && labels.Contains("Beta") && labels.Contains("Gamma"), $"seed {seed}: got {string.Join(", ", labels)}");
            Assert.Single(result.Document.Edges);
            Assert.NotEmpty(result.Warnings);
        }
    }

    [Theory]
    [InlineData("/* never closed")]
    [InlineData("select 'never closed")]
    [InlineData("create table half (a int, b")]
    public void An_unterminated_comment_or_string_ends_the_source_but_keeps_what_came_before(string tail)
    {
        ImportResult result = Import("create table Alpha (Id int primary key);\n" + tail + "\ncreate table Beta (Id int)");

        Assert.Contains(result.Document.Nodes, n => n.Label == "Alpha");
    }

    [Fact]
    public void Random_garbage_and_every_truncation_of_a_real_script_never_throw()
    {
        var random = new Random(17);
        const string alphabet = "abc CREATE TABLE table()[]\"'`,;.-/*#:\n\t0123 PRIMARY KEY REFERENCES go GO alter add constraint";
        for (int i = 0; i < 300; i++)
        {
            string noise = new(Enumerable.Range(0, random.Next(1, 200)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            ImportResult result = Import(noise);
            Assert.NotNull(result.Document);
        }

        foreach (string sql in new[] { SqlFixtures.TSql, SqlFixtures.Postgres, SqlFixtures.MySql })
        {
            for (int length = 0; length <= sql.Length; length += 7)
            {
                Assert.NotNull(Import(sql[..length]).Document);
            }
        }
    }

    [Fact]
    public void Dialect_is_detected_from_the_text_and_can_be_named()
    {
        Assert.Equal(SqlDialect.TSql, SqlDialects.Detect(SqlFixtures.TSql));
        Assert.Equal(SqlDialect.Postgres, SqlDialects.Detect(SqlFixtures.Postgres));
        Assert.Equal(SqlDialect.MySql, SqlDialects.Detect(SqlFixtures.MySql));
        Assert.Equal(SqlDialect.Sqlite, SqlDialects.Detect(SqlFixtures.Sqlite));

        Assert.True(SqlDialects.TryParse("SQLServer", out SqlDialect named));
        Assert.Equal(SqlDialect.TSql, named);
        Assert.True(SqlDialects.TryParse(null, out SqlDialect none));
        Assert.Equal(SqlDialect.Auto, none);
        Assert.False(SqlDialects.TryParse("oracle", out _));
    }

    [Fact]
    public void A_large_schema_imports_in_linear_time()
    {
        var sql = new System.Text.StringBuilder();
        for (int i = 0; i < 3000; i++)
        {
            sql.Append($"create table T{i} (Id int primary key, ParentId int null references T{Math.Max(0, i - 1)} (Id), Name nvarchar(50) not null);\n");
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        ImportResult result = Import(sql.ToString());
        watch.Stop();

        Assert.Equal(3000, result.Document.Nodes.Count);
        Assert.Equal(3000, result.Document.Edges.Count);
        Assert.True(watch.Elapsed < LoadScale.Scaled(TimeSpan.FromSeconds(10)), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData(SqlDialect.Postgres)]
    [InlineData(SqlDialect.MySql)]
    [InlineData(SqlDialect.Sqlite)]
    public void A_wrong_dialect_hint_still_reads_a_bracketed_script(SqlDialect wrong)
    {
        Document document = Import(SqlFixtures.TSql, wrong).Document;

        Assert.Equal(9, document.Nodes.Count);
        Assert.Equal(7, document.Edges.Count);
        Assert.Contains("\"Order Note\" nvarchar(200) null", Columns(document, "t_journey_Orders"));
    }

    [Fact]
    public void Column_lines_round_trip_through_the_entity_parser()
    {
        Document document = Import(SqlFixtures.TSql).Document;

        foreach (Node node in document.Nodes)
        {
            foreach (string line in Columns(document, node.Id))
            {
                ErColumn parsed = ErShapeLibrary.ParseColumn(line);
                Assert.False(string.IsNullOrEmpty(parsed.Name), line);
                Assert.NotNull(parsed.Nullable);
            }
        }

        ErColumn key = ErShapeLibrary.ParseColumn(Columns(document, "t_journey_OrderLines")[0]);
        Assert.Equal(new[] { "PK", "FK" }, key.Keys);
        Assert.Equal("OrderId", key.Name);
        Assert.Equal("int", key.Type);
    }

    [Fact]
    public void Props_are_json_arrays_of_strings()
    {
        Document document = Import("create table t (a int)").Document;

        JsonElement columns = document.Nodes.Single().Props!["columns"];
        Assert.Equal(JsonValueKind.Array, columns.ValueKind);
    }
}
