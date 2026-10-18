using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Mcp.Rendering;

namespace Draughtsman.Mcp.Tests;

/// <summary>The server's reader of the shipped diagram themes: the same kind defaults, tokens and overrides the canvas resolves (web/src/theme/resolveStyle.ts).</summary>
public class DiagramThemeTests
{
    private static readonly DiagramTheme s_theme = DiagramTheme.Default;

    [Fact]
    public void A_kind_takes_the_themes_default_for_it_and_an_unknown_kind_takes_the_default_entry()
    {
        NodeStyle participant = s_theme.Node("participant", null);
        Assert.Equal(["#202a42", "#7f8aa0", "#F2F2F2"], [participant.Fill!, participant.Stroke!, participant.TextColor!]);
        Assert.Equal(1.5, participant.StrokeWidth);
        Assert.Equal("#7f8aa0", s_theme.Node("no-such-kind", null).Stroke);
    }

    [Fact]
    public void An_elements_own_style_wins_field_by_field_and_a_token_names_a_palette_entry()
    {
        NodeStyle style = s_theme.Node("participant", new NodeStyle { Fill = "#abcdef", Stroke = "token:steel" });

        Assert.Equal("#abcdef", style.Fill);
        Assert.Equal("#7f8aa0", style.Stroke);
        // The theme's light text cannot be read on #abcdef, so it turns dark as the canvas's readableTextOn does (QA-06).
        Assert.Equal("#0f172a", style.TextColor);
        // A token the palette lacks keeps the kind's colour, as the canvas does.
        Assert.Equal("#7f8aa0", s_theme.Node("participant", new NodeStyle { Stroke = "token:nope" }).Stroke);
    }

    [Fact]
    public void An_edge_takes_the_theme_kind_then_the_vocabulary_default_then_the_default_edge()
    {
        EdgeStyle reply = s_theme.Edge("return", null);
        Assert.Equal(Dash.Dashed, reply.Dash);
        Assert.Equal(ArrowHead.Open, reply.ArrowEnd);
        Assert.Equal(ArrowHead.None, reply.ArrowStart);
        EdgeStyle vocabulary = new() { Dash = Dash.Dotted, ArrowEnd = ArrowHead.Diamond };
        // The theme names sync, so the vocabulary's default is not consulted; a kind the theme does not name takes it.
        Assert.Equal(Dash.Solid, s_theme.Edge("sync", null, vocabulary).Dash);
        EdgeStyle unnamed = s_theme.Edge("brand-new-kind", null, vocabulary);
        Assert.Equal(Dash.Dotted, unnamed.Dash);
        Assert.Equal(ArrowHead.Diamond, unnamed.ArrowEnd);
        Assert.Equal("#7f8aa0", unnamed.Stroke);
    }

    [Fact]
    public void Overrides_set_tokens_and_ignore_a_path_that_addresses_no_token()
    {
        DiagramTheme themed = s_theme.WithOverrides(new Dictionary<string, string>
        {
            ["palette.brand"] = "#112233",
            ["nodes.participant.fill"] = "token:brand",
            ["nodes.participant.strokeWidth"] = "3",
            ["nodes.service"] = "#ffffff",
            ["name"] = "hijacked",
            ["nodes.nothing.fill"] = "#ffffff",
        });

        NodeStyle participant = themed.Node("participant", null);
        Assert.Equal("#112233", participant.Fill);
        Assert.Equal(3, participant.StrokeWidth);
        // A whole kind's style is not a token, and a missing step is no route to one.
        Assert.Equal("#e04832", themed.Node("service", null).Stroke);
        Assert.Equal("#7f8aa0", themed.Node("nothing", null).Stroke);
        // The shipped theme itself is never changed by an override.
        Assert.Equal("#202a42", s_theme.Node("participant", null).Fill);
    }

    [Fact]
    public void Text_on_the_paper_reads_against_it_unless_the_theme_names_one()
    {
        Assert.Equal("#FFFFFF", s_theme.ExportBackground);
        Assert.Equal("#111111", s_theme.SurfaceText());
        Assert.Equal("#F2F2F2", s_theme.WithOverrides(new Dictionary<string, string> { ["exportBackground"] = "#101820" }).SurfaceText());
        Assert.Equal("#ff00ff", s_theme.WithOverrides(new Dictionary<string, string> { ["exportText"] = "#ff00ff" }).SurfaceText());
        Assert.Null(s_theme.WithOverrides(new Dictionary<string, string> { ["exportBackground"] = "rebeccapurple" }).SurfaceText());
    }

    [Theory]
    [InlineData("overpass")]
    [InlineData("neutral")]
    [InlineData("mono")]
    public void Every_shipped_theme_loads_and_names_the_sequence_kinds(string name)
    {
        DiagramTheme theme = DiagramTheme.Load(name);

        Assert.NotNull(theme.Node("participant", null).Fill);
        Assert.NotNull(theme.Node("fragment", null).Stroke);
        Assert.All(new[] { "sync", "async", "return", "note", "interaction-use", "create", "destroy" }, kind => Assert.NotNull(theme.Edge(kind, null).Stroke));
    }

    [Fact]
    public void An_unknown_theme_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => DiagramTheme.Load("nope"));
    }
}
