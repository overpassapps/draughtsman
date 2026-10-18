using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Export;

namespace Draughtsman.Server.Tests;

/// <summary>The page Chromium prints: an @page box from the options, the drawing scaled into it, the paper colour matching the theme's.</summary>
public class PdfPageTests
{
    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 400 200\" width=\"400\" height=\"200\">\n<rect x=\"0\" y=\"0\" width=\"400\" height=\"200\" fill=\"#0B1F44\"/>\n</svg>";

    [Theory]
    [InlineData(PdfPageSize.A4, PdfOrientation.Landscape, "297mm 210mm")]
    [InlineData(PdfPageSize.A4, PdfOrientation.Portrait, "210mm 297mm")]
    [InlineData(PdfPageSize.A3, PdfOrientation.Landscape, "420mm 297mm")]
    [InlineData(PdfPageSize.Letter, PdfOrientation.Portrait, "215.9mm 279.4mm")]
    public void A_fixed_page_takes_its_paper_size_and_orientation(PdfPageSize size, PdfOrientation orientation, string expected)
    {
        string html = PdfPage.Html(Svg, new PdfOptions(size, orientation));

        Assert.Contains($"@page{{size:{expected};", html, StringComparison.Ordinal);
        // The default margin for a fixed page is 10 mm.
        Assert.Contains("margin:10mm}", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fixed_page_keeps_the_drawing_inside_the_margins_so_it_stays_one_page()
    {
        string html = PdfPage.Html(Svg, new PdfOptions(PdfPageSize.A4, PdfOrientation.Landscape, 20));

        // 297 - 40 = 257 and 210 - 40 = 170, less the slack that stops rounding spilling onto a second page.
        Assert.Contains("width:256.7mm;height:169.7mm", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Fit_cuts_the_page_to_the_drawing_and_adds_the_margin_around_it()
    {
        string withoutMargin = PdfPage.Html(Svg, new PdfOptions(PdfPageSize.Fit));
        Assert.Contains("@page{size:400px 200px;margin:0mm}", withoutMargin, StringComparison.Ordinal);

        // 10 mm is 37.8 px at 96 dpi, on each side.
        string withMargin = PdfPage.Html(Svg, new PdfOptions(PdfPageSize.Fit, PdfOrientation.Portrait, 10));
        Assert.Contains("@page{size:475.59px 275.59px;margin:10mm}", withMargin, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_is_painted_the_drawings_own_paper_so_a_dark_theme_has_no_white_frame()
    {
        Assert.Equal("#0B1F44", PdfPage.Paper(Svg));
        Assert.Contains("background:#0B1F44", PdfPage.Html(Svg, new PdfOptions(PdfPageSize.A4)), StringComparison.Ordinal);
        // Anything that is not a plain hex colour is never spliced into the page: white instead.
        Assert.Equal("#ffffff", PdfPage.Paper("<rect x=\"0\" y=\"0\" fill=\"red;}</style><script>\"/>"));
    }

    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData(PdfPageSize.A4, PdfOrientation.Landscape, 0d, true)]
    [InlineData(PdfPageSize.A4, PdfOrientation.Landscape, 50d, true)]
    [InlineData(PdfPageSize.A4, PdfOrientation.Landscape, -1d, false)]
    [InlineData(PdfPageSize.A4, PdfOrientation.Landscape, 50.5, false)]
    [InlineData(PdfPageSize.A4, PdfOrientation.Landscape, double.NaN, false)]
    public void Margins_are_limited_to_what_leaves_a_page(PdfPageSize? size, PdfOrientation? orientation, double? margin, bool valid)
    {
        var options = new PdfOptions(size ?? PdfPageSize.Fit, orientation ?? PdfOrientation.Landscape, margin);

        Assert.Equal(valid, options.Validate() is null);
    }
}
