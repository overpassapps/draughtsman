using System.Globalization;
using System.Runtime.InteropServices;

namespace Draughtsman.Server.Diagnostics;

/// <summary>What <see cref="MachineProbe.Run"/> found out about the data folder, the database file and the disk under them.</summary>
public sealed record MachineProbeResult(
    string DataFolder,
    bool FolderExists,
    string? FolderWriteError,
    string Provider,
    string? DatabaseFile,
    bool DatabaseFileExists,
    string? DatabaseWriteError,
    long? FreeBytes,
    long? TotalBytes,
    FileOwner? FolderOwner,
    FileOwner? DatabaseOwner);

/// <summary>
/// The only part of the self-check, the doctor and the support bundle that touches the machine for the data folder and disk. It writes
/// nothing that stays: one empty probe file, created and deleted in the data folder, and for SQLite the database file opened for
/// writing and closed without a byte written (so it creates no <c>-wal</c> or <c>-shm</c> file and cannot change who owns anything).
/// </summary>
public static class MachineProbe
{
    public static MachineProbeResult Run(string dataFolder, string provider, string? databaseFile)
    {
        ArgumentNullException.ThrowIfNull(dataFolder);
        bool exists = Directory.Exists(dataFolder);
        string? folderError = exists ? WriteProbe(dataFolder) : "the folder does not exist";

        bool dbExists = databaseFile is not null && File.Exists(databaseFile);
        string? dbError = null;
        if (databaseFile is not null && dbExists)
        {
            dbError = OpenForWrite(databaseFile);
            if (dbError is null && Path.GetDirectoryName(Path.GetFullPath(databaseFile)) is { } folder && !string.Equals(Path.GetFullPath(folder), Path.GetFullPath(dataFolder), StringComparison.Ordinal))
            {
                // A database outside the data folder needs its own folder to be writable for the journal.
                dbError = WriteProbe(folder) is { } folderProblem ? $"its folder {folder} is not writable ({folderProblem})" : null;
            }
        }

        (long? free, long? total) = Disk(dataFolder);
        return new MachineProbeResult(
            dataFolder,
            exists,
            folderError,
            provider,
            databaseFile,
            dbExists,
            dbError,
            free,
            total,
            exists ? FileOwners.Of(dataFolder) : null,
            dbExists ? FileOwners.Of(databaseFile!) : null);
    }

    private static string? WriteProbe(string folder)
    {
        string probe = Path.Combine(folder, $".draughtsman-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.GetType().Name + ": " + error.Message;
        }
    }

    private static string? OpenForWrite(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.GetType().Name + ": " + error.Message;
        }
    }

    /// <summary>The free and total bytes of the volume the folder is on; nulls when the platform will not say.</summary>
    public static (long? Free, long? Total) Disk(string folder)
    {
        try
        {
            string probe = Directory.Exists(folder) ? folder : Path.GetDirectoryName(Path.GetFullPath(folder)) ?? folder;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(probe)) ?? probe);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }
}

/// <summary>The verdicts on a <see cref="MachineProbeResult"/>: can the server write where it must, and is there room.</summary>
public static class MachineChecks
{
    public const long FailBelowBytes = 100L * 1024 * 1024;

    public const long WarnBelowBytes = 1024L * 1024 * 1024;

    /// <summary>Below this much free, a volume that is also under <see cref="WarnBelowFraction"/> free is a warning: a small volume that is nearly full. A large one with tens of gibibytes spare is not.</summary>
    public const long SmallVolumeWarnBelowBytes = 5L * 1024 * 1024 * 1024;

    public const double WarnBelowFraction = 0.10;

    /// <summary>
    /// The findings on the data folder, the database and the disk. <paramref name="whose"/> names the account the probe ran as: inside the server (the Connection
    /// check) that is the server's own; for <c>doctor</c> it is whoever ran the command, which may not be the server's account (and, as root, can write anything).
    /// </summary>
    public static IReadOnlyList<DiagnosticItem> Evaluate(MachineProbeResult probe, string whose = "the server's account")
    {
        ArgumentNullException.ThrowIfNull(probe);
        return [DataFolder(probe, whose), Disk(probe)];
    }

