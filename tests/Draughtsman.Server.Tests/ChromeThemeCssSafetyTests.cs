using Draughtsman.Server.Theming;

namespace Draughtsman.Server.Tests;

/// <summary>The second lock on the compiled stylesheet: a value that could end its declaration or pull in a resource is dropped, a plain one is kept.</summary>
public class ChromeThemeCssSafetyTests
{
    private static ChromeTheme Theme(string brand, string font)
    {
        return new ChromeTheme
        {
            Name = "T",
            ProductName = "P",
            Mode = "dark",
            Colors = new Dictionary<string, string> { ["brand"] = brand, ["surface"] = "#000000" },
            Radius = 6,
            Font = new Dictionary<string, string> { ["ui"] = font },
        };
    }

    [Theory]
    [InlineData("#ff0000; } body { display: none", "Inter")]
    [InlineData("#ff0000", "Inter; } body { display: none")]
    [InlineData("url(https://evil.example/x)", "Inter")]
    [InlineData("#ff0000", "Inter /* x */")]
    [InlineData("#ff0000", "</style><script>alert(1)</script>")]
    public void An_unsafe_value_is_dropped_and_the_rest_of_the_theme_survives(string brand, string font)
    {
        string css = ChromeThemeCss.Emit(Theme(brand, font));

        Assert.DoesNotContain("body {", css, StringComparison.Ordinal);
        Assert.DoesNotContain("</style>", css, StringComparison.Ordinal);
        Assert.DoesNotContain("url(", css, StringComparison.Ordinal);
        Assert.DoesNotContain("/*", css, StringComparison.Ordinal);
        // The safe surface colour is still emitted: one bad value never blanks the theme.
        Assert.Contains("--chrome-surface: #000000;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_value_is_emitted_unchanged()
    {
        string css = ChromeThemeCss.Emit(Theme("#ff0000", "\"Source Sans 3\", Inter, sans-serif"));

        Assert.Contains("--chrome-brand: #ff0000;", css, StringComparison.Ordinal);
        Assert.Contains("--chrome-font-ui: \"Source Sans 3\", Inter, sans-serif;", css, StringComparison.Ordinal);
    }
}
