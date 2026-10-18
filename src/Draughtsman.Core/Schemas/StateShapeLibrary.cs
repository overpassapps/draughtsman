using System.Reflection;
using System.Text.Json;

namespace Draughtsman.Core.Schemas;

/// <summary>One shape in the state library: its shape name, display label, the flowchart kind it is, and the kind the
/// palette places it with (always the same as <see cref="Kind"/> here, since every state shape carries its own kind).</summary>
public sealed record StateShape(string Name, string Label, string Kind, string PaletteKind);

/// <summary>
/// The UML state-machine shape library, loaded from the embedded <c>state-shapes.json</c>. Each shape is placed as the
/// presentation shape <c>custom:state/&lt;name&gt;</c>, a branch the document schema already admits, so the library needs
/// no schema change. The drawing itself is native in both renderers beside every other shape; this file is the shared
/// contract for the shape set, the palette and the kinds they name, and the sweep test on each side holds the renderers
/// to it. Every shape names its own flowchart kind, whose default shape is the shape, so the DSL printer elides it and
/// <c>get_schema</c> teaches an agent when to use it.
/// </summary>
public static class StateShapeLibrary
{
    /// <summary>The shape-name prefix every state shape carries: <c>custom:state/&lt;name&gt;</c>.</summary>
    public const string Prefix = "custom:state/";

    private static readonly Lazy<Library> s_library = new(Load);

    /// <summary>Every shape, in the library's own order (the palette's order).</summary>
    public static IReadOnlyList<StateShape> All => s_library.Value.Shapes;

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

    /// <summary>The full shape name for a library entry: <c>custom:state/&lt;name&gt;</c>.</summary>
    public static string ShapeName(StateShape shape)
    {
        return Prefix + shape.Name;
    }

    /// <summary>True when <paramref name="shape"/> names a shape in this library (not merely the state prefix).</summary>
    public static bool IsStateShape(string shape)
    {
        return TryGet(shape, out _);
    }

    /// <summary>The shape a <c>custom:state/&lt;name&gt;</c> name refers to, or null when it is not one of ours.</summary>
    public static StateShape? For(string shape)
    {
        return TryGet(shape, out StateShape? found) ? found : null;
    }

    public static bool TryGet(string shape, out StateShape? stateShape)
    {
        stateShape = null;
        if (!shape.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string name = shape[Prefix.Length..];
        foreach (StateShape candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                stateShape = candidate;
                return true;
            }
        }

        return false;
    }

    private static Library Load()
    {
        using Stream stream = OpenResource();
        using JsonDocument document = JsonDocument.Parse(stream);
        var shapes = new List<StateShape>();
        foreach (JsonElement shape in document.RootElement.GetProperty("shapes").EnumerateArray())
        {
            shapes.Add(new StateShape(
                shape.GetProperty("name").GetString()!,
                shape.GetProperty("label").GetString()!,
                shape.GetProperty("kind").GetString()!,
                shape.GetProperty("paletteKind").GetString()!));
        }

        return new Library(shapes);
    }

    private static Stream OpenResource()
    {
        const string name = "schemas/state-shapes.json";
        Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return stream ?? throw new FileNotFoundException($"Embedded state shape library '{name}' is missing from the Draughtsman.Core assembly.");
    }

    private sealed record Library(IReadOnlyList<StateShape> Shapes);
}
