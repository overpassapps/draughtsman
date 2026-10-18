using System.Reflection;
using System.Text.Json;

namespace Draughtsman.Core.Schemas;

/// <summary>One org-chart box: its shape name, display label, the node kind it is the default shape for, the label a
/// palette glyph places it with (name and title on two lines), the outline style, and any interior decoration as
/// fractional primitives (coordinates are fractions of the node box).</summary>
public sealed record OrgShape(string Name, string Label, string Kind, string DefaultLabel, string Outline, IReadOnlyList<JsonElement> Elements);

/// <summary>
/// The org-chart box library, loaded from the embedded <c>org-shapes.json</c>. It is the single source both renderers
/// read - the web canvas through the imported JSON and the MCP SVG renderer through this class - so the box chrome
/// cannot drift between them. A box is placed as the presentation shape <c>custom:org/&lt;name&gt;</c>; the library is
/// nothing but data, and every shape name stays inside the <c>custom:&lt;lib&gt;/&lt;name&gt;</c> branch the document
/// schema already admits, so no schema change is needed.
/// </summary>
public static class OrgShapeLibrary
{
    /// <summary>The shape-name prefix every org box carries: <c>custom:org/&lt;name&gt;</c>.</summary>
    public const string Prefix = "custom:org/";

    private static readonly Lazy<Library> s_library = new(Load);

    /// <summary>Every box, in the library's own order (the palette's order).</summary>
    public static IReadOnlyList<OrgShape> All => s_library.Value.Shapes;

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

    /// <summary>True when <paramref name="shape"/> names a box in this library (not merely the org shape prefix).</summary>
    public static bool IsOrgShape(string shape)
    {
        return TryGet(shape, out _);
    }

    /// <summary>The box a <c>custom:org/&lt;name&gt;</c> shape names, or null when the shape is not one of ours.</summary>
    public static OrgShape? For(string shape)
    {
        return TryGet(shape, out OrgShape? box) ? box : null;
    }

    public static bool TryGet(string shape, out OrgShape? box)
    {
        box = null;
        if (!shape.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string name = shape[Prefix.Length..];
        foreach (OrgShape candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                box = candidate;
                return true;
            }
        }

        return false;
    }

    private static Library Load()
    {
        using Stream stream = OpenResource();
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        var shapes = new List<OrgShape>();
        foreach (JsonElement shape in root.GetProperty("shapes").EnumerateArray())
        {
            string name = shape.GetProperty("name").GetString()!;
            string label = shape.GetProperty("label").GetString()!;
            string kind = shape.GetProperty("kind").GetString()!;
            string defaultLabel = shape.GetProperty("defaultLabel").GetString()!;
            string outline = shape.GetProperty("outline").GetString()!;
            List<JsonElement> elements = shape.GetProperty("elements").EnumerateArray().Select(e => e.Clone()).ToList();
            shapes.Add(new OrgShape(name, label, kind, defaultLabel, outline, elements));
        }

        return new Library(shapes);
    }

    private static Stream OpenResource()
    {
        const string name = "schemas/org-shapes.json";
        Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return stream ?? throw new FileNotFoundException($"Embedded org shape library '{name}' is missing from the Draughtsman.Core assembly.");
    }

    private sealed record Library(IReadOnlyList<OrgShape> Shapes);
}
