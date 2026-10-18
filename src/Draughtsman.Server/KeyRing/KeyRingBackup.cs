using System.Globalization;
using System.Text.Json;
using Draughtsman.Server.Ai;
using Draughtsman.Server.Email;

namespace Draughtsman.Server.KeyRing;

/// <summary>
/// The safety copy taken before a ring is touched: every file in <c>keys/</c>, <c>ai/settings.json</c> and <c>email/settings.json</c>, copied
/// byte for byte under <c>&lt;data&gt;/backups/key-ring-&lt;UTC stamp&gt;/</c> and each copy hashed back against its
/// source before anything is changed. Nothing in the server deletes a backup, ever: it is the way back if the new
/// protection turns out to be wrong, lost or unreadable.
/// <para>
/// The copy is, by design, exactly as exposed as the data folder was before migration: it holds the old plain-text
/// ring. It is owner-only on Unix, and the admin AI page and the startup log keep saying it exists until the operator
/// has confirmed the stored key still works and removed it.
/// </para>
/// </summary>
internal static class KeyRingBackup
{
    public const string FolderName = "backups";

    public const string Prefix = "key-ring-";

    private const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions ManifestOptions = new() { WriteIndented = true };

    /// <summary>
    /// Backs up the ring and the AI settings and returns the backup's path relative to the data folder. A backup of
    /// exactly the same bytes already on disk (an earlier failed attempt) is reused rather than duplicated, so a
    /// server that cannot finish migrating does not write another plain-text copy on every restart.
    /// </summary>
    public static string Create(string dataFolder, DateTimeOffset now)
    {
        Dictionary<string, string> current = CurrentFiles(dataFolder);
        if (FindIdentical(dataFolder, current) is { } existing)
        {
            return existing;
        }

        string parent = Path.Combine(dataFolder, FolderName);
        CreateOwnerOnlyDirectory(parent);

        string stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string name = Prefix + stamp;
        string folder = Path.Combine(parent, name);
        for (int suffix = 2; Directory.Exists(folder); suffix++)
        {
            name = $"{Prefix}{stamp}-{suffix}";
            folder = Path.Combine(parent, name);
        }

        CreateOwnerOnlyDirectory(folder);
        foreach ((string relative, string hash) in current)
        {
            string target = Path.Combine(folder, relative);
            CreateOwnerOnlyDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(dataFolder, relative), target);
            OwnerOnly(target);
            if (KeyRingFiles.Hash(target) != hash)
            {
                throw new IOException($"The backup copy of {relative} does not match the original.");
            }
        }

        WriteOwnerOnly(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(new Manifest(now, current), ManifestOptions));
        WriteOwnerOnly(Path.Combine(folder, "README.txt"), ReadMe);
        return FolderName + "/" + name;
    }

    /// <summary>The newest backup folder, relative to the data folder, or null when there is none.</summary>
    public static string? Latest(string dataFolder)
    {
        string parent = Path.Combine(dataFolder, FolderName);
        if (!Directory.Exists(parent))
        {
            return null;
        }

        string? newest = Directory.EnumerateDirectories(parent, Prefix + "*").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).LastOrDefault();
        return newest is null ? null : FolderName + "/" + newest;
    }

    /// <summary>
    /// Puts the named backed-up ring files back over the current ones, each through a temporary file so a crash never
    /// leaves half a key. Only the files migration changed are named: a damaged file that was moved aside before it
    /// must not be restored into the ring.
    /// </summary>
    public static void RestoreKeys(string dataFolder, string backupRelative, IEnumerable<string> fileNames)
    {
        string source = Path.Combine(dataFolder, backupRelative, KeyRingFiles.DirectoryName);
        string target = KeyRingFiles.KeysFolder(dataFolder);
        Directory.CreateDirectory(target);
        foreach (string name in fileNames)
        {
            string destination = Path.Combine(target, name);
            string temp = destination + ".restoring";
            File.Copy(Path.Combine(source, name), temp, overwrite: true);
            OwnerOnly(temp);
            File.Move(temp, destination, overwrite: true);
        }
    }

    private static Dictionary<string, string> CurrentFiles(string dataFolder)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        string keys = KeyRingFiles.KeysFolder(dataFolder);
        if (Directory.Exists(keys))
        {
            foreach (string path in Directory.EnumerateFiles(keys).Order(StringComparer.Ordinal))
            {
                files[KeyRingFiles.DirectoryName + "/" + Path.GetFileName(path)] = KeyRingFiles.Hash(path);
            }
        }

        foreach (string folder in new[] { AiSettingsStore.DirectoryName, EmailSettingsStore.DirectoryName })
        {
            string settings = Path.Combine(dataFolder, folder, "settings.json");
            if (File.Exists(settings))
            {
                files[folder + "/settings.json"] = KeyRingFiles.Hash(settings);
            }
        }

        return files;
    }

    private static string? FindIdentical(string dataFolder, Dictionary<string, string> current)
    {
        string parent = Path.Combine(dataFolder, FolderName);
        if (!Directory.Exists(parent))
        {
            return null;
        }

        foreach (string folder in Directory.EnumerateDirectories(parent, Prefix + "*").Order(StringComparer.Ordinal))
        {
            try
            {
                Manifest? manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(folder, ManifestName)));
                if (manifest?.Files is null || manifest.Files.Count != current.Count || manifest.Files.Any(pair => !current.TryGetValue(pair.Key, out string? hash) || hash != pair.Value))
                {
                    continue;
                }

                // Trust the files, not just the manifest: each must still be there and unchanged.
                if (manifest.Files.All(pair => File.Exists(Path.Combine(folder, pair.Key)) && KeyRingFiles.Hash(Path.Combine(folder, pair.Key)) == pair.Value))
                {
                    return FolderName + "/" + Path.GetFileName(folder);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // An unreadable backup is simply not reusable; it is left exactly where it is.
            }
        }

        return null;
    }

    internal static void CreateOwnerOnlyDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal static void WriteOwnerOnly(string path, string text)
    {
        File.WriteAllText(path, text);
        OwnerOnly(path);
    }

    internal static void OwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private sealed record Manifest(DateTimeOffset CreatedAt, Dictionary<string, string> Files);

    private const string ReadMe =
        "Draughtsman key ring backup\n" +
        "\n" +
        "Taken automatically before the Data Protection key ring was protected at rest. keys/ is the ring exactly as it\n" +
        "was (plain text), and ai/settings.json is the stored AI settings. manifest.json lists each file's SHA-256.\n" +
        "\n" +
        "To go back: stop the server, copy keys/* over <data>/keys/ and set keyRing.protection to none.\n" +
        "\n" +
        "This folder is as sensitive as the data folder was before protection: it holds the plain-text key ring beside the\n" +
        "encrypted AI key. Once you have checked on the admin AI page that the stored key still works (Test connection),\n" +
        "delete this folder. Draughtsman never deletes it for you.\n";
}
