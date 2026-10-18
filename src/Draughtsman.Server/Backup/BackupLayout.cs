using Draughtsman.Server.Ai;
using Draughtsman.Server.Assets;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Email;
using Draughtsman.Server.KeyRing;
using Draughtsman.Storage;

namespace Draughtsman.Server.Backup;

/// <summary>
/// Which parts of the data folder a backup carries, and which it never does. The database comes from
/// <c>VACUUM INTO</c>, not a file copy (a copy taken while the server saves can be torn), so it is listed separately.
/// </summary>
public static class BackupLayout
{
    public const string FormatId = "draughtsman-backup/1";

    public const string ManifestName = "manifest.json";

    public const string ReadMeName = "README.txt";

    /// <summary>Every payload path in the zip starts with this, so a restore maps <c>data/x</c> to <c>&lt;data folder&gt;/x</c>.</summary>
    public const string DataPrefix = "data/";

    public const string DatabaseFile = StorageOptions.SqliteFileName;

    /// <summary>The key ring, the stored AI and email settings it encrypts and the key ring's own backups: what <c>--include-secrets</c> adds.</summary>
    public static readonly string[] SecretFolders = [KeyRingFiles.DirectoryName, AiSettingsStore.DirectoryName, EmailSettingsStore.DirectoryName, KeyRingBackup.FolderName];

    /// <summary>Folders backed up whole: the uploaded images, the admin's branding and the organisation themes.</summary>
    public static readonly string[] Folders = [AssetStore.DirectoryName, "branding", "themes"];

    /// <summary>
    /// Every top-level name a restore may place in the data folder, and so the ones <c>--overwrite</c> sets aside first. The
    /// write-ahead-log and journal siblings are here because a stale one beside a restored <c>data.db</c> is replayed
    /// onto it and silently puts the instance back on the old data (the same trap <see cref="StorageMigrator"/> documents).
    /// </summary>
    public static readonly string[] DatabaseSiblings = [DatabaseFile + "-wal", DatabaseFile + "-shm", DatabaseFile + "-journal"];

    /// <summary>The loose top-level files a backup carries: the settings and a chrome theme file kept in the data folder's root.</summary>
    public static IEnumerable<(string Relative, string Kind)> LooseFiles(string dataFolder, DraughtsmanConfig config)
    {
        yield return (DraughtsmanConfig.FileName, "config");

        // theme.chrome is looked up in <data>/themes first (backed up with the folder), then in the data folder's own root.
        string chrome = config.Theme.Chrome;
        if (!string.IsNullOrWhiteSpace(chrome) && !Path.IsPathRooted(chrome) && IsInside(dataFolder, chrome) && !Normalise(chrome).StartsWith("themes/", StringComparison.Ordinal))
        {
            yield return (Normalise(chrome), "themes");
        }
    }

    public static string Normalise(string relative)
    {
        return relative.Replace('\\', '/').TrimStart('/');
    }

    private static bool IsInside(string dataFolder, string relative)
    {
        string root = Path.GetFullPath(dataFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(Path.Combine(dataFolder, relative)).StartsWith(root, StringComparison.Ordinal);
    }

    /// <summary>A path read out of a bundle: relative, forward slashes, no <c>..</c>, no drive, under <c>data/</c>. Anything else is a bundle to refuse, not to extract.</summary>
    public static bool IsSafePayloadPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith(DataPrefix, StringComparison.Ordinal) || path.Length == DataPrefix.Length)
        {
            return false;
        }

        if (path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal) || path.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        return path[DataPrefix.Length..].Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }
}
