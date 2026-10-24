using System.Security.Cryptography;
using System.Text.Json;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Email;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// The Data Protection key ring and the AI key stored under it (docs/api.md, "Key ring protection"), across an upgrade. The ring a build
/// wrote, protected or plain, must open in the current code and decrypt the stored AI key under the application name it was written under
/// (the install path for a build before the pinned name, which is why the manifest records it; the pinned name after); a plain ring is migrated in place
/// by the current startup without losing the key; and with the protection missing nothing is rewritten, so putting it back brings the
/// key back. Each is paired with its negative.
/// </summary>
public sealed class UpgradeKeyRingTests
{
    private const string ExpectedAiKey = "sk-THROWAWAY-upgrade-fixture-not-a-real-key-0123456789";

    public static TheoryData<string> Labels => UpgradeFixture.Labels(UpgradeFixture.Sqlite);

    private const string ExpectedSmtpPassword = "THROWAWAY-smtp-password-not-real-0123456789";

    /// <summary>The saved SMTP password as the framework decrypts it with the given protection, or null when the fixture's build had no email settings or it cannot be decrypted.</summary>
    private static string? ReadSmtpPassword(string dataFolder, string discriminator, KeyRingProtection? protection)
    {
        if (!File.Exists(Path.Combine(dataFolder, "email", "settings.json")))
        {
            return null;
        }

        using ServiceProvider provider = KeyRingTestSupport.Provider(dataFolder, discriminator, protection);
        return new EmailSettingsStore(new DataFolder(dataFolder), provider.GetRequiredService<IDataProtectionProvider>(), NullLogger<EmailSettingsStore>.Instance).Load().Password;
    }

    private static KeyRingProtection? ProtectionOf(UpgradeFixture fixture, string? keyOverride = null)
    {
        return fixture.KeyRingProtection == "key" ? KeyRingTestSupport.Kek(keyOverride ?? fixture.KeyEncryptionKey) : null;
    }

