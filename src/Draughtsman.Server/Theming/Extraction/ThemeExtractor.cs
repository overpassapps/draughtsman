using System.Globalization;

namespace Draughtsman.Server.Theming.Extraction;

/// <summary>What the extractor reads: stylesheet texts (inline style blocks and linked sheets) and the page's theme-color meta values.</summary>
internal sealed record ThemeSource(IReadOnlyList<string> Sheets, IReadOnlyList<string> ThemeColours)
{
    /// <summary>Pasted text: an HTML page (its style blocks and theme-color are read) or, otherwise, a stylesheet.</summary>
    public static ThemeSource FromText(string text)
    {
        if (HtmlScanner.LooksLikeHtml(text))
        {
            return new ThemeSource(HtmlScanner.StyleBlocks(text), HtmlScanner.ThemeColours(text));
        }

        return new ThemeSource([text], []);
    }
}

/// <summary>A draft theme, and why it is what it is. Nothing here is saved; the draft only becomes a theme through the create and save routes.</summary>
internal sealed record ThemeExtraction(OrgTheme Draft, IReadOnlyList<string> Reasons, IReadOnlyList<string> Adjustments);

/// <summary>The source has no colour the extractor could read.</summary>
internal sealed class ThemeExtractionException(string message) : Exception(message);

/// <summary>
/// Turns the CSS of a site into a draft organisation theme (admin Themes, "From a website"). Pure and deterministic: text in, draft
/// out, no network, no clock, no randomness, so the same input always gives the same draft and the paste-CSS path works on an
/// air-gapped install. Colours are read from custom properties (named for their role: --primary, --brand, --background, --text,
/// --border, ...), then from where each colour is used (page and card backgrounds, text, links, buttons, borders) weighted by how
/// much that place says about the site. The mode found is used as written; the other is derived from it. Each mode is then moved,
/// minimally, until it passes the same contrast checks the Themes page shows (<see cref="ChromeContrast"/>), and every move is
/// reported. Only hex colours come out, so the draft passes <see cref="OrgThemeValidator"/> as it stands: nothing of the source
/// (selectors, values, fonts, images) is carried into the theme.
/// </summary>
internal static class ThemeExtractor
{
    private const int MaxDeclarations = 400_000;

    private const double DarkLuminance = 0.25;

    private static readonly Rgb White = new(255, 255, 255);

    private enum Role
    {
        Surface,
        Raised,
        Text,
        Muted,
        Border,
        Brand,
        BrandText,
        Accent,
        Danger,
        Button,
        Link,
        Bar,
        Background,
        TextOther,
    }

    private sealed record Obs(Rgb Colour, Role Role, double Weight, string Source);

    public static ThemeExtraction Extract(string text, string name = "Extracted theme")
    {
        return Extract(ThemeSource.FromText(text), name);
    }

