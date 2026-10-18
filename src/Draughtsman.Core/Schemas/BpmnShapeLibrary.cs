using System.Reflection;
using System.Text.Json;

namespace Draughtsman.Core.Schemas;

/// <summary>One BPMN shape: its shape name, display label, the flowchart kind it is the default shape for, its family
/// (event, activity, gateway, data or annotation), and the drawing data both renderers read. <paramref name="Position"/>
/// (start, intermediate or end) is set for events only; <paramref name="Border"/> is single, double or thick;
/// <paramref name="Glyph"/> names an entry of <see cref="BpmnShapeLibrary.Glyphs"/> and <paramref name="Placement"/>
/// (centre, corner or bottom) says where it sits; <paramref name="Throwing"/> draws the glyph filled (a throw event or
/// a send task); <paramref name="Width"/> and <paramref name="Height"/> are the size the palette places it at.</summary>
public sealed record BpmnShape(string Name, string Label, string Kind, string Family, string? Position, string Border, string? Glyph, string? Placement, bool Throwing, double Width, double Height);

/// <summary>The numbers the two renderers share: label placement, border weight and glyph and marker sizes.</summary>
public sealed record BpmnLayout(
    double LabelGap, double LabelBand, double LabelMinWidth,
    double EventRingInset, double ThickBorder, double EventGlyph, double GatewayGlyph,
    double MarkerSize, double MarkerInset, double CornerRadius,
    double DataFold, double DataFoldMax, double AnnotationBracket, double AnnotationBracketMax);

/// <summary>
/// The BPMN 2.0 shape library, loaded from the embedded <c>bpmn-shapes.json</c>. Like the generic icon library it is
/// the single source both renderers read (the web canvas through the imported JSON, the MCP SVG renderer through this
/// class), so the notation cannot drift between them. A shape is placed as the presentation shape
/// <c>custom:bpmn/&lt;name&gt;</c>, a branch the document schema already admits, and names its own flowchart kind
/// (<c>bpmn-&lt;name&gt;</c>) whose default shape it is. Pools and lanes are not part of it: they are
/// <see cref="SwimlaneShapes"/>.
/// </summary>
public static class BpmnShapeLibrary
{
    /// <summary>The shape-name prefix every BPMN shape carries: <c>custom:bpmn/&lt;name&gt;</c>.</summary>
    public const string Prefix = "custom:bpmn/";

    /// <summary>The kind-name prefix every BPMN kind carries: <c>bpmn-&lt;name&gt;</c>.</summary>
    public const string KindPrefix = "bpmn-";

    public const string EventFamily = "event";
    public const string ActivityFamily = "activity";
    public const string GatewayFamily = "gateway";
    public const string DataFamily = "data";
    public const string AnnotationFamily = "annotation";

    public const string StartPosition = "start";
    public const string IntermediatePosition = "intermediate";
    public const string EndPosition = "end";

    private static readonly Lazy<Library> s_library = new(Load);

    /// <summary>Every shape, in the library's own order (the palette's order).</summary>
    public static IReadOnlyList<BpmnShape> All => s_library.Value.Shapes;

    /// <summary>The 24x24 grid every glyph is authored on.</summary>
    public static double ViewBox => s_library.Value.ViewBox;

    /// <summary>The shared layout numbers.</summary>
    public static BpmnLayout Layout => s_library.Value.Layout;

    /// <summary>The glyph line art by name, as icon-format elements on the <see cref="ViewBox"/> grid.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<JsonElement>> Glyphs => s_library.Value.Glyphs;

