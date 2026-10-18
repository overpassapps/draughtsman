using System.Globalization;
using System.Text;
using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Core.Import.Drawio;

/// <summary>
/// Imports a draw.io (diagrams.net) file into a <see cref="Document"/>: plain or compressed, one page of however many it has. Shapes become nodes
/// (<c>n_&lt;id&gt;</c>, a container <c>c_&lt;id&gt;</c>) with their label and absolute geometry, and a kind guessed from the shape and flagged for
/// review where it was a judgement; connectors become edges (<c>e_&lt;id&gt;</c>) with their label, arrowheads, dash and routing. Geometry arrives
/// with the file, so it is carried as it is and the caller does not lay the diagram out.
/// <para>
/// What draw.io has that the model has not is never a failure: an unknown shape is a plain box and an <see cref="ImportWarningCodes.UnsupportedSyntax"/>
/// warning, a connector with a loose end is left out under <see cref="ImportWarningCodes.MissingReference"/>, hidden shapes are left out and counted,
/// and an arrowhead or colour with no equivalent is <see cref="ImportWarningCodes.StyleApproximated"/>. What is refused is the source itself, as a
/// <see cref="SourceRejectedException"/>: not a draw.io file, or past a limit (<see cref="DrawioLimits"/>), or carrying a DOCTYPE. The file is untrusted:
/// it is read by <see cref="DrawioReader"/>, nothing in it is ever fetched, and a label is reduced to its text (<see cref="DrawioText"/>).
/// </para>
/// </summary>
public static class DrawioImporter
{
    private const string DefaultTitle = "Imported diagram";
    private const string CreatedBy = "drawio-import";

    /// <exception cref="ArgumentException">The type cannot hold a drawing (a sequence document), or the page is not one the file has.</exception>
    /// <exception cref="SourceRejectedException">The source is not a draw.io file, is past a limit, or has a DOCTYPE.</exception>
    public static ImportResult Import(string source, DrawioImportOptions? options = null)
    {
        return SourceRejectedException.Guard("draw.io", () => ImportCore(source, options ?? new DrawioImportOptions()));
    }

    private static ImportResult ImportCore(string source, DrawioImportOptions options)
    {
        if (options.Type == DocumentType.Sequence)
        {
            throw new ArgumentException("A sequence document is made from a Mermaid 'sequenceDiagram', not from a drawing. Use type 'flowchart', 'architecture' or 'freeform' for a draw.io file.");
        }

        DrawioFile file = DrawioReader.Read(source, options.Page);
        var warnings = new List<ImportWarning>();
        Graph graph = Graph.Build(file.Cells, warnings);

        var ids = new MermaidImporter.IdAllocator("draw.io");
        var nodes = new List<Node>();
        var nodeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DrawioCell vertex in graph.EmissionOrder())
        {
            nodes.Add(BuildNode(vertex, graph, options.Type, ids, nodeIds, warnings));
        }

        var nodeKinds = nodes.ToDictionary(n => n.Id, n => n.Kind, StringComparer.Ordinal);
        var edges = new List<Edge>();
        foreach (DrawioCell edge in graph.Edges)
        {
            Edge? built = BuildEdge(edge, graph, options.Type, ids, nodeIds, nodeKinds, warnings);
            if (built is not null)
            {
                edges.Add(built);
            }
        }

