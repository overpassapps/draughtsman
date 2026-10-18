using Draughtsman.Server.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.KeyRing;

/// <summary>
/// The startup sequence for the Data Protection key ring, run before the host is built so the ring is already
/// protected (and the stored AI key already verified) by the time anything reads it: resolve the protection from
/// <c>keyRing</c> in draughtsman.yaml and the platform, migrate an existing plain-text ring in place
/// (<see cref="KeyRingMigration"/>), wire the ring into the host, and say plainly which protection is active.
/// <para>
/// Nothing here can stop the server starting. Protection that cannot be set up, a ring that cannot be migrated and a
/// ring that cannot be read all end the same way: the server runs with the ring as it is, logs why, and reports it on
/// the admin AI page, where a stored key that no longer decrypts is shown as needing to be entered again.
/// </para>
/// </summary>
internal static class KeyRingSetup
{
    /// <summary>Environment variable (or setting) that overrides <c>keyRing.protection</c>, for a container that cannot edit the file.</summary>
    public const string ProtectionOverrideVariable = "DRAUGHTSMAN_KEY_RING_PROTECTION";

    /// <param name="services">The host's services; the ring's repository, protection and <see cref="KeyRingStatus"/> are registered here.</param>
    /// <param name="dataFolder">The resolved data folder.</param>
    /// <param name="config">The <c>keyRing</c> section of draughtsman.yaml.</param>
    /// <param name="contentRoot">The host's content root. Data Protection is bound to <see cref="DataProtectionIdentity.ApplicationName"/>, not to this; it is the first install path tried, read-only, for a secret an earlier build bound to where it ran.</param>
    /// <param name="logger">Startup logger (the host's own does not exist yet).</param>
    /// <param name="environment">Looks up a variable by name; the host's configuration, so a test can supply one without touching the process environment.</param>
    /// <param name="platform">Overrides the detected platform; tests use it to exercise another platform's path on this one.</param>
    public static KeyRingStatus Configure(
        IServiceCollection services,
        string dataFolder,
        KeyRingConfig config,
        string contentRoot,
        ILogger logger,
        Func<string, string?> environment,
        KeyRingPlatform? platform = null,
        Func<string, IKeyEncryptionKeySource>? keychain = null,
        Func<string?>? dpapiProbe = null)
    {
        string? overridden = environment(ProtectionOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            config = new KeyRingConfig
            {
                Protection = overridden,
                KeyEnvironmentVariable = config.KeyEnvironmentVariable,
                KeyFile = config.KeyFile,
                Certificate = config.Certificate,
                LegacyInstallPaths = config.LegacyInstallPaths,
            };
        }

        string keysFolder = KeyRingFiles.KeysFolder(dataFolder);
        // The install paths an earlier build may have bound its stored secrets to (LegacyDiscriminators).
        IReadOnlyList<LegacyCandidate> legacy = LegacyDiscriminators.Resolve(dataFolder, contentRoot, config, environment);
        KeyRingStatus status;
        try
        {
            status = Prepare(dataFolder, keysFolder, config, contentRoot, legacy, logger, environment, platform ?? KeyRingProtectionResolver.CurrentPlatform, keychain, dpapiProbe);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or KeyRingProtectionRequiredException))
        {
            // The last net: whatever went wrong, the server still starts, with the ring exactly as main would have left it.
            logger.LogError("Key ring: protection could not be set up ({Reason}); the ring was left as it is.", ex.GetType().Name);
            status = new KeyRingStatus(dataFolder, new NoKeyRingProtection(UnprotectedReason.Unavailable, $"Setting up key ring protection failed unexpectedly ({ex.GetType().Name}); see the server log."), KeyRingMigrationResult.NotNeeded, 0, 0);
        }

