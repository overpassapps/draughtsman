using System.Text;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Mcp.Rendering;

/// <summary>A node's label rectangle in the node's own space: its top-left corner and size, in canvas units.</summary>
public readonly record struct LabelRect(double X, double Y, double W, double H);

/// <summary>
/// How a node's label is broken into lines and where it sits, mirroring the web canvas (<c>web/src/canvas/text.ts</c> and
/// <c>NodeView.tsx</c>'s <c>Label</c>) so a server render, a thumbnail and a PDF wrap a label where the editor does. SVG has no
/// automatic line wrapping, so the renderer breaks the label itself: greedy word wrap to the label rectangle's width less
/// <see cref="Padding"/> on each side, an explicit newline always a break, a word wider than a line split between characters.
/// <para>
/// Widths come from the same character-class estimate the canvas falls back on where nothing is laid out
/// (<c>textEstimate.ts</c> <c>estimateTextWidth</c>), not from a font: the server has no font to measure, and the estimate is
/// what the sequence scene already sizes participant headers with. It runs a little wider than Helvetica or Arial, so a
/// line it accepts fits those with room to spare; a face much wider than that (DejaVu Sans) can still run into the padding.
/// Like the canvas, a block taller than its rectangle is centred on it and overflows: nothing shrinks the font or clips.
/// </para>
/// <para>
/// The label rectangles are <c>web/src/canvas/shapes.tsx</c>'s <c>labelBox</c> for each shape, copied number for number; change
/// one and read the other. <c>NodeLabelLayoutTests</c> pins literal wrapped lines against <c>textLayout.test.ts</c>'s.
/// </para>
/// </summary>
public static class NodeLabelLayout
{
    /// <summary>Space between a label rectangle's edge and its text (<c>LABEL_PADDING</c>).</summary>
    public const double Padding = 8;

    /// <summary>The font size the renderer draws a node label in; widths are estimated at this size.</summary>
    public const double FontSize = 13;

    private const string NarrowCharacters = "iljtfrI.,':;|!() []{}";
    private const string WideCharacters = "mwMW@%";

    /// <summary>Width of <paramref name="text"/> in canvas units at <paramref name="fontSize"/>: <c>estimateTextWidth</c>, ported.</summary>
    public static double EstimateWidth(string text, double fontSize)
    {
        double width = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            width += rune.Value switch
            {
                ' ' => 0.28,
                _ when rune.IsBmp && NarrowCharacters.Contains((char)rune.Value, StringComparison.Ordinal) => 0.32,
                _ when rune.IsBmp && WideCharacters.Contains((char)rune.Value, StringComparison.Ordinal) => 0.9,
                >= 'A' and <= 'Z' => 0.68,
                >= '0' and <= '9' => 0.58,
                _ => 0.55,
            };
        }

