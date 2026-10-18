using System.Runtime.Versioning;
using Draughtsman.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Draughtsman.Storage.Tests;

/// <summary>
/// A database that opens and reads but cannot be written used to be invisible: health said ok, a wrong password was a 401 and a right one a bare
/// 500. <see cref="IStoreMaintenance.ProbeWrite"/> is the one question that tells it apart, and <see cref="StorageFaults"/> says which
/// exceptions mean "the database cannot save" so the server can answer them with a sentence instead of a stack trace.
/// </summary>
public sealed class StoreWriteProbeTests : IDisposable
{
    private readonly string _folder = TestPaths.NewTempFolder();

    public void Dispose()
    {
        TestPaths.DeleteTempFolder(_folder);
    }

    [Fact]
    public async Task A_writable_database_probes_writable_and_changes_nothing()
    {
        await using DraughtsmanDbContext db = await Migrated();
        int sessionsBefore = await db.Sessions.CountAsync();

        StoreWriteProbe probe = await new EfStoreMaintenance(db).ProbeWrite();

        Assert.True(probe.Writable, probe.Problem);
        Assert.Null(probe.Problem);
        Assert.Equal(sessionsBefore, await db.Sessions.CountAsync());
    }

    [UnixNonRootFact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_database_file_the_account_cannot_write_probes_as_not_writable_and_says_so()
    {
        await using (await Migrated())
        {
        }

        string file = Path.Combine(_folder, StorageOptions.SqliteFileName);
        File.SetUnixFileMode(file, UnixFileMode.UserRead);
        await using DraughtsmanDbContext db = StorageSetup.CreateContext(SqliteTestDatabase.Options(_folder));

        StoreWriteProbe probe = await new EfStoreMaintenance(db).ProbeWrite();

        Assert.False(probe.Writable);
        Assert.False(string.IsNullOrWhiteSpace(probe.Problem));
        // Reading still works: this is exactly the state that used to look healthy.
        Assert.Equal(0, await db.Sessions.CountAsync());
    }

    [Theory]
    [InlineData(8, true)] // SQLITE_READONLY
    [InlineData(13, true)] // SQLITE_FULL
    [InlineData(10, true)] // SQLITE_IOERR
    [InlineData(5, false)] // SQLITE_BUSY: contention, not a database that cannot save
    [InlineData(19, false)] // SQLITE_CONSTRAINT: a bad write, not an unwritable database
    public void A_sqlite_error_means_cannot_save_only_when_the_database_itself_is_the_problem(int code, bool expected)
    {
        var error = new SqliteException("boom", code, code);

        Assert.Equal(expected, StorageFaults.IsCannotWrite(error));
    }

    [Fact]
    public void The_cause_is_found_through_the_wrapping_exceptions_the_framework_adds()
    {
        var readOnly = new SqliteException("attempt to write a readonly database", 8, 8);

        Assert.True(StorageFaults.IsCannotWrite(new DbUpdateException("saving", readOnly)));
        Assert.True(StorageFaults.IsCannotWrite(new InvalidOperationException("outer", new DbUpdateException("saving", readOnly))));
        Assert.False(StorageFaults.IsCannotWrite(new InvalidOperationException("outer", new ArgumentException("inner"))));
        Assert.False(StorageFaults.IsCannotWrite(new InvalidOperationException("nothing to do with storage")));
    }

    [Theory]
    [InlineData("25006", true)] // read_only_sql_transaction: a replica, or default_transaction_read_only
    [InlineData("53100", true)] // disk_full
    [InlineData("42501", true)] // insufficient_privilege: the role may not write
    [InlineData("23505", false)] // unique_violation: a bad write
    public void A_postgres_error_means_cannot_save_only_when_the_server_will_not_write(string state, bool expected)
    {
        var error = new PostgresException("boom", "ERROR", "ERROR", state);

        Assert.Equal(expected, StorageFaults.IsCannotWrite(error));
    }

    private async Task<DraughtsmanDbContext> Migrated()
    {
        StorageOptions options = SqliteTestDatabase.Options(_folder);
        DraughtsmanDbContext db = StorageSetup.CreateContext(options);
        await StorageMigrator.Migrate(db, options, NullLogger.Instance);
        return db;
    }
}

/// <summary>A test that needs file modes to keep the account out: skipped on Windows and when run as root, which owns every file.</summary>
public sealed class UnixNonRootFactAttribute : FactAttribute
{
    public UnixNonRootFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "File modes do not keep an account out on Windows.";
        }
        else if (Environment.UserName == "root")
        {
            Skip = "Running as root, which can write a read-only file.";
        }
    }
}
