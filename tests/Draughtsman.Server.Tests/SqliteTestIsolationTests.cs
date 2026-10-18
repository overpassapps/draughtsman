using Draughtsman.Tests.Shared;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Keeps the server tests from racing each other through Microsoft.Data.Sqlite's process-wide connection pool (<see cref="SqlitePools"/> says how). A
/// <c>SqliteConnection.ClearAllPools()</c> in one class can dispose a connection another class is opening, and a server's start-up migration then fails
/// or is refused, which a test sees as <c>ObjectDisposedException</c> or <c>no such column: d.deleted_at</c> about one full run in several on a loaded
/// machine. A new test would bring that back unseen, so this reads the project's own source, as the repository's other layout tests read theirs.
/// </summary>
public sealed class SqliteTestIsolationTests
{
    // The helper that owns the one allowed pool operation, and this file, which names the forbidden one.
    private static readonly string[] s_exempt = ["SqlitePools.cs", "SqliteTestIsolationTests.cs"];

    [Fact]
    public void No_test_clears_every_sqlite_pool_in_the_process()
    {
        string project = Path.Combine(UpgradeFixturePaths.RepositoryRoot, "tests", "Draughtsman.Server.Tests");
        var offenders = new List<string>();
        string helper = string.Empty;

        foreach (string file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(project, file);
            if (relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) || relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            if (Path.GetFileName(file) == "SqlitePools.cs")
            {
                helper = string.Join('\n', lines);
            }

            if (s_exempt.Contains(Path.GetFileName(file)))
            {
                continue;
            }

            if (lines.Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)).Any(line => line.Contains("SqliteConnection.ClearAllPools", StringComparison.Ordinal)))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These test classes clear every SQLite pool in the process, which can dispose a connection another class is opening: "
            + string.Join(", ", offenders)
            + ". Release only the database the test used: SqlitePools.Release(dataFolder), ReleaseDatabase(path) or ReleaseUnder(workFolder).");
        Assert.Contains("SqliteConnection.ClearPool", helper, StringComparison.Ordinal);
    }
}
