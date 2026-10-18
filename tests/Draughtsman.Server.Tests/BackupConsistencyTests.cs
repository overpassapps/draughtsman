using Draughtsman.Server.Backup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// OP-01's torn-copy risk, made concrete. EF Core creates every SQLite database in write-ahead-log mode, and the server saves
/// every couple of seconds while someone is editing, so <c>data.db</c> is not the database: the newest commits sit in
/// <c>data.db-wal</c> until a checkpoint folds them in (a live instance's <c>data.db</c> can be 4 KB beside a 240 KB log).
/// A nightly agent's plain copy of the data folder therefore either misses everything still in the log, or, when it spans a
/// checkpoint, carries a main file built from two moments. The tests mirror that with the smallest honest model: two tables
/// whose totals are equal in every committed state (every transaction adds one to every row of both), so a copy whose totals
/// differ holds a database that never existed. Each situation is taken two ways - a file copy and <see cref="BackupWriter"/>'s
/// <c>VACUUM INTO</c> - and the first is shown wrong and the second right.
/// </summary>
public sealed class BackupConsistencyTests(ITestOutputHelper output) : IDisposable
{
    private const int Rows = 1500;
    private const int PageSize = 4096;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqlitePools.ReleaseUnder(_folder);
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>The premise of everything below, asserted rather than assumed: the real startup path leaves the database in WAL mode.</summary>
    [Fact]
    public async Task A_database_created_through_the_servers_own_startup_path_is_in_wal_mode()
    {
        Directory.CreateDirectory(_folder);
        var options = new Draughtsman.Storage.StorageOptions(Draughtsman.Storage.StorageProvider.Sqlite, Draughtsman.Storage.StorageOptions.DefaultSqliteConnectionString(_folder));
        await using Draughtsman.Storage.DraughtsmanDbContext db = Draughtsman.Storage.StorageSetup.CreateContext(options);
        await Draughtsman.Storage.StorageMigrator.Migrate(db, options, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await db.Database.OpenConnectionAsync();

        System.Data.Common.DbConnection connection = db.Database.GetDbConnection();
        using System.Data.Common.DbCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", command.ExecuteScalar() as string);
    }

    /// <summary>
    /// The everyday failure, no race needed: a save commits, and lands in the log, not in <c>data.db</c>. A copy of the main file
    /// (what a backup that copies "the database file" takes) is a consistent database - of before that save. The snapshot has it.
    /// </summary>
    [PosixOnlyFact]
    public void A_copy_of_data_db_alone_misses_a_commit_that_is_still_in_the_log_while_the_snapshot_has_it()
    {
        string database = NewDatabase();
        using SqliteConnection writer = Open(database);
        // A small save, like a document autosave: a page or two, far under the log size that triggers a checkpoint.
        Execute(writer, "UPDATE meta SET version = version + 1");
        Assert.True(File.Exists(database + "-wal"), "the commit should be sitting in the write-ahead log");

        string mainFileOnly = Path.Combine(_folder, "main-only.db");
        Tool("cp", database, mainFileOnly);
        string snapshot = Path.Combine(_folder, "snapshot.db");
        BackupWriter.SnapshotSqlite(new SqliteConnectionStringBuilder { DataSource = database }.ToString(), snapshot);

        long live = Version(writer);
        long copied = Version(mainFileOnly);
        long snapped = Version(snapshot);
        output.WriteLine($"live version {live}; copy of data.db alone {copied}; snapshot {snapped}");
        Assert.Equal(2, live);
        Assert.Equal(1, copied);
        Assert.Equal(live, snapped);
    }

    /// <summary>
    /// A cp of a database a few hundred MB large takes long enough for checkpoints to land while it runs; here that is one
    /// save and its checkpoint between the copy of the first half of the file and the second. The half copy holds table a from
    /// before the save and table b from after it: a database that was never committed. The snapshot taken across the very same
    /// checkpoint is whole. (It is the file copy that is on trial here; the test above is the one that fails if <see cref="BackupWriter.SnapshotSqlite"/> is ever turned into a file copy, because a settled main file is whole by itself.)
    /// </summary>
    [PosixOnlyFact]
    public void A_file_copy_that_spans_a_checkpoint_is_torn_while_a_snapshot_across_the_same_one_is_not()
    {
        string database = NewDatabase();
        using SqliteConnection writer = Open(database);
        long pages = new FileInfo(database).Length / PageSize;
        long half = pages / 2;

        // The slow copy: first half of the file now (dd, like cp, takes no lock and does not care what the database is doing)...
        string torn = Path.Combine(_folder, "torn.db");
        Tool("dd", $"if={database}", $"of={torn}", $"bs={PageSize}", $"count={half}");

        // ...a save lands and the log is checkpointed into the main file (SQLite does this by itself every ~1000 pages)...
        AddOneToEverything(writer);
        Execute(writer, "PRAGMA wal_checkpoint(TRUNCATE)");

        // ...and the copy carries on with the second half.
        Tool("dd", $"if={database}", $"of={torn}", $"bs={PageSize}", $"skip={half}", $"seek={half}", "conv=notrunc");

        string snapshot = Path.Combine(_folder, "snapshot.db");
        BackupWriter.SnapshotSqlite(new SqliteConnectionStringBuilder { DataSource = database }.ToString(), snapshot);

        (long tornA, long tornB) = Totals(torn);
        (long snapA, long snapB) = Totals(snapshot);
        output.WriteLine($"file copy across a checkpoint: a={tornA} b={tornB} (equal in every committed state); snapshot: a={snapA} b={snapB}");
        Assert.NotEqual(tornA, tornB);
        Assert.Equal(snapA, snapB);
        Assert.Equal(2L * Rows, snapA);
    }

    /// <summary>
    /// The same under real concurrency rather than a scripted interleaving: a writer commits as fast as it can while snapshots are
    /// taken (every one must be whole, and current) and <c>cp</c> copies the three files as <c>cp -r data/</c> would. Raw copies
    /// are classified and reported (torn: a and b differ; behind: older than the last commit before the copy began; unreadable),
    /// but how many go wrong depends on disk and load, so that count is information, never an assertion, except in the large
    /// mode below.
    /// </summary>
    [PosixOnlyFact]
    public async Task Snapshots_taken_while_a_writer_commits_continuously_are_always_whole()
    {
        // The default database is small and quick. DRAUGHTSMAN_TORN_COPY_ROWS=40000 makes it ~80 MB, where a copy takes long
        // enough to span checkpoints as a nightly cp of a real instance does, and then bad copies are asserted too.
        bool large = int.TryParse(Environment.GetEnvironmentVariable("DRAUGHTSMAN_TORN_COPY_ROWS"), out int configured) && configured > 0;
        int rows = large ? configured : 3000;
        string database = NewDatabase(rows);
        using var cancel = new CancellationTokenSource();
        long committed = 1;
        Task writerTask = Task.Run(() =>
        {
            using SqliteConnection writer = Open(database);
            while (!cancel.IsCancellationRequested)
            {
                AddOneToEverything(writer);
                Interlocked.Increment(ref committed);
            }
        });

        // Give the writer a moment to be committing before anything is measured (a loaded machine may start it late).
        while (Interlocked.Read(ref committed) < 3)
        {
            await Task.Delay(10);
        }

        int snapshots = 0;
        int copies = 0;
        int torn = 0;
        int behind = 0;
        int unreadable = 0;
        DateTime end = DateTime.UtcNow.AddSeconds(large ? 20 : 3);
        while (DateTime.UtcNow < end)
        {
            long before = Interlocked.Read(ref committed);
            string snapshot = Path.Combine(_folder, $"s{snapshots}.db");
            BackupWriter.SnapshotSqlite(new SqliteConnectionStringBuilder { DataSource = database }.ToString(), snapshot);
            (long a, long b) = Totals(snapshot);
            Assert.Equal(a, b);
            Assert.True(Version(snapshot) >= before, "a snapshot is never older than the last commit before it began");
            snapshots++;

            before = Interlocked.Read(ref committed);
            string raw = Path.Combine(_folder, $"raw{copies}");
            Directory.CreateDirectory(raw);
            // cp -r copies whatever is in the folder when it gets there: a log or index file that has just gone (or not yet been
            // made) is simply missing from that copy, which is one of the ways such a copy is wrong.
            Tool("cp", database, raw);
            foreach (string sibling in new[] { database + "-wal", database + "-shm" })
            {
                TryTool("cp", sibling, raw);
            }

            copies++;
            try
            {
                string copy = Path.Combine(raw, "data.db");
                (long ra, long rb) = Totals(copy);
                if (ra != rb)
                {
                    torn++;
                }
                else if (Version(copy) < before)
                {
                    behind++;
                }
            }
            catch (SqliteException)
            {
                unreadable++;
            }
        }

        await cancel.CancelAsync();
        await writerTask;
        output.WriteLine($"{snapshots} snapshots, all whole and current; of {copies} raw copies of data.db + -wal + -shm taken at the same time: {torn} torn, {behind} behind the last commit, {unreadable} unreadable");
        Assert.True(snapshots >= 2, "the loop should have taken several snapshots");
        if (large)
        {
            Assert.True(torn + behind + unreadable > 0, "with a database this size, a copy taken under continuous writes is expected to go wrong");
        }
    }

    private string NewDatabase(int rows = Rows)
    {
        Directory.CreateDirectory(_folder);
        string database = Path.Combine(_folder, "data.db");
        using SqliteConnection connection = Open(database);
        // The mode EF Core gives the instance's database (asserted in the first test).
        Execute(connection, "PRAGMA journal_mode=WAL");
        Execute(connection, "CREATE TABLE a (id INTEGER PRIMARY KEY, v INTEGER NOT NULL, pad TEXT NOT NULL)");
        Execute(connection, "CREATE TABLE b (id INTEGER PRIMARY KEY, v INTEGER NOT NULL, pad TEXT NOT NULL)");
        Execute(connection, "CREATE TABLE meta (id INTEGER PRIMARY KEY, version INTEGER NOT NULL)");
        Execute(connection, "INSERT INTO meta (id, version) VALUES (1, 1)");
        // Table a first, then table b, so a's pages sit in the first half of the file and b's in the second.
        foreach (string table in new[] { "a", "b" })
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            for (int i = 0; i < rows; i++)
            {
                Execute(connection, $"INSERT INTO {table} (v, pad) VALUES (1, '{new string('p', 900)}')", transaction);
            }

            transaction.Commit();
        }

        // Start from a settled file: everything in the main file, an empty log.
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
        return database;
    }

