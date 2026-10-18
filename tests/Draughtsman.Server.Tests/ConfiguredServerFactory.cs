using Draughtsman.Server.Configuration;
using Microsoft.AspNetCore.Hosting;

namespace Draughtsman.Server.Tests;

/// <summary>A <see cref="DraughtsmanServerFactory"/> whose data folder starts with the given draughtsman.yaml, read at startup like a real instance's.</summary>
public sealed class ConfiguredServerFactory(string yaml) : DraughtsmanServerFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(Path.Combine(DataFolder, DraughtsmanConfig.FileName), yaml);
    }
}
