using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Draughtsman.Server.Ai;
using Draughtsman.Server.Email;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.KeyRing;

/// <summary>A secret the server keeps encrypted under the Data Protection key ring: where it lives and how to read and replace it.</summary>
internal interface IProtectedSecretOwner
{
    /// <summary>What an administrator calls it: <c>AI API key</c>, <c>SMTP password</c>.</summary>
    string SecretName { get; }

    /// <summary>The settings file, relative to the data folder.</summary>
    string RelativeFile { get; }

    /// <summary>Where an administrator enters it again.</summary>
    string WhereToEnter { get; }

    SecretProtector Protector { get; }

    ProtectedSecretFile SecretFile { get; }
}

/// <summary>
/// One stored secret as the admin pages and <c>config check</c> show it. Everything here is safe to show an administrator: a name, a file, a stable
/// state word and plain English. No secret, no part of one.
/// </summary>
/// <param name="Name"><c>AI API key</c> or <c>SMTP password</c>.</param>
/// <param name="File">The settings file holding it, relative to the data folder.</param>
/// <param name="State">
/// <c>current</c> (protected independently of the install folder), <c>reprotected</c> (moved there at this start), <c>legacy</c> (readable under an old install path,
/// not yet moved: a read-only check, or a re-protection that failed), or <c>unreadable</c>.
/// </param>
/// <param name="Message">What this means, and what to do.</param>
public sealed record StoredSecretState(string Name, string File, string State, string Message);

/// <summary>What looking at one stored secret found, without changing anything.</summary>
internal sealed record SecretInspection(IProtectedSecretOwner Owner, SecretOutcome? Outcome)
{
    /// <summary>False when nothing is stored (no file, or a file with no protected value).</summary>
    public bool Stored => Outcome is not null;
}

internal static class StoredSecrets
{
    /// <summary>Opens each owner's stored value by every route this build has and reports how, writing nothing.</summary>
    public static IReadOnlyList<SecretInspection> Inspect(IEnumerable<IProtectedSecretOwner> owners)
    {
        return [.. owners.Select(owner => new SecretInspection(owner, owner.SecretFile.ReadPayload() is { } payload ? owner.Protector.Unprotect(payload) : null))];
    }

