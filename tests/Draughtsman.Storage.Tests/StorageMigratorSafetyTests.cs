using System.Reflection;
using System.Security.Cryptography;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Storage;
using Draughtsman.Tests.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Storage.Tests;

/// <summary>
/// Item 4 of the upgrade-safety work (docs/upgrade-backup.md, "What an upgrade does and undoes"): after a SQLite upgrade the
/// database is checked against the copy taken before it, and put back from that copy, byte for byte, when a migration throws or
/// the result lost rows; and an upgrade whose copy cannot be written is refused unless the operator says otherwise. Each test
/// has a counterpart that must NOT trigger (an additive migration upgrades and keeps its copy; the allow flag lets a copy-less
/// upgrade go ahead), so the checks are shown to be able to fail and to be able to pass. These run on the server's own pooled connection string, because
/// the rollback they prove calls <c>SqliteConnection.ClearAllPools()</c>, so they are alone in <see cref="PooledSqliteCollection"/>.
/// </summary>
#pragma warning disable EF1001 // The test replaces EF's migrations assembly so a deliberately broken migration can be added to the real ones.
[Collection(PooledSqliteCollection.Name)]
public sealed class StorageMigratorSafetyTests : IDisposable
{
    private readonly string _folder = TestPaths.NewTempFolder();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        TestPaths.DeleteTempFolder(_folder);
    }

    private string DatabasePath => Path.Combine(_folder, StorageOptions.SqliteFileName);

    private StorageOptions Options => new(StorageProvider.Sqlite, StorageOptions.DefaultSqliteConnectionString(_folder));

    /// <summary>A database on the shipped schema holding documents, users and an asset: what an upgrade must not damage.</summary>
    private async Task<byte[]> SeedCurrentDatabase()
    {
        await using (DraughtsmanDbContext db = StorageSetup.CreateContext(Options))
        {
            await StorageMigrator.Migrate(db, Options, NullLogger.Instance);
            var store = new EfStore(db, Options);
            DateTimeOffset now = new(2026, 9, 20, 22, 0, 0, TimeSpan.Zero);
            for (int i = 1; i <= 3; i++)
            {
                await store.CreateDocument(new Document($"doc_{i}", Document.CurrentSchemaVersion, $"Document {i}", DocumentType.Flowchart, [], [], new Meta(now, now, "test")));
            }

            db.Users.Add(new Entities.UserRow { Id = "usr_1", Email = "one@example.com", DisplayName = "One", Role = "Admin", PasswordHash = "hash", CreatedAt = now.UtcDateTime, Active = true });
            await db.SaveChangesAsync();
        }

        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using SqliteCommand checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        checkpoint.ExecuteNonQuery();
        connection.Close();
        SqliteConnection.ClearAllPools();
        return await File.ReadAllBytesAsync(DatabasePath);
    }

    private static DraughtsmanDbContext ContextWith(StorageOptions options, params Type[] extraMigrations)
    {
        var builder = new DbContextOptionsBuilder<SqliteDraughtsmanDbContext>();
        builder.UseSqlite(options.ConnectionString, sqlite => sqlite.MigrationsAssembly(typeof(StorageSetup).Assembly.GetName().Name));
        AppendingMigrationsAssembly.Extra = extraMigrations;
        builder.ReplaceService<IMigrationsAssembly, AppendingMigrationsAssembly>();
        return new SqliteDraughtsmanDbContext(builder.Options);
    }

    [Fact]
    public async Task A_migration_that_throws_after_an_earlier_one_applied_puts_the_database_back_byte_for_byte()
    {
        byte[] original = await SeedCurrentDatabase();
        string[] copiesBefore = Directory.GetFiles(_folder, "*" + StorageMigrator.PreMigrationSuffix + "*");

        MigrationRolledBackException undone;
        await using (DraughtsmanDbContext db = ContextWith(Options, typeof(AddsAColumnMigration), typeof(ThrowsMigration)))
        {
            undone = await Assert.ThrowsAsync<MigrationRolledBackException>(() => StorageMigrator.Migrate(db, Options, NullLogger.Instance));
        }

        SqliteConnection.ClearAllPools();
        Assert.True(undone.Restored, undone.RestoreFailure);
        Assert.Contains("a storage migration failed", undone.Message, StringComparison.Ordinal);
        Assert.Contains(undone.CopyPath, undone.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(undone.CopyPath), "the copy is kept so the message can name it");
        Assert.Equal(original, await File.ReadAllBytesAsync(DatabasePath));
        Assert.False(File.Exists(DatabasePath + "-wal"));
        Assert.False(File.Exists(DatabasePath + "-shm"));
        // Exactly as it was, plus the one copy this start took.
        Assert.Equal(copiesBefore.Length + 1, Directory.GetFiles(_folder, "*" + StorageMigrator.PreMigrationSuffix + "*").Length);

        // The first, harmless migration of the failed run was undone too: the schema is the one before it.
        await using DraughtsmanDbContext after = StorageSetup.CreateContext(Options);
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
        Assert.DoesNotContain(AddsAColumnMigration.Id, await after.Database.GetAppliedMigrationsAsync());
    }

    /// <summary>
    /// The same rollback on data a real EARLIER BUILD wrote (tests/upgrade-fixtures): the real migrations the fixture is behind on run, then
    /// a migration that deletes every document, and the database comes back byte for byte as the old build left it.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixtureLabels))]
    public async Task A_destructive_migration_on_a_real_earlier_builds_database_is_undone(string label)
    {
        string database = ExtractFixtureDatabase(label);
        byte[] original = await File.ReadAllBytesAsync(database);
        long documents = SqliteTableCounts.ReadVerified(database).Counts["documents"];
        Assert.True(documents > 0, "the fixture holds documents for the migration to destroy");

        MigrationRolledBackException undone;
        await using (DraughtsmanDbContext db = ContextWith(Options, typeof(DeletesDocumentsMigration)))
        {
            undone = await Assert.ThrowsAsync<MigrationRolledBackException>(() => StorageMigrator.Migrate(db, Options, NullLogger.Instance));
        }

        SqliteConnection.ClearAllPools();
        Assert.True(undone.Restored, undone.RestoreFailure);
        Assert.Contains($"table documents had {documents} rows before the migration and has 0 now", undone.Reasons);
        Assert.Equal(original, await File.ReadAllBytesAsync(database));
    }

    public static TheoryData<string> FixtureLabels
    {
        get
        {
            var labels = new TheoryData<string>();
            foreach (string directory in Directory.EnumerateDirectories(UpgradeFixturePaths.Root).Order(StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(directory, "sqlite", "data-folder.tar.gz")))
                {
                    labels.Add(Path.GetFileName(directory));
                }
            }

            return labels;
        }
    }

    /// <summary>Unpacks a fixture's SQLite data folder into this test's folder; the database is <c>data.db</c> in it, which is also where <see cref="Options"/> points.</summary>
    private string ExtractFixtureDatabase(string label)
    {
        using FileStream file = File.OpenRead(Path.Combine(UpgradeFixturePaths.Root, label, "sqlite", "data-folder.tar.gz"));
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        System.Formats.Tar.TarFile.ExtractToDirectory(gzip, _folder, overwriteFiles: true);
        return DatabasePath;
    }

    [Fact]
    public async Task A_migration_that_succeeds_but_deletes_rows_is_caught_by_the_row_count_check_and_undone()
    {
        byte[] original = await SeedCurrentDatabase();

        MigrationRolledBackException undone;
        await using (DraughtsmanDbContext db = ContextWith(Options, typeof(DeletesDocumentsMigration)))
        {
            undone = await Assert.ThrowsAsync<MigrationRolledBackException>(() => StorageMigrator.Migrate(db, Options, NullLogger.Instance));
        }

        SqliteConnection.ClearAllPools();
        Assert.True(undone.Restored, undone.RestoreFailure);
        Assert.Contains("table documents had 3 rows before the migration and has 0 now", undone.Reasons);
        Assert.Equal(original, await File.ReadAllBytesAsync(DatabasePath));
        Assert.Equal(3, SqliteTableCounts.ReadVerified(DatabasePath).Counts["documents"]);
    }

    [Fact]
    public async Task A_migration_that_drops_a_table_is_caught_and_undone()
    {
        byte[] original = await SeedCurrentDatabase();

        MigrationRolledBackException undone;
        await using (DraughtsmanDbContext db = ContextWith(Options, typeof(DropsATableMigration)))
        {
            undone = await Assert.ThrowsAsync<MigrationRolledBackException>(() => StorageMigrator.Migrate(db, Options, NullLogger.Instance));
        }

        SqliteConnection.ClearAllPools();
        Assert.Contains(undone.Reasons, r => r.Contains("table audit existed before the migration", StringComparison.Ordinal) && r.EndsWith("is gone", StringComparison.Ordinal));
        Assert.Equal(original, await File.ReadAllBytesAsync(DatabasePath));
    }

    [Fact]
    public async Task An_additive_migration_passes_the_checks_keeps_its_copy_and_prunes_older_copies()
    {
        byte[] original = await SeedCurrentDatabase();
        // Four older copies: after a good upgrade only the newest `keep` remain (and the new one counts).
        for (int i = 1; i <= 4; i++)
        {
            await File.WriteAllBytesAsync(DatabasePath + StorageMigrator.PreMigrationSuffix + $"2026010100000{i}", [1, 2, 3]);
        }

        string? copy;
        await using (DraughtsmanDbContext db = ContextWith(Options, typeof(AddsAColumnMigration)))
        {
            copy = await StorageMigrator.Migrate(db, Options, NullLogger.Instance, preMigrationCopiesToKeep: 3);
            Assert.Contains(AddsAColumnMigration.Id, await db.Database.GetAppliedMigrationsAsync());
        }

        SqliteConnection.ClearAllPools();
        Assert.NotNull(copy);
        Assert.Equal(original, await File.ReadAllBytesAsync(copy));
        Assert.Equal(3, Directory.GetFiles(_folder, "*" + StorageMigrator.PreMigrationSuffix + "*").Length);
        Assert.Equal(3, SqliteTableCounts.ReadVerified(DatabasePath).Counts["documents"]);
    }

    [Fact]
    public async Task A_rolled_back_upgrade_does_not_prune_the_older_copies()
    {
        await SeedCurrentDatabase();
        for (int i = 1; i <= 4; i++)
        {
            await File.WriteAllBytesAsync(DatabasePath + StorageMigrator.PreMigrationSuffix + $"2026010100000{i}", [1, 2, 3]);
        }

        await using (DraughtsmanDbContext db = ContextWith(Options, typeof(DeletesDocumentsMigration)))
        {
            await Assert.ThrowsAsync<MigrationRolledBackException>(() => StorageMigrator.Migrate(db, Options, NullLogger.Instance, preMigrationCopiesToKeep: 3));
        }

        // 4 older + the one this start took: nothing was pruned, the folder is as it was plus one copy.
        Assert.Equal(5, Directory.GetFiles(_folder, "*" + StorageMigrator.PreMigrationSuffix + "*").Length);
    }

    /// <summary>The target of the copy is a path nothing can be copied onto (a folder): the portable way to make File.Copy fail, which a full disk or a read-only folder does on a real host.</summary>
    private void MakeEveryNearTermCopyPathUnwritable()
    {
        DateTime now = DateTime.UtcNow;
        for (int seconds = -5; seconds <= 90; seconds++)
        {
            string stamp = now.AddSeconds(seconds).ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            Directory.CreateDirectory(DatabasePath + StorageMigrator.PreMigrationSuffix + stamp);
        }
    }

    [Fact]
    public async Task An_upgrade_whose_copy_cannot_be_written_is_refused_before_anything_changes()
    {
        byte[] original = await SeedCurrentDatabase();
        MakeEveryNearTermCopyPathUnwritable();

        PreMigrationCopyException refusal;
        await using (DraughtsmanDbContext db = ContextWith(Options, typeof(AddsAColumnMigration)))
        {
            refusal = await Assert.ThrowsAsync<PreMigrationCopyException>(() => StorageMigrator.Migrate(db, Options, NullLogger.Instance));
        }

        SqliteConnection.ClearAllPools();
        Assert.Contains("storage.allowMigrationWithoutBackup", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(DatabasePath));
        await using DraughtsmanDbContext after = StorageSetup.CreateContext(Options);
        Assert.DoesNotContain(AddsAColumnMigration.Id, await after.Database.GetAppliedMigrationsAsync());
    }

    /// <summary>
    /// A data folder the account cannot write (SQLite needs to create its -wal and -shm files beside the database) used to crash the server with an
    /// unhandled exception; it is now a refusal that names what to check and changes nothing. Found by running the new release archive on a read-only folder.
    /// </summary>
    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_data_folder_the_account_cannot_write_is_a_clean_refusal_and_changes_nothing()
    {
        Skip.If(OperatingSystem.IsWindows(), "folder permissions are modelled with Unix file modes");
        Skip.If(Environment.IsPrivilegedProcess, "a privileged account can write a read-only folder");
        byte[] original = await SeedCurrentDatabase();
        // Behind by one migration, so an upgrade is due.
        await using (DraughtsmanDbContext ahead = ContextWith(Options, typeof(AddsAColumnMigration)))
        {
            Assert.Single(await ahead.Database.GetPendingMigrationsAsync());
        }

        SqliteConnection.ClearAllPools();
        File.Delete(DatabasePath + "-wal");
        File.Delete(DatabasePath + "-shm");
        byte[] before = await File.ReadAllBytesAsync(DatabasePath);
        UnixFileMode mode = File.GetUnixFileMode(_folder);
        File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await using DraughtsmanDbContext db = ContextWith(Options, typeof(AddsAColumnMigration));
            StorageOpenException refusal = await Assert.ThrowsAsync<StorageOpenException>(() => StorageMigrator.Migrate(db, Options, NullLogger.Instance));
            Assert.Contains("may read and write the whole data folder", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was changed", refusal.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(_folder, mode);
            SqliteConnection.ClearAllPools();
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(DatabasePath));
        Assert.Equal(original.Length, before.Length);
    }

    [Fact]
    public async Task With_the_documented_flag_an_upgrade_without_a_copy_goes_ahead_and_says_so_loudly()
    {
        await SeedCurrentDatabase();
        MakeEveryNearTermCopyPathUnwritable();
        var log = new RecordingLogger();

        string? copy;
        await using (DraughtsmanDbContext db = ContextWith(Options, typeof(AddsAColumnMigration)))
        {
            copy = await StorageMigrator.Migrate(db, Options, log, allowMigrationWithoutBackup: true);
            Assert.Contains(AddsAColumnMigration.Id, await db.Database.GetAppliedMigrationsAsync());
        }

        Assert.Null(copy);
        Assert.Contains(log.Warnings, w => w.StartsWith("MIGRATING WITHOUT A BACKUP", StringComparison.Ordinal) && w.Contains("draughtsman backup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_database_that_already_fails_its_integrity_check_is_not_upgraded()
    {
        await SeedCurrentDatabase();
        // Damage the last pages (the migration history, which opening the database reads first, is untouched): the live file is
        // corrupt, so a copy of it is no safety net and SQLite's integrity_check says so when the copy is verified.
        byte[] bytes = await File.ReadAllBytesAsync(DatabasePath);
        int pageSize = BitConverter.ToUInt16([bytes[17], bytes[16]]);
        pageSize = pageSize == 1 ? 65536 : pageSize;
        int pages = bytes.Length / pageSize;
        for (int page = Math.Max(5, pages - 6); page < pages; page++)
        {
            for (int offset = 0; offset < pageSize; offset += 61)
            {
                bytes[(page * pageSize) + offset] ^= 0xFF;
            }
        }

        await File.WriteAllBytesAsync(DatabasePath, bytes);
        SqliteConnection.ClearAllPools();

        // Whatever SQLite reports (integrity_check lines or "malformed"), the upgrade must not run on a damaged file.
        await using DraughtsmanDbContext db = ContextWith(Options, typeof(AddsAColumnMigration));
        PreMigrationCopyException refusal = await Assert.ThrowsAsync<PreMigrationCopyException>(() => StorageMigrator.Migrate(db, Options, NullLogger.Instance));
        Assert.Contains("Nothing was changed", refusal.Message, StringComparison.Ordinal);
        SqliteConnection.ClearAllPools();
        Assert.Equal(bytes, await File.ReadAllBytesAsync(DatabasePath));
    }

    [Fact]
    public void The_postgres_notice_is_loud_and_names_pg_dump_the_migrations_and_the_way_back()
    {
        var options = new StorageOptions(StorageProvider.Postgres, "Host=db.example.invalid;Port=5432;Database=diagrams;Username=drafter;Password=never-printed");
        string notice = StorageMigrator.PostgresUpgradeNotice(options, ["20261201000000_AddThings", "20261201000001_AddMore"]);

        Assert.StartsWith("POSTGRES UPGRADE, NO AUTOMATIC ROLLBACK", notice, StringComparison.Ordinal);
        Assert.Contains("pg_dump --format=custom", notice, StringComparison.Ordinal);
        Assert.Contains("--host db.example.invalid", notice, StringComparison.Ordinal);
        Assert.Contains("--dbname diagrams", notice, StringComparison.Ordinal);
        Assert.Contains("pg_restore --no-owner --host db.example.invalid", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_restore --clean", notice.Replace("(pg_restore --clean cannot drop", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("DROP SCHEMA public CASCADE; CREATE SCHEMA public;", notice, StringComparison.Ordinal);
        Assert.Contains("20261201000000_AddThings, 20261201000001_AddMore", notice, StringComparison.Ordinal);
        Assert.Contains("stop the server NOW", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("never-printed", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void The_row_count_check_reads_every_data_table_and_none_of_the_bookkeeping()
    {
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE things (id INTEGER PRIMARY KEY); INSERT INTO things VALUES (1),(2); CREATE TABLE \"__EFMigrationsHistory\" (id TEXT);";
            command.ExecuteNonQuery();
        }

        SqliteTableCounts counts = SqliteTableCounts.ReadVerified(DatabasePath);

        Assert.Equal(new Dictionary<string, long> { ["things"] = 2 }, counts.Counts);
    }

    [DbContext(typeof(SqliteDraughtsmanDbContext))]
    [Migration(Id)]
    private sealed class AddsAColumnMigration : Migration
    {
        public const string Id = "29990301000000_HarmlessAddedColumn";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "scratch_note", table: "documents", type: "TEXT", nullable: true);
        }
    }

    [DbContext(typeof(SqliteDraughtsmanDbContext))]
    [Migration("29990302000000_ThrowsHalfway")]
    private sealed class ThrowsMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("INSERT INTO no_such_table VALUES (1);");
        }
    }

    [DbContext(typeof(SqliteDraughtsmanDbContext))]
    [Migration("29990303000000_DeletesDocuments")]
    private sealed class DeletesDocumentsMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM document_versions; DELETE FROM document_updates; DELETE FROM documents;");
        }
    }

    [DbContext(typeof(SqliteDraughtsmanDbContext))]
    [Migration("29990304000000_DropsATable")]
    private sealed class DropsATableMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "audit");
        }
    }

    /// <summary>EF's migrations assembly plus the test migrations of the moment, so the real migrations run first and the broken one after them.</summary>
    private sealed class AppendingMigrationsAssembly(ICurrentDbContext context, IDbContextOptions options, IMigrationsIdGenerator idGenerator, IDiagnosticsLogger<DbLoggerCategory.Migrations> logger)
        : MigrationsAssembly(context, options, idGenerator, logger)
    {
        [ThreadStatic]
        public static Type[] Extra = [];

        public override IReadOnlyDictionary<string, TypeInfo> Migrations
        {
            get
            {
                var all = base.Migrations.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
                foreach (Type type in Extra)
                {
                    all[type.GetCustomAttribute<MigrationAttribute>()!.Id] = type.GetTypeInfo();
                }

                return all;
            }
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
