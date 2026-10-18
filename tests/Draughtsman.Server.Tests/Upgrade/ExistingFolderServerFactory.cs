using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// A server started the way an upgrade starts one: on a data folder an EARLIER build already filled, through the real startup
/// path (configuration, migration, the key ring). Unlike <see cref="DraughtsmanServerFactory"/> it does not own a fresh folder; the
/// caller extracted the fixture and removes it, because a test often wants to look at the folder after the host has stopped.
/// </summary>
internal sealed class ExistingFolderServerFactory(string dataFolder, string keyRingProtection = "none", string? keyEncryptionKey = null, string? contentRoot = null, string? legacyInstallPaths = null) : WebApplicationFactory<Program>
{
    public string DataFolder { get; } = dataFolder;

    /// <summary>The content root this host runs from: the folder an install's program would be in, which is what Data Protection used to bind a stored secret to.</summary>
    public string? ContentRoot { get; } = contentRoot;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(DraughtsmanConfig.DataFolderEnvironmentVariable, DataFolder);
        if (ContentRoot is not null)
        {
            builder.UseContentRoot(ContentRoot);
        }

        if (legacyInstallPaths is not null)
        {
            builder.UseSetting(LegacyDiscriminators.HintVariable, legacyInstallPaths);
        }

        builder.UseSetting(KeyRingSetup.ProtectionOverrideVariable, keyRingProtection);
        if (keyEncryptionKey is not null)
        {
            builder.UseSetting("DRAUGHTSMAN_KEY_ENCRYPTION_KEY", keyEncryptionKey);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            SqlitePools.Release(DataFolder);
        }
    }
}
