using System.Reflection;
using System.Text.Json;

namespace Draughtsman.Core.Schemas;

/// <summary>The layout numbers every UML shape reader shares (uml-shapes.json <c>metrics</c>).</summary>
public sealed record UmlMetrics(double FontSize, double RowHeight, double PadX, double PadY, double CharWidth, double GlyphColumn, double MinWidth, double SymbolBand, double SymbolRadius);

/// <summary>One UML shape: <c>compartments</c> form for a class or interface box, <c>symbol</c> form for lollipop and socket.</summary>
public sealed record UmlShape(string Name, string Label, string Kind, string Form, string DefaultLabel, string Stereotype, double? DefaultWidth, double? DefaultHeight, JsonElement? DefaultProps)
{
    public bool IsCompartments => Form == "compartments";
}

/// <summary>One class member as drawn: the visibility glyph (empty when the line has none) and the text after it.</summary>
public sealed record UmlMember(string Visibility, string Text);

/// <summary>A compartment as laid out: its top edge inside the box, its height, and the members it draws.</summary>
public sealed record UmlCompartment(double Top, double Height, IReadOnlyList<UmlMember> Members, bool HasGlyph);

/// <summary>
/// The compartments of a class or interface box, derived from the node's content alone. The name band holds an
/// optional stereotype row above the name row; each member compartment holds one row per member (an empty one keeps a
/// single row). A box taller than <see cref="NaturalHeight"/> gives the extra to the operations compartment, whose
/// bottom edge is the box's own. Mirrors web/src/model/umlShapes.ts.
/// </summary>
public sealed record UmlClassLayout(string? Stereotype, double HeaderHeight, UmlCompartment Attributes, UmlCompartment Operations, double NaturalHeight, double NaturalWidth);

/// <summary>
/// The UML class shape library, loaded from the embedded <c>uml-shapes.json</c>. Like the org and generic-icon
/// libraries it is nothing but data both renderers read, and every shape name stays inside the
/// <c>custom:&lt;lib&gt;/&lt;name&gt;</c> branch the document schema already admits, so no schema change is needed.
/// The class and interface boxes keep their members in node <c>props</c> (<c>attributes</c>, <c>operations</c>,
/// <c>stereotype</c>), so the DSL prints them as ordinary props and a document written before this library existed is
/// unaffected. docs/uml-class-compartments.md describes the compartment model.
/// </summary>
public static class UmlShapeLibrary
{
    /// <summary>The shape-name prefix every UML shape carries: <c>custom:uml/&lt;name&gt;</c>.</summary>
    public const string Prefix = "custom:uml/";

    public const string AttributesProp = "attributes";
    public const string OperationsProp = "operations";
    public const string StereotypeProp = "stereotype";

    private const string VisibilityGlyphs = "+-#~";

    private static readonly Lazy<Library> s_library = new(Load);

    public static UmlMetrics Metrics => s_library.Value.Metrics;

    /// <summary>Every shape, in the library's own order (the palette's order).</summary>
    public static IReadOnlyList<UmlShape> All => s_library.Value.Shapes;

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

    public static bool IsUmlShape(string shape)
    {
        return TryGet(shape, out _);
    }