    private static DiagnosticItem DataFolder(MachineProbeResult p, string whose)
    {
        const string id = "data";
        const string title = "Data folder and database";
        string account = FileOwners.CurrentUserName() + (FileOwners.CurrentUserId() is { } uid ? " (uid " + uid.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty);
        var facts = new Dictionary<string, string>
        {
            ["Data folder"] = p.DataFolder,
            ["Server account (this process)"] = account,
            ["Folder owner"] = FileOwners.Describe(p.FolderOwner),
            ["Storage provider"] = p.Provider,
        };
        if (p.DatabaseFile is not null)
        {
            facts["Database file"] = p.DatabaseFile;
            facts["Database owner"] = FileOwners.Describe(p.DatabaseOwner);
        }

        if (!p.FolderExists || p.FolderWriteError is not null)
        {
            return DiagnosticItem.Fail(
                id,
                title,
                $"{Capital(whose)} cannot write to the data folder {p.DataFolder} ({p.FolderWriteError}). The server needs to: saving, signing in and migrating all write there.",
                facts,
                detail: [OwnerHint(p.DataFolder, p.FolderOwner)],
                fix: new DiagnosticFix(Shell: "# Give the account the server runs as ownership of the folder, for example:\n# chown -R <server account> " + p.DataFolder));
        }

        if (p.DatabaseWriteError is not null)
        {
            return DiagnosticItem.Fail(
                id,
                title,
                $"The database ({p.DatabaseFile ?? p.Provider}) cannot be written ({p.DatabaseWriteError}); that was tried as {whose}. If that is the server's account, the server cannot save anything: /api/health reports degraded and a correct password is answered with an error instead of a session. This usually follows a restore or copy done as another account (root).",
                facts,
                detail: p.DatabaseFile is null ? null : [OwnerHint(p.DatabaseFile, p.DatabaseOwner)],
                fix: p.DatabaseFile is null ? null : new DiagnosticFix(Shell: "# As root, hand the database and its folder back to the server's account:\n# chown -R <server account> " + p.DataFolder));
        }

        return DiagnosticItem.Pass(
            id,
            title,
            p.DatabaseFile is null
                ? $"The data folder {p.DataFolder} is writable by {whose}. Storage is {p.Provider}: the database is not a file here, so only the folder was checked."
                : p.DatabaseFileExists
                    ? $"The data folder and the database file are writable by {whose} ({account}).{RootNote()}"
                    : $"The data folder is writable by {whose} ({account}). The database file does not exist yet; the first start creates it.{RootNote()}",
            facts);
    }

    private static DiagnosticItem Disk(MachineProbeResult p)
    {
        const string id = "disk";
        const string title = "Free disk space";
        if (p.FreeBytes is not { } free || p.TotalBytes is not { } total || total <= 0)
        {
            return DiagnosticItem.Warn(id, title, "The free space under the data folder could not be read, so it was not checked.");
        }

        var facts = new Dictionary<string, string>
        {
            ["Free"] = Size(free),
            ["Volume size"] = Size(total),
        };
        if (free < FailBelowBytes)
        {
            return DiagnosticItem.Fail(id, title, $"Only {Size(free)} is free under the data folder. A database cannot save, and an upgrade cannot take its safety copy, when the disk is full.", facts, ["Free space, move the data folder to a bigger volume, or remove old backups and pre-migration copies (data.db.pre-migration-*)."]);
        }

        if (free < WarnBelowBytes || (free < SmallVolumeWarnBelowBytes && (double)free / total < WarnBelowFraction))
        {
            return DiagnosticItem.Warn(id, title, $"{Size(free)} is free under the data folder (of {Size(total)}). That is getting low: an upgrade copies the database first and needs room for it.", facts);
        }

        return DiagnosticItem.Pass(id, title, $"{Size(free)} free of {Size(total)}.", facts);
    }

    private static string Capital(string text)
    {
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Root can write anywhere, so a pass as root says nothing about the account the server runs as.</summary>
    private static string RootNote()
    {
        return FileOwners.CurrentUserId() == 0 ? " That is root, which can write anywhere: it does not show the server's account can; see the ownership finding." : string.Empty;
    }

    public static string Size(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes.ToString(CultureInfo.InvariantCulture)} B" : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string OwnerHint(string path, FileOwner? owner)
    {
        long? me = FileOwners.CurrentUserId();
        if (owner?.Uid is { } owned && me is { } mine && owned != mine)
        {
            return $"{path} is owned by uid {owned.ToString(CultureInfo.InvariantCulture)} but this process runs as uid {mine.ToString(CultureInfo.InvariantCulture)}.";
        }

        return OperatingSystem.IsWindows()
            ? "Check the folder's permissions (Properties > Security) for the account the service runs as."
            : $"Check the owner and mode of {path} (ls -ld).";
    }
}

/// <summary>What this server is: version, build, platform, whether it runs in a container, the account it runs as. Always a pass: it is the first thing a support request needs.</summary>
public static class BuildChecks
{
    public static DiagnosticItem Describe(string version, DateOnly buildDate, string host)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(host);
        long? uid = FileOwners.CurrentUserId();
        var facts = new Dictionary<string, string>
        {
            ["Version"] = version,
            ["Build date"] = buildDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["Platform"] = RuntimeInformation.OSDescription + " " + RuntimeInformation.OSArchitecture,
            [".NET runtime"] = RuntimeInformation.FrameworkDescription,
            ["Runs as"] = FileOwners.CurrentUserName() + (uid is { } id ? " (uid " + id.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty),
            ["Host"] = host == "container" ? "container" : host,
            ["Container"] = host == "container" ? "yes" : "no",
        };
        return DiagnosticItem.Pass("build", "This server", $"Draughtsman {version}, built {buildDate:yyyy-MM-dd}, on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}){(host == "container" ? ", in a container" : string.Empty)}.", facts);
    }
}
