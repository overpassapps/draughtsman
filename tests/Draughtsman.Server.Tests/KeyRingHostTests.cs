using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The whole host, started and restarted over one data folder the way an operator's deployment is: turn key ring
/// protection on for an instance that already has a stored AI key, lose the protection, damage the ring. Each step
/// goes through the real startup (<c>Program</c>), the real admin AI endpoints and the real files on disk, so what
/// passes here is what the admin page will show.
/// </summary>
public class KeyRingHostTests : IDisposable
{
    private const string Key = "sk-host-test-secret-1234567890";

    private const string KekVariable = "DRAUGHTSMAN_KEY_ENCRYPTION_KEY";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "draughtsman-keyring-host-tests", Guid.NewGuid().ToString("N"));

    public KeyRingHostTests()
    {
        Directory.CreateDirectory(_data);
    }

    public void Dispose()
    {
        SqlitePools.ReleaseUnder(_data);
        try
        {
            KeyRingTestSupport.DeleteFolder(_data);
        }
        catch (IOException)
        {
            // Best effort on Windows, where a pooled handle can outlive the host briefly.
        }
    }

    [Fact]
    public async Task Turning_protection_on_for_an_instance_with_a_stored_key_protects_the_ring_and_the_key_still_works()
    {
        string keys = Path.Combine(_data, "keys");
        string masterKey;
        JsonNode before;
        using (var first = new Host(_data))
        {
            HttpClient admin = await first.CreateAuthenticatedClient();
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/ai", new { provider = "openai", model = "gpt-4o-mini" })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = Key })).StatusCode);
            before = await ReadJson(await admin.GetAsync("/api/admin/ai"));
        }

        // The control: with protection off, the page says the ring is unprotected, and the ring really is plain text.
        Assert.Equal("unprotected", before["keyRing"]!["state"]!.GetValue<string>());
        Assert.Equal("disabled", before["keyRing"]!["reason"]!.GetValue<string>());
        masterKey = KeyRingTestSupport.PlainMasterKeys(keys).Single();
        Assert.Contains(masterKey, KeyRingTestSupport.AllText(keys), StringComparison.Ordinal);

        string kek = KeyRingTestSupport.NewKekText();
        string body;
        using (var second = new Host(_data, protection: null, (KekVariable, kek)))
        {
            HttpClient admin = await second.CreateAuthenticatedClient();
            HttpResponseMessage response = await admin.GetAsync("/api/admin/ai");
            body = await response.Content.ReadAsStringAsync();
            JsonNode state = JsonNode.Parse(body)!;

            Assert.Equal("protected", state["keyRing"]!["state"]!.GetValue<string>());
            Assert.Equal("key", state["keyRing"]!["provider"]!.GetValue<string>());
            Assert.Equal("migrated", state["keyRing"]!["migration"]!.GetValue<string>());
            Assert.StartsWith("backups/key-ring-", state["keyRing"]!["backup"]!.GetValue<string>(), StringComparison.Ordinal);

            // The stored key survived: set, in effect, and readable.
            Assert.Equal("stored", state["key"]!["source"]!.GetValue<string>());
            Assert.True(state["key"]!["storedSet"]!.GetValue<bool>());
            Assert.False(state["key"]!["storedUnreadable"]!.GetValue<bool>());
        }

        // The ring on disk is protected now; the backup still has the plain one.
        Assert.DoesNotContain(masterKey, KeyRingTestSupport.AllText(keys), StringComparison.Ordinal);
        Assert.Contains(masterKey, KeyRingTestSupport.AllText(Path.Combine(_data, "backups")), StringComparison.Ordinal);

        // Nothing secret in what the admin page received.
        Assert.DoesNotContain(Key, body, StringComparison.Ordinal);
        Assert.DoesNotContain(kek, body, StringComparison.Ordinal);
        Assert.DoesNotContain(masterKey, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Losing_the_protection_starts_the_server_reports_the_key_unreadable_and_getting_it_back_restores_the_key()
    {
        string kek = KeyRingTestSupport.NewKekText();
        using (var first = new Host(_data, protection: null, (KekVariable, kek)))
        {
            HttpClient admin = await first.CreateAuthenticatedClient();
            await admin.PutAsJsonAsync("/api/admin/ai", new { provider = "openai", model = "gpt-4o-mini" });
            await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = Key });
        }

        Dictionary<string, byte[]> ringBefore = KeyRingTestSupport.Bytes(Path.Combine(_data, "keys"), "*.xml");
        Assert.All(KeyRingFiles.Inspect(Path.Combine(_data, "keys")), file => Assert.Equal(KeyFileState.Encrypted, file.State));

        // The deployment lost the key-encryption key: protection was asked for ("key") and cannot be had.
        using (var lost = new Host(_data, protection: "key"))
        {
            HttpClient admin = await lost.CreateAuthenticatedClient();
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/health")).StatusCode);

            JsonNode state = await ReadJson(await admin.GetAsync("/api/admin/ai"));
            Assert.Equal("unprotected", state["keyRing"]!["state"]!.GetValue<string>());
            Assert.Equal("unavailable", state["keyRing"]!["reason"]!.GetValue<string>());
            Assert.Contains(KekVariable, state["keyRing"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.True(state["key"]!["storedUnreadable"]!.GetValue<bool>());
            Assert.False(state["key"]!["storedSet"]!.GetValue<bool>());
            Assert.Equal("none", state["key"]!["source"]!.GetValue<string>());
        }

        // Nothing already on disk was rewritten by running without the protection (a new key may have been added beside them).
        Dictionary<string, byte[]> ringDuring = KeyRingTestSupport.Bytes(Path.Combine(_data, "keys"), "*.xml");
        Assert.All(ringBefore, pair => Assert.Equal(pair.Value, ringDuring[pair.Key]));

        // The protection is back: the old key opens again, and the plain key the gap created is protected in turn.
        using (var restored = new Host(_data, protection: null, (KekVariable, kek)))
        {
            HttpClient admin = await restored.CreateAuthenticatedClient();
            JsonNode state = await ReadJson(await admin.GetAsync("/api/admin/ai"));
            Assert.Equal("stored", state["key"]!["source"]!.GetValue<string>());
            Assert.False(state["key"]!["storedUnreadable"]!.GetValue<bool>());
            Assert.Equal("protected", state["keyRing"]!["state"]!.GetValue<string>());
        }

        Assert.All(KeyRingFiles.Inspect(Path.Combine(_data, "keys")), file => Assert.Equal(KeyFileState.Encrypted, file.State));
    }

    [Fact]
    public async Task A_damaged_ring_file_does_not_stop_the_server_and_the_key_can_be_entered_again()
    {
        string kek = KeyRingTestSupport.NewKekText();
        using (var first = new Host(_data))
        {
            HttpClient admin = await first.CreateAuthenticatedClient();
            await admin.PutAsJsonAsync("/api/admin/ai", new { provider = "openai", model = "gpt-4o-mini" });
            await admin.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = Key });
        }

        string keyFile = Directory.GetFiles(Path.Combine(_data, "keys"), "*.xml").Single();
        byte[] original = File.ReadAllBytes(keyFile);
        File.WriteAllBytes(keyFile, original[..(original.Length / 2)]);

        using var second = new Host(_data, protection: null, (KekVariable, kek));
        HttpClient client = await second.CreateAuthenticatedClient();

        // It is up, and signing in (which needs a working ring for the antiforgery token) worked.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        JsonNode state = await ReadJson(await client.GetAsync("/api/admin/ai"));
        Assert.True(state["key"]!["storedUnreadable"]!.GetValue<bool>());
        Assert.Contains("damaged", state["keyRing"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);

        // The damaged file and the backup are both still there, byte for byte where they can be.
        Assert.Equal(original[..(original.Length / 2)], File.ReadAllBytes(keyFile + KeyRingQuarantine.Suffix));
        Assert.Equal(original[..(original.Length / 2)], File.ReadAllBytes(Directory.GetFiles(Path.Combine(_data, "backups"), "*.xml", SearchOption.AllDirectories).Single()));

        // Entering the key again works, and is protected like everything else in the ring now.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/admin/ai/key", new { apiKey = Key })).StatusCode);
        JsonNode after = await ReadJson(await client.GetAsync("/api/admin/ai"));
        Assert.True(after["key"]!["storedSet"]!.GetValue<bool>());
        Assert.False(after["key"]!["storedUnreadable"]!.GetValue<bool>());
        Assert.DoesNotContain(Key, await (await client.GetAsync("/api/admin/ai")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    /// <summary>A server over an existing data folder that is not deleted when it stops, so the next start can find what this one left.</summary>
    private sealed class Host(string dataFolder, string? protection = "none", params (string Name, string Value)[] settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(DraughtsmanConfig.DataFolderEnvironmentVariable, dataFolder);
            if (protection is not null)
            {
                builder.UseSetting(KeyRingSetup.ProtectionOverrideVariable, protection);
            }

            foreach ((string name, string value) in settings)
            {
                builder.UseSetting(name, value);
            }
        }
    }
}
