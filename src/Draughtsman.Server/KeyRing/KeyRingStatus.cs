namespace Draughtsman.Server.KeyRing;

/// <summary>
/// The key ring's protection as the admin AI page shows it (<c>keyRing</c> in <c>GET /api/admin/ai</c>). Everything
/// here is safe to show an admin: stable identifiers, a plain-English message, a count and a folder name. No key, no
/// key-encryption key, and no part of either.
/// </summary>
/// <param name="State"><c>protected</c> (no key is stored as plain text) or <c>unprotected</c> (something is).</param>
/// <param name="Provider"><c>dpapi</c>, <c>keychain</c>, <c>key</c>, <c>certificate</c> or <c>none</c>.</param>
/// <param name="Reason">For an unprotected ring: <c>no-provider</c>, <c>disabled</c>, <c>unavailable</c> or <c>migration-failed</c>.</param>
/// <param name="Message">What this means, and what to do about it.</param>
/// <param name="UnreadableKeys">Keys in the ring that cannot be decrypted now (a lost or changed key-encryption key); whatever they protected needs entering again.</param>
/// <param name="Backup">The newest pre-migration backup, relative to the data folder, while it exists. It holds the old plain-text ring.</param>
/// <param name="Migration"><c>none</c>, <c>migrated</c>, <c>failed</c> or <c>rolled-back</c>: what this start did to an existing ring.</param>
/// <param name="StoredSecrets">Each secret kept under the ring (the AI API key, the saved SMTP password) and whether it can be read, and why not when it cannot (<see cref="StoredSecretState"/>). Only secrets that are stored are listed; absent until the server has checked them at startup.</param>
public sealed record KeyRingState(string State, string Provider, string? Reason, string Message, int UnreadableKeys, string? Backup, string Migration, IReadOnlyList<StoredSecretState>? StoredSecrets = null);

/// <summary>
/// Decided once at startup by <see cref="KeyRingSetup"/> and read by the admin AI page. Only the backup is looked up
/// again on each read, so the page stops mentioning a backup the operator has deleted without a restart.
/// </summary>
internal sealed class KeyRingStatus(string dataFolder, KeyRingProtection protection, KeyRingMigrationResult migration, int plaintextKeys, int unreadableKeys, int damagedFiles = 0)
{
    private const string Exposure = "anyone who can read this server's data folder can decrypt the stored AI API key";

    public KeyRingProtection Protection => protection;

    public KeyRingMigrationResult Migration => migration;

    private IReadOnlyList<StoredSecretState>? _secrets;

    /// <summary>Records what the startup check found for each stored secret (<see cref="StoredSecretsMigration"/>), for the admin pages.</summary>
    public void RecordSecrets(IReadOnlyList<StoredSecretState> secrets)
    {
        _secrets = secrets;
    }

    /// <summary>Ring files that were not well-formed XML and are set aside as <c>*.damaged</c>. Never changed or deleted by the server, and one may still hold a plain-text key.</summary>
    public int DamagedFiles => damagedFiles;

    public KeyRingState Describe()
    {
        string migrationName = migration.Outcome switch
        {
            KeyRingMigrationOutcome.Migrated => "migrated",
            KeyRingMigrationOutcome.Failed => "failed",
            KeyRingMigrationOutcome.RolledBack => "rolled-back",
            _ => "none",
        };

        string? backup = KeyRingBackup.Latest(dataFolder);
        (string state, string? reason, string message) = Classify();
        if (unreadableKeys > 0)
        {
            message += $" {unreadableKeys} key(s) in the ring cannot be decrypted with the protection available now, so anything they protected, the stored AI API key included, has to be entered again (restoring the earlier protection would make them readable again).";
        }

        if (damagedFiles > 0)
        {
            message += $" {damagedFiles} damaged key file(s) are set aside in the ring folder as *{KeyRingQuarantine.Suffix} (and are in the backup); one of them may still hold a plain-text key, so review them and delete them when you no longer need them.";
        }

        return new KeyRingState(state, protection.Kind, reason, message, unreadableKeys, backup, migrationName, _secrets);
    }

    private (string State, string? Reason, string Message) Classify()
    {
        if (protection is NoKeyRingProtection none)
        {
            string reason = none.Reason switch
            {
                UnprotectedReason.NoProvider => "no-provider",
                UnprotectedReason.Disabled => "disabled",
                _ => "unavailable",
            };
            string message = none.Reason == UnprotectedReason.Unavailable
                ? $"Key ring protection could not be set up: {none.Description} Until that is fixed the ring is not protected, so {Exposure}."
                : $"The key ring is not protected: {Exposure}. {none.Description}";
            return ("unprotected", reason, message);
        }

        if (migration.Outcome is KeyRingMigrationOutcome.Failed or KeyRingMigrationOutcome.RolledBack || plaintextKeys > 0)
        {
            string problem = migration.Problem ?? "a key file in the ring is still stored as plain text";
            return ("unprotected", "migration-failed", $"The key ring is set up to be protected by {protection.Description}, but the existing ring is still plain text ({problem}): {Exposure}. It is unchanged and still works; fix the cause and restart to try again.");
        }

        return ("protected", null, $"The key ring is protected by {protection.Description}.");
    }
}