    /// <summary>Every file under a folder with its hash, leaving out the one note a start adds to the ring folder: where the program ran from (<see cref="LegacyDiscriminators.RecordFileName"/>). The ring itself must not change.</summary>
    private static Dictionary<string, string> Files(string folder)
    {
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != LegacyDiscriminators.RecordFileName)
            .ToDictionary(f => Path.GetRelativePath(folder, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void The_ring_an_earlier_build_wrote_opens_and_decrypts_the_stored_ai_key(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);

            string? key = KeyRingTestSupport.ReadAiKey(data, fixture.Discriminator, ProtectionOf(fixture));

            Assert.Equal(ExpectedAiKey, key);
            if (File.Exists(Path.Combine(data, "email", "settings.json")))
            {
                Assert.Equal(ExpectedSmtpPassword, ReadSmtpPassword(data, fixture.Discriminator, ProtectionOf(fixture)));
                Assert.Null(ReadSmtpPassword(data, "/somewhere/else/draughtsman/", ProtectionOf(fixture)));
            }
            // The ring is protected exactly as the build said it was: encrypted files for a key-encryption key, plain text for none.
            IReadOnlyList<KeyFileInfo> files = KeyRingFiles.Inspect(KeyRingFiles.KeysFolder(data));
            Assert.NotEmpty(files);
            Assert.All(files.Where(f => f.State != KeyFileState.NoSecret), f => Assert.Equal(fixture.KeyRingProtection == "key" ? KeyFileState.Encrypted : KeyFileState.Plaintext, f.State));
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void The_same_ring_does_not_open_under_another_install_path_or_the_wrong_key_encryption_key(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);

            // Through the framework alone, with another install path as the application name, the payload an earlier build wrote is unreadable: it IS bound to the
            // path it was written under. What makes an upgrade that moves the program safe is this build reading it through its legacy fallback and
            // re-protecting it (UpgradeDataProtectionPinTests), not the payload changing.
            Assert.Null(KeyRingTestSupport.ReadAiKey(data, "/somewhere/else/draughtsman/", ProtectionOf(fixture)));
            if (fixture.KeyRingProtection == "key")
            {
                Assert.Null(KeyRingTestSupport.ReadAiKey(data, fixture.Discriminator, ProtectionOf(fixture, KeyEncryptionKey.Generate())));
            }
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void The_current_startup_opens_the_ring_and_either_leaves_it_alone_or_migrates_it_without_losing_the_ai_key(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            Dictionary<string, string> before = Files(Path.Combine(data, "keys"));
            bool plain = fixture.KeyRingProtection != "key";
            // A key-ring-protected fixture is opened with its own key-encryption key. A plain one is opened by a current server that is
            // configured to protect it: the migration, on the very start that follows an upgrade.
            string kek = plain ? KeyEncryptionKey.Generate() : fixture.KeyEncryptionKey!;
            var services = new ServiceCollection();
            services.AddLogging();

            KeyRingStatus status = KeyRingSetup.Configure(
                services,
                data,
                new KeyRingConfig { Protection = "key" },
                fixture.ContentRoot,
                NullLogger.Instance,
                name => name == "DRAUGHTSMAN_KEY_ENCRYPTION_KEY" ? kek : null);

            KeyRingState state = status.Describe();
            Assert.Equal("protected", state.State);
            Assert.Equal(0, state.UnreadableKeys);
            if (plain)
            {
                Assert.Equal("migrated", state.Migration);
                Assert.NotNull(state.Backup);
                Assert.NotEqual(before, Files(Path.Combine(data, "keys")));
                Assert.Empty(KeyRingTestSupport.PlainMasterKeys(Path.Combine(data, "keys")));
            }
            else
            {
                Assert.Equal("none", state.Migration);
                Assert.Equal(before, Files(Path.Combine(data, "keys")));
            }

            // Through the ring the host would use, the stored key is the one the earlier build stored.
            Assert.Equal(ExpectedAiKey, KeyRingTestSupport.ReadAiKey(data, fixture.Discriminator, KeyRingTestSupport.Kek(kek)));
            if (File.Exists(Path.Combine(data, "email", "settings.json")))
            {
                Assert.Equal(ExpectedSmtpPassword, ReadSmtpPassword(data, fixture.Discriminator, KeyRingTestSupport.Kek(kek)));
            }
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void With_the_protection_missing_nothing_is_rewritten_and_putting_it_back_brings_the_key_back(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        if (fixture.KeyRingProtection != "key")
        {
            return;
        }

        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            Dictionary<string, string> before = Files(data);
            var services = new ServiceCollection();
            services.AddLogging();

            KeyRingStatus lost = KeyRingSetup.Configure(services, data, new KeyRingConfig { Protection = "key" }, fixture.ContentRoot, NullLogger.Instance, _ => null);

            KeyRingState state = lost.Describe();
            Assert.NotEqual("protected", state.State);
            Assert.Equal(before, Files(data));
            // The only thing the start added is the note of where it ran from, in the ring folder.
            Assert.True(File.Exists(Path.Combine(data, "keys", LegacyDiscriminators.RecordFileName)));
            // The stored key is reported as unreadable, never lost: restoring the key-encryption key reads it again.
            Assert.Null(KeyRingTestSupport.ReadAiKey(data, fixture.Discriminator, null));
            Assert.Equal(ExpectedAiKey, KeyRingTestSupport.ReadAiKey(data, fixture.Discriminator, KeyRingTestSupport.Kek(fixture.KeyEncryptionKey)));
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }

    [Fact]
    public void There_are_fixtures_of_both_ring_kinds_so_both_paths_above_run()
    {
        Assert.Contains(UpgradeFixture.All, f => f.KeyRingProtection == "key");
        Assert.Contains(UpgradeFixture.All, f => f.KeyRingProtection == "none");
    }
}
