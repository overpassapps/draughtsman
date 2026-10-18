using System.Text.Json;
using Draughtsman.Storage;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>Reads the stored snapshot of every document from a fixture's database BEFORE an upgrade, so a changed document can be reported member by member.</summary>
internal static class UpgradeData
{
    public static Func<string, JsonElement?> SqliteSnapshots(string databaseFile)
    {
        var snapshots = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var builder = new SqliteConnectionStringBuilder { DataSource = new Uri(databaseFile).AbsoluteUri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, snapshot FROM documents";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            snapshots[reader.GetString(0)] = JsonDocument.Parse(reader.GetString(1)).RootElement.Clone();
        }

        return id => snapshots.TryGetValue(id, out JsonElement value) ? value : null;
    }

    public static async Task<Func<string, JsonElement?>> PostgresSnapshots(string connectionString)
    {
        var snapshots = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, snapshot FROM documents";
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            snapshots[reader.GetString(0)] = JsonDocument.Parse(reader.GetString(1)).RootElement.Clone();
        }

        return id => snapshots.TryGetValue(id, out JsonElement value) ? value : null;
    }

    /// <summary>Row counts of every table of the migrated SQLite database, read through its own connection.</summary>
    public static SqliteTableCounts SqliteCounts(string databaseFile)
    {
        return SqliteTableCounts.Read($"Data Source={databaseFile}");
    }

    public static string NewTempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-upgrade-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static void Delete(string folder)
    {
        SqlitePools.ReleaseUnder(folder);
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort on Windows, where a handle can outlive the host briefly.
        }
    }
}
