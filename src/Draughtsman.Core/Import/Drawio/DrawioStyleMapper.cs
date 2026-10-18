using System.Globalization;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;

namespace Draughtsman.Core.Import.Drawio;

/// <summary>
/// Carries a draw.io style into the sparse style of a node or an edge: only what the model can hold, and only what the file set. A colour that is
/// not a hex colour, and an arrowhead with no equivalent, are reported through the <c>approximated</c> callback (the importer turns each into a
/// <see cref="ImportWarningCodes.StyleApproximated"/> warning) and replaced by nothing or by the nearest, never by an error.
/// </summary>
internal static class DrawioStyleMapper
{
    private const int MaxFontLength = 100;

    /// <summary>
    /// The colour as the model holds it: a hex colour as written, <c>none</c> as <c>transparent</c>, the light half of a <c>light-dark(light,dark)</c>
    /// pair, and null for everything else (<c>default</c> and <c>inherit</c> mean "the theme's", which is what an absent colour already is). Only hex
    /// reaches the model, so no function, URL or variable from a stranger's file can end up in an SVG paint attribute.
    /// </summary>
    /// <param name="value">The raw style value.</param>
    /// <param name="approximated">True when the value was something else a person wrote (a colour name, <c>rgb()</c>) and is being dropped.</param>
    public static string? Colour(string? value, out bool approximated)
    {
        approximated = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = value.Trim();
        if (text.StartsWith("light-dark(", StringComparison.OrdinalIgnoreCase) && text.EndsWith(')'))
        {
            string inner = text["light-dark(".Length..^1];
            int comma = inner.IndexOf(',', StringComparison.Ordinal);
            return Colour(comma < 0 ? inner : inner[..comma], out approximated);
        }

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return "transparent";
        }

        if (text.Equals("default", StringComparison.OrdinalIgnoreCase) || text.Equals("inherit", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (IsHex(text))
        {
            return text;
        }

        approximated = true;
        return null;
    }

    private static bool IsHex(string text)
    {
        return text.Length is 4 or 5 or 7 or 9 && text[0] == '#' && text.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    }

    /// <summary>The sparse style of a node, or null when the file set nothing the model can hold.</summary>
    /// <param name="isText">A text shape is borderless and fill-less by kind, so draw.io's explicit <c>none</c>s for it are not repeated.</param>
    public static NodeStyle? ForNode(DrawioStyle style, bool isText, Action<string> approximated)
    {
        string? fill = ColourOf(style, "fillColor", "fill colour", approximated);
        if (isText && fill == "transparent")
        {
            fill = null;
        }

        string? stroke = null;
        double? strokeWidth = style.Number("strokeWidth") is { } width and >= 0 ? width : null;
        if (string.Equals(style.Get("strokeColor")?.Trim(), "none", StringComparison.OrdinalIgnoreCase))
        {
            // No stroke is a stroke of no width: the model has no "none" colour.
            strokeWidth = isText ? null : 0;
        }
        else
        {
            stroke = ColourOf(style, "strokeColor", "outline colour", approximated);
        }

        string? textColour = ColourOf(style, "fontColor", "text colour", approximated);
        double? fontSize = style.Number("fontSize") is { } size and >= 0 and <= 1000 ? size : null;
        bool bold = style.Number("fontStyle") is { } fontStyle && ((int)fontStyle & 1) == 1;

        var result = new NodeStyle
        {
            Fill = fill,
            Stroke = stroke,
            StrokeWidth = strokeWidth,
            TextColor = textColour,
            Font = FontOf(style, approximated),
            Dash = DashOf(style),
            FontSize = fontSize,
            Bold = bold ? true : null,
        };
        return result == new NodeStyle() ? null : result;
    }

    /// <summary>The line style of an edge (stroke, width, dash), shared by <see cref="ForEdge"/>; null members are the kind's own.</summary>
    public static EdgeStyle? ForEdge(DrawioStyle style, string kind, ArrowHead start, ArrowHead end, Action<string> approximated)
    {
        (ArrowHead kindStart, ArrowHead kindEnd, Dash kindDash) = KindDefault(kind);
        Dash? dash = DashOf(style) ?? Dash.Solid;
        double? width = style.Number("strokeWidth") is { } w and >= 0 ? w : null;

        var result = new EdgeStyle
        {
            Stroke = ColourOf(style, "strokeColor", "line colour", approximated) is { } stroke and not "transparent" ? stroke : null,
            StrokeWidth = width,
            Dash = dash == kindDash ? null : dash,
            ArrowStart = start == kindStart ? null : start,
            ArrowEnd = end == kindEnd ? null : end,
        };
        return result == new EdgeStyle() ? null : result;
    }

    /// <summary>What the diagram themes draw for an edge of this kind, so the style written is only the difference.</summary>
    private static (ArrowHead Start, ArrowHead End, Dash Dash) KindDefault(string kind)
    {
        return kind switch
        {
            "data" => (ArrowHead.None, ArrowHead.Open, Dash.Solid),
            "dependency" => (ArrowHead.None, ArrowHead.Open, Dash.Dashed),
            "association" => (ArrowHead.None, ArrowHead.None, Dash.Solid),
            _ => (ArrowHead.None, ArrowHead.Arrow, Dash.Solid),
        };
    }

    /// <summary>The arrowhead at one end of a connector: draw.io's <c>endArrow</c> (default <c>classic</c>) or <c>startArrow</c> (default none).</summary>
    public static ArrowHead Head(DrawioStyle style, bool atStart, Action<string> approximated)
    {
        string? name = style.Get(atStart ? "startArrow" : "endArrow")?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return atStart ? ArrowHead.None : ArrowHead.Arrow;
        }

        string? fillValue = style.Get(atStart ? "startFill" : "endFill");
        bool filled = fillValue is not ("0" or "false");
        switch (name.ToLowerInvariant())
        {
            case "none":
                return ArrowHead.None;
            case "classic" or "classicthin":
                return filled ? ArrowHead.Arrow : ArrowHead.Open;
            case "block" or "blockthin":
                // A hollow triangle is UML's generalisation arrow, which the model has as its own head.
                return filled ? ArrowHead.Arrow : ArrowHead.Inheritance;
            case "open" or "openthin":
                return ArrowHead.Open;
            case "oval":
                return ArrowHead.Circle;
            case "diamond":
                return filled ? ArrowHead.Diamond : ArrowHead.Aggregation;
            case "diamondthin":
                return filled ? ArrowHead.Composition : ArrowHead.Aggregation;
            case "erone":
                return ArrowHead.One;
            case "ermandone":
                return ArrowHead.OneAndOnlyOne;
            case "ermany":
                return ArrowHead.Many;
            case "eronetomany":
                return ArrowHead.OneOrMany;
            case "erzerotoone":
                return ArrowHead.ZeroOrOne;
            case "erzerotomany":
                return ArrowHead.ZeroOrMany;
            default:
                approximated($"the arrowhead '{name}' at its {(atStart ? "start" : "end")}, which has no equivalent, so a plain arrowhead was used");
                return ArrowHead.Arrow;
        }
    }

