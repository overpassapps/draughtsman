using Draughtsman.Server.Ai;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The disk half of the AI settings (M3): the API key is written encrypted with Data Protection, never as plain
/// text, and survives a restart (a second store over the same folder and key ring). The HTTP tests never restart the
/// host, so this is where at-rest encryption and persistence are proved.
/// </summary>
public class AiSettingsStoreTests : IDisposable
{
    private const string Key = "sk-store-secret-value-1234567890";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "draughtsman-ai-store-tests", Guid.NewGuid().ToString("N"));

    public AiSettingsStoreTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void The_key_is_encrypted_on_disk_and_readable_after_a_restart()
    {
        AiSettingsStore first = Store();
        first.Save(new AiOverrides("openai", "gpt-4o-mini", null, null), Key);

        string onDisk = File.ReadAllText(Path.Combine(_folder, "ai", "settings.json"));
        // The half that must fail if someone writes the key as plain text.
        Assert.DoesNotContain(Key, onDisk, StringComparison.Ordinal);
        Assert.Contains("protectedApiKey", onDisk, StringComparison.Ordinal);
        Assert.Contains("gpt-4o-mini", onDisk, StringComparison.Ordinal);

        // A second store over the same folder and key ring is what a restart constructs.
        StoredAiSettings reloaded = Store().Load();
        Assert.Equal(Key, reloaded.ApiKey);
        Assert.Equal("openai", reloaded.Overrides.Provider);
        Assert.False(reloaded.KeyUnreadable);
    }

    [Fact]
    public void A_key_the_key_ring_cannot_decrypt_reads_as_unset_and_flagged_not_as_a_crash()
    {
        Store().Save(AiOverrides.None, Key);

        // A different key ring, as after the keys folder was lost.
        string otherRing = Path.Combine(_folder, "other-ring");
        var store = new AiSettingsStore(new DataFolder(_folder), DataProtectionProvider.Create(new DirectoryInfo(otherRing)), NullLogger<AiSettingsStore>.Instance);

        StoredAiSettings loaded = store.Load();
        Assert.Null(loaded.ApiKey);
        Assert.True(loaded.KeyUnreadable);
    }

    [Fact]
    public void Saving_nothing_removes_the_file_so_a_reset_leaves_no_trace()
    {
        AiSettingsStore store = Store();
        store.Save(new AiOverrides("ollama", null, null, null), Key);
        Assert.True(File.Exists(Path.Combine(_folder, "ai", "settings.json")));

        store.Save(AiOverrides.None, null);

        Assert.False(File.Exists(Path.Combine(_folder, "ai", "settings.json")));
        Assert.Equal(StoredAiSettings.Empty, Store().Load());
    }

    [Fact]
    public void A_corrupt_settings_file_is_treated_as_no_overrides()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "ai"));
        File.WriteAllText(Path.Combine(_folder, "ai", "settings.json"), "{ not json");

        Assert.Equal(StoredAiSettings.Empty, Store().Load());
    }

    private AiSettingsStore Store()
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_folder, "keys")));
        return new AiSettingsStore(new DataFolder(_folder), provider, NullLogger<AiSettingsStore>.Instance);
    }
}