    /// <summary>
    /// <see cref="Inspect"/> over a private Data Protection stack wired as the host wires it (the pinned application name, the ring's protection) but with
    /// automatic key generation off, so looking can never add a key. What <c>config check</c> uses.
    /// </summary>
    public static IReadOnlyList<SecretInspection> InspectReadOnly(string dataFolder, KeyRingProtection protection, LegacySecretReader legacy)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        IDataProtectionBuilder builder = services.AddDataProtection()
            .SetApplicationName(DataProtectionIdentity.ApplicationName)
            .DisableAutomaticKeyGeneration()
            .PersistKeysToFileSystem(new DirectoryInfo(KeyRingFiles.KeysFolder(dataFolder)));
        protection.Apply(builder);
        using ServiceProvider provider = services.BuildServiceProvider();
        IDataProtectionProvider framework = provider.GetRequiredService<IDataProtectionProvider>();
        var folder = new DataFolder(dataFolder);
        return Inspect([
            new AiSettingsStore(folder, framework, NullLogger<AiSettingsStore>.Instance, legacy),
            new EmailSettingsStore(folder, framework, NullLogger<EmailSettingsStore>.Instance, legacy),
        ]);
    }

    public static StoredSecretState Describe(SecretInspection inspection, LegacySecretReader? tried, string? backup = null, string? failure = null)
    {
        IProtectedSecretOwner owner = inspection.Owner;
        SecretOutcome outcome = inspection.Outcome!;
        string name = owner.SecretName;
        switch (outcome.Reading)
        {
            case SecretReading.Current:
                return new StoredSecretState(name, owner.RelativeFile, backup is null ? "current" : "reprotected", backup is null
                    ? $"The stored {name} is protected by the key ring alone, so it does not depend on the folder the program is installed in."
                    : $"The stored {name} was written by an earlier version that tied it to the folder the program ran from ({outcome.LegacySource?.Source ?? "a previous install folder"}). It has been re-protected, so it no longer depends on any folder. The earlier file is kept in {backup} inside the data folder.");
            case SecretReading.Legacy:
                return new StoredSecretState(name, owner.RelativeFile, "legacy", failure is null
                    ? $"The stored {name} was written by an earlier version that tied it to the folder the program ran from, and it can still be read ({outcome.LegacySource?.Source}). Starting the server re-protects it so that moving the program no longer matters."
                    : $"The stored {name} can be read, but it could not be re-protected at this start ({failure}). It still works. It is tied to the folder the program ran from, so keep the program there until a later start succeeds; nothing was changed.");
            default:
                return new StoredSecretState(name, owner.RelativeFile, "unreadable", Unreadable(owner, outcome.Why, tried));
        }
    }

    private static string Unreadable(IProtectedSecretOwner owner, UnreadableReason? why, LegacySecretReader? tried)
    {
        string name = owner.SecretName;
        string enter = $"Or enter it again in {owner.WhereToEnter}: nothing else is affected.";
        switch (why)
        {
            case UnreadableReason.PathNotKnown:
                // Only now, for a secret nothing opened, are the late candidates (old logs, neighbouring folders) worked out for the message.
                string sources = string.Join("; ", (tried?.Candidates ?? []).Select(c => c.Source).Distinct());
                return $"The stored {name} cannot be read. It was written by an earlier version that tied it to the folder the program ran from, and this version tried {sources} without success. "
                    + "To recover it, start the program once from the folder it ran from before, or name that folder in keyRing.legacyInstallPaths in draughtsman.yaml (or the environment variable "
                    + $"{LegacyDiscriminators.HintVariable}) and restart: it is then re-protected and never depends on a folder again. {enter}";
            case UnreadableReason.KeyUnavailable:
                return $"The stored {name} cannot be read: the key ring does not hold, or cannot open, the key that protected it (a ring restored from an older copy, a ring replaced, or a lost key-encryption key; see the key ring notice). "
                    + $"Restore the ring and its protection that wrote it and restart. {enter}";
            case UnreadableReason.Malformed:
                return $"The stored {name} is damaged: the saved value is not a valid protected value. Enter it again in {owner.WhereToEnter}.";
            default:
                return $"The stored {name} cannot be read (the key ring changed, or it is damaged). Enter it again in {owner.WhereToEnter}.";
        }
    }
}

