using System.Net;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The Overpass brand assets kept in themes/assets (spec section 8): the logo is the maintainer's real red wordmark, cleaned
/// to a single self-contained SVG, plus a white variant so a dark chrome theme's navy header reads; the favicon is a
/// square bridge mark lifted from the same artwork, because a wide wordmark is unreadable at tab-icon size. The shipped
/// default theme no longer names any of them - the Draughtsman mark and favicon the web app ships stand in until an
/// organisation supplies its own - so the serving tests run on <see cref="LogoThemedServerFactory"/>, a theme that does,
/// and one test pins the default naming none. These read the files copied next to the test binary, so they fail if someone
/// drops the old placeholder back in, leaves a DOCTYPE or a 100%-sized root in a new export, or lets the wordmark's deck
/// run off the favicon - they do not compare against a fixture built from the same constant.
/// </summary>
public class OverpassThemeAssetTests
{
    // The old placeholder artwork, so an accidental revert is caught rather than passing.
    private const string PlaceholderViewBox = "viewBox=\"0 0 32 32\"";
    private const string PlaceholderChevron = "M9 22 L16 9 L23 22";

    private static string AssetPath(string name) => Path.Combine(AppContext.BaseDirectory, "themes", name);

    private static string Read(string name) => File.ReadAllText(AssetPath(name));

