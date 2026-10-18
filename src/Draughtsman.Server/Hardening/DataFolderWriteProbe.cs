using System.Runtime.InteropServices;
using Draughtsman.Server.Cli;

namespace Draughtsman.Server.Hardening;

/// <summary>
/// Finds out, before anything else touches the data folder, that the account the server runs as cannot write to it, and says so in one plain line.
/// Without this an unwritable folder (a Docker bind mount owned by root, a database restored by another account) ended the process with an unhandled
/// exception and a stack trace, or worse, started it: the database opened and read, <c>/api/health</c> said ok, and only a correct password failed.
/// The probe creates and deletes a file in the folder and opens the database file, and the two files SQLite keeps beside it, for writing.
/// </summary>
public static class DataFolderWriteProbe
{
    /// <summary>The process exit code for a data folder or database file the account cannot write; the same as a database that could not be opened (<c>StorageStartup</c>), and nothing was changed.</summary>
    public const int ExitCode = 5;

    private static readonly string[] s_sqliteSideFiles = ["-wal", "-shm"];

    /// <summary>
    /// Null when the folder, and the database file if it exists, can be written; otherwise the one line to show. <paramref name="databasePath"/> is
    /// null for a provider with no file (Postgres) and then only the folder is checked.
    /// </summary>
    public static string? Check(string dataFolder, string? databasePath)
    {
        ArgumentNullException.ThrowIfNull(dataFolder);
        string probe = Path.Combine(dataFolder, ".write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Describe(dataFolder, "the data folder " + dataFolder);
        }

        if (databasePath is null)
        {
            return null;
        }

        foreach (string path in new[] { databasePath }.Concat(s_sqliteSideFiles.Select(suffix => databasePath + suffix)))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return Describe(dataFolder, path + " in the data folder " + dataFolder);
            }
        }

        return null;
    }

    /// <summary>The same line for a failure the folder preparation itself threw (it could not create or list the folder).</summary>
    public static string FromException(string dataFolder, Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        return Describe(dataFolder, "the data folder " + dataFolder);
    }

    /// <summary>Writes the line to standard error (and, for a Windows service that has no console, the Application event log) and sets the exit code; the caller then returns without serving.</summary>
    public static async Task Refuse(string line)
    {
        await Console.Error.WriteLineAsync(line);
        if (OperatingSystem.IsWindows())
        {
            WindowsEventSource.TryWriteError(line);
        }

        Environment.ExitCode = ExitCode;
    }

    /// <summary>The account the server runs as, as a person would name it: <c>draughtsman (uid 10001)</c> on Unix.</summary>
    public static string CurrentAccount()
    {
        return OperatingSystem.IsWindows() ? $"{Environment.UserDomainName}\\{Environment.UserName}" : $"{Environment.UserName} (uid {Uid()})";
    }

    /// <summary>The one line: what cannot be written, who tried, and the usual fix.</summary>
    public static string Describe(string dataFolder, string what)
    {
        string fix = OperatingSystem.IsWindows()
            ? $"Give that account Modify rights to the folder (icacls \"{dataFolder}\" /grant \"{Environment.UserName}:(OI)(CI)M\") and start again."
            : $"Give that account the folder and everything in it and start again, for example: chown -R {Uid()} {dataFolder} (a Docker bind mount must be owned by the container's own uid, 10001 in the published image).";
        return $"Draughtsman cannot start: it cannot write to {what}, running as {CurrentAccount()}. {fix} Nothing was changed. See docs/troubleshooting.md.";
    }

    private static uint Uid()
    {
        return OperatingSystem.IsWindows() ? 0 : GetUid();
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
}
