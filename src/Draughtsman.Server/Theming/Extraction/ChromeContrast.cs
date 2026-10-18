namespace Draughtsman.Server.Theming.Extraction;

/// <summary>
/// The chrome contrast checks the Themes page shows beside a theme, in C#, so a draft can be made to pass them before it is
/// offered. A mirror of <c>chromeContrastIssues</c> in web/src/theme/themeContrast.ts: the same pairs, the same minimums, the same
/// derived control outline. <c>ChromeContrastMirrorTests</c> reads that file and fails when the pair list or the outline mix drift.
/// </summary>
internal static class ChromeContrast
{
    public const double AaText = 4.5;

    public const double AaGraphic = 3;

    /// <summary>How much of the text colour a control outline takes when mixed into the surface (CONTROL_BORDER_MIX).</summary>
    public const double ControlBorderMix = 0.52;

    /// <summary>[foreground, background, minimum], in the order of CHROME_CONTRAST_PAIRS.</summary>
    public static readonly IReadOnlyList<(string Foreground, string Background, double Minimum)> Pairs =
    [
        ("text", "surface", AaText),
        ("textMuted", "surface", AaText),
        ("text", "surfaceRaised", AaText),
        ("textMuted", "surfaceRaised", AaText),
        ("brandText", "brand", AaText),
        ("accent", "surface", AaGraphic),
        ("danger", "surface", AaGraphic),
        ("danger", "surfaceRaised", AaGraphic),
    ];

    /// <summary>The colour a control outline resolves to: <see cref="ControlBorderMix"/> of text over surface.</summary>
    public static Rgb ControlBorder(Rgb text, Rgb surface)
    {
        return Rgb.Mix(surface, text, ControlBorderMix);
    }

    /// <summary>The subjects (<c>text/surface</c>, <c>controlBorder/surfaceRaised</c>) of every check one mode's colours fail; empty when they all pass.</summary>
    public static IReadOnlyList<string> Issues(IReadOnlyDictionary<string, Rgb> colours)
    {
        List<string> issues = [];
        foreach ((string foreground, string background, double minimum) in Pairs)
        {
            if (colours.TryGetValue(foreground, out Rgb fg) && colours.TryGetValue(background, out Rgb bg) && Rgb.Contrast(fg, bg) < minimum)
            {
                issues.Add($"{foreground}/{background}");
            }
        }

        if (colours.TryGetValue("text", out Rgb text) && colours.TryGetValue("surface", out Rgb surface))
        {
            Rgb outline = ControlBorder(text, surface);
            foreach (string background in new[] { "surface", "surfaceRaised" })
            {
                if (colours.TryGetValue(background, out Rgb bg) && Rgb.Contrast(outline, bg) < AaGraphic)
                {
                    issues.Add($"controlBorder/{background}");
                }
            }
        }

        return issues;
    }
}