    [Fact]
    public void Red_logo_is_the_cleaned_wordmark()
    {
        string svg = Read("overpass-logo.svg");

        AssertCleanedWordmark(svg);
        Assert.Contains("fill:#ff0000", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("fill:#ffffff", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Light_logo_is_the_cleaned_white_wordmark()
    {
        string svg = Read("overpass-logo-light.svg");

        AssertCleanedWordmark(svg);
        Assert.Contains("fill:#ffffff", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("fill:#ff0000", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_logos_are_header_marks_derived_from_the_wordmark_and_differ_only_in_colour()
    {
        string red = Read("overpass-logo-compact.svg");
        string white = Read("overpass-logo-compact-light.svg");

        Assert.Contains("fill=\"#ff0000\"", red, StringComparison.Ordinal);
        Assert.DoesNotContain("#ffffff", red, StringComparison.Ordinal);
        Assert.Contains("fill=\"#ffffff\"", white, StringComparison.Ordinal);
        Assert.DoesNotContain("#ff0000", white, StringComparison.Ordinal);
        Assert.Equal(red.Replace("#ff0000", "#ffffff", StringComparison.Ordinal), white);

        // Thickened bridge strokes, and the "apps" line (paths 2 to 5 of the brand file, y 380 and below) left out.
        Assert.Contains("stroke-width=", red, StringComparison.Ordinal);
        Assert.Contains("M452.708,442.708", red, StringComparison.Ordinal);
        Assert.DoesNotContain("M1660.8,380.842", red, StringComparison.Ordinal);
        Assert.DoesNotContain("<!DOCTYPE", red, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Overpass\"", red, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_brand_logos_are_left_unchanged_by_the_compact_derivation()
    {
        // The maintainer-supplied files keep their hairline strokes and their "apps" line.
        Assert.DoesNotContain("stroke-width=", Read("overpass-logo.svg"), StringComparison.Ordinal);
        Assert.Contains("M1660.8,380.842", Read("overpass-logo.svg"), StringComparison.Ordinal);
        Assert.Contains("M1660.8,380.842", Read("overpass-logo-light.svg"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_shipped_default_theme_names_no_logo_or_favicon_so_the_apps_own_mark_stands_in()
    {
        using DraughtsmanServerFactory factory = new();
        HttpClient client = factory.CreateClient();

        JsonNode manifest = JsonNode.Parse(await client.GetStringAsync("/api/theme", CancellationToken.None))!;
        Assert.Equal("Draughtsman", manifest["productName"]!.GetValue<string>());
        foreach (string member in new[] { "logo", "logoLight", "logoCompact", "logoCompactLight", "favicon" })
        {
            Assert.Null(manifest[member]);
        }

        foreach (string path in new[] { "logo", "logo-light", "logo-compact", "logo-compact-light", "favicon" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/theme/{path}", CancellationToken.None)).StatusCode);
        }
    }

    [Fact]
    public async Task A_theme_that_names_them_serves_the_compact_marks_and_keeps_the_full_logo_for_the_login_page()
    {
        using LogoThemedServerFactory factory = new();
        HttpClient client = factory.CreateClient();

        JsonNode manifest = JsonNode.Parse(await client.GetStringAsync("/api/theme", CancellationToken.None))!;
        Assert.Equal("/api/theme/logo-compact", manifest["logoCompact"]!.GetValue<string>());
        Assert.Equal("/api/theme/logo-compact-light", manifest["logoCompactLight"]!.GetValue<string>());
        Assert.Equal("/api/theme/logo", manifest["logo"]!.GetValue<string>());

        Assert.Equal(File.ReadAllBytes(AssetPath("overpass-logo-compact.svg")), await client.GetByteArrayAsync("/api/theme/logo-compact", CancellationToken.None));
        Assert.Equal(File.ReadAllBytes(AssetPath("overpass-logo-compact-light.svg")), await client.GetByteArrayAsync("/api/theme/logo-compact-light", CancellationToken.None));
        Assert.Equal(File.ReadAllBytes(AssetPath("overpass-logo-light.svg")), await client.GetByteArrayAsync("/api/theme/logo", CancellationToken.None));
    }

    [Fact]
    public void Favicon_is_a_square_mark_from_the_logo_artwork()
    {
        string svg = Read("overpass-favicon.svg");

        Assert.DoesNotContain("<!DOCTYPE", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<?xml", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("width=\"100%\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("height=\"100%\"", svg, StringComparison.Ordinal);
        Assert.Contains("role=\"img\"", svg, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Overpass\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill:#ff0000", svg, StringComparison.Ordinal);

        // A square viewBox with clear margin each side, so the whole bridge sits inside the rounded tile.
        (double minX, double minY, double width, double height) = ReadViewBox(svg);
        Assert.Equal(width, height);
        Assert.True(minX <= -0.08 * width, $"left margin {minX} is not at least 8% of {width}");
        Assert.True(minY <= -0.08 * height, $"top margin {minY} is not at least 8% of {height}");

        // The bridge symbol from the real wordmark, and not the old placeholder.
        Assert.Contains("M452.708,442.708", svg, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaceholderChevron, svg, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaceholderViewBox, svg, StringComparison.Ordinal);

        // The wordmark's deck/right-support subpath was dropped, so the deck does not run off the tile.
        Assert.DoesNotContain("1626.88", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("1982", svg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Served_logo_and_favicon_are_the_files_the_theme_names()
    {
        using LogoThemedServerFactory factory = new();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage logo = await client.GetAsync("/api/theme/logo", CancellationToken.None);
        HttpResponseMessage favicon = await client.GetAsync("/api/theme/favicon", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal(HttpStatusCode.OK, favicon.StatusCode);
        Assert.Equal("image/svg+xml", logo.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/svg+xml", favicon.Content.Headers.ContentType?.MediaType);

        // The server embeds and serves the cleaned file byte for byte, and the dark chrome theme names the light
        // variant, so what a browser gets is the white wordmark on the navy header.
        Assert.Equal(File.ReadAllBytes(AssetPath("overpass-logo-light.svg")), await logo.Content.ReadAsByteArrayAsync(CancellationToken.None));
        Assert.Equal(File.ReadAllBytes(AssetPath("overpass-favicon.svg")), await favicon.Content.ReadAsByteArrayAsync(CancellationToken.None));
    }

    private static void AssertCleanedWordmark(string svg)
    {
        Assert.DoesNotContain("<!DOCTYPE", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<?xml", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("width=\"100%\"", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("height=\"100%\"", svg, StringComparison.Ordinal);
        Assert.Contains("role=\"img\"", svg, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Overpass\"", svg, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 1986 443\"", svg, StringComparison.Ordinal);

        // Self-contained: no script, no external reference, no leftover xlink namespace, no placeholder.
        Assert.DoesNotContain("<script", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("xlink", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("href", svg, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaceholderChevron, svg, StringComparison.Ordinal);
        Assert.DoesNotContain(PlaceholderViewBox, svg, StringComparison.Ordinal);
    }

    private static (double MinX, double MinY, double Width, double Height) ReadViewBox(string svg)
    {
        int start = svg.IndexOf("viewBox=\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "asset has no viewBox");
        start += "viewBox=\"".Length;
        int end = svg.IndexOf('"', start);
        Assert.True(end >= 0, "asset has an unclosed viewBox");
        string[] parts = svg[start..end].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, parts.Length);
        return (double.Parse(parts[0]), double.Parse(parts[1]), double.Parse(parts[2]), double.Parse(parts[3]));
    }
}
