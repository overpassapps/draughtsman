using System.Text;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import.Drawio;

namespace Draughtsman.Core.Import.Visio;

/// <summary>The outline a shape's geometry draws, as far as the importer can tell: the few outlines the model has a shape for.</summary>
internal enum VisioOutline
{
    /// <summary>No visible geometry: a text label, or a helper that draws nothing.</summary>
    None,
    Rectangle,
    Diamond,
    Ellipse,
    Parallelogram,
    Hexagon,

    /// <summary>Something else (an arrow, a star, a freehand path, a drawing built of arcs): not a shape the model has.</summary>
    Custom,
}

/// <summary>What a Visio shape became: the kind and shape the model gives it, and what the importer could not do for it so it can be named in one aggregated warning.</summary>
/// <param name="UnknownMaster">The name of the Visio master (a stencil shape) this shape is an instance of that nothing here stands for, when its outline did not say what to draw instead.</param>
/// <param name="CustomOutline">True when the shape's outline is a custom drawing, so it is a plain box.</param>
/// <param name="Picture">True for a picture or embedded object, which is not imported.</param>
internal sealed record VisioMapping(DrawioShape Shape, string? UnknownMaster, bool CustomOutline, bool Picture);

/// <summary>
/// Maps a Visio shape to a kind and a schema shape for one document type. It never decides a kind itself: it works out what the shape is (from
/// the name of its master, then from the outline its geometry draws) and asks <see cref="DrawioShapeMapper"/> what that is called in the
/// document type, so a Visio decision, a draw.io ellipse and a Mermaid circle are the same kind of node, and a kind that was a judgement is flagged
/// the same way.
/// </summary>
internal static class VisioShapeMapper
{
    private const string Plain = "";
    private const string Rounded = "rounded=1";
    private const string LucidPrefix = "com.lucidchart.";

    /// <summary>A rounding smaller than this many inches (a fifth of Visio's own rounded rectangle) is a square corner drawn with a rounding of nearly nothing.</summary>
    private const double MinimumRounding = 0.02;

    /// <summary>Visio master (or shape) names, lower-cased with everything but letters and digits dropped, to the draw.io shape <see cref="DrawioShapeMapper"/> knows it as.</summary>
    private static readonly Dictionary<string, string> s_names = BuildNames();

    private static readonly HashSet<string> s_containerNames = new(StringComparer.Ordinal)
    {
        "swimlane", "swimlanes", "pool", "lane", "container", "horizontalswimlane", "verticalswimlane", "poolswimlane", "crossfunctionalswimlane",
    };

