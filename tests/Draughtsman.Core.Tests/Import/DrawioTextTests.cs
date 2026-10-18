using System.Diagnostics;
using Draughtsman.Core.Import.Drawio;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// A draw.io label is HTML a stranger wrote (<c>html=1</c> in the style). The importer keeps only its text: no tag, attribute,
/// script or style content reaches the model, and what the text says is read exactly once (one entity decode, never two).
/// </summary>
public class DrawioTextTests
{
    [Theory]
    [InlineData("<b>Hello</b> world", "Hello world")]
    [InlineData("<font style=\"font-size: 10px;\" color=\"#ff0000\">Red</font> and <i>italic</i>", "Red and italic")]
    [InlineData("one<br>two", "one\ntwo")]
    [InlineData("one<br/>two", "one\ntwo")]
    [InlineData("one<BR />two", "one\ntwo")]
    [InlineData("<div>one</div><div>two</div>", "one\ntwo")]
    [InlineData("<p>x</p><p>y</p>", "x\ny")]
    [InlineData("<h1>Title</h1>body", "Title\nbody")]
    [InlineData("<ul><li>a</li><li>b</li></ul>", "- a\n- b")]
    [InlineData("<table><tr><td>a</td></tr><tr><td>b</td></tr></table>", "a\nb")]
    public void Tags_are_dropped_and_block_boundaries_become_line_breaks(string html, string expected)
    {
        Assert.Equal(expected, DrawioText.FromHtml(html));
    }

    [Theory]
    [InlineData("a &amp; b", "a & b")]
    [InlineData("1 &lt; 2 &gt; 0", "1 < 2 > 0")]
    [InlineData("say &quot;hi&quot; &apos;there&apos;", "say \"hi\" 'there'")]
    [InlineData("&#65;&#x42;&#X43;", "ABC")]
    [InlineData("a&nbsp;b", "a b")]
    [InlineData("caf&eacute; &mdash; &hellip;", "café — …")]
    public void Entities_are_decoded(string html, string expected)
    {
        Assert.Equal(expected, DrawioText.FromHtml(html));
    }

    [Theory]
    [InlineData("&amp;lt;script&amp;gt;", "&lt;script&gt;")]
    [InlineData("&amp;amp;", "&amp;")]
    [InlineData("&lt;script&gt;alert(1)&lt;/script&gt;", "<script>alert(1)</script>")]
    public void Entities_are_decoded_once_and_what_they_spell_is_text_not_markup(string html, string expected)
    {
        // &lt;script&gt; is the author's literal text. Decoding it first and then stripping tags would delete it, and decoding twice
        // would turn &amp;lt; into a bracket: both are the same bug in opposite directions.
        Assert.Equal(expected, DrawioText.FromHtml(html));
    }

    [Theory]
    [InlineData("&bogus; &#xZZ; &#; &#99999999999; &;", "&bogus; &#xZZ; &#; &#99999999999; &;")]
    [InlineData("AT&T", "AT&T")]
    [InlineData("a &#0; b", "a b")]
    public void An_entity_that_is_not_one_is_left_as_written(string html, string expected)
    {
        Assert.Equal(expected, DrawioText.FromHtml(html));
    }

    [Theory]
    [InlineData("before<script>alert(1)</script>after", "beforeafter")]
    [InlineData("before<SCRIPT type=\"text/javascript\">alert('</b>')</SCRIPT>after", "beforeafter")]
    [InlineData("a<style>p { color: red }</style>b", "ab")]
    [InlineData("a<script>never closed", "a")]
    [InlineData("<img src=\"x\" onerror=\"alert(1)\">text", "text")]
    [InlineData("<a href=\"javascript:alert(1)\">link</a>", "link")]
    [InlineData("<iframe src=\"http://example.test/\"></iframe>safe", "safe")]
    [InlineData("a<!-- <b>hidden</b> -->b", "ab")]
    [InlineData("a<!DOCTYPE html>b", "ab")]
    [InlineData("a<?xml version=\"1.0\"?>b", "ab")]
    public void Nothing_executable_or_hidden_survives(string html, string expected)
    {
        Assert.Equal(expected, DrawioText.FromHtml(html));
    }