    public static bool TryGet(string shape, out UmlShape? uml)
    {
        uml = null;
        if (!shape.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string name = shape[Prefix.Length..];
        foreach (UmlShape candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                uml = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Splits a member line into its visibility glyph and text: "+ save()" is ("+", "save()"), "id: int" is ("", "id: int").</summary>
    public static UmlMember ParseMember(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length > 0 && VisibilityGlyphs.Contains(trimmed[0], StringComparison.Ordinal))
        {
            return new UmlMember(trimmed[..1], trimmed[1..].TrimStart());
        }

        return new UmlMember("", trimmed);
    }

    /// <summary>The member lines of a props list (<c>attributes</c> or <c>operations</c>): the non-blank strings, in order; any other JSON value in the list is ignored.</summary>
    public static IReadOnlyList<string> MemberLines(IReadOnlyDictionary<string, JsonElement>? props, string key)
    {
        if (props is null || !props.TryGetValue(key, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var lines = new List<string>();
        foreach (JsonElement item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text && text.Trim().Length > 0)
            {
                lines.Add(text);
            }
        }

        return lines;
    }

    /// <summary>The stereotype a box shows above its name: the <c>stereotype</c> prop when set, else the shape's own; null for none.</summary>
    public static string? StereotypeOf(UmlShape shape, IReadOnlyDictionary<string, JsonElement>? props)
    {
        if (props is not null && props.TryGetValue(StereotypeProp, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            string custom = value.GetString()!.Trim();
            return custom.Length == 0 ? null : custom;
        }

        return shape.Stereotype.Length == 0 ? null : shape.Stereotype;
    }

    /// <summary>The compartments of a class or interface box of height <paramref name="height"/>.</summary>
    public static UmlClassLayout LayoutClass(UmlShape shape, string label, IReadOnlyDictionary<string, JsonElement>? props, double height)
    {
        UmlMetrics m = Metrics;
        string? stereotype = StereotypeOf(shape, props);
        double header = (m.PadY * 2) + (m.RowHeight * (stereotype is null ? 1 : 2));
        UmlMember[] attributes = MemberLines(props, AttributesProp).Select(ParseMember).ToArray();
        UmlMember[] operations = MemberLines(props, OperationsProp).Select(ParseMember).ToArray();
        double attributesHeight = CompartmentHeight(attributes.Length);
        double operationsNatural = CompartmentHeight(operations.Length);
        double natural = header + attributesHeight + operationsNatural;
        double operationsHeight = Math.Max(operationsNatural, height - header - attributesHeight);

        double widest = (label.Length * m.CharWidth * 1.1) + (m.PadX * 2);
        if (stereotype is not null)
        {
            widest = Math.Max(widest, ((stereotype.Length + 4) * m.CharWidth) + (m.PadX * 2));
        }

        foreach (UmlMember[] members in new[] { attributes, operations })
        {
            bool glyph = members.Any(member => member.Visibility.Length > 0);
            foreach (UmlMember member in members)
            {
                widest = Math.Max(widest, (m.PadX * 2) + (glyph ? m.GlyphColumn : 0) + (member.Text.Length * m.CharWidth));
            }
        }

        return new UmlClassLayout(
            stereotype,
            header,
            new UmlCompartment(header, attributesHeight, attributes, attributes.Any(member => member.Visibility.Length > 0)),
            new UmlCompartment(header + attributesHeight, operationsHeight, operations, operations.Any(member => member.Visibility.Length > 0)),
            natural,
            Math.Max(m.MinWidth, Math.Ceiling(widest)));
    }

    private static double CompartmentHeight(int members)
    {
        UmlMetrics m = Metrics;
        return (m.PadY * 2) + (m.RowHeight * Math.Max(1, members));
    }

    private static Library Load()
    {
        using Stream stream = OpenResource();
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        JsonElement metrics = root.GetProperty("metrics");
        double Metric(string name) => metrics.GetProperty(name).GetDouble();
        var parsed = new UmlMetrics(Metric("fontSize"), Metric("rowHeight"), Metric("padX"), Metric("padY"), Metric("charWidth"), Metric("glyphColumn"), Metric("minWidth"), Metric("symbolBand"), Metric("symbolRadius"));
        var shapes = new List<UmlShape>();
        foreach (JsonElement shape in root.GetProperty("shapes").EnumerateArray())
        {
            double? width = null;
            double? height = null;
            if (shape.TryGetProperty("defaultSize", out JsonElement size))
            {
                width = size.GetProperty("w").GetDouble();
                height = size.GetProperty("h").GetDouble();
            }

            JsonElement? defaultProps = shape.TryGetProperty("defaultProps", out JsonElement props) ? props.Clone() : null;
            shapes.Add(new UmlShape(
                shape.GetProperty("name").GetString()!,
                shape.GetProperty("label").GetString()!,
                shape.GetProperty("kind").GetString()!,
                shape.GetProperty("form").GetString()!,
                shape.GetProperty("defaultLabel").GetString()!,
                shape.GetProperty("stereotype").GetString()!,
                width,
                height,
                defaultProps));
        }

        return new Library(parsed, shapes);
    }

    private static Stream OpenResource()
    {
        const string name = "schemas/uml-shapes.json";
        Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return stream ?? throw new FileNotFoundException($"Embedded UML shape library '{name}' is missing from the Draughtsman.Core assembly.");
    }

    private sealed record Library(UmlMetrics Metrics, IReadOnlyList<UmlShape> Shapes);
}
