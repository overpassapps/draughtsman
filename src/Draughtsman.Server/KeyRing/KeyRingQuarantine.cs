using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.KeyRing;

/// <summary>
/// Moves a ring file the framework cannot parse out of its way. The framework's file-system repository reads every
/// <c>*.xml</c> in <c>keys/</c> and lets a malformed one fail the whole ring, so a single truncated key (a full disk,
/// a bad copy) stops Data Protection from working at all: no new key can be made, nothing can be decrypted, and every
/// request that needs an antiforgery token fails. The file is renamed to <c>&lt;name&gt;.damaged</c> (so it is no
/// longer <c>*.xml</c>) after the ring has been backed up, never deleted and never edited, because it may still hold a
/// plain-text key that someone should look at.
/// </summary>
internal static class KeyRingQuarantine
{
    public const string Suffix = ".damaged";

    /// <summary>The ring files that are not well-formed XML and so need moving aside.</summary>
    public static IReadOnlyList<KeyFileInfo> Damaged(string dataFolder)
    {
        return KeyRingFiles.Inspect(KeyRingFiles.KeysFolder(dataFolder)).Where(file => file.State == KeyFileState.Corrupt).ToList();
    }

    /// <summary>Moves each <paramref name="damaged"/> file aside. The caller has already taken <paramref name="backup"/> of the ring, which still holds every one of them under its original name.</summary>
    public static void Run(IReadOnlyList<KeyFileInfo> damaged, string backup, ILogger logger)
    {
        foreach (KeyFileInfo file in damaged)
        {
            string target = file.Path + Suffix;
            for (int n = 2; File.Exists(target); n++)
            {
                target = $"{file.Path}{Suffix}{n}";
            }

            File.Move(file.Path, target);
            logger.LogWarning("Key ring: {File} is not valid XML, which stops the whole ring loading; it was moved aside as {Target} (a copy is in {Backup}).", Path.GetFileName(file.Path), Path.GetFileName(target), backup);
        }
    }

    /// <summary>How many quarantined files are in the ring folder now, from this start or an earlier one.</summary>
    public static int Count(string dataFolder)
    {
        string keys = KeyRingFiles.KeysFolder(dataFolder);
        return Directory.Exists(keys) ? Directory.EnumerateFiles(keys, "*" + Suffix + "*").Count() : 0;
    }
}
