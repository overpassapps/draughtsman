using System.Security.Cryptography;
using System.Text.Json;
using Draughtsman.Server.Ai;
using Draughtsman.Server.Backup;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Email;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// The stored AI key and the saved SMTP password no longer depend on where the program is installed. Every fixture is the data folder
/// a REAL earlier build wrote, with its secrets bound to the folder that build ran from (a different one from every host here); the
/// current server is started on a copy from another folder, must still read them, re-protects them under the pinned application name,
/// and then a SECOND move to yet another folder must need no fallback at all. Each test pairs the positive with the negative that
/// shows it could fail.
/// </summary>
public sealed class UpgradeDataProtectionPinTests
{
    private const string ExpectedAiKey = "sk-THROWAWAY-upgrade-fixture-not-a-real-key-0123456789";

    private const string ExpectedSmtpPassword = "THROWAWAY-smtp-password-not-real-0123456789";

    public static TheoryData<string> Labels => UpgradeFixture.Labels(UpgradeFixture.Sqlite);

    private static KeyRingProtection? ProtectionOf(UpgradeFixture fixture)
    {
        return fixture.KeyRingProtection == "key" ? KeyRingTestSupport.Kek(fixture.KeyEncryptionKey) : null;
    }

    /// <summary>The secrets as the framework opens them with NOTHING but the pinned application name: no legacy reader, no install path. What "no fallback needed" means.</summary>
    private static (string? Ai, string? Smtp) ReadWithoutFallback(string dataFolder, KeyRingProtection? protection)
    {
        using ServiceProvider provider = KeyRingTestSupport.Provider(dataFolder, DataProtectionIdentity.ApplicationName, protection);
        IDataProtectionProvider framework = provider.GetRequiredService<IDataProtectionProvider>();
        string? ai = new AiSettingsStore(new DataFolder(dataFolder), framework, NullLogger<AiSettingsStore>.Instance).Load().ApiKey;
        string? smtp = File.Exists(Path.Combine(dataFolder, "email", "settings.json"))
            ? new EmailSettingsStore(new DataFolder(dataFolder), framework, NullLogger<EmailSettingsStore>.Instance).Load().Password
            : null;
        return (ai, smtp);
    }

