using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Draughtsman.Core.Schemas;

/// <summary>One drawn part of a basic shape: absolute path data in the shape's coordinates (see <see cref="BasicShapeGeometry"/>)
/// and whether it is painted with the node's fill as well as its stroke. The first part of a shape is its outline.</summary>
public sealed record BasicShapePart(string D, bool Fill);

/// <summary>One connection point of a basic shape: a side id (<c>top</c>, <c>right</c>, <c>bottom</c> or <c>left</c>) and its place as
/// fractions of the node box.</summary>
public sealed record BasicShapePort(string Id, double X, double Y);

/// <summary>One general-purpose outline or annotation shape: its shape name, display label, whether it annotates the diagram instead of
/// taking part in it, the box the palette places it at, its parts, the label rectangle as four coordinate tokens (left, top, right,
/// bottom) and its ports.</summary>
public sealed record BasicShape(
    string Name,
    string Label,
    bool Annotation,
    double DefaultWidth,
    double DefaultHeight,
    IReadOnlyList<BasicShapePart> Parts,
    IReadOnlyList<string> LabelBox,
    IReadOnlyList<BasicShapePort> Ports);

/// <summary>
/// The general-purpose shape library, loaded from the embedded <c>basic-shapes.json</c>: ellipse, triangle, trapezoid, chevron, cross,
/// arrows, note, callout, brace, cube, folder and component. Each is placed as the presentation shape <c>custom:basic/&lt;name&gt;</c>,
/// a branch the document schema already admits, so the library needs no schema change and no kind of its own. The outlines are data both
/// renderers read - the web canvas through the imported JSON (<c>web/src/model/basicShapes.ts</c>) and the MCP SVG renderer through this
/// class - so the drawing cannot drift between them; <see cref="BasicShapeGeometry"/> and its web twin turn the same path data into the
/// same numbers for a given box, which a test on each side pins.
/// </summary>
public static class BasicShapeLibrary
{
    /// <summary>The shape-name prefix every basic shape carries: <c>custom:basic/&lt;name&gt;</c>.</summary>
    public const string Prefix = "custom:basic/";

    private static readonly Lazy<Library> s_library = new(Load);

    /// <summary>Every shape, in the library's own order (the palette's order).</summary>
    public static IReadOnlyList<BasicShape> All => s_library.Value.Shapes;

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

    /// <summary>The full shape name for a library entry: <c>custom:basic/&lt;name&gt;</c>.</summary>
    public static string ShapeName(BasicShape shape)
    {
        return Prefix + shape.Name;
    }

    /// <summary>True when <paramref name="shape"/> names a shape in this library (not merely the basic prefix).</summary>
    public static bool IsBasicShape(string shape)
    {
        return TryGet(shape, out _);
    }

    /// <summary>The shape a <c>custom:basic/&lt;name&gt;</c> name refers to, or null when it is not one of ours.</summary>
    public static BasicShape? For(string shape)
    {
        return TryGet(shape, out BasicShape? found) ? found : null;
    }

    public static bool TryGet(string shape, out BasicShape? basicShape)
    {
        basicShape = null;
        if (!shape.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string name = shape[Prefix.Length..];
        foreach (BasicShape candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                basicShape = candidate;
                return true;
            }
        }

        return false;
    }

    private static Library Load()
    {
        using Stream stream = OpenResource();
        using JsonDocument document = JsonDocument.Parse(stream);
        var shapes = new List<BasicShape>();
        foreach (JsonElement shape in document.RootElement.GetProperty("shapes").EnumerateArray())
        {
            JsonElement size = shape.GetProperty("size");
            var parts = new List<BasicShapePart>();
            foreach (JsonElement part in shape.GetProperty("parts").EnumerateArray())
            {
                bool fill = part.TryGetProperty("fill", out JsonElement fillValue) && fillValue.GetBoolean();
                parts.Add(new BasicShapePart(part.GetProperty("d").GetString()!, fill));
            }

            var ports = new List<BasicShapePort>();
            foreach (JsonProperty port in shape.GetProperty("ports").EnumerateObject())
            {
                ports.Add(new BasicShapePort(port.Name, port.Value[0].GetDouble(), port.Value[1].GetDouble()));
            }

            shapes.Add(new BasicShape(
                shape.GetProperty("name").GetString()!,
                shape.GetProperty("label").GetString()!,
                shape.TryGetProperty("annotation", out JsonElement annotation) && annotation.GetBoolean(),
                size.GetProperty("w").GetDouble(),
                size.GetProperty("h").GetDouble(),
                parts,
                shape.GetProperty("labelBox").EnumerateArray().Select(token => token.GetString()!).ToList(),
                ports));
        }

        return new Library(shapes);
    }

    private static Stream OpenResource()
    {
        const string name = "schemas/basic-shapes.json";
        Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return stream ?? throw new FileNotFoundException($"Embedded basic shape library '{name}' is missing from the Draughtsman.Core assembly.");
    }

    private sealed record Library(IReadOnlyList<BasicShape> Shapes);
}

