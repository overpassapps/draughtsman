using System.Text.RegularExpressions;

namespace Draughtsman.Server.Theming;

/// <summary>
/// An organisation theme (admin Themes page): the chrome tokens and a diagram-theme override, created and edited by
/// an admin without touching files. It is the same shape as the chrome theme file in <c>themes/</c> (see
/// <see cref="OrgThemeYaml"/>), so a theme exported from the page can be dropped in as a file and a file can be
/// imported into the page. The chrome part compiles through <see cref="ChromeThemeCss"/>; the diagram part is applied
/// over one of the compiled diagram themes by the SPA (<c>web/src/theme/orgTheme.ts</c>).
/// <para>
/// Everything an admin types reaches CSS (colours, fonts, radius) or an SVG attribute (diagram colours and fonts), so
/// <see cref="OrgThemeValidator"/> admits only values of a shape that cannot carry markup, a second declaration or a
/// script: no free-form CSS ever leaves this type.
/// </para>
/// </summary>
internal sealed record OrgTheme(
    string Id,
    string Name,
    string Mode,
    IReadOnlyDictionary<string, string> Dark,
    IReadOnlyDictionary<string, string> Light,
    double Radius,
    string FontUi,
    string FontMono,
    OrgDiagramTheme Diagram,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The chrome tokens every mode must give, in the order the page lists them. Matches <c>themes/chrome.yaml</c>.</summary>
    public static readonly IReadOnlyList<string> ChromeColourKeys =
        ["brand", "brandText", "surface", "surfaceRaised", "border", "text", "textMuted", "accent", "danger"];

    /// <summary>
    /// The chrome theme these tokens compile to. <paramref name="productName"/> is required by the chrome theme type
    /// but never read by the CSS compiler; the product name is a branding setting, not part of a theme.
    /// </summary>
    public ChromeTheme ToChromeTheme(string productName)
    {
        return new ChromeTheme
        {
            Name = Name,
            ProductName = productName,
            Mode = Mode,
            Colors = Mode == "light" ? Light : Dark,
            Light = Light,
            Dark = Dark,
            Radius = Radius,
            Font = new Dictionary<string, string> { ["ui"] = FontUi, ["mono"] = FontMono },
        };
    }
}

/// <summary>
/// The diagram part of an organisation theme: which compiled diagram theme it starts from, and what it changes. A
/// palette entry replaces that palette colour wherever the base theme's node and edge styles use it (the compiled
/// themes write literal hex, not references), so one edit restyles every kind that shares the colour and the
/// theme's visual hierarchy survives. The backgrounds, grid colour and fonts are direct.
/// </summary>
internal sealed record OrgDiagramTheme(
    string Base,
    IReadOnlyDictionary<string, string> Palette,
    string? CanvasBackground,
    string? ExportBackground,
    string? GridColor,
    string? FontLabel,
    string? FontMono)
{
    /// <summary>The compiled diagram themes the SPA ships (themes/diagram-*.yaml); a theme names one as its base.</summary>
    public static readonly IReadOnlyList<string> Bases = ["overpass", "neutral", "mono"];

    public static readonly OrgDiagramTheme Default = new("overpass", new Dictionary<string, string>(), null, null, null, null, null);
}

/// <summary>A reason an organisation theme was refused, worded for the admin who typed it.</summary>
internal sealed record OrgThemeProblem(string Field, string Message);

/// <summary>
/// Validates and normalises an organisation theme. Colours are plain hex (#rgb or #rrggbb, stored as lower-case
/// #rrggbb), fonts are a comma-separated list of family names (optionally quoted, nothing else), the radius a small
/// number. Anything else is refused rather than escaped: a theme must never inject markup, a second CSS declaration
/// or script, and "escaped" is weaker than "cannot be expressed". Returns every problem at once so the admin fixes
/// the form in one pass.
/// </summary>
internal static partial class OrgThemeValidator
{
    public const int MaxNameLength = 60;

    public const int MaxFontLength = 120;

    public const int MaxPaletteEntries = 32;

    /// <summary>Ids that would collide with a route (<c>/api/admin/themes/default</c>) or a shipped theme.</summary>
    private static readonly string[] ReservedIds = ["default", "overpass", "import", "new"];

