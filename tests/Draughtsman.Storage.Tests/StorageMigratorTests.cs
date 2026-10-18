using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Storage.Tests;

public sealed class StorageMigratorTests : IDisposable
{
    private readonly string _folder = TestPaths.NewTempFolder();

    public void Dispose()
    {
        TestPaths.DeleteTempFolder(_folder);
    }

    [Fact]
    public async Task Fresh_sqlite_file_is_created_and_migrated_without_a_copy()
    {
        StorageOptions options = SqliteOptions();

        string? backup;
        await using (DraughtsmanDbContext db = StorageSetup.CreateContext(options))
        {
            backup = await StorageMigrator.Migrate(db, options, NullLogger.Instance);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(0, await db.Documents.CountAsync());
        }

        Assert.Null(backup);
        Assert.True(File.Exists(Path.Combine(_folder, StorageOptions.SqliteFileName)));
        Assert.Empty(Directory.GetFiles(_folder, "*" + StorageMigrator.PreMigrationSuffix + "*"));
    }

    [Fact]
    public async Task Existing_sqlite_file_is_copied_before_a_pending_migration_and_not_when_current()
    {
        StorageOptions options = SqliteOptions();
        string database = Path.Combine(_folder, StorageOptions.SqliteFileName);
        // A zero-length file is a valid, empty SQLite database with every migration still pending.
        await File.WriteAllBytesAsync(database, []);

        string? first;
        string? second;
        await using (DraughtsmanDbContext db = StorageSetup.CreateContext(options))
        {
            first = await StorageMigrator.Migrate(db, options, NullLogger.Instance);
            second = await StorageMigrator.Migrate(db, options, NullLogger.Instance);
        }

        Assert.NotNull(first);
        Assert.StartsWith(database + StorageMigrator.PreMigrationSuffix, first, StringComparison.Ordinal);
        Assert.True(File.Exists(first));
        Assert.Equal(0, new FileInfo(first).Length);
        Assert.Null(second);
        Assert.Single(Directory.GetFiles(_folder, "*" + StorageMigrator.PreMigrationSuffix + "*"));
    }

