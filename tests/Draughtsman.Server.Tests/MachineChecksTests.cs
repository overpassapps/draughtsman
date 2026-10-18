using System.Diagnostics;
using Draughtsman.Server.Diagnostics;

namespace Draughtsman.Server.Tests;

/// <summary>The data-folder, database and disk findings (both directions), the probe that gathers them, and the file-owner reading the doctor relies on.</summary>
public class MachineChecksTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;
    private const string DatabaseFile = "/data/data.db";

    private static MachineProbeResult Probe(string? folderError = null, string? dbError = null, string? dbFile = DatabaseFile, bool dbExists = true, long? free = 50 * GiB, long? total = 100 * GiB, bool folderExists = true, string provider = "sqlite")
    {
        return new MachineProbeResult("/data", folderExists, folderError, provider, dbFile, dbExists, dbError, free, total, new FileOwner("draughtsman", 10001, 10001), new FileOwner("root", 0, 0));
    }

    /// <summary>
    /// The hint names the database file on Unix (who owns it, or <c>ls -ld</c> on it); on Windows it is the permissions wording (Properties > Security),
    /// which has no path in it at all. Either way it is the owner or permissions advice the finding promises.
    /// </summary>
    private static void AssertHintNamesTheDatabase(string hint)
    {
        string expected = OperatingSystem.IsWindows() ? "Properties > Security" : DatabaseFile;
        Assert.Contains(expected, hint, StringComparison.Ordinal);
    }

    private static DiagnosticItem Item(MachineProbeResult probe, string id)
    {
        return MachineChecks.Evaluate(probe).Single(i => i.Id == id);
    }

    // ------------------------------------------------------------------------------------------------------ data folder and database

    [Fact]
    public void A_writable_folder_and_database_pass()
    {
        DiagnosticItem item = Item(Probe(), "data");

        Assert.Equal(DiagnosticStatus.Pass, item.Status);
        Assert.Equal("/data/data.db", item.Facts!["Database file"]);
    }

    [Fact]
    public void A_folder_the_server_cannot_write_fails_and_says_how_to_hand_it_back()
    {
        DiagnosticItem item = Item(Probe(folderError: "UnauthorizedAccessException: Access denied"), "data");

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Contains("cannot write to the data folder /data", item.Summary, StringComparison.Ordinal);
        Assert.Contains("chown -R <server account> /data", item.Fix!.Shell, StringComparison.Ordinal);
    }

    [Fact]
    public void A_database_the_server_cannot_write_fails_and_names_the_500_it_causes_and_the_owner()
    {
        DiagnosticItem item = Item(Probe(dbError: "UnauthorizedAccessException: Access denied"), "data");

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Contains("a correct password is answered with an error instead of a session", item.Summary, StringComparison.Ordinal);
        Assert.Equal("root (uid 0)", item.Facts!["Database owner"]);
        AssertHintNamesTheDatabase(item.Detail![0]);
    }

    [Fact]
    public void The_findings_name_whose_account_was_tried_the_server_inside_the_server_and_the_commands_for_the_doctor()
    {
        DiagnosticItem inServer = MachineChecks.Evaluate(Probe(dbError: "denied")).Single(i => i.Id == "data");
        DiagnosticItem forDoctor = MachineChecks.Evaluate(Probe(dbError: "denied"), "the account running this command").Single(i => i.Id == "data");

        Assert.Contains("that was tried as the server's account.", inServer.Summary, StringComparison.Ordinal);
        Assert.Contains("that was tried as the account running this command.", forDoctor.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("tried as the server's account", forDoctor.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_that_does_not_exist_fails()
    {
        DiagnosticItem item = Item(Probe(folderExists: false, folderError: "the folder does not exist"), "data");

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
    }

    [Fact]
    public void A_database_that_does_not_exist_yet_and_postgres_both_pass_with_a_note()
    {
        DiagnosticItem fresh = Item(Probe(dbExists: false), "data");
        DiagnosticItem postgres = Item(Probe(dbFile: null, dbExists: false, provider: "postgres"), "data");

        Assert.Equal(DiagnosticStatus.Pass, fresh.Status);
        Assert.Contains("does not exist yet", fresh.Summary, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStatus.Pass, postgres.Status);
        Assert.Contains("only the folder was checked", postgres.Summary, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------ disk

    [Theory]
    [InlineData(50L * GiB, 100L * GiB, DiagnosticStatus.Pass)]
    [InlineData(10L * GiB, 100L * GiB, DiagnosticStatus.Pass)]
    [InlineData(2L * GiB, 20L * GiB, DiagnosticStatus.Pass)] // a small volume with 10% free is fine
    [InlineData(60L * GiB, 1000L * GiB, DiagnosticStatus.Pass)] // a big volume with 6% free is tens of gibibytes: not a warning
    [InlineData(4L * GiB, 100L * GiB, DiagnosticStatus.Warn)] // under 5 GiB and under a tenth of the volume
    [InlineData(1L * GiB - 1, 100L * GiB, DiagnosticStatus.Warn)]
    [InlineData(500L * MiB, 100L * GiB, DiagnosticStatus.Warn)]
    [InlineData(3L * GiB, 100L * GiB, DiagnosticStatus.Warn)] // 3% of the volume
    [InlineData(100L * MiB, 100L * GiB, DiagnosticStatus.Warn)]
    [InlineData(100L * MiB - 1, 100L * GiB, DiagnosticStatus.Fail)]
    [InlineData(0L, 100L * GiB, DiagnosticStatus.Fail)]
    public void Free_space_passes_warns_and_fails_at_the_documented_thresholds(long free, long total, DiagnosticStatus expected)
    {
        Assert.Equal(expected, Item(Probe(free: free, total: total), "disk").Status);
    }

    [Fact]
    public void Free_space_that_could_not_be_read_is_a_warning_not_a_pass()
    {
        DiagnosticItem item = Item(Probe(free: null, total: null), "disk");

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KiB")]
    [InlineData(1536L, "1.5 KiB")]
    [InlineData(100L * MiB, "100 MiB")]
    [InlineData(5L * GiB, "5 GiB")]
    public void Sizes_read_as_a_person_would_write_them(long bytes, string expected)
    {
        Assert.Equal(expected, MachineChecks.Size(bytes));
    }

    // ------------------------------------------------------------------------------------------------------ the probe, on a real folder

    [Fact]
    public void The_probe_on_a_writable_folder_finds_it_writable_and_leaves_nothing_behind()
    {
        string folder = NewFolder();
        try
        {
            string db = Path.Combine(folder, "data.db");
            File.WriteAllText(db, "x");

            MachineProbeResult result = MachineProbe.Run(folder, "sqlite", db);

            Assert.True(result.FolderExists);
            Assert.Null(result.FolderWriteError);
            Assert.Null(result.DatabaseWriteError);
            Assert.True(result.DatabaseFileExists);
            Assert.True(result.FreeBytes > 0);
            Assert.Equal(["data.db"], Directory.GetFileSystemEntries(folder).Select(Path.GetFileName));
            Assert.Equal("x", File.ReadAllText(db));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_probe_on_a_missing_folder_says_so()
    {
        MachineProbeResult result = MachineProbe.Run(Path.Combine(Path.GetTempPath(), "draughtsman-tests", "no-such-" + Guid.NewGuid().ToString("N")), "sqlite", null);

        Assert.False(result.FolderExists);
        Assert.Equal("the folder does not exist", result.FolderWriteError);
    }

    [UnixOnlyFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void The_probe_finds_a_read_only_folder_and_a_read_only_database_unless_the_account_is_root()
    {
        if (FileOwners.CurrentUserId() == 0)
        {
            return; // root writes anywhere; there is nothing to find.
        }

        string folder = NewFolder();
        try
        {
            string db = Path.Combine(folder, "data.db");
            File.WriteAllText(db, "x");
            File.SetUnixFileMode(db, UnixFileMode.UserRead);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            MachineProbeResult result = MachineProbe.Run(folder, "sqlite", db);

            Assert.NotNull(result.FolderWriteError);
            Assert.NotNull(result.DatabaseWriteError);
            Assert.Equal(DiagnosticStatus.Fail, MachineChecks.Evaluate(result).Single(i => i.Id == "data").Status);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(folder, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------------------------------ owners

    [UnixOnlyFact]
    public void A_file_this_process_made_is_owned_by_the_user_id_this_process_runs_as()
    {
        string folder = NewFolder();
        try
        {
            string file = Path.Combine(folder, "mine");
            File.WriteAllText(file, "x");

            FileOwner? owner = FileOwners.Of(file);

            Assert.NotNull(owner);
            Assert.Equal(FileOwners.CurrentUserId(), owner.Uid);
            Assert.NotNull(owner.Gid);
            Assert.Equal(owner.Uid, FileOwners.Of(folder)!.Uid);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [UnixOnlyFact]
    public void The_owner_agrees_with_what_the_system_stat_command_says()
    {
        // The offsets in FileOwners are per platform: ask the platform's own tool as a second opinion, for the user and the group.
        // BSD stat takes -f, GNU stat takes -c; the two format letters are the same.
        string[] format = OperatingSystem.IsMacOS() ? ["-f", "%u %g"] : ["-c", "%u %g"];
        string folder = NewFolder();
        try
        {
            string file = Path.Combine(folder, "mine");
            File.WriteAllText(file, "x");
            var start = new ProcessStartInfo("stat") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (string argument in format)
            {
                start.ArgumentList.Add(argument);
            }

            start.ArgumentList.Add(file);
            using Process process = Process.Start(start)!;
            string[] fields = process.StandardOutput.ReadToEnd().Trim().Split(' ');
            process.WaitForExit();

            FileOwner owner = FileOwners.Of(file)!;
            Assert.Equal(long.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture), owner.Uid);
            Assert.Equal(long.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture), owner.Gid);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_path_that_does_not_exist_has_no_owner_rather_than_a_wrong_one()
    {
        Assert.Null(FileOwners.Of(Path.Combine(Path.GetTempPath(), "draughtsman-tests", "no-such-" + Guid.NewGuid().ToString("N"))));
    }

    [Theory]
    [InlineData(0, "---------")]
    [InlineData(0b111_101_000, "rwxr-x---")]
    [InlineData(0b110_100_100, "rw-r--r--")]
    [InlineData(0b111_111_111, "rwxrwxrwx")]
    public void Modes_are_written_the_way_ls_writes_them(int bits, string expected)
    {
        Assert.Equal(expected, FileOwners.FormatMode((UnixFileMode)bits));
    }

    [Fact]
    public void Owners_read_the_way_a_person_expects()
    {
        Assert.Equal("draughtsman (uid 10001)", FileOwners.Describe(new FileOwner("draughtsman", 10001, 10001)));
        Assert.Equal("CORP\\svc", FileOwners.Describe(new FileOwner("CORP\\svc", null, null)));
        Assert.Equal("unknown", FileOwners.Describe(null));
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}

/// <summary>
/// Opening SQLite as root hands the database's <c>-wal</c> and <c>-shm</c> files to the database's owner: harmless when everything is the server's, damage when a root-run
/// restore left <c>data.db</c> root's. The doctor and the bundle therefore do not open the database in that one position (found by running them as root in a container).
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class DatabaseGuardTests
{
    private const string Db = "/data/data.db";

    private static bool Safe(long? current, long dbOwner, long? walOwner, long? shmOwner, out string? reason)
    {
        var owners = new Dictionary<string, long?> { [Db] = dbOwner, [Db + "-wal"] = walOwner, [Db + "-shm"] = shmOwner };
        return DatabaseGuard.SafeToOpen(Db, current, path => owners.TryGetValue(path, out long? o) && o is not null, path => owners.GetValueOrDefault(path), out reason);
    }

    [UnixOnlyFact]
    public void Root_opening_a_root_owned_database_beside_the_servers_own_wal_is_refused_and_the_reason_says_what_to_do()
    {
        bool safe = Safe(current: 0, dbOwner: 0, walOwner: 10001, shmOwner: 10001, out string? reason);

        Assert.False(safe);
        Assert.Contains("running as root", reason, StringComparison.Ordinal);
        Assert.Contains("data.db is owned by uid 0 but data.db-wal by uid 10001", reason, StringComparison.Ordinal);
        Assert.Contains("run this as the server's own account", reason, StringComparison.Ordinal);
    }

    [UnixOnlyFact]
    public void Every_other_position_is_safe()
    {
        Assert.True(Safe(current: 0, dbOwner: 10001, walOwner: 10001, shmOwner: 10001, out _)); // root, everything the server's: SQLite changes nothing
        Assert.True(Safe(current: 0, dbOwner: 0, walOwner: 0, shmOwner: 0, out _)); // root, everything root's: consistent
        Assert.True(Safe(current: 0, dbOwner: 0, walOwner: null, shmOwner: null, out _)); // no log beside it: a stopped server
        Assert.True(Safe(current: 10001, dbOwner: 0, walOwner: 10001, shmOwner: 10001, out _)); // not root: SQLite cannot give anything away
        Assert.True(Safe(current: null, dbOwner: 0, walOwner: 10001, shmOwner: 10001, out _)); // no uid (Windows)
        Assert.False(Safe(current: 0, dbOwner: 10001, walOwner: 10001, shmOwner: 0, out _)); // any one stray sibling is enough
    }

    [Fact]
    public void A_database_that_is_not_there_or_not_a_file_is_nothing_to_guard()
    {
        Assert.True(DatabaseGuard.SafeToOpen(null, 0, out _));
        Assert.True(DatabaseGuard.SafeToOpen("/definitely/not/here/data.db", 0, out _));
    }
}
