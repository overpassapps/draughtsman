using System.Reflection;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Maintenance;
using Draughtsman.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

#pragma warning disable EF1001 // Replaces EF's migrations assembly so a deliberately broken migration can be added to the real ones.
/// <summary>
/// What the process does when an upgrade cannot go ahead: <see cref="StorageStartup"/> turns the migrator's refusals into one clear critical line and
/// a distinct exit code, and never lets the host start (the storage tests prove what the migrator does; this proves what an operator sees).
/// </summary>
[Collection(ProcessExitCodeCollection.Name)]
public sealed class StorageStartupExitCodeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
    private readonly int _exitBefore = Environment.ExitCode;

    public StorageStartupExitCodeTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Environment.ExitCode = _exitBefore;
        SqlitePools.ReleaseUnder(_folder);
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best effort on Windows.
        }
    }

    private StorageOptions Options => new(StorageProvider.Sqlite, StorageOptions.DefaultSqliteConnectionString(_folder));

    private async Task SeedDocument()
    {
        await using DraughtsmanDbContext db = StorageSetup.CreateContext(Options);
        await StorageMigrator.Migrate(db, Options, NullLogger.Instance);
        DateTimeOffset now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await new EfStore(db, Options).CreateDocument(new Document("doc_seed", Document.CurrentSchemaVersion, "Seed", DocumentType.Flowchart, [], [], new Meta(now, now, "test")));
        SqlitePools.ReleaseUnder(_folder);
    }

    private static DraughtsmanDbContext ContextWith(StorageOptions options, params Type[] extra)
    {
        var builder = new DbContextOptionsBuilder<SqliteDraughtsmanDbContext>();
        builder.UseSqlite(options.ConnectionString, sqlite => sqlite.MigrationsAssembly(typeof(StorageSetup).Assembly.GetName().Name));
        Appending.Extra = extra;
        builder.ReplaceService<IMigrationsAssembly, Appending>();
        return new SqliteDraughtsmanDbContext(builder.Options);
    }

    [Fact]
    public async Task An_upgrade_that_loses_rows_is_undone_and_the_process_exits_with_code_4_without_serving()
    {
        await SeedDocument();
        byte[] before = await File.ReadAllBytesAsync(Path.Combine(_folder, "data.db"));

        bool ready;
        await using (DraughtsmanDbContext db = ContextWith(Options, typeof(WipesDocuments)))
        {
            ready = await StorageStartup.MigrateOrRefuseAsync(db, Options, new DraughtsmanConfig(), NullLogger.Instance);
        }

        SqlitePools.ReleaseUnder(_folder);
        Assert.False(ready);
        Assert.Equal(StorageStartup.MigrationRolledBackExitCode, Environment.ExitCode);
        Assert.Equal(4, StorageStartup.MigrationRolledBackExitCode);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(_folder, "data.db")));
    }

    [Fact]
    public async Task An_upgrade_whose_safety_copy_cannot_be_made_exits_with_code_5_and_a_good_one_exits_cleanly()
    {
        await SeedDocument();
        DateTime now = DateTime.UtcNow;
        for (int seconds = -5; seconds <= 90; seconds++)
        {
            // A folder where the copy would go: the portable way to make File.Copy fail.
            Directory.CreateDirectory(Path.Combine(_folder, "data.db" + StorageMigrator.PreMigrationSuffix + now.AddSeconds(seconds).ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)));
        }

        bool ready;
        await using (DraughtsmanDbContext db = ContextWith(Options, typeof(AddsAColumn)))
        {
            ready = await StorageStartup.MigrateOrRefuseAsync(db, Options, new DraughtsmanConfig(), NullLogger.Instance);
        }

        Assert.False(ready);
        Assert.Equal(StorageStartup.PreMigrationCopyFailedExitCode, Environment.ExitCode);
        Assert.Equal(5, StorageStartup.PreMigrationCopyFailedExitCode);

        // The documented flag turns the refusal into an upgrade without a copy.
        Environment.ExitCode = _exitBefore;
        await using DraughtsmanDbContext allowed = ContextWith(Options, typeof(AddsAColumn));
        bool went = await StorageStartup.MigrateOrRefuseAsync(allowed, Options, new DraughtsmanConfig { Storage = new StorageConfig { AllowMigrationWithoutBackup = true } }, NullLogger.Instance);
        Assert.True(went);
        Assert.Equal(_exitBefore, Environment.ExitCode);
    }

    [DbContext(typeof(SqliteDraughtsmanDbContext))]
    [Migration("29990401000000_WipesDocuments")]
    private sealed class WipesDocuments : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM document_versions; DELETE FROM document_updates; DELETE FROM documents;");
        }
    }

    [DbContext(typeof(SqliteDraughtsmanDbContext))]
    [Migration("29990402000000_AddsAColumn")]
    private sealed class AddsAColumn : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "scratch_note", table: "documents", type: "TEXT", nullable: true);
        }
    }

    private sealed class Appending(ICurrentDbContext context, IDbContextOptions options, IMigrationsIdGenerator idGenerator, IDiagnosticsLogger<DbLoggerCategory.Migrations> logger)
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
}
