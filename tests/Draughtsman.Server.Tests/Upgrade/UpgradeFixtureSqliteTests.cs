using System.Text.Json;
using Draughtsman.Storage;
using Microsoft.EntityFrameworkCore;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// The upgrade rehearsal (docs/upgrade-backup.md, "The guarantee"): for every committed fixture, a data folder an EARLIER build
/// wrote and populated through its real API, the CURRENT server is started on a copy and everything that build showed is read back.
/// Each fixture exercises a different suffix of the migration list (the first pushed commit has four of the eight migrations to
/// apply; the newest has none), so a migration that loses data, a model change that stops an old document loading, a password hash
/// that no longer verifies or a token that stops authenticating fails here, by label, naming what broke.
/// </summary>
public sealed class UpgradeFixtureSqliteTests
{
    public static TheoryData<string> Labels => UpgradeFixture.Labels(UpgradeFixture.Sqlite);

    [Fact]
    public void There_are_fixtures_to_upgrade_from_and_the_first_pushed_commit_is_one_of_them()
    {
        Assert.NotEmpty(UpgradeFixture.All);
        Assert.Contains(UpgradeFixture.All, f => f.Commit.StartsWith("9e22f56", StringComparison.Ordinal));
        Assert.True(UpgradeFixture.All.Count >= 7, "the fixtures should span the schema and data-folder changes: the first pushed commit, key ring, an old licence file, security events, ETag, bin, password reset, email settings");
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public async Task The_current_server_starts_on_what_an_earlier_build_wrote_and_every_part_of_it_is_intact(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            string database = Path.Combine(data, StorageOptions.SqliteFileName);
            SqliteTableCounts before = SqliteTableCounts.ReadVerified(database);
            Func<string, JsonElement?> snapshots = UpgradeData.SqliteSnapshots(database);
            using JsonDocument expectedDocument = fixture.Expected(UpgradeFixture.Sqlite);
            var checks = new UpgradeAssertions(fixture, expectedDocument.RootElement, snapshots);

            // The build that wrote this data bound its secrets to the folder IT ran from, which is never the folder a test host runs from: the operator's
            // one-time act of naming it (keyRing.legacyInstallPaths) is part of the upgrade here, and the secrets must then read from this other folder.
            await using var factory = new ExistingFolderServerFactory(data, fixture.KeyRingProtection, fixture.KeyEncryptionKey, legacyInstallPaths: fixture.ContentRoot);
            // The first request builds the host, which migrates the database before it serves anything.
            HttpClient? admin = await checks.SignIn(factory, checks.User("admin"));
            Assert.True(admin is not null, "the administrator could not sign in:\n" + string.Join("\n", checks.Problems));

            await checks.Users(factory, admin);
            await checks.Tokens(factory, admin);
            await checks.Documents(admin);
            await checks.Bin(admin);
            await checks.Assets(admin);
            await checks.Branding(admin);
            await checks.Themes(admin);
            await checks.Ai(admin);
            await checks.Email(admin);
            await checks.OldLicenceFileIsIgnored(admin, data);
            await checks.SecurityEvents(admin);
            await checks.Audit(admin);

            // The database as a whole: every migration this build knows is applied, and no table holds fewer rows than it did.
            SqliteTableCounts after = UpgradeData.SqliteCounts(database);
            foreach ((string table, long rows) in before.Counts)
            {
                checks.Check($"table {table} does not lose rows", after.Counts.TryGetValue(table, out long now) && now >= rows, $"{rows} -> {(after.Counts.TryGetValue(table, out long n) ? n : -1)}");
            }

            int known;
            await using (DraughtsmanDbContext db = StorageSetup.CreateContext(StorageOptionsFor(data)))
            {
                known = db.Database.GetMigrations().Count();
                checks.Check("no migration is left pending", !(await db.Database.GetPendingMigrationsAsync()).Any());
            }

            // After everything it read, make the instance write: restore from the bin, create, save an old document.
            await checks.RestoreFromBinAndWrite(admin);

            // The server took its safety copy on this start, because migrations were pending, and took none when the schema was already current.
            int pending = known - fixture.Migrations.Count;
            checks.Check(
                pending > 0 ? "the start took exactly one pre-migration copy" : "a current schema takes no copy",
                Directory.GetFiles(data, "data.db" + StorageMigrator.PreMigrationSuffix + "*").Length == (pending > 0 ? 1 : 0));

            Assert.True(checks.Problems.Count == 0, $"{label}: {checks.Problems.Count} of {checks.Checks} upgrade checks failed:\n  " + string.Join("\n  ", checks.Problems));
            Assert.True(checks.Checks > 40, $"{label}: only {checks.Checks} checks ran; the fixture or the assertions are hollow");
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    private static StorageOptions StorageOptionsFor(string dataFolder)
    {
        return new StorageOptions(StorageProvider.Sqlite, StorageOptions.DefaultSqliteConnectionString(dataFolder));
    }
}
