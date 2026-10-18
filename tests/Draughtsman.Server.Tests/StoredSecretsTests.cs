using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Draughtsman.Server.Ai;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Email;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// A stored secret (the AI API key, the saved SMTP password) is bound to <see cref="DataProtectionIdentity.ApplicationName"/>, not to the folder the
/// program runs from. An earlier build bound it to the content root, so every secret it wrote is read through a read-only fallback over the install
/// paths this build can guess, then re-protected under the pinned name with a backup of the old bytes. Each rule is paired with the case that
/// shows it could fail: a path nobody knows, a damaged value, a ring that cannot be opened, a write that does not read back.
/// </summary>
public sealed class StoredSecretsTests : IDisposable
{
    private const string Smtp = "THROWAWAY-smtp-password-for-tests-0123456789";

    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            KeyRingTestSupport.DeleteFolder(folder);
        }
    }

    private string Track(string folder)
    {
        _folders.Add(folder);
        return folder;
    }

    private string NewRoot()
    {
        return Track(KeyRingTestSupport.NewFolder());
    }

    /// <summary>An old-world data folder: a plain ring, the AI key and optionally an SMTP password, both bound to <paramref name="discriminator"/> as an earlier build bound them to its content root.</summary>
    private string OldWorld(string discriminator, string? aiKey = KeyRingTestSupport.AiKey, string? smtp = null)
    {
        string data = Track(KeyRingTestSupport.OldWorld(discriminator, aiKey));
        if (smtp is not null)
        {
            using ServiceProvider provider = KeyRingTestSupport.Provider(data, discriminator, protection: null);
            new EmailSettingsStore(new DataFolder(data), provider.GetRequiredService<IDataProtectionProvider>(), NullLogger<EmailSettingsStore>.Instance)
                .Save(new EmailOverrides("smtp", "from@example.invalid", null, "mail.example.invalid", 587, "starttls", "user", null), smtp);
        }

        return data;
    }

    private static byte[] Settings(string data, string folder = "ai")
    {
        return File.ReadAllBytes(Path.Combine(data, folder, "settings.json"));
    }

    private static string[] Backups(string data)
    {
        string folder = Path.Combine(data, "backups");
        return Directory.Exists(folder) ? Directory.GetDirectories(folder, SecretsBackup.Prefix + "*") : [];
    }

    /// <summary>The secrets as the framework opens them under the pinned name and nothing else: no fallback, no install path.</summary>
    private static (string? Ai, string? Smtp) Strict(string data, KeyRingProtection? protection = null)
    {
        using ServiceProvider provider = KeyRingTestSupport.Provider(data, DataProtectionIdentity.ApplicationName, protection);
        IDataProtectionProvider framework = provider.GetRequiredService<IDataProtectionProvider>();
        return (
            new AiSettingsStore(new DataFolder(data), framework, NullLogger<AiSettingsStore>.Instance).Load().ApiKey,
            new EmailSettingsStore(new DataFolder(data), framework, NullLogger<EmailSettingsStore>.Instance).Load().Password);
    }

    private static void AssertNoSecretLeaked(StoredSecretsRig rig, params string[] secrets)
    {
        string everything = rig.Log.All + string.Join('\n', rig.Secrets.Select(s => s.Message));
        foreach (string secret in secrets)
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------------------------------------------ re-protection

    [Fact]
    public void A_secret_bound_to_the_content_root_is_re_protected_under_the_pinned_name_and_the_old_bytes_are_kept()
    {
        string root = "/srv/draughtsman-old/";
        string data = OldWorld(root, smtp: Smtp);
        byte[] aiBefore = Settings(data);
        byte[] emailBefore = Settings(data, "email");
        Assert.Equal((null, null), Strict(data));

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, root);

        Assert.Equal("reprotected", rig.Secret("AI API key").State);
        Assert.Equal("reprotected", rig.Secret("SMTP password").State);
        Assert.Equal((KeyRingTestSupport.AiKey, Smtp), Strict(data));
        Assert.NotEqual(aiBefore, Settings(data));
        string backup = Assert.Single(Backups(data));
        Assert.Equal(aiBefore, File.ReadAllBytes(Path.Combine(backup, "ai", "settings.json")));
        Assert.Equal(emailBefore, File.ReadAllBytes(Path.Combine(backup, "email", "settings.json")));
        Assert.Contains("backups/secrets-", rig.Secret("AI API key").Message, StringComparison.Ordinal);
        AssertNoSecretLeaked(rig, KeyRingTestSupport.AiKey, Smtp);
    }

    [Fact]
    public void Re_protection_changes_only_the_protected_value_and_keeps_everything_else_in_the_file()
    {
        string root = "/srv/draughtsman-old/";
        string data = OldWorld(root);
        string path = Path.Combine(data, "ai", "settings.json");
        JsonNode file = JsonNode.Parse(File.ReadAllText(path))!;
        file["addedByANewerBuild"] = JsonNode.Parse("{\"keep\":[1,2,3]}");
        File.WriteAllText(path, file.ToJsonString());
        string oldPayload = (string)file["protectedApiKey"]!;

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, root);

        JsonNode after = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("[1,2,3]", after["addedByANewerBuild"]!["keep"]!.ToJsonString());
        Assert.Equal("openai", (string)after["provider"]!);
        Assert.NotEqual(oldPayload, (string)after["protectedApiKey"]!);
    }

    [Fact]
    public void Starting_again_after_re_protection_changes_nothing_and_takes_no_second_backup()
    {
        string root = "/srv/draughtsman-old/";
        string data = OldWorld(root);
        using (StoredSecretsRig.Start(data, root))
        {
        }

        byte[] after = Settings(data);
        using StoredSecretsRig second = StoredSecretsRig.Start(data, "/somewhere/else/");

        Assert.Equal("current", second.Secret("AI API key").State);
        Assert.Equal(after, Settings(data));
        Assert.Single(Backups(data));
    }

    [Fact]
    public void A_secret_written_by_this_build_is_current_from_the_start_and_needs_no_backup()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        using (StoredSecretsRig first = StoredSecretsRig.Start(data, "/srv/one/"))
        {
            first.Ai.Save(new AiOverrides("openai", null, null, null), KeyRingTestSupport.AiKey);
        }

        using StoredSecretsRig moved = StoredSecretsRig.Start(data, "/srv/two/");

        Assert.Equal("current", moved.Secret("AI API key").State);
        Assert.Equal(KeyRingTestSupport.AiKey, moved.Ai.Load().ApiKey);
        Assert.Empty(Backups(data));
        Assert.Equal(KeyRingTestSupport.AiKey, Strict(data).Ai);
    }

    // ------------------------------------------------------------------------------------------------------ the guesses

    [Fact]
    public void The_trailing_separator_does_not_matter_in_either_direction()
    {
        string withSlash = OldWorld("/srv/with-slash/");
        string without = OldWorld("/srv/without-slash");

        using StoredSecretsRig a = StoredSecretsRig.Start(withSlash, "/srv/with-slash");
        using StoredSecretsRig b = StoredSecretsRig.Start(without, "/srv/without-slash/");

        Assert.Equal("reprotected", a.Secret("AI API key").State);
        Assert.Equal("reprotected", b.Secret("AI API key").State);
    }

    [Fact]
    public void An_old_folder_named_in_draughtsman_yaml_or_the_environment_is_tried()
    {
        string old = "/opt/draughtsman-0.1.0/";
        string viaYaml = OldWorld(old);
        string viaEnvironment = OldWorld(old);
        string untold = OldWorld(old);

        using StoredSecretsRig yaml = StoredSecretsRig.Start(viaYaml, "/opt/elsewhere/", new KeyRingConfig { Protection = "none", LegacyInstallPaths = ["/opt/not-it", "/opt/draughtsman-0.1.0"] });
        using StoredSecretsRig environment = StoredSecretsRig.Start(viaEnvironment, "/opt/elsewhere/", environment: KeyRingTestSupport.Environment((LegacyDiscriminators.HintVariable, string.Join(Path.PathSeparator, "/opt/not-it", old))));
        using StoredSecretsRig none = StoredSecretsRig.Start(untold, "/opt/elsewhere/");

        Assert.Equal("reprotected", yaml.Secret("AI API key").State);
        Assert.Equal("reprotected", environment.Secret("AI API key").State);
        Assert.Equal("unreadable", none.Secret("AI API key").State);
    }

    [Fact]
    public void The_folder_the_data_folder_sits_in_is_tried_the_default_layout_keeps_data_inside_the_install()
    {
        string install = NewRoot();
        string data = Path.Combine(install, "data");
        KeyRingTestSupport.CopyDirectory(OldWorld(install + Path.DirectorySeparatorChar), data);

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, "/somewhere/else/");

        Assert.Equal("reprotected", rig.Secret("AI API key").State);
    }

    [Fact]
    public void A_folder_beside_this_install_is_tried_when_the_new_version_was_unpacked_next_to_the_old_one()
    {
        string parent = NewRoot();
        string oldInstall = Path.Combine(parent, "draughtsman-0.1.0-win-x64");
        string newInstall = Path.Combine(parent, "draughtsman-0.2.0-win-x64");
        Directory.CreateDirectory(oldInstall);
        Directory.CreateDirectory(newInstall);
        string data = OldWorld(oldInstall + Path.DirectorySeparatorChar);

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, newInstall + Path.DirectorySeparatorChar);

        Assert.Equal("reprotected", rig.Secret("AI API key").State);
        Assert.Equal(KeyRingTestSupport.AiKey, Strict(data).Ai);
    }

    [Fact]
    public void A_content_root_an_earlier_run_logged_is_tried_last()
    {
        string old = "/srv/logged-install/";
        string data = OldWorld(old);
        Directory.CreateDirectory(Path.Combine(data, "logs"));
        File.WriteAllText(
            Path.Combine(data, "logs", "draughtsman-20260901.log"),
            "2026-09-01T10:00:00.000Z info Microsoft.Hosting.Lifetime: Application started. Press Ctrl+C to shut down.\n" +
            "2026-09-01T10:00:00.001Z info Microsoft.Hosting.Lifetime: Content root path: " + old + "\n");

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, "/srv/another-place/");

        Assert.Equal("reprotected", rig.Secret("AI API key").State);
    }

    [Fact]
    public void A_path_nobody_can_guess_is_reported_never_altered_and_the_message_says_what_to_do()
    {
        string data = OldWorld("/srv/the-folder-nobody-remembers/", smtp: Smtp);
        byte[] aiBefore = Settings(data);
        byte[] emailBefore = Settings(data, "email");
        Dictionary<string, byte[]> ringBefore = KeyRingTestSupport.Bytes(Path.Combine(data, "keys"), "*.xml");

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, "/srv/this-install/");

        foreach (string name in new[] { "AI API key", "SMTP password" })
        {
            StoredSecretState state = rig.Secret(name);
            Assert.Equal("unreadable", state.State);
            Assert.Contains(name, state.Message, StringComparison.Ordinal);
            Assert.Contains("keyRing.legacyInstallPaths", state.Message, StringComparison.Ordinal);
            Assert.Contains(LegacyDiscriminators.HintVariable, state.Message, StringComparison.Ordinal);
            Assert.Contains(name == "AI API key" ? "Admin > AI" : "Admin > Email", state.Message, StringComparison.Ordinal);
        }

        // Nothing is dropped, rewritten or backed up: the ciphertext is exactly where it was, for whoever names the folder.
        Assert.Equal(aiBefore, Settings(data));
        Assert.Equal(emailBefore, Settings(data, "email"));
        Assert.Empty(Backups(data));
        Assert.Equal(ringBefore, KeyRingTestSupport.Bytes(Path.Combine(data, "keys"), "*.xml"));
        Assert.True(rig.Ai.Load().KeyUnreadable);
        Assert.Contains("Stored secrets:", rig.Log.All, StringComparison.Ordinal);
        AssertNoSecretLeaked(rig, KeyRingTestSupport.AiKey, Smtp);
    }

    [Fact]
    public void The_late_guesses_that_read_the_disk_are_worked_out_only_for_a_secret_the_first_choices_cannot_open()
    {
        string data = OldWorld("/srv/first-choice/");
        string keys = Path.Combine(data, "keys");
        int late = 0;
        IReadOnlyList<LegacyCandidate> Counting()
        {
            late++;
            return [];
        }

        using ServiceProvider real = KeyRingTestSupport.Provider(data, DataProtectionIdentity.ApplicationName, protection: null, createKeys: true);
        using var reader = new LegacySecretReader(keys, new NoKeyRingProtection(UnprotectedReason.Disabled, "test"), LegacyDiscriminators.ForContentRoot("/srv/first-choice/"), Counting);
        var ai = new AiSettingsStore(new DataFolder(data), real.GetRequiredService<IDataProtectionProvider>(), NullLogger<AiSettingsStore>.Instance, reader);
        var email = new EmailSettingsStore(new DataFolder(data), real.GetRequiredService<IDataProtectionProvider>(), NullLogger<EmailSettingsStore>.Instance, reader);

        // A secret the first choices open, and then one this build already protects: neither reads old logs or neighbouring folders.
        new StoredSecretsMigration(new DataFolder(data), ai, email).Run(new KeyRingTestSupport.CapturingLogger());
        new StoredSecretsMigration(new DataFolder(data), ai, email).Run(new KeyRingTestSupport.CapturingLogger());
        Assert.Equal(0, late);

        // A secret nothing opens: now they are.
        string other = OldWorld("/srv/nobody-knows/");
        File.Copy(Path.Combine(other, "ai", "settings.json"), Path.Combine(data, "ai", "settings.json"), overwrite: true);
        new StoredSecretsMigration(new DataFolder(data), ai, email).Run(new KeyRingTestSupport.CapturingLogger());
        Assert.True(late >= 1);
    }

    // ------------------------------------------------------------------------------------------------------ the record

    [Fact]
    public void A_start_records_the_content_root_once_per_path_and_a_later_move_still_finds_the_path_a_secret_was_written_under()
    {
        string first = "/srv/install-one/";
        string data = OldWorld(first);
        var none = new KeyRingConfig { Protection = "none" };

        // The first start of this build, from where the old build ran. It records the path but, for the sake of the test, does not get as far as
        // re-protecting the secret (a start that stopped first: a read-only file, a crash). The record is all that survives.
        KeyRingTestSupport.Startup(data, none, discriminator: first);
        string record = File.ReadAllText(Path.Combine(data, "keys", LegacyDiscriminators.RecordFileName));
        Assert.Contains(first, record, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyRingTestSupport.AiKey, record, StringComparison.Ordinal);

        // The same path again adds nothing; another path is added after it.
        KeyRingTestSupport.Startup(data, none, discriminator: first);
        Assert.Equal(record, File.ReadAllText(Path.Combine(data, "keys", LegacyDiscriminators.RecordFileName)));
        KeyRingTestSupport.Startup(data, none, discriminator: "/srv/install-two/");
        LegacyDiscriminators.InstallRecord written = LegacyDiscriminators.ReadRecord(data);
        Assert.Equal([first, "/srv/install-two/"], written.ContentRoots.Select(r => r.Path));

        // The program is now somewhere nothing else points at; only the record can say where the secret was bound.
        using StoredSecretsRig moved = StoredSecretsRig.Start(data, "/srv/a-third-place/");
        Assert.Equal("reprotected", moved.Secret("AI API key").State);
        Assert.Contains("recorded", moved.Secret("AI API key").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_or_unwritable_record_never_stops_the_start()
    {
        string data = OldWorld("/srv/x/");
        File.WriteAllText(Path.Combine(data, "keys", LegacyDiscriminators.RecordFileName), "{ not json");
        using StoredSecretsRig damaged = StoredSecretsRig.Start(data, "/srv/x/");
        Assert.Equal("reprotected", damaged.Secret("AI API key").State);
        Assert.Contains("/srv/x/", File.ReadAllText(Path.Combine(data, "keys", LegacyDiscriminators.RecordFileName)), StringComparison.Ordinal);

        // A path where the keys folder cannot be created: the record is skipped with a warning and the start goes on.
        string blocked = Track(KeyRingTestSupport.NewFolder());
        File.WriteAllText(Path.Combine(blocked, "keys"), "a file where the ring folder should be");
        var log = new KeyRingTestSupport.CapturingLogger();
        LegacyDiscriminators.Record(blocked, "/srv/y/", log);
        Assert.Contains("could not be recorded", log.All, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------ damage

    [Theory]
    [InlineData("truncated")]
    [InlineData("not-base64")]
    [InlineData("tampered")]
    public void A_corrupted_stored_value_is_reported_by_name_never_altered_and_never_stops_the_start(string damage)
    {
        string data = OldWorld("/srv/damaged/");
        string path = Path.Combine(data, "ai", "settings.json");
        JsonNode file = JsonNode.Parse(File.ReadAllText(path))!;
        string payload = (string)file["protectedApiKey"]!;
        file["protectedApiKey"] = damage switch
        {
            "truncated" => payload[..12],
            "not-base64" => "!!! not a protected value !!!",
            _ => payload[..40] + (payload[40] == 'A' ? 'B' : 'A') + payload[41..],
        };
        File.WriteAllText(path, file.ToJsonString());
        byte[] before = File.ReadAllBytes(path);

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, "/srv/damaged/");

        StoredSecretState state = rig.Secret("AI API key");
        Assert.Equal("unreadable", state.State);
        Assert.Contains("AI API key", state.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Backups(data));
        Assert.True(rig.Ai.Load().KeyUnreadable);
        if (damage == "not-base64" || damage == "truncated")
        {
            Assert.Contains("damaged", state.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_ring_with_no_stored_secrets_is_left_alone_and_nothing_is_backed_up()
    {
        // A ring that once protected something and now protects nothing: the key stays, the settings file goes.
        string data = OldWorld("/srv/none/");
        Directory.Delete(Path.Combine(data, "ai"), recursive: true);
        Dictionary<string, byte[]> ringBefore = KeyRingTestSupport.Bytes(Path.Combine(data, "keys"), "*.xml");

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, "/srv/elsewhere/");

        Assert.Empty(rig.Secrets);
        Assert.Equal(ringBefore, KeyRingTestSupport.Bytes(Path.Combine(data, "keys"), "*.xml"));
        Assert.Empty(Backups(data));
        Assert.False(Directory.Exists(Path.Combine(data, "ai")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(data, "keys"), "*.xml"));
    }

    [Fact]
    public void An_empty_data_folder_starts_and_records_where_it_ran()
    {
        string data = Track(KeyRingTestSupport.NewFolder());

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, "/srv/fresh/");

        Assert.Empty(rig.Secrets);
        Assert.Contains("/srv/fresh/", File.ReadAllText(Path.Combine(data, "keys", LegacyDiscriminators.RecordFileName)), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------ the write

    [Fact]
    public void A_re_protection_that_does_not_read_back_puts_the_old_file_back_and_the_secret_still_works()
    {
        string data = OldWorld("/srv/old/", smtp: Smtp);
        byte[] aiBefore = Settings(data);
        byte[] emailBefore = Settings(data, "email");
        string keys = Path.Combine(data, "keys");
        using ServiceProvider real = KeyRingTestSupport.Provider(data, DataProtectionIdentity.ApplicationName, protection: null, createKeys: true);
        var broken = new BrokenProvider(real.GetRequiredService<IDataProtectionProvider>());
        using var legacy = new LegacySecretReader(keys, new NoKeyRingProtection(UnprotectedReason.Disabled, "test"), LegacyDiscriminators.ForContentRoot("/srv/old/"));
        var ai = new AiSettingsStore(new DataFolder(data), broken, NullLogger<AiSettingsStore>.Instance, legacy);
        var email = new EmailSettingsStore(new DataFolder(data), broken, NullLogger<EmailSettingsStore>.Instance, legacy);
        var log = new KeyRingTestSupport.CapturingLogger();

        IReadOnlyList<StoredSecretState> states = new StoredSecretsMigration(new DataFolder(data), ai, email).Run(log);

        Assert.All(states, s => Assert.Equal("legacy", s.State));
        Assert.Contains("did not read back", states[0].Message, StringComparison.Ordinal);
        Assert.Equal(aiBefore, Settings(data));
        Assert.Equal(emailBefore, Settings(data, "email"));
        Assert.Single(Backups(data));
        Assert.Contains("could not be re-protected", log.All, StringComparison.Ordinal);
        // Still readable, through the fallback, exactly as before.
        Assert.Equal(KeyRingTestSupport.AiKey, ai.Load().ApiKey);
    }

    [Fact]
    public void The_replacement_is_published_by_one_rename_a_crash_before_it_leaves_the_old_file_whole()
    {
        string data = OldWorld("/srv/old/");
        string path = Path.Combine(data, "ai", "settings.json");
        byte[] before = File.ReadAllBytes(path);
        bool sawFinishedNewFile = false;
        var file = new ProtectedSecretFile(path, "protectedApiKey")
        {
            BeforePublish = () =>
            {
                // The complete new file exists beside the old one, and the old one has not been touched.
                sawFinishedNewFile = File.Exists(path + ".tmp") && File.ReadAllText(path + ".tmp").Contains("replacement-payload", StringComparison.Ordinal);
                Assert.Equal(before, File.ReadAllBytes(path));
                throw new IOException("the machine went away here");
            },
        };

        Assert.Throws<IOException>(() => file.ReplacePayload("replacement-payload"));

        Assert.True(sawFinishedNewFile);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void An_unwritable_settings_folder_leaves_the_file_as_it_was_and_the_secret_readable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string data = OldWorld("/srv/old/");
        string folder = Path.Combine(data, "ai");
        byte[] before = Settings(data);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using StoredSecretsRig rig = StoredSecretsRig.Start(data, "/srv/old/");

            Assert.Equal("legacy", rig.Secret("AI API key").State);
            Assert.Equal(before, Settings(data));
            Assert.Equal(KeyRingTestSupport.AiKey, rig.Ai.Load().ApiKey);
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ------------------------------------------------------------------------------------------------------ the key encryption layer

    [Fact]
    public void A_ring_protected_by_a_key_encryption_key_opens_unchanged_and_the_secret_is_re_protected()
    {
        string kek = KeyRingTestSupport.NewKekText();
        string data = ProtectedOldWorld(KeyRingTestSupport.Kek(kek), "/srv/old/");
        Dictionary<string, byte[]> ringBefore = KeyRingTestSupport.Bytes(Path.Combine(data, "keys"), "*.xml");
        Assert.Empty(KeyRingTestSupport.PlainMasterKeys(Path.Combine(data, "keys")));

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, "/srv/new/", new KeyRingConfig { Protection = "key", LegacyInstallPaths = ["/srv/old/"] }, KeyRingTestSupport.Environment(("DRAUGHTSMAN_KEY_ENCRYPTION_KEY", kek)));

        Assert.Equal("reprotected", rig.Secret("AI API key").State);
        Assert.Equal(ringBefore, KeyRingTestSupport.Bytes(Path.Combine(data, "keys"), "*.xml"));
        Assert.Equal(KeyRingTestSupport.AiKey, Strict(data, KeyRingTestSupport.Kek(kek)).Ai);
        Assert.Equal("protected", rig.Status.Describe().State);
    }

    [Fact]
    public void With_the_key_encryption_key_missing_the_secret_is_reported_as_the_ring_s_problem_not_a_path_and_nothing_changes()
    {
        string kek = KeyRingTestSupport.NewKekText();
        string data = ProtectedOldWorld(KeyRingTestSupport.Kek(kek), "/srv/old/");
        byte[] before = Settings(data);

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, "/srv/old/", new KeyRingConfig { Protection = "key" }, KeyRingTestSupport.Environment(("DRAUGHTSMAN_KEY_ENCRYPTION_KEY", KeyRingTestSupport.NewKekText())));

        StoredSecretState state = rig.Secret("AI API key");
        Assert.Equal("unreadable", state.State);
        Assert.Contains("key ring", state.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("legacyInstallPaths", state.Message, StringComparison.Ordinal);
        Assert.Equal(before, Settings(data));

        // Putting the key back brings the secret back: nothing was lost.
        using StoredSecretsRig again = StoredSecretsRig.Start(data, "/srv/old/", new KeyRingConfig { Protection = "key" }, KeyRingTestSupport.Environment(("DRAUGHTSMAN_KEY_ENCRYPTION_KEY", kek)));
        Assert.Equal("reprotected", again.Secret("AI API key").State);
    }

    [Fact]
    public void A_ring_protected_by_the_keychain_opens_and_the_secret_is_re_protected()
    {
        string kek = KeyRingTestSupport.NewKekText();
        string data = ProtectedOldWorld(KeyRingTestSupport.Kek(kek), "/srv/old/");

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, "/srv/old/", new KeyRingConfig { Protection = "keychain" }, platform: KeyRingPlatform.MacOS, keychain: _ => new FixedSource(kek));

        Assert.Equal("reprotected", rig.Secret("AI API key").State);
        Assert.Equal(KeyRingTestSupport.AiKey, Strict(data, KeyRingTestSupport.Kek(kek)).Ai);
        Assert.Equal(KeyRingConfig.ProtectionKeychain, rig.Status.Protection.Kind);
    }

    [Fact]
    public void A_ring_protected_by_a_certificate_opens_and_the_secret_is_re_protected()
    {
        string folder = NewRoot();
        string pfx = Path.Combine(folder, "ring.pfx");
        File.WriteAllBytes(pfx, NewCertificate("ring-password"));
        var config = new KeyRingConfig { Protection = "certificate", Certificate = new KeyRingCertificateConfig { Path = pfx } };
        Func<string, string?> environment = KeyRingTestSupport.Environment((config.Certificate.PasswordEnvironmentVariable, "ring-password"));
        string data = Track(KeyRingTestSupport.NewFolder());
        using (StoredSecretsRig written = StoredSecretsRig.Start(data, "/srv/old/", config, environment))
        {
            // An earlier build: the secret bound to its content root.
            using ServiceProvider old = KeyRingTestSupport.Provider(data, "/srv/old/", written.Status.Protection, createKeys: true);
            new AiSettingsStore(new DataFolder(data), old.GetRequiredService<IDataProtectionProvider>(), NullLogger<AiSettingsStore>.Instance).Save(new AiOverrides("openai", null, null, null), KeyRingTestSupport.AiKey);
        }

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, "/srv/old/", config, environment);

        Assert.Equal("reprotected", rig.Secret("AI API key").State);
        Assert.Equal(KeyRingTestSupport.AiKey, rig.Ai.Load().ApiKey);
    }

    [Fact]
    public void A_ring_protected_by_windows_dpapi_opens_and_the_secret_is_re_protected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string data = Track(KeyRingTestSupport.NewFolder());
        var config = new KeyRingConfig { Protection = "dpapi" };
        using (StoredSecretsRig written = StoredSecretsRig.Start(data, "C:\\old\\", config, platform: KeyRingPlatform.Windows))
        {
            using ServiceProvider old = KeyRingTestSupport.Provider(data, "C:\\old\\", written.Status.Protection, createKeys: true);
            new AiSettingsStore(new DataFolder(data), old.GetRequiredService<IDataProtectionProvider>(), NullLogger<AiSettingsStore>.Instance).Save(new AiOverrides("openai", null, null, null), KeyRingTestSupport.AiKey);
        }

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, "C:\\old\\", config, platform: KeyRingPlatform.Windows);

        Assert.Equal("reprotected", rig.Secret("AI API key").State);
        Assert.Equal(KeyRingTestSupport.AiKey, rig.Ai.Load().ApiKey);
    }

    [Fact]
    public void The_docker_image_keeps_its_content_root_at_app_so_an_image_upgrade_never_needs_a_path_and_the_next_move_needs_none_either()
    {
        string data = OldWorld("/app/");

        using (StoredSecretsRig image = StoredSecretsRig.Start(data, "/app/"))
        {
            Assert.Equal("reprotected", image.Secret("AI API key").State);
        }

        using StoredSecretsRig elsewhere = StoredSecretsRig.Start(data, "/srv/not-docker/");
        Assert.Equal("current", elsewhere.Secret("AI API key").State);
        Assert.Equal(KeyRingTestSupport.AiKey, Strict(data).Ai);
    }

    [Fact]
    public void A_secret_whose_key_is_not_in_the_ring_is_reported_as_the_ring_s_problem()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        Directory.CreateDirectory(Path.Combine(data, "ai"));
        File.Copy(Path.Combine(OldWorld("/srv/other/"), "ai", "settings.json"), Path.Combine(data, "ai", "settings.json"));

        using StoredSecretsRig rig = StoredSecretsRig.Start(data, "/srv/other/");

        Assert.Equal("unreadable", rig.Secret("AI API key").State);
        Assert.Contains("key ring", rig.Secret("AI API key").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_legacy_reader_only_reads_it_never_adds_a_key_to_an_empty_ring()
    {
        string data = OldWorld("/srv/other/");
        string empty = Track(KeyRingTestSupport.NewFolder());
        string payload = (string)JsonNode.Parse(File.ReadAllText(Path.Combine(data, "ai", "settings.json")))!["protectedApiKey"]!;
        using var reader = new LegacySecretReader(Path.Combine(empty, "keys"), new NoKeyRingProtection(UnprotectedReason.Disabled, "test"), LegacyDiscriminators.ForContentRoot("/srv/other/"));

        Assert.Null(reader.TryUnprotect("Draughtsman.Server.Ai.ApiKey.v1", payload));
        Assert.Empty(reader.ReadableKeys);

        Assert.Empty(Directory.Exists(Path.Combine(empty, "keys")) ? Directory.GetFiles(Path.Combine(empty, "keys")) : []);
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    private string ProtectedOldWorld(KeyRingProtection protection, string discriminator)
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        using ServiceProvider provider = KeyRingTestSupport.Provider(data, discriminator, protection, createKeys: true);
        new AiSettingsStore(new DataFolder(data), provider.GetRequiredService<IDataProtectionProvider>(), NullLogger<AiSettingsStore>.Instance)
            .Save(new AiOverrides("openai", "gpt-4o-mini", null, null), KeyRingTestSupport.AiKey);
        return data;
    }

    private static byte[] NewCertificate(string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Draughtsman stored secrets test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.Export(X509ContentType.Pfx, password);
    }

    private sealed class FixedSource(string kek) : IKeyEncryptionKeySource
    {
        public string Description => "a fixed test key";

        public KeyEncryptionKeyLookup Load()
        {
            return new KeyEncryptionKeyLookup(KeyEncryptionKey.Parse(kek), false, []);
        }
    }

    /// <summary>A provider whose protectors write something under another purpose, so a value this build "protects" cannot be read back by this build.</summary>
    private sealed class BrokenProvider(IDataProtectionProvider inner) : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose)
        {
            return new BrokenProtector(inner.CreateProtector(purpose), inner.CreateProtector("a different purpose"));
        }
    }

    private sealed class BrokenProtector(IDataProtector real, IDataProtector other) : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose)
        {
            return new BrokenProtector(real.CreateProtector(purpose), other.CreateProtector(purpose));
        }

        public byte[] Protect(byte[] plaintext)
        {
            return other.Protect(plaintext);
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            return real.Unprotect(protectedData);
        }
    }
}
