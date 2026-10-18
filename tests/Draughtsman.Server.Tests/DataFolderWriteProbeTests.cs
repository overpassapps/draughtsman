using System.Runtime.Versioning;
using Draughtsman.Server.Hardening;

namespace Draughtsman.Server.Tests;

/// <summary>
/// An unwritable data folder used to end the process with an unhandled exception and a stack trace. The probe turns it into one plain line that
/// names the folder, the account and the fix, before anything else touches the folder.
/// </summary>
public sealed class DataFolderWriteProbeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    public DataFolderWriteProbeTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_writable_folder_passes_and_the_probe_leaves_nothing_behind()
    {
        string? problem = DataFolderWriteProbe.Check(_folder, Path.Combine(_folder, "data.db"));

        Assert.Null(problem);
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
    }

    [Fact]
    public void A_database_file_that_can_be_written_passes()
    {
        string database = Path.Combine(_folder, "data.db");
        File.WriteAllText(database, "not really sqlite; the probe only asks whether it can be opened for writing");

        Assert.Null(DataFolderWriteProbe.Check(_folder, database));
    }

    [Fact]
    public void A_missing_database_is_not_a_problem_it_is_a_first_start()
    {
        Assert.Null(DataFolderWriteProbe.Check(_folder, Path.Combine(_folder, "data.db")));
    }

    [Fact]
    public void No_database_path_means_only_the_folder_is_checked()
    {
        Assert.Null(DataFolderWriteProbe.Check(_folder, null));
    }

    [UnixNonRootFact]
    [UnsupportedOSPlatform("windows")]
    public void A_folder_the_account_cannot_write_gives_one_line_naming_the_folder_the_account_and_the_fix()
    {
        File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        string? problem = DataFolderWriteProbe.Check(_folder, Path.Combine(_folder, "data.db"));

        Assert.NotNull(problem);
        Assert.DoesNotContain('\n', problem);
        Assert.DoesNotContain('\r', problem);
        Assert.Contains(_folder, problem, StringComparison.Ordinal);
        Assert.Contains(Environment.UserName, problem, StringComparison.Ordinal);
        Assert.Contains("chown -R", problem, StringComparison.Ordinal);
        Assert.Contains("docs/troubleshooting.md", problem, StringComparison.Ordinal);
        // A person's line, not an exception's.
        Assert.DoesNotContain("Exception", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", problem, StringComparison.Ordinal);
    }

    [UnixNonRootFact]
    [UnsupportedOSPlatform("windows")]
    public void A_database_file_the_account_cannot_write_in_a_folder_it_can_gives_a_line_naming_the_file()
    {
        string database = Path.Combine(_folder, "data.db");
        File.WriteAllText(database, "x");
        File.SetUnixFileMode(database, UnixFileMode.UserRead);

        string? problem = DataFolderWriteProbe.Check(_folder, database);

        Assert.NotNull(problem);
        Assert.DoesNotContain('\n', problem);
        Assert.Contains(database, problem, StringComparison.Ordinal);
        Assert.Contains("chown -R", problem, StringComparison.Ordinal);
    }

    [UnixNonRootFact]
    [UnsupportedOSPlatform("windows")]
    public void A_side_file_left_by_another_account_is_found_too()
    {
        // SQLite needs its write-ahead log and shared-memory files as much as the main file.
        string wal = Path.Combine(_folder, "data.db-wal");
        File.WriteAllText(wal, "x");
        File.SetUnixFileMode(wal, UnixFileMode.UserRead);

        string? problem = DataFolderWriteProbe.Check(_folder, Path.Combine(_folder, "data.db"));

        Assert.NotNull(problem);
        Assert.Contains("data.db-wal", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_the_folder_preparation_threw_becomes_the_same_kind_of_line()
    {
        string problem = DataFolderWriteProbe.FromException(_folder, new UnauthorizedAccessException("Access to the path is denied."));

        Assert.DoesNotContain('\n', problem);
        Assert.Contains(_folder, problem, StringComparison.Ordinal);
        Assert.Contains(Environment.UserName, problem, StringComparison.Ordinal);
        Assert.DoesNotContain("UnauthorizedAccessException", problem, StringComparison.Ordinal);
    }
}
