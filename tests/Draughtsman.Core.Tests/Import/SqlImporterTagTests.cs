using System.Text.Json;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Sql;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Tests.Import;

/// <summary>Table tags: a <c>[TAG]</c> in the comment above a CREATE TABLE tags the table and recolours it (docs/er-entities.md).</summary>
public class SqlImporterTagTests
{
    private const string Tagged = """
        -- [NEW] Orders arrive here.
        -- More about orders.
        CREATE TABLE dbo.Orders (Id int NOT NULL PRIMARY KEY);
        GO

        /* [LEGACY] Old invoices,
           kept for reporting. */
        CREATE TABLE dbo.Invoices (Id int NOT NULL PRIMARY KEY, OrderId int NOT NULL REFERENCES dbo.Orders(Id));
        GO

        -- [Shared] Lookup used by both.
        CREATE TABLE dbo.Countries (Id int NOT NULL PRIMARY KEY);
        GO

        -- [NEW] A second new table.
        CREATE TABLE dbo.Carts (Id int NOT NULL PRIMARY KEY);
        GO

        -- No tag here, just prose.
        CREATE TABLE dbo.Plain (Id int NOT NULL PRIMARY KEY);
        GO

        -- [NEW] separated from the table by a blank line, so not its tag

        CREATE TABLE dbo.Detached (Id int NOT NULL PRIMARY KEY);
        """;

    private static Node Table(ImportResult result, string label)
    {
        return result.Document.Nodes.Single(n => n.Label == label);
    }

    private static string? Tag(Node node)
    {
        return node.Props is { } props && props.TryGetValue("tag", out JsonElement tag) ? tag.GetString() : null;
    }

    [Fact]
    public void Tags_are_read_from_line_and_block_comments_directly_above_the_table()
    {
        ImportResult result = SqlImporter.Import(Tagged);

        Assert.Equal("NEW", Tag(Table(result, "Orders")));
        Assert.Equal("LEGACY", Tag(Table(result, "Invoices")));
        Assert.Equal("Shared", Tag(Table(result, "Countries")));
        Assert.Equal("NEW", Tag(Table(result, "Carts")));
        Assert.Null(Tag(Table(result, "Plain")));
        Assert.Null(Tag(Table(result, "Detached")));
    }

    [Fact]
    public void Each_distinct_tag_gets_its_own_fill_and_the_same_tag_shares_it()
    {
        ImportResult result = SqlImporter.Import(Tagged);

        NodeStyle orders = Table(result, "Orders").Style!;
        NodeStyle invoices = Table(result, "Invoices").Style!;
        Assert.NotEqual(orders.Fill, invoices.Fill);
        Assert.NotEqual(orders.Stroke, invoices.Stroke);
        Assert.Equal(orders, Table(result, "Carts").Style);
        foreach (NodeStyle style in new[] { orders, invoices })
        {
            Assert.False(string.IsNullOrEmpty(style.Fill));
            Assert.False(string.IsNullOrEmpty(style.Stroke));
            Assert.False(string.IsNullOrEmpty(style.TextColor));
        }
    }

    [Fact]
    public void Shared_and_untagged_tables_stay_neutral()
    {
        ImportResult result = SqlImporter.Import(Tagged);

        Assert.Null(Table(result, "Countries").Style);
        Assert.Null(Table(result, "Plain").Style);
        Assert.Null(Table(result, "Detached").Style);
    }

    [Fact]
    public void A_shared_tag_appearing_first_does_not_use_up_a_colour()
    {
        ImportResult result = SqlImporter.Import("-- [SHARED]\nCREATE TABLE A (Id int);\n-- [X]\nCREATE TABLE B (Id int);\n");
        ImportResult withoutShared = SqlImporter.Import("-- [X]\nCREATE TABLE B (Id int);\n");

        Assert.Equal(Table(withoutShared, "B").Style, Table(result, "B").Style);
    }

    [Fact]
    public void A_legend_holds_one_swatch_per_tag_in_that_tags_style()
    {
        ImportResult result = SqlImporter.Import(Tagged);

        Node legend = result.Document.Nodes.Single(n => n.Id == "legend");
        Assert.Equal("Legend", legend.Label);
        List<Node> swatches = result.Document.Nodes.Where(n => n.Parent == "legend").ToList();
        Assert.Equal(new[] { "NEW", "LEGACY", "Shared" }, swatches.Select(s => s.Label));
        Assert.Equal(Table(result, "Orders").Style, swatches[0].Style);
        Assert.Equal(Table(result, "Invoices").Style, swatches[1].Style);
        // The shared swatch is an explicit neutral, distinct from both tag colours.
        Assert.NotNull(swatches[2].Style?.Fill);
        Assert.DoesNotContain(swatches[2].Style!.Fill, new[] { swatches[0].Style!.Fill, swatches[1].Style!.Fill });
    }

    [Fact]
    public void No_tags_means_no_legend_and_the_same_output_as_tags_off()
    {
        const string plain = "-- just a note\nCREATE TABLE A (Id int NOT NULL PRIMARY KEY);\nCREATE TABLE B (Id int, AId int REFERENCES A(Id));\n";
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        ImportResult on = SqlImporter.Import(plain, new SqlImportOptions { Now = now });
        ImportResult off = SqlImporter.Import(plain, new SqlImportOptions { Now = now, Tags = false });

        Assert.DoesNotContain(on.Document.Nodes, n => n.Id == "legend");
        Assert.Equal(Normalise(off), Normalise(on));
    }

    [Fact]
    public void Tags_off_ignores_tags_completely()
    {
        DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ImportResult off = SqlImporter.Import(Tagged, new SqlImportOptions { Now = now, Tags = false });

        Assert.DoesNotContain(off.Document.Nodes, n => n.Id == "legend");
        Assert.All(off.Document.Nodes, n =>
        {
            Assert.Null(n.Style);
            Assert.Null(Tag(n));
        });
        Assert.NotEqual(Normalise(SqlImporter.Import(Tagged, new SqlImportOptions { Now = now })), Normalise(off));
    }

    // The document id is a fresh GUID each import; everything else must match.
    private static string Normalise(ImportResult result)
    {
        return DocumentJson.Serialize(result.Document with { Id = "doc_fixed" });
    }
}