/// <summary>
/// Turns a basic shape's path data into absolute coordinates for a box of a given size. Mirrors web/src/model/basicShapes.ts
/// (<c>resolvePath</c>, <c>resolveCoordinate</c>), and the two are held to the same literal output by a test on each side.
/// A coordinate is a fraction of the box's extent on its axis, optionally plus or minus a fixed length in pixels (<c>1-16</c>,
/// <c>0.5+4</c>); the fixed length never exceeds half the extent, so a box smaller than the shape's features squeezes them rather
/// than turning the outline inside out. Numbers are rounded to two decimals, half up, which is what JavaScript's <c>Math.round</c> does.
/// </summary>
public static class BasicShapeGeometry
{
    /// <summary>The longest fixed length a coordinate may add, as a fraction of the box's extent on that axis.</summary>
    public const double FixedLengthLimit = 0.5;

    /// <summary>The value of one coordinate token on an axis whose extent is <paramref name="extent"/> (the box's width for x, height for y).</summary>
    public static double ResolveCoordinate(string token, double extent)
    {
        int split = SignIndex(token);
        double fraction = double.Parse(split < 0 ? token : token[..split], CultureInfo.InvariantCulture);
        double value = fraction * extent;
        if (split >= 0)
        {
            double fixedLength = Math.Min(double.Parse(token[(split + 1)..], CultureInfo.InvariantCulture), extent * FixedLengthLimit);
            value = token[split] == '-' ? value - fixedLength : value + fixedLength;
        }

        return value;
    }

    /// <summary>The label rectangle of a shape in a box of the given size, as (left, top, right, bottom) relative to the box's top-left.</summary>
    public static (double Left, double Top, double Right, double Bottom) ResolveBox(IReadOnlyList<string> tokens, double w, double h)
    {
        return (ResolveCoordinate(tokens[0], w), ResolveCoordinate(tokens[1], h), ResolveCoordinate(tokens[2], w), ResolveCoordinate(tokens[3], h));
    }

    /// <summary>
    /// A part's path data resolved for a box whose top-left corner is at (<paramref name="x"/>, <paramref name="y"/>) and whose size
    /// is <paramref name="w"/> by <paramref name="h"/>. Only the commands M, L, C, H, V and Z are understood; anything else throws, so
    /// a typo in the library fails its test instead of drawing a half outline.
    /// </summary>
    public static string ResolvePath(string d, double x, double y, double w, double h)
    {
        var path = new StringBuilder();
        string[] tokens = SplitCommands(d);
        char command = ' ';
        var numbers = new List<string>();

        void Flush()
        {
            if (command == ' ')
            {
                return;
            }

            if (path.Length > 0)
            {
                path.Append(' ');
            }

            path.Append(Emit(command, numbers, x, y, w, h));
            numbers.Clear();
        }

        foreach (string token in tokens)
        {
            if (token.Length == 1 && "MLCHVZ".Contains(token[0], StringComparison.Ordinal))
            {
                Flush();
                command = token[0];
            }
            else
            {
                numbers.Add(token);
            }
        }

        Flush();
        return path.ToString();
    }

    private static string Emit(char command, List<string> numbers, double x, double y, double w, double h)
    {
        int perPoint = command is 'H' or 'V' ? 1 : 2;
        int arity = command switch
        {
            'C' => 6,
            'Z' => 0,
            _ => perPoint,
        };
        if (arity == 0 ? numbers.Count != 0 : numbers.Count == 0 || numbers.Count % arity != 0)
        {
            throw new FormatException($"Path command {command} takes {arity} numbers, found {numbers.Count}.");
        }

        if (command == 'Z')
        {
            return "Z";
        }

        var text = new StringBuilder();
        for (int start = 0; start < numbers.Count; start += arity)
        {
            if (start > 0)
            {
                text.Append(' ');
            }

            text.Append(command);
            switch (command)
            {
                case 'H':
                    text.Append(Format(x + ResolveCoordinate(numbers[start], w)));
                    break;
                case 'V':
                    text.Append(Format(y + ResolveCoordinate(numbers[start], h)));
                    break;
                default:
                    for (int i = 0; i < arity; i += 2)
                    {
                        text.Append(i > 0 ? " " : "");
                        text.Append(Format(x + ResolveCoordinate(numbers[start + i], w)));
                        text.Append(',');
                        text.Append(Format(y + ResolveCoordinate(numbers[start + i + 1], h)));
                    }

                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>Two decimals, half up (JavaScript's Math.round), in the invariant culture; whole numbers carry no decimal point.</summary>
    private static string Format(double value)
    {
        double rounded = Math.Floor(value * 100 + 0.5) / 100;
        return rounded.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The index of the sign that starts a token's fixed length, or -1 when the token is a bare fraction. A leading sign belongs to the fraction.</summary>
    private static int SignIndex(string token)
    {
        for (int i = 1; i < token.Length; i++)
        {
            if (token[i] is '+' or '-')
            {
                return i;
            }
        }

        return -1;
    }

    private static string[] SplitCommands(string d)
    {
        var spaced = new StringBuilder(d.Length * 2);
        foreach (char c in d)
        {
            if (char.IsLetter(c))
            {
                spaced.Append(' ').Append(c).Append(' ');
            }
            else
            {
                spaced.Append(c == ',' ? ' ' : c);
            }
        }

        return spaced.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
