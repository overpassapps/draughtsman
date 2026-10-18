using Draughtsman.Tests.Shared;
using Microsoft.Data.Sqlite;

namespace Draughtsman.Storage.Tests;

/// <summary>
/// <see cref="SqliteTableCounts.ReadVerified"/> is the proof step of the pre-migration safety copy: if it wrongly refuses, the server will not upgrade an
/// existing database. CI found that on Linux x64 it did, for a database that passes <c>integrity_check</c> ("reported: ok"): a .NET 10 JIT miscompile of
/// the old <c>out</c>-argument shape, which only shows in OPTIMISED code (a method starts at tier 0, which is correct, and is recompiled optimised
/// after 30 calls, which a start-up that calls it once never reaches, but a long test run does). So this project runs with tiered compilation off
/// (<c>Draughtsman.Storage.Tests.csproj</c>): every call here is optimised from the first, on every platform, and these tests read real databases
/// written by other builds of SQLite, so a platform or JIT difference in the proof step fails here deterministically instead of after a thousand tests.
/// </summary>
public sealed class SqliteTableCountsVerifiedTests : IDisposable
{
    private readonly string _folder = TestPaths.NewTempFolder();

    public void Dispose()
    {
        TestPaths.DeleteTempFolder(_folder);
    }

    private string DatabasePath => Path.Combine(_folder, "data.db");

    /// <summary>Every database the committed upgrade fixtures hold: written by earlier builds of this program, on another machine and SQLite library than the host running the test.</summary>
    public static TheoryData<string> FixtureLabels => StorageMigratorSafetyTests.FixtureLabels;

    [Theory]
    [MemberData(nameof(FixtureLabels))]
    public void A_whole_database_an_earlier_build_wrote_reads_verified_with_its_row_counts(string label)
    {
        using System.IO.FileStream file = File.OpenRead(Path.Combine(UpgradeFixturePaths.Root, label, "sqlite", "data-folder.tar.gz"));
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        System.Formats.Tar.TarFile.ExtractToDirectory(gzip, _folder, overwriteFiles: true);

        // Every call, not one: an optimised-code fault that depends on how often a method has run needs the repeats to be repeated.
        SqliteTableCounts counts = SqliteTableCounts.ReadVerified(DatabasePath);
        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(counts.Counts.OrderBy(c => c.Key), SqliteTableCounts.ReadVerified(DatabasePath).Counts.OrderBy(c => c.Key));
        }

        Assert.True(counts.Counts["documents"] > 0, "the fixture holds documents");
        Assert.True(counts.Counts["users"] > 0, "the fixture holds users");
    }

    [Fact]
    public void A_database_this_host_just_wrote_reads_verified_and_leaves_no_log_files_beside_it()
    {
        SqliteTestDatabase.Populate(DatabasePath, rows: 50);

        SqliteTableCounts counts = SqliteTableCounts.ReadVerified(DatabasePath);

        Assert.Equal(50, counts.Counts["items"]);
        Assert.DoesNotContain("sqlite_sequence", counts.Counts.Keys);
        Assert.False(File.Exists(DatabasePath + "-wal"));
        Assert.False(File.Exists(DatabasePath + "-shm"));
    }

    [Fact]
    public void A_damaged_database_is_refused_with_what_integrity_check_said_not_with_ok()
    {
        SqliteTestDatabase.Populate(DatabasePath, rows: 3000);
        SqliteTestDatabase.DamageAPage(DatabasePath);

        InvalidDataException refused = Assert.Throws<InvalidDataException>(() => SqliteTableCounts.ReadVerified(DatabasePath));

        Assert.StartsWith("PRAGMA integrity_check reported: ", refused.Message, StringComparison.Ordinal);
        Assert.NotEqual("PRAGMA integrity_check reported: ok", refused.Message);
    }

    [Fact]
    public void A_database_SQLite_itself_cannot_read_is_refused_too_and_by_an_exception_the_migrator_catches()
    {
        SqliteTestDatabase.Populate(DatabasePath, rows: 3000);
        using (var stream = new FileStream(DatabasePath, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(4096L * 4, SeekOrigin.Begin);
            stream.Write(new byte[4096].Select(_ => (byte)0xFF).ToArray());
        }

        // StorageMigrator.TakeVerifiedCopy refuses the upgrade on exactly SqliteException and InvalidDataException.
        Assert.Throws<SqliteException>(() => SqliteTableCounts.ReadVerified(DatabasePath));
    }
}
