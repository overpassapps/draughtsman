using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Backup;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// OP-01: <c>draughtsman backup</c> and <c>restore</c>. The end-to-end test is the one the lane is judged on: back up a
/// populated instance while its server is running, restore into a fresh folder, start a server on it, and find the same
/// documents, users, assets and branding. The rest assert the refusals in both directions - what a good bundle does and
/// what a damaged, hostile, non-empty-target or too-new one is refused for - so neither half can be broken silently.
/// </summary>
public sealed class BackupRestoreTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    public BackupRestoreTests()
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
    public async Task A_live_populated_instance_is_backed_up_restored_into_a_fresh_folder_and_starts_with_the_same_data()
    {
        using var source = new DraughtsmanServerFactory();
        HttpClient admin = await source.CreateAuthenticatedClient();
        await admin.CreateUser("second@example.com");
        string first = await CreateDocument(admin, "Order service");
        string second = await CreateDocument(admin, "Checkout flow");
        byte[] png = BackupTestSupport.AssetBytes;
        string assetId = await UploadAsset(admin, png);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/branding", new { productName = "Acme Diagrams" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadBranding(admin, "logo", png)).StatusCode);

        string documentsBefore = await Get(admin, $"/api/documents/{first}") + await Get(admin, $"/api/documents/{second}");
        string usersBefore = Users(await Get(admin, "/api/users"));
        byte[] logoBefore = await admin.GetByteArrayAsync("/api/theme/logo");

        // The server is still running and holding its database open: this is the online backup.
        string bundle = Path.Combine(_work, "live.zip");
        (int backupCode, string backupOut, string backupErr) = await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + source.DataFolder, "--out", bundle], w.Out, w.Err));
        Assert.True(backupCode == 0, backupErr);
        Assert.Contains("2 documents", backupOut, StringComparison.Ordinal);
        Assert.True(File.Exists(bundle + ".sha256"));

        string fresh = Path.Combine(_work, "fresh");
        (int restoreCode, string restoreOut, string restoreErr) = await Run(w => BackupCommands.RestoreAsync([bundle, "--DRAUGHTSMAN_DATA=" + fresh], w.Out, w.Err));
        Assert.True(restoreCode == 0, restoreErr);
        Assert.Contains("Restored 2 documents", restoreOut, StringComparison.Ordinal);

        using var restored = new RestoredServerFactory(fresh);
        HttpClient client = await SignIn(restored, AuthTestHelpers.DefaultEmail, AuthTestHelpers.DefaultPassword);
        Assert.Equal(documentsBefore, await Get(client, $"/api/documents/{first}") + await Get(client, $"/api/documents/{second}"));
        Assert.Equal(png, await client.GetByteArrayAsync($"/api/assets/{assetId}"));
        Assert.Equal(usersBefore, Users(await Get(client, "/api/users")));
        // The second user's password came across in the database: a different person can sign in too.
        (await SignIn(restored, "second@example.com", AuthBarHelpers.UserPassword)).Dispose();
        JsonNode theme = JsonNode.Parse(await Get(client, "/api/theme"))!;
        Assert.Equal("Acme Diagrams", theme["productName"]!.GetValue<string>());
        Assert.Equal(logoBefore, await client.GetByteArrayAsync("/api/theme/logo"));
    }

    [Fact]
    public async Task A_document_in_the_bin_is_backed_up_still_in_the_bin_and_a_restore_brings_it_back_to_be_restored_whole()
    {
        using var source = new DraughtsmanServerFactory();
        HttpClient admin = await source.CreateAuthenticatedClient();
        string live = await CreateDocument(admin, "Live at backup time");
        string binned = await CreateDocument(admin, "Deleted last week");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"/api/documents/{binned}/versions", new { name = "before the delete" })).StatusCode);
        string binnedBefore = await Get(admin, $"/api/documents/{binned}");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/documents/{binned}")).StatusCode);
        JsonNode binEntryBefore = (await (await admin.GetAsync("/api/bin")).Json())["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == binned)!;

        string bundle = Path.Combine(_work, "with-bin.zip");
        (int backupCode, string backupOut, string backupErr) = await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + source.DataFolder, "--out", bundle], w.Out, w.Err));
        Assert.True(backupCode == 0, backupErr);

        // The bin is part of what was backed up, and the output and manifest say so.
        Assert.Contains("2 documents (1 in the bin)", backupOut, StringComparison.Ordinal);
        BackupManifest manifest = BackupManifest.Parse(BackupTestSupport.ReadEntry(bundle, "manifest.json"));
        Assert.Equal(2, manifest.Counts.Documents);
        Assert.Equal(1, manifest.Counts.BinnedDocuments);

        string fresh = Path.Combine(_work, "fresh-with-bin");
        (int restoreCode, string restoreOut, string restoreErr) = await Run(w => BackupCommands.RestoreAsync([bundle, "--DRAUGHTSMAN_DATA=" + fresh], w.Out, w.Err));
        Assert.True(restoreCode == 0, restoreErr);
        Assert.Contains("Restored 2 documents (1 in the bin)", restoreOut, StringComparison.Ordinal);

        using var restored = new RestoredServerFactory(fresh);
        HttpClient client = await SignIn(restored, AuthTestHelpers.DefaultEmail, AuthTestHelpers.DefaultPassword);
        // Still deleted after the round trip: not resurrected into the list, not lost.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/documents/{live}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/documents/{binned}")).StatusCode);
        JsonNode entry = (await (await client.GetAsync("/api/bin")).Json())["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == binned)!;
        Assert.Equal(binEntryBefore["deletedAt"]!.GetValue<DateTimeOffset>(), entry["deletedAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal("local", entry["deletedBy"]!.GetValue<string>());

        // And it can be restored there, whole, with the snapshot someone named.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/bin/{binned}/restore", null)).StatusCode);
        Assert.Equal(binnedBefore, await Get(client, $"/api/documents/{binned}"));
        Assert.Equal("before the delete", (await (await client.GetAsync($"/api/documents/{binned}/versions")).Json())["items"]![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_bundle_whose_recorded_bin_count_disagrees_with_its_database_is_refused_and_restores_nothing()
    {
        string data = Path.Combine(_work, "binned-instance");
        await BackupTestSupport.SeedFolder(data);
        var options = new Draughtsman.Storage.StorageOptions(Draughtsman.Storage.StorageProvider.Sqlite, Draughtsman.Storage.StorageOptions.DefaultSqliteConnectionString(data));
        await using (Draughtsman.Storage.DraughtsmanDbContext db = Draughtsman.Storage.StorageSetup.CreateContext(options))
        {
            Assert.True(await new Draughtsman.Storage.EfStore(db, options).SoftDeleteDocument("doc_seed_1", DateTimeOffset.UtcNow, null, null));
        }

        SqlitePools.Release(data);
        string bundle = Path.Combine(_work, "binned.zip");
        Assert.Equal(0, (await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + data, "--out", bundle], w.Out, w.Err))).Code);
        BackupManifest manifest = BackupManifest.Parse(BackupTestSupport.ReadEntry(bundle, "manifest.json"));
        Assert.Equal(1, manifest.Counts.BinnedDocuments);

        // As written, it restores; with the recorded count changed it does not, and leaves no folder behind.
        string good = Path.Combine(_work, "restored-good");
        Assert.Equal(0, (await Run(w => BackupCommands.RestoreAsync([bundle, "--DRAUGHTSMAN_DATA=" + good], w.Out, w.Err))).Code);
        BackupTestSupport.Edit(bundle, zip => BackupTestSupport.ReplaceEntry(zip, "manifest.json", (manifest with { Counts = manifest.Counts with { BinnedDocuments = 0 } }).ToJson()));
        // The edit changes the zip, so its companion checksum would refuse it first; this test is about the count inside.
        File.Delete(bundle + ".sha256");
        string bad = Path.Combine(_work, "restored-bad");
        (int code, _, string err) = await Run(w => BackupCommands.RestoreAsync([bundle, "--DRAUGHTSMAN_DATA=" + bad], w.Out, w.Err));

        Assert.Equal(1, code);
        Assert.Contains("in the bin", err, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(bad, "data.db")));
    }

    [Fact]
    public async Task A_bundle_made_before_the_bin_existed_still_restores_and_its_documents_are_live_after_the_server_migrates_it()
    {
        string data = Path.Combine(_work, "pre-bin-instance");
        await BackupTestSupport.SeedFolder(data);
        // Put the database back to the schema before the bin columns: a backup a previous release could have made.
        var options = new Draughtsman.Storage.StorageOptions(Draughtsman.Storage.StorageProvider.Sqlite, Draughtsman.Storage.StorageOptions.DefaultSqliteConnectionString(data));
        await using (Draughtsman.Storage.DraughtsmanDbContext db = Draughtsman.Storage.StorageSetup.CreateContext(options))
        {
            await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db).MigrateAsync("20260930222757_AddDocumentEtag");
        }

        SqlitePools.Release(data);
        string bundle = Path.Combine(_work, "pre-bin.zip");
        Assert.Equal(0, (await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + data, "--out", bundle], w.Out, w.Err))).Code);
        BackupManifest manifest = BackupManifest.Parse(BackupTestSupport.ReadEntry(bundle, "manifest.json"));
        Assert.Null(manifest.Counts.BinnedDocuments);
        Assert.Equal("20260930222757_AddDocumentEtag", manifest.SchemaMigration);

        string fresh = Path.Combine(_work, "pre-bin-restored");
        (int code, _, string err) = await Run(w => BackupCommands.RestoreAsync([bundle, "--DRAUGHTSMAN_DATA=" + fresh], w.Out, w.Err));
        Assert.True(code == 0, err);

        // A restored server migrates forward on start; what was live is live, and nothing is in a bin it never had.
        using var restored = new RestoredServerFactory(fresh);
        using IServiceScope scope = restored.Services.CreateScope();
        Draughtsman.Core.Storage.IStore store = scope.ServiceProvider.GetRequiredService<Draughtsman.Core.Storage.IStore>();
        Assert.Equal(2, (await store.ListDocuments(new Draughtsman.Core.Storage.DocumentQuery())).Total);
        Assert.Equal(0, (await store.ListBin(new Draughtsman.Core.Storage.BinQuery())).Total);
    }

    [Fact]
    public async Task A_bundle_carries_the_instance_but_never_secrets_or_scratch_files_unless_asked()
    {
        string data = Path.Combine(_work, "instance");
        await BackupTestSupport.SeedFolder(data);

        string plain = Path.Combine(_work, "plain.zip");
        Assert.Equal(0, (await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + data, "--out", plain], w.Out, w.Err))).Code);
        List<string> entries = BackupTestSupport.Entries(plain);

        Assert.Equal(
            [
                "README.txt", "data/assets/ab/abcdef.png", "data/branding/branding.json", "data/data.db", "data/draughtsman.yaml",
                "data/themes/org/acme.yaml", "manifest.json",
            ],
            entries);
        BackupManifest manifest = BackupManifest.Parse(BackupTestSupport.ReadEntry(plain, "manifest.json"));
        Assert.False(manifest.IncludesSecrets);
        Assert.Null(manifest.Notice);
        Assert.Equal(2, manifest.Counts.Documents);
        Assert.Equal(1, manifest.Counts.Users);
        Assert.DoesNotContain("SENSITIVE", BackupTestSupport.ReadEntry(plain, "README.txt"), StringComparison.Ordinal);
        // A bundle is the same text on every host: indented JSON and AppendLine both write the platform newline unless told not to.
        Assert.DoesNotContain('\r', BackupTestSupport.ReadEntry(plain, "manifest.json"));
        Assert.Equal("\n", BackupManifest.JsonOptions.NewLine);
        Assert.DoesNotContain('\r', BackupTestSupport.ReadEntry(plain, "README.txt"));

        // Into a folder, the default name carries the marker when secrets are in.
        string folder = Path.Combine(_work, "out") + Path.DirectorySeparatorChar;
        (int code, string output, _) = await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + data, "--include-secrets", "--out", folder], w.Out, w.Err));
        Assert.Equal(0, code);
        string sensitive = Assert.Single(Directory.GetFiles(Path.Combine(_work, "out"), "*.zip"));
        Assert.EndsWith("-SENSITIVE.zip", sensitive, StringComparison.Ordinal);
        Assert.Contains("SENSITIVE", output, StringComparison.Ordinal);
        List<string> withSecrets = BackupTestSupport.Entries(sensitive);
        Assert.Contains("data/keys/key-1.xml", withSecrets);
        Assert.Contains("data/ai/settings.json", withSecrets);
        Assert.Contains("data/backups/key-ring-1/keys/key-1.xml", withSecrets);
        BackupManifest marked = BackupManifest.Parse(BackupTestSupport.ReadEntry(sensitive, "manifest.json"));
        Assert.True(marked.IncludesSecrets);
        Assert.StartsWith("SENSITIVE", marked.Notice, StringComparison.Ordinal);
        Assert.StartsWith("SENSITIVE", BackupTestSupport.ReadEntry(sensitive, "README.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_restore_into_a_non_empty_folder_is_refused_and_changes_nothing_until_overwrite_is_given()
    {
        string data = Path.Combine(_work, "instance");
        await BackupTestSupport.SeedFolder(data, documents: 3);
        string bundle = Path.Combine(_work, "b.zip");
        await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + data, "--out", bundle], w.Out, w.Err));

        string target = Path.Combine(_work, "target");
        await BackupTestSupport.SeedFolder(target, documents: 1);
        BackupTestSupport.Write(target, "keys/their-key.xml", "<key>KEEP ME</key>");
        string before = await Sha(Path.Combine(target, "data.db"));

        (int refused, _, string error) = await Run(w => BackupCommands.RestoreAsync([bundle, "--DRAUGHTSMAN_DATA=" + target], w.Out, w.Err));

        Assert.Equal(1, refused);
        Assert.Contains("not empty", error, StringComparison.Ordinal);
        Assert.Contains("--overwrite", error, StringComparison.Ordinal);
        Assert.Equal(before, await Sha(Path.Combine(target, "data.db")));
        Assert.Empty(Directory.GetDirectories(target, "pre-restore-*"));
        Assert.Empty(Directory.GetFileSystemEntries(target, ".restore-staging-*"));

        (int done, string output, string doneError) = await Run(w => BackupCommands.RestoreAsync([bundle, "--overwrite", "--DRAUGHTSMAN_DATA=" + target], w.Out, w.Err));

        Assert.True(done == 0, doneError);
        Assert.Contains("Restored 3 documents", output, StringComparison.Ordinal);
        Assert.Equal(3, CountDocuments(Path.Combine(target, "data.db")));
        // What was there is set aside, not deleted, and the files the bundle did not cover stay where they were.
        string setAside = Assert.Single(Directory.GetDirectories(target, "pre-restore-*"));
        Assert.Equal(before, await Sha(Path.Combine(setAside, "data.db")));
        Assert.True(File.Exists(Path.Combine(target, "keys", "their-key.xml")));
        Assert.Empty(Directory.GetFileSystemEntries(target, ".restore-staging-*"));
    }

    [Fact]
    public async Task A_stale_write_ahead_log_beside_the_old_database_is_set_aside_with_it_not_replayed_onto_the_restored_one()
    {
        string data = Path.Combine(_work, "instance");
        await BackupTestSupport.SeedFolder(data, documents: 3);
        string bundle = Path.Combine(_work, "b.zip");
        await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + data, "--out", bundle], w.Out, w.Err));
        string target = Path.Combine(_work, "target");
        await BackupTestSupport.SeedFolder(target, documents: 1);
        BackupTestSupport.Write(target, "data.db-wal", "stale log");
        BackupTestSupport.Write(target, "data.db-shm", "stale index");

        (int code, _, string error) = await Run(w => BackupCommands.RestoreAsync([bundle, "--overwrite", "--DRAUGHTSMAN_DATA=" + target], w.Out, w.Err));

        Assert.True(code == 0, error);
        Assert.False(File.Exists(Path.Combine(target, "data.db-wal")));
        Assert.False(File.Exists(Path.Combine(target, "data.db-shm")));
        Assert.True(File.Exists(Path.Combine(Assert.Single(Directory.GetDirectories(target, "pre-restore-*")), "data.db-wal")));
    }

    public static TheoryData<string> DamagedBundles => new()
    {
        "a file's bytes changed",
        "a file added that the manifest does not list",
        "a manifest path that escapes the data folder",
        "a file removed",
        "the bundle's own checksum is wrong",
        "truncated",
    };

    [Theory]
    [MemberData(nameof(DamagedBundles))]
    public async Task A_damaged_or_hostile_bundle_is_refused_before_anything_is_written(string damage)
    {
        string data = Path.Combine(_work, "instance");
        await BackupTestSupport.SeedFolder(data);
        string bundle = Path.Combine(_work, "b.zip");
        await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + data, "--out", bundle], w.Out, w.Err));
        Assert.Equal(0, (await Run(w => BackupCommands.RestoreAsync([bundle, "--DRAUGHTSMAN_DATA=" + Path.Combine(_work, "clean-check")], w.Out, w.Err))).Code);

        switch (damage)
        {
            case "a file's bytes changed":
                BackupTestSupport.Edit(bundle, zip => BackupTestSupport.ReplaceEntry(zip, "data/branding/branding.json", "{\"productName\":\"Evil Co\"}"));
                File.Delete(bundle + ".sha256");
                break;
            case "a file added that the manifest does not list":
                BackupTestSupport.Edit(bundle, zip => BackupTestSupport.ReplaceEntry(zip, "data/themes/org/injected.yaml", "name: injected"));
                File.Delete(bundle + ".sha256");
                break;
            case "a manifest path that escapes the data folder":
                BackupManifest manifest = BackupManifest.Parse(BackupTestSupport.ReadEntry(bundle, "manifest.json"));
                BackupFileEntry first = manifest.Files.First(f => f.Path.StartsWith("data/branding", StringComparison.Ordinal));
                BackupTestSupport.Edit(bundle, zip => BackupTestSupport.ReplaceEntry(zip, "manifest.json", (manifest with { Files = [.. manifest.Files.Where(f => f != first), first with { Path = "data/../../escaped.txt" }] }).ToJson()));
                File.Delete(bundle + ".sha256");
                break;
            case "a file removed":
                BackupTestSupport.Edit(bundle, zip => zip.GetEntry("data/assets/ab/abcdef.png")!.Delete());
                File.Delete(bundle + ".sha256");
                break;
            case "the bundle's own checksum is wrong":
                await File.WriteAllTextAsync(bundle + ".sha256", new string('0', 64) + "  b.zip\n");
                break;
            case "truncated":
                byte[] bytes = await File.ReadAllBytesAsync(bundle);
                await File.WriteAllBytesAsync(bundle, bytes.AsSpan(0, bytes.Length / 2).ToArray());
                File.Delete(bundle + ".sha256");
                break;
            default:
                throw new InvalidOperationException(damage);
        }

        string target = Path.Combine(_work, "target");
        (int code, _, string error) = await Run(w => BackupCommands.RestoreAsync([bundle, "--DRAUGHTSMAN_DATA=" + target], w.Out, w.Err));

        Assert.Equal(1, code);
        Assert.StartsWith("Restore refused:", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_work, "escaped.txt")));
        Assert.True(!Directory.Exists(target) || Directory.GetFileSystemEntries(target).Length == 0, "a refused bundle must leave the target empty");
    }

    [Fact]
    public async Task A_backup_made_by_a_newer_build_is_refused_with_the_reason()
    {
        string data = Path.Combine(_work, "instance");
        await BackupTestSupport.SeedFolder(data);
        string bundle = Path.Combine(_work, "b.zip");
        await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + data, "--out", bundle], w.Out, w.Err));
        BackupManifest manifest = BackupManifest.Parse(BackupTestSupport.ReadEntry(bundle, "manifest.json"));
        BackupTestSupport.Edit(bundle, zip => BackupTestSupport.ReplaceEntry(zip, "manifest.json", (manifest with { SchemaMigration = "29990101000000_FromTheFuture", DraughtsmanVersion = "99.0.0" }).ToJson()));
        File.Delete(bundle + ".sha256");

        string target = Path.Combine(_work, "target");
        (int code, _, string error) = await Run(w => BackupCommands.RestoreAsync([bundle, "--DRAUGHTSMAN_DATA=" + target], w.Out, w.Err));

        Assert.Equal(1, code);
        Assert.Contains("29990101000000_FromTheFuture", error, StringComparison.Ordinal);
        Assert.Contains("newer version", error, StringComparison.Ordinal);
        Assert.True(!Directory.Exists(target) || Directory.GetFileSystemEntries(target).Length == 0);
    }

    [Fact]
    public async Task A_backup_never_overwrites_a_file_and_leaves_no_scratch_behind()
    {
        string data = Path.Combine(_work, "instance");
        await BackupTestSupport.SeedFolder(data);
        string bundle = Path.Combine(_work, "b.zip");
        Assert.Equal(0, (await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + data, "--out", bundle], w.Out, w.Err))).Code);
        string before = await Sha(bundle);

        (int code, _, string error) = await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + data, "--out", bundle], w.Out, w.Err));

        Assert.Equal(1, code);
        Assert.Contains("already exists", error, StringComparison.Ordinal);
        Assert.Equal(before, await Sha(bundle));
        Assert.Empty(Directory.GetFileSystemEntries(_work, ".draughtsman-backup-*"));
        Assert.Empty(Directory.GetFiles(_work, "*.part"));
    }

    [Fact]
    public async Task A_postgres_instance_gets_the_exact_dump_command_and_no_backup_and_its_password_is_never_printed()
    {
        string data = Path.Combine(_work, "pg");
        Directory.CreateDirectory(data);
        await File.WriteAllTextAsync(Path.Combine(data, "draughtsman.yaml"), "storage:\n  provider: postgres\n  connectionString: \"Host=db.internal;Port=6543;Database=diagrams;Username=drafter;Password=s3cr3t-pw\"\n");
        string bundle = Path.Combine(_work, "pg.zip");

        (int code, string output, string error) = await Run(w => BackupCommands.BackupAsync(["--DRAUGHTSMAN_DATA=" + data, "--out", bundle], w.Out, w.Err));

        Assert.Equal(1, code);
        Assert.False(File.Exists(bundle));
        Assert.Contains("pg_dump --format=custom --no-owner --host db.internal --port 6543 --username drafter --dbname diagrams --file draughtsman-", output, StringComparison.Ordinal);
        Assert.Contains("No backup was made", error, StringComparison.Ordinal);
        Assert.Contains("pg_restore", error, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t-pw", output + error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("restore")]
    public async Task The_verbs_print_usage_and_refuse_unknown_arguments(string verb)
    {
        Func<string[], StringWriter, StringWriter, Task<int>> run = verb == "backup"
            ? (args, o, e) => BackupCommands.BackupAsync(args, o, e)
            : (args, o, e) => BackupCommands.RestoreAsync(args, o, e);

        (int helpCode, string help, _) = await Run(w => run(["--help"], w.Out, w.Err));
        (int badCode, _, string bad) = await Run(w => run(["--nonsense"], w.Out, w.Err));

        Assert.Equal(0, helpCode);
        Assert.Contains("Usage: draughtsman " + verb, help, StringComparison.Ordinal);
        Assert.Equal(2, badCode);
        Assert.Contains("Unexpected argument", bad, StringComparison.Ordinal);
    }

    private static async Task<(int Code, string Out, string Err)> Run(Func<(StringWriter Out, StringWriter Err), Task<int>> command)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int code = await command((stdout, stderr));
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static async Task<string> Sha(string path)
    {
        return await BackupWriter.Sha256OfFile(path, CancellationToken.None);
    }

    private static long CountDocuments(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM documents";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> CreateDocument(HttpClient client, string title)
    {
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", new { title, type = "flowchart", nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Json())["id"]!.GetValue<string>();
    }

    private static async Task<string> UploadAsset(HttpClient client, byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var form = new MultipartFormDataContent { { file, "file", "a.png" } };
        HttpResponseMessage response = await client.PostAsync("/api/assets", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Json())["id"]!.GetValue<string>();
    }

    private static async Task<HttpResponseMessage> UploadBranding(HttpClient client, string slot, byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var form = new MultipartFormDataContent { { file, "file", "logo.png" } };
        return await client.PostAsync($"/api/admin/branding/{slot}", form);
    }

    /// <summary>The accounts as an admin sees them, without the last-sign-in time, which the restored server's own sign-in rightly moves.</summary>
    private static string Users(string json)
    {
        JsonArray users = JsonNode.Parse(json) as JsonArray ?? JsonNode.Parse(json)!["items"]!.AsArray();
        return string.Join("\n", users.Select(u => $"{u!["id"]}|{u["email"]}|{u["displayName"]}|{u["role"]}|{u["active"]}|{u["createdAt"]}").Order(StringComparer.Ordinal));
    }

    private static async Task<string> Get(HttpClient client, string url)
    {
        HttpResponseMessage response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<HttpClient> SignIn(WebApplicationFactory<Program> factory, string email, string password)
    {
        HttpClient client = factory.CreateClient();
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        client.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, login.Cookie("XSRF-TOKEN")!);
        return client;
    }

    /// <summary>A second server on an existing data folder (the restored one), started through the same path a real instance uses.</summary>
    private sealed class RestoredServerFactory(string dataFolder) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(DraughtsmanConfig.DataFolderEnvironmentVariable, dataFolder);
            builder.UseSetting(KeyRingSetup.ProtectionOverrideVariable, "none");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqlitePools.Release(dataFolder);
        }
    }
}
