using System.Text;
using System.Text.RegularExpressions;

namespace Draughtsman.Server.Theming;

/// <summary>
/// Compiles a <see cref="ChromeTheme"/> to the CSS custom properties the app shell reads, matching the chrome section
/// of <c>web/scripts/build-tokens.ts</c> (which stays the development-time compiler). Token names are kebab-cased, so
/// <c>surfaceRaised</c> becomes <c>--chrome-surface-raised</c>. `mode` selects the primary set; the companion block
/// carries the other mode, falling back to the primary values when it is absent.
/// </summary>
internal static partial class ChromeThemeCss
{
    public static string Emit(ChromeTheme theme)
    {
        (IReadOnlyDictionary<string, string> dark, IReadOnlyDictionary<string, string> light) = ResolveModes(theme);
        IReadOnlyDictionary<string, string> primaryColors = theme.Mode == "light" ? light : dark;

        List<(string Property, string Value)> shared =
        [
            ("--chrome-radius", $"{Format(theme.Radius)}px"),
            .. theme.Font.Select(pair => ($"--chrome-font-{Kebab(pair.Key)}", pair.Value)),
            ("--chrome-color-scheme", theme.Mode == "system" ? "light dark" : theme.Mode),
        ];

        StringBuilder css = new();
        css.Append(Block(":root", [.. shared, .. Declarations(primaryColors)]));
        css.Append(Block(":root[data-theme-mode=\"dark\"]", [("--chrome-color-scheme", "dark"), .. Declarations(dark)]));
        css.Append(Block(":root[data-theme-mode=\"light\"]", [("--chrome-color-scheme", "light"), .. Declarations(light)]));
        if (theme.Mode == "system")
        {
            css.Append("@media (prefers-color-scheme: light) {\n");
            css.Append(Block(":root:not([data-theme-mode])", Declarations(light), "  "));
            css.Append("}\n");
        }

        return css.ToString();
    }

    private static (IReadOnlyDictionary<string, string> Dark, IReadOnlyDictionary<string, string> Light) ResolveModes(ChromeTheme theme)
    {
        bool primaryIsLight = theme.Mode == "light";
        IReadOnlyDictionary<string, string>? companion = primaryIsLight ? theme.Dark : theme.Light;
        Dictionary<string, string> companionColors = new(theme.Colors);
        if (companion is not null)
        {
            foreach ((string name, string value) in companion)
            {
                companionColors[name] = value;
            }
        }

        return primaryIsLight
            ? (companionColors, theme.Colors)
            : (theme.Colors, companionColors);
    }

    private static List<(string Property, string Value)> Declarations(IReadOnlyDictionary<string, string> colors)
    {
        return [.. colors.Select(pair => ($"--chrome-{Kebab(pair.Key)}", pair.Value))];
    }

    /// <summary>
    /// True when <paramref name="value"/> can only ever be one plain CSS value. A theme's values are interpolated into a
    /// stylesheet the browser runs as-is, so a value that could end its declaration or rule, open a comment or pull in a
    /// resource is dropped, not escaped: a hand-edited theme file must not be able to inject style any more than an
    /// admin's typed value can (<see cref="OrgThemeValidator"/> already refuses these; this is the second lock).
    /// </summary>
    internal static bool IsSafeValue(string value)
    {
        if (value.Length == 0 || value.Length > 200 || value.Any(char.IsControl))
        {
            return false;
        }

        if (value.AsSpan().IndexOfAny(";{}<>\\") >= 0)
        {
            return false;
        }

        string lower = value.ToLowerInvariant();
        return !lower.Contains("/*", StringComparison.Ordinal)
            && !lower.Contains("*/", StringComparison.Ordinal)
            && !lower.Contains("url(", StringComparison.Ordinal)
            && !lower.Contains("@import", StringComparison.Ordinal)
            && !lower.Contains("expression(", StringComparison.Ordinal);
    }

    private static string Block(string selector, List<(string Property, string Value)> declarations, string indent = "")
    {
        StringBuilder builder = new();
        builder.Append(indent).Append(selector).Append(" {\n");
        foreach ((string property, string value) in declarations)
        {
            if (!IsSafeValue(value) || !SafeProperty().IsMatch(property))
            {
                continue;
            }

            builder.Append(indent).Append("  ").Append(property).Append(": ").Append(value).Append(";\n");
        }

        builder.Append(indent).Append("}\n");
        return builder.ToString();
    }

    /// <summary>Turns camelCase token names into kebab-case CSS property suffixes: surfaceRaised -> surface-raised.</summary>
    private static string Kebab(string name)
    {
        return CamelBoundary().Replace(name, "$1-$2").ToLowerInvariant();
    }

    /// <summary>Numbers written without a trailing `.0`, as the TypeScript compiler's String(number) does.</summary>
    private static string Format(double value)
    {
        return value == Math.Floor(value) && !double.IsInfinity(value)
            ? ((long)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("^--chrome-[a-z0-9-]+$", RegexOptions.None, Draughtsman.Core.RegexLimits.TimeoutMilliseconds)]
    private static partial Regex SafeProperty();

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.None, Draughtsman.Core.RegexLimits.TimeoutMilliseconds)]
    private static partial Regex CamelBoundary();
}