    public static ThemeExtraction Extract(ThemeSource source, string name = "Extracted theme")
    {
        List<CssRule> rules = [];
        foreach (string sheet in source.Sheets)
        {
            rules.AddRange(CssSheet.Parse(sheet));
        }

        List<Obs> defaults = [];
        List<Obs> darks = [];
        Collect(rules, defaults, darks);

        foreach (string value in source.ThemeColours.Take(4))
        {
            if (CssColour.Parse(value) is { } colour)
            {
                defaults.Add(IsChromatic(colour)
                    ? new Obs(colour, Role.Brand, 25, "the page's theme-color meta tag")
                    : new Obs(colour, Role.Surface, 10, "the page's theme-color meta tag"));
            }
        }

        if (defaults.Count == 0 && darks.Count == 0)
        {
            throw new ThemeExtractionException("No colours could be read from that CSS. Paste the stylesheet itself, or a page with its styles inline.");
        }

        List<string> reasons = [];
        List<string> adjustments = [];
        Picks found = Pick(defaults);
        Picks foundDark = Pick(darks);

        bool primaryDark;
        Picks primaryPicks = found;
        if (found.IsEmpty && !foundDark.IsEmpty)
        {
            primaryDark = true;
            primaryPicks = foundDark;
        }
        else
        {
            primaryDark = found.Surface is { } s ? s.Luminance < DarkLuminance : found.Text is { } t && t.Luminance > 0.5;
        }

        reasons.Add(primaryDark ? "The site is dark (its page background or text says so), so the theme's mode is dark." : "The site is light (its page background or text says so), so the theme's mode is light.");
        Dictionary<string, Rgb> dark;
        Dictionary<string, Rgb> light;
        Rgb? brandHint = primaryPicks.Brand ?? primaryPicks.Accent;
        if (primaryDark)
        {
            dark = Complete(primaryPicks, true, null, brandHint, reasons, adjustments);
            light = Complete(new Picks(), false, dark, brandHint, reasons, adjustments);
            reasons.Add("Light colours derived from the dark ones: the site gave only a dark scheme.");
        }
        else if (!foundDark.IsEmpty)
        {
            light = Complete(found, false, null, brandHint, reasons, adjustments);
            dark = Complete(foundDark with { Brand = foundDark.Brand ?? found.Brand, Accent = foundDark.Accent ?? found.Accent, BrandText = foundDark.BrandText ?? found.BrandText }, true, light, brandHint, reasons, adjustments);
        }
        else
        {
            light = Complete(found, false, null, brandHint, reasons, adjustments);
            dark = Complete(new Picks(), true, light, brandHint, reasons, adjustments);
            reasons.Add("Dark colours derived from the light ones: the site gave only a light scheme.");
        }

        Dictionary<string, Rgb> primary = primaryDark ? dark : light;
        (string diagramBase, Dictionary<string, string> palette) = Diagram(primary, primaryDark, reasons);

        OrgTheme draft = new(
            OrgThemeValidator.Slug(name),
            name,
            primaryDark ? "dark" : "light",
            ToHex(dark),
            ToHex(light),
            6,
            "Inter, system-ui, sans-serif",
            "JetBrains Mono, monospace",
            new OrgDiagramTheme(diagramBase, palette, null, null, null, null, null),
            DateTimeOffset.UnixEpoch);
        (OrgTheme valid, List<OrgThemeProblem> problems) = OrgThemeValidator.Validate(draft);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The extractor produced a draft the theme validator refuses: " + string.Join("; ", problems.Select(p => $"{p.Field}: {p.Message}")));
        }