    public static bool IsValidId(string id)
    {
        return IdPattern().IsMatch(id) && !ReservedIds.Contains(id, StringComparer.Ordinal);
    }

    /// <summary>Turns a display name into an id: lower-case words joined by hyphens, never reserved, at most 40 characters.</summary>
    public static string Slug(string name)
    {
        string slug = NonIdCharacters().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 36)
        {
            slug = slug[..36].Trim('-');
        }

        if (slug.Length == 0)
        {
            slug = "theme";
        }

        return ReservedIds.Contains(slug, StringComparer.Ordinal) ? slug + "-theme" : slug;
    }

    /// <summary>The normalised colour (lower-case #rrggbb), or null when the text is not a hex colour.</summary>
    public static string? NormaliseColour(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string trimmed = value.Trim();
        if (!HexColour().IsMatch(trimmed))
        {
            return null;
        }

        string hex = trimmed[1..].ToLowerInvariant();
        if (hex.Length == 3)
        {
            hex = string.Concat(hex.Select(c => new string(c, 2)));
        }

        return "#" + hex;
    }

    public static bool IsSafeFont(string? value)
    {
        return value is not null && value.Length <= MaxFontLength && FontList().IsMatch(value);
    }

    /// <summary>
    /// Checks <paramref name="theme"/> and returns the normalised theme (colours lower-cased, names trimmed) plus any
    /// problems; the theme is only meaningful when there are none.
    /// </summary>
    public static (OrgTheme Theme, List<OrgThemeProblem> Problems) Validate(OrgTheme theme)
    {
        List<OrgThemeProblem> problems = [];

        string name = new([.. (theme.Name ?? string.Empty).Where(c => !char.IsControl(c))]);
        name = name.Trim();
        if (name.Length == 0)
        {
            problems.Add(new OrgThemeProblem("name", "Give the theme a name."));
        }
        else if (name.Length > MaxNameLength)
        {
            problems.Add(new OrgThemeProblem("name", $"The name can be at most {MaxNameLength} characters."));
        }

        if (theme.Mode is not ("dark" or "light" or "system"))
        {
            problems.Add(new OrgThemeProblem("mode", "The mode must be dark, light or system."));
        }

        Dictionary<string, string> dark = ValidateColours("dark", theme.Dark, problems);
        Dictionary<string, string> light = ValidateColours("light", theme.Light, problems);

        if (!double.IsFinite(theme.Radius) || theme.Radius < 0 || theme.Radius > 24)
        {
            problems.Add(new OrgThemeProblem("radius", "The corner radius must be between 0 and 24."));
        }

        if (!IsSafeFont(theme.FontUi))
        {
            problems.Add(new OrgThemeProblem("fontUi", FontMessage("interface")));
        }

        if (!IsSafeFont(theme.FontMono))
        {
            problems.Add(new OrgThemeProblem("fontMono", FontMessage("monospace")));
        }

        OrgDiagramTheme diagram = ValidateDiagram(theme.Diagram, problems);

        OrgTheme normalised = theme with
        {
            Name = name,
            Dark = dark,
            Light = light,
            FontUi = (theme.FontUi ?? string.Empty).Trim(),
            FontMono = (theme.FontMono ?? string.Empty).Trim(),
            Diagram = diagram,
        };
        return (normalised, problems);
    }

    private static Dictionary<string, string> ValidateColours(string set, IReadOnlyDictionary<string, string>? colours, List<OrgThemeProblem> problems)
    {
        Dictionary<string, string> result = [];
        foreach (string key in OrgTheme.ChromeColourKeys)
        {
            if (colours is null || !colours.TryGetValue(key, out string? value))
            {
                problems.Add(new OrgThemeProblem($"{set}.{key}", $"The {set} set is missing {key}."));
                continue;
            }

            string? normalised = NormaliseColour(value);
            if (normalised is null)
            {
                problems.Add(new OrgThemeProblem($"{set}.{key}", $"{key} ({set}) must be a hex colour such as #e04832."));
                continue;
            }

            result[key] = normalised;
        }

        if (colours is not null)
        {
            foreach (string key in colours.Keys.Where(k => !OrgTheme.ChromeColourKeys.Contains(k, StringComparer.Ordinal)))
            {
                problems.Add(new OrgThemeProblem($"{set}.{key}", $"{key} is not a chrome colour this theme knows."));
            }
        }

        return result;
    }

    private static OrgDiagramTheme ValidateDiagram(OrgDiagramTheme? diagram, List<OrgThemeProblem> problems)
    {
        if (diagram is null)
        {
            problems.Add(new OrgThemeProblem("diagram", "The theme needs a diagram section."));
            return OrgDiagramTheme.Default;
        }

        if (!OrgDiagramTheme.Bases.Contains(diagram.Base, StringComparer.Ordinal))
        {
            problems.Add(new OrgThemeProblem("diagram.base", $"The diagram base must be one of {string.Join(", ", OrgDiagramTheme.Bases)}."));
        }

        Dictionary<string, string> palette = [];
        if (diagram.Palette.Count > MaxPaletteEntries)
        {
            problems.Add(new OrgThemeProblem("diagram.palette", $"A palette can override at most {MaxPaletteEntries} colours."));
        }
        else
        {
            foreach ((string key, string value) in diagram.Palette)
            {
                if (!PaletteKey().IsMatch(key))
                {
                    problems.Add(new OrgThemeProblem($"diagram.palette.{Truncate(key)}", "A palette name is letters and digits, starting with a letter."));
                    continue;
                }

                string? normalised = NormaliseColour(value);
                if (normalised is null)
                {
                    problems.Add(new OrgThemeProblem($"diagram.palette.{key}", $"{key} must be a hex colour such as #e04832."));
                    continue;
                }

                palette[key] = normalised;
            }
        }

        string? canvas = OptionalColour("diagram.canvasBackground", "The canvas background", diagram.CanvasBackground, problems);
        string? export = OptionalColour("diagram.exportBackground", "The export background", diagram.ExportBackground, problems);
        string? grid = OptionalColour("diagram.gridColor", "The grid colour", diagram.GridColor, problems);
        string? labelFont = OptionalFont("diagram.fontLabel", "label", diagram.FontLabel, problems);
        string? monoFont = OptionalFont("diagram.fontMono", "monospace", diagram.FontMono, problems);

        return new OrgDiagramTheme(diagram.Base, palette, canvas, export, grid, labelFont, monoFont);
    }

    private static string? OptionalColour(string field, string label, string? value, List<OrgThemeProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string? normalised = NormaliseColour(value);
        if (normalised is null)
        {
            problems.Add(new OrgThemeProblem(field, $"{label} must be a hex colour such as #151e33."));
        }

        return normalised;
    }

    private static string? OptionalFont(string field, string label, string? value, List<OrgThemeProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!IsSafeFont(value))
        {
            problems.Add(new OrgThemeProblem(field, FontMessage(label)));
            return null;
        }

        return value.Trim();
    }

    private static string FontMessage(string which)
    {
        return $"The {which} font must be family names separated by commas, such as Inter, system-ui, sans-serif. Letters, digits, spaces, hyphens and underscores only, with optional quotes around a name.";
    }

    private static string Truncate(string value)
    {
        return value.Length <= 24 ? value : value[..24];
    }

    // A font family name, bare or in one matched pair of quotes; the list is comma-separated. No semicolons, braces,
    // parentheses, slashes, angle brackets or backslashes can be spelled, so a value cannot close its declaration.
    [GeneratedRegex("""^\s*(?:[A-Za-z0-9_ -]+|"[A-Za-z0-9_ -]+"|'[A-Za-z0-9_ -]+')(?:\s*,\s*(?:[A-Za-z0-9_ -]+|"[A-Za-z0-9_ -]+"|'[A-Za-z0-9_ -]+'))*\s*$""", RegexOptions.None, Draughtsman.Core.RegexLimits.TimeoutMilliseconds)]
    private static partial Regex FontList();

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.None, Draughtsman.Core.RegexLimits.TimeoutMilliseconds)]
    private static partial Regex HexColour();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.None, Draughtsman.Core.RegexLimits.TimeoutMilliseconds)]
    private static partial Regex IdPattern();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.None, Draughtsman.Core.RegexLimits.TimeoutMilliseconds)]
    private static partial Regex NonIdCharacters();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,31}$", RegexOptions.None, Draughtsman.Core.RegexLimits.TimeoutMilliseconds)]
    private static partial Regex PaletteKey();
}
