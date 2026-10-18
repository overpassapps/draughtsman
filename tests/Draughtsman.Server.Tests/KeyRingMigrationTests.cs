using System.Runtime.Versioning;
using System.Security.Cryptography;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Migrating an existing plain-text key ring to a protected one (the maintainer's hardening task). Each rule is asserted
/// in both directions: the secret is present before and absent after, present in the backup and absent from the ring,
/// readable with the right key-encryption key and unreadable with another, so dropping the encryption, the backup or
/// the verification fails here rather than shipping. Everything starts from a ring the framework wrote without an
/// encryptor, which is what every existing install has; the main-written fixture is the real thing.
/// </summary>
public class KeyRingMigrationTests : IDisposable
{
    private const string KekVariable = "DRAUGHTSMAN_KEY_ENCRYPTION_KEY";

    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            KeyRingTestSupport.DeleteFolder(folder);
        }
    }

    [Fact]
    public void A_ring_and_stored_key_written_by_main_migrate_with_the_key_still_readable()
    {
        string data = Track(KeyRingTestSupport.CopyFixture());
        string keys = Path.Combine(data, "keys");
        string keyFile = Directory.GetFiles(keys, "*.xml").Single();
        List<string> secrets = KeyRingTestSupport.PlainMasterKeys(keys);
        byte[] originalKey = File.ReadAllBytes(keyFile);
        byte[] originalSettings = File.ReadAllBytes(Path.Combine(data, "ai", "settings.json"));

        // Controls: the fixture really is plain text, and the stored key really does decrypt through the plain ring.
        Assert.Single(secrets);
        Assert.Contains(secrets[0], KeyRingTestSupport.AllText(keys), StringComparison.Ordinal);
        Assert.Equal(KeyRingTestSupport.FixtureAiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.FixtureDiscriminator, protection: null));

        string kek = KeyRingTestSupport.NewKekText();
        (KeyRingStatus status, _, KeyRingTestSupport.CapturingLogger log) = KeyRingTestSupport.Startup(
            data, environment: KeyRingTestSupport.Environment((KekVariable, kek)), discriminator: KeyRingTestSupport.FixtureDiscriminator);

        Assert.Equal(KeyRingMigrationOutcome.Migrated, status.Migration.OutcomeOrThrow(log));

        // The ring is no longer plain text.
        Assert.DoesNotContain(secrets[0], KeyRingTestSupport.AllText(keys), StringComparison.Ordinal);
        Assert.All(KeyRingFiles.Inspect(keys), file => Assert.Equal(KeyFileState.Encrypted, file.State));

        // The stored key still decrypts, through a provider built here with the same key-encryption key...
        Assert.Equal(KeyRingTestSupport.FixtureAiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.FixtureDiscriminator, KeyRingTestSupport.Kek(kek)));
        // ...and not without it, nor with a different one: the protection is real.
        Assert.Null(KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.FixtureDiscriminator, protection: null));
        Assert.Null(KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.FixtureDiscriminator, KeyRingTestSupport.Kek()));

        // The backup is the ring as it was, byte for byte, and holds the plain secret; the settings file was not rewritten.
        string backup = Path.Combine(data, status.Migration.BackupFolder!);
        string backedUpKey = Path.Combine(backup, "keys", Path.GetFileName(keyFile));
        Assert.Equal(originalKey, File.ReadAllBytes(backedUpKey));
        Assert.Contains(secrets[0], File.ReadAllText(backedUpKey), StringComparison.Ordinal);
        Assert.Equal(originalSettings, File.ReadAllBytes(Path.Combine(backup, "ai", "settings.json")));
        Assert.Equal(originalSettings, File.ReadAllBytes(Path.Combine(data, "ai", "settings.json")));

        Assert.Empty(Directory.GetDirectories(data, "keys.migrating-*"));
        Assert.Equal("protected", status.Describe().State);
        Assert.Equal("migrated", status.Describe().Migration);
        Assert.Equal(status.Migration.BackupFolder, status.Describe().Backup);
    }

    [Fact]
    public void Every_key_of_a_rotated_ring_is_protected_and_a_payload_from_each_still_opens()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string keys = Path.Combine(data, "keys");

        // A second key, as rotation makes one: the AI key above was protected by the first, this payload by the second.
        string secondPayload;
        using (ServiceProvider plain = KeyRingTestSupport.Provider(data, KeyRingTestSupport.Discriminator, protection: null, createKeys: true))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            plain.GetRequiredService<IKeyManager>().CreateNewKey(now.AddMinutes(-1), now.AddDays(90));
            secondPayload = plain.GetRequiredService<IDataProtectionProvider>().CreateProtector("second").Protect("second-payload");
        }

        // A revocation-style file holds no secret and must come through untouched.
        string noSecret = Path.Combine(keys, "revocation-20260101.xml");
        File.WriteAllText(noSecret, "<revocation version=\"1\"><revocationDate>2026-01-01T00:00:00Z</revocationDate><key id=\"00000000-0000-0000-0000-000000000000\" /></revocation>");
        byte[] noSecretBefore = File.ReadAllBytes(noSecret);
        List<string> secrets = KeyRingTestSupport.PlainMasterKeys(keys);
        Assert.Equal(2, secrets.Count);

        string kek = KeyRingTestSupport.NewKekText();
        (KeyRingStatus status, _, _) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KekVariable, kek)));

        Assert.Equal(2, status.Migration.KeysProtected);
        string ring = KeyRingTestSupport.AllText(keys);
        Assert.All(secrets, secret => Assert.DoesNotContain(secret, ring, StringComparison.Ordinal));
        Assert.Equal(2, KeyRingFiles.Inspect(keys).Count(file => file.State == KeyFileState.Encrypted));
        Assert.Equal(noSecretBefore, File.ReadAllBytes(noSecret));

        KekKeyRingProtection protection = KeyRingTestSupport.Kek(kek);
        Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, protection));
        using ServiceProvider protectedRing = KeyRingTestSupport.Provider(data, KeyRingTestSupport.Discriminator, protection);
        Assert.Equal("second-payload", protectedRing.GetRequiredService<IDataProtectionProvider>().CreateProtector("second").Unprotect(secondPayload));
    }

    [Fact]
    public void A_key_created_after_protection_is_encrypted_when_it_is_written()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        string kek = KeyRingTestSupport.NewKekText();
        (KeyRingStatus status, ServiceCollection services, _) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KekVariable, kek)));
        Assert.Equal(KeyRingMigrationOutcome.NotNeeded, status.Migration.Outcome);

        // The host's own wiring (what Program.cs registers), first use: the framework mints the ring's first key.
        services.AddLogging();
        using ServiceProvider host = services.BuildServiceProvider();
        host.GetRequiredService<IDataProtectionProvider>().CreateProtector("first-use").Protect("x");

        string keys = Path.Combine(data, "keys");
        KeyFileInfo file = Assert.Single(KeyRingFiles.Inspect(keys));
        Assert.Equal(KeyFileState.Encrypted, file.State);
        Assert.Empty(KeyRingTestSupport.PlainMasterKeys(keys));
        // Nothing to back up when there was nothing to protect.
        Assert.False(Directory.Exists(Path.Combine(data, "backups")));
    }

    [Fact]
    public void A_second_start_changes_nothing_and_takes_no_second_backup()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        Func<string, string?> environment = KeyRingTestSupport.Environment((KekVariable, KeyRingTestSupport.NewKekText()));
        (KeyRingStatus first, _, _) = KeyRingTestSupport.Startup(data, environment: environment);
        Dictionary<string, byte[]> ringAfterFirst = KeyRingTestSupport.Bytes(Path.Combine(data, "keys"), "*.xml");

        (KeyRingStatus second, _, _) = KeyRingTestSupport.Startup(data, environment: environment);

        Assert.Equal(KeyRingMigrationOutcome.Migrated, first.Migration.Outcome);
        Assert.Equal(KeyRingMigrationOutcome.NotNeeded, second.Migration.Outcome);
        Assert.Equal(ringAfterFirst, KeyRingTestSupport.Bytes(Path.Combine(data, "keys"), "*.xml"));
        Assert.Single(Directory.GetDirectories(Path.Combine(data, "backups")));
        Assert.Equal("protected", second.Describe().State);
    }

    [Fact]
    public void A_damaged_key_file_is_moved_aside_not_deleted_and_the_healthy_keys_are_still_protected()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string keys = Path.Combine(data, "keys");
        string damaged = Path.Combine(keys, "key-00000000-0000-0000-0000-000000000001.xml");
        File.WriteAllText(damaged, "<key id=\"00000000-0000-0000-0000-000000000001\" version=\"1\"><descriptor><masterKey>not finished");
        byte[] damagedBefore = File.ReadAllBytes(damaged);
        string healthy = Directory.GetFiles(keys, "*.xml").Single(path => path != damaged);
        string secret = KeyRingTestSupport.PlainMasterKeys(keys).Single();

        string kek = KeyRingTestSupport.NewKekText();
        (KeyRingStatus status, ServiceCollection services, KeyRingTestSupport.CapturingLogger log) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KekVariable, kek)));

        Assert.Equal(KeyRingMigrationOutcome.Migrated, status.Migration.OutcomeOrThrow(log));

        // Not edited, not deleted: moved to a name the framework does not read, with a copy in the backup.
        Assert.False(File.Exists(damaged));
        Assert.Equal(damagedBefore, File.ReadAllBytes(damaged + KeyRingQuarantine.Suffix));
        Assert.Equal(damagedBefore, File.ReadAllBytes(Path.Combine(data, status.Migration.BackupFolder!, "keys", Path.GetFileName(damaged))));

        // The healthy key is protected and the stored key still opens.
        Assert.DoesNotContain(secret, File.ReadAllText(healthy), StringComparison.Ordinal);
        Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, KeyRingTestSupport.Kek(kek)));

        // And the ring works again as a whole, which it did not while the damaged file was in it: a new key can be made.
        services.AddLogging();
        using ServiceProvider host = services.BuildServiceProvider();
        Assert.NotEmpty(host.GetRequiredService<IDataProtectionProvider>().CreateProtector("after-quarantine").Protect("x"));

        // It is reported, not hidden: a damaged file may still hold a plain-text key.
        Assert.Equal(1, status.DamagedFiles);
        Assert.Contains("damaged", status.Describe().Message, StringComparison.Ordinal);
        Assert.Contains("damaged", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ring_the_framework_cannot_read_starts_the_server_with_the_key_reported_unreadable_and_the_backup_kept()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string keys = Path.Combine(data, "keys");
        string keyFile = Directory.GetFiles(keys, "*.xml").Single();
        byte[] settings = File.ReadAllBytes(Path.Combine(data, "ai", "settings.json"));
        // The only key file is damaged beyond parsing: the stored key it protected is gone with it.
        File.WriteAllText(keyFile, "<key id=\"5cfbf985");

        string kek = KeyRingTestSupport.NewKekText();
        (KeyRingStatus status, ServiceCollection services, KeyRingTestSupport.CapturingLogger log) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KekVariable, kek)));

        // It started (we are here). The damaged file is kept, moved aside, and in the backup; the settings were not touched.
        Assert.Equal("<key id=\"5cfbf985", File.ReadAllText(keyFile + KeyRingQuarantine.Suffix));
        Assert.Equal("<key id=\"5cfbf985", File.ReadAllText(Directory.GetFiles(Path.Combine(data, "backups"), "*.xml", SearchOption.AllDirectories).Single()));
        Assert.Equal(settings, File.ReadAllBytes(Path.Combine(data, "ai", "settings.json")));

        // The stored key is reported as unreadable, which the admin page turns into "enter it again"...
        Assert.Null(KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, KeyRingTestSupport.Kek(kek)));
        Assert.Equal(1, status.DamagedFiles);
        Assert.Contains("entered again", status.Describe().Message + log.All, StringComparison.Ordinal);

        // ...and entering it again works: the ring makes a fresh, protected key for it.
        services.AddLogging();
        services.AddDataProtection().SetApplicationName(KeyRingTestSupport.Discriminator);
        using ServiceProvider host = services.BuildServiceProvider();
        var store = new Draughtsman.Server.Ai.AiSettingsStore(new DataFolder(data), host.GetRequiredService<IDataProtectionProvider>(), NullLogger<Draughtsman.Server.Ai.AiSettingsStore>.Instance);
        store.Save(new Draughtsman.Server.Ai.AiOverrides("openai", "gpt-4o-mini", null, null), "sk-entered-again-0001");
        Assert.Equal("sk-entered-again-0001", KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, KeyRingTestSupport.Kek(kek)));
        Assert.All(KeyRingFiles.Inspect(keys), file => Assert.Equal(KeyFileState.Encrypted, file.State));
    }

    [Fact]
    public void A_protection_that_disappears_after_migration_reports_the_key_as_needing_re_entry_and_loses_nothing()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string keys = Path.Combine(data, "keys");
        string kek = KeyRingTestSupport.NewKekText();
        (KeyRingStatus migrated, _, _) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KekVariable, kek)));
        string backup = migrated.Migration.BackupFolder!;
        Dictionary<string, byte[]> ringBefore = KeyRingTestSupport.Bytes(keys, "*.xml");
        Dictionary<string, byte[]> backupBefore = KeyRingTestSupport.Bytes(Path.Combine(data, backup, "keys"));

        // The next start has no key-encryption key at all (the variable was removed from the deployment).
        (KeyRingStatus lost, _, KeyRingTestSupport.CapturingLogger log) = KeyRingTestSupport.Startup(data);

        KeyRingState state = lost.Describe();
        Assert.Equal("unprotected", state.State);
        Assert.Equal("no-provider", state.Reason);
        Assert.Equal(1, state.UnreadableKeys);
        Assert.Contains("entered again", state.Message, StringComparison.Ordinal);
        Assert.Contains("entered again", log.All, StringComparison.Ordinal);
        Assert.Null(KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, protection: null));

        // Nothing was rewritten, removed or downgraded, and the backup is still there, byte for byte.
        Assert.Equal(ringBefore, KeyRingTestSupport.Bytes(keys, "*.xml"));
        Assert.Equal(backup, state.Backup);
        Assert.Equal(backupBefore, KeyRingTestSupport.Bytes(Path.Combine(data, backup, "keys")));

        // And restoring the protection makes the key readable again: the loss was of access, not of data.
        Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, KeyRingTestSupport.Kek(kek)));
    }

    [Fact]
    public void A_wrong_key_encryption_key_is_not_mistaken_for_a_fresh_install()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string kek = KeyRingTestSupport.NewKekText();
        KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KekVariable, kek)));
        Dictionary<string, byte[]> ringBefore = KeyRingTestSupport.Bytes(Path.Combine(data, "keys"), "*.xml");

        (KeyRingStatus wrong, _, _) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KekVariable, KeyRingTestSupport.NewKekText())));

        Assert.Equal("protected", wrong.Describe().State);
        Assert.Equal(1, wrong.Describe().UnreadableKeys);
        Assert.Equal(ringBefore, KeyRingTestSupport.Bytes(Path.Combine(data, "keys"), "*.xml"));
        Assert.Null(KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, KeyRingTestSupport.Kek()));
        Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, KeyRingTestSupport.Kek(kek)));
    }

    [Fact]
    public void Protection_that_cannot_be_set_up_leaves_the_plain_ring_untouched_and_the_key_readable()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string keys = Path.Combine(data, "keys");
        Dictionary<string, byte[]> ringBefore = KeyRingTestSupport.Bytes(keys, "*.xml");

        // keyRing.protection: key, but the variable is not set: a missing provider, asked for by name.
        (KeyRingStatus status, _, KeyRingTestSupport.CapturingLogger log) = KeyRingTestSupport.Startup(data, new KeyRingConfig { Protection = "key" });

        KeyRingState state = status.Describe();
        Assert.Equal("unprotected", state.State);
        Assert.Equal("unavailable", state.Reason);
        Assert.Equal(KeyRingMigrationOutcome.NotNeeded, status.Migration.Outcome);
        Assert.Contains(KekVariable, state.Message, StringComparison.Ordinal);
        Assert.Contains("not protected", log.All, StringComparison.OrdinalIgnoreCase);

        // The ring is as it was and still opens the stored key; no backup was made because nothing was touched.
        Assert.Equal(ringBefore, KeyRingTestSupport.Bytes(keys, "*.xml"));
        Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, protection: null));
        Assert.False(Directory.Exists(Path.Combine(data, "backups")));
    }

    [Fact]
    public void A_migration_whose_encrypted_copy_does_not_verify_changes_nothing_and_keeps_the_backup()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string keys = Path.Combine(data, "keys");
        Dictionary<string, byte[]> ringBefore = KeyRingTestSupport.Bytes(keys, "*.xml");
        byte[] settingsBefore = File.ReadAllBytes(Path.Combine(data, "ai", "settings.json"));

        // Encrypts with one key but can only decrypt with another, so the staged ring cannot be read back.
        var protection = new InconsistentProtection(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));
        var log = new KeyRingTestSupport.CapturingLogger();
        KeyRingMigrationResult result = new KeyRingMigration(data, KeyRingTestSupport.Discriminator, protection, log).Run();

        Assert.Equal(KeyRingMigrationOutcome.Failed, result.Outcome);
        Assert.Contains("not readable", result.Problem, StringComparison.Ordinal);
        Assert.Equal(ringBefore, KeyRingTestSupport.Bytes(keys, "*.xml"));
        Assert.Equal(settingsBefore, File.ReadAllBytes(Path.Combine(data, "ai", "settings.json")));
        Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, protection: null));
        Assert.Empty(Directory.GetDirectories(data, "keys.migrating-*"));
        Assert.Equal(ringBefore, KeyRingTestSupport.Bytes(Path.Combine(data, result.BackupFolder!, "keys"), "*.xml"));
    }

    [Fact]
    public void A_ring_that_fails_verification_after_the_swap_is_restored_from_the_backup()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string keys = Path.Combine(data, "keys");
        Dictionary<string, byte[]> ringBefore = KeyRingTestSupport.Bytes(keys, "*.xml");

        // Consistent for the first five wirings, then it "loses" its key: the real folder read after the swap cannot be
        // decrypted, which must put the originals back. A wiring is one Apply: the encryptor (1); then, per reading of the ring,
        // the stack that reads under the current application name and the legacy reader the old-world AI key needs
        // (the key here is bound to a content root): the ring as it was (2, 3), the staged copy (4, 5).
        var protection = new InconsistentProtection(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), consistentWirings: 5);
        var log = new KeyRingTestSupport.CapturingLogger();
        KeyRingMigrationResult result = new KeyRingMigration(data, KeyRingTestSupport.Discriminator, protection, log).Run();

        Assert.Equal(KeyRingMigrationOutcome.RolledBack, result.Outcome);
        Assert.Equal(ringBefore, KeyRingTestSupport.Bytes(keys, "*.xml"));
        Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, protection: null));
        Assert.Contains("put back", log.All, StringComparison.Ordinal);
    }

    [Fact]
    public void Repeated_failed_attempts_reuse_one_backup_instead_of_copying_the_plain_ring_each_time()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        var protection = new InconsistentProtection(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

        KeyRingMigrationResult first = new KeyRingMigration(data, KeyRingTestSupport.Discriminator, protection, NullLogger.Instance).Run();
        KeyRingMigrationResult second = new KeyRingMigration(data, KeyRingTestSupport.Discriminator, protection, NullLogger.Instance).Run();
        Assert.Equal(KeyRingMigrationOutcome.Failed, second.Outcome);

        Assert.Equal(first.BackupFolder, second.BackupFolder);
        Assert.Single(Directory.GetDirectories(Path.Combine(data, "backups")));

        // But a ring that has since changed is a different thing to keep: it gets its own backup.
        File.WriteAllText(Path.Combine(data, "keys", "key-00000000-0000-0000-0000-000000000002.xml"), "<not-a-key />");
        KeyRingMigrationResult third = new KeyRingMigration(data, KeyRingTestSupport.Discriminator, protection, NullLogger.Instance).Run();
        Assert.NotEqual(first.BackupFolder, third.BackupFolder);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(data, "backups")).Length);
    }

    [Fact]
    public void The_backup_is_owner_only_and_carries_a_manifest_and_a_note_saying_how_to_go_back()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        (KeyRingStatus status, _, _) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KekVariable, KeyRingTestSupport.NewKekText())));
        string backup = Path.Combine(data, status.Migration.BackupFolder!);

        Assert.True(File.Exists(Path.Combine(backup, "manifest.json")));
        Assert.Contains("delete this folder", File.ReadAllText(Path.Combine(backup, "README.txt")), StringComparison.Ordinal);
        Assert.Contains("copy keys/*", File.ReadAllText(Path.Combine(backup, "README.txt")), StringComparison.Ordinal);

        if (!OperatingSystem.IsWindows())
        {
            AssertOwnerOnly(data);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void AssertOwnerOnly(string data)
    {
        UnixFileMode other = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        string backups = Path.Combine(data, "backups");
        Assert.All(Directory.EnumerateFileSystemEntries(backups, "*", SearchOption.AllDirectories).Append(backups), entry =>
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(entry) & other));
        Assert.All(Directory.GetFiles(Path.Combine(data, "keys"), "*.xml"), file => Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(file) & other));
    }

    [Fact]
    public void A_ring_written_by_a_build_with_another_assembly_version_still_opens()
    {
        // The key file records the decryptor's assembly-qualified name, version included; an upgrade (or a rollback)
        // changes that version and must not orphan a protected ring.
        foreach (string version in new[] { "0.0.1.0", "9.9.9.9" })
        {
            string data = Track(KeyRingTestSupport.OldWorld());
            string kek = KeyRingTestSupport.NewKekText();
            KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KekVariable, kek)));
            string keyFile = Directory.GetFiles(Path.Combine(data, "keys"), "*.xml").Single();
            string text = File.ReadAllText(keyFile);
            Assert.Contains("KekXmlDecryptor, draughtsman, Version=", text, StringComparison.Ordinal);
            File.WriteAllText(keyFile, System.Text.RegularExpressions.Regex.Replace(text, @"(KekXmlDecryptor, draughtsman, Version=)[0-9.]+", "${1}" + version));
            Assert.Contains("Version=" + version, File.ReadAllText(keyFile), StringComparison.Ordinal);

            Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, KeyRingTestSupport.Kek(kek)));
        }
    }

    [Fact]
    public void Looking_at_a_ring_never_adds_a_key_to_it_but_using_it_does()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        string keys = Path.Combine(data, "keys");

        // The reading the migration and the startup report do, over an empty ring: it must not mint a key.
        KeyRingSnapshot snapshot = KeyRingSnapshot.Read(data, keys, KeyRingTestSupport.Discriminator, KeyRingTestSupport.Kek());
        Assert.Empty(snapshot.ReadableKeys);
        Assert.Empty(Directory.Exists(keys) ? Directory.GetFiles(keys) : []);

        // The control: a provider that may create keys does, the first time it is asked to protect something.
        using (ServiceProvider writing = KeyRingTestSupport.Provider(data, KeyRingTestSupport.Discriminator, protection: null, createKeys: true))
        {
            writing.GetRequiredService<IDataProtectionProvider>().CreateProtector("x").Protect("y");
        }

        Assert.Single(Directory.GetFiles(keys, "*.xml"));
    }

    [Fact]
    public void A_failure_nobody_planned_for_still_starts_the_server_with_the_ring_untouched_and_no_detail_leaked()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        Dictionary<string, byte[]> ringBefore = KeyRingTestSupport.Bytes(Path.Combine(data, "keys"), "*.xml");

        // The Keychain lookup blows up with something the resolver has no case for.
        (KeyRingStatus status, ServiceCollection services, KeyRingTestSupport.CapturingLogger log) = KeyRingTestSupport.Startup(
            data, platform: KeyRingPlatform.MacOS, keychain: _ => throw new InvalidOperationException("boom-detail-that-must-not-leak"));

        KeyRingState state = status.Describe();
        Assert.Equal("unprotected", state.State);
        Assert.Equal("unavailable", state.Reason);
        Assert.Contains("InvalidOperationException", state.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("boom-detail", state.Message + log.All, StringComparison.Ordinal);
        Assert.Equal(ringBefore, KeyRingTestSupport.Bytes(Path.Combine(data, "keys"), "*.xml"));
        Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, protection: null));

        // The host still gets a working ring, as main did.
        services.AddLogging();
        services.AddDataProtection().SetApplicationName(KeyRingTestSupport.Discriminator);
        using ServiceProvider host = services.BuildServiceProvider();
        Assert.NotEmpty(host.GetRequiredService<IDataProtectionProvider>().CreateProtector("still-works").Protect("x"));
    }

    [Fact]
    public void Nothing_secret_reaches_the_log_or_the_status()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string secret = KeyRingTestSupport.PlainMasterKeys(Path.Combine(data, "keys")).Single();
        string kek = KeyRingTestSupport.NewKekText();

        (KeyRingStatus status, _, KeyRingTestSupport.CapturingLogger log) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KekVariable, kek)));
        (KeyRingStatus wrong, _, KeyRingTestSupport.CapturingLogger wrongLog) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KekVariable, KeyRingTestSupport.NewKekText())));
        (KeyRingStatus bad, _, KeyRingTestSupport.CapturingLogger badLog) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KekVariable, "not-base64!" + kek)));

        string everything = string.Join('\n', log.All, wrongLog.All, badLog.All, System.Text.Json.JsonSerializer.Serialize(status.Describe()), System.Text.Json.JsonSerializer.Serialize(wrong.Describe()), System.Text.Json.JsonSerializer.Serialize(bad.Describe()));
        Assert.DoesNotContain(kek, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyRingTestSupport.AiKey, everything, StringComparison.Ordinal);
        // The control: the log is not empty, so the assertions above are about something.
        Assert.Contains("Key ring", log.All, StringComparison.Ordinal);
        Assert.Contains("base64", bad.Describe().Message, StringComparison.Ordinal);
    }

    private string Track(string folder)
    {
        _folders.Add(folder);
        return folder;
    }

    /// <summary>A protection whose encryptor and decryptor disagree about the key, to force the verification steps to fail.</summary>
    private sealed class InconsistentProtection(byte[] encryptWith, byte[] decryptWith, int consistentWirings = 0) : KeyRingProtection
    {
        private int _wirings;

        public override string Kind => KeyRingConfig.ProtectionKey;

        public override string Description => "a deliberately inconsistent test protection";

        public override void Apply(IDataProtectionBuilder builder)
        {
            // The first `consistentWirings` calls decrypt with the key that encrypted; after that, with another.
            bool consistent = Interlocked.Increment(ref _wirings) <= consistentWirings;
            builder.Services.AddSingleton(new KeyEncryptionKeyHolder(consistent ? encryptWith : decryptWith));
            builder.Services.Configure<KeyManagementOptions>(options => options.XmlEncryptor = new KekXmlEncryptor(encryptWith));
        }
    }
}

internal static class KeyRingMigrationResultExtensions
{
    /// <summary>The outcome, with the log attached to the failure so a run that went wrong explains itself in the test report.</summary>
    public static KeyRingMigrationOutcome OutcomeOrThrow(this KeyRingMigrationResult result, KeyRingTestSupport.CapturingLogger log)
    {
        if (result.Outcome is KeyRingMigrationOutcome.Failed or KeyRingMigrationOutcome.RolledBack)
        {
            throw new InvalidOperationException($"Migration did not complete ({result.Outcome}: {result.Problem}).\n{log.All}");
        }

        return result.Outcome;
    }
}
