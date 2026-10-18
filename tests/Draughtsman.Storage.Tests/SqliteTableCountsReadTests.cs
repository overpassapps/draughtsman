namespace Draughtsman.Storage.Tests;

/// <summary>What <see cref="SqliteTableCounts.Read"/> reports about a database's integrity: it travels in the result, so a caller cannot read it before it is written.</summary>
public sealed class SqliteTableCountsReadTests : IDisposable
{
    private readonly string _folder = TestPaths.NewTempFolder();

    public void Dispose()
    {
        TestPaths.DeleteTempFolder(_folder);
    }

    private string DatabasePath => Path.Combine(_folder, "data.db");

    [Fact]
    public void A_whole_database_reports_ok_whichever_way_it_is_opened()
    {
        SqliteTestDatabase.Populate(DatabasePath, rows: 20);

        SqliteTableCounts plain = SqliteTableCounts.Read(DatabasePath);
        SqliteTableCounts immutable = SqliteTableCounts.Read(DatabasePath, immutable: true);
        SqliteTableCounts byConnectionString = SqliteTableCounts.Read($"Data Source={DatabasePath}");

        foreach (SqliteTableCounts counts in new[] { plain, immutable, byConnectionString })
        {
            Assert.Equal("ok", counts.Integrity);
            Assert.True(counts.IsWhole);
            Assert.Equal(20, counts.Counts["items"]);
        }
    }

    [Fact]
    public void A_damaged_database_reports_what_integrity_check_found_and_is_not_whole()
    {
        SqliteTestDatabase.Populate(DatabasePath, rows: 3000);
        SqliteTestDatabase.DamageAPage(DatabasePath);

        SqliteTableCounts counts = SqliteTableCounts.Read(DatabasePath, immutable: true);

        Assert.False(counts.IsWhole);
        Assert.NotEqual("ok", counts.Integrity);
        Assert.NotEmpty(counts.Integrity);
    }

    [Fact]
    public void The_integrity_text_is_exactly_ok_or_it_is_not_whole()
    {
        Assert.True(new SqliteTableCounts(new Dictionary<string, long>(), "ok").IsWhole);
        Assert.False(new SqliteTableCounts(new Dictionary<string, long>(), "OK").IsWhole);
        Assert.False(new SqliteTableCounts(new Dictionary<string, long>(), "ok; ok").IsWhole);
        Assert.False(new SqliteTableCounts(new Dictionary<string, long>(), "").IsWhole);
        Assert.False(new SqliteTableCounts(new Dictionary<string, long>(), "*** in database main ***\nPage 5: btreeInitPage() returns error code 11").IsWhole);
    }
}
