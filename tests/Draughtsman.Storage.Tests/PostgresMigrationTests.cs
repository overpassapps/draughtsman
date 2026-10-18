using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Draughtsman.Storage.Tests;

/// <summary>
/// The Postgres half of the upgrade rules StorageMigratorTests proves on SQLite: the etag column arrives on a database that
/// already holds documents, an older build refuses a newer schema, and an existing database gets the snapshot notice before it is
/// migrated. Each test makes its own scratch database on the test server, so nothing shared is changed. Skips like the rest of
/// the Postgres suite when no server is reachable.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresMigrationTests(PostgresTestDatabase database)
{
    private const string PreviousMigration = "20260930214350_AddSecurityEventsAndTokenExpiry";

    [SkippableFact]
    public async Task A_document_saved_before_the_etag_column_survives_the_upgrade_and_gets_its_tag()
    {
        await using Scratch scratch = await Scratch.Create(database);
        var document = new Document("doc_before_etag", Document.CurrentSchemaVersion, "Saved before", DocumentType.Flowchart, [], [], new Meta(new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero), "test"));
        await using (DraughtsmanDbContext before = StorageSetup.CreateContext(scratch.Options))
        {
            await before.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await before.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, title, title_search, type, workspace, snapshot, schema_version, created_at, updated_at) VALUES ('doc_before_etag', 'Saved before', 'saved before', 'flowchart', 'default', {0}, {1}, '2026-09-20 22:00:00+00', '2026-09-20 22:00:00+00')",
                DocumentJson.Serialize(document),
                document.SchemaVersion);
        }

        var log = new ListLogger();
        await using DraughtsmanDbContext db = StorageSetup.CreateContext(scratch.Options);
        await StorageMigrator.Migrate(db, scratch.Options, log);
        var store = new EfStore(db, scratch.Options);

        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == "doc_before_etag").Select(d => d.Etag).SingleAsync());
        Assert.Equal(DocumentVersionTag.Compute(document), await store.GetDocumentTag("doc_before_etag"));
        Assert.NotNull(await db.Documents.AsNoTracking().Where(d => d.Id == "doc_before_etag").Select(d => d.Etag).SingleAsync());
        Assert.Contains(log.Warnings, w => w.Contains("pg_dump", StringComparison.Ordinal) && w.Contains("Postgres", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task A_document_saved_before_the_bin_columns_is_live_after_the_upgrade()
    {
        await using Scratch scratch = await Scratch.Create(database);
        var document = new Document("doc_before_bin", Document.CurrentSchemaVersion, "Saved before the bin", DocumentType.Flowchart, [], [], new Meta(new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero), "test"));
        await using (DraughtsmanDbContext before = StorageSetup.CreateContext(scratch.Options))
        {
            await before.GetService<IMigrator>().MigrateAsync("20260930222759_AddDocumentEtag");
            await before.Database.ExecuteSqlRawAsync(
                "INSERT INTO documents (id, title, title_search, type, workspace, snapshot, schema_version, etag, created_at, updated_at) VALUES ('doc_before_bin', 'Saved before the bin', 'saved before the bin', 'flowchart', 'default', {0}, {1}, 'tag', '2026-09-20 22:00:00+00', '2026-09-20 22:00:00+00')",
                DocumentJson.Serialize(document),
                document.SchemaVersion);
        }

        await using DraughtsmanDbContext db = StorageSetup.CreateContext(scratch.Options);
        await StorageMigrator.Migrate(db, scratch.Options, new ListLogger());
        var store = new EfStore(db, scratch.Options);

        Assert.Equal("doc_before_bin", Assert.Single((await store.ListDocuments(new DocumentQuery())).Items).Id);
        Assert.NotNull(await store.GetDocument("doc_before_bin"));
        Assert.Null(await store.GetBinnedDocument("doc_before_bin"));

        Assert.True(await store.SoftDeleteDocument("doc_before_bin", DateTimeOffset.UtcNow, null, null));
        Assert.Empty((await store.ListDocuments(new DocumentQuery())).Items);
        Assert.Equal(RestoreOutcome.Restored, await store.RestoreDocument("doc_before_bin"));
        Assert.NotNull(await store.GetDocument("doc_before_bin"));
    }

    [SkippableFact]
    public async Task A_fresh_database_gets_no_snapshot_notice_and_a_current_one_is_not_refused()
    {
        await using Scratch scratch = await Scratch.Create(database);
        var log = new ListLogger();
        await using DraughtsmanDbContext db = StorageSetup.CreateContext(scratch.Options);

        await StorageMigrator.Migrate(db, scratch.Options, log);
        await StorageMigrator.Migrate(db, scratch.Options, log);

        Assert.Empty(log.Warnings);
    }

    [SkippableFact]
    public async Task A_database_migrated_by_a_newer_build_is_refused_with_the_way_back()
    {
        await using Scratch scratch = await Scratch.Create(database);
        await using DraughtsmanDbContext db = StorageSetup.CreateContext(scratch.Options);
        await StorageMigrator.Migrate(db, scratch.Options, new ListLogger());
        await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29990101000000_FromTheFuture', '99.0.0')");

        SchemaNewerThanBuildException refusal = await Assert.ThrowsAsync<SchemaNewerThanBuildException>(() => StorageMigrator.Migrate(db, scratch.Options, new ListLogger()));

        Assert.Equal("29990101000000_FromTheFuture", Assert.Single(refusal.UnknownMigrations));
        Assert.Contains("pg_restore", refusal.Message, StringComparison.Ordinal);
    }

    private sealed class ListLogger : ILogger
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

    /// <summary>A throwaway database on the test server, dropped afterwards.</summary>
    private sealed class Scratch : IAsyncDisposable
    {
        private readonly string _admin;
        private readonly string _name;

        private Scratch(string admin, string name, StorageOptions options)
        {
            _admin = admin;
            _name = name;
            Options = options;
        }

        public StorageOptions Options { get; }

        public static async Task<Scratch> Create(PostgresTestDatabase database)
        {
            Skip.If(database.SkipReason is not null, database.SkipReason);
            string admin = database.ConnectionString!;
            string name = "draughtsman_upgrade_" + Guid.NewGuid().ToString("N")[..12];
            await Execute(admin, $"CREATE DATABASE {name}");
            string connection = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ToString();
            return new Scratch(admin, name, new StorageOptions(StorageProvider.Postgres, connection));
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await Execute(_admin, $"DROP DATABASE IF EXISTS {_name} WITH (FORCE)");
        }

        private static async Task Execute(string connectionString, string sql)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }
}