    /// <summary>The raw embedded JSON, for the MCP get_schema tool (the file is the contract, never restated by hand).</summary>
    public static string RawJson
    {
        get
        {
            using Stream stream = OpenResource();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>The full shape name for a library entry: <c>custom:bpmn/&lt;name&gt;</c>.</summary>
    public static string ShapeName(BpmnShape shape) => Prefix + shape.Name;

    /// <summary>True when <paramref name="shape"/> names a shape in this library (not merely the BPMN prefix).</summary>
    public static bool IsBpmnShape(string shape) => TryGet(shape, out _);

    /// <summary>The shape a <c>custom:bpmn/&lt;name&gt;</c> name refers to, or false when it is not one of ours.</summary>
    public static bool TryGet(string shape, out BpmnShape? bpmn)
    {
        bpmn = null;
        if (!shape.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string name = shape[Prefix.Length..];
        foreach (BpmnShape candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                bpmn = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>The library shape whose kind is <paramref name="kind"/>, or null when the kind is not a BPMN kind.</summary>
    public static BpmnShape? ForKind(string kind)
    {
        foreach (BpmnShape candidate in All)
        {
            if (string.Equals(candidate.Kind, kind, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>True when the kind is a BPMN kind of the given family.</summary>
    public static bool IsKindOfFamily(string kind, string family) => ForKind(kind)?.Family == family;

    /// <summary>True when the kind is a BPMN start event.</summary>
    public static bool IsStartEventKind(string kind) => ForKind(kind) is { Family: EventFamily, Position: StartPosition };

    /// <summary>True when the kind is a BPMN intermediate event.</summary>
    public static bool IsIntermediateEventKind(string kind) => ForKind(kind) is { Family: EventFamily, Position: IntermediatePosition };

    /// <summary>True when a shape draws its label below the outline rather than inside it (events, gateways, data).</summary>
    public static bool LabelOutside(BpmnShape shape) => shape.Family is EventFamily or GatewayFamily or DataFamily;

    private static Library Load()
    {
        using Stream stream = OpenResource();
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;

        JsonElement layout = root.GetProperty("layout");
        double L(string name) => layout.GetProperty(name).GetDouble();
        var parsedLayout = new BpmnLayout(
            L("labelGap"), L("labelBand"), L("labelMinWidth"),
            L("eventRingInset"), L("thickBorder"), L("eventGlyph"), L("gatewayGlyph"),
            L("markerSize"), L("markerInset"), L("cornerRadius"),
            L("dataFold"), L("dataFoldMax"), L("annotationBracket"), L("annotationBracketMax"));

        var glyphs = new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.Ordinal);
        foreach (JsonProperty glyph in root.GetProperty("glyphs").EnumerateObject())
        {
            glyphs[glyph.Name] = glyph.Value.EnumerateArray().Select(e => e.Clone()).ToList();
        }

        var shapes = new List<BpmnShape>();
        foreach (JsonElement shape in root.GetProperty("shapes").EnumerateArray())
        {
            string? Optional(string name) => shape.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
            JsonElement size = shape.GetProperty("size");
            shapes.Add(new BpmnShape(
                shape.GetProperty("name").GetString()!,
                shape.GetProperty("label").GetString()!,
                shape.GetProperty("kind").GetString()!,
                shape.GetProperty("family").GetString()!,
                Optional("position"),
                shape.GetProperty("border").GetString()!,
                Optional("glyph"),
                Optional("placement"),
                shape.TryGetProperty("throwing", out JsonElement throwing) && throwing.GetBoolean(),
                size.GetProperty("w").GetDouble(),
                size.GetProperty("h").GetDouble()));
        }

        return new Library(root.GetProperty("viewBox").GetDouble(), parsedLayout, glyphs, shapes);
    }

    private static Stream OpenResource()
    {
        const string name = "schemas/bpmn-shapes.json";
        Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return stream ?? throw new FileNotFoundException($"Embedded BPMN shape library '{name}' is missing from the Draughtsman.Core assembly.");
    }

    private sealed record Library(double ViewBox, BpmnLayout Layout, IReadOnlyDictionary<string, IReadOnlyList<JsonElement>> Glyphs, IReadOnlyList<BpmnShape> Shapes);
}
