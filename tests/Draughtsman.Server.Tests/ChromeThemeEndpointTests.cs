using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The chrome theme contract (spec section 8): the server compiles the configured YAML to CSS custom properties at
/// startup and serves them, so the shipped SPA takes a theme on without a rebuild. Both themes are asserted present
/// and absent the other way round, and a missing or invalid file falls back to the shipped Overpass default.
/// </summary>
public class ChromeThemeEndpointTests
{
    private const string OverpassBrand = "--chrome-brand: #c0392b;";
    private const string ClientBrand = "--chrome-brand: #0b6b3a;";

    [Fact]
    public async Task Default_theme_serves_overpass_properties()
    {
        using ThemedServerFactory factory = new();
        (string css, JsonNode manifest) = await ReadTheme(factory);

        Assert.Contains(OverpassBrand, css, StringComparison.Ordinal);
        Assert.Contains("--chrome-surface: #0a0e1a;", css, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientBrand, css, StringComparison.Ordinal);
        Assert.Equal("Draughtsman", manifest["productName"]!.GetValue<string>());
        Assert.Equal("dark", manifest["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Configured_client_theme_serves_its_properties()
    {
        using ThemedServerFactory factory = new("theme:\n  chrome: chrome-example-client.yaml\n");
        (string css, JsonNode manifest) = await ReadTheme(factory);

        Assert.Contains(ClientBrand, css, StringComparison.Ordinal);
        Assert.Contains("--chrome-surface: #ffffff;", css, StringComparison.Ordinal);
        Assert.DoesNotContain(OverpassBrand, css, StringComparison.Ordinal);
        Assert.Equal("Northwind", manifest["productName"]!.GetValue<string>());
        Assert.Equal("light", manifest["mode"]!.GetValue<string>());
        // A client theme that names no compact mark gets none, so the header falls back to its normal logo.
        Assert.Null(manifest["logoCompact"]);
        Assert.Null(manifest["logoCompactLight"]);
        Assert.Equal("/api/theme/logo", manifest["logo"]!.GetValue<string>());
    }

    [Fact]
    public async Task Theme_in_the_data_folder_overrides_the_shipped_one()
    {
        const string custom = """
            name: Data folder theme
            productName: Custom
            mode: dark
            colors:
              brand: "#123456"
            radius: 4
            font:
              ui: sans-serif
            """;
        using ThemedServerFactory factory = new("theme:\n  chrome: custom.yaml\n", dataFolder =>
            File.WriteAllText(Path.Combine(dataFolder, "themes", "custom.yaml"), custom));
        (string css, JsonNode manifest) = await ReadTheme(factory);

        Assert.Contains("--chrome-brand: #123456;", css, StringComparison.Ordinal);
        Assert.DoesNotContain(OverpassBrand, css, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientBrand, css, StringComparison.Ordinal);
        Assert.Equal("Custom", manifest["productName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Missing_theme_falls_back_to_the_shipped_default()
    {
        using ThemedServerFactory factory = new("theme:\n  chrome: does-not-exist.yaml\n");
        (string css, JsonNode manifest) = await ReadTheme(factory);

        Assert.Contains(OverpassBrand, css, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientBrand, css, StringComparison.Ordinal);
        Assert.Equal("Draughtsman", manifest["productName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_theme_falls_back_to_the_shipped_default()
    {
        using ThemedServerFactory factory = new("theme:\n  chrome: broken.yaml\n", dataFolder =>
            File.WriteAllText(Path.Combine(dataFolder, "themes", "broken.yaml"), "- not\n- a\n- map\n"));
        (string css, JsonNode manifest) = await ReadTheme(factory);

        Assert.Contains(OverpassBrand, css, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientBrand, css, StringComparison.Ordinal);
        Assert.Equal("Draughtsman", manifest["productName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Logo_and_favicon_come_from_the_configured_theme()
    {
        using ThemedServerFactory factory = new("theme:\n  chrome: chrome-example-client.yaml\n");
        HttpClient client = factory.CreateClient();

        HttpResponseMessage logo = await client.GetAsync("/api/theme/logo", CancellationToken.None);
        HttpResponseMessage favicon = await client.GetAsync("/api/theme/favicon", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/svg+xml", logo.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Northwind", await logo.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, favicon.StatusCode);
        Assert.Equal("image/svg+xml", favicon.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<(string Css, JsonNode Manifest)> ReadTheme(DraughtsmanServerFactory factory)
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage cssResponse = await client.GetAsync("/api/theme/chrome.css", CancellationToken.None);
        HttpResponseMessage manifestResponse = await client.GetAsync("/api/theme", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, cssResponse.StatusCode);
        Assert.Equal("text/css", cssResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, manifestResponse.StatusCode);
        string css = await cssResponse.Content.ReadAsStringAsync(CancellationToken.None);
        JsonNode? manifest = JsonNode.Parse(await manifestResponse.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.NotNull(manifest);
        return (css, manifest);
    }

    /// <summary>A server over its own data folder with a draughtsman.yaml (and any theme files) written before startup.</summary>
    private sealed class ThemedServerFactory : DraughtsmanServerFactory
    {
        public ThemedServerFactory(string? configYaml = null, Action<string>? prepare = null)
        {
            Directory.CreateDirectory(DataFolder);
            Directory.CreateDirectory(Path.Combine(DataFolder, "themes"));
            prepare?.Invoke(DataFolder);
            if (configYaml is not null)
            {
                File.WriteAllText(Path.Combine(DataFolder, DraughtsmanConfig.FileName), configYaml, Encoding.UTF8);
            }
        }
    }
}
