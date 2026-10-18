using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Storage;

/// <summary>
/// Applies pending migrations at startup. For SQLite it first copies the database file next to itself as
/// <c>data.db.pre-migration-&lt;stamp&gt;</c>, so a downgrade is restoring the copy (spec section 11), and keeps only the
/// newest few of those copies (OP-03: each upgrade used to leave another full database behind). Restoring means
/// deleting the sibling <c>data.db-wal</c> and <c>data.db-shm</c> first: a surviving write-ahead log from the migrated
/// database is replayed onto the restored file and silently puts the instance back on the migrated schema. Postgres
/// users get a loud notice before the migration runs, with the exact <c>pg_dump</c> command; the server cannot take that
/// snapshot for them.
/// <para>
/// Before anything else it refuses a database that a newer build has already migrated (<see cref="SchemaNewerThanBuildException"/>):
/// an older binary on a newer schema would fail at runtime or write rows the newer build misreads.
/// </para>
/// <para>
/// A SQLite upgrade is checked afterwards and undone when it went wrong (docs/upgrade-backup.md, "What an upgrade does and
/// undoes"): the copy it took is opened and verified first (so a copy that cannot be written, or reads back damaged, stops the
/// upgrade before anything changes, unless <c>storage.allowMigrationWithoutBackup</c> says otherwise), and after the migrations
/// the database must pass SQLite's integrity check and hold at least as many rows in every table as the copy did. A migration
/// that throws, or either check failing, restores the copy over the database and refuses to start
/// (<see cref="MigrationRolledBackException"/>), leaving the data file exactly as it was. Postgres cannot be rolled back by
/// this process; its notice says so, names <c>pg_dump</c> and the migrations about to run.
/// </para>
/// </summary>
public static class StorageMigrator
{
    public const string PreMigrationSuffix = ".pre-migration-";

    /// <summary>How many pre-migration SQLite copies are kept after a new one is taken (<c>retention.preMigrationCopies</c>).</summary>
    public const int DefaultPreMigrationCopiesToKeep = 3;

    /// <summary>
    /// Tables a shipped migration drops on purpose, so that the row-count check does not read their disappearance as damage.
    /// Empty: every shipped migration only adds. A contract release adds its table here and, in the same change, an entry to the
    /// destructive-migration allow-list that names the fixture proving the data survives (tests/Draughtsman.Storage.Tests,
    /// <c>MigrationSafetyTests</c> keeps the two in step).
    /// </summary>
    public static readonly IReadOnlySet<string> IntentionallyDroppedTables = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Migrates to this build's schema. Returns the path of the pre-migration copy when one was taken, otherwise null.
    /// </summary>
    /// <param name="preMigrationCopiesToKeep">Copies kept beside a SQLite database after a new one is taken; zero or less keeps every copy.</param>
    /// <param name="allowMigrationWithoutBackup">
    /// When the pre-migration copy cannot be written or does not read back whole, migrate anyway, without the ability to undo
    /// (<c>storage.allowMigrationWithoutBackup</c>). The default refuses: an upgrade that cannot be undone is the operator's
    /// decision, not the server's.
    /// </param>
    /// <exception cref="SchemaNewerThanBuildException">The database has migrations this build does not contain.</exception>
    /// <exception cref="PreMigrationCopyException">The copy could not be made or verified and the operator has not allowed that; nothing was changed.</exception>
    /// <exception cref="StorageOpenException">The database could not be opened at all (an unwritable folder, a damaged file, an unreachable Postgres server); nothing was changed.</exception>
    /// <exception cref="MigrationRolledBackException">A migration failed or the result failed its checks; the copy was restored.</exception>
    public static async Task<string?> Migrate(DraughtsmanDbContext db, StorageOptions options, ILogger logger, CancellationToken cancellationToken = default, int preMigrationCopiesToKeep = DefaultPreMigrationCopiesToKeep, bool allowMigrationWithoutBackup = false)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        HashSet<string> known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        List<string> applied;
        try
        {
            applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        }
        catch (Exception error) when (error is SqliteException or Npgsql.NpgsqlException or System.Net.Sockets.SocketException)
        {
            throw new StorageOpenException(options, error);
        }

