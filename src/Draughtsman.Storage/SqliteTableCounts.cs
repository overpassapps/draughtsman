using Microsoft.Data.Sqlite;

namespace Draughtsman.Storage;

/// <summary>
/// Row counts per table of a SQLite database, read on a connection of their own with pooling off. The post-migration check
/// compares the migrated database with the pre-migration copy through this; <c>draughtsman config check</c> and the upgrade
/// tests use it to compare an upgraded database with the fixture it came from.
/// </summary>
/// <param name="Counts">Rows per data table.</param>
/// <param name="Integrity">What <c>PRAGMA integrity_check</c> said, rows joined by "; ": "ok" when the database is whole.</param>
public sealed record SqliteTableCounts(IReadOnlyDictionary<string, long> Counts, string Integrity)
{
    /// <summary>
    /// True when <c>PRAGMA integrity_check</c> said "ok". The report travels in the result, not in an <c>out</c> argument: a .NET 10 JIT on linux-x64 evaluated
    /// <c>string.Equals(integrity, "ok")</c> BEFORE the call that filled an <c>out</c> local when that call's result was still waiting on the stack (as in
    /// <c>var counts = Read(file, out var integrity); if (!Equals(integrity, "ok")) throw; return counts;</c>), so every verified copy "failed" on Linux x64 with
    /// "reported: ok" (found by CI; <c>SqliteTableCountsTests</c> pins it, and it only bites on that platform).
    /// </summary>
    public bool IsWhole => string.Equals(Integrity, "ok", StringComparison.Ordinal);

    /// <summary>Tables that are bookkeeping, not data: SQLite's own and EF's migration history and lock.</summary>
    public static bool IsBookkeeping(string table)
    {
        return table.StartsWith("sqlite_", StringComparison.Ordinal) || table.StartsWith("__EFMigrations", StringComparison.Ordinal);
    }

    /// <summary>
    /// Counts every data table of a quiescent SQLite file (a copy, a fixture), opened read-only and immutable, so that reading it
    /// leaves no <c>-wal</c> or <c>-shm</c> file beside it; throws <see cref="InvalidDataException"/> when the file fails <c>PRAGMA integrity_check</c>.
    /// </summary>
    public static SqliteTableCounts ReadVerified(string databaseFile)
    {
        SqliteTableCounts counts = Read(databaseFile, immutable: true);
        if (!counts.IsWhole)
        {
            throw new InvalidDataException($"PRAGMA integrity_check reported: {counts.Integrity}");
        }

        return counts;
    }

    /// <summary>Counts every data table of a SQLite file or connection string, opened read-only, with what <c>PRAGMA integrity_check</c> said (<see cref="IsWhole"/>).</summary>
    public static SqliteTableCounts Read(string databaseFileOrConnectionString, bool immutable = false)
    {
        SqliteConnectionStringBuilder builder = databaseFileOrConnectionString.Contains('=', StringComparison.Ordinal)
            ? new SqliteConnectionStringBuilder(databaseFileOrConnectionString)
            : new SqliteConnectionStringBuilder { DataSource = databaseFileOrConnectionString };
        if (immutable)
        {
            // immutable=1 promises the file cannot change, so SQLite takes no locks and creates no write-ahead-log files for it.
            builder.DataSource = new Uri(Path.GetFullPath(builder.DataSource)).AbsoluteUri + "?immutable=1";
        }

        builder.Mode = SqliteOpenMode.ReadOnly;
        builder.Pooling = false;
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        string integrity;
        using (SqliteCommand check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check";
            var lines = new List<string>();
            using SqliteDataReader reader = check.ExecuteReader();
            while (reader.Read())
            {
                lines.Add(reader.GetString(0));
            }

            integrity = string.Join("; ", lines);
        }

        var tables = new List<string>();
        using (SqliteCommand list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
            using SqliteDataReader reader = list.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.GetString(0);
                if (!IsBookkeeping(name))
                {
                    tables.Add(name);
                }
            }
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            counts[table] = Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        return new SqliteTableCounts(counts, integrity);
    }
}
