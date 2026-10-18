using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The building blocks of key ring protection: the key-encryption key's crypto and parsing, where it may come from,
/// and which protection a configuration resolves to on which platform. Two-sided throughout: a right key opens and a
/// wrong or altered one does not; a source outside the data folder is accepted and one inside it is refused.
/// </summary>
public class KeyRingProtectionTests : IDisposable
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
    public void Sealed_data_opens_with_its_key_and_not_with_another_or_after_tampering()
    {
        byte[] kek = RandomNumberGenerator.GetBytes(32);
        byte[] plaintext = "a master key"u8.ToArray();

        byte[] sealedData = KeyEncryptionKey.Seal(kek, plaintext);

        Assert.Equal(plaintext, KeyEncryptionKey.Open(kek, sealedData));
        Assert.DoesNotContain("a master key", Convert.ToHexString(sealedData), StringComparison.Ordinal);
        Assert.NotEqual(sealedData, KeyEncryptionKey.Seal(kek, plaintext));
        Assert.ThrowsAny<CryptographicException>(() => KeyEncryptionKey.Open(RandomNumberGenerator.GetBytes(32), sealedData));

        byte[] tampered = (byte[])sealedData.Clone();
        tampered[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => KeyEncryptionKey.Open(kek, tampered));
        Assert.ThrowsAny<CryptographicException>(() => KeyEncryptionKey.Open(kek, sealedData[..10]));
    }

    [Fact]
    public void A_key_encryption_key_must_be_base64_of_enough_bytes_and_a_refusal_never_repeats_it()
    {
        byte[] good = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(good, KeyEncryptionKey.Parse("  " + Convert.ToBase64String(good) + "\n"));
        Assert.Equal(good, KeyEncryptionKey.Parse(Convert.ToBase64String(good).TrimEnd('=').Replace('+', '-').Replace('/', '_')));
        Assert.Equal(48, KeyEncryptionKey.Parse(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))).Length);

        string tooShort = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        KeyEncryptionKeyException shortError = Assert.Throws<KeyEncryptionKeyException>(() => KeyEncryptionKey.Parse(tooShort));
        Assert.Contains("at least 32", shortError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tooShort, shortError.Message, StringComparison.Ordinal);

        const string notBase64 = "correct horse battery staple!";
        KeyEncryptionKeyException formatError = Assert.Throws<KeyEncryptionKeyException>(() => KeyEncryptionKey.Parse(notBase64));
        Assert.Contains("base64", formatError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("horse", formatError.Message, StringComparison.Ordinal);

        Assert.Throws<KeyEncryptionKeyException>(() => KeyEncryptionKey.Parse("   "));
    }

    [Fact]
    public void The_encrypted_element_carries_no_plain_text_and_names_the_decryptor_the_framework_will_ask_for()
    {
        var encryptor = new KekXmlEncryptor(RandomNumberGenerator.GetBytes(32));
        var secret = new XElement("masterKey", new XElement("value", "TOP-SECRET-MASTER-KEY-VALUE"));

        EncryptedXmlInfo info = encryptor.Encrypt(secret);

        Assert.Equal(typeof(KekXmlDecryptor), info.DecryptorType);
        Assert.DoesNotContain("TOP-SECRET-MASTER-KEY-VALUE", info.EncryptedElement.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("masterKey", info.EncryptedElement.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_environment_source_reads_the_variable_and_says_which_one_is_missing()
    {
        string kek = KeyRingTestSupport.NewKekText();
        var source = new EnvironmentKeySource("MY_KEK", KeyRingTestSupport.Environment(("MY_KEK", kek)));
        Assert.Equal(KeyEncryptionKey.Parse(kek), source.Load().Key);
        Assert.Contains("MY_KEK", source.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(kek, source.Description, StringComparison.Ordinal);

        KeyEncryptionKeyException missing = Assert.Throws<KeyEncryptionKeyException>(() => new EnvironmentKeySource("MY_KEK", _ => null).Load());
        Assert.Contains("MY_KEK", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_file_outside_the_data_folder_is_used_and_one_inside_it_is_refused()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        string elsewhere = Track(KeyRingTestSupport.NewFolder());
        string kek = KeyRingTestSupport.NewKekText();

        string outside = Path.Combine(elsewhere, "kek.txt");
        File.WriteAllText(outside, kek + "\n");
        Assert.Equal(KeyEncryptionKey.Parse(kek), new FileKeySource(outside, data).Load().Key);

        string inside = Path.Combine(data, "secrets", "kek.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        File.WriteAllText(inside, kek);
        KeyEncryptionKeyException refusal = Assert.Throws<KeyEncryptionKeyException>(() => new FileKeySource(inside, data).Load());
        Assert.Contains("inside the data folder", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(kek, refusal.Message, StringComparison.Ordinal);

        // A path that merely starts with the same characters is a different folder, not inside.
        string sibling = data + "-sibling";
        Directory.CreateDirectory(sibling);
        Track(sibling);
        File.WriteAllText(Path.Combine(sibling, "kek.txt"), kek);
        Assert.Equal(KeyEncryptionKey.Parse(kek), new FileKeySource(Path.Combine(sibling, "kek.txt"), data).Load().Key);

        Assert.Throws<KeyEncryptionKeyException>(() => new FileKeySource(Path.Combine(elsewhere, "absent.txt"), data).Load());
    }

    [Fact]
    public void A_symbolic_link_outside_the_data_folder_cannot_smuggle_in_a_key_file_inside_it()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        string elsewhere = Track(KeyRingTestSupport.NewFolder());
        string target = Path.Combine(data, "kek.txt");
        File.WriteAllText(target, KeyRingTestSupport.NewKekText());
        string link = Path.Combine(elsewhere, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Creating a symbolic link needs a privilege on Windows; without it there is nothing to test.
            return;
        }

        Assert.Throws<KeyEncryptionKeyException>(() => new FileKeySource(link, data).Load());
    }

    [Fact]
    public void A_key_file_other_users_can_read_is_used_but_the_operator_is_told()
    {
        if (!OperatingSystem.IsWindows())
        {
            AssertReadableByOthersIsNoted();
        }
    }

    [UnsupportedOSPlatform("windows")]
    private void AssertReadableByOthersIsNoted()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        string elsewhere = Track(KeyRingTestSupport.NewFolder());
        string path = Path.Combine(elsewhere, "kek.txt");
        File.WriteAllText(path, KeyRingTestSupport.NewKekText());
        SetMode(path, owner: true);
        Assert.Empty(new FileKeySource(path, data).Load().Notes);

        SetMode(path, owner: false);
        KeyEncryptionKeyLookup lookup = new FileKeySource(path, data).Load();
        Assert.Contains("chmod 600", Assert.Single(lookup.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void On_linux_with_nothing_configured_the_ring_is_unprotected_and_the_message_says_what_to_set()
    {
        string data = Track(KeyRingTestSupport.NewFolder());

        KeyRingProtection protection = Resolver(data, new KeyRingConfig(), KeyRingPlatform.Other).Resolve();

        NoKeyRingProtection none = Assert.IsType<NoKeyRingProtection>(protection);
        Assert.Equal(UnprotectedReason.NoProvider, none.Reason);
        Assert.False(none.IsActive);
        Assert.Contains(KekVariable, none.Description, StringComparison.Ordinal);
        Assert.Contains("keyRing.keyFile", none.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_encryption_key_from_the_environment_or_a_file_activates_protection_on_any_platform()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        string elsewhere = Track(KeyRingTestSupport.NewFolder());
        string kek = KeyRingTestSupport.NewKekText();

        KeyRingProtection fromEnvironment = Resolver(data, new KeyRingConfig(), KeyRingPlatform.Other, (KekVariable, kek)).Resolve();
        Assert.Equal(KeyRingConfig.ProtectionKey, fromEnvironment.Kind);
        Assert.Contains(KekVariable, fromEnvironment.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(kek, fromEnvironment.Description, StringComparison.Ordinal);

        string file = Path.Combine(elsewhere, "kek.txt");
        File.WriteAllText(file, kek);
        KeyRingProtection fromFile = Resolver(data, new KeyRingConfig { KeyFile = file }, KeyRingPlatform.Other).Resolve();
        Assert.Equal(KeyRingConfig.ProtectionKey, fromFile.Kind);
        Assert.Contains(file, fromFile.Description, StringComparison.Ordinal);

        // A key-encryption key wins over the operating system store on the platforms that have one.
        Assert.Equal(KeyRingConfig.ProtectionKey, Resolver(data, new KeyRingConfig(), KeyRingPlatform.Windows, (KekVariable, kek)).Resolve().Kind);
        Assert.Equal(KeyRingConfig.ProtectionKey, Resolver(data, new KeyRingConfig(), KeyRingPlatform.MacOS, (KekVariable, kek)).Resolve().Kind);
    }

    [Fact]
    public void Configured_protection_that_cannot_be_used_is_reported_as_unavailable_not_thrown()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        string inside = Path.Combine(data, "kek.txt");
        File.WriteAllText(inside, KeyRingTestSupport.NewKekText());

        (string Name, KeyRingConfig Config, KeyRingPlatform Platform, (string, string)[] Variables, string Expect)[] cases =
        [
            ("an invalid key in the variable", new KeyRingConfig(), KeyRingPlatform.Other, [(KekVariable, "not a key")], "base64"),
            ("a key file inside the data folder", new KeyRingConfig { KeyFile = inside }, KeyRingPlatform.Other, [], "inside the data folder"),
            ("a missing key file", new KeyRingConfig { KeyFile = Path.Combine(Path.GetTempPath(), "draughtsman-absent-" + Guid.NewGuid().ToString("N")) }, KeyRingPlatform.Other, [], "could not be read"),
            ("protection key with no key", new KeyRingConfig { Protection = "key" }, KeyRingPlatform.Other, [], KekVariable),
            ("dpapi off Windows", new KeyRingConfig { Protection = "dpapi" }, KeyRingPlatform.Other, [], "only on Windows"),
            ("keychain off macOS", new KeyRingConfig { Protection = "keychain" }, KeyRingPlatform.Windows, [], "only on macOS"),
            ("a missing certificate", new KeyRingConfig { Protection = "certificate" }, KeyRingPlatform.Other, [], "certificate.path is empty"),
            ("a certificate file that is not there", new KeyRingConfig { Certificate = new KeyRingCertificateConfig { Path = Path.Combine(data, "nope.pfx") } }, KeyRingPlatform.Other, [], "does not exist"),
            ("an unknown protection name", new KeyRingConfig { Protection = "rot13" }, KeyRingPlatform.Other, [], "not one of"),
        ];

        foreach ((string name, KeyRingConfig config, KeyRingPlatform platform, (string, string)[] variables, string expect) in cases)
        {
            NoKeyRingProtection none = Assert.IsType<NoKeyRingProtection>(Resolver(data, config, platform, variables).Resolve());
            Assert.True(none.Reason == UnprotectedReason.Unavailable, $"{name}: {none.Description}");
            Assert.True(none.Description.Contains(expect, StringComparison.Ordinal), $"{name}: expected \"{expect}\" in: {none.Description}");
        }
    }

    [Fact]
    public void Protection_none_is_a_deliberate_choice_and_is_reported_as_one()
    {
        string data = Track(KeyRingTestSupport.NewFolder());

        // Even with a key supplied, an explicit none is honoured: the operator asked for it.
        NoKeyRingProtection none = Assert.IsType<NoKeyRingProtection>(Resolver(data, new KeyRingConfig { Protection = "none" }, KeyRingPlatform.Other, (KekVariable, KeyRingTestSupport.NewKekText())).Resolve());
        Assert.Equal(UnprotectedReason.Disabled, none.Reason);
    }

    [Fact]
    public void Windows_resolves_to_dpapi_and_macos_to_the_keychain_with_a_stable_account_per_ring()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        Assert.Equal(KeyRingConfig.ProtectionDpapi, Resolver(data, new KeyRingConfig(), KeyRingPlatform.Windows).Resolve().Kind);

        var asked = new List<string>();
        Func<string, IKeyEncryptionKeySource> keychain = account =>
        {
            asked.Add(account);
            return new FixedSource(KeyRingTestSupport.NewKekText());
        };

        KeyRingProtection first = new KeyRingProtectionResolver(new KeyRingConfig(), data, KeyRingPlatform.MacOS, _ => null, keychain).Resolve();
        KeyRingProtection second = new KeyRingProtectionResolver(new KeyRingConfig(), data, KeyRingPlatform.MacOS, _ => null, keychain).Resolve();
        Assert.Equal(KeyRingConfig.ProtectionKeychain, first.Kind);
        Assert.Equal(KeyRingConfig.ProtectionKeychain, second.Kind);
        Assert.Equal(2, asked.Count);
        Assert.Equal(asked[0], asked[1]);

        // A different data folder is a different ring with its own Keychain item.
        string other = Track(KeyRingTestSupport.NewFolder());
        _ = new KeyRingProtectionResolver(new KeyRingConfig(), other, KeyRingPlatform.MacOS, _ => null, keychain).Resolve();
        Assert.NotEqual(asked[0], asked[2]);
        Assert.Contains("ring-protection.json", Directory.GetFiles(Path.Combine(data, "keys")).Select(Path.GetFileName));
    }

    [Fact]
    public void A_certificate_protects_the_ring_and_the_stored_key_opens_with_it_and_not_without()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string certs = Track(KeyRingTestSupport.NewFolder());
        string pfx = Path.Combine(certs, "ring.pfx");
        File.WriteAllBytes(pfx, NewCertificate("ring-password", includePrivateKey: true));
        var config = new KeyRingConfig { Certificate = new KeyRingCertificateConfig { Path = pfx } };
        Func<string, string?> environment = KeyRingTestSupport.Environment((config.Certificate.PasswordEnvironmentVariable, "ring-password"));
        string secret = KeyRingTestSupport.PlainMasterKeys(Path.Combine(data, "keys")).Single();

        (KeyRingStatus status, _, KeyRingTestSupport.CapturingLogger log) = KeyRingTestSupport.Startup(data, config, environment);

        Assert.Equal(KeyRingConfig.ProtectionCertificate, status.Protection.Kind);
        Assert.Equal(KeyRingMigrationOutcome.Migrated, status.Migration.OutcomeOrThrow(log));
        Assert.DoesNotContain(secret, KeyRingTestSupport.AllText(Path.Combine(data, "keys")), StringComparison.Ordinal);
        Assert.DoesNotContain("ring-password", log.All, StringComparison.Ordinal);

        KeyRingProtection again = Resolver(data, config, KeyRingPlatform.Other, (config.Certificate.PasswordEnvironmentVariable, "ring-password")).Resolve();
        Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, again));
        Assert.Null(KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, protection: null));
    }

    [Fact]
    public void A_certificate_with_the_wrong_password_or_no_private_key_is_unavailable_with_a_reason()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        string certs = Track(KeyRingTestSupport.NewFolder());
        string pfx = Path.Combine(certs, "ring.pfx");
        File.WriteAllBytes(pfx, NewCertificate("right", includePrivateKey: true));
        var config = new KeyRingConfig { Certificate = new KeyRingCertificateConfig { Path = pfx } };

        NoKeyRingProtection wrong = Assert.IsType<NoKeyRingProtection>(Resolver(data, config, KeyRingPlatform.Other, (config.Certificate.PasswordEnvironmentVariable, "pw-Zx81-not-the-one")).Resolve());
        Assert.Contains("password", wrong.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("pw-Zx81-not-the-one", wrong.Description, StringComparison.Ordinal);

        string publicOnly = Path.Combine(certs, "public.pfx");
        File.WriteAllBytes(publicOnly, NewCertificate(null, includePrivateKey: false));
        NoKeyRingProtection noKey = Assert.IsType<NoKeyRingProtection>(Resolver(data, new KeyRingConfig { Certificate = new KeyRingCertificateConfig { Path = publicOnly } }, KeyRingPlatform.Other).Resolve());
        Assert.Contains("private key", noKey.Description, StringComparison.Ordinal);

        // And the control: the right password on the right file does resolve.
        Assert.Equal(KeyRingConfig.ProtectionCertificate, Resolver(data, config, KeyRingPlatform.Other, (config.Certificate.PasswordEnvironmentVariable, "right")).Resolve().Kind);
    }

    [Fact]
    public void The_environment_override_wins_over_the_file_and_is_the_only_way_a_container_can_switch_protection_off()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string kek = KeyRingTestSupport.NewKekText();
        Func<string, string?> environment = KeyRingTestSupport.Environment((KekVariable, kek), (KeyRingSetup.ProtectionOverrideVariable, "none"));

        (KeyRingStatus status, _, _) = KeyRingTestSupport.Startup(data, new KeyRingConfig(), environment);

        KeyRingState state = status.Describe();
        Assert.Equal("none", state.Provider);
        Assert.Equal("disabled", state.Reason);
        Assert.Equal(KeyRingMigrationOutcome.NotNeeded, status.Migration.Outcome);
        Assert.NotEmpty(KeyRingTestSupport.PlainMasterKeys(Path.Combine(data, "keys")));
    }

    [RequiresMacOSFact]
    public void The_macos_keychain_item_is_created_once_reused_and_replaced_only_after_it_is_deleted()
    {
        string account = Guid.NewGuid().ToString("N");
        var source = new MacKeychainKeySource(account, "Draughtsman test key ring " + account);
        try
        {
            KeyEncryptionKeyLookup created = source.Load();
            Assert.True(created.Created);
            Assert.Equal(32, created.Key.Length);

            KeyEncryptionKeyLookup reused = source.Load();
            Assert.False(reused.Created);
            Assert.Equal(created.Key, reused.Key);

            Assert.True(source.Delete());
            KeyEncryptionKeyLookup replaced = source.Load();
            Assert.True(replaced.Created);
            Assert.NotEqual(created.Key, replaced.Key);
        }
        finally
        {
            source.Delete();
        }
    }

    [RequiresMacOSFact]
    public void A_ring_migrated_with_the_real_keychain_opens_until_the_keychain_item_goes_and_then_needs_the_key_entered_again()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string service = "Draughtsman test key ring " + Guid.NewGuid().ToString("N");
        MacKeychainKeySource? source = null;
        Func<string, IKeyEncryptionKeySource> keychain = account => source = new MacKeychainKeySource(account, service);
        try
        {
            (KeyRingStatus status, _, KeyRingTestSupport.CapturingLogger log) = KeyRingTestSupport.Startup(data, platform: KeyRingPlatform.MacOS, keychain: keychain);

            Assert.Equal(KeyRingConfig.ProtectionKeychain, status.Protection.Kind);
            Assert.Equal(KeyRingMigrationOutcome.Migrated, status.Migration.OutcomeOrThrow(log));
            Assert.All(KeyRingFiles.Inspect(Path.Combine(data, "keys")), file => Assert.Equal(KeyFileState.Encrypted, file.State));
            KeyRingProtection again = new KeyRingProtectionResolver(new KeyRingConfig(), data, KeyRingPlatform.MacOS, _ => null, keychain).Resolve();
            Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, again));
            Assert.Contains("macOS Keychain", log.All, StringComparison.Ordinal);

            // The item is deleted (another Mac, a keychain reset): a new key is made, the old one is not quietly lost or reused.
            Assert.True(source!.Delete());
            (KeyRingStatus lost, _, _) = KeyRingTestSupport.Startup(data, platform: KeyRingPlatform.MacOS, keychain: keychain);
            Assert.Equal(1, lost.Describe().UnreadableKeys);
            Assert.Null(KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, new KeyRingProtectionResolver(new KeyRingConfig(), data, KeyRingPlatform.MacOS, _ => null, keychain).Resolve()));
        }
        finally
        {
            source?.Delete();
        }
    }

    // Runs only on the Windows CI leg: Windows DPAPI is the one protection this suite cannot exercise on a Mac.
    [RequiresWindowsFact]
    public void A_ring_migrated_with_windows_dpapi_keeps_the_stored_key_readable()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string secret = KeyRingTestSupport.PlainMasterKeys(Path.Combine(data, "keys")).Single();

        (KeyRingStatus status, _, KeyRingTestSupport.CapturingLogger log) = KeyRingTestSupport.Startup(data, platform: KeyRingPlatform.Windows);

        Assert.Equal(KeyRingConfig.ProtectionDpapi, status.Protection.Kind);
        Assert.Equal(KeyRingMigrationOutcome.Migrated, status.Migration.OutcomeOrThrow(log));
        Assert.DoesNotContain(secret, KeyRingTestSupport.AllText(Path.Combine(data, "keys")), StringComparison.Ordinal);
        Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, status.Protection));

        // On disk the secret is a real DPAPI blob, not a plain or merely base64 value: it starts with the DPAPI provider GUID, which is
        // what makes it readable only by this Windows account (reading the files tells another account nothing).
        IReadOnlyList<KeyFileInfo> files = KeyRingFiles.Inspect(Path.Combine(data, "keys"));
        Assert.NotEmpty(files);
        Assert.All(files, file => Assert.Equal(KeyFileState.Encrypted, file.State));
        byte[] dpapiProvider = [0x01, 0x00, 0x00, 0x00, 0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11, 0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB];
        foreach (KeyFileInfo file in files)
        {
            XElement encrypted = XDocument.Load(file.Path).Descendants(KeyRingFiles.EncryptedSecret).Single();
            Assert.Contains("DpapiXmlDecryptor", (string?)encrypted.Attribute("decryptorType"), StringComparison.Ordinal);
            byte[] blob = Convert.FromBase64String(encrypted.Descendants().Single(e => e.Name.LocalName == "value").Value.Trim());
            Assert.True(blob.AsSpan().StartsWith(dpapiProvider), "the wrapped key is not a DPAPI blob");
        }

        // DPAPI is the one protection that needs nothing configured to read back: the framework names its decryptor in the key
        // file and DPAPI itself supplies the key (the account's), so a same-account read with no protection set up succeeds. The
        // other protections (key-encryption key, certificate, Keychain) are the ones whose ring must stay unreadable without theirs.
        Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, protection: null));
    }

    private static KeyRingProtectionResolver Resolver(string data, KeyRingConfig config, KeyRingPlatform platform, params (string Name, string Value)[] variables)
    {
        return new KeyRingProtectionResolver(config, data, platform, KeyRingTestSupport.Environment(variables));
    }

    private static byte[] NewCertificate(string? password, bool includePrivateKey)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Draughtsman key ring test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        if (includePrivateKey)
        {
            return certificate.Export(X509ContentType.Pfx, password);
        }

        using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        return publicOnly.Export(X509ContentType.Pfx);
    }

    [UnsupportedOSPlatform("windows")]
    private static void SetMode(string path, bool owner)
    {
        File.SetUnixFileMode(path, owner ? UnixFileMode.UserRead | UnixFileMode.UserWrite : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    private string Track(string folder)
    {
        _folders.Add(folder);
        return folder;
    }

    private sealed class FixedSource(string kek) : IKeyEncryptionKeySource
    {
        public string Description => "a fixed test key";

        public KeyEncryptionKeyLookup Load()
        {
            return new KeyEncryptionKeyLookup(KeyEncryptionKey.Parse(kek), false, []);
        }
    }
}

/// <summary>Reported Skipped, not Passed, off macOS: the test drives the real Keychain through <c>/usr/bin/security</c>.</summary>
internal sealed class RequiresMacOSFactAttribute : FactAttribute
{
    public RequiresMacOSFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "Needs the macOS Keychain.";
        }
    }
}

/// <summary>Reported Skipped, not Passed, off Windows: the test drives the real Windows DPAPI.</summary>
internal sealed class RequiresWindowsFactAttribute : FactAttribute
{
    public RequiresWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs Windows DPAPI.";
        }
    }
}