    private static SqliteConnection Open(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false, DefaultTimeout = 30 }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>One committed save: every row of both tables moves together, so a and b have equal totals in every state that was ever committed.</summary>
    private static void AddOneToEverything(SqliteConnection connection)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        Execute(connection, "UPDATE a SET v = v + 1", transaction);
        Execute(connection, "UPDATE b SET v = v + 1", transaction);
        Execute(connection, "UPDATE meta SET version = version + 1", transaction);
        transaction.Commit();
    }

    private static (long A, long B) Totals(string database)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return (Scalar(connection, "SELECT SUM(v) FROM a"), Scalar(connection, "SELECT SUM(v) FROM b"));
    }

    private static long Version(string database)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return Version(connection);
    }

    private static long Version(SqliteConnection connection)
    {
        return Scalar(connection, "SELECT version FROM meta");
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// A real copy tool rather than a .NET stream: on Unix .NET takes an advisory lock on every file it opens, which a
    /// running SQLite writer (correctly) fights, whereas <c>cp</c> and <c>dd</c> read the file without asking anyone.
    /// </summary>
    private static void Tool(string name, params string[] arguments)
    {
        (int exitCode, string error) = TryTool(name, arguments);
        Assert.True(exitCode == 0, $"{name} failed: {error}");
    }

    private static (int ExitCode, string Error) TryTool(string name, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(name) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start " + name);
        process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, error);
    }
}

/// <summary>
/// The torn-copy demonstrations need a tool that reads a SQLite file without taking a lock, which is what <c>cp</c> does on
/// Linux and macOS. On Windows SQLite holds mandatory byte-range locks, so a plain copy of a busy database fails outright
/// instead of tearing, and there is nothing to demonstrate.
/// </summary>
public sealed class PosixOnlyFactAttribute : FactAttribute
{
    public PosixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Needs cp/dd on Linux or macOS; Windows locks a busy database file mandatorily, so a copy fails instead of tearing.";
        }
    }
}