        List<string> unknown = applied.Where(m => !known.Contains(m)).ToList();
        if (unknown.Count > 0)
        {
            throw new SchemaNewerThanBuildException(unknown, options, DescribeRecovery(options));
        }

        List<string> pending = known.Except(applied, StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Storage schema is current ({Provider})", options.ProviderName);
            return null;
        }

        string? backup = null;
        SqliteTableCounts? before = null;
        string? databasePath = options.Provider == StorageProvider.Sqlite ? SqliteFilePath(options.ConnectionString) : null;
        if (options.Provider == StorageProvider.Sqlite)
        {
            if (databasePath is not null && File.Exists(databasePath))
            {
                (backup, before) = TakeVerifiedCopy(options.ConnectionString, databasePath, allowMigrationWithoutBackup, logger);
            }
        }
        else if (applied.Count > 0)
        {
            // An existing Postgres database is about to change shape and this process cannot snapshot it. A first-ever
            // start (nothing applied yet) has nothing to lose.
            logger.LogWarning("{Notice}", PostgresUpgradeNotice(options, pending));
        }

        logger.LogInformation("Applying {Count} storage migration(s) to {Provider}: {Migrations}", pending.Count, options.ProviderName, string.Join(", ", pending));
        try
        {
            await db.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception error) when (backup is not null && databasePath is not null && error is not OperationCanceledException)
        {
            // A migration that throws has been rolled back for itself, but earlier ones of this run stay applied: put the whole database back.
            throw await RestoreCopy(db, databasePath, backup, [$"a storage migration failed: {error.GetType().Name}: {error.Message}"], logger, error);
        }

        if (backup is not null && before is not null && databasePath is not null)
        {
            IReadOnlyList<string> problems = VerifyMigratedDatabase(options.ConnectionString, before);
            if (problems.Count > 0)
            {
                throw await RestoreCopy(db, databasePath, backup, problems, logger, null);
            }

            logger.LogInformation("Post-migration check passed: SQLite integrity_check is ok and no table has fewer rows than in {Backup}", backup);
            PruneSqliteCopies(backup, preMigrationCopiesToKeep, logger);
        }