    private static string NewInstall(string parent, string name)
    {
        string folder = Path.Combine(parent, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static ExistingFolderServerFactory Host(UpgradeFixture fixture, string data, string install, string? legacyInstallPaths = null)
    {
        return new ExistingFolderServerFactory(data, fixture.KeyRingProtection, fixture.KeyEncryptionKey, contentRoot: install, legacyInstallPaths: legacyInstallPaths);
    }

    private static (string? Ai, string? Smtp) ReadThroughHost(ExistingFolderServerFactory factory)
    {
        IServiceProvider services = factory.Services;
        string? smtp = services.GetRequiredService<EmailSettingsStore>().Load().Password;
        return (services.GetRequiredService<AiSettingsStore>().Load().ApiKey, smtp);
    }

    /// <summary>The same environment the fixture's build ran with: its key-encryption key, or none.</summary>
    private static (KeyRingConfig Config, Func<string, string?> Environment) RingOf(UpgradeFixture fixture)
    {
        return fixture.KeyRingProtection == "key"
            ? (new KeyRingConfig { Protection = KeyRingConfig.ProtectionKey }, name => name == "DRAUGHTSMAN_KEY_ENCRYPTION_KEY" ? fixture.KeyEncryptionKey : null)
            : (new KeyRingConfig { Protection = KeyRingConfig.ProtectionNone }, _ => null);
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void The_program_moved_and_upgraded_in_one_step_reads_the_secrets_once_the_old_folder_is_named_re_protects_them_and_a_second_move_needs_nothing(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            bool hasEmail = File.Exists(Path.Combine(data, "email", "settings.json"));
            byte[] aiBefore = File.ReadAllBytes(Path.Combine(data, "ai", "settings.json"));
            KeyRingProtection? protection = ProtectionOf(fixture);

            // The fixture is what the OLD build wrote: through the framework alone, under the pinned name, it does not open (it is bound to ITS folder).
            Assert.Null(ReadWithoutFallback(data, protection).Ai);

            // First start of this build, from a folder the old build never saw, with the old folder named: the one step nothing else can know.
            string installA = NewInstall(temp, "install-a");
            Assert.NotEqual(Path.TrimEndingDirectorySeparator(fixture.ContentRoot), installA);
            using (var factory = Host(fixture, data, installA, legacyInstallPaths: fixture.ContentRoot))
            {
                (string? ai, string? smtp) = ReadThroughHost(factory);
                Assert.Equal(ExpectedAiKey, ai);
                Assert.Equal(hasEmail ? ExpectedSmtpPassword : null, smtp);

                IReadOnlyList<StoredSecretState> states = factory.Services.GetRequiredService<KeyRingStatus>().Describe().StoredSecrets!;
                Assert.Equal("reprotected", Assert.Single(states, s => s.Name == "AI API key").State);
                if (hasEmail)
                {
                    Assert.Equal("reprotected", Assert.Single(states, s => s.Name == "SMTP password").State);
                }
            }

            // On disk: the AI settings file changed, and the old bytes are kept, whole, in the backup.
            Assert.NotEqual(aiBefore, File.ReadAllBytes(Path.Combine(data, "ai", "settings.json")));
            string backup = Assert.Single(Directory.GetDirectories(Path.Combine(data, "backups"), "secrets-*"));
            Assert.Equal(aiBefore, File.ReadAllBytes(Path.Combine(backup, "ai", "settings.json")));

            // Read with nothing but the pinned name: no fallback, no install path.
            (string? aiNow, string? smtpNow) = ReadWithoutFallback(data, protection);
            Assert.Equal(ExpectedAiKey, aiNow);
            Assert.Equal(hasEmail ? ExpectedSmtpPassword : null, smtpNow);

            // A SECOND move: the data folder to a new place, the program to yet another folder, and nothing told to the server this time.
            string moved = Path.Combine(temp, "moved", "data");
            KeyRingTestSupport.CopyDirectory(data, moved);
            string installB = NewInstall(temp, "install-b");
            using (var factory = Host(fixture, moved, installB))
            {
                (string? ai, string? smtp) = ReadThroughHost(factory);
                Assert.Equal(ExpectedAiKey, ai);
                Assert.Equal(hasEmail ? ExpectedSmtpPassword : null, smtp);

                IReadOnlyList<StoredSecretState> states = factory.Services.GetRequiredService<KeyRingStatus>().Describe().StoredSecrets!;
                Assert.All(states, s => Assert.Equal("current", s.State));
            }

            Assert.Equal(ExpectedAiKey, ReadWithoutFallback(moved, protection).Ai);
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void The_program_upgraded_in_place_re_protects_the_secrets_with_nothing_named_and_the_next_move_needs_nothing(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            bool hasEmail = File.Exists(Path.Combine(data, "email", "settings.json"));
            (KeyRingConfig config, Func<string, string?> environment) = RingOf(fixture);

            // The new program in the folder the old one ran from (the documented upgrade): its content root IS the old discriminator.
            using (StoredSecretsRig inPlace = StoredSecretsRig.Start(data, fixture.ContentRoot, config, environment))
            {
                Assert.Equal("reprotected", inPlace.Secret("AI API key").State);
                Assert.Equal(ExpectedAiKey, inPlace.Ai.Load().ApiKey);
                if (hasEmail)
                {
                    Assert.Equal("reprotected", inPlace.Secret("SMTP password").State);
                    Assert.Equal(ExpectedSmtpPassword, inPlace.Email.Load().Password);
                }
            }

            // Then the program moves, nothing named: it still reads, and reports there was nothing left to move.
            using StoredSecretsRig moved = StoredSecretsRig.Start(data, Path.Combine(temp, "somewhere", "else") + Path.DirectorySeparatorChar, config, environment);
            Assert.All(moved.Secrets, s => Assert.Equal("current", s.State));
            Assert.Equal(ExpectedAiKey, moved.Ai.Load().ApiKey);
            Assert.Equal(ExpectedAiKey, ReadWithoutFallback(data, ProtectionOf(fixture)).Ai);
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public async Task With_the_old_folder_not_known_nothing_is_changed_the_admin_page_says_how_to_recover_and_naming_it_later_recovers_the_secret(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            bool hasEmail = File.Exists(Path.Combine(data, "email", "settings.json"));
            Dictionary<string, byte[]> settingsBefore = new()
            {
                ["ai"] = File.ReadAllBytes(Path.Combine(data, "ai", "settings.json")),
            };
            if (hasEmail)
            {
                settingsBefore["email"] = File.ReadAllBytes(Path.Combine(data, "email", "settings.json"));
            }

            string installA = NewInstall(temp, "install-a");
            using (var factory = Host(fixture, data, installA))
            {
                IReadOnlyList<StoredSecretState> states = factory.Services.GetRequiredService<KeyRingStatus>().Describe().StoredSecrets!;
                StoredSecretState ai = Assert.Single(states, s => s.Name == "AI API key");
                Assert.Equal("unreadable", ai.State);
                // Which secret, why, and what to do: never a silent drop.
                Assert.Contains("keyRing.legacyInstallPaths", ai.Message, StringComparison.Ordinal);
                Assert.Contains(LegacyDiscriminators.HintVariable, ai.Message, StringComparison.Ordinal);
                Assert.Contains("Admin > AI", ai.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(ExpectedAiKey, ai.Message, StringComparison.Ordinal);
                if (hasEmail)
                {
                    Assert.Equal("unreadable", Assert.Single(states, s => s.Name == "SMTP password").State);
                }

                // The admin page (not just the log) is where it shows.
                using System.Text.Json.JsonDocument expectedDocument = fixture.Expected(UpgradeFixture.Sqlite);
                var checks = new UpgradeAssertions(fixture, expectedDocument.RootElement, _ => null);
                HttpClient? admin = await checks.SignIn(factory, checks.User("admin"));
                Assert.NotNull(admin);
                using System.Text.Json.JsonDocument page = System.Text.Json.JsonDocument.Parse(await admin.GetStringAsync("/api/admin/ai"));
                Assert.True(page.RootElement.GetProperty("key").GetProperty("storedUnreadable").GetBoolean());
                Assert.Contains("keyRing.legacyInstallPaths", page.RootElement.GetProperty("keyRing").GetProperty("storedSecrets")[0].GetProperty("message").GetString(), StringComparison.Ordinal);
            }

            // Untouched: byte for byte, and no backup, because nothing was changed. The ciphertext is still there for whoever names the folder.
            Assert.Equal(settingsBefore["ai"], File.ReadAllBytes(Path.Combine(data, "ai", "settings.json")));
            if (hasEmail)
            {
                Assert.Equal(settingsBefore["email"], File.ReadAllBytes(Path.Combine(data, "email", "settings.json")));
            }

            Assert.Empty(Directory.Exists(Path.Combine(data, "backups")) ? Directory.GetDirectories(Path.Combine(data, "backups"), "secrets-*") : []);

            // Naming the folder (and restarting) brings the secret back.
            using (var factory = Host(fixture, data, installA, legacyInstallPaths: fixture.ContentRoot))
            {
                Assert.Equal(ExpectedAiKey, ReadThroughHost(factory).Ai);
                Assert.Equal("reprotected", Assert.Single(factory.Services.GetRequiredService<KeyRingStatus>().Describe().StoredSecrets!, s => s.Name == "AI API key").State);
            }
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public async Task A_backup_with_secrets_taken_before_the_change_restores_and_its_secrets_come_back_and_one_taken_after_restores_anywhere(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            bool hasEmail = File.Exists(Path.Combine(data, "email", "settings.json"));
            byte[] aiOriginal = File.ReadAllBytes(Path.Combine(data, "ai", "settings.json"));
            (KeyRingConfig config, Func<string, string?> environment) = RingOf(fixture);
            KeyRingProtection? protection = ProtectionOf(fixture);

            // A backup the way the earlier build took it: the key ring, the AI settings and the mail settings, byte for byte, bound to its folder.
            string bundles = Path.Combine(temp, "bundles");
            Directory.CreateDirectory(bundles);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            Assert.True(await BackupCommands.BackupAsync(["--out", bundles + Path.DirectorySeparatorChar, "--include-secrets"], stdout, stderr, data) == 0, stderr.ToString());
            string before = Directory.GetFiles(bundles, "*.zip").Single();
            string restored = Path.Combine(temp, "restored");
            Assert.True(await BackupCommands.RestoreAsync([before], new StringWriter(), stderr, restored) == 0, stderr.ToString());
            Assert.Equal(aiOriginal, File.ReadAllBytes(Path.Combine(restored, "ai", "settings.json")));

            // Restored where the program still runs from the folder the old build ran from: read, and re-protected, with nothing named.
            string sameFolder = Path.Combine(temp, "same-folder");
            KeyRingTestSupport.CopyDirectory(restored, sameFolder);
            using (StoredSecretsRig rig = StoredSecretsRig.Start(sameFolder, fixture.ContentRoot, config, environment))
            {
                Assert.Equal("reprotected", rig.Secret("AI API key").State);
                Assert.Equal(ExpectedAiKey, rig.Ai.Load().ApiKey);
            }

            // Restored under a program somewhere else, the old folder unknown: said plainly, and nothing is altered, so naming it later still works.
            string elsewhere = Path.Combine(temp, "elsewhere");
            KeyRingTestSupport.CopyDirectory(restored, elsewhere);
            string install = Path.Combine(temp, "install-elsewhere") + Path.DirectorySeparatorChar;
            using (StoredSecretsRig rig = StoredSecretsRig.Start(elsewhere, install, config, environment))
            {
                Assert.Equal("unreadable", rig.Secret("AI API key").State);
                Assert.Equal(aiOriginal, File.ReadAllBytes(Path.Combine(elsewhere, "ai", "settings.json")));
            }

            using (StoredSecretsRig rig = StoredSecretsRig.Start(elsewhere, install, new KeyRingConfig { Protection = config.Protection, LegacyInstallPaths = [fixture.ContentRoot] }, environment))
            {
                Assert.Equal("reprotected", rig.Secret("AI API key").State);
                Assert.Equal(ExpectedAiKey, rig.Ai.Load().ApiKey);
                if (hasEmail)
                {
                    Assert.Equal(ExpectedSmtpPassword, rig.Email.Load().Password);
                }
            }

            // A backup taken AFTER the change carries secrets that need no folder: restored into yet another place, nothing named, nothing re-protected.
            string afterBundles = Path.Combine(temp, "after-bundles");
            Directory.CreateDirectory(afterBundles);
            Assert.True(await BackupCommands.BackupAsync(["--out", afterBundles + Path.DirectorySeparatorChar, "--include-secrets"], stdout, stderr, elsewhere) == 0, stderr.ToString());
            string after = Directory.GetFiles(afterBundles, "*.zip").Single();
            string anywhere = Path.Combine(temp, "anywhere");
            Assert.True(await BackupCommands.RestoreAsync([after], new StringWriter(), stderr, anywhere) == 0, stderr.ToString());
            Assert.Equal(ExpectedAiKey, ReadWithoutFallback(anywhere, protection).Ai);
            using StoredSecretsRig third = StoredSecretsRig.Start(anywhere, Path.Combine(temp, "a-third-install") + Path.DirectorySeparatorChar, config, environment);
            Assert.All(third.Secrets, s => Assert.Equal("current", s.State));
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void The_pin_is_what_makes_a_move_harmless_the_framework_default_would_not_open_it(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            KeyRingProtection? protection = ProtectionOf(fixture);
            string installA = NewInstall(temp, "install-a");
            using (var factory = Host(fixture, data, installA, legacyInstallPaths: fixture.ContentRoot))
            {
                Assert.Equal(ExpectedAiKey, ReadThroughHost(factory).Ai);
            }

            // The negative: with the application name left to the framework (the content root, as the earlier builds had it), the very same ring
            // and the very same stored key do not open. This is what the pin removes.
            Assert.Null(KeyRingTestSupport.ReadAiKey(data, installA + Path.DirectorySeparatorChar, protection));
            Assert.Equal(ExpectedAiKey, KeyRingTestSupport.ReadAiKey(data, DataProtectionIdentity.ApplicationName, protection));
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void A_start_records_where_the_program_ran_from_in_the_ring_folder_and_changes_no_ring_file(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            string keys = Path.Combine(data, "keys");
            Dictionary<string, byte[]> ringBefore = KeyRingTestSupport.Bytes(keys, "*.xml");
            string installA = NewInstall(temp, "install-a");
            using (var factory = Host(fixture, data, installA))
            {
                _ = ReadThroughHost(factory);
            }

            // The discriminator is not in the key files: pinning it changes nothing in the ring, whatever protects it.
            if (fixture.KeyRingProtection == "key")
            {
                // Every key file that was there is still there, byte for byte. The ring may GAIN a file: the framework starts a new key when the current
                // one is within two days of its expiry, and a fixture's key (made once, valid 90 days) gets there as the fixture ages.
                Dictionary<string, byte[]> ringAfter = KeyRingTestSupport.Bytes(keys, "*.xml");
                foreach ((string file, byte[] bytes) in ringBefore)
                {
                    Assert.True(ringAfter.TryGetValue(file, out byte[]? now) && bytes.AsSpan().SequenceEqual(now), $"the start changed or removed key file {file}");
                }
            }

            string record = File.ReadAllText(Path.Combine(keys, LegacyDiscriminators.RecordFileName));
            // The record is JSON, so a Windows path is in it with its backslashes escaped: read the paths out of it rather than searching the text.
            using JsonDocument recordJson = JsonDocument.Parse(record);
            string[] recorded = [.. recordJson.RootElement.GetProperty("contentRoots").EnumerateArray().Select(root => Path.TrimEndingDirectorySeparator(root.GetProperty("path").GetString()!))];
            Assert.Contains(Path.TrimEndingDirectorySeparator(installA), recorded);
            Assert.DoesNotContain(ExpectedAiKey, record, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.KeyEncryptionKey ?? "no key", record, StringComparison.Ordinal);
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }
}
