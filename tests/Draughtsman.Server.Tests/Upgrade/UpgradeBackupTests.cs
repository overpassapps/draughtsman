using Draughtsman.Server.Backup;
using Draughtsman.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// The pre-upgrade checklist's first step, "take a backup", run the way an operator runs it: with the NEW binary, on a data folder an
/// OLDER build wrote, before the new version has started (docs/upgrade-backup.md, "Before you upgrade"). The bundle must be restorable into
/// an empty folder with the new binary's own <c>restore</c>, give back exactly the old rows, and then migrate like any old database.
/// </summary>
public sealed class UpgradeBackupTests
{
    public static TheoryData<string> Labels => UpgradeFixture.Labels(UpgradeFixture.Sqlite);

    [Theory]
    [MemberData(nameof(Labels))]
    public async Task A_backup_taken_by_the_new_binary_of_an_old_data_folder_restores_to_exactly_the_old_rows_and_then_upgrades(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            SqliteTableCounts original = SqliteTableCounts.ReadVerified(Path.Combine(data, "data.db"));
            Func<string, System.Text.Json.JsonElement?> snapshots = UpgradeData.SqliteSnapshots(Path.Combine(data, "data.db"));
            using System.Text.Json.JsonDocument expected = fixture.Expected(UpgradeFixture.Sqlite);

            string bundles = Path.Combine(temp, "bundles");
            Directory.CreateDirectory(bundles);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int backupExit = await BackupCommands.BackupAsync(["--out", bundles + Path.DirectorySeparatorChar], stdout, stderr, data);

            Assert.True(backupExit == 0, $"{label}: backup of the old data folder failed: {stderr}");
            string bundle = Directory.GetFiles(bundles, "*.zip").Single();
            Assert.Contains("documents", stdout.ToString(), StringComparison.Ordinal);

            string restored = Path.Combine(temp, "restored");
            var restoreOut = new StringWriter();
            var restoreErr = new StringWriter();
            int restoreExit = await BackupCommands.RestoreAsync([bundle], restoreOut, restoreErr, restored);

            Assert.True(restoreExit == 0, $"{label}: restore failed: {restoreErr}");
            string restoredDatabase = Path.Combine(restored, "data.db");
            SqliteTableCounts back = SqliteTableCounts.ReadVerified(restoredDatabase);
            Assert.Equal(original.Counts.OrderBy(c => c.Key), back.Counts.OrderBy(c => c.Key));

            // Every document is the same stored snapshot.
            Func<string, System.Text.Json.JsonElement?> restoredSnapshots = UpgradeData.SqliteSnapshots(restoredDatabase);
            foreach (System.Text.Json.JsonElement doc in expected.RootElement.GetProperty("documents").EnumerateArray())
            {
                string id = doc.GetProperty("id").GetString()!;
                Assert.True(snapshots(id) is { } was && restoredSnapshots(id) is { } now && Canonical.Hash(was) == Canonical.Hash(now), $"{label}: document {id} differs after backup and restore");
            }

            // And the restored folder upgrades like the original would have.
            var options = new StorageOptions(StorageProvider.Sqlite, StorageOptions.DefaultSqliteConnectionString(restored));
            await using (DraughtsmanDbContext db = StorageSetup.CreateContext(options))
            {
                await StorageMigrator.Migrate(db, options, NullLogger.Instance);
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }

            SqlitePools.Release(restored);
            foreach ((string table, long rows) in original.Counts)
            {
                Assert.True(UpgradeData.SqliteCounts(restoredDatabase).Counts[table] >= rows, $"{label}: table {table} lost rows in backup, restore and upgrade");
            }
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }
}
