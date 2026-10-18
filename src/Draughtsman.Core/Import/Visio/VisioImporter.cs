using System.Globalization;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import.Drawio;
using Draughtsman.Core.Models;

namespace Draughtsman.Core.Import.Visio;

/// <summary>
/// Imports a Visio (.vsdx) drawing into a <see cref="Document"/>: one page of however many it has. Shapes become nodes (<c>n_&lt;id&gt;</c>, a
/// container <c>c_&lt;id&gt;</c>) with their text, size and absolute position (Visio's inches, bottom-left origin and y axis pointing up become the
/// canvas's units and top-left, y down), and a kind guessed from the name of the master they came from and the outline they draw, flagged for
/// review where it was a judgement; groups become containers holding their children; connectors become edges between the shapes they are glued to,
/// with their text, line ends and routing. Positions arrive with the file, so they are carried as they are and the caller does not lay the diagram out.
/// <para>
/// What Visio has that the model has not is never a failure: a master with no equivalent is a plain box, named once with a count; a connector with
/// a loose end is left out under <see cref="ImportWarningCodes.MissingReference"/>; layers, shape data, themes, pictures and flips are each
/// <em>one</em> aggregated <see cref="ImportWarningCodes.UnsupportedSyntax"/> warning with a count. What is refused is the source itself, as a
/// <see cref="SourceRejectedException"/>: not a Visio file, a legacy .vsd, or past a limit (<see cref="VisioLimits"/>). The file is untrusted: it is
/// read by <see cref="VisioReader"/> over <see cref="VisioPackage"/>, nothing in it is ever fetched or extracted, and no regular expression reads it.
/// </para>
/// </summary>
public static class VisioImporter
{
    /// <summary>The largest .vsdx the importer reads, in bytes (the figure the endpoint and the MCP tool refuse above too).</summary>
    public const int MaxFileBytes = VisioLimits.MaxFileBytes;

    private const string DefaultTitle = "Imported diagram";
    private const string CreatedBy = "visio-import";

    /// <exception cref="ArgumentException">The type cannot hold a drawing (a sequence document), or the page is not one the file has.</exception>
    /// <exception cref="SourceRejectedException">The source is not a Visio file, is past a limit, or is a hostile package.</exception>
    public static ImportResult Import(byte[] file, VisioImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        VisioImportOptions chosen = options ?? new VisioImportOptions();
        if (chosen.Type == DocumentType.Sequence)
        {
            throw new ArgumentException("A sequence document is made from a Mermaid 'sequenceDiagram', not from a drawing. Use type 'flowchart', 'architecture' or 'freeform' for a Visio file.");
        }

        return SourceRejectedException.Guard("Visio", () => new Run(VisioReader.Read(file, chosen.Page), chosen).Execute());
    }

    /// <summary>Reads at most <see cref="MaxFileBytes"/> from <paramref name="file"/> (one more byte than that is a refusal), then imports it as the byte overload does.</summary>
    public static ImportResult Import(Stream file, VisioImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = file.Read(chunk, 0, (int)Math.Min(chunk.Length, MaxFileBytes + 1L - buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxFileBytes)
            {
                throw new SourceRejectedException($"The Visio file is larger than {MaxFileBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes, which is more than Draughtsman reads. Remove embedded pictures, or split the drawing into one file per page.");
            }
        }

        return Import(buffer.ToArray(), options);
    }

    /// <summary>A 2-D affine transform, <c>x' = a·x + c·y + e</c>, <c>y' = b·x + d·y + f</c>: how a shape's own space (inches, origin at its bottom-left) sits on its parent's.</summary>
    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static Affine Identity { get; } = new(1, 0, 0, 1, 0, 0);

        /// <summary>The transform of a shape: rotate by <paramref name="angle"/> (counter-clockwise, radians) about its local pin, and put the pin at (<paramref name="pinX"/>, <paramref name="pinY"/>) in the parent.</summary>
        public static Affine Of(double pinX, double pinY, double locPinX, double locPinY, double angle)
        {
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            return new Affine(cos, sin, -sin, cos, pinX - ((cos * locPinX) - (sin * locPinY)), pinY - ((sin * locPinX) + (cos * locPinY)));
        }

