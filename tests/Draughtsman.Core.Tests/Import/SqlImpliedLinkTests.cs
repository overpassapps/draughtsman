using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Sql;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// Relationships a schema's comments declare that the database does not enforce (docs/er-entities.md). The SQL is
/// synthetic, in the style of an annotated production export, never a real client's.
/// </summary>
public class SqlImpliedLinkTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 29, 1, 2, 3, TimeSpan.Zero);

    /// <summary>Two links in the header block (one of them repeated inline), one only inline with a note after it, and a real foreign key.</summary>
    private const string Annotated = """
        /*
           Relationships the database does NOT enforce (no foreign key), but the code relies on:
             dbo.Widgets.KindId      -> dbo.WidgetKinds.Id
             dbo.Widgets.OwnerId     -> dbo.Owners.Id
             dbo.AuditTrail.WidgetId -> dbo.Widgets.Id
        */
        CREATE TABLE dbo.WidgetKinds (Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL);
        GO
        CREATE TABLE dbo.Owners (Id int NOT NULL PRIMARY KEY);
        GO
        CREATE TABLE dbo.Widgets
        (
            Id      int IDENTITY PRIMARY KEY,
            KindId  int NOT NULL DEFAULT 1,     -- -> WidgetKinds.Id (no FK)
            OwnerId int                          -- -> Owners.Id (no FK); the legacy owner concept
        );
        GO
        CREATE TABLE dbo.AuditTrail
        (
            Id       int IDENTITY PRIMARY KEY,
            WidgetId nvarchar(50)
        );
        GO
        """;

    private static ImportResult Import(string sql, bool implied = true, DocumentType type = DocumentType.Architecture)
    {
        return SqlImporter.Import(sql, new SqlImportOptions { Implied = implied, Type = type, Now = s_now });
    }

    private static Edge[] Dashed(Document document)
    {
        return document.Edges.Where(e => e.Style?.Dash == Dash.Dashed).ToArray();
    }

    [Fact]
    public void Header_and_inline_links_draw_one_dashed_edge_each_labelled_with_the_column_and_no_fk()
    {
        Document document = Import(Annotated).Document;

        // The header lists three; two of them are repeated inline, and a repeat is still one edge.
        Edge[] edges = Dashed(document);
        Assert.Equal(3, edges.Length);
        Assert.Equal(3, document.Edges.Count);
        Assert.Equal(
            new[] { "t_AuditTrail>t_Widgets:WidgetId (no FK)", "t_Widgets>t_Owners:OwnerId (no FK)", "t_Widgets>t_WidgetKinds:KindId (no FK)" },
            edges.Select(e => $"{e.From.Node}>{e.To.Node}:{e.Label}").Order(StringComparer.Ordinal));
        Assert.All(edges, e => Assert.Equal("association", e.Kind));
    }

    [Fact]
    public void An_implied_edge_attaches_to_the_column_rows_and_has_the_crows_foot_ends_a_real_foreign_key_has()
    {
        Document document = Import(Annotated).Document;

        Edge kind = SqlImporterTests.EdgeBetween(document, "t_Widgets", "t_WidgetKinds");
        Assert.Equal("col:KindId", kind.From.Port);
        Assert.Equal("col:Id", kind.To.Port);
        Assert.Equal(ArrowHead.ZeroOrMany, kind.Style!.ArrowStart);
        Assert.Equal(ArrowHead.OneAndOnlyOne, kind.Style.ArrowEnd); // NOT NULL: the referenced row must exist
        Assert.Equal(RoutingMode.Orthogonal, kind.Routing!.Mode);

        // OwnerId is nullable, so the referenced end is optional.
        Assert.Equal(ArrowHead.ZeroOrOne, SqlImporterTests.EdgeBetween(document, "t_Widgets", "t_Owners").Style!.ArrowEnd);
    }

    [Fact]
    public void The_implied_column_is_not_marked_fk_because_the_database_has_no_foreign_key_there()
    {
        Document document = Import(Annotated).Document;

        Assert.Equal(new[] { "PK Id int not null", "KindId int not null", "OwnerId int null" }, SqlImporterTests.Columns(document, "t_Widgets"));
    }

    [Fact]
    public void Switching_implied_off_draws_the_foreign_keys_only_and_raises_no_warning_about_the_comments()
    {
        const string sql = Annotated + "\nCREATE TABLE dbo.Ghosts (Id int PRIMARY KEY, OwnerId int); -- placeholder\n";
        const string withGhostLink = sql + "-- dbo.Ghosts.OwnerId -> dbo.Nowhere.Id\n";

        ImportResult on = Import(withGhostLink);
        ImportResult off = Import(withGhostLink, implied: false);

        Assert.Equal(3, on.Document.Edges.Count);
        Assert.Contains(on.Warnings, w => w.Code == ImportWarningCodes.ImpliedLinkSkipped);
        Assert.Empty(off.Document.Edges);
        Assert.DoesNotContain(off.Warnings, w => w.Code == ImportWarningCodes.ImpliedLinkSkipped);
        Assert.Equal(on.Document.Nodes.Select(n => n.Id), off.Document.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void Switching_implied_off_keeps_the_real_foreign_keys()
    {
        const string sql = """
            CREATE TABLE a (Id int PRIMARY KEY);
            CREATE TABLE b (Id int PRIMARY KEY, AId int NOT NULL REFERENCES a(Id), OtherId int); -- OtherId -> a.Id (no FK)
            -- b.OtherId -> a.Id (no FK)
            """;

        Assert.Equal(2, Import(sql).Document.Edges.Count);
        Edge kept = Assert.Single(Import(sql, implied: false).Document.Edges);
        Assert.Null(kept.Style!.Dash);
        Assert.Equal("AId", kept.Label);
    }

    [Theory]
    [InlineData("-- the owner ( -> Owners.Id ) is set on first login")]
    [InlineData("-- OwnerId -> Owners.Id (no FK).")]
    [InlineData("-- -> Owners.Id is where this points")]
    [InlineData("-- Owners.Id <- Widgets.OwnerId")]
    [InlineData("-- -> see the wiki")]
    [InlineData("-- maps Widgets.OwnerId -> Owners.Id when the legacy flag is set")]
    [InlineData("-- => Owners.Id")]
    [InlineData("-- see Widgets.OwnerId -> Owners.Id (no FK)")]
    [InlineData("-- TODO: -> Owners.Id (no FK)")]
    public void A_comment_that_mentions_an_arrow_in_prose_draws_nothing_and_warns_nothing(string comment)
    {
        string sql = $"""
            CREATE TABLE Owners (Id int PRIMARY KEY);
            {comment}
            CREATE TABLE Widgets
            (
                Id int PRIMARY KEY,
                OwnerId int {comment}
            );
            """;

        ImportResult result = Import(sql);

        Assert.Empty(result.Document.Edges);
        Assert.DoesNotContain(result.Warnings, w => w.Code == ImportWarningCodes.ImpliedLinkSkipped);
    }

    [Fact]
    public void The_same_accepted_shapes_do_draw_so_the_prose_test_above_is_not_passing_because_nothing_is_ever_read()
    {
        const string sql = """
            CREATE TABLE Owners (Id int PRIMARY KEY);
            -- Widgets.OwnerId -> Owners.Id
            CREATE TABLE Widgets (Id int PRIMARY KEY, OwnerId int);
            CREATE TABLE Gadgets (Id int PRIMARY KEY, OwnerId int -- -> Owners.Id
            );
            """;

        Assert.Equal(2, Import(sql).Document.Edges.Count);
    }

    [Fact]
    public void Bracketed_and_quoted_names_a_bullet_and_three_part_names_are_read()
    {
        const string sql = """
            CREATE TABLE [journey].[User Identities] ([Identity Id] int PRIMARY KEY);
            CREATE TABLE dbo.Logins (Id int PRIMARY KEY, [Identity Id] int);
            /*
             * - dbo.Logins.[Identity Id] -> [journey].[User Identities].[Identity Id]
             */
            """;

        Edge edge = Assert.Single(Import(sql).Document.Edges);

        Assert.Equal("t_journey_User_Identities", edge.To.Node);
        Assert.Equal("Identity Id (no FK)", edge.Label);
        Assert.Equal("col:Identity%20Id", edge.From.Port);
        Assert.Equal("col:Identity%20Id", edge.To.Port);
    }

    [Fact]
    public void A_link_to_a_table_or_column_that_is_not_in_the_source_is_one_warning_and_no_edge()
    {
        const string sql = """
            CREATE TABLE Widgets (Id int PRIMARY KEY, OwnerId int, KindId int, ColourId int, ShapeId int,
                Note nvarchar(10)); -- Note -> Widgets.Id
            -- Widgets.OwnerId -> Owners.Id
            -- Widgets.KindId -> Widgets.NoSuchColumn
            -- Nowhere.Thing -> Widgets.Id
            -- Widgets.NoSuchColumn -> Widgets.Id
            """;

        ImportResult result = Import(sql);

        Assert.Empty(result.Document.Edges);
        ImportWarning[] skipped = result.Warnings.Where(w => w.Code == ImportWarningCodes.ImpliedLinkSkipped).ToArray();
        Assert.Equal(new int?[] { 3, 4, 5, 6 }, skipped.Select(w => w.Line));
        Assert.Contains("Owners", skipped[0].Message, StringComparison.Ordinal);
        Assert.Contains("NoSuchColumn", skipped[1].Message, StringComparison.Ordinal);
        Assert.Contains("Nowhere", skipped[2].Message, StringComparison.Ordinal);
        Assert.Contains("NoSuchColumn", skipped[3].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inline_arrow_that_is_not_on_a_column_line_is_a_warning_because_it_does_not_say_which_column()
    {
        const string sql = """
            CREATE TABLE Owners (Id int PRIMARY KEY);
            CREATE TABLE Widgets
            (
                -- -> Owners.Id (no FK)
                Id int PRIMARY KEY,
                OwnerId int
            );
            -- -> Owners.Id
            """;

        ImportResult result = Import(sql);

        Assert.Empty(result.Document.Edges);
        Assert.Equal(new int?[] { 4, 8 }, result.Warnings.Where(w => w.Code == ImportWarningCodes.ImpliedLinkSkipped).Select(w => w.Line));
    }

    [Fact]
    public void A_link_a_real_foreign_key_already_declares_is_not_drawn_twice_but_a_different_column_pair_still_is()
    {
        const string sql = """
            CREATE TABLE Owners (Id int PRIMARY KEY, Code int NOT NULL);
            CREATE TABLE Widgets
            (
                Id int PRIMARY KEY,
                OwnerId int NOT NULL REFERENCES Owners(Id),   -- -> Owners.Id (no FK)
                OwnerCode int NOT NULL                        -- -> Owners.Code (no FK)
            );
            -- Widgets.OwnerId -> Owners.Id
            """;

        Document document = Import(sql).Document;

        Assert.Equal(2, document.Edges.Count);
        Edge real = document.Edges.Single(e => e.Style?.Dash is null);
        Assert.Equal("OwnerId", real.Label);
        Edge implied = document.Edges.Single(e => e.Style?.Dash == Dash.Dashed);
        Assert.Equal("OwnerCode (no FK)", implied.Label);
        Assert.Equal("col:Code", implied.To.Port);
    }

    [Fact]
    public void A_real_foreign_key_with_no_column_list_points_at_the_primary_key_so_a_link_to_it_is_a_duplicate_and_a_link_to_another_column_is_not()
    {
        const string sql = """
            CREATE TABLE Owners (Id int PRIMARY KEY, Code int NOT NULL);
            CREATE TABLE Widgets (Id int PRIMARY KEY, OwnerId int NOT NULL REFERENCES Owners);
            -- Widgets.OwnerId -> Owners.Id
            -- Widgets.OwnerId -> Owners.Code
            """;

        Document document = Import(sql).Document;

        Assert.Equal(2, document.Edges.Count);
        Assert.Equal("col:Code", document.Edges.Single(e => e.Style?.Dash == Dash.Dashed).To.Port);
    }

    [Fact]
    public void An_implied_link_on_a_column_that_is_unique_is_one_to_one()
    {
        const string sql = """
            CREATE TABLE Owners (Id int PRIMARY KEY);
            CREATE TABLE Profiles (Id int PRIMARY KEY, OwnerId int NOT NULL UNIQUE); -- -> Owners.Id (no FK)
            """;

        Edge edge = Assert.Single(Import(sql).Document.Edges);

        Assert.Equal(ArrowHead.ZeroOrOne, edge.Style!.ArrowStart);
    }

    [Fact]
    public void Freeform_documents_get_the_same_edges()
    {
        Assert.Equal(3, Import(Annotated, type: DocumentType.Freeform).Document.Edges.Count);
    }

    [Fact]
    public void The_document_with_implied_edges_is_schema_valid_and_every_port_names_a_real_column()
    {
        Document document = Import(Annotated).Document;

        SchemaCheck schema = ContractFiles.Evaluate(DocumentJson.Serialize(document));
        Assert.True(schema.IsValid, ContractFiles.DescribeErrors(schema));
        Assert.All(document.Edges, e =>
        {
            Assert.NotNull(Schemas.ErShapeLibrary.ColumnRowCentre(document.Nodes.Single(n => n.Id == e.From.Node), e.From.Port));
            Assert.NotNull(Schemas.ErShapeLibrary.ColumnRowCentre(document.Nodes.Single(n => n.Id == e.To.Node), e.To.Port));
        });
    }

    [Fact]
    public void The_ddl_export_does_not_turn_an_implied_edge_into_a_constraint_and_a_round_trip_keeps_the_real_keys()
    {
        const string sql = Annotated + "\nCREATE TABLE dbo.Notes (Id int PRIMARY KEY, WidgetId int NOT NULL REFERENCES dbo.Widgets(Id));\n";
        Document first = Import(sql).Document;
        Assert.Equal(4, first.Edges.Count);

        SqlExportResult exported = SqlExporter.Export(first, SqlDialect.TSql);
        ImportResult second = SqlImporter.Import(exported.Sql, new SqlImportOptions { Now = s_now });

        Assert.Equal(1, exported.Sql.Split("foreign key", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, exported.Sql.Split("references", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("references [dbo].[WidgetKinds]", exported.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("references [dbo].[Owners]", exported.Sql, StringComparison.Ordinal);
        Assert.Empty(exported.Warnings);
        Assert.Empty(Dashed(second.Document));
        Edge real = Assert.Single(second.Document.Edges);
        Assert.Equal("t_Notes", real.From.Node);
        Assert.Equal(first.Nodes.Select(n => n.Label), second.Document.Nodes.Select(n => n.Label));
    }
}
