using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Maintenance;
using Draughtsman.Storage;
using Draughtsman.Storage.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>OP-03, AU-15 and AU-16 as a running server shows them: the refusal to start on a newer database, the daily pass, the asset quota and the cheap conditional read.</summary>
[Collection(ProcessExitCodeCollection.Name)]
public sealed class RetentionAndGuardTests
{
    [Fact]
    public async Task A_server_started_on_a_database_a_newer_build_migrated_refuses_untouched_and_exits_with_code_3()
    {
        using var factory = new DraughtsmanServerFactory();
        string database = Path.Combine(factory.DataFolder, StorageOptions.SqliteFileName);
        Directory.CreateDirectory(factory.DataFolder);
        var options = new StorageOptions(StorageProvider.Sqlite, StorageOptions.DefaultSqliteConnectionString(factory.DataFolder));
        await using (DraughtsmanDbContext db = StorageSetup.CreateContext(options))
        {
            await StorageMigrator.Migrate(db, options, NullLogger.Instance);
            await db.Database.ExecuteSqlRawAsync("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29990101000000_FromTheFuture', '99.0.0')");
        }

        SqlitePools.Release(factory.DataFolder);
        byte[] before = await File.ReadAllBytesAsync(database);
        int exitBefore = Environment.ExitCode;
        try
        {
            // Program returns before building a host when storage is refused, which the test factory reports as no host at all.
            Assert.ThrowsAny<Exception>(() => factory.CreateClient());
            Assert.Equal(StorageStartup.SchemaNewerExitCode, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = exitBefore;
        }

        SqlitePools.Release(factory.DataFolder);
        Assert.Equal(before, await File.ReadAllBytesAsync(database));
        Assert.Empty(Directory.GetFiles(factory.DataFolder, "*" + StorageMigrator.PreMigrationSuffix + "*"));
    }

    [Fact]
    public async Task The_daily_pass_trims_checkpoints_and_ended_sessions_to_the_configured_retention_and_leaves_live_ones()
    {
        using var factory = new DraughtsmanServerFactory();
        WriteConfig(factory, "retention:\n  checkpointsPerDocument: 3\n  expiredSessionDays: 7\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = (await (await admin.PostAsJsonAsync("/api/documents", new { title = "Busy", type = "flowchart", nodes = Array.Empty<object>(), edges = Array.Empty<object>() })).Json())["id"]!.GetValue<string>();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IStore store = scope.ServiceProvider.GetRequiredService<IStore>();
            for (int i = 0; i < 6; i++)
            {
                await store.CreateVersion(id, "checkpoint", new Actor(ActorKind.Agent, "claude-code", "claude-fable-5-1"));
            }

            await store.CreateVersion(id, "release 1.0", new Actor(ActorKind.User, "alice"));
            DraughtsmanDbContext db = scope.ServiceProvider.GetRequiredService<DraughtsmanDbContext>();
            string userId = await db.Users.Select(u => u.Id).FirstAsync();
            db.Sessions.Add(new SessionRow { Id = "ses_ancient", UserId = userId, TokenHash = "ancient", CreatedAt = DateTime.UtcNow.AddDays(-60), ExpiresAt = DateTime.UtcNow.AddDays(-30) });
            await db.SaveChangesAsync();
        }

        MaintenanceService pass = factory.Services.GetServices<IHostedService>().OfType<MaintenanceService>().Single();
        MaintenanceResult result = await pass.RunOnceAsync();

        Assert.Equal(3, result.CheckpointsRemoved);
        Assert.Equal(1, result.SessionsRemoved);
        JsonNode versions = await (await admin.GetAsync($"/api/documents/{id}/versions")).Json();
        Assert.Equal(4, versions["total"]!.GetValue<int>());
        // The admin's own session is live and untouched.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(new MaintenanceResult(0, 0), await pass.RunOnceAsync());
    }

    [Fact]
    public async Task Retention_of_zero_keeps_every_checkpoint()
    {
        using var factory = new DraughtsmanServerFactory();
        WriteConfig(factory, "retention:\n  checkpointsPerDocument: 0\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = (await (await admin.PostAsJsonAsync("/api/documents", new { title = "Busy", type = "flowchart", nodes = Array.Empty<object>(), edges = Array.Empty<object>() })).Json())["id"]!.GetValue<string>();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            for (int i = 0; i < 5; i++)
            {
                await scope.ServiceProvider.GetRequiredService<IStore>().CreateVersion(id, "checkpoint", new Actor(ActorKind.Agent, "claude-code", "claude-fable-5-1"));
            }
        }

        MaintenanceResult result = await factory.Services.GetServices<IHostedService>().OfType<MaintenanceService>().Single().RunOnceAsync();

        Assert.Equal(0, result.CheckpointsRemoved);
        Assert.Equal(5, (await (await admin.GetAsync($"/api/documents/{id}/versions")).Json())["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_upload_that_would_pass_the_asset_quota_is_refused_with_507_but_a_repeat_of_a_stored_image_is_not()
    {
        using var factory = new DraughtsmanServerFactory();
        WriteConfig(factory, "assets:\n  quotaBytes: 60\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.Created, (await Upload(admin, BackupTestSupport.Png(10, 10))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Upload(admin, BackupTestSupport.Png(11, 11))).StatusCode);
        HttpResponseMessage refused = await Upload(admin, BackupTestSupport.Png(12, 12));

        Assert.Equal(HttpStatusCode.InsufficientStorage, refused.StatusCode);
        Assert.Contains("quota", (await refused.Json())["detail"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        // The same bytes as one already stored cost nothing, so they still go through at the limit.
        Assert.Equal(HttpStatusCode.Created, (await Upload(admin, BackupTestSupport.Png(10, 10))).StatusCode);
    }

    /// <summary>
    /// AU-16 at the HTTP level: with the snapshot column made unreadable, a conditional read that names the current version is
    /// still answered 304. The previous handler loaded and parsed the snapshot to compute the tag it compared with, so it
    /// answered 500 here; the stored tag is what makes the 1 Hz poll cheap.
    /// </summary>
    [Fact]
    public async Task A_conditional_read_of_the_current_version_is_answered_from_the_stored_tag_without_parsing_the_snapshot()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/documents", new { title = "Polled", type = "flowchart", nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
        string id = (await created.Json())["id"]!.GetValue<string>();
        string etag = created.Headers.ETag!.ToString();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            DraughtsmanDbContext db = scope.ServiceProvider.GetRequiredService<DraughtsmanDbContext>();
            await db.Documents.Where(d => d.Id == id).ExecuteUpdateAsync(row => row.SetProperty(d => d.Snapshot, "{ not a document"));
        }

        var current = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{id}");
        current.Headers.TryAddWithoutValidation("If-None-Match", etag);
        HttpResponseMessage answered = await admin.SendAsync(current);
        var other = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{id}");
        other.Headers.TryAddWithoutValidation("If-None-Match", "\"dv1:not-the-current-version\"");
        HttpResponseMessage changed = await admin.SendAsync(other);

        Assert.Equal(HttpStatusCode.NotModified, answered.StatusCode);
        Assert.Equal(etag, answered.Headers.ETag!.ToString());
        // A version the caller does not have does need the document, which is what the broken snapshot then shows.
        Assert.Equal(HttpStatusCode.InternalServerError, changed.StatusCode);
    }

    private static void WriteConfig(DraughtsmanServerFactory factory, string yaml)
    {
        Directory.CreateDirectory(factory.DataFolder);
        File.WriteAllText(Path.Combine(factory.DataFolder, "draughtsman.yaml"), yaml);
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var form = new MultipartFormDataContent { { file, "file", "a.png" } };
        return await client.PostAsync("/api/assets", form);
    }
}
