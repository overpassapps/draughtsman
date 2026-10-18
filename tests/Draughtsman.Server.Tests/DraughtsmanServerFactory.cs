using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Draughtsman.Server.Tests;

/// <summary>
/// A server over its own temporary data folder, so every test class gets a fresh SQLite database through the same
/// startup path (config, provider selection, migration) a real instance uses. The folder is removed on dispose.
/// </summary>
public class DraughtsmanServerFactory : WebApplicationFactory<Program>
{
    public string DataFolder { get; } = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// What <c>DRAUGHTSMAN_KEY_RING_PROTECTION</c> is for this host. Off by default so a test run never reaches for the
    /// real macOS Keychain or Windows DPAPI of whoever runs it; the key ring tests that want protection override this.
    /// </summary>
    protected virtual string? KeyRingProtection => "none";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(DraughtsmanConfig.DataFolderEnvironmentVariable, DataFolder);
        if (KeyRingProtection is not null)
        {
            builder.UseSetting(KeyRingSetup.ProtectionOverrideVariable, KeyRingProtection);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        SqlitePools.Release(DataFolder);
        try
        {
            if (Directory.Exists(DataFolder))
            {
                Directory.Delete(DataFolder, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort on Windows, where a pooled handle can outlive the host briefly.
        }
    }
}
