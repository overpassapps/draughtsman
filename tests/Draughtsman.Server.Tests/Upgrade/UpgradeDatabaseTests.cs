using System.Security.Cryptography;
using Draughtsman.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// The database half of the upgrade rehearsal, without a server: the migrator is run on each fixture's copy and the pre-migration
/// copy it leaves is held to what the documentation promises (docs/upgrade-backup.md, "Going back"): it is a whole, openable
/// database on the OLD schema, holding exactly the old rows, and putting it back with the documented steps returns the data folder
/// to the old state byte for byte, after which the upgrade can simply be run again.
/// </summary>
public sealed class UpgradeDatabaseTests
{
    public static TheoryData<string> Labels => UpgradeFixture.Labels(UpgradeFixture.Sqlite);

    private static string Sha(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public async Task The_pre_migration_copy_is_a_valid_old_schema_database_and_putting_it_back_restores_the_old_state_exactly(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            string database = Path.Combine(data, StorageOptions.SqliteFileName);
            string originalHash = Sha(database);
            SqliteTableCounts original = SqliteTableCounts.ReadVerified(database);
            var options = new StorageOptions(StorageProvider.Sqlite, StorageOptions.DefaultSqliteConnectionString(data));

            string? copy;
            int knownMigrations;
            await using (DraughtsmanDbContext db = StorageSetup.CreateContext(options))
            {
                knownMigrations = db.Database.GetMigrations().Count();
                copy = await StorageMigrator.Migrate(db, options, NullLogger.Instance);
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.Equal(knownMigrations, (await db.Database.GetAppliedMigrationsAsync()).Count());
            }

            SqlitePools.Release(data);
            int pending = knownMigrations - fixture.Migrations.Count;
            if (pending == 0)
            {
                Assert.Null(copy);
                Assert.Equal(originalHash, Sha(database));
                return;
            }

            // The copy exists beside the database, is the one this start took, and is whole.
            Assert.NotNull(copy);
            Assert.StartsWith(database + StorageMigrator.PreMigrationSuffix, copy, StringComparison.Ordinal);
            Assert.Equal(originalHash, Sha(copy));
            SqliteTableCounts inCopy = SqliteTableCounts.ReadVerified(copy);
            Assert.Equal(original.Counts.OrderBy(c => c.Key), inCopy.Counts.OrderBy(c => c.Key));

            // It is on the OLD schema: opened with this build's own schema check it reports exactly the fixture's migrations as applied
            // and the rest as pending, which is what the older binary needs in order to start on it again.
            var copyOptions = new StorageOptions(StorageProvider.Sqlite, $"Data Source={copy}");
            await using (DraughtsmanDbContext old = StorageSetup.CreateContext(copyOptions))
            {
                Assert.Equal(fixture.Migrations, (await old.Database.GetAppliedMigrationsAsync()).Order(StringComparer.Ordinal));
                Assert.Equal(pending, (await old.Database.GetPendingMigrationsAsync()).Count());
            }

            SqlitePools.ReleaseDatabase(copy);

            // The migrated database holds at least every old row, in every old table.
            SqliteTableCounts migrated = UpgradeData.SqliteCounts(database);
            foreach ((string table, long rows) in original.Counts)
            {
                Assert.True(migrated.Counts.TryGetValue(table, out long now) && now >= rows, $"{label}: table {table} {rows} -> {(migrated.Counts.TryGetValue(table, out long n) ? n : -1)}");
            }

            // The documented way back (docs/upgrade-backup.md, "Going back"): server stopped, delete the -wal and -shm, copy the
            // pre-migration copy over data.db. The result is the old data folder state, byte for byte.
            File.Delete(database + "-wal");
            File.Delete(database + "-shm");
            File.Copy(copy, database, overwrite: true);
            Assert.Equal(originalHash, Sha(database));
            Assert.False(File.Exists(database + "-wal"));

            // And the upgrade can be run again from there, to the same result.
            await using (DraughtsmanDbContext again = StorageSetup.CreateContext(options))
            {
                string? second = await StorageMigrator.Migrate(again, options, NullLogger.Instance);
                Assert.NotNull(second);
                Assert.Empty(await again.Database.GetPendingMigrationsAsync());
            }

            SqlitePools.Release(data);
            Assert.Equal(migrated.Counts.OrderBy(c => c.Key), UpgradeData.SqliteCounts(database).Counts.OrderBy(c => c.Key));
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }
}
