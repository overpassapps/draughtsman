using Draughtsman.Storage;
using Microsoft.Data.Sqlite;

namespace Draughtsman.Server.Tests;

/// <summary>What <see cref="SqlitePools"/> promises: it reaches the pool a server or context on that folder really uses, and no other pool.</summary>
public sealed class SqlitePoolsTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqlitePools.ReleaseUnder(_work);
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Release_closes_the_pooled_connections_of_the_folder_it_is_given_and_leaves_every_other_pool_alone()
    {
        string first = Path.Combine(_work, "first");
        string second = Path.Combine(_work, "second");
        WriteAndReturnToPool(first);
        WriteAndReturnToPool(second);

        // A pooled connection that has gone idle still holds its database open, so the write-ahead log is still beside the file.
        Assert.True(File.Exists(Path.Combine(first, "data.db-wal")), "the first database is held open by its pool");
        Assert.True(File.Exists(Path.Combine(second, "data.db-wal")), "the second database is held open by its pool");

        SqlitePools.Release(first);

        // Closing the last connection checkpoints the log away: the first pool is gone, the neighbour's is exactly as it was.
        Assert.False(File.Exists(Path.Combine(first, "data.db-wal")), "Release must reach the pool the folder's own connection string names");
        Assert.True(File.Exists(Path.Combine(second, "data.db-wal")), "Release must not touch a pool of another folder");

        SqlitePools.ReleaseUnder(_work);

        Assert.False(File.Exists(Path.Combine(second, "data.db-wal")), "ReleaseUnder reaches every data.db below a work folder");
    }

    [Fact]
    public void ReleaseDatabase_reaches_a_database_file_that_is_not_data_db()
    {
        Directory.CreateDirectory(_work);
        string database = Path.Combine(_work, "copy.db");
        using (var connection = new SqliteConnection("Data Source=" + database))
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL");
            Execute(connection, "CREATE TABLE t (x INTEGER)");
        }

        Assert.True(File.Exists(database + "-wal"), "the copy is held open by its pool");

        SqlitePools.ReleaseDatabase(database);

        Assert.False(File.Exists(database + "-wal"));
    }

    private static void WriteAndReturnToPool(string folder)
    {
        Directory.CreateDirectory(folder);
        using var connection = new SqliteConnection(StorageOptions.DefaultSqliteConnectionString(folder));
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, "CREATE TABLE t (x INTEGER)");
        Execute(connection, "INSERT INTO t VALUES (1)");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
