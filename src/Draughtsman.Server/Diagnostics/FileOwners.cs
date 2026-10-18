using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Draughtsman.Server.Diagnostics;

/// <summary>Who owns a file or folder, as far as this platform can say: the account name where it can be resolved, and the numeric user id on Unix.</summary>
public sealed record FileOwner(string Name, long? Uid, long? Gid);

/// <summary>
/// File owners and the account this process runs as, for <c>draughtsman doctor</c> and the support bundle's data-folder listing.
/// .NET reads a file's permission bits but not its owner, and "the database is owned by root and the server runs as uid 10001" is
/// exactly the install fault this has to name, so on Unix the owner comes from the C library's <c>stat</c>: its <c>st_uid</c> and
/// <c>st_gid</c> sit at fixed offsets per platform (listed below), read out of a buffer, never a managed struct, so an unknown
/// platform degrades to "owner unknown" instead of reading the wrong bytes. On Windows it is the file's owner SID resolved by name.
/// A failure of any kind returns null; nothing here can stop a diagnostic.
/// </summary>
public static class FileOwners
{
    private const int StatBufferBytes = 512;

    /// <summary>The user id this process runs as on Unix (the effective one); null on Windows or if it cannot be read.</summary>
    public static long? CurrentUserId()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return GetEffectiveUserId();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The name of the account this process runs as (<c>draughtsman</c>, <c>root</c>, <c>DOMAIN\user</c>).</summary>
    public static string CurrentUserName()
    {
        return Environment.UserName;
    }

    /// <summary>Who owns the file or folder at <paramref name="path"/>, or null where that cannot be read.</summary>
    public static FileOwner? Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return OperatingSystem.IsWindows() ? WindowsOwner(path) : UnixOwner(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The permission bits as <c>rwxr-x---</c>, or <c>-</c> where the platform has no such thing (Windows).</summary>
    public static string Mode(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsWindows())
        {
            return "-";
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            return FormatMode(mode);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return "?";
        }
    }

    /// <summary>The octal form (<c>0750</c>) of the same bits.</summary>
    public static string Octal(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (OperatingSystem.IsWindows())
        {
            return "-";
        }

        try
        {
            return "0" + Convert.ToString((int)File.GetUnixFileMode(path), 8).PadLeft(3, '0');
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return "?";
        }
    }

    public static string FormatMode(UnixFileMode mode)
    {
        var text = new char[9];
        UnixFileMode[] bits =
        [
            UnixFileMode.UserRead, UnixFileMode.UserWrite, UnixFileMode.UserExecute,
            UnixFileMode.GroupRead, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute,
            UnixFileMode.OtherRead, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute,
        ];
        for (int i = 0; i < 9; i++)
        {
            text[i] = (mode & bits[i]) != 0 ? "rwx"[i % 3] : '-';
        }

        return new string(text);
    }

    /// <summary>"name (uid 10001)" for a Unix owner, the account name on Windows, "unknown" where it cannot be read.</summary>
    public static string Describe(FileOwner? owner)
    {
        if (owner is null)
        {
            return "unknown";
        }

        return owner.Uid is { } uid ? $"{owner.Name} (uid {uid.ToString(CultureInfo.InvariantCulture)})" : owner.Name;
    }

    // ------------------------------------------------------------------------------------------------------ unix

    private static FileOwner? UnixOwner(string path)
    {
        if (!TryOffsets(out int uidOffset, out int gidOffset))
        {
            return null;
        }

        byte[] buffer = new byte[StatBufferBytes];
        int result = StatNative(path, buffer);
        if (result != 0)
        {
            return null;
        }

        long uid = BitConverter.ToUInt32(buffer, uidOffset);
        long gid = BitConverter.ToUInt32(buffer, gidOffset);
        return new FileOwner(UserNameOf(uid) ?? uid.ToString(CultureInfo.InvariantCulture), uid, gid);
    }

    /// <summary>
    /// Where <c>st_uid</c> and <c>st_gid</c> are in the C library's <c>struct stat</c> on the platforms Draughtsman ships for:
    /// 64-bit Linux on x86-64 (<c>st_dev</c> 8, <c>st_ino</c> 8, <c>st_nlink</c> 8, <c>st_mode</c> 4, then uid and gid), 64-bit Linux on
    /// anything with the generic layout such as arm64 (dev 8, ino 8, mode 4, nlink 4, then uid and gid), and macOS with 64-bit inode
    /// numbers (dev 4, mode 2, nlink 2, ino 8, then uid and gid). Anything else is not guessed.
    /// </summary>
    internal static bool TryOffsets(out int uid, out int gid)
    {
        uid = gid = 0;
        if (IntPtr.Size != 8)
        {
            return false;
        }

        if (OperatingSystem.IsMacOS())
        {
            (uid, gid) = (16, 20);
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            (uid, gid) = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? (28, 32) : (24, 28);
            return RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;
        }

        return false;
    }

    private static int StatNative(string path, byte[] buffer)
    {
        // The original x86-64 macOS symbol returns the old, smaller struct; its 64-bit-inode twin is the one with the layout above.
        return OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? StatInode64(path, buffer)
            : Stat(path, buffer);
    }

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int Stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int StatInode64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    /// <summary>The account name for a user id from <c>/etc/passwd</c> (macOS keeps most accounts elsewhere, so a miss there is normal); null when it is not listed.</summary>
    internal static string? UserNameOf(long uid)
    {
        try
        {
            const string passwd = "/etc/passwd";
            if (!File.Exists(passwd))
            {
                return null;
            }

            string wanted = uid.ToString(CultureInfo.InvariantCulture);
            foreach (string line in File.ReadLines(passwd))
            {
                string[] fields = line.Split(':');
                if (fields.Length > 2 && fields[2] == wanted && !line.StartsWith('#'))
                {
                    return fields[0];
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Falls back to the number.
        }

        return null;
    }

    // ------------------------------------------------------------------------------------------------------ windows

    [SupportedOSPlatform("windows")]
    private static FileOwner? WindowsOwner(string path)
    {
        System.Security.AccessControl.FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        System.Security.Principal.IdentityReference? owner = security.GetOwner(typeof(System.Security.Principal.NTAccount)) ?? security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
        return owner is null ? null : new FileOwner(owner.Value, null, null);
    }
}