        reasons.Add("Fonts and corner radius are left at the Overpass defaults: fonts are not copied from a site.");
        return new ThemeExtraction(valid, reasons, adjustments);
    }

    private static Dictionary<string, string> ToHex(Dictionary<string, Rgb> colours)
    {
        return OrgTheme.ChromeColourKeys.ToDictionary(key => key, key => colours[key].Hex, StringComparer.Ordinal);
    }

    // ---- reading ----------------------------------------------------------------------------------------------------

    private static void Collect(List<CssRule> rules, List<Obs> defaults, List<Obs> darks)
    {
        Dictionary<string, string>[] vars = [new(StringComparer.Ordinal), new(StringComparer.Ordinal)];
        foreach (CssRule rule in rules)
        {
            bool root = IsRootSelector(rule.Selector);
            foreach (CssDeclaration declaration in rule.Declarations)
            {
                if (declaration.Property.StartsWith("--", StringComparison.Ordinal) && declaration.Value.Length <= 512)
                {
                    Dictionary<string, string> scope = vars[(int)rule.Scheme];
                    if (root || rule.Scheme == SheetScheme.Dark)
                    {
                        scope[declaration.Property] = declaration.Value;
                    }
                    else
                    {
                        scope.TryAdd(declaration.Property, declaration.Value);
                    }
                }
            }
        }

        foreach (SheetScheme scheme in new[] { SheetScheme.Default, SheetScheme.Dark })
        {
            List<Obs> target = scheme == SheetScheme.Dark ? darks : defaults;
            foreach (string property in vars[(int)scheme].Keys.Order(StringComparer.Ordinal))
            {
                if (NameRole(property) is not { } named)
                {
                    continue;
                }

                string? resolved = Resolve(vars[(int)scheme][property], scheme, vars);
                if (resolved is not null && CssColour.Parse(resolved, allowBareHsl: true) is { } colour)
                {
                    target.Add(new Obs(colour, named.Role, named.Weight, $"custom property {SafeName(property)}"));
                }
            }
        }

        int budget = MaxDeclarations;
        foreach (CssRule rule in rules)
        {
            List<Obs> target = rule.Scheme == SheetScheme.Dark ? darks : defaults;
            Flags flags = Flags.Of(rule.Selector);
            foreach (CssDeclaration declaration in rule.Declarations)
            {
                if (--budget < 0)
                {
                    return;
                }

                if (declaration.Property.StartsWith("--", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach ((Rgb colour, double factor) in ColoursOf(declaration, rule.Scheme, vars))
                {
                    Observe(declaration.Property, flags, colour, factor * (flags.Interactive ? 0.3 : 1), target);
                }
            }
        }
    }

    private static void Observe(string property, Flags f, Rgb colour, double factor, List<Obs> into)
    {
        void Add(Role role, double weight, string source)
        {
            into.Add(new Obs(colour, role, weight * factor, source));
        }

        if (property is "background" or "background-color" or "background-image")
        {
            if (f.Danger)
            {
                Add(Role.Danger, 4, "a danger or error background");
            }
            else if (f.Button)
            {
                Add(Role.Button, 6, "a button background");
            }
            else if (f.Root)
            {
                Add(Role.Surface, 10, "the page background");
            }
            else if (f.Main)
            {
                Add(Role.Surface, 5, "the main content background");
            }
            else if (f.Card)
            {
                Add(Role.Raised, 3, "a card or panel background");
            }
            else if (f.Bar)
            {
                Add(Role.Bar, 2, "a header, navigation or footer background");
            }
            else
            {
                Add(Role.Background, 1, "a background");
            }
        }
        else if (property == "color")
        {
            if (f.Danger)
            {
                Add(Role.Danger, 4, "danger or error text");
            }
            else if (f.Button)
            {
                Add(Role.TextOther, 1, "button text");
            }
            else if (f.Link)
            {
                Add(Role.Link, 6, "link colour");
            }
            else if (f.Muted)
            {
                Add(Role.Muted, 4, "secondary text");
            }
            else if (f.Root || f.Paragraph || f.Main)
            {
                Add(Role.Text, 8, "body text");
            }
            else
            {
                Add(Role.TextOther, 1, "text");
            }
        }
        else if (property.StartsWith("border", StringComparison.Ordinal) || property.StartsWith("outline", StringComparison.Ordinal))
        {
            if (f.Danger)
            {
                Add(Role.Danger, 2, "a danger or error border");
            }
            else if (f.Button)
            {
                Add(Role.Button, 2, "a button border");
            }
            else
            {
                Add(Role.Border, 2, "a border");
            }
        }
        else if (property is "fill" or "stroke" or "accent-color")
        {
            Add(Role.Accent, 1, "an icon or control colour");
        }
    }

    /// <summary>The colours one declaration paints, each with a factor: 1 for a plain colour, 0.5 for the end stops of a gradient (never its middle).</summary>
    private static List<(Rgb Colour, double Factor)> ColoursOf(CssDeclaration declaration, SheetScheme scheme, Dictionary<string, string>[] vars)
    {
        List<(Rgb, double)> found = [];
        string property = declaration.Property;
        bool relevant = property is "background" or "background-color" or "background-image" or "color" or "fill" or "stroke" or "accent-color"
            || property.StartsWith("border", StringComparison.Ordinal) || property.StartsWith("outline", StringComparison.Ordinal);
        if (!relevant || property.StartsWith("border-image", StringComparison.Ordinal) || property.Contains("radius", StringComparison.Ordinal) || property.Contains("width", StringComparison.Ordinal) || property.Contains("style", StringComparison.Ordinal) || property.Contains("image-", StringComparison.Ordinal))
        {
            return found;
        }

        string? value = Resolve(declaration.Value, scheme, vars);
        if (value is null)
        {
            return found;
        }

        int gradient = value.IndexOf("gradient(", StringComparison.OrdinalIgnoreCase);
        if (gradient >= 0)
        {
            int open = value.IndexOf('(', gradient);
            int close = MatchingParen(value, open);
            if (close > open)
            {
                List<string> stops = SplitTopLevel(value[(open + 1)..close], ',');
                int first = stops.Count > 0 && IsGradientDirection(stops[0]) ? 1 : 0;
                if (stops.Count - first >= 2)
                {
                    foreach (string stop in new[] { stops[first], stops[^1] })
                    {
                        if (FirstColour(stop) is { } colour)
                        {
                            found.Add((colour, 0.5));
                        }
                    }
                }
            }

            return found;
        }

        if (FirstColour(value) is { } plain)
        {
            found.Add((plain, 1));
        }

        return found;
    }

    private static bool IsGradientDirection(string argument)
    {
        string a = argument.Trim().ToLowerInvariant();
        return a.StartsWith("to ", StringComparison.Ordinal) || a.EndsWith("deg", StringComparison.Ordinal) || a.EndsWith("turn", StringComparison.Ordinal) || a.EndsWith("rad", StringComparison.Ordinal)
            || a.StartsWith("circle", StringComparison.Ordinal) || a.StartsWith("ellipse", StringComparison.Ordinal) || a.StartsWith("at ", StringComparison.Ordinal) || a.StartsWith("from ", StringComparison.Ordinal);
    }

    private static Rgb? FirstColour(string value)
    {
        foreach (string token in SplitTopLevel(value, ' '))
        {
            if (token.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (CssColour.Parse(token) is { } colour)
            {
                return colour;
            }
        }

        return null;
    }

    /// <summary>Splits at a separator outside parentheses and quotes; empty pieces are dropped.</summary>
    private static List<string> SplitTopLevel(string text, char separator)
    {
        List<string> parts = [];
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && (c == separator || (separator == ' ' && char.IsWhiteSpace(c))))
            {
                if (i > start)
                {
                    parts.Add(text[start..i].Trim());
                }

                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            parts.Add(text[start..].Trim());
        }

        return parts.Where(p => p.Length > 0).ToList();
    }

    private static int MatchingParen(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Replaces var(--x, fallback) with what it names, up to a fixed number of times; null when any is left unresolved (or loops).</summary>
    private static string? Resolve(string value, SheetScheme scheme, Dictionary<string, string>[] vars)
    {
        string current = value;
        for (int pass = 0; pass < 16; pass++)
        {
            int at = current.IndexOf("var(", StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return current;
            }

            int open = at + 3;
            int close = MatchingParen(current, open);
            if (close < 0)
            {
                return null;
            }

            List<string> args = SplitTopLevel(current[(open + 1)..close], ',');
            string? replacement = null;
            if (args.Count > 0)
            {
                string key = args[0].Trim().ToLowerInvariant();
                if (vars[(int)scheme].TryGetValue(key, out string? own))
                {
                    replacement = own;
                }
                else if (vars[(int)SheetScheme.Default].TryGetValue(key, out string? shared))
                {
                    replacement = shared;
                }
                else if (args.Count > 1)
                {
                    replacement = string.Join(", ", args.Skip(1));
                }
            }

            if (replacement is null)
            {
                return null;
            }

            current = current[..at] + replacement + current[(close + 1)..];
            if (current.Length > 1024)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The role a custom property's name says it plays, and how much the site's own naming is trusted.</summary>
    private static (Role Role, double Weight)? NameRole(string property)
    {
        string n = property.TrimStart('-').ToLowerInvariant();
        foreach (string prefix in new[] { "bs-", "md-sys-color-", "wp--preset--color--", "color-", "clr-" })
        {
            if (n.StartsWith(prefix, StringComparison.Ordinal))
            {
                n = n[prefix.Length..];
                break;
            }
        }

        if (n.StartsWith("tw-", StringComparison.Ordinal) || n.Length == 0)
        {
            return null;
        }

        string[] excluded = ["radius", "width", "size", "shadow", "font", "spacing", "gap", "margin", "padding", "height", "opacity", "duration", "transition", "weight", "rounded", "blur", "ease", "breakpoint", "animation", "z-index", "columns", "ratio", "offset"];
        if (excluded.Any(word => n.Contains(word, StringComparison.Ordinal)))
        {
            return null;
        }

        bool Has(params string[] words) => words.Any(word => n.Contains(word, StringComparison.Ordinal));
        bool StartsWith(params string[] words) => words.Any(word => n.StartsWith(word, StringComparison.Ordinal));

        if (Has("on-primary", "primary-foreground", "primary-contrast", "primary-text", "text-on-primary", "on-brand", "brand-text", "brand-contrast", "brand-foreground", "button-text", "btn-text"))
        {
            return (Role.BrandText, 30);
        }

        if (Has("danger", "error", "destructive", "negative", "critical", "invalid"))
        {
            return (Role.Danger, 30);
        }

        bool textual = Has("text", "foreground", "fg", "ink") || n is "color" or "body-color";
        if (textual && Has("muted", "subtle", "secondary", "dim", "tertiary", "placeholder", "caption", "light", "gray", "grey", "soft", "faint", "-2", "-3"))
        {
            return (Role.Muted, 30);
        }

        if (Has("muted") && !textual)
        {
            return (Role.Raised, 15);
        }

        if (StartsWith("text", "foreground", "fg", "body-color", "body-text", "ink", "on-surface", "on-background", "heading") || textual && !Has("brand", "primary", "accent", "link"))
        {
            return (Role.Text, 30);
        }

        if (Has("border", "divider", "separator", "outline", "stroke", "hairline"))
        {
            return (Role.Border, 25);
        }

        if (Has("brand", "primary", "theme-color", "key-color"))
        {
            return (Role.Brand, 40);
        }

        if (Has("background", "bg", "surface", "canvas", "paper", "page"))
        {
            return Has("raised", "elevated", "card", "panel", "alt", "secondary", "subtle", "container", "popover", "tint", "sunken", "inset", "sidebar", "header", "muted")
                ? (Role.Raised, 20)
                : (Role.Surface, 35);
        }

        if (Has("accent", "link", "highlight", "secondary", "interactive", "action", "cta"))
        {
            return (Role.Accent, 25);
        }

        return null;
    }

    private static string SafeName(string property)
    {
        string clean = new([.. property.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(40)]);
        return clean;
    }

    private static bool IsRootSelector(string selector, bool includeApp = true)
    {
        foreach (string part in selector.Split(','))
        {
            string p = part.Trim();
            if (p is ":root" or "html" or "body" or "*" or ":host" or "html body"
                || (includeApp && p is "main" or "#app" or "#root" or ".app")
                || p.StartsWith("body.", StringComparison.Ordinal) || p.StartsWith("html.", StringComparison.Ordinal) || p.StartsWith("html[", StringComparison.Ordinal) || p.StartsWith("body[", StringComparison.Ordinal) || p.StartsWith(":root[", StringComparison.Ordinal) || p.StartsWith(":root.", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct Flags(bool Root, bool Main, bool Button, bool Link, bool Bar, bool Card, bool Muted, bool Danger, bool Paragraph, bool Interactive)
    {
        public static Flags Of(string selector)
        {
            bool Has(params string[] words) => words.Any(word => selector.Contains(word, StringComparison.Ordinal));
            bool link = false;
            foreach (string part in selector.Split(','))
            {
                string[] tokens = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length > 0 && (tokens[^1] == "a" || tokens[^1].StartsWith("a:", StringComparison.Ordinal) || tokens[^1].StartsWith("a.", StringComparison.Ordinal)))
                {
                    link = true;
                }
            }

            bool paragraph = selector.Split(',').Any(part => part.Trim() is "p" or "li" or "article" or "td");
            return new Flags(
                IsRootSelector(selector, includeApp: false),
                !IsRootSelector(selector, includeApp: false) && Has("main", "#app", "#root", ".app"),
                Has("button", ".btn", "btn-", "[type=submit]", "[type=\"submit\"]", ".cta", "[type=button]"),
                link || Has(".link"),
                Has("header", "nav", "footer", ".topbar", ".top-bar"),
                Has(".card", ".panel", ".box", ".modal", ".dropdown", "aside", ".sidebar", ".well"),
                Has("muted", "secondary", "subtle", "caption", "small", "placeholder", ".text-gray", ".text-grey", "hint", "meta"),
                Has("danger", "error", "invalid", "alert-error", "destructive"),
                paragraph,
                Has(":hover", ":focus", ":active", ":disabled", ":visited"));
        }
    }

    // ---- choosing ---------------------------------------------------------------------------------------------------

    private sealed record Picks
    {
        public Rgb? Surface { get; init; }

        public Rgb? Raised { get; init; }

        public Rgb? Text { get; init; }

        public Rgb? Muted { get; init; }

        public Rgb? Border { get; init; }

        public Rgb? Brand { get; init; }

        public Rgb? BrandText { get; init; }

        public Rgb? Accent { get; init; }

        public Rgb? Danger { get; init; }

        public Dictionary<string, string> Sources { get; init; } = [];

        public bool IsEmpty => Surface is null && Raised is null && Text is null && Muted is null && Border is null && Brand is null && BrandText is null && Accent is null && Danger is null;
    }

    private static bool IsChromatic(Rgb colour)
    {
        (_, double s, double l) = colour.ToHsl();
        return s >= 0.15 && l is > 0.08 and < 0.95;
    }

    private static Picks Pick(List<Obs> set)
    {
        Dictionary<string, string> sources = [];
        Rgb? Top(string token, Func<Obs, bool> filter, params Role[] roles)
        {
            foreach (Role role in roles)
            {
                var scored = set
                    .Where(o => o.Role == role && filter(o))
                    .GroupBy(o => o.Colour)
                    .Select(g => (Colour: g.Key, Score: g.Sum(o => o.Weight) + (role == Role.Brand && IsChromatic(g.Key) ? 5 : 0), Source: g.OrderByDescending(o => o.Weight).First().Source))
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Colour.Hex, StringComparer.Ordinal)
                    .ToList();
                if (scored.Count > 0)
                {
                    sources[token] = scored[0].Source;
                    return scored[0].Colour;
                }
            }

            return null;
        }

        static bool Any(Obs _) => true;
        static bool Chromatic(Obs o) => IsChromatic(o.Colour);

        Rgb? brand = Top("brand", Any, Role.Brand);
        if (brand is null)
        {
            // Nothing says "brand": the most-used saturated colour among where brands show up, in order of how much each place says.
            brand = Top("brand", Chromatic, Role.Button, Role.Bar, Role.Link, Role.Accent, Role.Background);
        }

        Rgb? accent = Top("accent", Chromatic, Role.Accent, Role.Link);
        return new Picks
        {
            Surface = Top("surface", Any, Role.Surface, Role.Background),
            Raised = Top("surfaceRaised", Any, Role.Raised),
            Text = Top("text", Any, Role.Text, Role.TextOther),
            Muted = Top("textMuted", Any, Role.Muted),
            Border = Top("border", Any, Role.Border),
            Brand = brand,
            BrandText = Top("brandText", Any, Role.BrandText),
            Accent = accent,
            Danger = Top("danger", Any, Role.Danger),
            Sources = sources,
        };
    }

    // ---- completing and fixing one mode ----------------------------------------------------------------------------

    private static Dictionary<string, Rgb> Complete(Picks f, bool dark, Dictionary<string, Rgb>? donor, Rgb? brandHint, List<string> reasons, List<string> adjustments)
    {
        string mode = dark ? "dark" : "light";
        // A site with no colour of its own (black and white) takes its text colour as the brand, rather than a colour it never used.
        Rgb brandSeed = f.Brand ?? donor?["brand"] ?? brandHint ?? f.Text ?? (dark ? new Rgb(0xf2, 0xf2, 0xf2) : new Rgb(0x11, 0x18, 0x27));
        (double hue, double sat, _) = (f.Surface ?? brandSeed).ToHsl();
        if (f.Surface is null || sat < 0.08)
        {
            (hue, sat, _) = brandSeed.ToHsl();
            sat = Math.Min(sat, 0.3) * 0.6;
        }

        bool derivedMode = f.IsEmpty;

        Rgb surface = f.Surface ?? (dark ? Rgb.FromHsl(hue, sat, 0.07) : White);
        Rgb text = f.Text ?? (dark ? Rgb.FromHsl(hue, sat * 0.3, 0.95) : Rgb.FromHsl(hue, sat, 0.12));
        Rgb raised = f.Raised is { } r && r != surface && Rgb.Contrast(r, surface) < 2.5
            ? r
            : Rgb.Mix(surface, text, 0.06);
        Rgb muted = f.Muted ?? Rgb.Mix(text, surface, 0.38);
        Rgb border = f.Border is { } b && Rgb.Contrast(b, surface) >= 1.1 ? b : Rgb.Mix(surface, text, 0.14);
        Rgb brand = brandSeed;
        Rgb accent = f.Accent ?? (donor is not null ? donor["accent"] : brand);
        Rgb danger = f.Danger ?? (dark ? new Rgb(0xff, 0x4d, 0x4f) : new Rgb(0xd4, 0x38, 0x0d));

        if (!derivedMode)
        {
            Say(reasons, mode, "surface", f.Sources, f.Surface, "no page background found, so one was chosen to suit the text");
            Say(reasons, mode, "text", f.Sources, f.Text, "no body text colour found, so one was chosen to read on the surface");
            Say(reasons, mode, "brand", f.Sources, f.Brand, "no brand colour found, so " + (donor is null ? "the text colour was used" : "the other mode's was kept"));
            foreach ((string token, Rgb? colour) in new (string, Rgb?)[] { ("surfaceRaised", f.Raised), ("textMuted", f.Muted), ("border", f.Border), ("accent", f.Accent), ("danger", f.Danger), ("brandText", f.BrandText) })
            {
                if (colour is not null)
                {
                    Say(reasons, mode, token, f.Sources, colour, string.Empty);
                }
            }
        }

        Dictionary<string, Rgb> tokens = new(StringComparer.Ordinal)
        {
            ["brand"] = brand,
            ["brandText"] = f.BrandText ?? new Rgb(0xff, 0xff, 0xff),
            ["surface"] = surface,
            ["surfaceRaised"] = raised,
            ["border"] = border,
            ["text"] = text,
            ["textMuted"] = muted,
            ["accent"] = accent,
            ["danger"] = danger,
        };

        // The brand text is white or near-black, whichever reads on the brand, unless the site named one that already does.
        Rgb brandText = tokens["brandText"];
        if (f.BrandText is null || Rgb.Contrast(brandText, brand) < ChromeContrast.AaText)
        {
            Rgb white = new(255, 255, 255);
            Rgb ink = new(0x0f, 0x17, 0x2a);
            brandText = Rgb.Contrast(white, brand) >= Rgb.Contrast(ink, brand) ? white : ink;
            tokens["brandText"] = brandText;
        }

        Fix(tokens, "brand", [("brandText", ChromeContrast.AaText)], brandText.Luminance <= 0.5, mode, adjustments);
        Fix(tokens, "text", [("surface", ChromeContrast.AaText), ("surfaceRaised", ChromeContrast.AaText)], dark, mode, adjustments);
        Fix(tokens, "textMuted", [("surface", ChromeContrast.AaText), ("surfaceRaised", ChromeContrast.AaText)], dark, mode, adjustments);
        Fix(tokens, "accent", [("surface", ChromeContrast.AaText)], dark, mode, adjustments);
        Fix(tokens, "danger", [("surface", ChromeContrast.AaGraphic), ("surfaceRaised", ChromeContrast.AaGraphic)], dark, mode, adjustments);

        // The control outline is text mixed into the surface, so text may need to be stronger than 4.5:1 for the outline to reach 3:1.
        Rgb textBefore = tokens["text"];
        for (int pass = 0; pass < 60 && ChromeContrast.Issues(tokens).Count > 0; pass++)
        {
            Rgb pushed = Rgb.Mix(tokens["text"], dark ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0), 0.03);
            if (pushed == tokens["text"])
            {
                break;
            }

            tokens["text"] = pushed;
        }

        if (tokens["text"] != textBefore)
        {
            adjustments.Add($"{mode} text {textBefore.Hex} became {tokens["text"].Hex}: the control outline (text mixed into the surface) must reach 3:1 on the surface and raised surface.");
        }

        return tokens;
    }

    private static void Say(List<string> reasons, string mode, string token, Dictionary<string, string> sources, Rgb? colour, string missing)
    {
        if (colour is { } c)
        {
            reasons.Add($"{Capitalise(mode)} {token} {c.Hex}: {(sources.TryGetValue(token, out string? source) ? source : "found in the CSS")}.");
        }
        else
        {
            reasons.Add($"{Capitalise(mode)} {token}: {missing}.");
        }
    }

    private static string Capitalise(string text)
    {
        return string.Concat(text[..1].ToUpperInvariant(), text.AsSpan(1));
    }

    /// <summary>
    /// Moves <paramref name="token"/>'s lightness, in 1% steps and no further than it has to, until it meets every contrast minimum
    /// against the named tokens; <paramref name="lighten"/> says which way. Records the change and why.
    /// </summary>
    private static void Fix(Dictionary<string, Rgb> tokens, string token, (string Against, double Minimum)[] constraints, bool lighten, string mode, List<string> adjustments)
    {
        bool Met(Rgb colour) => constraints.All(c => Rgb.Contrast(colour, tokens[c.Against]) >= c.Minimum);
        Rgb original = tokens[token];
        if (Met(original))
        {
            return;
        }

        // Brand is adjusted against brandText, which stays put: move brand away from it.
        Rgb moved = original;
        (double h, double s, double l) = original.ToHsl();
        for (int step = 0; step < 100 && !Met(moved); step++)
        {
            l = Math.Clamp(l + (lighten ? 0.01 : -0.01), 0, 1);
            moved = Rgb.FromHsl(h, s, l);
        }

        if (!Met(moved))
        {
            moved = lighten ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);
        }

        tokens[token] = moved;
        double worst = constraints.Min(c => c.Minimum);
        string against = string.Join(" and ", constraints.Select(c => c.Against == "brandText" ? "the text on it" : c.Against == "surfaceRaised" ? "the raised surface" : "the surface"));
        adjustments.Add($"{mode} {token} {original.Hex} became {moved.Hex}: it read at {constraints.Min(c => Rgb.Contrast(original, tokens[c.Against])).ToString("0.0", CultureInfo.InvariantCulture)}:1 against {against}, below the {worst.ToString("0.#", CultureInfo.InvariantCulture)}:1 WCAG AA asks for.");
    }

    // ---- the diagram ------------------------------------------------------------------------------------------------

    private static (string Base, Dictionary<string, string> Palette) Diagram(Dictionary<string, Rgb> primary, bool dark, List<string> reasons)
    {
        Rgb brand = primary["brand"];
        Rgb accent = primary["accent"];
        Rgb tint = IsChromatic(brand) ? brand : accent;
        if (!IsChromatic(tint))
        {
            reasons.Add("Diagrams start from the mono theme: the site has no strongly coloured brand, so no diagram colours were set.");
            return ("mono", []);
        }

        if (dark)
        {
            // The overpass diagram theme draws on dark navy with white export paper, so every stroke must read on both: its two red
            // palette entries are replaced, kept in the luminance bands (WCAG 3:1 on both backgrounds; 4.5:1 for white text on redDeep).
            Dictionary<string, string> palette = new(StringComparer.Ordinal)
            {
                ["red"] = Clamp(tint, 0.15, 0.28).Hex,
                ["redDeep"] = Clamp(tint, 0.10, 0.17).Hex,
            };
            reasons.Add($"Diagrams start from the overpass theme (dark canvas, white export paper) because the site is dark; its two reds became {palette["red"]} and {palette["redDeep"]}, shades of the brand colour that read on both.");
            return ("overpass", palette);
        }

        (double h, double s, _) = tint.ToHsl();
        Dictionary<string, string> light = new(StringComparer.Ordinal)
        {
            ["blue"] = Clamp(tint, 0.10, 0.28).Hex,
            ["blueDeep"] = Clamp(tint, 0.03, 0.10).Hex,
            ["blueLight"] = Rgb.FromHsl(h, Math.Min(s, 0.7), 0.91).Hex,
        };
        reasons.Add($"Diagrams start from the neutral theme (white canvas) because the site is light; its blues became {light["blue"]}, {light["blueDeep"]} and {light["blueLight"]}, shades of the brand colour that read on white.");
        return ("neutral", light);
    }

    /// <summary>The colour with its lightness moved the least that puts its luminance inside [min, max]; hue and saturation kept.</summary>
    private static Rgb Clamp(Rgb colour, double minLuminance, double maxLuminance)
    {
        (double h, double s, double l) = colour.ToHsl();
        Rgb current = colour;
        for (int step = 0; step < 100; step++)
        {
            double lum = current.Luminance;
            if (lum > maxLuminance)
            {
                l -= 0.01;
            }
            else if (lum < minLuminance)
            {
                l += 0.01;
            }
            else
            {
                return current;
            }

            current = Rgb.FromHsl(h, s, Math.Clamp(l, 0, 1));
        }

        return current;
    }
}
