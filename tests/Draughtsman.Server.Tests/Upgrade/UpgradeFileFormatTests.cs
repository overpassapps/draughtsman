using Draughtsman.Server.Theming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// The small file formats in a data folder that are not the database: organisation theme files and the branding manifest. Each
/// fixture's own files, as an earlier build wrote them, must be read by the current code to the same meaning.
/// </summary>
public sealed class UpgradeFileFormatTests
{
    public static TheoryData<string> Labels => UpgradeFixture.Labels(UpgradeFixture.Sqlite);

    [Theory]
    [MemberData(nameof(Labels))]
    public void Organisation_theme_files_and_the_branding_manifest_an_earlier_build_wrote_are_read_to_the_same_meaning(string label)
    {
        UpgradeFixture fixture = UpgradeFixture.Get(label);
        string temp = UpgradeData.NewTempFolder();
        try
        {
            string data = fixture.ExtractDataFolder(UpgradeFixture.Sqlite, temp);
            using System.Text.Json.JsonDocument expected = fixture.Expected(UpgradeFixture.Sqlite);

            if (expected.RootElement.TryGetProperty("themes", out System.Text.Json.JsonElement themes))
            {
                var store = new OrgThemeStore(new DataFolder(data), NullLogger<OrgThemeStore>.Instance);
                IReadOnlyList<OrgTheme> loaded = store.LoadAll();
                string[] expectedIds = [.. themes.GetProperty("list").GetProperty("themes").EnumerateArray().Select(t => t.GetProperty("id").GetString()!).Order(StringComparer.Ordinal)];
                Assert.Equal(expectedIds, loaded.Select(t => t.Id).Order(StringComparer.Ordinal));
                Assert.Equal(themes.GetProperty("defaultId").GetString(), store.LoadDefaultId());
                OrgTheme theme = loaded.Single(t => t.Id == themes.GetProperty("defaultId").GetString());
                Assert.Equal("#c0392b", theme.Dark["brand"]);
                // An organisation theme file is also a valid chrome theme file (docs/api.md, Themes: "one format").
                string path = Path.Combine(data, "themes", "org", theme.Id + ".yaml");
                LoadedChromeTheme asChrome = ChromeThemeLoader.Load(data, path, NullLogger.Instance);
                Assert.Equal(theme.Name, asChrome.Theme.Name);
            }
            else
            {
                Assert.False(Directory.Exists(Path.Combine(data, "themes")), "a build without organisation themes wrote no themes folder");
            }

            if (expected.RootElement.TryGetProperty("branding", out System.Text.Json.JsonElement branding))
            {
                BrandingOverrides overrides = new BrandingStore(new DataFolder(data), NullLogger<BrandingStore>.Instance).Load();
                Assert.Equal(branding.GetProperty("productName").GetString(), overrides.ProductName);
                Assert.NotNull(overrides.Logo);
                Assert.NotNull(overrides.LogoLight);
                Assert.NotNull(overrides.Favicon);
                Assert.All(new[] { overrides.Logo, overrides.LogoLight, overrides.Favicon }, name => Assert.True(File.Exists(Path.Combine(data, "branding", name!)), name));
            }
        }
        finally
        {
            UpgradeData.Delete(temp);
        }
    }
}
