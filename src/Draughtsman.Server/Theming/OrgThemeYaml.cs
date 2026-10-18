using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Draughtsman.Server.Theming;

/// <summary>
/// The one file format for an organisation theme, shared by the admin page's import and export and by the files
/// mechanism (<c>themes/chrome.yaml</c>). It is the chrome theme file's shape - <c>name</c>, <c>productName</c>,
/// <c>mode</c>, <c>colors</c> (the set for that mode, dark when the mode is system), a <c>light:</c> or <c>dark:</c>
/// companion block, <c>radius</c> and <c>font</c> - plus a <c>diagram:</c> block. <see cref="ChromeThemeLoader"/>
/// ignores keys it does not know, so an exported theme also loads as a chrome theme file through
/// <c>theme.chrome</c>; the same YAML imported into the page adds the diagram block back.
/// <para>
/// <c>productName</c> and the logo names are not part of an organisation theme (they are branding settings), so the
/// file carries the product name only because the chrome file format requires one, and an import ignores it.
/// </para>
/// </summary>
internal static class OrgThemeYaml
{
    /// <summary>The largest theme file the page will read. A real theme is about 1.5 KiB.</summary>
    public const int MaxBytes = 64 * 1024;

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static string Write(OrgTheme theme, string productName)
    {
        bool lightPrimary = theme.Mode == "light";
        IReadOnlyDictionary<string, string> primary = lightPrimary ? theme.Light : theme.Dark;
        IReadOnlyDictionary<string, string> companion = lightPrimary ? theme.Dark : theme.Light;
        var file = new ThemeFile
        {
            Name = theme.Name,
            ProductName = productName,
            Mode = theme.Mode,
            Colors = Ordered(primary),
            Light = lightPrimary ? null : new CompanionFile { Colors = Ordered(companion) },
            Dark = lightPrimary ? new CompanionFile { Colors = Ordered(companion) } : null,
            Radius = theme.Radius,
            Font = new Dictionary<string, string> { ["ui"] = theme.FontUi, ["mono"] = theme.FontMono },
            Diagram = new DiagramFile
            {
                Base = theme.Diagram.Base,
                Palette = theme.Diagram.Palette.Count == 0 ? null : new Dictionary<string, string>(theme.Diagram.Palette),
                CanvasBackground = theme.Diagram.CanvasBackground,
                ExportBackground = theme.Diagram.ExportBackground,
                GridColor = theme.Diagram.GridColor,
                Fonts = theme.Diagram.FontLabel is null && theme.Diagram.FontMono is null
                    ? null
                    : new DiagramFontsFile { Label = theme.Diagram.FontLabel, Mono = theme.Diagram.FontMono },
            },
        };
        return Serializer.Serialize(file);
    }

    /// <summary>
    /// Reads a theme file. Returns the theme (id left empty for the caller to assign, not yet validated) or throws
    /// <see cref="FormatException"/> naming what is wrong with the file as a file. A companion set that is missing, or
    /// that omits a token, takes the primary set's value, as the token compiler does.
    /// </summary>
    public static OrgTheme Read(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        if (System.Text.Encoding.UTF8.GetByteCount(yaml) > MaxBytes)
        {
            throw new FormatException($"The theme file is larger than {MaxBytes / 1024} KiB.");
        }

        ThemeFile? file;
        try
        {
            file = Deserializer.Deserialize<ThemeFile>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FormatException("The file could not be read as YAML: " + ex.Message, ex);
        }

        if (file is null)
        {
            throw new FormatException("The file is empty; expected a theme map.");
        }

        if (file.Colors is null || file.Colors.Count == 0)
        {
            throw new FormatException("The file has no \"colors\" map.");
        }

        bool lightPrimary = file.Mode == "light";
        Dictionary<string, string> primary = new(file.Colors);
        Dictionary<string, string> companion = new(primary);
        Dictionary<string, string>? given = (lightPrimary ? file.Dark : file.Light)?.Colors;
        if (given is not null)
        {
            foreach ((string key, string value) in given)
            {
                companion[key] = value;
            }
        }

        string? fontUi = null;
        string? fontMono = null;
        file.Font?.TryGetValue("ui", out fontUi);
        file.Font?.TryGetValue("mono", out fontMono);

        DiagramFile? diagram = file.Diagram;
        OrgDiagramTheme diagramTheme = diagram is null
            ? OrgDiagramTheme.Default
            : new OrgDiagramTheme(
                diagram.Base ?? OrgDiagramTheme.Default.Base,
                diagram.Palette is null ? new Dictionary<string, string>() : new Dictionary<string, string>(diagram.Palette),
                diagram.CanvasBackground,
                diagram.ExportBackground,
                diagram.GridColor,
                diagram.Fonts?.Label,
                diagram.Fonts?.Mono);

        return new OrgTheme(
            string.Empty,
            file.Name ?? string.Empty,
            file.Mode ?? "dark",
            lightPrimary ? companion : primary,
            lightPrimary ? primary : companion,
            file.Radius ?? 6,
            fontUi ?? string.Empty,
            fontMono ?? string.Empty,
            diagramTheme,
            DateTimeOffset.UtcNow);
    }

    /// <summary>The primary colour set in the chrome token order, so an exported file reads like <c>themes/chrome.yaml</c>.</summary>
    private static Dictionary<string, string> Ordered(IReadOnlyDictionary<string, string> colours)
    {
        Dictionary<string, string> ordered = [];
        foreach (string key in OrgTheme.ChromeColourKeys)
        {
            if (colours.TryGetValue(key, out string? value))
            {
                ordered[key] = value;
            }
        }

        return ordered;
    }

    private sealed class ThemeFile
    {
        public string? Name { get; set; }

        public string? ProductName { get; set; }

        public string? Mode { get; set; }

        public Dictionary<string, string>? Colors { get; set; }

        public CompanionFile? Light { get; set; }

        public CompanionFile? Dark { get; set; }

        public double? Radius { get; set; }

        public Dictionary<string, string>? Font { get; set; }

        public DiagramFile? Diagram { get; set; }
    }

    private sealed class CompanionFile
    {
        public Dictionary<string, string>? Colors { get; set; }
    }

    private sealed class DiagramFile
    {
        public string? Base { get; set; }

        public Dictionary<string, string>? Palette { get; set; }

        public string? CanvasBackground { get; set; }

        public string? ExportBackground { get; set; }

        public string? GridColor { get; set; }

        public DiagramFontsFile? Fonts { get; set; }
    }

    private sealed class DiagramFontsFile
    {
        public string? Label { get; set; }

        public string? Mono { get; set; }
    }
}
