using System.Globalization;
using System.Text.Json;

namespace Draughtsman.Server.KeyRing;

/// <summary>
/// The copy taken before a stored secret is re-protected under <see cref="DataProtectionIdentity.ApplicationName"/>: the settings file
/// as it was, byte for byte, under <c>&lt;data&gt;/backups/secrets-&lt;UTC stamp&gt;/</c> (so <c>--include-secrets</c> carries it with the
/// ring), each copy hashed back against its source before anything is changed. It holds the OLD ciphertext, which only the old
/// install path (and the ring) can open: it is the way back to an earlier build, and the proof nothing was thrown away. Nothing in the
/// server deletes it.
/// </summary>
internal static class SecretsBackup
{
    public const string Prefix = "secrets-";

    private const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions ManifestOptions = new() { WriteIndented = true };

    /// <summary>Copies <paramref name="relativeFiles"/> (paths under the data folder) and returns the backup folder relative to the data folder. Reuses a backup that already holds exactly these bytes.</summary>
    public static string Create(string dataFolder, DateTimeOffset now, IReadOnlyList<string> relativeFiles)
    {
        Dictionary<string, string> current = relativeFiles.ToDictionary(file => file, file => KeyRingFiles.Hash(Path.Combine(dataFolder, file)), StringComparer.Ordinal);
        if (FindIdentical(dataFolder, current) is { } existing)
        {
            return existing;
        }

        string parent = Path.Combine(dataFolder, KeyRingBackup.FolderName);
        KeyRingBackup.CreateOwnerOnlyDirectory(parent);
        string stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string name = Prefix + stamp;
        string folder = Path.Combine(parent, name);
        for (int suffix = 2; Directory.Exists(folder); suffix++)
        {
            name = $"{Prefix}{stamp}-{suffix}";
            folder = Path.Combine(parent, name);
        }

        KeyRingBackup.CreateOwnerOnlyDirectory(folder);
        foreach ((string relative, string hash) in current)
        {
            string target = Path.Combine(folder, relative);
            KeyRingBackup.CreateOwnerOnlyDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(dataFolder, relative), target);
            KeyRingBackup.OwnerOnly(target);
            if (KeyRingFiles.Hash(target) != hash)
            {
                throw new IOException($"The backup copy of {relative} does not match the original.");
            }
        }

        KeyRingBackup.WriteOwnerOnly(Path.Combine(folder, ManifestName), JsonSerializer.Serialize(new Manifest(now, current), ManifestOptions));
        KeyRingBackup.WriteOwnerOnly(Path.Combine(folder, "README.txt"), ReadMe);
        return KeyRingBackup.FolderName + "/" + name;
    }

    /// <summary>Puts one backed-up file back over the current one through a temporary file, so a crash never leaves half of it.</summary>
    public static void Restore(string dataFolder, string backupRelative, string relativeFile)
    {
        string destination = Path.Combine(dataFolder, relativeFile);
        string temp = destination + ".restoring";
        File.Copy(Path.Combine(dataFolder, backupRelative, relativeFile), temp, overwrite: true);
        KeyRingBackup.OwnerOnly(temp);
        File.Move(temp, destination, overwrite: true);
    }

    private static string? FindIdentical(string dataFolder, Dictionary<string, string> current)
    {
        string parent = Path.Combine(dataFolder, KeyRingBackup.FolderName);
        if (!Directory.Exists(parent))
        {
            return null;
        }

        foreach (string folder in Directory.EnumerateDirectories(parent, Prefix + "*").Order(StringComparer.Ordinal))
        {
            try
            {
                if (current.All(pair => File.Exists(Path.Combine(folder, pair.Key)) && KeyRingFiles.Hash(Path.Combine(folder, pair.Key)) == pair.Value))
                {
                    return KeyRingBackup.FolderName + "/" + Path.GetFileName(folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable backup is simply not reusable; it is left exactly where it is.
            }
        }

        return null;
    }

    private sealed record Manifest(DateTimeOffset CreatedAt, Dictionary<string, string> Files);

    private const string ReadMe =
        "Draughtsman stored-secrets backup\n" +
        "\n" +
        "Taken automatically before the stored AI API key and/or the saved SMTP password was re-protected. Earlier versions tied them to the\n" +
        "folder the program ran from, so moving the program made them unreadable; this version ties them to the key ring alone. These are the\n" +
        "settings files exactly as they were (still encrypted: only the key ring in <data>/keys, with the old install path, opens the secret).\n" +
        "manifest.json lists each file's SHA-256.\n" +
        "\n" +
        "To go back to the earlier version: stop the server, copy these files over <data>/ai/settings.json and <data>/email/settings.json,\n" +
        "and start the earlier version from the folder it ran from before. Otherwise there is nothing to do; delete this folder when you\n" +
        "no longer need it. Draughtsman never deletes it for you.\n";
}
