using System.Text.Json;
using Draughtsman.Server.Theming;
using Draughtsman.Server.Theming.Extraction;

namespace Draughtsman.Server.Tests.ThemeColourExtraction;

/// <summary>
/// The colour-scheme extractor (admin Themes, "From a website"): pure text in, draft theme out. Each fixture is a different kind
/// of site; for each the draft must pass <see cref="OrgThemeValidator"/> as it stands, meet every contrast check the Themes page
/// shows in both modes, and come out identical on a second run. Every rule is shown both ways: what is used and what is ignored.
/// </summary>
public class ThemeExtractorTests
{
    private static readonly (string Name, string Css)[] Fixtures =
    [
        ("dark brand site", ThemeFixtures.DarkBrandSite),
        ("light minimal site", ThemeFixtures.LightMinimalSite),
        ("custom properties only", ThemeFixtures.CustomPropertiesOnly),
        ("bare hsl properties", ThemeFixtures.BareHslProperties),
        ("hard-coded colours only", ThemeFixtures.HardCodedOnly),
        ("hostile sheet", ThemeFixtures.Hostile),
        ("html page", ThemeFixtures.HtmlPage),
    ];

    public static TheoryData<string> FixtureNames => new(Fixtures.Select(f => f.Name));

    private static string CssOf(string name)
    {
        return Fixtures.Single(f => f.Name == name).Css;
    }

