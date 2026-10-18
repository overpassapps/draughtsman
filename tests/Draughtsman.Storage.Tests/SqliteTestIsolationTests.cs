using Draughtsman.Tests.Shared;

namespace Draughtsman.Storage.Tests;

/// <summary>
/// Keeps the SQLite tests from racing each other through Microsoft.Data.Sqlite's process-wide connection pool (<see cref="SqliteTestDatabase.Options"/> says
/// how). The intermittent <c>ObjectDisposedException: SQLitePCL.sqlite3</c> this prevents showed in about one full run in forty, so a copied test class that
/// brought back a pooled connection string or a <c>ClearAllPools()</c> would not be noticed until it failed a release check. Reads this project's own source,
/// as the repository's other layout tests read theirs.
/// </summary>
public sealed class SqliteTestIsolationTests
{
    private static readonly string[] s_pooledOrPoolClearing = ["SqliteConnection.ClearAllPools", "DefaultSqliteConnectionString("];

    // The helper that builds the unpooled string out of the pooled one, and this file, which names both.
    private static readonly string[] s_exempt = ["SqliteTestDatabase.cs", "SqliteTestIsolationTests.cs"];

    [Fact]
    public void Only_the_pooled_collection_clears_a_pool_or_builds_the_servers_pooled_connection_string()
    {
        string project = Path.Combine(UpgradeFixturePaths.RepositoryRoot, "tests", "Draughtsman.Storage.Tests");
        var parallelOffenders = new List<string>();
        var inPooledCollection = new List<string>();

        foreach (string file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(project, file);
            if (s_exempt.Contains(Path.GetFileName(file)) || relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            bool uses = lines.Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Any(line => s_pooledOrPoolClearing.Any(needle => line.Contains(needle, StringComparison.Ordinal)));
            if (!uses)
            {
                continue;
            }

            bool alone = lines.Any(line => line.Contains("[Collection(PooledSqliteCollection.Name)]", StringComparison.Ordinal));
            (alone ? inPooledCollection : parallelOffenders).Add(relative);
        }

        Assert.True(
            parallelOffenders.Count == 0,
            "These test classes run in parallel with the others but clear a SQLite pool or use a pooled connection string, which can dispose a connection another class is opening: "
            + string.Join(", ", parallelOffenders)
            + ". Build their options with SqliteTestDatabase.Options(folder) and call nothing to clear pools (see SqliteTestDatabase), or, if the class is exercising the migrator's own ClearAllPools, put it in PooledSqliteCollection.");
        Assert.True(inPooledCollection.Count > 0, "the scan found no class in the pooled collection, so it is not reading what it is meant to check");
    }
}