    [Theory]
    [InlineData("a < b", "a < b")]
    [InlineData("1<2 and 3>2", "1<2 and 3>2")]
    [InlineData("x <y never closed", "x <y never closed")]
    [InlineData("<<b>>bold<</b>>", "<>bold<>")]
    [InlineData("5 > 3", "5 > 3")]
    public void A_bracket_that_does_not_open_a_tag_is_text(string html, string expected)
    {
        Assert.Equal(expected, DrawioText.FromHtml(html));
    }

    [Theory]
    [InlineData("  a   b  ", "a b")]
    [InlineData("a\tb", "a b")]
    [InlineData("line one\nline two", "line one\nline two")]
    [InlineData("a\r\nb\rc", "a\nb\nc")]
    [InlineData("a\n\n\n\n\nb", "a\n\nb")]
    [InlineData("<div><br></div><div>x</div>", "x")]
    [InlineData("\n\n  padded  \n\n", "padded")]
    [InlineData("tab\u0007bell\u0000nul", "tabbellnul")]
    public void Whitespace_is_collapsed_the_way_a_label_reads(string html, string expected)
    {
        Assert.Equal(expected, DrawioText.FromHtml(html));
    }

    [Theory]
    [InlineData("a<b>c", "a<b>c")]
    [InlineData("1 &amp; 2", "1 &amp; 2")]
    [InlineData("  keep   inner   spaces  ", "keep   inner   spaces")]
    [InlineData("two\r\nlines", "two\nlines")]
    [InlineData("bell\u0007", "bell")]
    public void A_label_that_is_not_html_is_taken_as_written(string text, string expected)
    {
        // Without html=1 the value is plain text: "<b>" in it is three characters, not markup.
        Assert.Equal(expected, DrawioText.FromPlain(text));
    }

    [Fact]
    public void A_very_deep_pile_of_tags_is_read_without_recursion()
    {
        string html = string.Concat(Enumerable.Repeat("<div><b>", 200_000)) + "x" + string.Concat(Enumerable.Repeat("</b></div>", 200_000));

        Assert.Equal("x", DrawioText.FromHtml(html));
    }

    [Fact]
    public void An_attribute_longer_than_any_real_tag_is_text_not_a_tag()
    {
        string tag = "<span style=\"" + new string('a', DrawioText.MaxTagLength) + "\">";

        string text = DrawioText.FromHtml(tag + "kept");

        Assert.EndsWith("kept", text);
        Assert.StartsWith("<span", text);
    }

    public static TheoryData<string, string> Hostile()
    {
        // Sized so that a scan from every opening bracket to the end (vectorised, so about 30 GB/s) still takes over ten seconds, not one.
        const int Repeat = 600_000;
        return new TheoryData<string, string>
        {
            { "an unclosed bracket repeated", string.Concat(Enumerable.Repeat("<a ", Repeat)) },
            { "an unclosed bracket repeated, one > at the very end", string.Concat(Enumerable.Repeat("<a", Repeat)) + ">" },
            { "an unclosed quote repeated", string.Concat(Enumerable.Repeat("<a \"", Repeat)) + ">" },
            { "an ampersand repeated", string.Concat(Enumerable.Repeat("&", Repeat * 2)) },
            { "an entity start repeated", string.Concat(Enumerable.Repeat("&#x", Repeat)) },
            { "scripts that are never closed", string.Concat(Enumerable.Repeat("<script>", Repeat / 4)) },
            { "comment starts that are never closed", string.Concat(Enumerable.Repeat("<!--", Repeat)) },
            { "scripts closed one at a time", string.Concat(Enumerable.Repeat("<script></script>", Repeat / 4)) },
        };
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task Hostile_markup_is_read_in_linear_time(string description, string html)
    {
        // A scan that looked ahead for the closing bracket from every opening one would do hundreds of billions of steps on these (minutes);
        // the linear reader takes milliseconds. The budget is stretched for a host busy with other builds (LoadScale), and still far below that.
        TimeSpan budget = LoadScale.Scaled(TimeSpan.FromSeconds(2));
        await Task.Run(() => DrawioText.FromHtml("<b>warm</b>"));

        var timer = Stopwatch.StartNew();
        string text = await Task.Run(() => DrawioText.FromHtml(html));
        timer.Stop();

        Assert.True(timer.Elapsed < budget, $"{description}: took {timer.Elapsed.TotalMilliseconds:F0} ms, budget {budget.TotalMilliseconds:F0} ms (result length {text.Length})");
    }
}
