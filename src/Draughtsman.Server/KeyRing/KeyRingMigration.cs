using System.Globalization;
using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Draughtsman.Server.KeyRing;

internal enum KeyRingMigrationOutcome
{
    /// <summary>No plain-text key in the ring: a fresh install, or one already protected.</summary>
    NotNeeded,

    Migrated,

    /// <summary>Stopped before any ring file was changed; the ring is exactly as it was and the backup is kept.</summary>
    Failed,

    /// <summary>A ring file had been changed and the result did not verify, so the backed-up originals were put back.</summary>
    RolledBack,
}

/// <param name="Outcome">What happened.</param>
/// <param name="BackupFolder">The backup taken for this attempt, relative to the data folder, or null if none was needed or none could be made.</param>
/// <param name="KeysProtected">How many key files were rewritten.</param>
/// <param name="Problem">Why it did not complete. Plain English and free of secrets.</param>
internal sealed record KeyRingMigrationResult(KeyRingMigrationOutcome Outcome, string? BackupFolder, int KeysProtected, string? Problem)
{
    public static readonly KeyRingMigrationResult NotNeeded = new(KeyRingMigrationOutcome.NotNeeded, null, 0, null);
}

/// <summary>
/// Protects an existing plain-text key ring in place. The keys themselves do not change, only how they are stored, so
/// every payload already protected by them (the stored AI key above all) stays readable without being decrypted and
/// re-encrypted by this code.
/// <para>The order is the safety argument:</para>
/// <list type="number">
/// <item>Copy <c>keys/</c> and <c>ai/settings.json</c> to a timestamped backup and verify the copy (<see cref="KeyRingBackup"/>).</item>
/// <item>Read the ring as it is now: which keys load, and whether the stored AI key decrypts (<see cref="KeyRingSnapshot"/>).</item>
/// <item>Write encrypted versions of the plain-text key files into a staging folder beside <c>keys/</c>; touch nothing in <c>keys/</c>.</item>
/// <item>Read the staging folder through the real framework with the new protection and compare: every key that loaded before loads now, the AI key decrypts to the same text, and no secret value appears in any staged file.</item>
/// <item>Only then move the encrypted files over the plain ones, one atomic rename each, and compare again on the real folder. If that fails, put the backed-up originals back.</item>
/// </list>
/// A failure at any step leaves the server starting with whatever ring it has (still readable, at worst still plain
/// text) and the backup in place; nothing here deletes a ring file or a backup.
/// </summary>
internal sealed class KeyRingMigration(string dataFolder, string contentRoot, KeyRingProtection protection, ILogger logger, TimeProvider? clock = null, IReadOnlyList<LegacyCandidate>? legacy = null)
{
    private readonly IReadOnlyList<LegacyCandidate> _legacy = legacy ?? LegacyDiscriminators.ForContentRoot(contentRoot);

