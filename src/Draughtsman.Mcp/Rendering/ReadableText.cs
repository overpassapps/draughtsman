using System.Globalization;

namespace Draughtsman.Mcp.Rendering;

/// <summary>
/// The text colour that reads on a chosen fill (QA-06). Mirrors <c>web/src/theme/contrast.ts</c> <c>readableTextOn</c>
/// so a node painted with a yellow fill has dark text in an exported picture as it does on the canvas: the preferred
/// colour while it reaches WCAG AA (4.5:1), otherwise the better of the dark and light fallbacks. A colour that is not
/// a hex value is left alone, since there is nothing to measure.
/// </summary>
internal static class ReadableText
{
    internal const string Dark = "#0f172a";
    internal const string Light = "#ffffff";
    private const double AaText = 4.5;

    internal static string On(string preferred, string fill)
    {
        double? ratio = Contrast(preferred, fill);
        if (ratio is null || ratio >= AaText)
        {
            return preferred;
        }

        double dark = Contrast(Dark, fill) ?? 0;
        double light = Contrast(Light, fill) ?? 0;
        return dark >= light ? Dark : Light;
    }

    private static double? Contrast(string foreground, string background)
    {
        if (Luminance(foreground) is not { } a || Luminance(background) is not { } b)
        {
            return null;
        }

        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>Relative luminance of #rgb, #rrggbb or #rrggbbaa (alpha ignored); null for anything else.</summary>
    private static double? Luminance(string colour)
    {
        string value = colour.Trim();
        if (value.Length < 2 || value[0] != '#' || !value.Skip(1).All(Uri.IsHexDigit))
        {
            return null;
        }

        string hex = value[1..];
        if (hex.Length is 3 or 4)
        {
            hex = string.Concat(hex.Take(3).Select(c => $"{c}{c}"));
        }
        else if (hex.Length is 6 or 8)
        {
            hex = hex[..6];
        }
        else
        {
            return null;
        }

        double Channel(int index)
        {
            double c = int.Parse(hex.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(1)) + (0.0722 * Channel(2));
    }
}
