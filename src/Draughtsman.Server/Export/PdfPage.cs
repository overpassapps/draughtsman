using System.Globalization;
using System.Text.RegularExpressions;
using Draughtsman.Mcp.Rendering;

namespace Draughtsman.Server.Export;

/// <summary>
/// The HTML page Chromium prints: the rendered SVG inside an <c>@page</c> box chosen by <see cref="PdfOptions"/>. A fixed
/// paper size scales the drawing to fit its margins (never above its own size) and centres it; <see cref="PdfPageSize.Fit"/>
/// cuts the page to the drawing. Either way the page is painted the drawing's own paper colour, so a dark theme's export does
/// not sit in a white frame. Pure, so the page maths is tested without a browser.
/// </summary>
internal static partial class PdfPage
{
    private const double PxPerMm = 96 / 25.4;

    // Leave a hair of slack so rounding can never push the content box onto a second page.
    private const double SlackMm = 0.3;

    public static string Html(string svg, PdfOptions options)
    {
        (double drawingW, double drawingH) = DrawingSize(svg);
        double margin = options.EffectiveMarginMm;
        string paper = Paper(svg);
        string pageRule;
        string boxCss;
        if (options.PageSize == PdfPageSize.Fit)
        {
            double pageW = drawingW + (2 * margin * PxPerMm);
            double pageH = drawingH + (2 * margin * PxPerMm);
            pageRule = Inv($"@page{{size:{pageW:0.##}px {pageH:0.##}px;margin:{margin:0.##}mm}}");
            boxCss = Inv($"width:{drawingW:0.##}px;height:{drawingH:0.##}px");
        }
        else
        {
            (double w, double h) = PaperMm(options.PageSize, options.Orientation);
            pageRule = Inv($"@page{{size:{w:0.##}mm {h:0.##}mm;margin:{margin:0.##}mm}}");
            boxCss = Inv($"width:{Math.Max(w - (2 * margin) - SlackMm, 1):0.##}mm;height:{Math.Max(h - (2 * margin) - SlackMm, 1):0.##}mm");
        }

        // The svg's own width/height attributes give way to the box, and its viewBox scales it to fit, centred.
        return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"{ChromiumPdfRenderer.PageContentSecurityPolicy}\"><style>{pageRule}html,body{{margin:0;padding:0;background:{paper};overflow:hidden}}.box{{{boxCss};overflow:hidden;break-inside:avoid}}.box svg{{display:block;width:100%;height:100%}}</style></head><body><div class=\"box\">{svg}</div></body></html>";
    }

    /// <summary>The paper's width and height in millimetres, after orientation.</summary>
    public static (double Width, double Height) PaperMm(PdfPageSize size, PdfOrientation orientation)
    {
        (double w, double h) = size switch
        {
            PdfPageSize.A3 => (297d, 420d),
            PdfPageSize.Letter => (215.9, 279.4),
            _ => (210d, 297d),
        };
        return orientation == PdfOrientation.Landscape ? (h, w) : (w, h);
    }

    /// <summary>The drawing's width and height in CSS pixels, from the root's viewBox (what <see cref="SvgRenderer"/> writes).</summary>
    public static (double Width, double Height) DrawingSize(string svg)
    {
        Match match = ViewBox().Match(svg);
        if (match.Success
            && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
            && double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double h)
            && w > 0 && h > 0)
        {
            return (w, h);
        }

        return (800, 600);
    }

    /// <summary>The colour of the drawing's own background rectangle, white when it is not a plain hex colour.</summary>
    public static string Paper(string svg)
    {
        Match match = Background().Match(svg);
        return match.Success ? match.Groups[1].Value : "#ffffff";
    }

    private static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"<svg\b[^>]*\bviewBox=""\s*[-\d.]+\s+[-\d.]+\s+([\d.]+)\s+([\d.]+)\s*""", RegexOptions.None, Draughtsman.Core.RegexLimits.TimeoutMilliseconds)]
    private static partial Regex ViewBox();

    [GeneratedRegex(@"<rect x=""0"" y=""0""[^>]*\bfill=""(#[0-9a-fA-F]{3,8})""", RegexOptions.None, Draughtsman.Core.RegexLimits.TimeoutMilliseconds)]
    private static partial Regex Background();
}