    /// <summary>True for the heads that spell a relationship (entity-relationship and UML), which make a connector an association rather than a flow.</summary>
    public static bool IsNotation(ArrowHead head)
    {
        return head is ArrowHead.One or ArrowHead.Many or ArrowHead.ZeroOrOne or ArrowHead.OneOrMany or ArrowHead.ZeroOrMany or ArrowHead.OneAndOnlyOne
            or ArrowHead.Inheritance or ArrowHead.Aggregation or ArrowHead.Composition;
    }

    private static string? ColourOf(DrawioStyle style, string key, string what, Action<string> approximated)
    {
        string? raw = style.Get(key);
        string? colour = Colour(raw, out bool dropped);
        if (dropped)
        {
            approximated($"the {what} '{Shorten(raw!)}', which is not a hex colour and was not carried");
        }

        return colour;
    }

    private static string? FontOf(DrawioStyle style, Action<string> approximated)
    {
        string? font = style.Get("fontFamily")?.Trim();
        if (string.IsNullOrEmpty(font))
        {
            return null;
        }

        // A font family list is written into SVG and CSS: only the characters a family name has.
        if (font.Length > MaxFontLength || !font.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or ',' or '\''))
        {
            approximated($"the font '{Shorten(font)}', which is not a plain font name and was not carried");
            return null;
        }

        return font;
    }

    private static Dash? DashOf(DrawioStyle style)
    {
        if (!style.Flag("dashed"))
        {
            return null;
        }

        // draw.io draws a dotted line as a dash pattern of short marks ("1 4"); a pattern of longer marks is dashed.
        string? pattern = style.Get("dashPattern");
        if (pattern is not null)
        {
            int space = pattern.IndexOf(' ', StringComparison.Ordinal);
            string first = space < 0 ? pattern : pattern[..space];
            if (double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out double mark) && mark <= 1.5)
            {
                return Dash.Dotted;
            }
        }

        return Dash.Dashed;
    }

    private static string Shorten(string text)
    {
        return text.Length <= 40 ? text : text[..40] + "...";
    }
}
