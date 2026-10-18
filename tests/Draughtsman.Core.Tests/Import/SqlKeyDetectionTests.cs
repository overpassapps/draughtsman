using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Sql;
using Draughtsman.Core.Models;

namespace Draughtsman.Core.Tests.Import;

/// <summary>What a table's keys say about it: a unique key on the foreign key columns is one-to-one, and a foreign key inside a composite primary key is a weak entity.</summary>
public class SqlKeyDetectionTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 29, 1, 2, 3, TimeSpan.Zero);

    private static ImportResult Import(string sql, SqlDialect dialect = SqlDialect.Auto, DocumentType type = DocumentType.Architecture)
    {
        return SqlImporter.Import(sql, new SqlImportOptions { Dialect = dialect, Type = type, Now = s_now });
    }

    private static bool IsOneToOne(ImportResult result, string child = "t_Child", string parent = "t_Parent")
    {
        ArrowHead? start = SqlImporterTests.EdgeBetween(result.Document, child, parent).Style!.ArrowStart;
        Assert.True(start is ArrowHead.ZeroOrOne or ArrowHead.ZeroOrMany, $"unexpected crow's foot {start}");
        return start == ArrowHead.ZeroOrOne;
    }

    private const string Tables = """
        CREATE TABLE dbo.Parent (Id int NOT NULL PRIMARY KEY, Code int NOT NULL);
        CREATE TABLE dbo.Child (Id int NOT NULL PRIMARY KEY, ParentId int NOT NULL, Other int NULL,
            CONSTRAINT FK_Child_Parent FOREIGN KEY (ParentId) REFERENCES dbo.Parent (Id));
        """;

    #region One-to-one

    [Fact]
    public void A_foreign_key_with_no_unique_key_is_one_to_many()
    {
        Assert.False(IsOneToOne(Import(Tables)));
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX UX_Child_Parent ON dbo.Child (ParentId);")]
    [InlineData("CREATE UNIQUE NONCLUSTERED INDEX [UX_Child_Parent] ON [dbo].[Child] ([ParentId] ASC) WITH (PAD_INDEX = OFF, IGNORE_DUP_KEY = OFF) ON [PRIMARY]\nGO")]
    [InlineData("CREATE UNIQUE CLUSTERED INDEX UX ON dbo.Child (ParentId DESC)")]
    [InlineData("CREATE UNIQUE INDEX IF NOT EXISTS ux ON dbo.Child USING btree (ParentId);")]
    [InlineData("CREATE UNIQUE INDEX CONCURRENTLY ux ON ONLY dbo.Child (ParentId);")]
    [InlineData("CREATE UNIQUE INDEX ux ON dbo.Child (ParentId) INCLUDE (Other);")]
    [InlineData("CREATE UNIQUE INDEX ux ON dbo.Child (ParentId) WHERE ParentId IS NOT NULL;")]
    [InlineData("CREATE UNIQUE INDEX ux ON dbo.Child (ParentId) WHERE (ParentId IS NOT NULL) AND (Other IS NOT NULL);")]
    public void A_unique_index_on_exactly_the_foreign_key_columns_makes_the_edge_one_to_one(string index)
    {
        ImportResult result = Import(Tables + "\n" + index);

        Assert.True(IsOneToOne(result));
        Assert.DoesNotContain(result.Warnings, w => w.Code is ImportWarningCodes.UnsupportedSyntax or ImportWarningCodes.ParseError or ImportWarningCodes.MissingReference);
    }

    [Fact]
    public void A_unique_constraint_on_exactly_the_foreign_key_columns_makes_the_edge_one_to_one_written_either_way()
    {
        const string inColumn = "CREATE TABLE P (Id int PRIMARY KEY); CREATE TABLE Child (Id int PRIMARY KEY, ParentId int NOT NULL UNIQUE REFERENCES P(Id));";
        const string inTable = "CREATE TABLE P (Id int PRIMARY KEY); CREATE TABLE Child (Id int PRIMARY KEY, ParentId int NOT NULL, CONSTRAINT UQ UNIQUE (ParentId), FOREIGN KEY (ParentId) REFERENCES P(Id));";
        const string viaAlter = "CREATE TABLE P (Id int PRIMARY KEY); CREATE TABLE Child (Id int PRIMARY KEY, ParentId int NOT NULL REFERENCES P(Id)); ALTER TABLE Child ADD CONSTRAINT UQ UNIQUE (ParentId);";
        const string none = "CREATE TABLE P (Id int PRIMARY KEY); CREATE TABLE Child (Id int PRIMARY KEY, ParentId int NOT NULL REFERENCES P(Id));";

        Assert.True(IsOneToOne(Import(inColumn), "t_Child", "t_P"));
        Assert.True(IsOneToOne(Import(inTable), "t_Child", "t_P"));
        Assert.True(IsOneToOne(Import(viaAlter), "t_Child", "t_P"));
        Assert.False(IsOneToOne(Import(none), "t_Child", "t_P"));
    }

    [Fact]
    public void A_composite_foreign_key_is_one_to_one_only_when_the_unique_key_covers_exactly_all_its_columns_in_any_order()
    {
        const string head = """
            CREATE TABLE P (A int NOT NULL, B int NOT NULL, PRIMARY KEY (A, B));
            CREATE TABLE Child (Id int PRIMARY KEY, A int NOT NULL, B int NOT NULL, C int NULL,
                FOREIGN KEY (A, B) REFERENCES P (A, B));
            """;

        Assert.True(IsOneToOne(Import(head + "CREATE UNIQUE INDEX ux ON Child (B, A);"), "t_Child", "t_P"));
        Assert.False(IsOneToOne(Import(head + "CREATE UNIQUE INDEX ux ON Child (A);"), "t_Child", "t_P"));
        Assert.False(IsOneToOne(Import(head + "CREATE UNIQUE INDEX ux ON Child (A, B, C);"), "t_Child", "t_P"));
    }

    [Theory]
    [InlineData("CREATE INDEX ix ON dbo.Child (ParentId);")]
    [InlineData("CREATE UNIQUE INDEX ux ON dbo.Child (Other);")]
    [InlineData("CREATE UNIQUE INDEX ux ON dbo.Child (ParentId, Other);")]
    public void A_plain_index_or_a_unique_index_on_other_columns_leaves_it_one_to_many(string index)
    {
        Assert.False(IsOneToOne(Import(Tables + "\n" + index)));
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX ux ON dbo.Child (ParentId) WHERE Other = 0;", "filtered")]
    [InlineData("CREATE UNIQUE INDEX ux ON dbo.Child (lower(ParentId));", "plain list")]
    [InlineData("CREATE UNIQUE INDEX ux ON dbo.Child (ParentId + 1);", "plain list")]
    public void A_filtered_or_expression_unique_index_proves_nothing_so_it_stays_one_to_many_with_a_warning(string index, string reason)
    {
        ImportResult result = Import(Tables + "\n" + index);

        Assert.False(IsOneToOne(result));
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.UnsupportedSyntax && w.Message.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void A_unique_index_on_a_table_that_is_not_in_the_source_is_a_missing_reference_warning()
    {
        ImportResult result = Import(Tables + "\nCREATE UNIQUE INDEX ux ON dbo.Elsewhere (ParentId);");

        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.MissingReference && w.Message.Contains("Elsewhere", StringComparison.Ordinal));
        Assert.False(IsOneToOne(result));
    }

    #endregion

    #region Weak entities

    private static string KindOf(ImportResult result, string id)
    {
        return result.Document.Nodes.Single(n => n.Id == id).Kind;
    }

    [Fact]
    public void A_composite_primary_key_that_includes_a_foreign_key_column_is_a_weak_entity()
    {
        const string sql = """
            CREATE TABLE Orders (Id int PRIMARY KEY);
            CREATE TABLE OrderLines (OrderId int NOT NULL, LineNo int NOT NULL, Sku nvarchar(20),
                PRIMARY KEY (OrderId, LineNo),
                FOREIGN KEY (OrderId) REFERENCES Orders (Id));
            """;

        ImportResult result = Import(sql);

        Node weak = result.Document.Nodes.Single(n => n.Id == "t_OrderLines");
        Assert.Equal("weak-entity", weak.Kind);
        Assert.Equal("custom:er/weak-entity", weak.Shape);
        Assert.Equal("entity", KindOf(result, "t_Orders"));
    }

    [Theory]
    [InlineData("CREATE TABLE Child (ParentId int NOT NULL PRIMARY KEY REFERENCES Parent (Id))", "a single-column key that is also a foreign key")]
    [InlineData("CREATE TABLE Child (Id int, ParentId int NOT NULL REFERENCES Parent (Id), PRIMARY KEY (Id))", "a foreign key outside the primary key")]
    [InlineData("CREATE TABLE Child (A int, B int, ParentId int REFERENCES Parent (Id), PRIMARY KEY (A, B))", "a composite key with no foreign key column")]
    [InlineData("CREATE TABLE Child (A int, ParentId int, PRIMARY KEY (A, ParentId))", "a composite key with no foreign key at all")]
    [InlineData("CREATE TABLE Child (A int, ParentId int REFERENCES Elsewhere (Id), PRIMARY KEY (A, ParentId))", "a foreign key to a table that is not in the source")]
    [InlineData("CREATE TABLE Child (A int, ParentId int REFERENCES Child (A), PRIMARY KEY (A, ParentId))", "a foreign key to itself")]
    [InlineData("CREATE TABLE Child (A int, ParentId int)", "no primary key")]
    public void Every_other_table_stays_an_entity(string child, string why)
    {
        ImportResult result = Import("CREATE TABLE Parent (Id int PRIMARY KEY);\n" + child + ";");

        Assert.True(KindOf(result, "t_Child") == "entity", why);
        Assert.Equal("custom:er/entity", result.Document.Nodes.Single(n => n.Id == "t_Child").Shape);
    }

    [Fact]
    public void A_key_added_by_alter_table_counts_and_so_does_a_freeform_import()
    {
        const string sql = """
            CREATE TABLE Parent (Id int PRIMARY KEY);
            CREATE TABLE Child (ParentId int NOT NULL, Seq int NOT NULL);
            ALTER TABLE Child ADD CONSTRAINT PK_Child PRIMARY KEY (ParentId, Seq);
            ALTER TABLE Child ADD CONSTRAINT FK_Child FOREIGN KEY (ParentId) REFERENCES Parent (Id);
            """;

        Assert.Equal("weak-entity", KindOf(Import(sql), "t_Child"));
        Assert.Equal("weak-entity", KindOf(Import(sql, type: DocumentType.Freeform), "t_Child"));
    }

    [Fact]
    public void A_weak_entity_survives_export_and_import_again_and_an_implied_link_does_not_make_one()
    {
        const string sql = """
            CREATE TABLE Orders (Id int PRIMARY KEY);
            CREATE TABLE OrderLines (OrderId int NOT NULL, LineNo int NOT NULL, PRIMARY KEY (OrderId, LineNo),
                FOREIGN KEY (OrderId) REFERENCES Orders (Id));
            CREATE TABLE Notes (OrderId int NOT NULL, Seq int NOT NULL, PRIMARY KEY (OrderId, Seq)); -- placeholder
            -- Notes.OrderId -> Orders.Id
            """;
        Document first = Import(sql).Document;

        Document second = SqlImporter.Import(SqlExporter.Export(first, SqlDialect.TSql).Sql, new SqlImportOptions { Now = s_now }).Document;

        Assert.Equal("weak-entity", first.Nodes.Single(n => n.Id == "t_OrderLines").Kind);
        Assert.Equal("weak-entity", second.Nodes.Single(n => n.Id == "t_OrderLines").Kind);
        Assert.Equal("entity", first.Nodes.Single(n => n.Id == "t_Notes").Kind);
        Assert.Equal(first.Nodes.Select(n => n.Kind), second.Nodes.Select(n => n.Kind));
    }

    #endregion
}