    private static Dictionary<string, Rgb> Colours(IReadOnlyDictionary<string, string> set)
    {
        return set.ToDictionary(pair => pair.Key, pair => Rgb.FromHex(pair.Value)!.Value, StringComparer.Ordinal);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Every_draft_passes_the_validator_and_every_contrast_check_in_both_modes(string fixture)
    {
        ThemeExtraction result = ThemeExtractor.Extract(CssOf(fixture));

        (_, List<OrgThemeProblem> problems) = OrgThemeValidator.Validate(result.Draft);
        Assert.Empty(problems);
        Assert.Equal(OrgTheme.ChromeColourKeys.Order(), result.Draft.Dark.Keys.Order());
        Assert.Equal(OrgTheme.ChromeColourKeys.Order(), result.Draft.Light.Keys.Order());
        Assert.Empty(ChromeContrast.Issues(Colours(result.Draft.Dark)));
        Assert.Empty(ChromeContrast.Issues(Colours(result.Draft.Light)));
        Assert.Contains(result.Draft.Mode, new[] { "dark", "light" });
        Assert.Contains(result.Draft.Diagram.Base, OrgDiagramTheme.Bases);
        Assert.NotEmpty(result.Reasons);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void The_same_input_always_gives_the_same_draft(string fixture)
    {
        string first = JsonSerializer.Serialize(ThemeExtractor.Extract(CssOf(fixture)));
        string second = JsonSerializer.Serialize(ThemeExtractor.Extract(CssOf(fixture)));
        string viaSource = JsonSerializer.Serialize(ThemeExtractor.Extract(ThemeSource.FromText(CssOf(fixture))));

        Assert.Equal(first, second);
        Assert.Equal(first, viaSource);
    }

    [Fact]
    public void A_dark_brand_site_gives_a_dark_theme_in_its_own_colours_and_a_derived_light_one()
    {
        ThemeExtraction result = ThemeExtractor.Extract(ThemeFixtures.DarkBrandSite);

        Assert.Equal("dark", result.Draft.Mode);
        Assert.Equal("#0b1020", result.Draft.Dark["surface"]);
        Assert.Equal("#121a30", result.Draft.Dark["surfaceRaised"]);
        Assert.Equal("#ff6a3d", result.Draft.Dark["brand"]);
        Assert.Equal("#ff6a3d", result.Draft.Dark["accent"]);
        Assert.Equal("#e6e9f2", result.Draft.Dark["text"]);
        Assert.Equal("#98a2bd", result.Draft.Dark["textMuted"]);
        Assert.Equal("#263150", result.Draft.Dark["border"]);
        Assert.Equal("#ff5c5c", result.Draft.Dark["danger"]);
        // White on that orange is 2.9:1, so the text on the brand became the dark ink, which reads.
        Assert.Equal("#0f172a", result.Draft.Dark["brandText"]);
        // The light side is derived, not found: a white page, dark text, and the brand carried over.
        Assert.Equal("#ffffff", result.Draft.Light["surface"]);
        Assert.True(Rgb.FromHex(result.Draft.Light["text"])!.Value.Luminance < 0.1);
        Assert.Contains(result.Reasons, r => r.Contains("Light colours derived from the dark", StringComparison.Ordinal));
        Assert.Equal("overpass", result.Draft.Diagram.Base);
        Assert.Equal(["red", "redDeep"], result.Draft.Diagram.Palette.Keys.Order());
    }

    [Fact]
    public void The_hero_gradients_middle_stop_is_ignored_and_so_are_font_face_and_keyframes()
    {
        ThemeExtraction result = ThemeExtractor.Extract(ThemeFixtures.DarkBrandSite);
        string everything = string.Join(' ', result.Draft.Dark.Values.Concat(result.Draft.Light.Values).Concat(result.Draft.Diagram.Palette.Values));

        Assert.DoesNotContain("#1d2a52", everything, StringComparison.Ordinal); // the gradient's middle stop
        Assert.DoesNotContain("#00ff00", everything, StringComparison.Ordinal); // inside @keyframes
        Assert.DoesNotContain("#ff00ff", everything, StringComparison.Ordinal);

        // And the other way: a sheet whose only colour is a three-stop gradient uses the two ends, not the saturated middle.
        ThemeExtraction gradient = ThemeExtractor.Extract("body { background: linear-gradient(to right, #102030, #00ff00, #304050); color: #ffffff }");
        Assert.DoesNotContain("#00ff00", gradient.Draft.Dark["brand"] + gradient.Draft.Light["brand"] + gradient.Draft.Dark["surface"], StringComparison.Ordinal);
        Assert.Equal("#102030", gradient.Draft.Dark["surface"]);
    }

    [Fact]
    public void A_light_minimal_site_with_no_brand_takes_its_link_colour_and_raises_its_faint_grey_text_to_AA()
    {
        ThemeExtraction result = ThemeExtractor.Extract(ThemeFixtures.LightMinimalSite);

        Assert.Equal("light", result.Draft.Mode);
        Assert.Equal("#ffffff", result.Draft.Light["surface"]);
        Assert.Equal("#f6f6f6", result.Draft.Light["surfaceRaised"]);
        Assert.Equal("#0a58ca", result.Draft.Light["brand"]);
        Assert.Equal("#222222", result.Draft.Light["text"]);
        Assert.Equal("neutral", result.Draft.Diagram.Base);

        // #999999 on white is 2.8:1: it was moved, by the least needed, and the move was reported with its reason.
        Assert.NotEqual("#999999", result.Draft.Light["textMuted"]);
        Assert.True(Rgb.Contrast(Rgb.FromHex(result.Draft.Light["textMuted"])!.Value, new Rgb(255, 255, 255)) >= 4.5);
        Assert.True(Rgb.Contrast(Rgb.FromHex(result.Draft.Light["textMuted"])!.Value, new Rgb(0xf6, 0xf6, 0xf6)) >= 4.5);
        Assert.Contains(result.Adjustments, a => a.StartsWith("light textMuted #999999 became ", StringComparison.Ordinal) && a.Contains("WCAG AA", StringComparison.Ordinal));
        // The outline of a control (text mixed into the surface) was the binding rule for the body text, not 4.5:1.
        Assert.Empty(ChromeContrast.Issues(Colours(result.Draft.Light)));
    }

    [Fact]
    public void A_custom_property_named_for_its_role_beats_whatever_the_page_paints_most()
    {
        ThemeExtraction result = ThemeExtractor.Extract(ThemeFixtures.CustomPropertiesOnly);

        Assert.Equal("light", result.Draft.Mode);
        Assert.Equal("#7c3aed", result.Draft.Light["brand"]);
        Assert.Equal("#fafafa", result.Draft.Light["surface"]);
        Assert.Equal("#f1f0f7", result.Draft.Light["surfaceRaised"]);
        Assert.Equal("#e5e7eb", result.Draft.Light["border"]);
        Assert.Equal("#dc2626", result.Draft.Light["danger"]);
        // The dark scheme under prefers-color-scheme is found, not derived.
        Assert.Equal("#0f0a1e", result.Draft.Dark["surface"]);
        Assert.Equal("#1a1330", result.Draft.Dark["surfaceRaised"]);
        Assert.Equal("#f3f4f6", result.Draft.Dark["text"]);
        Assert.DoesNotContain(result.Reasons, r => r.Contains("derived from", StringComparison.Ordinal));
        // A framework-internal property (--tw-ring-color) and a size or shadow are not roles.
        Assert.DoesNotContain("#00ff00", string.Join(' ', result.Draft.Light.Values.Concat(result.Draft.Dark.Values)), StringComparison.Ordinal);
        Assert.Contains(result.Reasons, r => r.Contains("custom property --brand", StringComparison.Ordinal));
    }

    [Fact]
    public void Bare_hsl_triplets_from_a_shadcn_style_sheet_are_read_for_both_schemes()
    {
        ThemeExtraction result = ThemeExtractor.Extract(ThemeFixtures.BareHslProperties);

        Assert.Equal("light", result.Draft.Mode);
        Assert.Equal("#ffffff", result.Draft.Light["surface"]);
        Assert.Equal("#020817", result.Draft.Dark["surface"], ignoreCase: true); // hsl(222.2 84% 4.9%)
        Assert.NotEqual(result.Draft.Light["brand"], result.Draft.Dark["brand"]); // --primary differs in .dark
        Assert.Equal("#ef4444", result.Draft.Light["danger"], ignoreCase: true); // --destructive: hsl(0 84.2% 60.2%)
    }

    [Fact]
    public void A_site_of_hard_coded_colours_is_read_through_rgb_hsl_named_and_hex_and_a_mostly_transparent_one_is_ignored()
    {
        ThemeExtraction result = ThemeExtractor.Extract(ThemeFixtures.HardCodedOnly);

        Assert.Equal("light", result.Draft.Mode);
        Assert.Equal("#faf5f0", result.Draft.Light["surface"]);
        Assert.Equal("#008080", result.Draft.Light["brand"]); // `teal`, a named colour; white on it is 4.8:1 so it stays as it is
        Assert.Equal("#ffffff", result.Draft.Light["brandText"]);
        // hsl(20, 10%, 15%) is #2a2522; its 52% control outline on this surface falls short of 3:1, so it was deepened, and said so.
        Assert.Equal("#231e1b", result.Draft.Light["text"]);
        Assert.Contains(result.Adjustments, a => a.StartsWith("light text #2a2522 became #231e1b", StringComparison.Ordinal) && a.Contains("control outline", StringComparison.Ordinal));
        Assert.Equal("#b3261e", result.Draft.Light["danger"]);
        // rgba(0,0,0,.04) is a faint wash, not a colour of the site: it must not become the raised surface or any token.
        Assert.DoesNotContain("#000000", result.Draft.Light.Values, StringComparer.Ordinal);
    }

    [Fact]
    public void A_hostile_sheet_still_gives_a_valid_draft_from_the_colours_it_does_hold()
    {
        ThemeExtraction result = ThemeExtractor.Extract(ThemeFixtures.Hostile);

        Assert.Equal("dark", result.Draft.Mode);
        Assert.Equal("#101820", result.Draft.Dark["surface"]);
        Assert.Equal("#ff3366", result.Draft.Dark["brand"]);
        // Colours that sat inside a string, an unterminated comment, javascript: or expression() are never read.
        string everything = string.Join(' ', result.Draft.Dark.Values.Concat(result.Draft.Light.Values));
        Assert.DoesNotContain("#00ff00", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("#ff0000", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void A_huge_garbled_sheet_is_read_within_its_limits_and_gives_a_result_or_the_one_clear_refusal()
    {
        string garbage = string.Concat(Enumerable.Repeat("a{b:c(d,e;f}}{{\"'", 40_000));
        ThemeExtraction head = ThemeExtractor.Extract("body{background:#112233;color:#eeeeee}" + garbage + garbage);
        Assert.Equal("#112233", head.Draft.Dark["surface"]);

        // Past the 2 MiB limit nothing is read: a colour after it is not used.
        string beyond = new string(' ', 2 * 1024 * 1024 + 10) + "body{background:#112233;color:#eeeeee}";
        Assert.Throws<ThemeExtractionException>(() => ThemeExtractor.Extract(beyond));

        string nothing = garbage;
        ThemeExtractionException refusal = Assert.Throws<ThemeExtractionException>(() => ThemeExtractor.Extract(nothing));
        Assert.Contains("No colours could be read", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_is_read_for_its_inline_style_and_its_theme_color_but_not_for_tags_inside_comments_or_scripts()
    {
        ThemeExtraction result = ThemeExtractor.Extract(ThemeFixtures.HtmlPage);

        Assert.Equal("#ffffff", result.Draft.Light["surface"]);
        Assert.Equal("#0f766e", result.Draft.Light["brand"]);
        Assert.Equal("#1c1917", result.Draft.Light["text"]);
        string everything = string.Join(' ', result.Draft.Light.Values.Concat(result.Draft.Dark.Values));
        Assert.DoesNotContain("#ff0000", everything, StringComparison.Ordinal); // inside an HTML comment
        Assert.DoesNotContain("#00ff00", everything, StringComparison.Ordinal); // inside a script string
        Assert.Contains(result.Reasons, r => r.Contains("button background", StringComparison.Ordinal) || r.Contains("theme-color", StringComparison.Ordinal));
    }

    [Fact]
    public void A_theme_color_meta_that_is_not_a_colour_is_ignored_and_a_greyscale_one_is_not_taken_as_the_brand()
    {
        ThemeExtraction grey = ThemeExtractor.Extract("""<meta name="theme-color" content="#ffffff"><style>body{background:#fff;color:#111}</style>""");
        Assert.Equal("mono", grey.Draft.Diagram.Base);
        Assert.Empty(grey.Draft.Diagram.Palette);

        ThemeExtraction bogus = ThemeExtractor.Extract("""<meta name="theme-color" content="not-a-colour"><style>body{background:#fff;color:#111}</style>""");
        Assert.Equal("#ffffff", bogus.Draft.Light["surface"]);
    }

    [Fact]
    public void A_greyscale_site_gets_the_mono_diagram_base_and_a_chromatic_one_does_not()
    {
        ThemeExtraction grey = ThemeExtractor.Extract("body{background:#ffffff;color:#111111} a{color:#333333} .btn{background:#222222;color:#fff}");
        ThemeExtraction coloured = ThemeExtractor.Extract(ThemeFixtures.LightMinimalSite);

        Assert.Equal("mono", grey.Draft.Diagram.Base);
        Assert.NotEqual("mono", coloured.Draft.Diagram.Base);
    }

    [Fact]
    public void The_diagram_palette_stays_inside_the_bands_that_keep_strokes_readable_on_the_canvas_and_the_export_paper()
    {
        ThemeExtraction dark = ThemeExtractor.Extract(ThemeFixtures.DarkBrandSite);
        Rgb red = Rgb.FromHex(dark.Draft.Diagram.Palette["red"])!.Value;
        Rgb redDeep = Rgb.FromHex(dark.Draft.Diagram.Palette["redDeep"])!.Value;
        Rgb navy = Rgb.FromHex("#151e33")!.Value;
        Rgb paper = new(255, 255, 255);

        Assert.True(Rgb.Contrast(red, navy) >= 3 && Rgb.Contrast(red, paper) >= 3);
        Assert.True(Rgb.Contrast(redDeep, paper) >= 4.5, "white text on the terminal fill must read");

        ThemeExtraction light = ThemeExtractor.Extract(ThemeFixtures.LightMinimalSite);
        Assert.True(Rgb.Contrast(Rgb.FromHex(light.Draft.Diagram.Palette["blue"])!.Value, paper) >= 3);
        Assert.True(Rgb.Contrast(Rgb.FromHex(light.Draft.Diagram.Palette["blueLight"])!.Value, Rgb.FromHex("#1f2937")!.Value) >= 4.5, "dark text on the pale fill must read");
    }

    [Fact]
    public void Nothing_of_the_source_beyond_hex_colours_reaches_the_draft()
    {
        ThemeExtraction result = ThemeExtractor.Extract(ThemeFixtures.DarkBrandSite);
        string json = JsonSerializer.Serialize(result);

        // Fonts stay the Overpass defaults (a site's font names are not copied), and no selector or url from the sheet appears anywhere.
        Assert.Equal("Inter, system-ui, sans-serif", result.Draft.FontUi);
        Assert.DoesNotContain("Helvetica", json, StringComparison.Ordinal);
        Assert.DoesNotContain("fonts.example.invalid", json, StringComparison.Ordinal);
        Assert.DoesNotContain("site-header", json, StringComparison.Ordinal);
        Assert.DoesNotContain(".btn", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_property_name_is_echoed_only_as_letters_digits_hyphens_and_underscores_and_at_most_forty_characters()
    {
        string name = "--brand-" + new string('x', 35) + "<script>";
        ThemeExtraction result = ThemeExtractor.Extract($":root {{ {name}: #336699; --bg: #ffffff; --text: #111111 }}");

        string reason = Assert.Single(result.Reasons, r => r.Contains("custom property --brand", StringComparison.Ordinal));
        Assert.DoesNotContain("<", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("script", reason, StringComparison.Ordinal);
        string echoed = reason[(reason.IndexOf("--", StringComparison.Ordinal))..].TrimEnd('.');
        Assert.Equal(40, echoed.Length);
    }
}
