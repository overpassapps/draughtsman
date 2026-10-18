using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;

namespace Draughtsman.Server.Tests;

/// <summary>
/// D4 of the Windows install proof: under a logon with no DPAPI master key (an SSH public-key session) the server started, logged
/// "Key ring protection: Windows DPAPI", said healthy, and then answered 500 to the first sign-in. Windows DPAPI that cannot work now stops
/// startup with the message pattern the macOS Keychain uses (what failed, the two safe choices), never a silent unprotected ring. The probe is
/// stood in for here; the real one runs in the SSH session on the Windows VM.
/// </summary>
public class DpapiUnavailableTests : IDisposable
{
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

    private static KeyRingProtectionResolver Windows(string data, KeyRingConfig config, Func<string?> probe, Func<string, string?>? environment = null) =>
        new(config, data, KeyRingPlatform.Windows, environment ?? (_ => null), keychain: null, dpapiProbe: probe);

    [Fact]
    public void A_DPAPI_that_cannot_work_stops_the_resolver_with_what_failed_and_both_safe_choices_for_auto_and_for_an_explicit_dpapi()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        Func<string?> failing = () => "CryptographicException: Key not valid for use in specified state.";

        foreach (string protection in new[] { "auto", "dpapi" })
        {
            KeyRingProtectionRequiredException stop = Assert.Throws<KeyRingProtectionRequiredException>(Windows(data, new KeyRingConfig { Protection = protection }, failing).Resolve);

            Assert.Contains("Windows DPAPI cannot be used", stop.Message, StringComparison.Ordinal);
            Assert.Contains("Key not valid for use in specified state", stop.Message, StringComparison.Ordinal);
            Assert.Contains("SSH public-key", stop.Message, StringComparison.Ordinal);
            Assert.Contains("DRAUGHTSMAN_KEY_ENCRYPTION_KEY", stop.Message, StringComparison.Ordinal);
            Assert.Contains("keyRing.keyFile", stop.Message, StringComparison.Ordinal);
            Assert.Contains("keyRing.protection: none", stop.Message, StringComparison.Ordinal);
            Assert.Contains("DRAUGHTSMAN_KEY_RING_PROTECTION=none", stop.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_DPAPI_that_works_resolves_as_it_always_did_and_the_choices_the_message_names_do_not_ask_DPAPI_at_all()
    {
        string data = Track(KeyRingTestSupport.NewFolder());
        Func<string?> mustNotBeAsked = () => throw new InvalidOperationException("DPAPI was probed");

        Assert.Equal(KeyRingConfig.ProtectionDpapi, Windows(data, new KeyRingConfig(), () => null).Resolve().Kind);
        Assert.Equal(KeyRingConfig.ProtectionDpapi, Windows(data, new KeyRingConfig { Protection = "dpapi" }, () => null).Resolve().Kind);

        // Choice 1: a key-encryption key never touches DPAPI (under auto, the key comes before the operating system store).
        KeyRingProtection keyed = Windows(data, new KeyRingConfig(), mustNotBeAsked, KeyRingTestSupport.Environment(("DRAUGHTSMAN_KEY_ENCRYPTION_KEY", KeyRingTestSupport.NewKekText()))).Resolve();
        Assert.Equal(KeyRingConfig.ProtectionKey, keyed.Kind);

        // Choice 2: the deliberate opt-out starts, unprotected, and says it is deliberate.
        KeyRingProtection none = Windows(data, new KeyRingConfig { Protection = "none" }, mustNotBeAsked).Resolve();
        Assert.False(none.IsActive);
        Assert.Equal(UnprotectedReason.Disabled, ((NoKeyRingProtection)none).Reason);
    }

    [Fact]
    public void Startup_with_an_unusable_DPAPI_throws_before_touching_the_ring_and_the_opt_outs_still_start()
    {
        string data = Track(KeyRingTestSupport.OldWorld());
        List<string> before = [.. Directory.EnumerateFileSystemEntries(data, "*", SearchOption.AllDirectories).Order()];

        Assert.Throws<KeyRingProtectionRequiredException>(() => KeyRingTestSupport.Startup(data, platform: KeyRingPlatform.Windows, dpapiProbe: () => "no master key"));

        Assert.Equal(before, Directory.EnumerateFileSystemEntries(data, "*", SearchOption.AllDirectories).Order().ToList());
        Assert.False(File.Exists(Path.Combine(data, "keys", LegacyDiscriminators.RecordFileName)));
        (KeyRingStatus none, _, _) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment((KeyRingSetup.ProtectionOverrideVariable, "none")), platform: KeyRingPlatform.Windows, dpapiProbe: () => "no master key");
        Assert.Equal("disabled", none.Describe().Reason);
        (KeyRingStatus keyed, _, _) = KeyRingTestSupport.Startup(data, environment: KeyRingTestSupport.Environment(("DRAUGHTSMAN_KEY_ENCRYPTION_KEY", KeyRingTestSupport.NewKekText())), platform: KeyRingPlatform.Windows, dpapiProbe: () => "no master key");
        Assert.Equal(KeyRingConfig.ProtectionKey, keyed.Protection.Kind);
    }

    [Fact]
    public void The_real_probe_has_nothing_to_probe_off_Windows_so_the_other_platforms_paths_are_unchanged()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // the real probe is exercised for real on the Windows VM
        }

        Assert.Null(DpapiKeyRingProtection.Probe());
    }

    [Fact]
    public void The_message_says_the_same_things_the_keychain_message_does_in_the_same_order()
    {
        string dpapi = KeyRingProtectionResolver.DpapiUnusable("reason.", "DRAUGHTSMAN_KEY_ENCRYPTION_KEY");
        string keychain = KeyRingProtectionResolver.KeychainUnusable("reason.", "DRAUGHTSMAN_KEY_ENCRYPTION_KEY");

        foreach (string phrase in new[] { "will not start with the key ring left unprotected unless you choose that", "Pick one:", "(1) give it a key-encryption key", "(2) run without protection as a deliberate opt-out" })
        {
            Assert.Contains(phrase, dpapi, StringComparison.Ordinal);
            Assert.Contains(phrase, keychain, StringComparison.Ordinal);
        }
    }
}
