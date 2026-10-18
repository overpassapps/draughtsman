using System.Text.RegularExpressions;
using Draughtsman.Server.Theming.Extraction;

namespace Draughtsman.Server.Tests.ThemeColourExtraction;

/// <summary>The extractor's small readers (colours, CSS, HTML) and its mirror of the Themes page's contrast checks.</summary>
public class ThemeParsingTests
{
    [Theory]
    [InlineData("#fff", "#ffffff")]
    [InlineData("#FF6A3D", "#ff6a3d")]
    [InlineData("#ff6a3dff", "#ff6a3d")]
    [InlineData("#f00f", "#ff0000")]
    [InlineData("rgb(255, 106, 61)", "#ff6a3d")]
    [InlineData("rgb(255 106 61)", "#ff6a3d")]
    [InlineData("rgba(255, 106, 61, 0.9)", "#ff6a3d")]
    [InlineData("rgb(255 106 61 / 80%)", "#ff6a3d")]
    [InlineData("rgb(100%, 0%, 0%)", "#ff0000")]
    [InlineData("hsl(0, 100%, 50%)", "#ff0000")]
    [InlineData("hsl(120deg 100% 25%)", "#008000")]
    [InlineData("hsla(240, 100%, 50%, 1)", "#0000ff")]
    [InlineData("rebeccapurple", "#663399")]
    [InlineData("  TEAL  ", "#008080")]
    [InlineData("rgb(300, -4, 9999)", "#ff00ff")]
    public void Colours_in_every_syntax_the_extractor_reads_come_out_as_the_expected_hex(string text, string expected)
    {
        Assert.Equal(expected, CssColour.Parse(text)?.Hex);
    }

    [Theory]
    [InlineData("transparent")]
    [InlineData("currentcolor")]
    [InlineData("inherit")]
    [InlineData("initial")]
    [InlineData("none")]
    [InlineData("var(--x)")]
    [InlineData("color-mix(in srgb, red, blue)")]
    [InlineData("oklch(0.7 0.1 200)")]
    [InlineData("rgba(0, 0, 0, 0.04)")]
    [InlineData("#00000010")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("rgb(1, 2)")]
    [InlineData("hsl(abc, 5%, 5%)")]
    [InlineData("hsl(10, 5, 5)")]
    [InlineData("")]
    public void What_is_not_a_readable_opaque_colour_is_ignored(string text)
    {
        Assert.Null(CssColour.Parse(text));
    }

    [Fact]
    public void A_bare_hsl_triplet_is_a_colour_only_where_the_caller_says_so()
    {
        Assert.Equal("#ffffff", CssColour.Parse("0 0% 100%", allowBareHsl: true)?.Hex);
        Assert.Null(CssColour.Parse("0 0% 100%"));
        Assert.Null(CssColour.Parse("12px 3px 1px", allowBareHsl: true));
    }

    [Fact]
    public void Contrast_is_the_wcag_ratio_and_agrees_with_the_known_pairs()
    {
        Assert.Equal(21.0, Rgb.Contrast(new Rgb(0, 0, 0), new Rgb(255, 255, 255)), 3);
        Assert.Equal(1.0, Rgb.Contrast(new Rgb(10, 20, 30), new Rgb(10, 20, 30)), 3);
        // The shipped Overpass pairs, as the chrome.yaml comments record them: brand red on the dark surface 4.72:1, on white 4.08:1.
        Assert.Equal(4.72, Rgb.Contrast(Rgb.FromHex("#e04832")!.Value, Rgb.FromHex("#0a0e1a")!.Value), 2);
        Assert.Equal(4.08, Rgb.Contrast(Rgb.FromHex("#e04832")!.Value, new Rgb(255, 255, 255)), 2);
    }

    [Fact]
    public void The_shipped_overpass_chrome_theme_passes_the_mirror_in_both_modes_and_a_known_bad_pair_fails_it()
    {
        Dictionary<string, Rgb> dark = Hex(new Dictionary<string, string> { ["brand"] = "#c0392b", ["brandText"] = "#ffffff", ["surface"] = "#0a0e1a", ["surfaceRaised"] = "#151e33", ["border"] = "#202a42", ["text"] = "#f2f2f2", ["textMuted"] = "#9aa4b8", ["accent"] = "#e04832", ["danger"] = "#ff4d4f" });
        Dictionary<string, Rgb> light = Hex(new Dictionary<string, string> { ["brand"] = "#c0392b", ["brandText"] = "#ffffff", ["surface"] = "#ffffff", ["surfaceRaised"] = "#f4f6fa", ["border"] = "#d6dae4", ["text"] = "#151e33", ["textMuted"] = "#616b7b", ["accent"] = "#c0392b", ["danger"] = "#d4380d" });
        Assert.Empty(ChromeContrast.Issues(dark));
        Assert.Empty(ChromeContrast.Issues(light));

        light["textMuted"] = new Rgb(0xaa, 0xaa, 0xaa);
        Assert.Contains("textMuted/surface", ChromeContrast.Issues(light));
        light["textMuted"] = new Rgb(0x61, 0x6b, 0x7b);
        light["text"] = new Rgb(0x76, 0x76, 0x76); // 4.5:1 on white, but its 52% outline is far below 3:1
        Assert.Contains("controlBorder/surface", ChromeContrast.Issues(light));
    }

