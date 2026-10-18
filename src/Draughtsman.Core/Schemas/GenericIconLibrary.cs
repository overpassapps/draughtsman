using System.Reflection;
using System.Text.Json;

namespace Draughtsman.Core.Schemas;

/// <summary>One icon in the generic library: its shape name, display label, the architecture kind it names as its own
/// default shape (if any), the real kind the palette places it with, its drawing as SVG primitives on the library's
/// own viewBox grid, and whether it is a container boundary rather than a leaf icon.</summary>
public sealed record GenericIcon(string Name, string Label, string? Kind, string PaletteKind, IReadOnlyList<JsonElement> Elements, bool Boundary = false);

/// <summary>
/// The unbranded generic-technology icon library, loaded from the embedded <c>generic-icons.json</c>. It is the single
/// source both renderers read - the web canvas through the imported JSON and the MCP SVG renderer through this class -
/// so the icon artwork cannot drift between them. An icon is placed as the presentation shape
/// <c>custom:generic/&lt;name&gt;</c>; the library is nothing but data, and every shape name stays inside the
/// <c>custom:&lt;lib&gt;/&lt;name&gt;</c> branch the document schema already admits, so no schema change is needed.
/// </summary>
public static class GenericIconLibrary
{
    /// <summary>The shape-name prefix every generic icon carries: <c>custom:generic/&lt;name&gt;</c>.</summary>
    public const string Prefix = "custom:generic/";

    private static readonly Lazy<Library> s_library = new(Load);

    /// <summary>Every icon, in the library's own order (the palette's order).</summary>
    public static IReadOnlyList<GenericIcon> All => s_library.Value.Icons;

    /// <summary>The 24x24 coordinate grid every icon's primitives are authored on.</summary>
    public static double ViewBox => s_library.Value.ViewBox;

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

    /// <summary>True when <paramref name="shape"/> names an icon in this library (not merely the generic shape prefix).</summary>
    public static bool IsGenericShape(string shape)
    {
        return TryGet(shape, out _);
    }

    /// <summary>The icon a <c>custom:generic/&lt;name&gt;</c> shape names, or null when the shape is not one of ours.</summary>
    public static GenericIcon? For(string shape)
    {
        return TryGet(shape, out GenericIcon? icon) ? icon : null;
    }

    public static bool TryGet(string shape, out GenericIcon? icon)
    {
        icon = null;
        if (!shape.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string name = shape[Prefix.Length..];
        foreach (GenericIcon candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                icon = candidate;
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
        double viewBox = root.GetProperty("viewBox").GetDouble();
        var icons = new List<GenericIcon>();
        foreach (JsonElement icon in root.GetProperty("icons").EnumerateArray())
        {
            string name = icon.GetProperty("name").GetString()!;
            string label = icon.GetProperty("label").GetString()!;
            JsonElement kindElement = icon.GetProperty("kind");
            string? kind = kindElement.ValueKind == JsonValueKind.Null ? null : kindElement.GetString();
            string paletteKind = icon.GetProperty("paletteKind").GetString()!;
            bool boundary = icon.TryGetProperty("boundary", out JsonElement boundaryElement) && boundaryElement.ValueKind == JsonValueKind.True;
            List<JsonElement> elements = icon.GetProperty("elements").EnumerateArray().Select(e => e.Clone()).ToList();
            icons.Add(new GenericIcon(name, label, kind, paletteKind, elements, boundary));
        }

        return new Library(viewBox, icons);
    }

    private static Stream OpenResource()
    {
        const string name = "schemas/generic-icons.json";
        Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return stream ?? throw new FileNotFoundException($"Embedded icon library '{name}' is missing from the Draughtsman.Core assembly.");
    }

    private sealed record Library(double ViewBox, IReadOnlyList<GenericIcon> Icons);
}
