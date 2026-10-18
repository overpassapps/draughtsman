using Draughtsman.Core.Import.Drawio;

namespace Draughtsman.Core.Tests.Import;

/// <summary>A draw.io style is one string: bare names (<c>ellipse</c>) and <c>key=value</c> pairs, split on semicolons.</summary>
public class DrawioStyleTests
{
    [Fact]
    public void Pairs_are_read_by_key()
    {
        DrawioStyle style = DrawioStyle.Parse("rounded=1;whiteSpace=wrap;html=1;fillColor=#dae8fc;strokeColor=#6c8ebf;");

        Assert.Equal("#dae8fc", style.Get("fillColor"));
        Assert.Equal("#6c8ebf", style.Get("strokeColor"));
        Assert.Equal("wrap", style.Get("whiteSpace"));
        Assert.Null(style.Get("shape"));
        Assert.True(style.Has("rounded"));
        Assert.False(style.Has("shape"));
    }

    [Fact]
    public void The_first_bare_word_is_the_style_name_and_every_bare_word_is_a_token()
    {
        DrawioStyle style = DrawioStyle.Parse("ellipse;whiteSpace=wrap;html=1;aspect=fixed;");

        Assert.Equal("ellipse", style.Name);
        Assert.True(style.HasToken("ellipse"));
        Assert.False(style.HasToken("rhombus"));
        Assert.False(style.HasToken("whiteSpace"));

        DrawioStyle several = DrawioStyle.Parse("html=1;swimlane;container=1;group;");
        Assert.Equal("swimlane", several.Name);
        Assert.True(several.HasToken("group"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;;")]
    public void Nothing_in_the_style_is_a_style_with_nothing_in_it(string? text)
    {
        DrawioStyle style = DrawioStyle.Parse(text);

        Assert.Null(style.Name);
        Assert.Null(style.Get("fillColor"));
        Assert.False(style.Flag("rounded"));
    }

    [Fact]
    public void A_later_pair_replaces_an_earlier_one_as_draw_io_does()
    {
        Assert.Equal("2", DrawioStyle.Parse("strokeWidth=1;strokeWidth=2").Get("strokeWidth"));
    }

    [Fact]
    public void Only_the_first_equals_sign_splits_a_pair()
    {
        DrawioStyle style = DrawioStyle.Parse("image=https://example.test/a.png?x=1&y=2;=orphan;empty=;");

        Assert.Equal("https://example.test/a.png?x=1&y=2", style.Get("image"));
        Assert.Equal(string.Empty, style.Get("empty"));
        Assert.Null(style.Get(string.Empty));
    }

    [Theory]
    [InlineData("dashed=1", true)]
    [InlineData("dashed=true", true)]
    [InlineData("dashed=0", false)]
    [InlineData("dashed=false", false)]
    [InlineData("dashed=yes", false)]
    [InlineData("rounded=1", false)]
    public void A_flag_is_on_for_one_or_true_only(string text, bool expected)
    {
        Assert.Equal(expected, DrawioStyle.Parse(text).Flag("dashed"));
    }

    [Theory]
    [InlineData("strokeWidth=2", 2.0)]
    [InlineData("strokeWidth= 3.5 ", 3.5)]
    [InlineData("strokeWidth=-1", -1.0)]
    [InlineData("strokeWidth=1e2", 100.0)]
    public void A_number_is_read_with_the_invariant_culture(string text, double expected)
    {
        Assert.Equal(expected, DrawioStyle.Parse(text).Number("strokeWidth"));
    }

    [Theory]
    [InlineData("strokeWidth=abc")]
    [InlineData("strokeWidth=")]
    [InlineData("strokeWidth=NaN")]
    [InlineData("strokeWidth=Infinity")]
    [InlineData("strokeWidth=1e999")]
    [InlineData("strokeWidth=2,5")]
    [InlineData("rounded=1")]
    public void A_value_that_is_not_a_finite_number_is_no_number(string text)
    {
        Assert.Null(DrawioStyle.Parse(text).Number("strokeWidth"));
    }

    [Fact]
    public void A_very_long_style_is_read_in_one_pass_and_keeps_every_pair()
    {
        string text = string.Concat(Enumerable.Range(0, 50_000).Select(i => $"k{i}=v{i};")) + "last=1";

        DrawioStyle style = DrawioStyle.Parse(text);

        Assert.Equal("v49999", style.Get("k49999"));
        Assert.Equal("1", style.Get("last"));
    }
}