    private const string StagingPrefix = "keys.migrating-";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <param name="existingBackup">A backup of the ring as it is now, already taken by the caller (relative to the data folder); when null, one is taken here.</param>
    public KeyRingMigrationResult Run(string? existingBackup = null)
    {
        string keys = KeyRingFiles.KeysFolder(dataFolder);
        IReadOnlyList<KeyFileInfo> files = KeyRingFiles.Inspect(keys);
        List<KeyFileInfo> plaintext = files.Where(file => file.State == KeyFileState.Plaintext).ToList();
        if (plaintext.Count == 0)
        {
            return KeyRingMigrationResult.NotNeeded;
        }

        if (!protection.IsActive)
        {
            throw new InvalidOperationException("Migration needs an active key ring protection.");
        }

        string? backup = existingBackup;
        string? staging = null;
        bool committing = false;
        try
        {
            DateTimeOffset now = _clock.GetUtcNow();
            if (backup is null)
            {
                backup = KeyRingBackup.Create(dataFolder, now);
                logger.LogInformation("Key ring: backed up the existing ring and AI settings to {Backup} before protecting it.", backup);
            }

            KeyRingSnapshot before = KeyRingSnapshot.Read(dataFolder, keys, _legacy, protection);
            List<string> secrets = plaintext.SelectMany(file => SecretValues(XDocument.Load(file.Path))).ToList();

            RemoveStaleStaging();
            staging = Path.Combine(dataFolder, StagingPrefix + now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(staging);
            IXmlEncryptor encryptor = EncryptorOf(protection);
            foreach (KeyFileInfo file in files)
            {
                string target = Path.Combine(staging, Path.GetFileName(file.Path));
                if (file.State == KeyFileState.Plaintext)
                {
                    WriteOwnerOnly(target, EncryptSecrets(XDocument.Load(file.Path), encryptor));
                }
                else
                {
                    File.Copy(file.Path, target);
                }
            }

            string? problem = Compare(before, KeyRingSnapshot.Read(dataFolder, staging, _legacy, protection)) ?? LeftoverSecret(staging, secrets);
            if (problem is not null)
            {
                logger.LogWarning("Key ring: protecting the ring was not done, because the encrypted copy did not verify ({Problem}). The ring is unchanged; the backup is kept at {Backup}.", problem, backup);
                return new KeyRingMigrationResult(KeyRingMigrationOutcome.Failed, backup, 0, problem);
            }

            committing = true;
            foreach (KeyFileInfo file in plaintext)
            {
                File.Move(Path.Combine(staging, Path.GetFileName(file.Path)), file.Path, overwrite: true);
            }

            problem = Compare(before, KeyRingSnapshot.Read(dataFolder, keys, _legacy, protection)) ?? LeftoverSecret(keys, secrets);
            if (problem is not null)
            {
                KeyRingBackup.RestoreKeys(dataFolder, backup, plaintext.Select(file => Path.GetFileName(file.Path)));
                logger.LogError("Key ring: the protected ring did not verify ({Problem}); the original ring was put back from {Backup}.", problem, backup);
                return new KeyRingMigrationResult(KeyRingMigrationOutcome.RolledBack, backup, 0, problem);
            }

            logger.LogInformation("Key ring: {Count} key file(s) are now protected by {Protection}; the stored AI key was checked and still decrypts. The old plain-text ring remains in {Backup} until you delete it.", plaintext.Count, protection.Description, backup);
            return new KeyRingMigrationResult(KeyRingMigrationOutcome.Migrated, backup, plaintext.Count, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Type only: nothing here should carry a secret, but an exception's text is not vetted the way ours is.
            string problem = $"{ex.GetType().Name} while protecting the key ring.";
            if (committing && backup is not null)
            {
                try
                {
                    KeyRingBackup.RestoreKeys(dataFolder, backup, plaintext.Select(file => Path.GetFileName(file.Path)));
                    logger.LogError("Key ring: {Problem} The original ring was put back from {Backup}.", problem, backup);
                    return new KeyRingMigrationResult(KeyRingMigrationOutcome.RolledBack, backup, 0, problem);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    problem += $" Restoring it also failed ({restore.GetType().Name}); copy {backup}/keys back over keys/ by hand.";
                }
            }

            logger.LogWarning("Key ring: {Problem} The ring was not protected. The server starts with it as it is{Backup}.", problem, backup is null ? string.Empty : "; the backup is at " + backup);
            return new KeyRingMigrationResult(KeyRingMigrationOutcome.Failed, backup, 0, problem);
        }
        finally
        {
            DeleteQuietly(staging);
        }
    }

    /// <summary>
    /// The configured encryptor, as the framework will use it for new keys, read from the real options so the plain
    /// keys are wrapped by the very object that wraps every later one.
    /// </summary>
    internal static IXmlEncryptor EncryptorOf(KeyRingProtection protection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        protection.Apply(services.AddDataProtection());
        using ServiceProvider provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor
            ?? throw new InvalidOperationException("The key ring protection registered no encryptor.");
    }

    /// <summary>
    /// What <c>XmlKeyManager</c> does when it writes a new key: every element marked <c>requiresEncryption</c>
    /// is encrypted and replaced by an <c>encryptedSecret</c> that names the decryptor to use on the way back.
    /// </summary>
    internal static XDocument EncryptSecrets(XDocument original, IXmlEncryptor encryptor)
    {
        var document = new XDocument(original);
        foreach (XElement secret in document.Descendants().Where(KeyRingFiles.RequiresEncryptionMarker).ToList())
        {
            EncryptedXmlInfo encrypted = encryptor.Encrypt(secret);
            secret.ReplaceWith(new XElement(
                KeyRingFiles.EncryptedSecret,
                new XAttribute("decryptorType", encrypted.DecryptorType.AssemblyQualifiedName!),
                encrypted.EncryptedElement));
        }

        return document;
    }

    private static string? Compare(KeyRingSnapshot before, KeyRingSnapshot after)
    {
        if (!before.ReadableKeys.IsSubsetOf(after.ReadableKeys))
        {
            return "a key that was readable before is not readable through the new protection";
        }

        if (before.Ai.Readable && !after.Ai.SameKeyAs(before.Ai))
        {
            return "the stored AI key does not decrypt to the same value through the new protection";
        }

        return null;
    }

    /// <summary>The base64 secret values of a key file's plain-text elements, to prove none of them survives in any file afterwards.</summary>
    private static IEnumerable<string> SecretValues(XDocument document)
    {
        return document.Descendants().Where(KeyRingFiles.RequiresEncryptionMarker)
            .SelectMany(secret => secret.Descendants().Where(child => !child.HasElements).Select(child => child.Value.Trim()))
            .Where(value => value.Length >= 16);
    }

    private static string? LeftoverSecret(string folder, IReadOnlyList<string> secrets)
    {
        foreach (KeyFileInfo file in KeyRingFiles.Inspect(folder))
        {
            if (file.State == KeyFileState.Plaintext)
            {
                return "a key file is still plain text";
            }

            string text = File.ReadAllText(file.Path);
            if (secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal)))
            {
                return "a key's secret value is still present in a ring file";
            }
        }

        return null;
    }

    private static void WriteOwnerOnly(string path, XDocument document)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        document.Save(stream);
    }

    /// <summary>The staging folder is ours and holds only copies, so it is removed whatever happened; a failure to remove it is not worth failing over.</summary>
    private static void DeleteQuietly(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for RemoveStaleStaging on the next start.
        }
    }

    /// <summary>A staging folder left by a run that was killed holds only copies of ring files; it is ours to clear.</summary>
    private void RemoveStaleStaging()
    {
        foreach (string stale in Directory.EnumerateDirectories(dataFolder, StagingPrefix + "*"))
        {
            Directory.Delete(stale, recursive: true);
        }
    }
}
