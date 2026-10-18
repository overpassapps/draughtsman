using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Draughtsman.Server.Hardening;

/// <summary>
/// Keeps the data folder's contents private to the account the server runs as (AU-12). It holds every document, the
/// password, session and token hashes (<c>data.db</c>), the key ring and the stored AI key, and the Postgres password in
/// <c>draughtsman.yaml</c>; with the process umask at its usual 022 all of it was world-readable on a shared Linux host
/// (only the key ring and <c>ai/settings.json</c> were 0600). On Unix this sets the process umask to 077 before anything is
/// created, so every file and folder the server makes from then on, SQLite's own database and its <c>-wal</c> and
/// <c>-shm</c> files included, is owner-only from its first byte rather than chmod-ed after a window of exposure, and tightens
/// what earlier runs already left behind. It never makes an existing data folder's own mode stricter, which may be group-shared
/// on purpose, but says so when it is open to others. Windows has no modes: its protection is the folder's ACL (<see cref="WindowsDataFolderAcl"/>).
/// </summary>
public static class DataFolderPermissions
{
    private const uint RestrictiveUmask = 0x3F; // octal 077

    private const UnixFileMode GroupAndOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Folders under the data folder that hold secrets or user content; each is made owner-only, which shuts the door on whatever mode its files have.</summary>
    private static readonly string[] s_privateFolders = ["keys", "ai", "email", "branding", "assets", "themes", "logs"];

    /// <summary>
    /// Creates the data folder (owner-only when new), applies the umask and tightens what already exists. Returns what it found
    /// that the operator should know; with <paramref name="echo"/> (the default) each note also goes to standard error, not the
    /// log, since the stdio MCP host owns standard output and has no logger yet. The web host passes <c>false</c> and logs the
    /// notes once its logger exists, so a Windows service reports them in the Event Log, where nobody reads standard error.
    /// Windows has no modes: a new folder gets a protected ACL (<see cref="WindowsDataFolderAcl.CreateProtected"/>) and an
    /// existing one is only checked, and reported when <c>Users</c> or <c>Everyone</c> can read or change it.
    /// </summary>
    public static IReadOnlyList<string> Prepare(string dataFolder, bool echo = true)
    {
        IReadOnlyList<string> notes;
        if (OperatingSystem.IsWindows())
        {
            notes = PrepareWindows(dataFolder);
        }
        else
        {
            Umask(RestrictiveUmask);
            Directory.CreateDirectory(dataFolder, OwnerOnlyFolder);
            notes = Tighten(dataFolder);
        }

        if (echo)
        {
            foreach (string note in notes)
            {
                Console.Error.WriteLine($"warn: Draughtsman.Server.DataFolder: {note}");
            }
        }

        return notes;
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> PrepareWindows(string dataFolder)
    {
        if (!Directory.Exists(dataFolder))
        {
            WindowsDataFolderAcl.CreateProtected(dataFolder);
            return [];
        }

        try
        {
            return WindowsDataFolderAcl.Describe(dataFolder, WindowsDataFolderAcl.Read(dataFolder), WindowsDataFolderAcl.CurrentAccount());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return [$"The permissions of the data folder {dataFolder} could not be checked ({ex.GetType().Name}); make sure only SYSTEM, the Administrators and the service account can open it."];
        }
    }

    /// <summary>
    /// Brings what is already in <paramref name="dataFolder"/> to owner-only (the database and its side files, the config file,
    /// the private folders and the files in <c>keys/</c> and <c>ai/</c>) and returns a note for each thing it could not fix or
    /// chose to leave. A no-op on Windows.
    /// </summary>
    public static IReadOnlyList<string> Tighten(string dataFolder)
    {
        var notes = new List<string>();
        if (OperatingSystem.IsWindows() || !Directory.Exists(dataFolder))
        {
            return notes;
        }

        UnixFileMode folderMode = File.GetUnixFileMode(dataFolder);
        if ((folderMode & GroupAndOther) != 0)
        {
            string writable = (folderMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0
                ? " Other users can also add, rename and delete files in it (the README used to suggest chmod 777 for Docker; use chown to the container's uid, 10001, instead)."
                : string.Empty;
            notes.Add($"The data folder {dataFolder} is open to other users (mode {Octal(folderMode)}); chmod 700 it unless that is intended.{writable}");
        }

        foreach (string file in Directory.EnumerateFiles(dataFolder))
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith("data.db", StringComparison.Ordinal) || name.Equals("draughtsman.yaml", StringComparison.Ordinal) || name.Equals("draughtsman.yml", StringComparison.Ordinal))
            {
                Restrict(file, OwnerOnlyFile, notes);
            }
        }

        foreach (string folder in s_privateFolders)
        {
            string path = Path.Combine(dataFolder, folder);
            if (!Directory.Exists(path))
            {
                continue;
            }

            Restrict(path, OwnerOnlyFolder, notes);
            if (folder is "keys" or "ai" or "email")
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    Restrict(file, OwnerOnlyFile, notes);
                }
            }
        }

        return notes;
    }

    private static void Restrict(string path, UnixFileMode wanted, List<string> notes)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            UnixFileMode current = File.GetUnixFileMode(path);
            if ((current & GroupAndOther) != 0)
            {
                File.SetUnixFileMode(path, wanted);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            notes.Add($"{path} is readable by other users and could not be restricted ({ex.Message}); chmod {(wanted == OwnerOnlyFolder ? "700" : "600")} it.");
        }
    }

    private static string Octal(UnixFileMode mode)
    {
        return Convert.ToString((int)mode, 8);
    }

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint Umask(uint mask);
}
