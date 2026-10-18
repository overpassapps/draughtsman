using System.Globalization;
using System.Text;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import.Drawio;

namespace Draughtsman.Core.Import.Visio;

/// <summary>
/// Carries a Visio shape's basic fill, line and text style into the model, and Visio's numbered line ends into <see cref="ArrowHead"/>. It does
/// it by writing the draw.io style string for what the shape set (<c>fillColor</c>, <c>strokeColor</c>, <c>dashed</c> ...) and handing that to
/// <see cref="DrawioStyleMapper"/>, so a colour or dash means the same in both importers and the one place that checks a colour (hex only, so
/// nothing from a stranger's file reaches an SVG paint attribute) is used by both. Every value written into the style string is one this class
/// built from a number or a validated hex colour, never text copied from the file.
/// </summary>
internal static class VisioStyleMapper
{
    /// <summary>The style of a node: only what the shape (or its master) wrote down. Null members are the diagram theme's.</summary>
    public static DrawioStyle NodeStyle(VisioShapeView view, IReadOnlyDictionary<int, string> palette)
    {
        var style = new StringBuilder();
        if (view.StyleCell("FillPattern") is "0")
        {
            style.Append("fillColor=none;");
        }
        else if (Colour(view.StyleCell("FillForegnd"), palette) is { } fill)
        {
            style.Append("fillColor=").Append(fill).Append(';');
        }

        Line(view, palette, style);
        if (Colour(view.Shape.Cells.GetValueOrDefault("Char.Color"), palette) is { } text)
        {
            style.Append("fontColor=").Append(text).Append(';');
        }

        if (VisioXml.Number(view.Shape.Cells.GetValueOrDefault("Char.Size")) is { } size && size > 0 && size * VisioLimits.UnitsPerInch <= 1000)
        {
            style.Append("fontSize=").Append(Units(size)).Append(';');
        }

        if (VisioXml.Number(view.Shape.Cells.GetValueOrDefault("Char.Style")) is { } bits && ((int)bits & 1) == 1)
        {
            style.Append("fontStyle=1;");
        }

        return DrawioStyle.Parse(style.ToString());
    }

    /// <summary>The style of a connector: its line only (colour, weight, dash).</summary>
    public static DrawioStyle EdgeStyle(VisioShapeView view, IReadOnlyDictionary<int, string> palette)
    {
        var style = new StringBuilder();
        Line(view, palette, style);
        return DrawioStyle.Parse(style.ToString());
    }

    private static void Line(VisioShapeView view, IReadOnlyDictionary<int, string> palette, StringBuilder style)
    {
        string? pattern = view.StyleCell("LinePattern");
        if (pattern is "0")
        {
            style.Append("strokeColor=none;");
            return;
        }

        if (Colour(view.StyleCell("LineColor"), palette) is { } stroke)
        {
            style.Append("strokeColor=").Append(stroke).Append(';');
        }

        if (VisioXml.Number(view.StyleCell("LineWeight")) is { } weight && weight > 0 && weight * VisioLimits.UnitsPerInch <= 100)
        {
            style.Append("strokeWidth=").Append(Units(weight)).Append(';');
        }

        // Visio's pattern 1 is solid and 3 is dotted; the rest (dash, dash-dot, long dash, ...) are dashes of some kind.
        if (VisioXml.Number(pattern) is { } number and > 1)
        {
            style.Append(number == 3 ? "dashed=1;dashPattern=1 4;" : "dashed=1;");
        }
    }

    /// <summary>Inches (a Visio length) as canvas units, to two decimals, in the invariant culture.</summary>
    private static string Units(double inches)
    {
        return Math.Round(inches * VisioLimits.UnitsPerInch, 2).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A colour cell as hex: <c>#RRGGBB</c> as written, or a number as an index into the document's colour table. Anything else (Visio's
    /// <c>Themed</c>, which asks the theme, or a table entry that is not hex) is null: the diagram theme's colour.
    /// </summary>
    public static string? Colour(string? value, IReadOnlyDictionary<int, string> palette)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = value.Trim();
        if (text[0] == '#')
        {
            return IsHex(text) ? text : null;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && palette.TryGetValue(index, out string? entry) && IsHex(entry) ? entry : null;
    }

    private static bool IsHex(string text)
    {
        return text.Length is 4 or 7 && text[0] == '#' && text.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
    }

    // ---- line ends ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Visio's numbered arrowheads (0 is none, 1 to 45 are the entries of its Line dialog) as the nearest <see cref="ArrowHead"/>, using the same
    /// table draw.io's own Visio importer does: a filled triangle (4, 13 and others) is an arrow, a hollow one is UML's generalisation head, a
    /// diamond is aggregation, the circles are circles and 24 to 30 are the entity-relationship (crow's foot) ends.
    /// </summary>
    /// <param name="value">The cell's value: a number, <c>Themed</c> (the theme chooses), or nothing.</param>
    /// <param name="resolved">False when the file did not say (no cell, or <c>Themed</c>): the head is then none and the caller decides what Visio's theme would have drawn.</param>
    /// <param name="approximated">Told when the number is past Visio's table, so the head was replaced by a plain arrow.</param>
    public static ArrowHead Head(string? value, bool atStart, out bool resolved, Action<string> approximated)
    {
        resolved = false;
        if (!int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int index))
        {
            return ArrowHead.None;
        }

        resolved = true;
        switch (index)
        {
            case 0:
                return ArrowHead.None;
            case 1 or 3 or 7 or 9 or 12 or 17 or 19 or 43 or 44 or 45:
                return ArrowHead.Open;
            case 2 or 4 or 5 or 6 or 8 or 13 or 39:
                return ArrowHead.Arrow;
            case 10 or 20 or 31 or 32 or 33 or 34 or 35 or 36 or 37 or 38 or 41 or 42:
                return ArrowHead.Circle;
            case 11:
                return ArrowHead.Diamond;
            case 14 or 15 or 16 or 18 or 40:
                return ArrowHead.Inheritance;
            case 21 or 22:
                return ArrowHead.Aggregation;
            case 23 or 24:
                return ArrowHead.One;
            case 25 or 26:
                return ArrowHead.OneAndOnlyOne;
            case 27:
                return ArrowHead.Many;
            case 28:
                return ArrowHead.OneOrMany;
            case 29:
                return ArrowHead.ZeroOrMany;
            case 30:
                return ArrowHead.ZeroOrOne;
            default:
                approximated($"the arrowhead number {index.ToString(CultureInfo.InvariantCulture)} at its {(atStart ? "start" : "end")}, which has no equivalent, so a plain arrowhead was used");
                return ArrowHead.Arrow;
        }
    }
}