    /// <summary>The page's checks are in TypeScript; this fails when its pair list or outline mix stops matching the C# mirror.</summary>
    [Fact]
    public void The_C_sharp_mirror_holds_exactly_the_pairs_and_outline_mix_of_themeContrast_ts()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "web", "src", "theme", "themeContrast.ts"));
        var pairs = Regex.Matches(source, @"\['(?<fg>\w+)', '(?<bg>\w+)', (?<min>AA_TEXT|AA_GRAPHIC)\]", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(m => (m.Groups["fg"].Value, m.Groups["bg"].Value, m.Groups["min"].Value == "AA_TEXT" ? 4.5 : 3.0))
            .ToList();
        Assert.Equal(8, pairs.Count);
        Assert.Equal(pairs, ChromeContrast.Pairs.ToList());

        Match mix = Regex.Match(source, @"CONTROL_BORDER_MIX = (?<v>[0-9.]+);", RegexOptions.None, TimeSpan.FromSeconds(5));
        Assert.True(mix.Success);
        Assert.Equal(ChromeContrast.ControlBorderMix, double.Parse(mix.Groups["v"].Value, System.Globalization.CultureInfo.InvariantCulture));
        string contrast = File.ReadAllText(Path.Combine(RepositoryRoot(), "web", "src", "theme", "contrast.ts"));
        Assert.Contains("AA_TEXT = 4.5", contrast, StringComparison.Ordinal);
        Assert.Contains("AA_GRAPHIC = 3", contrast, StringComparison.Ordinal);
    }

    [Fact]
    public void The_css_reader_flattens_media_queries_marks_dark_scopes_and_drops_font_faces_keyframes_and_nested_rules()
    {
        IReadOnlyList<CssRule> rules = CssSheet.Parse("""
            /* c */ a { color: red; } @media (min-width: 1px) { b { color: blue } }
            @media (prefers-color-scheme: dark) { c { color: lime } }
            .dark d { color: aqua } [data-theme="dark"] e { color: fuchsia }
            @font-face { font-family: x; color: #111 } @keyframes k { from { color: #222 } }
            f { color: gold; & g { color: pink } ; background: "}" }
            """);

        Assert.Equal(["a", "b", "c", ".dark d", "[data-theme=\"dark\"] e", "f"], rules.Select(r => r.Selector).ToArray());
        Assert.Equal([SheetScheme.Default, SheetScheme.Default, SheetScheme.Dark, SheetScheme.Dark, SheetScheme.Dark, SheetScheme.Default], rules.Select(r => r.Scheme).ToArray());
        Assert.Equal(["color", "background"], rules[^1].Declarations.Select(d => d.Property).ToArray());
        Assert.Equal("gold", rules[^1].Declarations[0].Value);
    }

    [Fact]
    public void The_html_scanner_finds_style_blocks_theme_colours_and_stylesheet_links_and_skips_comments_and_scripts()
    {
        const string html = """
            <!-- <link rel="stylesheet" href="/commented.css"> -->
            <script>document.write('<link rel="stylesheet" href="/scripted.css">')</script>
            <meta name="Theme-Color" content="#112233"><meta name="viewport" content="width=device-width">
            <link rel="preload" href="/font.woff2"><LINK REL="stylesheet" HREF="/a.css">
            <link rel='alternate stylesheet' href=/b.css><link rel="stylesheet" href="/print.css" media="print"><link rel="stylesheet" href="">
            <style>a{color:red}</style><style media="x">b{color:blue}</style>
            """;

        Assert.Equal(["#112233"], HtmlScanner.ThemeColours(html));
        Assert.Equal(["/a.css", "/b.css"], HtmlScanner.StylesheetLinks(html));
        Assert.Equal(["a{color:red}", "b{color:blue}"], HtmlScanner.StyleBlocks(html));
        Assert.True(HtmlScanner.LooksLikeHtml(html));
        Assert.False(HtmlScanner.LooksLikeHtml("body { color: red }"));
    }

    private static Dictionary<string, Rgb> Hex(Dictionary<string, string> set)
    {
        return set.ToDictionary(p => p.Key, p => Rgb.FromHex(p.Value)!.Value, StringComparer.Ordinal);
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Draughtsman.sln")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