        /// <summary>This transform after <paramref name="inner"/>: a point goes through <paramref name="inner"/> first, then this.</summary>
        public Affine After(Affine inner)
        {
            return new Affine(
                (A * inner.A) + (C * inner.B),
                (B * inner.A) + (D * inner.B),
                (A * inner.C) + (C * inner.D),
                (B * inner.C) + (D * inner.D),
                (A * inner.E) + (C * inner.F) + E,
                (B * inner.E) + (D * inner.F) + F);
        }

        public (double X, double Y) Apply(double x, double y)
        {
            return ((A * x) + (C * y) + E, (B * x) + (D * y) + F);
        }

        /// <summary>The rotation the transform applies, counter-clockwise in radians.</summary>
        public double Rotation => Math.Atan2(B, A);
    }

    /// <summary>A shape on the page that is, or may become, a node (or, for a 1-D shape, an edge): its view, where it sits, and what it holds.</summary>
    private sealed class Item(VisioShape shape, VisioShapeView view, Affine world, Item? container)
    {
        public VisioShape Shape { get; } = shape;

        public VisioShapeView View { get; } = view;

        public Affine World { get; } = world;

        public Item? Container { get; } = container;

        /// <summary>True for a 1-D shape: a connector, which becomes an edge, not a node.</summary>
        public bool Connector { get; set; }

        /// <summary>A stencil group stood in for by one box: its parts are not imported.</summary>
        public bool Collapsed { get; set; }

        public int ImportedChildren { get; set; }

        public string? NodeId { get; set; }
    }

    /// <summary>What an import left out or could only partly do, counted so each kind is said once.</summary>
    private sealed class Summary
    {
        public Dictionary<string, List<string>> UnknownMasters { get; } = new(StringComparer.Ordinal);

        public int CustomOutlines { get; set; }

        public int Pictures { get; set; }

        public int ShapeData { get; set; }

        public int Themed { get; set; }

        public int Flipped { get; set; }

        public int HiddenByLayer { get; set; }

        public int NothingToDraw { get; set; }

        public int ThemedArrows { get; set; }

    }

    private sealed class Run(VisioFile file, VisioImportOptions options)
    {
        private readonly List<ImportWarning> _warnings = [];
        private readonly Summary _summary = new();
        private readonly List<Item> _nodes = [];
        private readonly List<Item> _connectors = [];
        private readonly Dictionary<string, Item> _itemsById = new(StringComparer.Ordinal);
        private readonly HashSet<string> _skippedIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Item> _alias = new(StringComparer.Ordinal);
        private readonly Dictionary<int, bool> _layerVisible = [];
        private readonly Dictionary<string, (string? Begin, string? End)> _glue = new(StringComparer.Ordinal);
        private int _unnamed;
        private const string LooseStart = "is not attached to a shape at its start";
        private const string LooseEnd = "is not attached to a shape at its end";

        private VisioPageInfo? Page => file.SelectedPage >= 0 ? file.Pages[file.SelectedPage] : null;

        public ImportResult Execute()
        {
            foreach (VisioLayer layer in Page?.Layers ?? [])
            {
                _layerVisible[layer.Index] = layer.Visible;
            }

            foreach (VisioConnect connect in file.Connects)
            {
                (string? begin, string? end) = _glue.GetValueOrDefault(connect.FromSheet);
                if (connect.FromCell is "BeginX" or "BeginY")
                {
                    _glue[connect.FromSheet] = (begin ?? connect.ToSheet, end);
                }
                else if (connect.FromCell is "EndX" or "EndY")
                {
                    _glue[connect.FromSheet] = (begin, end ?? connect.ToSheet);
                }
            }

            Walk(file.Shapes, null, Affine.Identity, null);

            var ids = new MermaidImporter.IdAllocator("Visio");
            var nodes = new List<Node>();
            double pageHeight = PageHeight();
            foreach (Item item in _nodes)
            {
                nodes.Add(BuildNode(item, pageHeight, ids));
            }

            var nodeKinds = nodes.ToDictionary(n => n.Id, n => n.Kind, StringComparer.Ordinal);
            var edges = new List<Edge>();
            foreach (Item connector in _connectors)
            {
                Edge? edge = BuildEdge(connector, ids, nodeKinds);
                if (edge is not null)
                {
                    edges.Add(edge);
                }
            }

            Summarise();
            if (nodes.Count == 0)
            {
                _warnings.Add(new ImportWarning(ImportWarningCodes.EmptyDiagram, file.Pages.Count == 0 ? "The file has no pages (a Visio stencil holds shapes, not a drawing)." : "The page has no shapes."));
            }

            PageNote();
            DateTimeOffset now = options.Now ?? DateTimeOffset.UtcNow;
            var document = new Document(
                $"doc_{Guid.NewGuid():N}",
                Document.CurrentSchemaVersion,
                options.Title ?? TitleOf() ?? DefaultTitle,
                options.Type,
                nodes,
                edges,
                new Meta(now, now, CreatedBy));

            return new ImportResult(document, DrawioImporter.CapWarnings(_warnings))
            {
                Pages = file.Pages.Select((p, i) => new ImportPage(i + 1, p.Name, i == file.SelectedPage)).ToList(),
            };
        }

        // ---- walking the page ----------------------------------------------------------------------------------------------------

        /// <summary>
        /// Visits the shapes of the page, parents before their children. A 1-D shape is a connector; a shape with no size or nothing to draw is
        /// counted and left out; a group that is an instance of a master nothing here knows is one box standing for the whole stencil shape; any
        /// other group is a container whose children are visited in its own space, so a child's position is the group's plus its own.
        /// </summary>
        private void Walk(IEnumerable<VisioShape> shapes, Item? container, Affine parentWorld, VisioMaster? parentMaster)
        {
            foreach (VisioShape shape in shapes)
            {
                if (shape.Type == "Guide")
                {
                    continue;
                }

                VisioShapeView view = VisioShapeView.Of(shape, parentMaster, file.Masters);
                if (shape.Id.Length > 0 && (_itemsById.ContainsKey(shape.Id) || _skippedIds.Contains(shape.Id)))
                {
                    _warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax,
                        $"The id '{VisioPackage.Printable(shape.Id)}' is used by more than one shape on the page; the first was imported and this later one ({DrawioImporter.NameOf(TextOf(view, out _), shape.Id)}) was skipped."));
                    continue;
                }

                if (IsHiddenByLayer(view))
                {
                    _summary.HiddenByLayer++;
                    Skip(shape);
                    continue;
                }

                double width = Math.Abs(view.Number("Width") ?? 0);
                double height = Math.Abs(view.Number("Height") ?? 0);
                Affine local = Affine.Of(
                    view.Number("PinX") ?? 0,
                    view.Number("PinY") ?? 0,
                    view.Number("LocPinX") ?? (view.Number("Width") ?? 0) / 2,
                    view.Number("LocPinY") ?? (view.Number("Height") ?? 0) / 2,
                    Finite(view.Number("Angle")));
                var item = new Item(shape, view, parentWorld.After(local), container);

                if (view.Has("BeginX") && view.Has("EndX"))
                {
                    item.Connector = true;
                    _connectors.Add(item);
                    Register(item);
                    continue;
                }

                bool hasText = !string.IsNullOrWhiteSpace(shape.Text);
                if (width <= 0 || height <= 0 || (!hasText && shape.Children.Count == 0 && !shape.IsForeign && VisioShapeMapper.Classify(view) == VisioOutline.None))
                {
                    // Nothing to draw (a helper with no outline and no text) or no size to draw it at.
                    _summary.NothingToDraw++;
                    Skip(shape);
                    if (shape.Children.Count > 0)
                    {
                        Walk(shape.Children, container, item.World, view.Master);
                    }

                    continue;
                }

                Register(item);
                _nodes.Add(item);
                if (container is not null)
                {
                    container.ImportedChildren++;
                }

                Count(item);
                if (shape.Children.Count == 0)
                {
                    continue;
                }

                bool stencilGroup = shape.Master is not null && !VisioShapeMapper.IsContainerName(view.Name) && shape.StructureType != "Container";
                if (stencilGroup)
                {
                    item.Collapsed = true;
                    AliasDescendants(shape, item);
                }
                else
                {
                    Walk(shape.Children, item, item.World, view.Master);
                }
            }
        }

        private void Register(Item item)
        {
            if (item.Shape.Id.Length > 0)
            {
                _itemsById[item.Shape.Id] = item;
            }
        }

        private void Skip(VisioShape shape)
        {
            if (shape.Id.Length > 0)
            {
                _skippedIds.Add(shape.Id);
            }
        }

        /// <summary>Glue to a part of a stencil group means the group's box, since that is the one node there is.</summary>
        private void AliasDescendants(VisioShape shape, Item item)
        {
            var pending = new Stack<VisioShape>(shape.Children);
            while (pending.Count > 0)
            {
                VisioShape child = pending.Pop();
                if (child.Id.Length > 0)
                {
                    _alias.TryAdd(child.Id, item);
                }

                foreach (VisioShape grandchild in child.Children)
                {
                    pending.Push(grandchild);
                }
            }
        }

        /// <summary>The shape's <c>LayerMember</c> names layers (<c>0</c>, <c>0;2</c>); it is hidden when every layer it is on is hidden.</summary>
        private bool IsHiddenByLayer(VisioShapeView view)
        {
            if (_layerVisible.Count == 0 || view.Cell("LayerMember") is not { Length: > 0 } members)
            {
                return false;
            }

            bool any = false;
            foreach (string member in members.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(member, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || !_layerVisible.TryGetValue(index, out bool visible))
                {
                    return false;
                }

                if (visible)
                {
                    return false;
                }

                any = true;
            }

            return any;
        }

        /// <summary>Tallies what a shape carries that the model has no place for, so it can be said once.</summary>
        private void Count(Item item)
        {
            VisioShapeView view = item.View;
            if (view.Shape.HasShapeData)
            {
                _summary.ShapeData++;
            }

            if (view.IsThemed("FillForegnd", "FillPattern", "LineColor", "LinePattern", "LineWeight"))
            {
                _summary.Themed++;
            }

            if (VisioXml.IsOn(view.Cell("FlipX")) || VisioXml.IsOn(view.Cell("FlipY")))
            {
                _summary.Flipped++;
            }
        }

        private static double Finite(double? value)
        {
            return value is { } v && double.IsFinite(v) ? v % (2 * Math.PI) : 0;
        }

        // ---- nodes -------------------------------------------------------------------------------------------------------------------

        /// <summary>The page's height in inches, which turns Visio's y axis (up from the bottom) into the canvas's (down from the top). A page that does not say is as tall as its content.</summary>
        private double PageHeight()
        {
            if (Page?.Height is { } declared && declared > 0 && double.IsFinite(declared))
            {
                return declared;
            }

            double top = 0;
            foreach (Item item in _nodes)
            {
                double w = Math.Abs(item.View.Number("Width") ?? 0);
                double h = Math.Abs(item.View.Number("Height") ?? 0);
                foreach ((double cornerX, double cornerY) in new[] { (0d, 0d), (w, 0d), (w, h), (0d, h) })
                {
                    top = Math.Max(top, item.World.Apply(cornerX, cornerY).Y);
                }
            }

            return top;
        }

        private Node BuildNode(Item item, double pageHeight, MermaidImporter.IdAllocator ids)
        {
            VisioShapeView view = item.View;
            VisioShape shape = item.Shape;
            string label = TextOf(view, out bool shortened);
            if (shortened)
            {
                _warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax,
                    $"The label of {DrawioImporter.NameOf(label, shape.Id)} was shortened to {VisioLimits.MaxLabelLength.ToString("N0", CultureInfo.InvariantCulture)} characters."));
            }

            VisioMapping mapping = VisioShapeMapper.Map(options.Type, view, item.ImportedChildren > 0, label.Length > 0, item.Collapsed ? OutlineChild(item) : null, item.Collapsed);
            DrawioShape drawn = mapping.Shape;
            string prefix = drawn.Kind == DrawioShapeMapper.ContainerKind ? "c_" : "n_";
            string id = ids.Allocate(prefix, shape.Id.Length > 0 ? shape.Id : $"shape-{(++_unnamed).ToString(CultureInfo.InvariantCulture)}", null, _warnings);
            item.NodeId = id;
            string name = DrawioImporter.NameOf(label, shape.Id);

            if (drawn.Guessed)
            {
                _warnings.Add(new ImportWarning(ImportWarningCodes.KindGuessed,
                    $"{name} is drawn as {drawn.Description}, so it was given the kind '{drawn.Kind}'; change it if that is not what it is.", null, id));
            }

            if (mapping.UnknownMaster is { } master)
            {
                if (!_summary.UnknownMasters.TryGetValue(master, out List<string>? samples))
                {
                    _summary.UnknownMasters[master] = samples = [];
                }

                samples.Add(label);
            }

            _summary.CustomOutlines += mapping.CustomOutline ? 1 : 0;
            _summary.Pictures += mapping.Picture ? 1 : 0;

            var notes = new List<string>();
            NodeStyle? style = DrawioStyleMapper.ForNode(VisioStyleMapper.NodeStyle(view, file.Palette), drawn.Kind == "text", notes.Add);
            foreach (string note in notes)
            {
                _warnings.Add(new ImportWarning(ImportWarningCodes.StyleApproximated, $"{name} has {note}.", null, id));
            }

            Geometry? geometry = GeometryOf(item, pageHeight);
            if (geometry is not null && drawn.Shape.StartsWith("custom:generic/", StringComparison.Ordinal))
            {
                geometry = DrawioImporter.GrownForIcon(geometry);
            }

            if (geometry is null)
            {
                _warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax, $"{name} is placed too far from the origin for a canvas, so it was given no position.", null, id));
            }

            return new Node(id, drawn.Kind, label, drawn.Shape)
            {
                Geometry = geometry,
                Style = style,
                Parent = item.Container?.NodeId,
            };
        }

        /// <summary>
        /// The part of a stencil group that draws its outline: its first child that draws something and covers (nearly) the group's whole box. Lucidchart
        /// writes every shape as a group of one such part with the text on the group, so the part is what says the shape is a diamond or an ellipse.
        /// </summary>
        private VisioShapeView? OutlineChild(Item item)
        {
            double w = Math.Abs(item.View.Number("Width") ?? 0);
            double h = Math.Abs(item.View.Number("Height") ?? 0);
            foreach (VisioShape child in item.Shape.Children)
            {
                VisioShapeView view = VisioShapeView.Of(child, item.View.Master, file.Masters);
                if (VisioShapeMapper.Classify(view) != VisioOutline.None || view.GeometryTruncated)
                {
                    bool covers = Math.Abs(view.Number("Width") ?? 0) >= w * 0.9 && Math.Abs(view.Number("Height") ?? 0) >= h * 0.9;
                    return covers ? view : null;
                }
            }

            return null;
        }

        /// <summary>
        /// The shape's box on the canvas: its centre (the shape's own centre carried through every group it sits in), its size, and the rotation
        /// those groups and the shape add up to, turned from Visio's counter-clockwise radians on a y-up page into the canvas's clockwise degrees
        /// on a y-down one. Null when it is placed too far from the origin for any canvas.
        /// </summary>
        private static Geometry? GeometryOf(Item item, double pageHeight)
        {
            double w = Math.Abs(item.View.Number("Width") ?? 0);
            double h = Math.Abs(item.View.Number("Height") ?? 0);
            (double cx, double cy) = item.World.Apply(w / 2, h / 2);
            double unit = VisioLimits.UnitsPerInch;
            double width = w * unit;
            double height = h * unit;
            double x = (cx * unit) - (width / 2);
            double y = ((pageHeight - cy) * unit) - (height / 2);
            if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > VisioLimits.MaxCoordinate || Math.Abs(y) > VisioLimits.MaxCoordinate || width > VisioLimits.MaxCoordinate || height > VisioLimits.MaxCoordinate)
            {
                return null;
            }

            double degrees = -item.World.Rotation * 180 / Math.PI;
            degrees = Math.Round(degrees, 2);
            if (degrees <= -180)
            {
                degrees += 360;
            }

            return new Geometry(Math.Round(x, 2), Math.Round(y, 2), Math.Round(width, 2), Math.Round(height, 2)) { Rotation = Math.Abs(degrees) < 0.005 ? null : degrees };
        }

        /// <summary>The label of a shape: its text with Visio's paragraph and line separators as line breaks, control characters dropped, cut to the label limit.</summary>
        private static string TextOf(VisioShapeView view, out bool shortened)
        {
            string text = (view.Text ?? string.Empty).Replace('\u2028', '\n').Replace('\u2029', '\n');
            string label = DrawioText.FromPlain(text);
            shortened = label.Length > VisioLimits.MaxLabelLength;
            return shortened ? label[..VisioLimits.MaxLabelLength] : label;
        }

        // ---- edges -------------------------------------------------------------------------------------------------------------------

        private Edge? BuildEdge(Item connector, MermaidImporter.IdAllocator ids, Dictionary<string, string> nodeKinds)
        {
            VisioShapeView view = connector.View;
            string label = TextOf(view, out _);
            string name = DrawioImporter.NameOf(label, connector.Shape.Id, "connector");

            (string? from, string? fromReason) = EndOf(connector, atStart: true);
            (string? to, string? toReason) = EndOf(connector, atStart: false);
            if (from is null || to is null)
            {
                string reasons = from is null && to is null && fromReason == LooseStart && toReason == LooseEnd
                    ? "is not attached to a shape at either end (a loose line in Visio), and a Draughtsman connector joins two shapes"
                    : string.Join(" and ", new[] { fromReason, toReason }.Where(r => r is not null).Select(r => r == LooseStart || r == LooseEnd ? $"{r} (a loose end in Visio), and a Draughtsman connector joins two shapes" : r));
                _warnings.Add(new ImportWarning(ImportWarningCodes.MissingReference, $"{name} (Visio id {VisioPackage.Printable(connector.Shape.Id)}) {reasons}, so it was left out."));
                return null;
            }

            string id = ids.Allocate("e_", connector.Shape.Id.Length > 0 ? connector.Shape.Id : "connector", null, _warnings);
            var notes = new List<string>();
            ArrowHead start = VisioStyleMapper.Head(view.StyleCell("BeginArrow"), atStart: true, out _, notes.Add);
            ArrowHead end = VisioStyleMapper.Head(view.StyleCell("EndArrow"), atStart: false, out bool endResolved, notes.Add);
            if (!endResolved && view.IsMasterInstance)
            {
                // A connector dropped from Visio's own connector master draws the arrowhead its theme asks for, which is not read: an arrow at the end,
                // as the default connector of a flowchart has, and said once.
                end = ArrowHead.Arrow;
                _summary.ThemedArrows++;
            }

            DrawioStyle drawioStyle = VisioStyleMapper.EdgeStyle(view, file.Palette);
            bool dashed = drawioStyle.Flag("dashed");
            string kind = EdgeKindFor(options.Type, dashed, start, end, nodeKinds[from], nodeKinds[to]);
            EdgeStyle? edgeStyle = DrawioStyleMapper.ForEdge(drawioStyle, kind, start, end, notes.Add);
            foreach (string note in notes)
            {
                _warnings.Add(new ImportWarning(ImportWarningCodes.StyleApproximated, $"{name} has {note}.", null, id));
            }

            return new Edge(id, kind, new Endpoint(from), new Endpoint(to))
            {
                Label = label.Length == 0 ? null : label,
                Routing = new Routing(RoutingOf(view)),
                Style = edgeStyle,
            };
        }

        /// <summary>The node id a connector end is glued to, or null and the reason it is not (a loose end, a shape that is not on the page, another connector, a shape that was left out).</summary>
        private (string? NodeId, string? Reason) EndOf(Item connector, bool atStart)
        {
            string which = atStart ? "start" : "end";
            (string? begin, string? end) = _glue.GetValueOrDefault(connector.Shape.Id);
            string? toSheet = atStart ? begin : end;
            if (string.IsNullOrEmpty(toSheet))
            {
                return (null, atStart ? LooseStart : LooseEnd);
            }

            if (_itemsById.TryGetValue(toSheet, out Item? target))
            {
                if (target.Connector)
                {
                    return (null, $"is attached at its {which} to another connector, which Draughtsman cannot attach to");
                }

                if (target.NodeId is { } nodeId)
                {
                    return (nodeId, null);
                }
            }

            if (_alias.TryGetValue(toSheet, out Item? whole) && whole.NodeId is { } wholeId)
            {
                return (wholeId, null);
            }

            if (_skippedIds.Contains(toSheet) || _itemsById.ContainsKey(toSheet))
            {
                return (null, $"is attached at its {which} to a shape that was hidden or left out");
            }

            return (null, $"is attached at its {which} to a shape, '{VisioPackage.Printable(toSheet)}', that is not on the page");
        }

        private static string EdgeKindFor(DocumentType type, bool dashed, ArrowHead start, ArrowHead end, string fromKind, string toKind)
        {
            if ((start == ArrowHead.None && end == ArrowHead.None) || DrawioStyleMapper.IsNotation(start) || DrawioStyleMapper.IsNotation(end))
            {
                return "association";
            }

            if (type == DocumentType.Architecture)
            {
                if (dashed)
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
        /// How Visio routes the connector: curved or straight when its settings say so, centre-to-centre (<c>ShapeRouteStyle</c> 16) as straight, and
        /// otherwise right-angled, which is what a dynamic connector does. Like draw.io's, an orthogonal connector keeps no bends: the editor routes it
        /// from the shapes, and a straight or curved one runs between the shapes it joins.
        /// </summary>
        private static RoutingMode RoutingOf(VisioShapeView view)
        {
            double extension = view.Number("ConLineRouteExt") ?? 0;
            if (extension == 2)
            {
                return RoutingMode.Curved;
            }

            if (extension == 1 || view.Number("ShapeRouteStyle") == 16)
            {
                return RoutingMode.Straight;
            }

            return view.Number("ShapeRouteStyle") is > 0 || view.IsMasterInstance ? RoutingMode.Orthogonal : RoutingMode.Straight;
        }

        // ---- what could not be carried -----------------------------------------------------------------------------------------------

        /// <summary>One warning per kind of thing left out, each with its count, so a drawing of three hundred stencil shapes does not answer with three hundred lines.</summary>
        private void Summarise()
        {
            foreach ((string master, List<string> labels) in _summary.UnknownMasters)
            {
                string examples = string.Join(", ", labels.Where(l => l.Length > 0).Distinct().Take(3).Select(l => $"\"{(l.Length > 30 ? l[..30] + "..." : l).Replace('\n', ' ')}\""));
                string eg = examples.Length > 0 ? $" (for example {examples})" : string.Empty;
                string name = VisioPackage.Printable(master);
                Say(labels.Count,
                    $"{{n}} shape is an instance of the Visio master '{name}' (a stencil shape), which has no equivalent here, so it is drawn as a plain box with its text{eg}.",
                    $"{{n}} shapes are instances of the Visio master '{name}' (a stencil shape), which has no equivalent here, so they are drawn as plain boxes with their text{eg}.");
            }

            Say(_summary.CustomOutlines,
                "{n} shape has a custom outline (not a box, ellipse, diamond, parallelogram or hexagon), so it is drawn as a plain box.",
                "{n} shapes have a custom outline (not a box, ellipse, diamond, parallelogram or hexagon), so they are drawn as plain boxes.");
            Say(_summary.Pictures,
                "{n} picture or embedded object was not imported (pictures never are); it is drawn as a plain box.",
                "{n} pictures or embedded objects were not imported (pictures never are); they are drawn as plain boxes.");
            Say(_summary.ShapeData,
                "{n} shape carries shape data (custom properties), which is not imported.",
                "{n} shapes carry shape data (custom properties), which is not imported.");
            Say(_summary.Themed,
                "{n} shape takes its colours or line from a Visio theme. The colours Visio worked out for the page are kept; theme effects, shadows and theme fonts are not carried.",
                "{n} shapes take their colours or line from a Visio theme. The colours Visio worked out for the page are kept; theme effects, shadows and theme fonts are not carried.");
            Say(_summary.Flipped,
                "{n} shape is flipped in Visio. Flips are not carried, so it is drawn unflipped (the parts of a flipped group may sit in the wrong places).",
                "{n} shapes are flipped in Visio. Flips are not carried, so they are drawn unflipped (the parts of a flipped group may sit in the wrong places).");
            Say(_summary.NothingToDraw,
                "{n} shape has no size, or nothing to draw (no outline and no text), and was left out.",
                "{n} shapes have no size, or nothing to draw (no outline and no text), and were left out.");
            Say(_summary.ThemedArrows,
                "{n} connector draws its arrowhead from Visio's theme, which is not read, so it was given an arrow at its end.",
                "{n} connectors draw their arrowheads from Visio's theme, which is not read, so each was given an arrow at its end.");
            Say(_summary.HiddenByLayer,
                "{n} shape or connector on a hidden layer was left out.",
                "{n} shapes or connectors on hidden layers were left out.");

            int layers = Page?.Layers.Count ?? 0;
            if (layers > 1)
            {
                _warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax,
                    $"The page has {Count(layers)} layers. Draughtsman has no layers, so their shapes all sit on one canvas, in the order Visio drew them."));
            }

            if (Page is { BackPage: { Length: > 0 } back })
            {
                string named = file.Pages.FirstOrDefault(p => p.Id == back) is { Name.Length: > 0 } backPage ? $" (\"{VisioPackage.Printable(backPage.Name)}\")" : string.Empty;
                _warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax, $"The page sits on a background page{named}, whose shapes are not imported."));
            }
        }

        private void Say(int count, string one, string many)
        {
            if (count > 0)
            {
                _warnings.Add(new ImportWarning(ImportWarningCodes.UnsupportedSyntax, (count == 1 ? one : many).Replace("{n}", Count(count), StringComparison.Ordinal)));
            }
        }

        private static string Count(int count)
        {
            return count.ToString("N0", CultureInfo.InvariantCulture);
        }

        private void PageNote()
        {
            if (file.Pages.Count <= 1)
            {
                return;
            }

            VisioPageInfo page = file.Pages[file.SelectedPage];
            int others = file.Pages.Count - 1;
            string named = page.Name.Length > 0 ? $" (\"{VisioPackage.Printable(page.Name)}\")" : string.Empty;
            _warnings.Insert(0, new ImportWarning(ImportWarningCodes.PageSelected,
                $"Page {(file.SelectedPage + 1).ToString(CultureInfo.InvariantCulture)}{named} of {file.Pages.Count.ToString(CultureInfo.InvariantCulture)} was imported; the other {others.ToString(CultureInfo.InvariantCulture)} pages were not. Choose another page to import it instead."));
        }

        /// <summary>The page's name, unless it is Visio's own <c>Page-1</c>, which says nothing.</summary>
        private string? TitleOf()
        {
            if (Page is null)
            {
                return null;
            }

            string name = Page.Name.Trim();
            bool placeholder = name.StartsWith("Page-", StringComparison.Ordinal) && name.Length > 5 && name.AsSpan(5).IndexOfAnyExceptInRange('0', '9') < 0;
            return name.Length == 0 || placeholder ? null : VisioPackage.Printable(name);
        }
    }
}
