using System.Globalization;
using System.Reflection;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using YamlDotNet.Serialization;

namespace Draughtsman.Mcp.Rendering;

/// <summary>
/// A diagram theme (<c>themes/diagram-*.yaml</c>, embedded in this assembly) and the style resolution the canvas does over
/// it (web/src/theme/resolveStyle.ts): the kind's default, a <c>token:&lt;name&gt;</c> read from the palette, the document's
/// dotted-path <c>theme</c> overrides, and the element's own sparse style laid over that. The web build compiles the same YAML
/// into <c>web/src/generated/diagram-themes.ts</c>; this is the server's reader of the one file, so the two cannot hold
/// different colours. Every document type uses it (<see cref="SvgRenderer"/>.Render), so the server's SVG and the PDF printed
/// from it carry the same colours as the client's export.
/// <para>
/// The client lets each viewer pick a theme, so the server has no theme "the user chose": it uses the shipped default,
/// <see cref="DefaultName"/>, the one the editor opens with, and paints the <em>export</em> surface (paper background), as the
/// client's own PNG/SVG export does. Fonts are not resolved: the server draws <c>sans-serif</c>, as the scene's widths assume.
/// </para>
/// </summary>
internal sealed class DiagramTheme
{
    /// <summary>The theme the editor opens with (<c>defaultDiagramThemeName</c> in the generated tokens).</summary>
    public const string DefaultName = "overpass";

    private const string TokenPrefix = "token:";
    private const string LightText = "#F2F2F2";
    private const string DarkText = "#111111";
    private const double LightSurfaceLuminance = 0.179;

    private static readonly Lazy<DiagramTheme> Shipped = new(() => Load(DefaultName));

    private readonly Dictionary<string, object?> _tree;

    private DiagramTheme(Dictionary<string, object?> tree)
    {
        _tree = tree;
    }

    /// <summary>The shipped default theme, loaded once.</summary>
    public static DiagramTheme Default => Shipped.Value;

    /// <summary>The paper the export is painted on: <c>exportBackground</c>.</summary>
    public string ExportBackground => Scalar(_tree, "exportBackground") ?? "#FFFFFF";

    /// <summary>Loads a shipped theme by name (<c>overpass</c>, <c>neutral</c>, <c>mono</c>).</summary>
    public static DiagramTheme Load(string name)
    {
        string resource = $"Draughtsman.Mcp.Themes.diagram-{name}.yaml";
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new ArgumentException($"There is no shipped diagram theme named '{name}'.", nameof(name));
        using var reader = new StreamReader(stream);
        object? parsed = new DeserializerBuilder().Build().Deserialize(reader, typeof(object));
        return new DiagramTheme((Dictionary<string, object?>)Normalise(parsed)!);
    }