        // A note of where this program ran, for a later move (LegacyDiscriminators). After the protection is settled, so a start that is refused
        // (an unusable Keychain or DPAPI) leaves the data folder exactly as it found it.
        LegacyDiscriminators.Record(dataFolder, contentRoot, logger);
        // Bound to a constant, not the content root the framework defaults to: a stored secret then depends on the key ring alone, which travels with the data folder.
        status.Protection.Apply(services.AddDataProtection().SetApplicationName(DataProtectionIdentity.ApplicationName).PersistKeysToFileSystem(new DirectoryInfo(keysFolder)));
        services.AddSingleton(status);
        // Read-only: how a secret an earlier build bound to its install path is opened (StoredSecretsMigration then re-protects it).
        services.AddSingleton(new LegacySecretReader(keysFolder, status.Protection, legacy, () => LegacyDiscriminators.Late(dataFolder, contentRoot)));
        Log(logger, status, keysFolder);
        return status;
    }

    private static KeyRingStatus Prepare(
        string dataFolder,
        string keysFolder,
        KeyRingConfig config,
        string contentRoot,
        IReadOnlyList<LegacyCandidate> legacy,
        ILogger logger,
        Func<string, string?> environment,
        KeyRingPlatform platform,
        Func<string, IKeyEncryptionKeySource>? keychain,
        Func<string?>? dpapiProbe)
    {
        KeyRingProtection protection = new KeyRingProtectionResolver(config, dataFolder, platform, environment, keychain, dpapiProbe).Resolve();

        if (protection.IsActive && WiringProblem(protection) is { } wiring)
        {
            // Caught before anything is backed up or rewritten: a protection that cannot even be wired in must not start a migration.
            logger.LogError("Key ring: {Protection} cannot be used here ({Reason}); the ring stays as it is.", protection.Kind, wiring);
            protection = new NoKeyRingProtection(UnprotectedReason.Unavailable, $"{protection.Kind} protection is not available on this platform.");
        }

        // One backup, before anything in the ring changes, covering both things this start may do to it: moving a
        // damaged file aside and protecting the plain-text keys.
        IReadOnlyList<KeyFileInfo> damaged = KeyRingQuarantine.Damaged(dataFolder);
        bool willProtect = protection.IsActive && KeyRingFiles.Inspect(keysFolder).Any(file => file.State == KeyFileState.Plaintext);
        KeyRingMigrationResult migration = KeyRingMigrationResult.NotNeeded;
        if (damaged.Count > 0 || willProtect)
        {
            string backup;
            try
            {
                backup = KeyRingBackup.Create(dataFolder, DateTimeOffset.UtcNow);
                logger.LogInformation("Key ring: backed up the existing ring and AI settings to {Backup} before changing the ring.", backup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No backup, no change: a ring that cannot be copied is not one to rewrite.
                logger.LogError("Key ring: the ring could not be backed up ({Reason}), so it was not changed.", ex.GetType().Name);
                return new KeyRingStatus(dataFolder, protection, new KeyRingMigrationResult(KeyRingMigrationOutcome.Failed, null, 0, $"The ring could not be backed up ({ex.GetType().Name})."), 0, 0, KeyRingQuarantine.Count(dataFolder));
            }

            // Before the ring is read at all: one malformed file would fail every read below and, in the host, all of Data Protection.
            KeyRingQuarantine.Run(damaged, backup, logger);
            if (damaged.Count > 0)
            {
                logger.LogWarning("Key ring: {Count} damaged file(s) moved aside. If the stored AI API key cannot be read as a result, it has to be entered again in the admin AI settings.", damaged.Count);
            }

            if (protection.IsActive)
            {
                try
                {
                    migration = new KeyRingMigration(dataFolder, contentRoot, protection, logger, legacy: legacy).Run(backup);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // The migration reports its own expected failures as a result; this is the net under the unexpected.
                    logger.LogError("Key ring: migration stopped unexpectedly ({Reason}); the ring was left as it is.", ex.GetType().Name);
                    migration = new KeyRingMigrationResult(KeyRingMigrationOutcome.Failed, backup, 0, $"{ex.GetType().Name} while protecting the key ring.");
                }
            }
        }

        int plaintext = KeyRingFiles.Inspect(keysFolder).Count(file => file.State == KeyFileState.Plaintext);
        int unreadable = CountUnreadable(dataFolder, keysFolder, legacy, protection, logger);
        return new KeyRingStatus(dataFolder, protection, migration, plaintext, unreadable, KeyRingQuarantine.Count(dataFolder));
    }

    /// <summary>The type of exception that stops <paramref name="protection"/> being wired into a host here (DPAPI off Windows), or null.</summary>
    private static string? WiringProblem(KeyRingProtection protection)
    {
        try
        {
            _ = KeyRingMigration.EncryptorOf(protection);
            return null;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            return ex.GetType().Name;
        }
    }

    private static int CountUnreadable(string dataFolder, string keysFolder, IReadOnlyList<LegacyCandidate> legacy, KeyRingProtection protection, ILogger logger)
    {
        if (!Directory.Exists(keysFolder))
        {
            return 0;
        }

        try
        {
            return KeyRingSnapshot.Read(dataFolder, keysFolder, legacy, protection).UnreadableKeys;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Key ring: the ring could not be read at startup ({Reason}).", ex.GetType().Name);
            return 0;
        }
    }

    private static void Log(ILogger logger, KeyRingStatus status, string keysFolder)
    {
        KeyRingState state = status.Describe();
        foreach (string note in status.Protection.Notes)
        {
            logger.LogWarning("Key ring: {Note}", note);
        }

        if (status.Protection is KekKeyRingProtection { Created: true })
        {
            logger.LogInformation("Key ring: a new key-encryption key was created for this data folder.");
        }

        if (state.State == "protected")
        {
            logger.LogInformation("Key ring protection: {Protection}. The stored AI API key cannot be decrypted from the data folder alone.", status.Protection.Description);
        }
        else
        {
            logger.LogWarning("Key ring protection: {Message} (ring folder: {Keys})", state.Message, keysFolder);
        }

        if (status.DamagedFiles > 0)
        {
            logger.LogWarning("Key ring: {Count} damaged file(s) are set aside in {Keys} as *{Suffix}; one may still hold a plain-text key, so review them and delete them when you no longer need them.", status.DamagedFiles, keysFolder, KeyRingQuarantine.Suffix);
        }

        if (state.UnreadableKeys > 0)
        {
            logger.LogWarning("Key ring: {Count} key(s) cannot be decrypted with the protection available now; the stored AI API key needs entering again in the admin AI settings.", state.UnreadableKeys);
        }

        if (state.Backup is not null)
        {
            logger.LogWarning("Key ring: the backup {Backup} still holds the old, plain-text ring. Delete it once you have checked the stored AI API key works.", state.Backup);
        }
    }
}