    /// <summary>
    /// The AU-04/AU-05 migration's documented rule: a token that existed before it is grandfathered. It keeps working
    /// (no expiry, full scope) rather than being given a date or a narrower right by the upgrade, and the new table
    /// exists and is empty. Built by migrating to the previous migration, inserting a token the old schema could hold,
    /// then applying the rest.
    /// </summary>
    [Fact]
    public async Task Existing_api_tokens_are_grandfathered_with_no_expiry_and_full_scope()
    {
        StorageOptions options = SqliteOptions();
        await using DraughtsmanDbContext db = StorageSetup.CreateContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260929101728_AddAuditActorUserId");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO users (id, email, display_name, role, password_hash, created_at, active) VALUES ('usr_old', 'old@example.com', 'Old', 'Editor', 'hash', '2026-09-01 00:00:00', 1)");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO api_tokens (id, user_id, name, token_hash, created_at, revoked) VALUES ('tok_old', 'usr_old', 'pre-upgrade', 'hash-old', '2026-09-01 00:00:00', 0)");
        Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());

        await StorageMigrator.Migrate(db, options, NullLogger.Instance);

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Entities.ApiTokenRow token = await db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == "tok_old");
        Assert.Null(token.ExpiresAt);
        Assert.Equal("full", token.Scope);
        Assert.False(token.Revoked);
        Assert.Equal(0, await db.SecurityEvents.CountAsync());
    }

    /// <summary>
    /// AU-16's upgrade path. A database on the schema before the etag column holds documents with no stored tag; after the
    /// migration they are still readable, the tag a read hands out is the one a save must quote, and it is stored from then on.
    /// </summary>
    [Fact]
    public async Task A_document_saved_before_the_etag_column_survives_the_upgrade_and_gets_its_tag()
    {
        StorageOptions options = SqliteOptions();
        var document = new Document("doc_before_etag", Document.CurrentSchemaVersion, "Saved before", DocumentType.Flowchart, [], [], new Meta(new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero), "test"));
        await using (DraughtsmanDbContext before = StorageSetup.CreateContext(options))
        {
            await before.GetService<IMigrator>().MigrateAsync("20260930214345_AddSecurityEventsAndTokenExpiry");
            await before.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, title, title_search, type, workspace, snapshot, schema_version, created_at, updated_at) VALUES ('doc_before_etag', 'Saved before', 'saved before', 'flowchart', 'default', {0}, {1}, '2026-09-20 22:00:00', '2026-09-20 22:00:00')",
                DocumentJson.Serialize(document),
                document.SchemaVersion);
        }

        await using DraughtsmanDbContext db = StorageSetup.CreateContext(options);
        await StorageMigrator.Migrate(db, options, NullLogger.Instance);
        var store = new EfStore(db, options);

        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == "doc_before_etag").Select(d => d.Etag).SingleAsync());
        string? tag = await store.GetDocumentTag("doc_before_etag");

        Assert.Equal(DocumentVersionTag.Compute(document), tag);
        Assert.Equal(tag, await db.Documents.AsNoTracking().Where(d => d.Id == "doc_before_etag").Select(d => d.Etag).SingleAsync());
        Assert.Equal("Saved before", (await store.GetDocument("doc_before_etag"))!.Title);
    }

    /// <summary>
    /// OP-02's upgrade path. A document saved before the bin columns existed is a live document afterwards (not deleted, not
    /// hidden), shows in the list, and can then be moved to the bin and back.
    /// </summary>
    [Fact]
    public async Task A_document_saved_before_the_bin_columns_is_live_after_the_upgrade()
    {
        StorageOptions options = SqliteOptions();
        var document = new Document("doc_before_bin", Document.CurrentSchemaVersion, "Saved before the bin", DocumentType.Flowchart, [], [], new Meta(new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero), "test"));
        await using (DraughtsmanDbContext before = StorageSetup.CreateContext(options))
        {
            await before.GetService<IMigrator>().MigrateAsync("20260930222757_AddDocumentEtag");
            await before.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, title, title_search, type, workspace, snapshot, schema_version, etag, created_at, updated_at) VALUES ('doc_before_bin', 'Saved before the bin', 'saved before the bin', 'flowchart', 'default', {0}, {1}, 'tag', '2026-09-20 22:00:00', '2026-09-20 22:00:00')",
                DocumentJson.Serialize(document),
                document.SchemaVersion);
        }

        await using DraughtsmanDbContext db = StorageSetup.CreateContext(options);
        await StorageMigrator.Migrate(db, options, NullLogger.Instance);
        var store = new EfStore(db, options);

        Assert.Equal("doc_before_bin", Assert.Single((await store.ListDocuments(new DocumentQuery())).Items).Id);
        Assert.NotNull(await store.GetDocument("doc_before_bin"));
        Assert.Null(await store.GetBinnedDocument("doc_before_bin"));
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == "doc_before_bin").Select(d => d.DeletedAt).SingleAsync());

        Assert.True(await store.SoftDeleteDocument("doc_before_bin", DateTimeOffset.UtcNow, null, null));
        Assert.Empty((await store.ListDocuments(new DocumentQuery())).Items);
        Assert.Equal(RestoreOutcome.Restored, await store.RestoreDocument("doc_before_bin"));
        Assert.NotNull(await store.GetDocument("doc_before_bin"));
    }

    /// <summary>OP-03: an older build must not open a database a newer one has migrated, and must say what to do instead.</summary>
    [Fact]
    public async Task A_database_migrated_by_a_newer_build_is_refused_untouched_with_the_way_back()
    {
        StorageOptions options = SqliteOptions();
        string database = Path.Combine(_folder, StorageOptions.SqliteFileName);
        await using (DraughtsmanDbContext db = StorageSetup.CreateContext(options))
        {
            await StorageMigrator.Migrate(db, options, NullLogger.Instance);
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29990101000000_FromTheFuture', '99.0.0')");
        }

        // A copy a previous upgrade left, which the message must point at.
        string earlierCopy = database + StorageMigrator.PreMigrationSuffix + "20260101000000";
        await File.WriteAllBytesAsync(earlierCopy, [1, 2, 3]);
        byte[] before = await File.ReadAllBytesAsync(database);

        SchemaNewerThanBuildException refusal;
        await using (DraughtsmanDbContext db = StorageSetup.CreateContext(options))
        {
            refusal = await Assert.ThrowsAsync<SchemaNewerThanBuildException>(() => StorageMigrator.Migrate(db, options, NullLogger.Instance));
        }

        Assert.Equal("29990101000000_FromTheFuture", Assert.Single(refusal.UnknownMigrations));
        Assert.Contains("29990101000000_FromTheFuture", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("newer version of Draughtsman", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(earlierCopy, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("data.db-wal", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(database));
        Assert.Single(Directory.GetFiles(_folder, "*" + StorageMigrator.PreMigrationSuffix + "*"));
    }

    [Fact]
    public async Task A_current_database_is_not_refused()
    {
        StorageOptions options = SqliteOptions();
        await using DraughtsmanDbContext db = StorageSetup.CreateContext(options);
        await StorageMigrator.Migrate(db, options, NullLogger.Instance);

        Assert.Null(await StorageMigrator.Migrate(db, options, NullLogger.Instance));
    }

    [Fact]
    public async Task Pre_migration_copies_are_pruned_to_the_newest_few_when_a_new_one_is_taken()
    {
        StorageOptions options = SqliteOptions();
        string database = Path.Combine(_folder, StorageOptions.SqliteFileName);
        await using (DraughtsmanDbContext older = StorageSetup.CreateContext(options))
        {
            await older.GetService<IMigrator>().MigrateAsync("20260930214345_AddSecurityEventsAndTokenExpiry");
        }

        string[] stamps = ["20260101000000", "20260102000000", "20260103000000", "20260104000000"];
        foreach (string stamp in stamps)
        {
            await File.WriteAllBytesAsync(database + StorageMigrator.PreMigrationSuffix + stamp, [1]);
        }

        string? taken;
        await using (DraughtsmanDbContext db = StorageSetup.CreateContext(options))
        {
            taken = await StorageMigrator.Migrate(db, options, NullLogger.Instance, preMigrationCopiesToKeep: 2);
        }

        Assert.NotNull(taken);
        string[] left = Directory.GetFiles(_folder, "*" + StorageMigrator.PreMigrationSuffix + "*").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.True(left.SequenceEqual([database + StorageMigrator.PreMigrationSuffix + "20260104000000", taken]), string.Join(" | ", left.Select(Path.GetFileName)));
    }

    [Fact]
    public void Zero_keeps_every_pre_migration_copy()
    {
        string database = Path.Combine(_folder, StorageOptions.SqliteFileName);
        string[] copies = new[] { "20260101000000", "20260102000000", "20260103000000" }.Select(stamp => database + StorageMigrator.PreMigrationSuffix + stamp).ToArray();
        foreach (string copy in copies)
        {
            File.WriteAllBytes(copy, [1]);
        }

        Assert.Equal(0, StorageMigrator.PruneSqliteCopies(copies[2], 0, NullLogger.Instance));
        Assert.Equal(1, StorageMigrator.PruneSqliteCopies(copies[2], 2, NullLogger.Instance));
        Assert.Equal(2, StorageMigrator.ListPreMigrationCopies(database).Count);
    }

    [Fact]
    public void Copy_is_skipped_for_missing_or_in_memory_databases()
    {
        Assert.Null(StorageMigrator.CopySqliteBeforeMigration("Data Source=:memory:"));
        Assert.Null(StorageMigrator.CopySqliteBeforeMigration("Data Source=whatever;Mode=Memory"));
        Assert.Null(StorageMigrator.CopySqliteBeforeMigration(SqliteTestDatabase.ConnectionString(_folder)));
    }

    [Fact]
    public void Copies_taken_in_the_same_second_get_distinct_names()
    {
        string database = Path.Combine(_folder, StorageOptions.SqliteFileName);
        // A zero-length file is a valid, empty SQLite database.
        File.WriteAllBytes(database, []);
        string connection = SqliteTestDatabase.ConnectionString(_folder);

        string? one = StorageMigrator.CopySqliteBeforeMigration(connection);
        string? two = StorageMigrator.CopySqliteBeforeMigration(connection);

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.NotEqual(one, two);
        Assert.True(File.Exists(one));
        Assert.True(File.Exists(two));
    }

    [Fact]
    public void Copy_of_a_wal_database_carries_pages_still_in_the_log()
    {
        string connection = SqliteTestDatabase.ConnectionString(_folder);
        using var writer = new SqliteConnection(connection);
        writer.Open();
        // Held open on purpose: closing the last connection checkpoints, which is exactly the case this does not cover.
        Execute(writer, "PRAGMA journal_mode=WAL");
        Execute(writer, "CREATE TABLE committed (value TEXT NOT NULL)");
        Execute(writer, "INSERT INTO committed (value) VALUES ('kept')");

        string? backup = StorageMigrator.CopySqliteBeforeMigration(connection);

        Assert.NotNull(backup);
        using var restored = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString());
        restored.Open();
        using SqliteCommand read = restored.CreateCommand();
        read.CommandText = "SELECT value FROM committed";
        Assert.Equal("kept", read.ExecuteScalar() as string);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private StorageOptions SqliteOptions()
    {
        return SqliteTestDatabase.Options(_folder);
    }
}