    /// <summary>
    /// A shipped theme with an organisation's override applied (<c>applyDiagramOverride</c> in web/src/theme/orgTheme.ts): a
    /// palette entry replaces that colour wherever the base's node and edge styles use it (the themes hold literal hex), and
    /// the export background, when set, wins over a palette swap that touched it.
    /// </summary>
    public static DiagramTheme For(DiagramThemeChoice? choice)
    {
        if (choice is null)
        {
            return Default;
        }

        DiagramTheme baseTheme = Load(Array.Exists(["overpass", "neutral", "mono"], name => name == choice.Base) ? choice.Base : DefaultName);
        var tree = (Dictionary<string, object?>)Clone(baseTheme._tree)!;
        Dictionary<string, object?> palette = Entry(tree, "palette") ?? new Dictionary<string, object?>();
        var swaps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string replacement) in choice.Palette)
        {
            if (Scalar(palette, name) is { } current && IsHex(replacement))
            {
                swaps[current] = replacement;
                palette[name] = replacement;
            }
        }

        string Swap(string colour) => swaps.TryGetValue(colour, out string? replacement) ? replacement : colour;
        foreach (string group in new[] { "nodes", "edges" })
        {
            foreach (Dictionary<string, object?> style in (Entry(tree, group) ?? []).Values.OfType<Dictionary<string, object?>>())
            {
                foreach (string field in new[] { "fill", "stroke", "textColor" })
                {
                    if (Scalar(style, field) is { } value)
                    {
                        style[field] = Swap(value);
                    }
                }
            }
        }

        foreach (string field in new[] { "canvasBackground", "exportBackground", "canvasText", "exportText" })
        {
            if (Scalar(tree, field) is { } value)
            {
                tree[field] = Swap(value);
            }
        }

        if (choice.ExportBackground is { } paper && IsHex(paper))
        {
            tree["exportBackground"] = paper;
        }

        return new DiagramTheme(tree);
    }

    private static bool IsHex(string value) => value.Length is 4 or 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit);

    /// <summary>
    /// The theme with a document's dotted-path token overrides applied (<c>applyDocumentOverrides</c>): palette entries first
    /// (colour literals only), then every other path, a value naming a palette entry through <c>token:</c>. A path that addresses
    /// no token (an unknown step, a whole kind's style, the theme's name) is ignored.
    /// </summary>
    public DiagramTheme WithOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return this;
        }

        var tree = (Dictionary<string, object?>)Clone(_tree)!;
        foreach ((string path, string value) in overrides.Where(entry => entry.Key.StartsWith("palette.", StringComparison.Ordinal) && !IsToken(entry.Value)))
        {
            SetPath(tree, path, value);
        }

        var theme = new DiagramTheme(tree);
        foreach ((string path, string value) in overrides.Where(entry => !entry.Key.StartsWith("palette.", StringComparison.Ordinal)))
        {
            if (theme.Paint(value) is { } colour)
            {
                SetPath(tree, path, colour);
            }
        }

        return theme;
    }

    /// <summary>A node's fully resolved style: the kind's default (else <c>default</c>), then the node's own sparse style over it.</summary>
    public NodeStyle Node(string kind, NodeStyle? own)
    {
        Dictionary<string, object?> group = Group("nodes");
        Dictionary<string, object?> baseStyle = Entry(group, kind) ?? Entry(group, "default") ?? new Dictionary<string, object?>();
        string? fill = Paint(own?.Fill) ?? Scalar(baseStyle, "fill");
        string? baseText = Scalar(baseStyle, "textColor");
        return new NodeStyle
        {
            Fill = fill,
            Stroke = Paint(own?.Stroke) ?? Scalar(baseStyle, "stroke"),
            StrokeWidth = own?.StrokeWidth ?? Number(baseStyle, "strokeWidth"),
            // A text colour the node names is kept; otherwise the theme's, unless the node's own fill makes it unreadable (QA-06).
            TextColor = Paint(own?.TextColor) ?? (own is not null && baseText is not null && fill is not null ? ReadableText.On(baseText, fill) : baseText),
            Font = own?.Font,
            Dash = own?.Dash ?? DashOf(Scalar(baseStyle, "dash")),
            FontSize = own?.FontSize ?? Number(baseStyle, "fontSize"),
            Bold = own?.Bold,
        };
    }

    /// <summary>
    /// An edge's fully resolved style, field by field: the edge's own sparse style, else the theme's entry for the kind, else
    /// <paramref name="kindDefault"/> (the kind's <c>defaultStyle</c> in its vocabulary, for a theme that does not name the kind),
    /// else the theme's <c>default</c> edge.
    /// </summary>
    public EdgeStyle Edge(string kind, EdgeStyle? own, EdgeStyle? kindDefault = null)
    {
        Dictionary<string, object?> group = Group("edges");
        Dictionary<string, object?> named = Entry(group, kind) ?? new Dictionary<string, object?>();
        Dictionary<string, object?> fallback = Entry(group, "default") ?? new Dictionary<string, object?>();
        return new EdgeStyle
        {
            Stroke = Paint(own?.Stroke) ?? Scalar(named, "stroke") ?? Paint(kindDefault?.Stroke) ?? Scalar(fallback, "stroke"),
            StrokeWidth = own?.StrokeWidth ?? Number(named, "strokeWidth") ?? kindDefault?.StrokeWidth ?? Number(fallback, "strokeWidth"),
            Dash = own?.Dash ?? DashOf(Scalar(named, "dash")) ?? kindDefault?.Dash ?? DashOf(Scalar(fallback, "dash")),
            ArrowStart = own?.ArrowStart ?? ArrowOf(Scalar(named, "arrowStart")) ?? kindDefault?.ArrowStart ?? ArrowOf(Scalar(fallback, "arrowStart")),
            ArrowEnd = own?.ArrowEnd ?? ArrowOf(Scalar(named, "arrowEnd")) ?? kindDefault?.ArrowEnd ?? ArrowOf(Scalar(fallback, "arrowEnd")),
        };
    }

    /// <summary>
    /// The colour of text drawn straight on the export paper (<c>surfaceTextColor</c>): the theme's <c>exportText</c>, else
    /// dark on a light paper and light on a dark one. Null when the paper is not a hex colour.
    /// </summary>
    public string? SurfaceText()
    {
        if (Scalar(_tree, "exportText") is { } explicit_)
        {
            return explicit_;
        }

        return Luminance(ExportBackground) is { } luminance ? luminance > LightSurfaceLuminance ? DarkText : LightText : null;
    }

    /// <summary>The kind's default text colour, which a document override may have changed (used to decide whether the surface owns a label's colour).</summary>
    public string? KindTextColour(string kind)
    {
        Dictionary<string, object?> group = Group("nodes");
        return Scalar(Entry(group, kind) ?? Entry(group, "default") ?? new Dictionary<string, object?>(), "textColor");
    }

    private static bool IsToken(string value) => value.StartsWith(TokenPrefix, StringComparison.Ordinal);

    /// <summary>A colour a document stores: a literal as written, or <c>token:&lt;name&gt;</c> read from the palette (null when it names none).</summary>
    private string? Paint(string? colour)
    {
        if (colour is null || !colour.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return colour;
        }

        return Scalar(Group("palette"), colour[TokenPrefix.Length..]);
    }

    private Dictionary<string, object?> Group(string name) => Entry(_tree, name) ?? new Dictionary<string, object?>();

    private static Dictionary<string, object?>? Entry(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out object? value) ? value as Dictionary<string, object?> : null;

    private static string? Scalar(Dictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out object? value) && value is string text ? text : null;

    private static double? Number(Dictionary<string, object?> map, string key) =>
        Scalar(map, key) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : null;

    private static Dash? DashOf(string? name) => name switch
    {
        "solid" => Dash.Solid,
        "dashed" => Dash.Dashed,
        "dotted" => Dash.Dotted,
        _ => null,
    };

    private static ArrowHead? ArrowOf(string? name) =>
        name is not null && Enum.TryParse(name, ignoreCase: true, out ArrowHead head) ? head : null;

    /// <summary>YamlDotNet's untyped tree as string-keyed dictionaries of strings (every scalar stays text; <see cref="Number"/> reads the numbers).</summary>
    private static object? Normalise(object? node) => node switch
    {
        IDictionary<object, object> map => map.ToDictionary(pair => Convert.ToString(pair.Key, CultureInfo.InvariantCulture)!, pair => Normalise(pair.Value), StringComparer.Ordinal),
        null => null,
        _ => Convert.ToString(node, CultureInfo.InvariantCulture),
    };

    private static object? Clone(object? node) => node switch
    {
        Dictionary<string, object?> map => map.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal),
        _ => node,
    };

    /// <summary>
    /// Sets one token by dotted path. A step the theme does not carry addresses nothing, and so does a position holding a whole
    /// style (a kind under <c>nodes</c>/<c>edges</c>) or one of the theme's identity fields; a token the theme omits (a new
    /// palette entry, <c>canvasText</c>) may be added.
    /// </summary>
    private static void SetPath(Dictionary<string, object?> tree, string path, string value)
    {
        string[] segments = path.Split('.');
        Dictionary<string, object?> container = tree;
        string? containerName = null;
        foreach (string segment in segments[..^1])
        {
            if (Entry(container, segment) is not { } next)
            {
                return;
            }

            container = next;
            containerName = segment;
        }

        string key = segments[^1];
        bool wholeStyle = containerName is null ? key is "name" or "description" : containerName is "nodes" or "edges";
        if (wholeStyle || (container.TryGetValue(key, out object? existing) && existing is Dictionary<string, object?>))
        {
            return;
        }

        container[key] = value;
    }

    private static double? Luminance(string colour)
    {
        string hex = colour.Trim();
        if (hex.Length is not (4 or 5 or 7 or 9) || hex[0] != '#' || !hex[1..].All(Uri.IsHexDigit))
        {
            return null;
        }

        hex = hex[1..];
        if (hex.Length <= 4)
        {
            hex = string.Concat(hex[..3].Select(c => $"{c}{c}"));
        }

        double Channel(int at)
        {
            double c = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(0)) + (0.7152 * Channel(2)) + (0.0722 * Channel(4));
    }
}
