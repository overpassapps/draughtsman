using System.Globalization;

namespace Draughtsman.Server.Theming.Extraction;

/// <summary>An opaque sRGB colour, channels 0 to 255.</summary>
internal readonly record struct Rgb(int R, int G, int B)
{
    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    /// <summary>WCAG 2.x relative luminance. Mirrors <c>relativeLuminance</c> in web/src/theme/contrast.ts.</summary>
    public double Luminance => (0.2126 * Channel(R)) + (0.7152 * Channel(G)) + (0.0722 * Channel(B));

    public static Rgb? FromHex(string? value)
    {
        string? normalised = OrgThemeValidator.NormaliseColour(value);
        if (normalised is null)
        {
            return null;
        }

        return new Rgb(
            int.Parse(normalised.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(normalised.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(normalised.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>WCAG contrast ratio, 1 to 21. Mirrors <c>contrastRatio</c> in web/src/theme/contrast.ts.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        double la = a.Luminance;
        double lb = b.Luminance;
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary><paramref name="amount"/> of <paramref name="towards"/> mixed into <paramref name="from"/>, channel by channel.</summary>
    public static Rgb Mix(Rgb from, Rgb towards, double amount)
    {
        return new Rgb(Mix(from.R, towards.R, amount), Mix(from.G, towards.G, amount), Mix(from.B, towards.B, amount));
    }

    public (double H, double S, double L) ToHsl()
    {
        double r = R / 255.0;
        double g = G / 255.0;
        double b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        if (max == min)
        {
            return (0, 0, l);
        }

        double d = max - min;
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r)
        {
            h = ((g - b) / d) + (g < b ? 6 : 0);
        }
        else if (max == g)
        {
            h = ((b - r) / d) + 2;
        }
        else
        {
            h = ((r - g) / d) + 4;
        }

        return (h * 60, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs((2 * l) - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60 % 2) - 1));
        double m = l - (c / 2);
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return new Rgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    private static int Mix(int a, int b, double amount)
    {
        return (int)Math.Clamp(Math.Floor((b * amount) + (a * (1 - amount)) + 0.5), 0, 255);
    }

    private static int ToByte(double unit)
    {
        return (int)Math.Clamp(Math.Floor((unit * 255) + 0.5), 0, 255);
    }

    private static double Channel(int value)
    {
        double c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}

/// <summary>
/// Reads one CSS colour value: hex (3, 4, 6 or 8 digits), rgb()/rgba(), hsl()/hsla() in the comma and the space syntax, the named
/// colours, and (only when asked) a bare "H S% L%" triplet as shadcn-style sheets write custom properties. Anything it cannot read
/// (transparent, currentcolor, inherit, var(), color-mix(), oklch(), a mostly transparent colour) is null: the extractor ignores what it
/// cannot parse rather than guess.
/// </summary>
internal static class CssColour
{
    /// <summary>A colour with less alpha than this is not a colour the page is seen in.</summary>
    private const double MinimumAlpha = 0.5;

    public static Rgb? Parse(string? text, bool allowBareHsl = false)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
        {
            return null;
        }

        string value = text.Trim().ToLowerInvariant();
        if (value.EndsWith("!important", StringComparison.Ordinal))
        {
            value = value[..^"!important".Length].Trim();
        }

        if (value.StartsWith('#'))
        {
            return ParseHex(value[1..]);
        }

        int open = value.IndexOf('(', StringComparison.Ordinal);
        if (open > 0 && value.EndsWith(')'))
        {
            return ParseFunction(value[..open].Trim(), value[(open + 1)..^1]);
        }

        if (Named.TryGetValue(value, out string? hex))
        {
            return ParseHex(hex[1..]);
        }

        return allowBareHsl ? ParseBareHsl(value) : null;
    }

    private static Rgb? ParseHex(string digits)
    {
        foreach (char c in digits)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return null;
            }
        }

        string full;
        switch (digits.Length)
        {
            case 3:
            case 4:
                full = string.Concat(digits.Select(c => new string(c, 2)));
                break;
            case 6:
            case 8:
                full = digits;
                break;
            default:
                return null;
        }

        if (full.Length == 8 && int.Parse(full.AsSpan(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0 < MinimumAlpha)
        {
            return null;
        }

        return new Rgb(
            int.Parse(full.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(full.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(full.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static Rgb? ParseFunction(string name, string arguments)
    {
        bool isRgb = name is "rgb" or "rgba";
        bool isHsl = name is "hsl" or "hsla";
        if (!isRgb && !isHsl)
        {
            return null;
        }

        string[] parts = arguments.Split([',', ' ', '/', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 4)
        {
            return null;
        }

        if (parts.Length == 4)
        {
            double? alpha = Number(parts[3], 1);
            if (alpha is null || alpha < MinimumAlpha)
            {
                return null;
            }
        }

        if (isRgb)
        {
            double?[] channels = [Channel(parts[0]), Channel(parts[1]), Channel(parts[2])];
            return channels.Any(c => c is null) ? null : new Rgb((int)channels[0]!.Value, (int)channels[1]!.Value, (int)channels[2]!.Value);
        }

        double? hue = Hue(parts[0]);
        double? saturation = Number(parts[1], 1, requirePercent: true);
        double? lightness = Number(parts[2], 1, requirePercent: true);
        return hue is null || saturation is null || lightness is null ? null : Rgb.FromHsl(hue.Value, saturation.Value, lightness.Value);
    }

    private static Rgb? ParseBareHsl(string value)
    {
        // "222.2 47.4% 11.2%" or "222.2 47.4% 11.2% / 0.9": only meaningful as the whole value of a custom property.
        string[] parts = value.Split([' ', '/', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 4 || !parts[1].EndsWith('%') || !parts[2].EndsWith('%'))
        {
            return null;
        }

        return ParseFunction("hsl", string.Join(' ', parts));
    }

    private static double? Hue(string token)
    {
        string t = token;
        double scale = 1;
        if (t.EndsWith("deg", StringComparison.Ordinal))
        {
            t = t[..^3];
        }
        else if (t.EndsWith("turn", StringComparison.Ordinal))
        {
            t = t[..^4];
            scale = 360;
        }

        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v * scale : null;
    }

    /// <summary>A 0-255 channel written as a number or a percentage, clamped.</summary>
    private static double? Channel(string token)
    {
        bool percent = token.EndsWith('%');
        string t = percent ? token[..^1] : token;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
        {
            return null;
        }

        return Math.Clamp(Math.Floor((percent ? v * 2.55 : v) + 0.5), 0, 255);
    }

    /// <summary>A number, or a percentage turned into 0..1 (times <paramref name="unit"/>).</summary>
    private static double? Number(string token, double unit, bool requirePercent = false)
    {
        bool percent = token.EndsWith('%');
        if (requirePercent && !percent)
        {
            return null;
        }

        string t = percent ? token[..^1] : token;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
        {
            return null;
        }

        return percent ? v / 100 * unit : v * unit;
    }

    // The CSS named colours (css-color-4). `transparent` and `currentcolor` are deliberately not here.
    private static readonly Dictionary<string, string> Named = BuildNamed();

    private static Dictionary<string, string> BuildNamed()
    {
        const string table =
            "aliceblue=f0f8ff,antiquewhite=faebd7,aqua=00ffff,aquamarine=7fffd4,azure=f0ffff,beige=f5f5dc,bisque=ffe4c4,black=000000," +
            "blanchedalmond=ffebcd,blue=0000ff,blueviolet=8a2be2,brown=a52a2a,burlywood=deb887,cadetblue=5f9ea0,chartreuse=7fff00," +
            "chocolate=d2691e,coral=ff7f50,cornflowerblue=6495ed,cornsilk=fff8dc,crimson=dc143c,cyan=00ffff,darkblue=00008b," +
            "darkcyan=008b8b,darkgoldenrod=b8860b,darkgray=a9a9a9,darkgreen=006400,darkgrey=a9a9a9,darkkhaki=bdb76b,darkmagenta=8b008b," +
            "darkolivegreen=556b2f,darkorange=ff8c00,darkorchid=9932cc,darkred=8b0000,darksalmon=e9967a,darkseagreen=8fbc8f," +
            "darkslateblue=483d8b,darkslategray=2f4f4f,darkslategrey=2f4f4f,darkturquoise=00ced1,darkviolet=9400d3,deeppink=ff1493," +
            "deepskyblue=00bfff,dimgray=696969,dimgrey=696969,dodgerblue=1e90ff,firebrick=b22222,floralwhite=fffaf0,forestgreen=228b22," +
            "fuchsia=ff00ff,gainsboro=dcdcdc,ghostwhite=f8f8ff,gold=ffd700,goldenrod=daa520,gray=808080,green=008000,greenyellow=adff2f," +
            "grey=808080,honeydew=f0fff0,hotpink=ff69b4,indianred=cd5c5c,indigo=4b0082,ivory=fffff0,khaki=f0e68c,lavender=e6e6fa," +
            "lavenderblush=fff0f5,lawngreen=7cfc00,lemonchiffon=fffacd,lightblue=add8e6,lightcoral=f08080,lightcyan=e0ffff," +
            "lightgoldenrodyellow=fafad2,lightgray=d3d3d3,lightgreen=90ee90,lightgrey=d3d3d3,lightpink=ffb6c1,lightsalmon=ffa07a," +
            "lightseagreen=20b2aa,lightskyblue=87cefa,lightslategray=778899,lightslategrey=778899,lightsteelblue=b0c4de,lightyellow=ffffe0," +
            "lime=00ff00,limegreen=32cd32,linen=faf0e6,magenta=ff00ff,maroon=800000,mediumaquamarine=66cdaa,mediumblue=0000cd," +
            "mediumorchid=ba55d3,mediumpurple=9370db,mediumseagreen=3cb371,mediumslateblue=7b68ee,mediumspringgreen=00fa9a," +
            "mediumturquoise=48d1cc,mediumvioletred=c71585,midnightblue=191970,mintcream=f5fffa,mistyrose=ffe4e1,moccasin=ffe4b5," +
            "navajowhite=ffdead,navy=000080,oldlace=fdf5e6,olive=808000,olivedrab=6b8e23,orange=ffa500,orangered=ff4500,orchid=da70d6," +
            "palegoldenrod=eee8aa,palegreen=98fb98,paleturquoise=afeeee,palevioletred=db7093,papayawhip=ffefd5,peachpuff=ffdab9,peru=cd853f," +
            "pink=ffc0cb,plum=dda0dd,powderblue=b0e0e6,purple=800080,rebeccapurple=663399,red=ff0000,rosybrown=bc8f8f,royalblue=4169e1," +
            "saddlebrown=8b4513,salmon=fa8072,sandybrown=f4a460,seagreen=2e8b57,seashell=fff5ee,sienna=a0522d,silver=c0c0c0,skyblue=87ceeb," +
            "slateblue=6a5acd,slategray=708090,slategrey=708090,snow=fffafa,springgreen=00ff7f,steelblue=4682b4,tan=d2b48c,teal=008080," +
            "thistle=d8bfd8,tomato=ff6347,turquoise=40e0d0,violet=ee82ee,wheat=f5deb3,white=ffffff,whitesmoke=f5f5f5,yellow=ffff00," +
            "yellowgreen=9acd32";
        return table.Split(',').Select(entry => entry.Split('=')).ToDictionary(pair => pair[0], pair => "#" + pair[1], StringComparer.Ordinal);
    }
}
