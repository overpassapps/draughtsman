using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Tests;

public class ConfigLoaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));

    public ConfigLoaderTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Absent_file_yields_defaults()
    {
        DraughtsmanConfig config = ConfigLoader.Load(_folder);

        AssertDefaults(config);
    }

    [Fact]
    public void Empty_file_yields_defaults()
    {
        File.WriteAllText(Path.Combine(_folder, DraughtsmanConfig.FileName), string.Empty);

        DraughtsmanConfig config = ConfigLoader.Load(_folder);

        AssertDefaults(config);
    }

    [Fact]
    public void Comment_only_file_yields_defaults()
    {
        File.WriteAllText(Path.Combine(_folder, DraughtsmanConfig.FileName), "# nothing set yet\n");

        DraughtsmanConfig config = ConfigLoader.Load(_folder);

        AssertDefaults(config);
    }

    [Fact]
    public void Partial_file_overrides_only_what_it_sets()
    {
        const string yaml = """
            storage:
              provider: postgres
              connectionString: "Host=db;Database=draughtsman"
            server:
              port: 8080
            export:
              chromium:
                enabled: true
            sidecar:
              nodePath: /opt/node/bin/node
              layoutTimeoutSeconds: 5
            futureKey: ignored
            """;
        File.WriteAllText(Path.Combine(_folder, DraughtsmanConfig.FileName), yaml);

        DraughtsmanConfig config = ConfigLoader.Load(_folder);

        Assert.Equal("postgres", config.Storage.Provider);
        Assert.Equal("Host=db;Database=draughtsman", config.Storage.ConnectionString);
        Assert.Equal(8080, config.Server.Port);
        Assert.True(config.Export.Chromium.Enabled);
        Assert.Equal("/opt/node/bin/node", config.Sidecar.NodePath);
        Assert.Equal(5, config.Sidecar.LayoutTimeoutSeconds);
        Assert.Equal(10, config.Sidecar.YjsTimeoutSeconds);
        Assert.Equal("local", config.Auth.Mode);
        Assert.True(config.Mcp.Enabled);
        Assert.Equal("overpass", config.Theme.Diagram);
    }

    [Fact]
    public void Data_folder_defaults_to_dot_data_and_honours_override()
    {
        string defaultFolder = ConfigLoader.ResolveDataFolder(environmentOverride: string.Empty);
        string overridden = ConfigLoader.ResolveDataFolder(environmentOverride: _folder);

        Assert.True(Path.IsPathRooted(defaultFolder));
        Assert.Equal("data", Path.GetFileName(defaultFolder));
        Assert.Equal(Path.GetFullPath(_folder), overridden);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_timeout_that_is_not_a_positive_number_of_seconds_falls_back_to_the_default(double configured)
    {
        Assert.Equal(TimeSpan.FromSeconds(SidecarConfig.DefaultLayoutTimeoutSeconds), SidecarConfig.ResolveTimeout(configured, SidecarConfig.DefaultLayoutTimeoutSeconds));
        Assert.Equal(TimeSpan.FromSeconds(SidecarConfig.DefaultYjsTimeoutSeconds), SidecarConfig.ResolveTimeout(configured, SidecarConfig.DefaultYjsTimeoutSeconds));
    }

    [Fact]
    public void A_positive_timeout_is_used_as_configured()
    {
        Assert.Equal(TimeSpan.FromSeconds(0.5), SidecarConfig.ResolveTimeout(0.5, SidecarConfig.DefaultLayoutTimeoutSeconds));
        Assert.Equal(TimeSpan.FromSeconds(120), SidecarConfig.ResolveTimeout(120, SidecarConfig.DefaultLayoutTimeoutSeconds));
    }

    [Fact]
    public void Server_knownProxies_and_forceHttps_parse_from_yaml()
    {
        const string yaml = """
            server:
              knownProxies:
                - 10.0.0.1
                - 10.0.0.2
              forceHttps: true
            """;
        File.WriteAllText(Path.Combine(_folder, DraughtsmanConfig.FileName), yaml);

        DraughtsmanConfig config = ConfigLoader.Load(_folder);

        Assert.Equal(["10.0.0.1", "10.0.0.2"], config.Server.KnownProxies);
        Assert.True(config.Server.ForceHttps);
    }

    [Fact]
    public void KeyRing_section_parses_and_every_key_defaults_when_absent()
    {
        const string yaml = """
            keyRing:
              protection: certificate
              keyEnvironmentVariable: MY_RING_KEY
              keyFile: /run/secrets/draughtsman-kek
              certificate:
                path: /etc/draughtsman/ring.pfx
                passwordEnvironmentVariable: MY_RING_PFX_PASSWORD
            """;
        File.WriteAllText(Path.Combine(_folder, DraughtsmanConfig.FileName), yaml);

        KeyRingConfig set = ConfigLoader.Load(_folder).KeyRing;
        Assert.Equal("certificate", set.Protection);
        Assert.Equal("MY_RING_KEY", set.KeyEnvironmentVariable);
        Assert.Equal("/run/secrets/draughtsman-kek", set.KeyFile);
        Assert.Equal("/etc/draughtsman/ring.pfx", set.Certificate.Path);
        Assert.Equal("MY_RING_PFX_PASSWORD", set.Certificate.PasswordEnvironmentVariable);

        // A file that names only one key leaves the rest at their defaults.
        KeyRingConfig partial = ConfigLoader.Parse("keyRing:\n  protection: none\n").KeyRing;
        Assert.Equal("none", partial.Protection);
        Assert.Equal("DRAUGHTSMAN_KEY_ENCRYPTION_KEY", partial.KeyEnvironmentVariable);
        Assert.Equal("DRAUGHTSMAN_KEY_CERTIFICATE_PASSWORD", partial.Certificate.PasswordEnvironmentVariable);
    }

    private static void AssertDefaults(DraughtsmanConfig config)
    {
        Assert.Equal("auto", config.KeyRing.Protection);
        Assert.Equal("DRAUGHTSMAN_KEY_ENCRYPTION_KEY", config.KeyRing.KeyEnvironmentVariable);
        Assert.Equal(string.Empty, config.KeyRing.KeyFile);
        Assert.Equal(string.Empty, config.KeyRing.Certificate.Path);
        Assert.Equal(5180, config.Server.Port);
        Assert.Empty(config.Server.KnownProxies);
        Assert.False(config.Server.ForceHttps);
        Assert.Equal("sqlite", config.Storage.Provider);
        Assert.Equal(string.Empty, config.Storage.ConnectionString);
        Assert.Equal("local", config.Auth.Mode);
        Assert.Equal(string.Empty, config.Ai.Provider);
        Assert.Equal(false, config.Ai.Think);
        Assert.Null(config.Ai.TimeoutSeconds);
        Assert.Equal("chrome.yaml", config.Theme.Chrome);
        Assert.Equal("overpass", config.Theme.Diagram);
        Assert.False(config.Export.Chromium.Enabled);
        Assert.True(config.Mcp.Enabled);
        Assert.Equal(string.Empty, config.Sidecar.NodePath);
        Assert.Equal(string.Empty, config.Sidecar.ScriptPath);
        Assert.Equal(30, config.Sidecar.LayoutTimeoutSeconds);
        Assert.Equal(10, config.Sidecar.YjsTimeoutSeconds);
    }
}
