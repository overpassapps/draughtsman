using System.Security.AccessControl;

namespace Draughtsman.Server.Auth;

/// <summary>
/// Where the first-run setup code lives when the server runs as a Windows service. A service has no console, and the one
/// place it can log to, the Application event log, is readable by every local user: putting the code there would let any
/// account on the machine race to create the first administrator. So the code is written to <c>setup-code.txt</c> in the
/// data folder instead, readable by Administrators and SYSTEM only (<see cref="WindowsAclSddl"/>, set when the file is
/// created, never widened afterwards), and the log carries only its path. <c>draughtsman first-run-code</c> prints it for an
/// elevated shell. The file goes the moment the first administrator exists (<see cref="SetupCodeGate.Consume"/>, and at
/// start-up when an instance already has users), and a restart replaces it with a fresh code. On Unix the file is created
/// 0600; only a Windows service uses it, since every other host prints the code to its own log.
/// </summary>
public sealed class SetupCodeFile(string dataFolder)
{
    public const string FileName = "setup-code.txt";

    /// <summary>
    /// The DACL of the file: protected (<c>P</c>, nothing inherited from the data folder) and granting full control to SYSTEM and
    /// the built-in Administrators, nobody else. The service account, which creates the file, can still delete it because it
    /// holds full control of the folder it sits in (an entry for it on the file itself would be a way to read it).
    /// </summary>
    public const string WindowsAclSddl = "D:PAI(A;;FA;;;SY)(A;;FA;;;BA)";

    public string Path => System.IO.Path.Combine(dataFolder, FileName);

    /// <summary>Writes <paramref name="code"/>, replacing any earlier file. The restricted permissions are applied at creation, so there is no window in which the file is readable by anyone else.</summary>
    public void Write(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Delete();
        Directory.CreateDirectory(dataFolder);
        using FileStream stream = Create();
        using var writer = new StreamWriter(stream);
        writer.Write(code);
        writer.Write('\n');
    }

    /// <summary>Removes the file; true when one was there. Never throws for a file that is already gone or cannot be removed, since nothing may stop first-run for it.</summary>
    public bool Delete()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return false;
            }

            File.Delete(Path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The code in the file, trimmed; null when there is no file. An unreadable file throws <see cref="UnauthorizedAccessException"/> or <see cref="IOException"/> for the caller to explain.</summary>
    public string? Read()
    {
        return File.Exists(Path) ? File.ReadAllText(Path).Trim() : null;
    }

    private FileStream Create()
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetSecurityDescriptorSddlForm(WindowsAclSddl);
            return new FileInfo(Path).Create(FileMode.CreateNew, FileSystemRights.WriteData | FileSystemRights.AppendData, FileShare.None, 4096, FileOptions.None, security);
        }

        return new FileStream(Path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }
}
