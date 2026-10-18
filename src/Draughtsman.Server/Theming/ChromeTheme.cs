using Draughtsman.Core.Media;

namespace Draughtsman.Server.Theming;

/// <summary>
/// The chrome theme (spec section 8): the app shell's tokens as plain data. Loaded at startup from the file named by
/// <c>theme.chrome</c>; see <see cref="ChromeThemeLoader"/> for resolution and fallback.
/// </summary>
internal sealed class ChromeTheme
{
    public required string Name { get; init; }

    public required string ProductName { get; init; }

    public string? Logo { get; init; }

    /// <summary>
    /// Optional light-on-dark logo. The dark chrome theme is drawn on a dark surface, so a logo that only reads on a
    /// light background needs a light companion; when the theme names none, the single <see cref="Logo"/> is used.
    /// </summary>
    public string? LogoLight { get; init; }

    /// <summary>
    /// Optional header variant of <see cref="Logo"/>: a wide wordmark's hairlines vanish at header height, so a theme
    /// may name a simplified mark for the top bar only. Drawn on a light surface; <see cref="LogoCompactLight"/> is its
    /// light-on-dark companion. Never used for the login, first-run or About pages, and never when an admin uploads a logo.
    /// </summary>
    public string? LogoCompact { get; init; }

    /// <summary>Optional light-on-dark companion to <see cref="LogoCompact"/>; falls back to it when absent.</summary>
    public string? LogoCompactLight { get; init; }

    public string? Favicon { get; init; }

    /// <summary>dark | light | system; <c>system</c> follows the OS and CSS keeps both modes.</summary>
    public required string Mode { get; init; }

    /// <summary>The colour set for <see cref="Mode"/> (dark when the mode is system).</summary>
    public required IReadOnlyDictionary<string, string> Colors { get; init; }

    public IReadOnlyDictionary<string, string>? Light { get; init; }

    public IReadOnlyDictionary<string, string>? Dark { get; init; }

    public required double Radius { get; init; }

    public required IReadOnlyDictionary<string, string> Font { get; init; }
}

/// <summary>A resolved theme together with where it came from and the logo/favicon bytes it names.</summary>
internal sealed class LoadedChromeTheme
{
    public required ChromeTheme Theme { get; init; }

    /// <summary>A file path or an embedded-resource description, for the startup log line.</summary>
    public required string Source { get; init; }

    public ChromeThemeAsset? Logo { get; init; }

    public ChromeThemeAsset? LogoLight { get; init; }

    public ChromeThemeAsset? LogoCompact { get; init; }

    public ChromeThemeAsset? LogoCompactLight { get; init; }

    public ChromeThemeAsset? Favicon { get; init; }
}

/// <summary>One asset the theme names, already read so the endpoint never touches the disk per request.</summary>
internal sealed record ChromeThemeAsset(string FileName, byte[] Content, string ContentType);

/// <summary>
/// The branding actually served, after any admin overrides stored under the data folder are layered over the theme
/// file's own product name and assets. The theme is the fallback; removing the last override (or the shipped Overpass
/// theme when none is configured) is the default. <see cref="LogoLight"/> falls back to <see cref="Logo"/> when the
/// admin gave no light-on-dark variant, so a caller always has something to draw. The compact header logos are the
/// theme file's own and are null as soon as an admin uploads either logo: an uploaded logo is the brand the admin
/// chose and is drawn in the header as it is, never swapped for the theme's simplified mark.
/// </summary>
internal sealed record EffectiveBranding(
    string ThemeName,
    string ProductName,
    string Mode,
    ChromeThemeAsset? Logo,
    ChromeThemeAsset? LogoLight,
    ChromeThemeAsset? LogoCompact,
    ChromeThemeAsset? LogoCompactLight,
    ChromeThemeAsset? Favicon,
    BrandingOverrides Overrides);

/// <summary>The admin's stored overrides; null per field means "use the theme file's value".</summary>
internal sealed record BrandingOverrides(string? ProductName, string? Logo, string? LogoLight, string? Favicon)
{
    public static readonly BrandingOverrides Empty = new(null, null, null, null);

    public bool HasProductName => !string.IsNullOrEmpty(ProductName);

    public bool Has(BrandingSlot slot)
    {
        return FileName(slot) is not null;
    }

    public string? FileName(BrandingSlot slot)
    {
        return slot switch
        {
            BrandingSlot.Logo => Logo,
            BrandingSlot.LogoLight => LogoLight,
            BrandingSlot.Favicon => Favicon,
            _ => null,
        };
    }

    public BrandingOverrides With(BrandingSlot slot, string? fileName)
    {
        return slot switch
        {
            BrandingSlot.Logo => this with { Logo = fileName },
            BrandingSlot.LogoLight => this with { LogoLight = fileName },
            BrandingSlot.Favicon => this with { Favicon = fileName },
            _ => this,
        };
    }
}
