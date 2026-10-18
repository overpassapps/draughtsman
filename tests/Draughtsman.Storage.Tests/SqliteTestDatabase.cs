using Microsoft.Data.Sqlite;

namespace Draughtsman.Storage.Tests;

internal static class SqliteTestDatabase
{
    /// <summary>
    /// The storage options every SQLite test builds its contexts from: the server's own connection string for <paramref name="folder"/>, with pooling off.
    /// Microsoft.Data.Sqlite's pool is process-wide and xUnit runs test classes in parallel. <c>SqliteConnection.ClearAllPools()</c> (a test's cleanup, or the
    /// migrator's rollback) also reclaims a connection another class has just taken from its pool but not yet finished activating, and that class's
    /// <c>Open()</c> then throws <c>ObjectDisposedException: SQLitePCL.sqlite3</c>. A connection that is in no pool has nothing to reclaim, and releases
    /// its file when it closes, so no test needs to clear a pool. <see cref="SqliteTestIsolationTests"/> keeps it that way.
    /// </summary>
    public static StorageOptions Options(string folder)
    {
        return new StorageOptions(StorageProvider.Sqlite, ConnectionString(folder));
    }

    public static string ConnectionString(string folder)
    {
        return new SqliteConnectionStringBuilder(StorageOptions.DefaultSqliteConnectionString(folder)) { Pooling = false }.ToString();
    }

    /// <summary>
    /// A SQLite database in the default rollback-journal mode (the shape a file copied from a stopped server has): a table of <paramref name="rows"/> rows with an
    /// index on it, big enough at a few thousand rows to span many 4 KiB pages, so a test can damage one page and leave the header and schema whole.
    /// </summary>
    public static void Populate(string databasePath, int rows)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText = "CREATE TABLE items (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, body TEXT NOT NULL); CREATE INDEX ix_items_name ON items(name);";
        create.ExecuteNonQuery();
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO items (name, body) VALUES ($name, $body)";
        SqliteParameter name = insert.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter body = insert.Parameters.Add("$body", SqliteType.Text);
        for (int i = 0; i < rows; i++)
        {
            name.Value = "item-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            body.Value = new string('x', 200) + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>Overwrites fifty bytes inside the ninth page, a table's b-tree: the file still opens, and <c>PRAGMA integrity_check</c> reports rows describing the damage.</summary>
    public static void DamageAPage(string databasePath)
    {
        using var stream = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite);
        stream.Seek((4096L * 8) + 3000, SeekOrigin.Begin);
        stream.Write(Enumerable.Repeat((byte)'Z', 50).ToArray());
    }
}