        return backup;
    }

    /// <summary>
    /// The loud notice for an existing Postgres database about to be migrated. It cannot be undone by this process, so the
    /// notice names the migrations, the exact <c>pg_dump</c> command and the way back.
    /// </summary>
    public static string PostgresUpgradeNotice(StorageOptions options, IReadOnlyCollection<string> pending)
    {
        string dump = PostgresBackupAdvice.DumpCommand(options.ConnectionString);
        string empty = PostgresBackupAdvice.EmptySchemaCommand(options.ConnectionString);
        string restore = PostgresBackupAdvice.RestoreCommand(options.ConnectionString, "<the dump file>");
        return "POSTGRES UPGRADE, NO AUTOMATIC ROLLBACK: applying "
            + pending.Count.ToString(CultureInfo.InvariantCulture)
            + " storage migration(s) to an existing Postgres database: " + string.Join(", ", pending) + ".\n"
            + "Draughtsman cannot snapshot Postgres for you, and unlike SQLite it does not copy the database first, so if this is not what you want, stop the server NOW. "
            + "Going back to the previous version means restoring a snapshot taken before this start. If you have none, take one first:\n"
            + "  " + dump + "\n"
            + "and to go back: stop this version, empty the schema (pg_restore --clean cannot drop a table a newer migration added), then restore the dump into it:\n"
            + "  " + empty + "\n"
            + "  " + restore + "\n"
            + "(the password comes from PGPASSWORD or .pgpass; see docs/upgrade-backup.md, 'Postgres')";
    }

    /// <summary>The pre-migration copies of a SQLite database file, oldest first. The stamp in the name sorts in time order.</summary>
    public static IReadOnlyList<string> ListPreMigrationCopies(string databasePath)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        string full = Path.GetFullPath(databasePath);
        string? folder = Path.GetDirectoryName(full);
        if (folder is null || !Directory.Exists(folder))
        {
            return [];
        }

        return Directory.GetFiles(folder, Path.GetFileName(full) + PreMigrationSuffix + "*")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Deletes all but the newest <paramref name="keep"/> copies of the database that <paramref name="newestCopy"/> belongs
    /// to. Best effort: a copy that cannot be removed is logged and left, never a reason to stop the migration.
    /// </summary>
    public static int PruneSqliteCopies(string newestCopy, int keep, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(newestCopy);
        ArgumentNullException.ThrowIfNull(logger);
        if (keep <= 0)
        {
            return 0;
        }

        int markerAt = newestCopy.LastIndexOf(PreMigrationSuffix, StringComparison.Ordinal);
        if (markerAt < 0)
        {
            return 0;
        }

        IReadOnlyList<string> copies = ListPreMigrationCopies(newestCopy[..markerAt]);
        int removed = 0;
        foreach (string old in copies.Take(Math.Max(0, copies.Count - keep)))
        {
            try
            {
                File.Delete(old);
                removed++;
                logger.LogInformation("Removed old pre-migration copy {Copy} (keeping the newest {Keep})", old, keep);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not remove old pre-migration copy {Copy}: {Reason}", old, error.Message);
            }
        }

        return removed;
    }

    private static string? SqliteFilePath(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode == SqliteOpenMode.Memory || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(builder.DataSource);
    }

    /// <summary>
    /// Copies the database and proves the copy: it is the same size, opens read-only, passes <c>integrity_check</c>, and its row
    /// counts are the baseline the migrated database is compared with. A copy that cannot be made or does not read back whole is
    /// removed and either refuses the upgrade or, with the operator's explicit say-so, lets it go ahead without an undo.
    /// </summary>
    private static (string? Backup, SqliteTableCounts? Before) TakeVerifiedCopy(string connectionString, string databasePath, bool allowWithoutBackup, ILogger logger)
    {
        string? copy = null;
        try
        {
            copy = CopySqliteBeforeMigration(connectionString);
            if (copy is null)
            {
                return (null, null);
            }

            long sourceLength = new FileInfo(databasePath).Length;
            long copyLength = new FileInfo(copy).Length;
            if (copyLength != sourceLength)
            {
                throw new IOException($"the copy is {copyLength} bytes but the database is {sourceLength} bytes (the disk may be full)");
            }

            SqliteTableCounts counts = SqliteTableCounts.ReadVerified(copy);
            logger.LogInformation("Copied SQLite database to {Backup} before migrating; the copy opens and passes integrity_check ({Tables} tables)", copy, counts.Counts.Count);
            return (copy, counts);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException or InvalidDataException)
        {
            TryDelete(copy);
            if (allowWithoutBackup)
            {
                logger.LogWarning(
                    "MIGRATING WITHOUT A BACKUP: the pre-migration copy of {Database} could not be made ({Reason}) and storage.allowMigrationWithoutBackup is true, so the upgrade goes ahead with no automatic way back. Take a backup by hand now if you can (draughtsman backup, or copy the stopped data folder).",
                    databasePath,
                    error.Message);
                return (null, null);
            }

            throw new PreMigrationCopyException(databasePath, error);
        }
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A partial copy that cannot be removed is reported by the failure the caller is about to raise.
        }
    }

    /// <summary>What is wrong with the migrated database, empty when it passes: SQLite's own integrity check, then every table the copy had.</summary>
    private static IReadOnlyList<string> VerifyMigratedDatabase(string connectionString, SqliteTableCounts before)
    {
        var problems = new List<string>();
        SqliteTableCounts after;
        try
        {
            after = SqliteTableCounts.Read(connectionString);
            if (!after.IsWhole)
            {
                problems.Add($"PRAGMA integrity_check reported: {after.Integrity}");
            }
        }
        catch (SqliteException error)
        {
            return [$"the migrated database could not be read back: {error.Message}"];
        }

        foreach ((string table, long rows) in before.Counts)
        {
            if (!after.Counts.TryGetValue(table, out long now))
            {
                if (!IntentionallyDroppedTables.Contains(table))
                {
                    problems.Add($"table {table} existed before the migration ({rows} rows) and is gone");
                }
            }
            else if (now < rows)
            {
                problems.Add($"table {table} had {rows} rows before the migration and has {now} now");
            }
        }

        return problems;
    }

    /// <summary>Puts the pre-migration copy back over the database and builds the exception that refuses to start. If the restore itself cannot be proven, the exception says so and names the copy to restore by hand.</summary>
    private static async Task<MigrationRolledBackException> RestoreCopy(DraughtsmanDbContext db, string databasePath, string copy, IReadOnlyList<string> reasons, ILogger logger, Exception? cause)
    {
        string? restoreFailure = null;
        try
        {
            try
            {
                await db.Database.CloseConnectionAsync();
            }
            catch (Exception error) when (error is InvalidOperationException or SqliteException)
            {
                // The connection is being abandoned; a failed close does not stop the restore.
            }

            SqliteConnection.ClearAllPools();
            // A write-ahead log left by the failed run would be replayed onto the restored file and put the migrated schema back.
            foreach (string sibling in new[] { databasePath + "-wal", databasePath + "-shm", databasePath + "-journal" })
            {
                if (File.Exists(sibling))
                {
                    File.Delete(sibling);
                }
            }

            File.Copy(copy, databasePath, overwrite: true);
            if (!FilesAreIdentical(copy, databasePath))
            {
                restoreFailure = "the restored file does not match the copy byte for byte";
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            restoreFailure = error.Message;
        }

        if (restoreFailure is null)
        {
            logger.LogCritical("Storage migration undone: {Database} was restored from {Copy} and is exactly as it was before this start. Reasons: {Reasons}", databasePath, copy, string.Join("; ", reasons));
        }
        else
        {
            logger.LogCritical("Storage migration FAILED AND COULD NOT BE UNDONE: restore {Copy} over {Database} by hand (delete -wal and -shm first). Restore problem: {Problem}. Reasons: {Reasons}", copy, databasePath, restoreFailure, string.Join("; ", reasons));
        }

        return new MigrationRolledBackException(databasePath, copy, reasons, restoreFailure, cause);
    }

    private static bool FilesAreIdentical(string a, string b)
    {
        using FileStream first = File.OpenRead(a);
        using FileStream second = File.OpenRead(b);
        return first.Length == second.Length && System.Security.Cryptography.SHA256.HashData(first).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(second));
    }

    private static string DescribeRecovery(StorageOptions options)
    {
        if (options.Provider == StorageProvider.Postgres)
        {
            return "Either start the newer Draughtsman version again, or restore the Postgres snapshot you took before upgrading (pg_restore; see docs/upgrade-backup.md) and then start this older version.";
        }

        string path = Path.GetFullPath(new SqliteConnectionStringBuilder(options.ConnectionString).DataSource);
        IReadOnlyList<string> copies = ListPreMigrationCopies(path);
        string copyAdvice = copies.Count > 0
            ? $"The pre-upgrade copy to restore is {copies[^1]}: stop the server, delete {Path.GetFileName(path)}-wal and {Path.GetFileName(path)}-shm if they exist, copy it over {Path.GetFileName(path)}, then start this older version."
            : "No pre-migration copy was found beside the database; restore a backup made before the upgrade ('draughtsman restore <bundle>', see docs/upgrade-backup.md).";
        return "Either start the newer Draughtsman version again, or downgrade by restoring what was saved before the upgrade. " + copyAdvice;
    }

    /// <summary>Copies the SQLite file, if it exists on disk, to a stamped sibling. In-memory databases have nothing to copy.</summary>
    public static string? CopySqliteBeforeMigration(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode == SqliteOpenMode.Memory || string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string source = Path.GetFullPath(builder.DataSource);
        if (!File.Exists(source))
        {
            return null;
        }

        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string target = source + PreMigrationSuffix + stamp;
        for (int n = 1; File.Exists(target); n++)
        {
            target = source + PreMigrationSuffix + stamp + "-" + n.ToString(CultureInfo.InvariantCulture);
        }

        CheckpointWriteAheadLog(connectionString);
        File.Copy(source, target);
        return target;
    }

    /// <summary>
    /// Folds the write-ahead log back into the file first. EF Core puts every SQLite database it creates into WAL mode
    /// (journal_mode is a persistent property of the file), so the newest committed pages live in the -wal sibling, and
    /// the main file alone - which is all the copy below takes - would miss them.
    /// </summary>
    private static void CheckpointWriteAheadLog(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        command.ExecuteNonQuery();
    }
}