    private static Dictionary<string, string> BuildNames()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string token, params string[] visioNames)
        {
            foreach (string name in visioNames)
            {
                names[name] = token;
            }
        }

        Add(Plain, "process", "rectangle", "square", "box", "rect", "basicrectangle", "task", "step");
        Add(Rounded, "roundedrectangle", "rounded", "roundrectangle");
        Add("rhombus", "decision", "diamond", "rhombus", "choice");
        Add("mxgraph.flowchart.terminator", "startend", "terminator", "start", "end", "startterminator", "endterminator");
        Add("document", "document");
        Add("mxgraph.flowchart.multi-document", "multidocument", "documents", "multipledocuments");
        Add("parallelogram", "data", "inputoutput", "io", "parallelogram");
        Add("cylinder", "database", "directdata", "directaccessstorage", "datastore", "cylinder", "can", "disk", "magneticdisk");
        Add("mxgraph.flowchart.stored_data", "storeddata", "sequentialdata");
        Add("internalStorage", "internalstorage");
        Add("process", "predefinedprocess", "subprocess", "subroutine");
        Add("manualInput", "manualinput");
        Add("mxgraph.flowchart.manual_operation", "manualoperation");
        Add("delay", "delay");
        Add("display", "display");
        Add("hexagon", "preparation", "hexagon");
        Add("card", "card");
        Add("or", "or");
        Add("sumEllipse", "summingjunction");
        Add("collate", "collate");
        Add("sortShape", "sort");
        Add("offPageConnector", "offpagereference", "offpageconnector", "offpage");
        Add("mxgraph.flowchart.on-page_reference", "onpagereference", "onpageconnector");
        Add("ellipse", "ellipse", "circle", "oval");
        Add("actor", "actor", "user", "person", "stickfigure");
        Add("cloud", "cloud");
        Add("note", "note", "stickynote", "annotation");
        Add("text", "text", "textbox", "textblock", "label");
        return names;
    }

    /// <summary>
    /// The name of a master or shape as the rules above are keyed: Visio's copy suffix (<c>Process.12</c>) dropped, lower case, letters and digits only.
    /// Lucidchart names the masters it writes <c>com.lucidchart.ProcessBlock21.&lt;hash&gt;</c>: the prefix, the hash and the number are dropped and
    /// so is a trailing <c>Block</c>, leaving <c>process</c>, which is what Visio's own flowchart master is called.
    /// </summary>
    public static string Normalise(string name)
    {
        if (name.StartsWith(LucidPrefix, StringComparison.Ordinal))
        {
            string rest = name[LucidPrefix.Length..];
            int dot = rest.IndexOf('.', StringComparison.Ordinal);
            string core = (dot < 0 ? rest : rest[..dot]).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            string key = Letters(core);
            return key.EndsWith("block", StringComparison.Ordinal) && key.Length > 5 ? key[..^5] : key;
        }

        int last = name.LastIndexOf('.');
        if (last > 0 && last < name.Length - 1 && name.AsSpan(last + 1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            name = name[..last];
        }

        return Letters(name);
    }

    /// <summary>A name as it is shown in a warning: Lucidchart's long master names are cut to the part a person would recognise.</summary>
    public static string Display(string name)
    {
        if (!name.StartsWith(LucidPrefix, StringComparison.Ordinal))
        {
            return name;
        }

        string rest = name[LucidPrefix.Length..];
        int dot = rest.IndexOf('.', StringComparison.Ordinal);
        return "Lucidchart " + (dot < 0 ? rest : rest[..dot]).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
    }

    private static string Letters(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>True when the shape's name says it is a swimlane, a pool or a container, whatever else it is.</summary>
    public static bool IsContainerName(string name)
    {
        string key = Normalise(name);
        return s_containerNames.Contains(key) || key.Contains("swimlane", StringComparison.Ordinal);
    }

    /// <summary>The kind and shape for <paramref name="view"/> in a document of <paramref name="type"/>.</summary>
    /// <param name="holdsChildren">Whether shapes of the page were imported as this shape's children: it is then a container.</param>
    /// <param name="hasLabel">Whether the shape has text: a shape with no outline and text is a text label.</param>
    /// <param name="outlineSource">For a stencil group stood in for by one box: the part of it that draws its outline (a group has none of its own), when there is one.</param>
    /// <param name="collapsedGroup">True for a stencil group stood in for by one box, which is an unknown master when nothing says what it is.</param>
    public static VisioMapping Map(DocumentType type, VisioShapeView view, bool holdsChildren, bool hasLabel, VisioShapeView? outlineSource = null, bool collapsedGroup = false)
    {
        if (view.Shape.IsForeign)
        {
            return new VisioMapping(DrawioShapeMapper.Map(type, DrawioStyle.Parse(Plain), false), null, false, Picture: true);
        }

        if (holdsChildren || view.Shape.StructureType == "Container" || IsContainerName(view.Name))
        {
            return new VisioMapping(DrawioShapeMapper.Map(type, DrawioStyle.Parse("group"), true), null, false, false);
        }

        VisioShapeView outlineView = outlineSource ?? view;
        string key = Normalise(view.Name);
        VisioOutline outline = outlineView.GeometryTruncated ? VisioOutline.Custom : Classify(outlineView);
        bool rounded = (view.Number("Rounding") ?? 0) >= MinimumRounding;

        if (s_names.TryGetValue(key, out string? token))
        {
            string style = token == Plain && rounded ? Rounded : token;
            return new VisioMapping(DrawioShapeMapper.Map(type, DrawioStyle.Parse(style), false), null, false, false);
        }

        switch (outline)
        {
            case VisioOutline.Rectangle:
                return Mapped(type, rounded ? Rounded : Plain);
            case VisioOutline.Diamond:
                return Mapped(type, "rhombus");
            case VisioOutline.Ellipse:
                return Mapped(type, "ellipse");
            case VisioOutline.Parallelogram:
                return Mapped(type, "parallelogram");
            case VisioOutline.Hexagon:
                return Mapped(type, "hexagon");
            case VisioOutline.None when hasLabel && !collapsedGroup:
                return Mapped(type, "text");
            case VisioOutline.None when !collapsedGroup:
                return Mapped(type, Plain);
            default:
                // A custom drawing, or a group with no outline of its own: a plain box. A stencil shape says which master it is; a shape drawn by hand just says it was custom.
                bool stencil = collapsedGroup || (view.IsMasterInstance && view.Master is not null && key.Length > 0);
                string name = view.Name.Length > 0 ? Display(view.Name) : "(unnamed master)";
                return new VisioMapping(DrawioShapeMapper.Map(type, DrawioStyle.Parse(Plain), false), stencil ? name : null, CustomOutline: !stencil, false);
        }
    }

    private static VisioMapping Mapped(DocumentType type, string style)
    {
        return new VisioMapping(DrawioShapeMapper.Map(type, DrawioStyle.Parse(style), false), null, false, false);
    }

    // ---- outlines ----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The outline of the first visible geometry section. A section is visible unless it is hidden or neither filled nor stroked. Only a path of
    /// straight segments, or one ellipse, is read: a path with curves in it is custom (a rounded rectangle Visio draws is a rectangle with a
    /// rounding setting, which is read from the cell, not from the path).
    /// </summary>
    public static VisioOutline Classify(VisioShapeView view)
    {
        VisioGeometrySection? section = view.Geometry.FirstOrDefault(s => !s.NoShow && !(s.NoFill && s.NoLine));
        if (section is null || section.Rows.Count == 0)
        {
            return VisioOutline.None;
        }

        if (section.Truncated)
        {
            return VisioOutline.Custom;
        }

        if (section.Rows.Any(r => r.Type == "Ellipse"))
        {
            return VisioOutline.Ellipse;
        }

        double w = view.Number("Width") ?? 0;
        double h = view.Number("Height") ?? 0;
        var points = new List<(double X, double Y)>();
        for (int i = 0; i < section.Rows.Count; i++)
        {
            VisioGeometryRow row = section.Rows[i];
            bool relative = row.Type is "RelMoveTo" or "RelLineTo";
            bool absolute = row.Type is "MoveTo" or "LineTo";
            if (!(relative || absolute) || (row.Type is "MoveTo" or "RelMoveTo") != (i == 0) || double.IsNaN(row.X) || double.IsNaN(row.Y))
            {
                return VisioOutline.Custom;
            }

            if (absolute && (w <= 0 || h <= 0))
            {
                return VisioOutline.Custom;
            }

            (double x, double y) = relative ? (row.X, row.Y) : (row.X / w, row.Y / h);
            if (points.Count == 0 || !Near(points[^1], (x, y)))
            {
                points.Add((x, y));
            }
        }

        if (points.Count > 1 && Near(points[0], points[^1]))
        {
            points.RemoveAt(points.Count - 1);
        }

        return points.Count switch
        {
            4 when HasAll(points, (0, 0), (1, 0), (1, 1), (0, 1)) => VisioOutline.Rectangle,
            4 when HasAll(points, (0.5, 0), (1, 0.5), (0.5, 1), (0, 0.5)) => VisioOutline.Diamond,
            4 when IsParallelogram(points) => VisioOutline.Parallelogram,
            6 when IsHexagon(points) => VisioOutline.Hexagon,
            _ => VisioOutline.Custom,
        };
    }

    private const double Tolerance = 0.02;

    private static bool Near((double X, double Y) a, (double X, double Y) b)
    {
        return Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(a.Y - b.Y) <= Tolerance;
    }

    private static bool HasAll(List<(double X, double Y)> points, params (double X, double Y)[] expected)
    {
        return expected.All(e => points.Any(p => Near(p, e))) && points.All(p => expected.Any(e => Near(p, e)));
    }

    /// <summary>Two points on the bottom edge and two on the top, the same distance apart, the top pair shifted sideways.</summary>
    private static bool IsParallelogram(List<(double X, double Y)> points)
    {
        double[] bottom = points.Where(p => p.Y <= Tolerance).Select(p => p.X).Order().ToArray();
        double[] top = points.Where(p => p.Y >= 1 - Tolerance).Select(p => p.X).Order().ToArray();
        return bottom.Length == 2 && top.Length == 2
            && Math.Abs((bottom[1] - bottom[0]) - (top[1] - top[0])) <= 0.03
            && Math.Abs(bottom[0] - top[0]) > 0.03
            && bottom[1] - bottom[0] < 1 - Tolerance;
    }

    /// <summary>Two points on the left and right edges at mid height, two on the bottom edge and two on the top.</summary>
    private static bool IsHexagon(List<(double X, double Y)> points)
    {
        bool left = points.Count(p => p.X <= Tolerance && Math.Abs(p.Y - 0.5) <= Tolerance) == 1;
        bool right = points.Count(p => p.X >= 1 - Tolerance && Math.Abs(p.Y - 0.5) <= Tolerance) == 1;
        return left && right && points.Count(p => p.Y <= Tolerance) == 2 && points.Count(p => p.Y >= 1 - Tolerance) == 2;
    }
}