        return width * fontSize;
    }

    /// <summary>
    /// The lines <paramref name="text"/> is drawn on, each fitting <paramref name="maxWidth"/> at <paramref name="fontSize"/>; at
    /// least one (possibly empty). <c>wrapText</c>, ported: breaks at spaces only, a run of spaces a line breaks at hangs past
    /// its end and is left off it, and spaces ending a paragraph stay.
    /// </summary>
    public static IReadOnlyList<string> Wrap(string text, double maxWidth, double fontSize = FontSize)
    {
        var lines = new List<string>();
        double width = Math.Max(maxWidth, 1);
        foreach (string paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string current = "";
            foreach (string token in Tokens(paragraph))
            {
                if (token[0] == ' ')
                {
                    // Spaces never push a line over: they hang past its end.
                    current += token;
                    continue;
                }

                if (EstimateWidth(current + token, fontSize) <= width)
                {
                    current += token;
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current.TrimEnd(' '));
                }

                if (EstimateWidth(token, fontSize) <= width)
                {
                    current = token;
                    continue;
                }

                // The word alone is too wide: split it between characters.
                var piece = new StringBuilder();
                foreach (Rune rune in token.EnumerateRunes())
                {
                    if (piece.Length > 0 && EstimateWidth(piece + rune.ToString(), fontSize) > width)
                    {
                        lines.Add(piece.ToString());
                        piece.Clear();
                    }

                    piece.Append(rune.ToString());
                }

                current = piece.ToString();
            }

            lines.Add(current);
        }

        return lines;
    }

    /// <summary>Runs of spaces and runs of everything else, in order (<c>/ +|[^ ]+/g</c>).</summary>
    private static IEnumerable<string> Tokens(string paragraph)
    {
        int start = 0;
        while (start < paragraph.Length)
        {
            bool spaces = paragraph[start] == ' ';
            int end = start;
            while (end < paragraph.Length && (paragraph[end] == ' ') == spaces)
            {
                end++;
            }

            yield return paragraph[start..end];
            start = end;
        }
    }

    /// <summary>
    /// The rectangle a node of <paramref name="shape"/>, <paramref name="w"/> by <paramref name="h"/>, keeps its label in
    /// (<c>shapes.tsx</c> <c>labelBox</c>): the part of the box clear of the outline's slopes and of what the shape draws
    /// itself. A shape with no entry of its own labels in the whole box. The library shapes (generic icon, basic, org, BPMN) are
    /// answered here too; a UML lollipop or socket, a sequence actor and a composite state keep their bands in the renderer.
    /// </summary>
    public static LabelRect RectFor(string? shape, double w, double h)
    {
        var full = new LabelRect(0, 0, w, h);
        if (shape is null)
        {
            return full;
        }

        if (GenericIconLibrary.IsGenericShape(shape))
        {
            // A boundary icon is a container's outline, labelled over the whole box; any other icon labels in the band under its glyph.
            if (GenericIconLibrary.TryGet(shape, out GenericIcon? icon) && icon!.Boundary)
            {
                return full;
            }

            double band = h * IconLabelBand;
            return new LabelRect(0, band, w, h - band);
        }

        if (BasicShapeLibrary.TryGet(shape, out BasicShape? basic))
        {
            (double left, double top, double right, double bottom) = BasicShapeGeometry.ResolveBox(basic!.LabelBox, w, h);
            return new LabelRect(left, top, right - left, bottom - top);
        }

        if (OrgShapeLibrary.TryGet(shape, out OrgShape? org))
        {
            double inset = Math.Min(w, h) * 0.08;
            return org!.Outline == "double"
                ? new LabelRect(inset * 1.5, inset * 1.5, w - (inset * 3), h - (inset * 3))
                : org.Name == "assistant"
                    ? new LabelRect(w * 0.2, 0, w * 0.8, h)
                    : full;
        }

        if (BpmnShapeLibrary.TryGet(shape, out BpmnShape? bpmn))
        {
            return BpmnRect(bpmn!, w, h);
        }

        return shape switch
        {
            "document" => new LabelRect(0, 0, w, h * 0.7),
            "predefined-process" => new LabelRect(w * 0.12, 0, w * 0.76, h),
            "manual-input" => new LabelRect(0, h * 0.25, w, h * 0.75),
            "off-page-connector" => new LabelRect(0, 0, w, h * 0.65),
            "delay" => new LabelRect(0, 0, w * 0.8, h),
            "manual-operation" => new LabelRect(w * 0.2, 0, w - (w * 0.2 * 2), h),
            "display" => new LabelRect(w * 0.25, 0, w * 0.75, h),
            "stored-data" => new LabelRect(0, 0, w * 0.85, h),
            "multi-document" => new LabelRect(0, h * 0.2, w * 0.8, h * 0.64),
            "merge" => new LabelRect(w * 0.25, 0, w * 0.5, h * 0.6),
            "extract" => new LabelRect(w * 0.25, h * 0.4, w * 0.5, h * 0.6),
            "on-page-connector" or "summing-junction" or "or" => new LabelRect(w * 0.15, h * 0.2, w * 0.7, h * 0.6),
            "sort" => new LabelRect(w * 0.25, h * 0.25, w * 0.5, h * 0.5),
            "loop-limit" => new LabelRect(w * 0.12, h * 0.15, w * 0.76, h * 0.8),
            "internal-storage" => new LabelRect(w * 0.2, h * 0.2, w * 0.8, h * 0.8),
            "card" => CardRect(w, h),
            "pill" => new LabelRect(h / 4, 0, w - (h / 2), h),
            "diamond" => new LabelRect(w * 0.19, h / 4, w * 0.62, h / 2),
            "hexagon" => InsetRect(Math.Min(w * 0.2, h / 2), w, h),
            "parallelogram" => InsetRect(Math.Min(w * 0.2, h / 2), w, h),
            "cylinder" => CylinderRect(w, h),
            _ => full,
        };
    }

    private const double IconLabelBand = 0.7;

    private static LabelRect InsetRect(double inset, double w, double h) => new(inset, 0, w - (inset * 2), h);

    private static LabelRect CardRect(double w, double h)
    {
        double cut = Math.Min(w * 0.2, h * 0.5);
        return new LabelRect(cut * 0.5, cut * 0.5, w - cut, h - cut);
    }

    private static LabelRect CylinderRect(double w, double h)
    {
        double ry = Math.Min(h * 0.12, 14);
        return new LabelRect(0, ry * 2, w, h - (ry * 2));
    }

    private static LabelRect BpmnRect(BpmnShape shape, double w, double h)
    {
        BpmnLayout l = BpmnShapeLibrary.Layout;
        if (BpmnShapeLibrary.LabelOutside(shape))
        {
            // Events, gateways and data shapes label in a band below the outline, at least labelMinWidth wide.
            double width = Math.Max(w, l.LabelMinWidth);
            return new LabelRect(-(width - w) / 2, h + l.LabelGap, width, l.LabelBand);
        }

        if (shape.Family == BpmnShapeLibrary.AnnotationFamily)
        {
            double bracket = Math.Min(w * l.AnnotationBracket, l.AnnotationBracketMax);
            return new LabelRect(bracket, 0, w - bracket, h);
        }

        double top = shape.Glyph is not null && shape.Placement == "corner" ? l.MarkerInset + (l.MarkerSize / 2) : 0;
        double foot = shape.Glyph is not null && shape.Placement == "bottom" ? l.MarkerSize + l.MarkerInset : 0;
        return new LabelRect(0, top, w, h - top - foot);
    }
}
