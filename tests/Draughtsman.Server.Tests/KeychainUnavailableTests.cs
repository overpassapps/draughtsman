using System.Diagnostics;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;

namespace Draughtsman.Server.Tests;

/// <summary>
/// A Mac whose Keychain cannot be used (no login keychain: a service account, a missing home folder) used to hang
/// forever in startup, because the <c>security</c> tool's output was read before its timeout applied. It must now give
/// up in bounded time and stop the server with a message naming the two safe choices, never carry on with a ring that
/// should be protected left as plain text. Each rule has its opposite beside it: a working tool still yields its key, a
/// working Keychain still protects and re-opens a ring, and a deliberate opt-out still starts.
/// </summary>
public class KeychainUnavailableTests : IDisposable
{
    private readonly List<string> _folders = [];

    public void Dispose()
    {
        foreach (string folder in _folders)
        {
            KeyRingTestSupport.DeleteFolder(folder);
        }
    }

    [RequiresUnixFact]
    public void A_security_tool_that_hangs_is_given_up_on_in_bounded_time()
    {
        // The shape seen on a Mac with HOME=/nonexistent: the lookup says "not found", then the add (security -i) never returns.
        string tool = FakeTool("""
            case "$1" in
              find-generic-password) exit 44 ;;
              -i) cat > /dev/null; sleep 600 ;;
            esac
            """);
        var source = new MacKeychainKeySource("account", "test service", tool, TimeSpan.FromSeconds(1.5));

        var load = Task.Run(source.Load);
        bool finished = Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(30))).GetAwaiter().GetResult() == load;

        Assert.True(finished, "Load() was still blocked after 30 seconds: the security tool's output is being read with no time limit.");
        KeyEncryptionKeyException error = Assert.Throws<KeyEncryptionKeyException>(() => load.GetAwaiter().GetResult());
        Assert.Contains("did not answer", error.Message, StringComparison.Ordinal);
    }

    [RequiresUnixFact]
    public void A_security_tool_that_answers_still_yields_its_key_and_one_that_fails_says_so()
    {
        string kek = KeyRingTestSupport.NewKekText();
        string working = FakeTool($"""
            case "$1" in
              find-generic-password) echo '{kek}'; exit 0 ;;
            esac
            """);
        var source = new MacKeychainKeySource("account", "test service", working, TimeSpan.FromSeconds(20));
        KeyEncryptionKeyLookup found = source.Load();
        Assert.False(found.Created);
        Assert.Equal(KeyEncryptionKey.Parse(kek), found.Key);

        // A locked keychain (anything but "not found") is not a reason to mint a new key.
        string locked = FakeTool("exit 36");
        var lockedSource = new MacKeychainKeySource("account", "test service", locked, TimeSpan.FromSeconds(20));
        KeyEncryptionKeyException error = Assert.Throws<KeyEncryptionKeyException>(lockedSource.Load);
        Assert.Contains("status 36", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unusable_keychain_stops_the_resolver_with_both_safe_choices_named_while_a_working_one_resolves()
    {
        string data = Track(KeyRingTestSupport.NewFolder());

        var resolver = new KeyRingProtectionResolver(new KeyRingConfig(), data, KeyRingPlatform.MacOS, _ => null, _ => new FailingSource("the security tool did not answer."));
        KeyRingProtectionRequiredException stop = Assert.Throws<KeyRingProtectionRequiredException>(resolver.Resolve);
        Assert.Contains("did not answer", stop.Message, StringComparison.Ordinal);
        Assert.Contains("DRAUGHTSMAN_KEY_ENCRYPTION_KEY", stop.Message, StringComparison.Ordinal);
        Assert.Contains("keyRing.keyFile", stop.Message, StringComparison.Ordinal);
        Assert.Contains("keyRing.protection: none", stop.Message, StringComparison.Ordinal);
        Assert.Contains("DRAUGHTSMAN_KEY_RING_PROTECTION=none", stop.Message, StringComparison.Ordinal);

        // The same explicit setting stops too, not only auto.
        Assert.Throws<KeyRingProtectionRequiredException>(() => new KeyRingProtectionResolver(new KeyRingConfig { Protection = "keychain" }, data, KeyRingPlatform.MacOS, _ => null, _ => new FailingSource("locked.")).Resolve());

        // A working Keychain resolves as it always did.
        Assert.Equal(KeyRingConfig.ProtectionKeychain, new KeyRingProtectionResolver(new KeyRingConfig(), data, KeyRingPlatform.MacOS, _ => null, _ => new FixedSource(KeyRingTestSupport.NewKekText())).Resolve().Kind);

        // A key-encryption key (one of the two named choices) never touches the Keychain at all.
        Func<string, IKeyEncryptionKeySource> mustNotBeAsked = _ => throw new InvalidOperationException("the Keychain was asked");
        KeyRingProtection withKey = new KeyRingProtectionResolver(new KeyRingConfig(), data, KeyRingPlatform.MacOS, KeyRingTestSupport.Environment(("DRAUGHTSMAN_KEY_ENCRYPTION_KEY", KeyRingTestSupport.NewKekText())), mustNotBeAsked).Resolve();
        Assert.Equal(KeyRingConfig.ProtectionKey, withKey.Kind);
    }

    [Fact]
    public void Startup_with_an_unusable_keychain_throws_before_touching_the_ring_and_the_opt_outs_and_a_working_keychain_still_start()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        Dictionary<string, byte[]> before = Snapshot(data);

        Assert.Throws<KeyRingProtectionRequiredException>(() => KeyRingTestSupport.Startup(data, platform: KeyRingPlatform.MacOS, keychain: _ => new FailingSource("security exited with status 36.")));

        // No migration, no backup, no quarantine: every file and folder is exactly as it was, and no note of where the program ran.
        Assert.Equal(before.Keys.Order(), Snapshot(data).Keys.Order());
        Assert.Equal(before, Snapshot(data));
        Assert.False(File.Exists(Path.Combine(data, "keys", LegacyDiscriminators.RecordFileName)));

        // Opt-out 1: protection none, in the file or through the environment variable, is honoured and says it is deliberate.
        (KeyRingStatus none, _, _) = KeyRingTestSupport.Startup(data, new KeyRingConfig { Protection = "none" }, platform: KeyRingPlatform.MacOS, keychain: _ => new FailingSource("unused."));
        Assert.Equal("disabled", none.Describe().Reason);
        (KeyRingStatus viaEnvironment, _, _) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KeyRingSetup.ProtectionOverrideVariable, "none")), platform: KeyRingPlatform.MacOS, keychain: _ => new FailingSource("unused."));
        Assert.Equal("disabled", viaEnvironment.Describe().Reason);
        Assert.Equal(before, Snapshot(data));

        // Opt-out 2: a key-encryption key protects the ring without the Keychain.
        (KeyRingStatus keyed, _, _) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment(("DRAUGHTSMAN_KEY_ENCRYPTION_KEY", KeyRingTestSupport.NewKekText())), platform: KeyRingPlatform.MacOS, keychain: _ => new FailingSource("unused."));
        Assert.Equal(KeyRingConfig.ProtectionKey, keyed.Protection.Kind);
    }

    [Fact]
    public void An_existing_ring_protected_through_the_keychain_still_opens_on_the_next_start()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        string kek = KeyRingTestSupport.NewKekText();
        Func<string, IKeyEncryptionKeySource> keychain = _ => new FixedSource(kek);

        (KeyRingStatus first, _, KeyRingTestSupport.CapturingLogger log) = KeyRingTestSupport.Startup(data, platform: KeyRingPlatform.MacOS, keychain: keychain);
        Assert.Equal(KeyRingConfig.ProtectionKeychain, first.Protection.Kind);
        Assert.Equal(KeyRingMigrationOutcome.Migrated, first.Migration.OutcomeOrThrow(log));
        Dictionary<string, byte[]> protectedRing = Snapshot(data);

        (KeyRingStatus second, _, _) = KeyRingTestSupport.Startup(data, platform: KeyRingPlatform.MacOS, keychain: keychain);
        Assert.Equal(KeyRingConfig.ProtectionKeychain, second.Protection.Kind);
        Assert.Equal(0, second.Describe().UnreadableKeys);
        Assert.Equal(KeyRingTestSupport.AiKey, KeyRingTestSupport.ReadAiKey(data, KeyRingTestSupport.Discriminator, second.Protection));
        Assert.Equal(protectedRing, Snapshot(data));

        // And with the Keychain now unusable the protected ring is left exactly as it is: the server refuses to start rather than touch it.
        Assert.Throws<KeyRingProtectionRequiredException>(() => KeyRingTestSupport.Startup(data, platform: KeyRingPlatform.MacOS, keychain: _ => new FailingSource("locked.")));
        Assert.Equal(protectedRing, Snapshot(data));
    }

    private static Dictionary<string, byte[]> Snapshot(string folder)
    {
        // The Keychain account id (KeyRingFiles.IdentityFileName) is the one file a lookup writes first; it is not key material. A start that goes ahead
        // also notes where the program ran (LegacyDiscriminators.RecordFileName), which is no key either; a start that is refused writes neither of ours.
        return Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path) != KeyRingFiles.IdentityFileName && Path.GetFileName(path) != LegacyDiscriminators.RecordFileName)
            .ToDictionary(path => Path.GetRelativePath(folder, path), path => File.Exists(path) ? File.ReadAllBytes(path) : []);
    }

    /// <summary>A shell script standing in for <c>/usr/bin/security</c>, so the hang can be reproduced without a Mac or its Keychain.</summary>
    private string FakeTool(string body)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("A POSIX shell is needed.");
        }

        string folder = Track(KeyRingTestSupport.NewFolder());
        string path = Path.Combine(folder, "security");
        File.WriteAllText(path, "#!/bin/sh\n" + body.ReplaceLineEndings("\n") + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private string Track(string folder)
    {
        _folders.Add(folder);
        return folder;
    }

    private sealed class FailingSource(string reason) : IKeyEncryptionKeySource
    {
        public string Description => "a failing test source";

        public KeyEncryptionKeyLookup Load()
        {
            throw new KeyEncryptionKeyException(reason);
        }
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

/// <summary>Reported Skipped, not Passed, on Windows: the test runs a shell script in place of the macOS <c>security</c> tool.</summary>
internal sealed class RequiresUnixFactAttribute : FactAttribute
{
    public RequiresUnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Needs a POSIX shell.";
        }
    }
}