        if (graph.Hidden > 0)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax,
                $"{graph.Hidden.ToString(CultureInfo.InvariantCulture)} hidden {(graph.Hidden == 1 ? "shape or connector" : "shapes or connectors")} (hidden in draw.io, on a hidden layer or inside a hidden shape) {(graph.Hidden == 1 ? "was" : "were")} left out."));
        }

        if (graph.VisibleLayers > 1)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax,
                $"The page has {graph.VisibleLayers.ToString(CultureInfo.InvariantCulture)} layers. Draughtsman has no layers, so their shapes all sit on one canvas, in the order draw.io drew them."));
        }

        if (nodes.Count == 0)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.EmptyDiagram, file.Pages.Count == 0 ? "The file has no pages." : "The page has no shapes."));
        }

        PageNote(file, warnings);
        DateTimeOffset now = options.Now ?? DateTimeOffset.UtcNow;
        var document = new Document(
            $"doc_{Guid.NewGuid():N}",
            Document.CurrentSchemaVersion,
            options.Title ?? TitleOf(file) ?? DefaultTitle,
            options.Type,
            nodes,
            edges,
            new Meta(now, now, CreatedBy));

        return new ImportResult(document, CapWarnings(warnings))
        {
            Pages = file.Pages.Select((p, i) => new ImportPage(i + 1, p.Name, i == file.SelectedPage)).ToList(),
        };
    }

    // ---- nodes -----------------------------------------------------------------------------------------------------------------------

    private static Node BuildNode(DrawioCell vertex, Graph graph, DocumentType type, MermaidImporter.IdAllocator ids, Dictionary<string, string> nodeIds, List<ImportWarning> warnings)
    {
        DrawioStyle style = DrawioStyle.Parse(vertex.Style);
        DrawioShape shape = DrawioShapeMapper.Map(type, style, graph.HoldsChildren(vertex));
        string prefix = shape.Kind == DrawioShapeMapper.ContainerKind ? "c_" : "n_";
        string id = ids.Allocate(prefix, vertex.Id, null, warnings);
        nodeIds[vertex.Id] = id;

        string label = LabelOf(vertex, style, id, warnings);
        string name = NameOf(label, vertex.Id);
        if (graph.MissingParentOf(vertex) is { } missingParent)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.MissingReference, $"{name} sits in a cell, '{missingParent}', that is not in the file, so it was placed at the top level.", null, id));
        }

        if (shape.Guessed)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.KindGuessed,
                $"{name} is drawn as {shape.Description}, so it was given the kind '{shape.Kind}'; change it if that is not what it is.", null, id));
        }

        if (shape.Unsupported is { } unsupported)
        {
            string detail = unsupported.Equals("image", StringComparison.OrdinalIgnoreCase)
                ? "is a picture in draw.io (an image shape); pictures are not imported, so it is drawn as a plain box."
                : $"uses the draw.io shape '{unsupported}', which has no equivalent here; it is drawn as a plain box.";
            warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax, $"{name} {detail}", null, id));
        }

        var notes = new List<string>();
        NodeStyle? nodeStyle = DrawioStyleMapper.ForNode(style, shape.Kind == "text", notes.Add);
        AddApproximations(notes, name, id, warnings);

        Geometry? geometry = graph.GeometryOf(vertex, style, out bool invalid);
        if (geometry is not null && shape.Shape.StartsWith("custom:generic/", StringComparison.Ordinal))
        {
            geometry = GrownForIcon(geometry);
        }

        if (geometry is null)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax,
                invalid ? $"{name} is placed too far from the origin for a canvas, so it was given no position." : $"{name} has no position in the file, so it was given none.", null, id));
        }

        return new Node(id, shape.Kind, label, shape.Shape)
        {
            Geometry = geometry,
            Style = nodeStyle,
            Props = PropsOf(vertex, name, id, warnings),
            Parent = graph.ParentOf(vertex) is { } parent ? nodeIds[parent.Id] : null,
        };
    }

    /// <summary>An icon node's box grown to at least <see cref="DrawioLimits.IconMinSize"/> each way, about the same centre, so its label has room beneath the artwork.</summary>
    internal static Geometry GrownForIcon(Geometry geometry)
    {
        double width = Math.Max(geometry.W, DrawioLimits.IconMinSize);
        double height = Math.Max(geometry.H, DrawioLimits.IconMinSize);
        return geometry with { X = geometry.X - ((width - geometry.W) / 2), Y = geometry.Y - ((height - geometry.H) / 2), W = width, H = height };
    }

    // ---- edges -----------------------------------------------------------------------------------------------------------------------

    private static Edge? BuildEdge(DrawioCell edge, Graph graph, DocumentType type, MermaidImporter.IdAllocator ids, Dictionary<string, string> nodeIds, Dictionary<string, string> nodeKinds, List<ImportWarning> warnings)
    {
        DrawioStyle style = DrawioStyle.Parse(edge.Style);
        string labelText = LabelOf(edge, style, edge.Id, warnings, quiet: true);
        List<string> extraLabels = graph.LabelsOf(edge);
        string name = NameOf(labelText.Length > 0 ? labelText : extraLabels.FirstOrDefault() ?? string.Empty, edge.Id, "connector");

        string? from = EndOf(edge, edge.Source, "start", graph, nodeIds, name, warnings);
        string? to = EndOf(edge, edge.Target, "end", graph, nodeIds, name, warnings);
        if (from is null || to is null)
        {
            return null;
        }

        string id = ids.Allocate("e_", edge.Id, null, warnings);
        string label = labelText.Length > 0 ? labelText : extraLabels.FirstOrDefault() ?? string.Empty;
        int unused = labelText.Length > 0 ? extraLabels.Count : Math.Max(0, extraLabels.Count - 1);
        if (unused > 0)
        {
            string dropped = string.Join("\", \"", (labelText.Length > 0 ? extraLabels : extraLabels.Skip(1)).Take(3));
            warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax,
                $"{name} has {unused.ToString(CultureInfo.InvariantCulture)} more {(unused == 1 ? "label" : "labels")} than a connector can carry (\"{dropped}\"); only \"{label}\" was kept.", null, id));
        }

        var notes = new List<string>();
        ArrowHead start = DrawioStyleMapper.Head(style, atStart: true, notes.Add);
        ArrowHead end = DrawioStyleMapper.Head(style, atStart: false, notes.Add);
        string kind = EdgeKindFor(type, style, start, end, nodeKinds[from], nodeKinds[to]);
        EdgeStyle? edgeStyle = DrawioStyleMapper.ForEdge(style, kind, start, end, notes.Add);
        AddApproximations(notes, name, id, warnings);

        return new Edge(id, kind, new Endpoint(from), new Endpoint(to))
        {
            Label = label.Length == 0 ? null : label,
            Routing = RoutingOf(edge, style, graph, name, id, warnings),
            Style = edgeStyle,
            Props = PropsOf(edge, name, id, warnings),
        };
    }

    /// <summary>The node id a connector end attaches to, or null with a missing-reference warning saying why the connector cannot be drawn.</summary>
    private static string? EndOf(DrawioCell edge, string? cellId, string which, Graph graph, Dictionary<string, string> nodeIds, string name, List<ImportWarning> warnings)
    {
        string? reason;
        if (string.IsNullOrEmpty(cellId))
        {
            reason = $"is not attached to a shape at its {which} (a loose end in draw.io), and a Draughtsman connector joins two shapes";
        }
        else if (nodeIds.TryGetValue(cellId, out string? nodeId))
        {
            return nodeId;
        }
        else
        {
            reason = graph.Describe(cellId) switch
            {
                CellKind.Missing => $"is attached at its {which} to a cell, '{cellId}', that is not in the file",
                CellKind.Edge => $"is attached at its {which} to another connector, which Draughtsman cannot attach to",
                CellKind.HiddenOrSkipped => $"is attached at its {which} to a shape that was hidden or left out",
                _ => $"is attached at its {which} to something that is not a shape",
            };
        }

        warnings.Add(new ImportWarning(ImportWarningCodes.MissingReference, $"{name} (draw.io id {edge.Id}) {reason}, so it was left out."));
        return null;
    }

    private static string EdgeKindFor(DocumentType type, DrawioStyle style, ArrowHead start, ArrowHead end, string fromKind, string toKind)
    {
        if ((start == ArrowHead.None && end == ArrowHead.None) || DrawioStyleMapper.IsNotation(start) || DrawioStyleMapper.IsNotation(end))
        {
            return "association";
        }

        if (type == DocumentType.Architecture)
        {
            if (style.Flag("dashed"))
            {
                return "dependency";
            }

            if (fromKind == "database" || toKind == "database")
            {
                return "data";
            }
        }

        return "flow";
    }

    /// <summary>
    /// The routing draw.io drew the connector with. An orthogonal one (any <c>edgeStyle</c>) keeps no points: draw.io's points are hints for its own
    /// router, not the bends, so the editor's router derives the route from the shapes as it does for a connector that was never drawn by hand. A
    /// straight or curved one keeps its points, which are its bends.
    /// </summary>
    private static Routing RoutingOf(DrawioCell edge, DrawioStyle style, Graph graph, string name, string id, List<ImportWarning> warnings)
    {
        string? edgeStyle = style.Get("edgeStyle");
        if (!string.IsNullOrWhiteSpace(edgeStyle) && !edgeStyle.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return new Routing(RoutingMode.Orthogonal);
        }

        RoutingMode mode = style.Flag("curved") ? RoutingMode.Curved : RoutingMode.Straight;
        IReadOnlyList<DrawioPoint> points = edge.Geometry?.Points ?? [];
        if (points.Count == 0)
        {
            return new Routing(mode);
        }

        (double dx, double dy) = graph.OriginOf(edge);
        var waypoints = new List<Point>();
        foreach (DrawioPoint point in points.Take(DrawioLimits.MaxWaypoints))
        {
            double x = point.X + dx;
            double y = point.Y + dy;
            if (Math.Abs(x) <= DrawioLimits.MaxCoordinate && Math.Abs(y) <= DrawioLimits.MaxCoordinate)
            {
                waypoints.Add(new Point(x, y));
            }
        }

        if (points.Count > DrawioLimits.MaxWaypoints)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax, $"{name} bends through {points.Count.ToString(CultureInfo.InvariantCulture)} points; only the first {DrawioLimits.MaxWaypoints.ToString(CultureInfo.InvariantCulture)} were kept.", null, id));
        }

        return new Routing(mode) { Waypoints = waypoints.Count == 0 ? null : waypoints };
    }

    // ---- labels, props, warnings -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The text of a cell's label: placeholders filled in from its custom data, HTML reduced to text when the style says <c>html=1</c>, cut to
    /// <see cref="DrawioLimits.MaxLabelLength"/>. <paramref name="quiet"/> leaves out the shortened warning (an edge says it once, when it has its id).
    /// </summary>
    private static string LabelOf(DrawioCell cell, DrawioStyle style, string id, List<ImportWarning> warnings, bool quiet = false)
    {
        string value = cell.Value;
        if (cell.Attributes.TryGetValue("placeholders", out string? placeholders) && placeholders == "1")
        {
            value = FillPlaceholders(value, cell.Attributes);
        }

        string text = style.Flag("html") ? DrawioText.FromHtml(value) : DrawioText.FromPlain(value);
        if (text.Length > DrawioLimits.MaxLabelLength)
        {
            text = text[..DrawioLimits.MaxLabelLength];
            if (!quiet)
            {
                warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax,
                    $"The label of {NameOf(text, cell.Id)} was shortened to {DrawioLimits.MaxLabelLength.ToString("N0", CultureInfo.InvariantCulture)} characters.", null, id));
            }
        }

        return text;
    }

    /// <summary>Replaces <c>%name%</c> with the shape's custom data of that name, as draw.io does for a shape with placeholders on; a name it has no value for stays as written.</summary>
    private static string FillPlaceholders(string label, IReadOnlyDictionary<string, string> data)
    {
        if (!label.Contains('%', StringComparison.Ordinal))
        {
            return label;
        }

        var result = new StringBuilder(label.Length);
        int i = 0;
        while (i < label.Length)
        {
            int open = label.IndexOf('%', i);
            if (open < 0)
            {
                break;
            }

            int close = label.IndexOf('%', open + 1, Math.Min(label.Length - open - 1, 64));
            result.Append(label, i, open - i);
            if (close > open + 1 && data.TryGetValue(label.Substring(open + 1, close - open - 1), out string? replacement))
            {
                result.Append(replacement);
                i = close + 1;
            }
            else
            {
                result.Append('%');
                i = open + 1;
            }
        }

        result.Append(label, i, label.Length - i);
        return result.ToString();
    }

    /// <summary>
    /// A shape's custom data (the attributes of its <c>object</c> wrapper) as props: <c>link</c> as <c>url</c> when it is a web or mail address, the
    /// rest under their own names, at most <see cref="DrawioLimits.MaxProps"/> of them and each cut to <see cref="DrawioLimits.MaxPropLength"/>.
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement>? PropsOf(DrawioCell cell, string name, string id, List<ImportWarning> warnings)
    {
        var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        bool truncated = false;
        foreach ((string key, string raw) in cell.Attributes)
        {
            if (key == "placeholders")
            {
                continue;
            }

            string propKey = key;
            string value = raw.Length > DrawioLimits.MaxPropLength ? raw[..DrawioLimits.MaxPropLength] : raw;
            if (key == "link")
            {
                if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https" or "mailto"))
                {
                    warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax, $"The link on {name} is not a web or mail address, so it was not kept.", null, id));
                    continue;
                }

                propKey = "url";
                value = value.Trim();
            }

            if (propKey.Length == 0 || propKey.Length > 64 || propKey.Any(char.IsControl))
            {
                continue;
            }

            if (props.Count >= DrawioLimits.MaxProps)
            {
                truncated = true;
                break;
            }

            props[propKey] = JsonSerializer.SerializeToElement(value);
        }

        if (truncated)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax, $"{name} has more custom data than is kept; the first {DrawioLimits.MaxProps.ToString(CultureInfo.InvariantCulture)} entries were.", null, id));
        }

        return props.Count == 0 ? null : props;
    }

    /// <summary>The shape in the words its author would use: the label they wrote, or its draw.io id when it has none.</summary>
    internal static string NameOf(string label, string drawioId, string what = "shape")
    {
        string shown = label.Length > 40 ? label[..40].Replace('\n', ' ') + "..." : label.Replace('\n', ' ');
        return shown.Length > 0 ? $"\"{shown}\"" : $"The unlabelled {what} {drawioId}";
    }

    private static void AddApproximations(List<string> notes, string name, string id, List<ImportWarning> warnings)
    {
        foreach (string note in notes)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.StyleApproximated, $"{name} has {note}.", null, id));
        }
    }

    private static void PageNote(DrawioFile file, List<ImportWarning> warnings)
    {
        if (file.Pages.Count <= 1)
        {
            return;
        }

        DrawioPageInfo page = file.Pages[file.SelectedPage];
        int others = file.Pages.Count - 1;
        string named = page.Name.Length > 0 ? $" (\"{page.Name}\")" : string.Empty;
        warnings.Insert(0, new ImportWarning(ImportWarningCodes.PageSelected,
            $"Page {(file.SelectedPage + 1).ToString(CultureInfo.InvariantCulture)}{named} of {file.Pages.Count.ToString(CultureInfo.InvariantCulture)} was imported; the other {others.ToString(CultureInfo.InvariantCulture)} pages were not. Choose another page to import it instead."));
    }

    /// <summary>The page's name, unless it is draw.io's own <c>Page-1</c>, which says nothing.</summary>
    private static string? TitleOf(DrawioFile file)
    {
        if (file.SelectedPage < 0)
        {
            return null;
        }

        string name = file.Pages[file.SelectedPage].Name.Trim();
        bool placeholder = name.StartsWith("Page-", StringComparison.Ordinal) && name.Length > 5 && name.AsSpan(5).IndexOfAnyExceptInRange('0', '9') < 0;
        return name.Length == 0 || placeholder ? null : name;
    }

    /// <summary>At most <see cref="DrawioLimits.MaxWarnings"/> warnings, the rest summarised in one, so a file of ten thousand stencils does not answer with ten thousand lines.</summary>
    internal static List<ImportWarning> CapWarnings(List<ImportWarning> warnings)
    {
        if (warnings.Count <= DrawioLimits.MaxWarnings)
        {
            return warnings;
        }

        int more = warnings.Count - DrawioLimits.MaxWarnings;
        List<ImportWarning> kept = warnings.GetRange(0, DrawioLimits.MaxWarnings);
        kept.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax, $"...and {more.ToString("N0", CultureInfo.InvariantCulture)} more warnings like these were not listed."));
        return kept;
    }

    // ---- the cell graph ----------------------------------------------------------------------------------------------------------

    private enum CellKind
    {
        Missing,
        Edge,
        HiddenOrSkipped,
        Other,
    }

    /// <summary>
    /// The page's cells arranged for import: which are shapes and which connectors, which are hidden, which shape sits in which (parents resolved
    /// without cycles), every shape's absolute position, and each connector's extra label cells. Built once, in time linear in the cell count however
    /// the file nests them.
    /// </summary>
    private sealed class Graph
    {
        private readonly Dictionary<string, DrawioCell> _byId = new(StringComparer.Ordinal);
        private readonly HashSet<DrawioCell> _kept = [];
        private readonly Dictionary<DrawioCell, DrawioCell?> _parent = [];
        private readonly Dictionary<DrawioCell, (double X, double Y)> _origin = [];
        private readonly Dictionary<string, List<string>> _labels = new(StringComparer.Ordinal);
        private readonly HashSet<DrawioCell> _holders = [];
        private readonly List<DrawioCell> _vertices = [];
        private readonly Dictionary<DrawioCell, string> _missingParent = [];

        public List<DrawioCell> Edges { get; } = [];

        public int Hidden { get; private set; }

        public int VisibleLayers { get; private set; }

        public static Graph Build(IReadOnlyList<DrawioCell> cells, List<ImportWarning> warnings)
        {
            var graph = new Graph();
            var skippedDuplicates = new HashSet<DrawioCell>();
            foreach (DrawioCell cell in cells)
            {
                if (graph._byId.TryAdd(cell.Id, cell))
                {
                    continue;
                }

                if (cell.Vertex || cell.Edge)
                {
                    skippedDuplicates.Add(cell);
                    warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax,
                        $"The id '{cell.Id}' is used by more than one cell in the file; the first was imported and this later one ({NameOf(DrawioText.FromPlain(cell.Value), cell.Id)}) was skipped."));
                }
            }

            graph.Classify(cells, skippedDuplicates, warnings);
            return graph;
        }

        public CellKind Describe(string id)
        {
            if (!_byId.TryGetValue(id, out DrawioCell? cell))
            {
                return CellKind.Missing;
            }

            if (cell.Edge && !cell.Vertex)
            {
                return CellKind.Edge;
            }

            return cell.Vertex ? CellKind.HiddenOrSkipped : CellKind.Other;
        }

        /// <summary>The id of the parent a shape names that is not in the file, or null.</summary>
        public string? MissingParentOf(DrawioCell vertex)
        {
            return _missingParent.GetValueOrDefault(vertex);
        }

        public bool HoldsChildren(DrawioCell vertex)
        {
            return _holders.Contains(vertex);
        }

        public DrawioCell? ParentOf(DrawioCell vertex)
        {
            return _parent[vertex];
        }

        public List<string> LabelsOf(DrawioCell edge)
        {
            return _labels.TryGetValue(edge.Id, out List<string>? labels) ? labels : [];
        }

        /// <summary>The absolute position of the origin the cell's own coordinates are relative to: its parent shape's top-left, or 0,0 on the canvas.</summary>
        public (double X, double Y) OriginOf(DrawioCell cell)
        {
            if (cell.Parent is { } parentId && _byId.TryGetValue(parentId, out DrawioCell? parent) && _origin.TryGetValue(parent, out (double X, double Y) origin))
            {
                return origin;
            }

            return (0, 0);
        }

        /// <summary>The shape's absolute geometry, or null (with <paramref name="invalid"/> telling the two reasons apart) when it has none the canvas could hold.</summary>
        public Geometry? GeometryOf(DrawioCell vertex, DrawioStyle style, out bool invalid)
        {
            invalid = false;
            if (vertex.Geometry is not { } geometry)
            {
                return null;
            }

            (double x, double y) = _origin[vertex];
            double width = Math.Abs(geometry.Width);
            double height = Math.Abs(geometry.Height);
            if (Math.Abs(x) > DrawioLimits.MaxCoordinate || Math.Abs(y) > DrawioLimits.MaxCoordinate || width > DrawioLimits.MaxCoordinate || height > DrawioLimits.MaxCoordinate)
            {
                invalid = true;
                return null;
            }

            double rotation = style.Number("rotation") ?? 0;
            return new Geometry(x, y, width, height) { Rotation = rotation != 0 && Math.Abs(rotation) <= 36_000 ? rotation : null };
        }

        /// <summary>The kept shapes in file order, each after the shape it sits in, so a container is written (and drawn) before what it holds.</summary>
        public IEnumerable<DrawioCell> EmissionOrder()
        {
            var emitted = new HashSet<DrawioCell>();
            var pending = new Stack<DrawioCell>();
            foreach (DrawioCell vertex in _vertices)
            {
                for (DrawioCell? cell = vertex; cell is not null && !emitted.Contains(cell); cell = _parent[cell])
                {
                    pending.Push(cell);
                }

                while (pending.Count > 0)
                {
                    DrawioCell next = pending.Pop();
                    if (emitted.Add(next))
                    {
                        yield return next;
                    }
                }
            }
        }

        private void Classify(IReadOnlyList<DrawioCell> cells, HashSet<DrawioCell> skippedDuplicates, List<ImportWarning> warnings)
        {
            var layerHidden = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (DrawioCell cell in cells)
            {
                if (!cell.Vertex && !cell.Edge && _byId[cell.Id] == cell)
                {
                    layerHidden[cell.Id] = !cell.Visible;
                }
            }

            // Layers: the structural cells other than the root, which is the one with no parent.
            VisibleLayers = cells.Count(c => !c.Vertex && !c.Edge && c.Parent is not null && c.Visible);

            var vertices = new List<DrawioCell>();
            foreach (DrawioCell cell in cells)
            {
                if (skippedDuplicates.Contains(cell))
                {
                    continue;
                }

                if (cell.Vertex)
                {
                    if (cell.Parent is { } parentId && _byId.TryGetValue(parentId, out DrawioCell? parent) && parent.Edge && !parent.Vertex)
                    {
                        // An edge's own extra label: a cell of its own whose parent is the connector.
                        if (cell.Visible)
                        {
                            string text = cell.Style is not null && DrawioStyle.Parse(cell.Style).Flag("html") ? DrawioText.FromHtml(cell.Value) : DrawioText.FromPlain(cell.Value);
                            if (text.Length > 0)
                            {
                                if (!_labels.TryGetValue(parentId, out List<string>? list))
                                {
                                    _labels[parentId] = list = [];
                                }

                                list.Add(text.Length > DrawioLimits.MaxLabelLength ? text[..DrawioLimits.MaxLabelLength] : text);
                            }
                        }

                        continue;
                    }

                    vertices.Add(cell);
                }
                else if (cell.Edge)
                {
                    Edges.Add(cell);
                }
            }

            ResolveParents(vertices, layerHidden, warnings);
            Edges.RemoveAll(edge => IsHiddenEdge(edge, layerHidden));
        }

        private bool IsHiddenEdge(DrawioCell edge, Dictionary<string, bool> layerHidden)
        {
            bool hidden = !edge.Visible
                || (edge.Parent is { } parentId && _byId.TryGetValue(parentId, out DrawioCell? parent)
                    && (parent.Vertex ? !_kept.Contains(parent) : layerHidden.GetValueOrDefault(parent.Id)));
            if (hidden)
            {
                Hidden++;
            }

            return hidden;
        }

        /// <summary>
        /// Resolves every shape's parent (a shape inside a shape; anything else is the canvas) so that what remains is a forest, and computes the absolute
        /// origin of each, parents first. A chain is walked up once and unwound back down; if it runs into a cell already on the way up it is a cycle, and
        /// the shape that closes it becomes a top-level shape, with a warning. No shape is visited twice, so nesting 10,000 deep costs the same as flat.
        /// </summary>
        private void ResolveParents(List<DrawioCell> vertices, Dictionary<string, bool> layerHidden, List<ImportWarning> warnings)
        {
            var parentCell = new Dictionary<DrawioCell, DrawioCell?>();
            var vertexSet = vertices.ToHashSet();
            foreach (DrawioCell vertex in vertices)
            {
                DrawioCell? parent = null;
                if (vertex.Parent is { } id && _byId.TryGetValue(id, out DrawioCell? found) && vertexSet.Contains(found))
                {
                    parent = found;
                }
                else if (vertex.Parent is { } missing && !_byId.ContainsKey(missing))
                {
                    _missingParent[vertex] = missing;
                }

                parentCell[vertex] = parent;
            }

            var state = new Dictionary<DrawioCell, bool>(); // false while on the way up, true once its origin is known
            var hidden = new Dictionary<DrawioCell, bool>();
            foreach (DrawioCell start in vertices)
            {
                if (state.ContainsKey(start))
                {
                    continue;
                }

                var path = new List<DrawioCell>();
                DrawioCell? current = start;
                while (current is not null && !state.ContainsKey(current))
                {
                    state[current] = false;
                    path.Add(current);
                    current = parentCell[current];
                }

                if (current is not null && !state[current])
                {
                    DrawioCell closing = path[^1];
                    parentCell[closing] = null;
                    warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax,
                        $"{NameOf(DrawioText.FromPlain(closing.Value), closing.Id)} sits inside itself through a chain of parents; it was placed at the top level instead."));
                }

                for (int i = path.Count - 1; i >= 0; i--)
                {
                    DrawioCell cell = path[i];
                    DrawioCell? parent = parentCell[cell];
                    (double px, double py) = parent is null ? (0, 0) : _origin[parent];
                    DrawioGeometry? geometry = cell.Geometry;
                    _origin[cell] = (px + (geometry?.X ?? 0), py + (geometry?.Y ?? 0));
                    bool parentHidden = parent is null
                        ? cell.Parent is { } layer && _byId.TryGetValue(layer, out DrawioCell? layerCell) && !layerCell.Vertex && layerHidden.GetValueOrDefault(layerCell.Id)
                        : hidden[parent];
                    hidden[cell] = parentHidden || !cell.Visible;
                    state[cell] = true;
                }
            }

            foreach (DrawioCell vertex in vertices)
            {
                if (hidden[vertex])
                {
                    Hidden++;
                    continue;
                }

                _kept.Add(vertex);
                _vertices.Add(vertex);
                _parent[vertex] = parentCell[vertex];
            }

            // A shape whose parent was hidden is hidden with it, so parents of kept shapes are kept.
            foreach (DrawioCell vertex in _vertices)
            {
                if (_parent[vertex] is { } parent)
                {
                    _holders.Add(parent);
                }
            }
        }
    }
}
