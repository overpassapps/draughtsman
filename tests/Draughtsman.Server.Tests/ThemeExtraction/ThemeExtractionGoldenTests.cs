using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Theming.Extraction;

namespace Draughtsman.Server.Tests.ThemeColourExtraction;

/// <summary>
/// The draft the extractor gives for each fixture, kept as a JSON file under <c>Fixtures/ThemeExtraction/</c>. Two things hang off it:
/// this test fails when the extractor's output for a fixture changes (so a change to the colour rules is always a visible diff), and
/// the web suite (<c>web/src/theme/extractedDrafts.test.ts</c>) reads the same files and runs the Themes page's own TypeScript contrast
/// checks, the diagram's included, over them, which the C# side cannot do. Regenerate with <c>DRAUGHTSMAN_UPDATE_GOLDEN=1</c>.
/// </summary>
public class ThemeExtractionGoldenTests
{
    private static readonly (string File, string Css)[] Cases =
    [
        ("dark-brand-site", ThemeFixtures.DarkBrandSite),
        ("light-minimal-site", ThemeFixtures.LightMinimalSite),
        ("custom-properties-only", ThemeFixtures.CustomPropertiesOnly),
        ("bare-hsl-properties", ThemeFixtures.BareHslProperties),
        ("hard-coded-only", ThemeFixtures.HardCodedOnly),
        ("hostile", ThemeFixtures.Hostile),
        ("html-page", ThemeFixtures.HtmlPage),
    ];

    public static TheoryData<string> Files => new(Cases.Select(c => c.File));

    private static string FolderPath()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Draughtsman.sln")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return Path.Combine(directory ?? throw new InvalidOperationException("The repository root was not found."), "tests", "Draughtsman.Server.Tests", "Fixtures", "ThemeExtraction");
    }

    private static string Render(string css)
    {
        ThemeExtraction result = ThemeExtractor.Extract(css);
        JsonObject Set(IReadOnlyDictionary<string, string> colours)
        {
            var node = new JsonObject();
            foreach (string key in Draughtsman.Server.Theming.OrgTheme.ChromeColourKeys)
            {
                node[key] = colours[key];
            }

            return node;
        }

        var palette = new JsonObject();
        foreach ((string key, string value) in result.Draft.Diagram.Palette.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            palette[key] = value;
        }

        var root = new JsonObject
        {
            ["mode"] = result.Draft.Mode,
            ["chrome"] = new JsonObject { ["dark"] = Set(result.Draft.Dark), ["light"] = Set(result.Draft.Light) },
            ["diagram"] = new JsonObject { ["base"] = result.Draft.Diagram.Base, ["palette"] = palette },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n";
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void The_draft_for_each_fixture_is_the_recorded_one(string file)
    {
        string expected = Render(Cases.Single(c => c.File == file).Css);
        string path = Path.Combine(FolderPath(), file + ".draft.json");
        if (Environment.GetEnvironmentVariable("DRAUGHTSMAN_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(FolderPath());
            File.WriteAllText(path, expected);
        }

        Assert.True(File.Exists(path), $"{path} is missing: run once with DRAUGHTSMAN_UPDATE_GOLDEN=1.");
        Assert.Equal(expected, File.ReadAllText(path));
    }

    [Fact]
    public void Every_recorded_draft_has_a_fixture_and_the_other_way_round()
    {
        string[] recorded = [.. Directory.GetFiles(FolderPath(), "*.draft.json").Select(f => Path.GetFileName(f)[..^".draft.json".Length]).Order(StringComparer.Ordinal)];
        Assert.Equal(Cases.Select(c => c.File).Order(StringComparer.Ordinal).ToArray(), recorded);
    }
}
