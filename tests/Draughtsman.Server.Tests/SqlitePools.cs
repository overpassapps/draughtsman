using Draughtsman.Storage;
using Microsoft.Data.Sqlite;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Releases the pooled SQLite connections of the one database a test (or a server it started) used, so the file is checkpointed, whole on disk and free
/// to delete. Never <c>SqliteConnection.ClearAllPools()</c>: Microsoft.Data.Sqlite's pools are process-wide and xUnit runs test classes in parallel, and
/// clearing every pool reclaims a connection another class has just taken from its pool but not yet finished activating, so that class's <c>Open()</c>
/// throws <c>ObjectDisposedException: SQLitePCL.sqlite3</c>, or a start-up migration cannot copy its database and the server refuses to start
/// (the test then fails with <c>no such column: d.deleted_at</c>). Clearing only this database's pool cannot touch anyone else's: its folder is
/// this test's own. <see cref="SqliteTestIsolationTests"/> keeps it that way.
/// </summary>
internal static class SqlitePools
{
    /// <summary>The pool of <c>data.db</c> in a data folder, under the exact connection string a server or test context there uses.</summary>
    public static void Release(string dataFolder)
    {
        Clear(StorageOptions.DefaultSqliteConnectionString(Path.GetFullPath(dataFolder)));
    }

    /// <summary>The pool of a database file that is not <c>data.db</c> (a pre-migration copy opened as a database of its own).</summary>
    public static void ReleaseDatabase(string databasePath)
    {
        Clear("Data Source=" + databasePath);
    }

    /// <summary>Every <c>data.db</c> under a test's work folder, for a test that starts servers or restores into folders of its own.</summary>
    public static void ReleaseUnder(string workFolder)
    {
        if (!Directory.Exists(workFolder))
        {
            return;
        }

        foreach (string database in Directory.EnumerateFiles(workFolder, StorageOptions.SqliteFileName, SearchOption.AllDirectories))
        {
            Release(Path.GetDirectoryName(database)!);
        }
    }

    private static void Clear(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }
}
