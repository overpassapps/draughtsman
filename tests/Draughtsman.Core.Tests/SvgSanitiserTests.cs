using System.Text;
using Draughtsman.Core.Media;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Core.Tests;

/// <summary>
/// The uploaded-SVG allowlist (M3 branding). Each dangerous construct is proved refused and a clean logo proved
/// accepted, so the rule is pinned from both directions rather than only the happy path.
/// </summary>
public class SvgSanitiserTests
{
    private const string CleanLogo = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 12">
          <g fill="#c0392b"><rect width="24" height="12" rx="2"/><path d="M2 2 L22 10"/></g>
        </svg>
        """;

    [Fact]
    public void A_clean_logo_is_accepted_and_its_size_read()
    {
        SanitizedSvg result = SvgSanitiser.Sanitise(Encoding.UTF8.GetBytes(CleanLogo));

        Assert.Equal(24, result.Width);
        Assert.Equal(12, result.Height);
        Assert.Equal(CleanLogo, Encoding.UTF8.GetString(result.Content));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><foreignObject><body xmlns=\"http://www.w3.org/1999/xhtml\">hi</body></foreignObject></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"><rect width=\"4\" height=\"4\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><a onclick=\"steal()\"><text>x</text></a></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><use href=\"https://evil.example/x.svg#a\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"><use xlink:href=\"https://evil.example/x.svg#a\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>@import url(https://evil.example/x.css);</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect style=\"background:url(javascript:alert(1))\" width=\"4\" height=\"4\"/></svg>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://evil.example/x.png\"/></svg>")]
    public void A_dangerous_svg_is_rejected(string svg)
    {
        Assert.Throws<UnsafeSvgException>(() => SvgSanitiser.Sanitise(Encoding.UTF8.GetBytes(svg)));
    }

    // AU-17: CSS reads an escape in a style as the character it names, so `u\72l(` is `url(`. The refusal must see what the
    // browser sees, not the spelling.
    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>rect{fill:u\\72l(https://evil.example/y)}</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>rect{fill:\\75 rl(https://evil.example/y)}</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>rect{fill:\\000075rl(https://evil.example/y)}</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>rect{fill:\\55RL(https://evil.example/y)}</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>rect{fill:ur\\l(https://evil.example/y)}</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>@\\69mport 'https://evil.example/x.css';</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>rect{width:e\\78pression(alert(1))}</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect style=\"fill:u\\72l(https://evil.example/y)\" width=\"4\" height=\"4\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect fill=\"u\\72l(https://evil.example/y)\" width=\"4\" height=\"4\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>rect{background:j\\61vascript:alert(1)}</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>rect{fill:&#117;rl(https://evil.example/y)}</style></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>rect{background:image-set('https://evil.example/y.png' 1x)}</style></svg>")]
    public void A_url_or_script_spelled_with_css_escapes_is_rejected_like_the_plain_spelling(string svg)
    {
        Assert.Throws<UnsafeSvgException>(() => SvgSanitiser.Sanitise(Encoding.UTF8.GetBytes(svg)));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>text{font-family:\"Helvetica\\20 Neue\"}</style><text>x</text></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>rect{fill:url(#grad)}</style><rect width=\"4\" height=\"4\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>rect{fill:url(\\23 grad)}</style><rect width=\"4\" height=\"4\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>text::before{content:\"\\201C\"}</style><text>x</text></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect fill=\"#c0392b\" stroke=\"rgb(1, 2, 3)\" width=\"4\" height=\"4\"/></svg>")]
    public void An_escape_that_spells_nothing_dangerous_is_still_accepted(string svg)
    {
        SanitizedSvg result = SvgSanitiser.Sanitise(Encoding.UTF8.GetBytes(svg));

        Assert.Equal(svg, Encoding.UTF8.GetString(result.Content));
    }

    [Theory]
    [InlineData("u\\72l(", "url(")]
    [InlineData("\\75 rl(", "url(")]
    [InlineData("\\000075rl(", "url(")]
    [InlineData("\\0075\r\nrl", "url")]
    [InlineData("ur\\l(", "url(")]
    [InlineData("a\\\nb", "ab")]
    [InlineData("\\41BC", "\u41BC")]
    [InlineData("\\0", "\uFFFD")]
    [InlineData("\\110000", "\uFFFD")]
    [InlineData("\\D800", "\uFFFD")]
    [InlineData("trailing\\", "trailing")]
    [InlineData("no escapes", "no escapes")]
    public void Css_escapes_decode_as_a_stylesheet_parser_reads_them(string css, string expected)
    {
        Assert.Equal(expected, SvgSanitiser.DecodeCssEscapes(css));
    }

    [Fact]
    public void Decoding_a_megabyte_of_backslashes_is_quick_and_linear()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();

        _ = SvgSanitiser.DecodeCssEscapes(new string('\\', 1_000_000));
        _ = SvgSanitiser.DecodeCssEscapes(string.Concat(Enumerable.Repeat("\\75 ", 250_000)));

        Assert.True(timer.Elapsed < LoadScale.Scaled(TimeSpan.FromSeconds(2)), $"took {timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_non_svg_xml_document_is_rejected()
    {
        Assert.Throws<UnsafeSvgException>(() =>
            SvgSanitiser.Sanitise(Encoding.UTF8.GetBytes("<html xmlns=\"http://www.w3.org/1999/xhtml\"><body/></html>")));
    }

    [Fact]
    public void A_processing_instruction_is_rejected()
    {
        Assert.Throws<UnsafeSvgException>(() =>
            SvgSanitiser.Sanitise(Encoding.UTF8.GetBytes("<?xml-stylesheet href=\"https://evil.example/x.css\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>")));
    }
}