/// <summary>
/// At startup, moves every stored secret an earlier build bound to its install path onto <see cref="DataProtectionIdentity.ApplicationName"/>, so it no longer
/// depends on where the program runs. The order is the safety argument, per secret:
/// <list type="number">
/// <item>Read it: first under the current name, then (read-only) under each install path in <see cref="LegacyDiscriminators"/>. Only a secret that opened under an old path goes on; one that already opens is not touched, and one that opens nowhere is reported, never altered.</item>
/// <item>Copy its settings file byte for byte to <c>backups/secrets-&lt;stamp&gt;/</c> and verify the copy (<see cref="SecretsBackup"/>).</item>
/// <item>Protect the plain text under the current name and replace ONLY that value in the file through a temporary file and one rename (<see cref="ProtectedSecretFile"/>).</item>
/// <item>Read the file back with the legacy fallback switched off and compare with what was read in step 1. If it does not match, or anything threw, put the copy back.</item>
/// </list>
/// Nothing deletes the earlier ciphertext: it stays in the backup. Nothing here can stop the server starting.
/// </summary>
internal sealed class StoredSecretsMigration(DataFolder dataFolder, AiSettingsStore ai, EmailSettingsStore email, KeyRingStatus? status = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IReadOnlyList<StoredSecretState> Run(ILogger logger)
    {
        IProtectedSecretOwner[] owners = [ai, email];
        LegacySecretReader? tried = ai.Protector.Legacy;
        var states = new List<StoredSecretState>();
        try
        {
            IReadOnlyList<SecretInspection> inspections = StoredSecrets.Inspect(owners);
            string? backup = null;
            string? backupProblem = null;
            List<SecretInspection> legacy = [.. inspections.Where(i => i.Outcome?.Reading == SecretReading.Legacy)];
            if (legacy.Count > 0)
            {
                try
                {
                    backup = SecretsBackup.Create(dataFolder.Path, _clock.GetUtcNow(), [.. legacy.Select(i => i.Owner.RelativeFile)]);
                    logger.LogInformation("Stored secrets: backed up {Files} to {Backup} before re-protecting them.", string.Join(", ", legacy.Select(i => i.Owner.RelativeFile)), backup);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // No backup, no change: a file that cannot be copied is not one to rewrite.
                    backupProblem = $"the settings file could not be backed up ({ex.GetType().Name})";
                    logger.LogError("Stored secrets: {Problem}, so nothing was re-protected.", backupProblem);
                }
            }

            foreach (SecretInspection inspection in inspections.Where(i => i.Stored))
            {
                states.Add(inspection.Outcome!.Reading == SecretReading.Legacy
                    ? Adopt(inspection, tried, backup, backupProblem, logger)
                    : Report(inspection, tried, logger));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The last net: a stored secret that cannot be inspected never stops the server, which still starts with the settings as they are.
            logger.LogError("Stored secrets: the check stopped unexpectedly ({Reason}); the settings were left as they are.", ex.GetType().Name);
        }

        status?.RecordSecrets(states);
        return states;
    }

    private static StoredSecretState Report(SecretInspection inspection, LegacySecretReader? tried, ILogger logger)
    {
        StoredSecretState state = StoredSecrets.Describe(inspection, tried);
        if (state.State == "unreadable")
        {
            logger.LogWarning("Stored secrets: {Message}", state.Message);
        }

        return state;
    }

    private StoredSecretState Adopt(SecretInspection inspection, LegacySecretReader? tried, string? backup, string? backupProblem, ILogger logger)
    {
        IProtectedSecretOwner owner = inspection.Owner;
        if (backup is null)
        {
            return Failed(inspection, tried, backupProblem ?? "no backup was made", logger);
        }

        string? problem = null;
        try
        {
            string plaintext = inspection.Outcome!.Plaintext!;
            ProtectedSecretFile file = owner.SecretFile;
            file.ReplacePayload(owner.Protector.Protect(plaintext));
            SecretOutcome check = file.ReadPayload() is { } written ? owner.Protector.Unprotect(written, allowLegacy: false) : new SecretOutcome(SecretReading.Unreadable, null, null, null);
            if (check.Reading != SecretReading.Current || check.Plaintext is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(check.Plaintext), Encoding.UTF8.GetBytes(plaintext)))
            {
                problem = "the re-protected value did not read back the same";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or JsonException)
        {
            problem = $"{ex.GetType().Name} while writing the re-protected value";
        }

        if (problem is not null)
        {
            try
            {
                SecretsBackup.Restore(dataFolder.Path, backup, owner.RelativeFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError("Stored secrets: the {Name} could not be put back from {Backup} ({Reason}); copy {File} from there by hand.", owner.SecretName, backup, ex.GetType().Name, owner.RelativeFile);
            }

            return Failed(inspection, tried, problem, logger);
        }

        logger.LogInformation("Stored secrets: the {Name} (written under {Source}) is now protected by the key ring alone; it no longer depends on the install folder. The earlier file is kept in {Backup}.", owner.SecretName, inspection.Outcome!.LegacySource?.Source, backup);
        return StoredSecrets.Describe(new SecretInspection(owner, new SecretOutcome(SecretReading.Current, null, inspection.Outcome.LegacySource, null)), tried, backup);
    }

    private static StoredSecretState Failed(SecretInspection inspection, LegacySecretReader? tried, string problem, ILogger logger)
    {
        logger.LogWarning("Stored secrets: the {Name} was readable but could not be re-protected ({Problem}); the file was left as it was and the secret still works. It is tried again at the next start.", inspection.Owner.SecretName, problem);
        return StoredSecrets.Describe(inspection, tried, failure: problem);
    }
}
