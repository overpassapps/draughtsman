using System.IO.Compression;
using System.Net;
using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth.PasswordReset;
using Draughtsman.Server.Backup;
using Draughtsman.Server.Cli;
using Draughtsman.Server.Maintenance;
using Draughtsman.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Draughtsman.Server.Tests;

/// <summary>
/// What the rest of the system does with the new table: a backup deliberately leaves reset links out, the export-everything zip has
/// none, the daily pass clears ended ones and never a live one, and the host-shell <c>reset-admin</c> still works and ends any link
/// outstanding for the account it recovers.
/// </summary>
public sealed class PasswordResetStorageTests : IDisposable
{
    private static readonly DateTimeOffset s_start = DateTimeOffset.Parse("2030-01-01T09:00:00Z");

    private readonly string _work = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    public PasswordResetStorageTests()
    {
        Directory.CreateDirectory(_work);
    }

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
    public async Task A_backup_leaves_reset_links_out_of_the_snapshot_rows_and_bytes_and_the_live_database_keeps_them()
    {
        using var source = new DraughtsmanServerFactory();
        HttpClient admin = await source.CreateAuthenticatedClient();
        string id = await admin.CreateUser("backed-up@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        string hash = PasswordResetSecrets.Hash(secret!);

        // The control: a raw snapshot of the same database does hold the hash, so the search below can find it.
        string control = Path.Combine(_work, "control.db");
        BackupWriter.SnapshotSqlite(StorageOptions.DefaultSqliteConnectionString(source.DataFolder), control);
        Assert.Contains(hash, System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(control)), StringComparison.Ordinal);
        Assert.Equal(1, CountLinks(control));

        string bundle = Path.Combine(_work, "backup.zip");
        (int code, _, string error) = await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + source.DataFolder, "--out", bundle], w.Out, w.Err));
        Assert.True(code == 0, error);

        string extracted = Path.Combine(_work, "from-backup.db");
        using (ZipArchive zip = ZipFile.OpenRead(bundle))
        {
            zip.GetEntry(BackupLayout.DataPrefix + BackupLayout.DatabaseFile)!.ExtractToFile(extracted);
            Assert.Contains("Password-reset links are deliberately not in it", new StreamReader(zip.GetEntry(BackupLayout.ReadMeName)!.Open()).ReadToEnd(), StringComparison.Ordinal);
        }

        Assert.Equal(0, CountLinks(extracted));
        Assert.DoesNotContain(hash, System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(extracted)), StringComparison.Ordinal);
        // The user and everything else came across; only the short-lived credentials did not.
        Assert.Equal(1, Scalar(extracted, "SELECT COUNT(*) FROM users WHERE email = 'backed-up@example.com'"));
        // And the live instance was not touched by taking the backup.
        Assert.Equal(HttpStatusCode.NoContent, (await source.CreateClient().Complete(secret)).StatusCode);
    }

    [Fact]
    public async Task A_restored_instance_has_no_live_link_so_an_old_one_cannot_come_back_to_life()
    {
        using var source = new DraughtsmanServerFactory();
        HttpClient admin = await source.CreateAuthenticatedClient();
        string id = await admin.CreateUser("restored@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        string bundle = Path.Combine(_work, "to-restore.zip");
        Assert.Equal(0, (await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + source.DataFolder, "--out", bundle], w.Out, w.Err))).Code);
        string fresh = Path.Combine(_work, "restored");
        Assert.Equal(0, (await Run(w => BackupCommands.RestoreAsync([bundle, "--DRAUGHTSMAN_DATA=" + fresh], w.Out, w.Err))).Code);

        using var restored = new RestoredServer(fresh);
        HttpResponseMessage refused = await restored.CreateClient().Complete(secret);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        // The people did come across: the restored instance is a working one, and can issue a fresh link.
        HttpResponseMessage login = await restored.CreateClient().PostAsJsonAsyncPublic("/api/auth/login", new { email = AuthTestHelpers.DefaultEmail, password = AuthTestHelpers.DefaultPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task The_export_everything_zip_holds_documents_and_assets_only_and_no_trace_of_a_link()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("exported@example.com");
        (_, string? secret) = await admin.CreateLink(id);
        string hash = PasswordResetSecrets.Hash(secret!);

        HttpResponseMessage response = await admin.GetAsync("/api/admin/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            Assert.DoesNotContain("password", entry.FullName, StringComparison.OrdinalIgnoreCase);
            using var reader = new StreamReader(entry.Open());
            string text = reader.ReadToEnd();
            Assert.DoesNotContain(secret!, text, StringComparison.Ordinal);
            Assert.DoesNotContain(hash, text, StringComparison.Ordinal);
            Assert.DoesNotContain("exported@example.com", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_daily_pass_clears_ended_links_after_the_retention_period_and_never_a_live_one()
    {
        var clock = new TestClock(s_start);
        using var factory = new ResetServerFactory(clock: clock);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string spentId = await admin.CreateUser("spent@example.com");
        string abandonedId = await admin.CreateUser("abandoned@example.com");
        (_, string? spent) = await admin.CreateLink(spentId);
        (_, string? abandoned) = await admin.CreateLink(abandonedId);
        await factory.CreateClient().Complete(spent);
        clock.Advance(TimeSpan.FromDays(9));
        string liveId = await admin.CreateUser("still-live@example.com");
        (_, string? live) = await admin.CreateLink(liveId);
        MaintenanceService pass = factory.Services.GetServices<IHostedService>().OfType<MaintenanceService>().Single();

        MaintenanceResult first = await pass.RunOnceAsync();

        Assert.Equal(2, first.ResetLinksRemoved);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IPasswordResetStore store = scope.ServiceProvider.GetRequiredService<IPasswordResetStore>();
            Assert.Null(await store.GetByHash(PasswordResetSecrets.Hash(spent!)));
            Assert.Null(await store.GetByHash(PasswordResetSecrets.Hash(abandoned!)));
            Assert.NotNull(await store.GetByHash(PasswordResetSecrets.Hash(live!)));
        }

        Assert.Equal(0, (await pass.RunOnceAsync()).ResetLinksRemoved);
        Assert.Equal(HttpStatusCode.NoContent, (await factory.CreateClient().Complete(live)).StatusCode);
    }

    [Fact]
    public async Task The_host_shell_reset_admin_still_works_unchanged_and_ends_a_link_that_was_outstanding()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string adminId = await admin.CreateUser("host-recovered@example.com", "Admin");
        (_, string? secret) = await admin.CreateLink(adminId);
        var output = new StringWriter();
        var error = new StringWriter();

        CliOutcome outcome = await ServerCli.ExecuteAsync(["reset-admin", "host-recovered@example.com", "--DRAUGHTSMAN_DATA=" + factory.DataFolder], output, error, stdin: new StringReader("recovered-from-the-host\n"));

        Assert.True(outcome.ExitCode == 0, error.ToString());
        Assert.Contains("Password reset for host-recovered@example.com", output.ToString(), StringComparison.Ordinal);
        Assert.True(await factory.CanSignIn("host-recovered@example.com", "recovered-from-the-host"));
        // The link made before the operator chose a password must not be able to replace it.
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.CreateClient().Complete(secret)).StatusCode);
        Assert.True(await factory.CanSignIn("host-recovered@example.com", "recovered-from-the-host"));
        Assert.Single(await admin.SecurityEvents("?type=admin.recovered"));
    }

    private static int CountLinks(string databasePath)
    {
        return (int)Scalar(databasePath, "SELECT COUNT(*) FROM password_reset_tokens");
    }

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<(int Code, string Out, string Err)> Run(Func<(StringWriter Out, StringWriter Err), Task<int>> command)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = await command((output, error));
        return (code, output.ToString(), error.ToString());
    }

    /// <summary>A second server on an existing data folder (the restored one), started through the same path a real instance uses.</summary>
    private sealed class RestoredServer(string dataFolder) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(Draughtsman.Server.Configuration.DraughtsmanConfig.DataFolderEnvironmentVariable, dataFolder);
            builder.UseSetting(Draughtsman.Server.KeyRing.KeyRingSetup.ProtectionOverrideVariable, "none");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqlitePools.Release(dataFolder);
        }
    }
}

internal static class HttpClientJsonExtensions
{
    public static Task<HttpResponseMessage> PostAsJsonAsyncPublic(this HttpClient client, string url, object body)
    {
        return System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(client, url, body);
    }
}
