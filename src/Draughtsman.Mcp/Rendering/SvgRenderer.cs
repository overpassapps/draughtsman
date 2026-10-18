using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Sidecar;

namespace Draughtsman.Mcp.Rendering;

/// <summary>
/// A minimal, self-contained SVG serialiser for <c>render_document</c>: node shapes, labels and edges with
/// arrowheads, from the sparse <see cref="NodeStyle"/> / <see cref="EdgeStyle"/> a document already carries (its
/// own colours where set, plain neutral defaults otherwise). It draws the model faithfully but does not resolve the
/// diagram theme's tokens the way <c>web/src/theme/resolveStyle.ts</c> and the canvas do inside the drawing code itself: <see cref="Render"/>
/// resolves the shipped theme through <see cref="DiagramTheme"/> first, for every document type, so what is drawn here carries resolved styles. Every node passed in must
/// already have <see cref="Node.Geometry"/>; the caller is responsible for laying the document out first.
/// </summary>
public static partial class SvgRenderer
{
    private const double Padding = 32;
    private const string DefaultFill = "#ffffff";
    private const string DefaultStroke = "#334155";
    private const string DefaultText = "#0f172a";
    private const string BackgroundFill = "#f8fafc";

    // An edge label's plate: an opaque rect behind the text (checklist item 11 / M2 gap-audit Finding 4) sized from
    // this renderer's own character-width estimate (it has no font metrics to measure against, see the class
    // comment), plus a small pad so the plate clears the glyphs on every side.
    private const double LabelCharWidth = 7;
    private const double LabelPadX = 4;
    private const double LabelHeight = 16;
    private const double LabelNudgeStep = 4;
    private const int LabelNudgeMaxSteps = 60;

    // A generic icon node's layout, mirroring web/src/canvas/shapes.tsx: the label sits in the bottom band and the icon
    // fills a square in the band above it, drawn on the library's own viewBox grid.
    private const double IconLabelBand = 0.7;
    private const double IconSizeRatio = 0.8;

    // A boundary icon (VPC, subnet, availability zone) draws as a labelled box with a corner glyph, mirroring
    // web/src/canvas/shapes.tsx's boundaryRender; the label is top-left, as the canvas places a container's label.
    private const double BoundaryIconRatio = 0.22;
    private const double BoundaryIconMax = 32;
    private const double BoundaryIconInset = 8;
    private const double BoundaryLabelPadding = 8;
    private const double BoundaryLabelOffset = 15;

    /// <summary>The freeform vocabulary's container kind (Schemas/freeform.json); the canvas draws its label top-left.</summary>
    private const string ContainerKind = "container";

    // A state-machine shape's native geometry, mirroring web/src/canvas/shapes.tsx's stateShapeRender. A pseudostate
    // glyph is a fraction of the node's shorter side; the fork/join bar spans the width with a capped thickness; the
    // composite state is a rounded boundary with a name compartment at the top.
    private const double StateInitialDot = 0.22;
    private const double StateFinalRing = 0.3;
    private const double StateFinalDot = 0.15;
    private const double StateHistoryRadius = 0.28;
    private const double StatePointRadius = 0.24;
    private const double StateHFont = 0.34;
    private const double StateBarFraction = 0.16;
    private const double StateBarMax = 14;
    private const double StateNameBandFraction = 0.35;
    private const double StateNameBandMax = 28;
    private const double StateRoundedRadius = 8;

    /// <summary>The thickness of the fork/join bar, so it stays a bar however tall the node is resized. Mirrors shapes.tsx.</summary>
    private static double StateBarThickness(double h) => Math.Min(h * StateBarFraction, StateBarMax);

    /// <summary>The height of a composite state's name compartment (also used to anchor its label). Mirrors shapes.tsx.</summary>
    private static double StateNameBand(double h) => Math.Min(h * StateNameBandFraction, StateNameBandMax);

    /// <summary>Rounds a state shape's coordinate to two decimals, the same as shapes.tsx's `fmt`, so both renderers emit the same attribute strings.</summary>
    private static double R2(double value) => Math.Round(value * 100) / 100;

    private static double LabelWidth(string label) => label.Length * LabelCharWidth + LabelPadX * 2;

    /// <summary>
    /// Renders the document as a standalone SVG document. Throws <see cref="InvalidOperationException"/> if any node lacks geometry.
    /// <paramref name="routes"/> are the orthogonal connectors' paths, by edge id, in document coordinates: this renderer
    /// has no router of its own, so the caller gets them from the sidecar (<c>ISidecarClient.RouteAsync</c>, which runs
    /// the editor's router) and an orthogonal edge with no entry here draws straight, as it does with no routes at all.
    /// A route wins over nothing: an edge that carries waypoints is never routed, so it never has an entry.
    /// A <c>sequence</c> document stores no positions, so it is drawn from <paramref name="sequence"/>, the scene the
    /// sidecar derives with the editor's own function (<c>ISidecarClient.SequenceSceneAsync</c>); without one it is refused.
    /// <paramref name="theme"/> is the organisation's default diagram theme, when it has one; otherwise the shipped Overpass theme draws.
    /// </summary>
    public static SvgRenderResult Render(Document document, double scale = 1, Func<string, AssetImage?>? assets = null, IReadOnlyDictionary<string, IReadOnlyList<Point>>? routes = null, SequenceScene? sequence = null, DiagramThemeChoice? theme = null)
    {
        Rendered rendered = RenderCore(document, scale, assets, routes, sequence, theme);
        return new SvgRenderResult(rendered.Svg, rendered.Warnings);
    }

    /// <summary>A finished render and what the thumbnail wrapper (<c>SvgRenderer.Thumbnail.cs</c>) needs to reuse it: the picture's size, its paper, and where its body starts (after the root tag).</summary>
    private sealed record Rendered(string Svg, IReadOnlyList<string> Warnings, double Width, double Height, string Background, int BodyStart);

    private static Rendered RenderCore(Document document, double scale, Func<string, AssetImage?>? assets, IReadOnlyDictionary<string, IReadOnlyList<Point>>? routes, SequenceScene? sequence, DiagramThemeChoice? theme)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (scale is <= 0 or double.NaN || double.IsInfinity(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be positive and finite.");
        }

        RequireUniqueIds(document.Nodes.Select(n => n.Id), "node");
        RequireUniqueIds(document.Edges.Select(e => e.Id), "edge");

        IReadOnlyDictionary<string, (double X, double Y)>? sequenceLabels = null;
        // Every document type takes the shipped diagram theme's tokens (kind defaults, the document's own overrides, then each
        // element's sparse style over them), so a server-side picture, and the PDF printed from it, matches the client's export.
        (Document themed, SurfacePaint surface) = ApplyDiagramTheme(document, theme);
        Document sequenceSource = themed;
        document = themed;
        if (document.Type == DocumentType.Sequence)
        {
            if (sequence is null)
            {
                throw new InvalidOperationException("Cannot render a sequence diagram without its derived scene: it stores no positions, so the layout comes from the sidecar (sequence.scene).");
            }

            (document, routes, sequenceLabels) = ApplySequenceScene(sequenceSource, sequence);
        }

        Node[] missing = document.Nodes.Where(n => n.Geometry is null).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Cannot render: {missing.Length} node(s) have no geometry ({string.Join(", ", missing.Select(n => n.Id))}). Lay the document out first.");
        }

        (double minX, double minY, double maxX, double maxY) = sequence?.Bounds is { } sceneBounds && document.Type == DocumentType.Sequence
            ? (sceneBounds.X, sceneBounds.Y, sceneBounds.X + sceneBounds.W, sceneBounds.Y + sceneBounds.H)
            : Bounds(document, routes);
        double width = Math.Max(maxX - minX, 1);
        double height = Math.Max(maxY - minY, 1);
        var svg = new StringBuilder();
        svg.Append(Inv($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" width=\"{width * scale}\" height=\"{height * scale}\">\n"));
        int bodyStart = svg.Length;
        svg.Append(Inv($"<rect x=\"0\" y=\"0\" width=\"{width}\" height=\"{height}\" fill=\"{Escape(surface.Background)}\"/>\n"));
        Dictionary<string, string> markers = MarkerSuffixes(document);
        AppendMarkers(svg, markers);

        Dictionary<string, Node> byId = document.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var parentIds = new HashSet<string>(document.Nodes.Where(n => n.Parent is not null).Select(n => n.Parent!), StringComparer.Ordinal);
        if (document.Type == DocumentType.Sequence && sequence is not null)
        {
            AppendSequenceLayer(svg, sequenceSource, sequence, surface, minX, minY);
        }

        foreach (Node node in OrderForDrawing(document.Nodes))
        {
            AppendNode(svg, node, minX, minY, assets, node.Kind == ContainerKind || parentIds.Contains(node.Id));
        }

        IReadOnlyDictionary<string, (double X, double Y)> labelPositions = sequenceLabels is null
            ? PlaceEdgeLabels(document, byId, minX, minY, routes)
            : sequenceLabels.ToDictionary(pair => pair.Key, pair => (pair.Value.X - minX, pair.Value.Y - minY), StringComparer.Ordinal);
        IReadOnlyDictionary<string, double> portOffsets = EdgeAttachment.SharedPortOffsets(document.Edges);
        var warnings = new List<string>();
        foreach (Edge edge in document.Edges)
        {
            if (AppendEdge(svg, edge, byId, minX, minY, markers, labelPositions, routes, portOffsets, surface) is { } dropped)
            {
                warnings.Add(dropped);
            }
        }

        svg.Append("</svg>\n");
        return new Rendered(svg.ToString(), warnings, width, height, surface.Background, bodyStart);
    }

    // The document schema and the REST write path do not yet enforce id uniqueness, so a document stored through
    // POST /api/documents can reach here with two nodes or edges sharing an id. Fail with a message the caller can act on
    // rather than an opaque dictionary error.
    private static void RequireUniqueIds(IEnumerable<string> ids, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            if (!seen.Add(id))
            {
                throw new InvalidOperationException($"Cannot render: the document contains more than one {what} with id '{id}'.");
            }
        }
    }

    /// <summary>Containers (nodes something else parents to) are drawn first so children paint on top of them.</summary>
    private static IEnumerable<Node> OrderForDrawing(IReadOnlyList<Node> nodes)
    {
        Dictionary<string, Node> byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        return nodes.OrderBy(node =>
        {
            var ancestors = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            int depth = 0;
            while (node.Parent is { } parent && byId.TryGetValue(parent, out Node? ancestor) && ancestors.Add(parent))
            {
                depth++;
                node = ancestor;
            }

            return depth;
        });
    }

    private static (double, double, double, double) Bounds(Document document, IReadOnlyDictionary<string, IReadOnlyList<Point>>? routes)
    {
        IReadOnlyList<Node> nodes = document.Nodes;
        if (nodes.Count == 0)
        {
            return (0, 0, 200, 120);
        }

        IReadOnlyDictionary<string, double> portOffsets = EdgeAttachment.SharedPortOffsets(document.Edges);

        var boxes = nodes.Select(node =>
        {
            Geometry g = node.Geometry!;
            return RotatedExtent(g.X + g.W / 2, g.Y + g.H / 2, g.W / 2, g.H / 2, g.Rotation ?? 0);
        }).ToArray();
        double minX = boxes.Min(b => b.Left);
        double minY = boxes.Min(b => b.Top);
        double maxX = boxes.Max(b => b.Right);
        double maxY = boxes.Max(b => b.Bottom);
        foreach (Node node in nodes)
        {
            if (string.IsNullOrEmpty(node.Label))
            {
                continue;
            }

            // The label is drawn inside the node's rotate() group, so its extent turns with the node.
            Geometry g = node.Geometry!;
            var label = RotatedExtent(g.X + g.W / 2, g.Y + g.H / 2, node.Label.Length * 3.5, 8, g.Rotation ?? 0);
            minX = Math.Min(minX, label.Left);
            maxX = Math.Max(maxX, label.Right);
            minY = Math.Min(minY, label.Top);
            maxY = Math.Max(maxY, label.Bottom);
        }

        foreach (Node node in nodes)
        {
            // A BPMN event, gateway or data shape draws its label in a band below the outline, outside the node's box.
            if (!BpmnShapeLibrary.TryGet(node.Shape, out BpmnShape? bpmn) || !BpmnShapeLibrary.LabelOutside(bpmn!))
            {
                continue;
            }

            Geometry g = node.Geometry!;
            BpmnLayout layout = BpmnShapeLibrary.Layout;
            double labelWidth = Math.Max(g.W, layout.LabelMinWidth);
            (double Left, double Top, double Right, double Bottom) extent = g.Rotation is null or 0
                ? (g.X - ((labelWidth - g.W) / 2), g.Y, g.X + ((labelWidth + g.W) / 2), g.Y + g.H + layout.LabelGap + layout.LabelBand)
                : RotatedExtent(g.X + g.W / 2, g.Y + g.H / 2, labelWidth / 2, (g.H / 2) + layout.LabelGap + layout.LabelBand, g.Rotation ?? 0);
            minX = Math.Min(minX, extent.Left);
            maxX = Math.Max(maxX, extent.Right);
            minY = Math.Min(minY, extent.Top);
            maxY = Math.Max(maxY, extent.Bottom);
        }

        Dictionary<string, Node> byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (Edge edge in document.Edges)
        {
            if (!byId.TryGetValue(edge.From.Node, out Node? from) || !byId.TryGetValue(edge.To.Node, out Node? to))
            {
                continue;
            }

            EdgeGeometry geometry = GetEdgeGeometry(edge, from, to, 0, 0, routes, portOffsets);
            var curveExtent = edge.Routing?.Mode == RoutingMode.Curved && geometry.Points.Count > 2
                ? CurveControlPoints(geometry.Points)
                : [];
            foreach (var point in geometry.Points.Append(geometry.Control1).Append(geometry.Control2).Concat(curveExtent))
            {
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }

            if (!string.IsNullOrEmpty(edge.Label))
            {
                // A nudged label (PlaceEdgeLabels) never leaves its own segment, so padding every polyline point by
                // the plate's half-extent - rather than just the unnudged midpoint - covers every position it could
                // land at, wherever along the edge that turns out to be.
                double halfWidth = LabelWidth(edge.Label) / 2;
                const double halfHeight = LabelHeight / 2;
                foreach ((double X, double Y) point in geometry.Points)
                {
                    minX = Math.Min(minX, point.X - halfWidth);
                    maxX = Math.Max(maxX, point.X + halfWidth);
                    minY = Math.Min(minY, point.Y - halfHeight);
                    maxY = Math.Max(maxY, point.Y + halfHeight);
                }
            }
        }

        return (minX - Padding, minY - Padding, maxX + Padding, maxY + Padding);
    }

    /// <summary>The axis-aligned box a half-width by half-height rectangle centred on (cx, cy) occupies once rotated.</summary>
    private static (double Left, double Top, double Right, double Bottom) RotatedExtent(double cx, double cy, double halfWidth, double halfHeight, double rotation)
    {
        double radians = rotation * Math.PI / 180;
        double projectedWidth = Math.Abs(Math.Cos(radians)) * halfWidth + Math.Abs(Math.Sin(radians)) * halfHeight;
        double projectedHeight = Math.Abs(Math.Sin(radians)) * halfWidth + Math.Abs(Math.Cos(radians)) * halfHeight;
        return (cx - projectedWidth, cy - projectedHeight, cx + projectedWidth, cy + projectedHeight);
    }

    private static void AppendNode(StringBuilder svg, Node node, double offsetX, double offsetY, Func<string, AssetImage?>? assets, bool container)
    {
        Geometry g = node.Geometry!;
        double x = g.X - offsetX;
        double y = g.Y - offsetY;
        string fill = node.Style?.Fill ?? DefaultFill;
        string stroke = node.Style?.Stroke ?? DefaultStroke;
        // A text colour the document names is kept; otherwise the default, unless it cannot be read on this fill (QA-06).
        string text = node.Style?.TextColor ?? ReadableText.On(DefaultText, fill);
        double strokeWidth = node.Style?.StrokeWidth ?? 1.5;
        string dash = DashArray(node.Style?.Dash);

        string transform = g.Rotation is { } rotation && rotation != 0
            ? Inv($" transform=\"rotate({rotation} {x + g.W / 2} {y + g.H / 2})\"")
            : "";
        svg.Append(Inv($"<g data-id=\"{Escape(node.Id)}\"{transform}>\n"));
        AssetImage? image = node.Shape == "image" && AssetIdOf(node) is { } assetId ? assets?.Invoke(assetId) : null;
        if (image is not null)
        {
            svg.Append(ImageMarkup(x, y, g.W, g.H, image));
        }
        else if (UmlShapeLibrary.TryGet(node.Shape, out UmlShape? uml))
        {
            svg.Append(UmlMarkup(uml!, node, x, y, g.W, g.H, fill, stroke, strokeWidth, dash, text));
        }
        else if (ErShapeLibrary.TryGet(node.Shape, out ErShape? er))
        {
            svg.Append(ErMarkup(er!, node, x, y, g.W, g.H, fill, stroke, strokeWidth, dash, text));
        }
        else
        {
            svg.Append(ShapeMarkup(node.Shape, x, y, g.W, g.H, fill, stroke, strokeWidth, dash, text));
        }

        if (UmlShapeLibrary.TryGet(node.Shape, out UmlShape? umlLabelled))
        {
            // A class or interface names itself inside its own name compartment (UmlMarkup); a lollipop or socket
            // labels itself centred in the band under its symbol.
            if (!umlLabelled!.IsCompartments)
            {
                double band = UmlShapeLibrary.Metrics.SymbolBand;
                AppendNodeLabel(svg, node.Label, x, y, new LabelRect(0, g.H * band, g.W, g.H * (1 - band)), text, false);
            }
        }
        else if (ErShapeLibrary.IsErShape(node.Shape))
        {
            // An entity names itself inside its own name band (ErMarkup), like a class; nothing more to add.
        }
        else if (node.Shape == ActorShape)
        {
            // An actor's name sits under its stick figure, centred in what is left of the header.
            AppendNodeLabel(svg, node.Label, x, y, new LabelRect(0, ActorFigureHeight, g.W, g.H - ActorFigureHeight), text, false);
        }
        else if (StateShapeLibrary.IsStateShape(node.Shape))
        {
            // A state pseudostate hides its label; only the composite state names itself, in the top compartment.
            if (node.Shape == StateShapeLibrary.Prefix + "composite-state")
            {
                AppendNodeLabel(svg, node.Label, x, y, new LabelRect(0, 0, g.W, StateNameBand(g.H)), text, false);
            }
        }
        else if (BasicShapeLibrary.TryGet(node.Shape, out _))
        {
            // A basic shape names its own label rectangle (basic-shapes.json). A leaf centres the label in it; a container (a folder
            // with children, say) names itself top-left in it, as the canvas draws a container's label.
            AppendNodeLabel(svg, node.Label, x, y, NodeLabelLayout.RectFor(node.Shape, g.W, g.H), text, container);
        }
        else if (GenericIconLibrary.TryGet(node.Shape, out GenericIcon? generic) && generic!.Boundary)
        {
            // A boundary icon is a container's outline: the label sits top-left (as the canvas draws a container
            // label), leaving the corner glyph clear, rather than in the bottom band a leaf icon uses.
            AppendNodeLabel(svg, node.Label, x, y, new LabelRect(0, 0, g.W, g.H), text, true);
        }
        else if (OrgShapeLibrary.TryGet(node.Shape, out _))
        {
            // An org box's label is centred in the box; the assistant's sits clear of its left spine, a double border's inside it.
            AppendNodeLabel(svg, node.Label, x, y, NodeLabelLayout.RectFor(node.Shape, g.W, g.H), text, false);
        }
        else if (BpmnShapeLibrary.TryGet(node.Shape, out _))
        {
            AppendNodeLabel(svg, node.Label, x, y, NodeLabelLayout.RectFor(node.Shape, g.W, g.H), text, false);
        }
        else if (SwimlaneShapes.IsSwimlane(node.Shape))
        {
            // A pool or lane is a container: its label sits top-left, inside the title band (as the canvas draws it).
            AppendNodeLabel(svg, node.Label, x, y, new LabelRect(0, 0, g.W, g.H), text, true);
        }
        else if (container && !GenericIconLibrary.IsGenericShape(node.Shape))
        {
            // A container (kind `container`, or any node something else parents to) names itself top-left in its
            // header band, as the canvas draws it (NodeView.tsx Label), not centred over the rows it holds.
            AppendNodeLabel(svg, node.Label, x, y, new LabelRect(0, 0, g.W, g.H), text, true);
        }
        else
        {
            // Every other shape centres its label in the rectangle the canvas keeps clear of the outline (shapes.tsx labelBox).
            AppendNodeLabel(svg, node.Label, x, y, NodeLabelLayout.RectFor(node.Shape, g.W, g.H), text, false);
        }

        svg.Append("</g>\n");
    }

    private static string ShapeMarkup(string shape, double x, double y, double w, double h, string fill, string stroke, double strokeWidth, string dash, string text)
    {
        if (SwimlaneShapes.IsSwimlane(shape))
        {
            return SwimlaneMarkup(shape, x, y, w, h, fill, stroke, strokeWidth, dash);
        }

        if (shape == ActorShape)
        {
            return ActorMarkup(x, y, w, h, fill, stroke, strokeWidth);
        }

        if (StateShapeLibrary.IsStateShape(shape))
        {
            return StateShapeMarkup(shape, x, y, w, h, fill, stroke, strokeWidth, text);
        }

        if (BasicShapeLibrary.TryGet(shape, out BasicShape? basic))
        {
            return BasicMarkup(basic!, x, y, w, h, fill, stroke, strokeWidth, dash);
        }

        if (OrgShapeLibrary.TryGet(shape, out OrgShape? org))
        {
            return OrgMarkup(org!, x, y, w, h, fill, stroke, strokeWidth, dash);
        }

        if (BpmnShapeLibrary.TryGet(shape, out BpmnShape? bpmn))
        {
            return BpmnMarkup(bpmn!, x, y, w, h, fill, stroke, strokeWidth, dash);
        }

        if (GenericIconLibrary.TryGet(shape, out GenericIcon? icon))
        {
            return icon!.Boundary
                ? BoundaryMarkup(icon, x, y, w, h, fill, stroke, strokeWidth, dash)
                : IconMarkup(icon, x, y, w, h, stroke, strokeWidth);
        }

        string common = Inv($"fill=\"{Escape(fill)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}");
        return shape switch
        {
            "document" => Inv($"<path d=\"{DocumentOutline(x, y, w, h)}\" {common}/>\n"),
            "predefined-process" => Inv($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" {common}/><path d=\"M{x + w * 0.12},{y} V{y + h} M{x + w * 0.88},{y} V{y + h}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n"),
            "manual-input" => Inv($"<polygon points=\"{x},{y + h * 0.25} {x + w},{y} {x + w},{y + h} {x},{y + h}\" {common}/>\n"),
            "off-page-connector" => Inv($"<polygon points=\"{x},{y} {x + w},{y} {x + w},{y + h * 0.65} {x + w / 2},{y + h} {x},{y + h * 0.65}\" {common}/>\n"),
            "delay" => Inv($"<path d=\"M{x},{y} H{x + w / 2} A{w / 2},{h / 2} 0 0 1 {x + w / 2},{y + h} H{x} Z\" {common}/>\n"),
            "manual-operation" => Inv($"<polygon points=\"{x},{y} {x + w},{y} {x + w * 0.8},{y + h} {x + w * 0.2},{y + h}\" {common}/>\n"),
            "display" => Inv($"<path d=\"M{x + w * 0.25},{y} H{x + w} V{y + h} H{x + w * 0.25} A{w * 0.25},{h / 2} 0 0 0 {x + w * 0.25},{y} Z\" {common}/>\n"),
            "stored-data" => Inv($"<path d=\"M{x},{y} H{x + w * 0.75} C{x + w},{y} {x + w},{y + h * 0.25} {x + w},{y + h / 2} C{x + w},{y + h * 0.75} {x + w},{y + h} {x + w * 0.75},{y + h} H{x} Z\" {common}/>\n"),
            "multi-document" => MultiDocument(x, y, w, h, common),
            "merge" => Inv($"<polygon points=\"{x},{y} {x + w},{y} {x + w / 2},{y + h}\" {common}/>\n"),
            "extract" => Inv($"<polygon points=\"{x + w / 2},{y} {x + w},{y + h} {x},{y + h}\" {common}/>\n"),
            "on-page-connector" => Inv($"<ellipse cx=\"{x + w / 2}\" cy=\"{y + h / 2}\" rx=\"{w / 2}\" ry=\"{h / 2}\" {common}/>\n"),
            "summing-junction" => Inv($"<ellipse cx=\"{x + w / 2}\" cy=\"{y + h / 2}\" rx=\"{w / 2}\" ry=\"{h / 2}\" {common}/><path d=\"M{x + w * 0.15},{y + h * 0.15} L{x + w * 0.85},{y + h * 0.85} M{x + w * 0.15},{y + h * 0.85} L{x + w * 0.85},{y + h * 0.15}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n"),
            "or" => Inv($"<ellipse cx=\"{x + w / 2}\" cy=\"{y + h / 2}\" rx=\"{w / 2}\" ry=\"{h / 2}\" {common}/><path d=\"M{x + w * 0.15},{y + h / 2} H{x + w * 0.85} M{x + w / 2},{y + h * 0.15} V{x + w / 2},{y + h * 0.85}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n"),
            "collate" => Inv($"<polygon points=\"{x},{y} {x + w},{y} {x},{y + h} {x + w},{y + h}\" {common}/>\n"),
            "sort" => Inv($"<polygon points=\"{x + w / 2},{y} {x + w},{y + h / 2} {x + w / 2},{y + h} {x},{y + h / 2}\" {common}/><path d=\"M{x},{y + h / 2} H{x + w}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n"),
            "loop-limit" => LoopLimit(x, y, w, h, common),
            "internal-storage" => Inv($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" {common}/><path d=\"M{x},{y + h * 0.2} H{x + w} M{x + w * 0.2},{y} V{y + h}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n"),
            "card" => Card(x, y, w, h, common),
            "rounded" => Inv($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" rx=\"{Math.Min(w, h) * 0.15}\" {common}/>\n"),
            "pill" => Inv($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" rx=\"{h / 2}\" {common}/>\n"),
            "diamond" => Inv($"<polygon points=\"{x + w / 2},{y} {x + w},{y + h / 2} {x + w / 2},{y + h} {x},{y + h / 2}\" {common}/>\n"),
            "hexagon" => Hexagon(x, y, w, h, common),
            "parallelogram" => Inv($"<polygon points=\"{x + w * 0.2},{y} {x + w},{y} {x + w * 0.8},{y + h} {x},{y + h}\" {common}/>\n"),
            "cylinder" => Cylinder(x, y, w, h, common),
            "text" => Inv($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"none\" stroke=\"none\"/>\n"),
            _ => Inv($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" {common}/>\n"), // rectangle and any unknown shape
        };
    }

    /// <summary>
    /// A swimlane pool or lane: a rectangle with a horizontal title band along its top, told apart by the band's
    /// height. Mirrors web/src/canvas/shapes.tsx's swimlaneRender; the label is placed top-left, in the band, by
    /// <see cref="AppendNode"/>.
    /// </summary>
    private static string SwimlaneMarkup(string shape, double x, double y, double w, double h, string fill, string stroke, double strokeWidth, string dash)
    {
        double band = string.Equals(shape, SwimlaneShapes.LaneShape, StringComparison.Ordinal) ? SwimlaneShapes.LaneHeaderHeight(h) : SwimlaneShapes.PoolHeaderHeight(h);
        return Inv($"<rect class=\"shape swimlane\" x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"{Escape(fill)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n<path class=\"shape-detail\" d=\"M{x},{R2(y + band)} H{x + w}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"/>\n");
    }

    /// <summary>
    /// A generic icon node: a transparent hit rectangle covering the box (the line art is a thin target), then the icon
    /// drawn as line art in a square at the top, its label placed in the bottom band by <see cref="AppendNode"/>. The
    /// stroke width is divided back out of the icon's viewBox scale so one theme stroke width reads the same on every
    /// icon; the artwork itself is data in <see cref="GenericIconLibrary"/>, shared with the web canvas.
    /// </summary>
    private static string IconMarkup(GenericIcon icon, double x, double y, double w, double h, string stroke, double strokeWidth)
    {
        double bandY = h * IconLabelBand;
        double iconSide = Math.Min(w, bandY) * IconSizeRatio;
        double ix = x + ((w - iconSide) / 2);
        double iy = y + ((bandY - iconSide) / 2);
        double scale = iconSide / GenericIconLibrary.ViewBox;
        var svg = new StringBuilder();
        svg.Append(Inv($"<rect class=\"icon-hit\" x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"transparent\" stroke=\"none\"/>\n"));
        svg.Append(Inv($"<g class=\"icon\" transform=\"translate({ix} {iy}) scale({scale})\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth / scale}\" stroke-linecap=\"round\" stroke-linejoin=\"round\">\n"));
        foreach (JsonElement element in icon.Elements)
        {
            svg.Append(IconElement(element));
        }

        svg.Append("</g>\n");
        return svg.ToString();
    }

    /// <summary>
    /// A boundary icon (VPC, subnet, availability zone): the whole box in the node's fill and stroke, with the icon
    /// drawn small in the top-right corner as a container's corner mark. Mirrors web/src/canvas/shapes.tsx's
    /// boundaryRender; <see cref="AppendNode"/> places the label top-left.
    /// </summary>
    private static string BoundaryMarkup(GenericIcon icon, double x, double y, double w, double h, string fill, string stroke, double strokeWidth, string dash)
    {
        double side = Math.Min(Math.Min(w, h) * BoundaryIconRatio, BoundaryIconMax);
        double ix = x + w - side - BoundaryIconInset;
        double iy = y + BoundaryIconInset;
        double scale = side / GenericIconLibrary.ViewBox;
        var svg = new StringBuilder();
        svg.Append(Inv($"<rect class=\"icon-hit\" x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"transparent\" stroke=\"none\"/>\n"));
        svg.Append(Inv($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"{Escape(fill)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n"));
        svg.Append(Inv($"<g class=\"icon\" transform=\"translate({ix} {iy}) scale({scale})\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth / scale}\" stroke-linecap=\"round\" stroke-linejoin=\"round\">\n"));
        foreach (JsonElement element in icon.Elements)
        {
            svg.Append(IconElement(element));
        }

        svg.Append("</g>\n");
        return svg.ToString();
    }

    /// <summary>
    /// A UML class, interface, lollipop or socket. A class or interface is one rectangle cut into name, attribute and
    /// operation compartments whose heights come from the node's content (<see cref="UmlShapeLibrary.LayoutClass"/>,
    /// the same numbers web/src/model/umlShapes.ts reads); the name, the optional stereotype row and every member are
    /// drawn here, so <see cref="AppendNode"/> adds no label. A member's visibility glyph sits in its own column so the
    /// names line up. A lollipop or socket is a stem and a ball or half-circle in the top band of its box. Mirrors
    /// web/src/canvas/shapes.tsx's umlRender.
    /// </summary>
    private static string UmlMarkup(UmlShape shape, Node node, double x, double y, double w, double h, string fill, string stroke, double strokeWidth, string dash, string text)
    {
        UmlMetrics m = UmlShapeLibrary.Metrics;
        string line = Inv($"fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}");
        var svg = new StringBuilder();
        if (!shape.IsCompartments)
        {
            double band = h * m.SymbolBand;
            double cy = R2(y + (band / 2));
            double r = R2(band * m.SymbolRadius);
            svg.Append(Inv($"<rect class=\"icon-hit\" x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"transparent\" stroke=\"none\"/>\n"));
            if (shape.Name == "lollipop")
            {
                svg.Append(Inv($"<path class=\"shape-detail\" d=\"M{x},{cy} H{R2(x + w - (2 * r))}\" {line}/>\n"));
                svg.Append(Inv($"<circle class=\"shape uml\" cx=\"{R2(x + w - r)}\" cy=\"{cy}\" r=\"{r}\" fill=\"{Escape(fill)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n"));
            }
            else
            {
                svg.Append(Inv($"<path class=\"shape-detail\" d=\"M{x},{cy} H{R2(x + w - r)}\" {line}/>\n"));
                svg.Append(Inv($"<path class=\"shape uml\" d=\"M{R2(x + w)},{R2(cy - r)} A{r},{r} 0 0 0 {R2(x + w)},{R2(cy + r)}\" {line}/>\n"));
            }

            return svg.ToString();
        }

        UmlClassLayout layout = UmlShapeLibrary.LayoutClass(shape, node.Label, node.Props, h);
        svg.Append(Inv($"<rect class=\"shape uml\" x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"{Escape(fill)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n"));
        svg.Append(Inv($"<path class=\"shape-detail\" d=\"M{x},{R2(y + layout.Attributes.Top)} H{R2(x + w)} M{x},{R2(y + layout.Operations.Top)} H{R2(x + w)}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"/>\n"));
        double centre = x + (w / 2);
        double rowY = y + m.PadY + (m.RowHeight / 2);
        if (layout.Stereotype is not null)
        {
            svg.Append(UmlText(centre, rowY, "middle", text, "\u00AB" + layout.Stereotype + "\u00BB", false));
            rowY += m.RowHeight;
        }

        if (node.Label.Length > 0)
        {
            svg.Append(UmlText(centre, rowY, "middle", text, node.Label, true));
        }

        foreach (UmlCompartment compartment in new[] { layout.Attributes, layout.Operations })
        {
            double memberY = y + compartment.Top + m.PadY + (m.RowHeight / 2);
            foreach (UmlMember member in compartment.Members)
            {
                double left = x + m.PadX;
                if (member.Visibility.Length > 0)
                {
                    svg.Append(UmlText(left, memberY, "start", text, member.Visibility, false));
                }

                svg.Append(UmlText(compartment.HasGlyph ? left + m.GlyphColumn : left, memberY, "start", text, member.Text, false));
                memberY += m.RowHeight;
            }
        }

        return svg.ToString();
    }

    /// <summary>
    /// An ER entity: one rectangle with a name band, then one row per column drawn as real columns (key marker, name,
    /// type, nullability), each left-aligned at the offset <see cref="ErShapeLibrary.LayoutEntity"/> derives from the
    /// widest cell (the same numbers web/src/model/erShapes.ts reads), with a divider after the leading primary-key
    /// rows. A weak entity adds an inner border. The name is drawn here, so <see cref="AppendNode"/> adds no label.
    /// Mirrors web/src/canvas/shapes.tsx's erRender.
    /// </summary>
    private static string ErMarkup(ErShape shape, Node node, double x, double y, double w, double h, string fill, string stroke, double strokeWidth, string dash, string text)
    {
        ErMetrics m = ErShapeLibrary.Metrics;
        ErLayout layout = ErShapeLibrary.LayoutEntity(node.Label, node.Props, h);
        double inset = shape.Double ? m.DoubleInset : 0;
        var svg = new StringBuilder();
        svg.Append(Inv($"<rect class=\"shape er\" x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"{Escape(fill)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n"));
        if (shape.Double)
        {
            svg.Append(Inv($"<rect class=\"shape-detail\" x=\"{R2(x + inset)}\" y=\"{R2(y + inset)}\" width=\"{R2(w - (inset * 2))}\" height=\"{R2(h - (inset * 2))}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"/>\n"));
        }

        IEnumerable<double> ruleYs = layout.KeyDivider is { } divider ? [layout.HeaderHeight, divider] : [layout.HeaderHeight];
        string rules = string.Join(' ', ruleYs.Select(ruleY => Inv($"M{R2(x + inset)},{R2(y + ruleY)} H{R2(x + w - inset)}")));
        svg.Append(Inv($"<path class=\"shape-detail\" d=\"{rules}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"/>\n"));
        if (node.Label.Length > 0)
        {
            svg.Append(UmlText(x + (w / 2), y + m.PadY + (m.RowHeight / 2), "middle", text, node.Label, true, m.FontSize));
        }

        for (int i = 0; i < layout.Rows.Count; i++)
        {
            ErColumn row = layout.Rows[i];
            double rowY = y + layout.BodyTop + m.PadY + (m.RowHeight * i) + (m.RowHeight / 2);
            void Cell(double? offset, string content, bool bold)
            {
                if (offset is { } left && content.Length > 0)
                {
                    svg.Append(UmlText(x + left, rowY, "start", text, content, bold, m.FontSize));
                }
            }

            Cell(layout.Offsets.Key, row.KeyText, true);
            Cell(layout.Offsets.Name, row.Name, false);
            Cell(layout.Offsets.Type, row.Type, false);
            Cell(layout.Offsets.Nullable, row.NullText, false);
        }

        return svg.ToString();
    }

    private static string UmlText(double x, double y, string anchor, string color, string content, bool bold, double? fontSize = null)
    {
        string weight = bold ? " font-weight=\"bold\"" : "";
        return Inv($"<text x=\"{R2(x)}\" y=\"{R2(y)}\" fill=\"{Escape(color)}\" font-family=\"sans-serif\" font-size=\"{fontSize ?? UmlShapeLibrary.Metrics.FontSize}\"{weight} text-anchor=\"{anchor}\" dominant-baseline=\"middle\" xml:space=\"preserve\">{Escape(content)}</text>\n");
    }

    /// <summary>
    /// An org-chart box: a rectangle whose chrome is data in <see cref="OrgShapeLibrary"/>, shared with the web canvas
    /// (web/src/canvas/shapes.tsx's orgRender). <c>outline</c> picks the single, inset-double or dashed border;
    /// <c>elements</c> are fractional lines drawn inside the box (the assistant's left spine). The label is placed at
    /// the box's own anchor by <see cref="AppendNode"/>.
    /// </summary>
    private static string OrgMarkup(OrgShape box, double x, double y, double w, double h, string fill, string stroke, double strokeWidth, string dash)
    {
        string outlineDash = box.Outline == "dashed" ? " stroke-dasharray=\"6 4\"" : dash;
        var svg = new StringBuilder();
        svg.Append(Inv($"<rect class=\"shape org\" x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"{Escape(fill)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{outlineDash}/>\n"));
        if (box.Outline == "double")
        {
            double inset = Math.Min(w, h) * 0.08;
            svg.Append(Inv($"<rect x=\"{x + inset}\" y=\"{y + inset}\" width=\"{w - inset * 2}\" height=\"{h - inset * 2}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n"));
        }

        foreach (JsonElement element in box.Elements)
        {
            if (element.GetProperty("tag").GetString() != "line")
            {
                continue;
            }

            double x1 = element.GetProperty("x1").GetDouble();
            double y1 = element.GetProperty("y1").GetDouble();
            double x2 = element.GetProperty("x2").GetDouble();
            double y2 = element.GetProperty("y2").GetDouble();
            svg.Append(Inv($"<line x1=\"{x + x1 * w}\" y1=\"{y + y1 * h}\" x2=\"{x + x2 * w}\" y2=\"{y + y2 * h}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n"));
        }

        return svg.ToString();
    }

    /// <summary>
    /// A BPMN shape (<see cref="BpmnShapeLibrary"/>), drawn natively and mirrored shape for shape by
    /// web/src/canvas/shapes.tsx's bpmnRender: an event is an ellipse filling its box (a circle on the square node the
    /// palette places) whose border weight is its position, an activity a rounded rectangle with a marker glyph, a
    /// gateway a diamond with a centred glyph, plus the data object, data store and text annotation. Numbers come from
    /// the library's shared <see cref="BpmnLayout"/>.
    /// </summary>
    private static string BpmnMarkup(BpmnShape shape, double x, double y, double w, double h, string fill, string stroke, double strokeWidth, string dash)
    {
        BpmnLayout l = BpmnShapeLibrary.Layout;
        var svg = new StringBuilder();
        string paint = Inv($"fill=\"{Escape(fill)}\" stroke=\"{Escape(stroke)}\"");
        double cx = x + w / 2;
        double cy = y + h / 2;
        double min = Math.Min(w, h);
        // A thick border is drawn heavier and pulled inward by half the extra weight, so it stays inside the box.
        double thick = strokeWidth * l.ThickBorder;
        double weight = shape.Border == "thick" ? thick : strokeWidth;
        double pull = shape.Border == "thick" ? (thick - strokeWidth) / 2 : 0;

        switch (shape.Family)
        {
            case BpmnShapeLibrary.EventFamily:
                svg.Append(Inv($"<ellipse class=\"shape bpmn\" cx=\"{R2(cx)}\" cy=\"{R2(cy)}\" rx=\"{R2((w / 2) - pull)}\" ry=\"{R2((h / 2) - pull)}\" {paint} stroke-width=\"{R2(weight)}\"{dash}/>\n"));
                if (shape.Border == "double")
                {
                    double ring = min * l.EventRingInset;
                    svg.Append(Inv($"<ellipse class=\"shape-detail\" cx=\"{R2(cx)}\" cy=\"{R2(cy)}\" rx=\"{R2((w / 2) - ring)}\" ry=\"{R2((h / 2) - ring)}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{R2(strokeWidth)}\"{dash}/>\n"));
                }

                svg.Append(BpmnGlyphMarkup(shape, cx - (min * l.EventGlyph / 2), cy - (min * l.EventGlyph / 2), min * l.EventGlyph, fill, stroke, strokeWidth));
                break;
            case BpmnShapeLibrary.GatewayFamily:
                svg.Append(Inv($"<polygon class=\"shape bpmn\" points=\"{R2(cx)},{R2(y)} {R2(x + w)},{R2(cy)} {R2(cx)},{R2(y + h)} {R2(x)},{R2(cy)}\" {paint} stroke-width=\"{R2(strokeWidth)}\"{dash}/>\n"));
                svg.Append(BpmnGlyphMarkup(shape, cx - (min * l.GatewayGlyph / 2), cy - (min * l.GatewayGlyph / 2), min * l.GatewayGlyph, fill, stroke, strokeWidth));
                break;
            case BpmnShapeLibrary.ActivityFamily:
                svg.Append(Inv($"<rect class=\"shape bpmn\" x=\"{R2(x + pull)}\" y=\"{R2(y + pull)}\" width=\"{R2(w - (pull * 2))}\" height=\"{R2(h - (pull * 2))}\" rx=\"{R2(Math.Min(l.CornerRadius, h / 2))}\" {paint} stroke-width=\"{R2(weight)}\"{dash}/>\n"));
                if (shape.Glyph is not null)
                {
                    double markerX = shape.Placement == "bottom" ? x + ((w - l.MarkerSize) / 2) : x + l.MarkerInset;
                    double markerY = shape.Placement == "bottom" ? y + h - l.MarkerInset - l.MarkerSize : y + l.MarkerInset;
                    svg.Append(BpmnGlyphMarkup(shape, markerX, markerY, l.MarkerSize, fill, stroke, strokeWidth));
                }

                break;
            case BpmnShapeLibrary.DataFamily when shape.Name == "data-store":
                {
                    double ry = Math.Min(h * 0.12, 14);
                    double rx = w / 2;
                    svg.Append(Inv($"<path class=\"shape bpmn\" d=\"M{R2(x)},{R2(y + ry)} A{R2(rx)},{R2(ry)} 0 0 1 {R2(x + w)},{R2(y + ry)} V{R2(y + h - ry)} A{R2(rx)},{R2(ry)} 0 0 1 {R2(x)},{R2(y + h - ry)} Z\" {paint} stroke-width=\"{R2(strokeWidth)}\"{dash}/>\n"));
                    svg.Append(Inv($"<path class=\"shape-detail\" d=\"M{R2(x)},{R2(y + ry)} A{R2(rx)},{R2(ry)} 0 0 0 {R2(x + w)},{R2(y + ry)} M{R2(x)},{R2(y + (ry * 2.4))} A{R2(rx)},{R2(ry)} 0 0 0 {R2(x + w)},{R2(y + (ry * 2.4))}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{R2(strokeWidth)}\"{dash}/>\n"));
                    break;
                }

            case BpmnShapeLibrary.DataFamily:
                {
                    double fold = Math.Min(w * l.DataFold, l.DataFoldMax);
                    svg.Append(Inv($"<path class=\"shape bpmn\" d=\"M{R2(x)},{R2(y)} H{R2(x + w - fold)} L{R2(x + w)},{R2(y + fold)} V{R2(y + h)} H{R2(x)} Z\" {paint} stroke-width=\"{R2(strokeWidth)}\"{dash}/>\n"));
                    svg.Append(Inv($"<path class=\"shape-detail\" d=\"M{R2(x + w - fold)},{R2(y)} V{R2(y + fold)} H{R2(x + w)}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{R2(strokeWidth)}\"{dash}/>\n"));
                    break;
                }

            default:
                {
                    // The text annotation: an open bracket down the left, with a transparent hit rectangle over the box.
                    double bracket = BpmnBracket(w);
                    svg.Append(Inv($"<rect class=\"icon-hit\" x=\"{R2(x)}\" y=\"{R2(y)}\" width=\"{R2(w)}\" height=\"{R2(h)}\" fill=\"transparent\" stroke=\"none\"/>\n"));
                    svg.Append(Inv($"<path class=\"shape bpmn\" d=\"M{R2(x + bracket)},{R2(y)} H{R2(x)} V{R2(y + h)} H{R2(x + bracket)}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{R2(strokeWidth)}\"{dash}/>\n"));
                    break;
                }
        }

        return svg.ToString();
    }

    /// <summary>The annotation bracket's width for a box of width <paramref name="w"/>. Mirrors bpmnBracket in shapes.tsx.</summary>
    private static double BpmnBracket(double w) => Math.Min(w * BpmnShapeLibrary.Layout.AnnotationBracket, BpmnShapeLibrary.Layout.AnnotationBracketMax);

    /// <summary>
    /// A BPMN glyph: the library's line art scaled from its 24 grid into a <paramref name="side"/> square whose top-left
    /// is (<paramref name="gx"/>, <paramref name="gy"/>). A throwing shape fills the art with the stroke colour, and a
    /// <c>detail</c> element (the envelope's flap) then draws in the node's fill colour so it still reads.
    /// </summary>
    private static string BpmnGlyphMarkup(BpmnShape shape, double gx, double gy, double side, string fill, string stroke, double strokeWidth)
    {
        if (shape.Glyph is null || !BpmnShapeLibrary.Glyphs.TryGetValue(shape.Glyph, out IReadOnlyList<JsonElement>? elements))
        {
            return "";
        }

        double scale = side / BpmnShapeLibrary.ViewBox;
        var svg = new StringBuilder();
        svg.Append(Inv($"<g class=\"icon bpmn-glyph\" transform=\"translate({R2(gx)} {R2(gy)}) scale({R2(scale)})\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth / scale}\" stroke-linecap=\"round\" stroke-linejoin=\"round\">\n"));
        foreach (JsonElement element in elements)
        {
            bool solid = element.TryGetProperty("solid", out JsonElement solidValue) && solidValue.GetBoolean();
            bool detail = element.TryGetProperty("detail", out JsonElement detailValue) && detailValue.GetBoolean();
            string extra = "";
            if (solid || shape.Throwing)
            {
                extra += Inv($" fill=\"{Escape(detail && shape.Throwing ? fill : stroke)}\"");
            }

            if (detail && shape.Throwing)
            {
                extra += Inv($" stroke=\"{Escape(fill)}\"");
            }

            svg.Append(BpmnGlyphElement(element, extra));
        }

        svg.Append("</g>\n");
        return svg.ToString();
    }

    /// <summary>One glyph primitive: the element's attributes verbatim except its tag and the two control flags, then <paramref name="extra"/>.</summary>
    private static string BpmnGlyphElement(JsonElement element, string extra)
    {
        string tag = element.GetProperty("tag").GetString()!;
        var attributes = new StringBuilder();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals("tag") || property.NameEquals("solid") || property.NameEquals("detail"))
            {
                continue;
            }

            string value = property.Value.ValueKind == JsonValueKind.Number
                ? Inv($"{Math.Round(property.Value.GetDouble(), 2)}")
                : property.Value.GetString()!;
            attributes.Append(Inv($" {property.Name}=\"{Escape(value)}\""));
        }

        return Inv($"<{tag}{attributes}{extra}/>\n");
    }

    /// <summary>One SVG primitive of an icon, copying the element's attributes verbatim except its `tag`.</summary>
    private static string IconElement(JsonElement element)
    {
        string tag = element.GetProperty("tag").GetString()!;
        var attributes = new StringBuilder();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.NameEquals("tag"))
            {
                continue;
            }

            string value = property.Value.ValueKind == JsonValueKind.Number
                ? Inv($"{Math.Round(property.Value.GetDouble(), 2)}")
                : property.Value.GetString()!;
            attributes.Append(Inv($" {property.Name}=\"{Escape(value)}\""));
        }

        return Inv($"<{tag}{attributes}/>\n");
    }

    /// <summary>
    /// A general-purpose shape (basic-shapes.json), drawn from the same path data as web/src/canvas/shapes.tsx's basicRender. The
    /// outline takes the node's fill and stroke; a detail takes its stroke, and its fill only where the library says so. An outline with
    /// no fill (the brace) is a line, drawn with fill none.
    /// </summary>
    private static string BasicMarkup(BasicShape shape, double x, double y, double w, double h, string fill, string stroke, double strokeWidth, string dash)
    {
        var svg = new StringBuilder();
        foreach (BasicShapePart part in shape.Parts)
        {
            string paint = part.Fill ? Escape(fill) : "none";
            svg.Append(Inv($"<path d=\"{BasicShapeGeometry.ResolvePath(part.D, x, y, w, h)}\" fill=\"{paint}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}/>\n"));
        }

        return svg.ToString();
    }

    /// <summary>
    /// A state-machine shape drawn natively (model/stateShapes.ts), mirroring web/src/canvas/shapes.tsx shape for
    /// shape. The filled parts (an initial dot, a fork bar, a history letter) take the node's text colour, so they
    /// re-skin with the theme; outlines take the node's stroke and fill.
    /// </summary>
    private static string StateShapeMarkup(string shape, double x, double y, double w, double h, string fill, string stroke, double strokeWidth, string text)
    {
        string common = Inv($"fill=\"{Escape(fill)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"");
        string solid = Inv($"fill=\"{Escape(text)}\" stroke=\"none\"");
        double cx = x + w / 2;
        double cy = y + h / 2;
        double min = Math.Min(w, h);
        string name = shape[StateShapeLibrary.Prefix.Length..];
        return name switch
        {
            "initial" => Inv($"<circle cx=\"{R2(cx)}\" cy=\"{R2(cy)}\" r=\"{R2(min * StateInitialDot)}\" {solid}/>\n"),
            "final" => Inv($"<circle cx=\"{R2(cx)}\" cy=\"{R2(cy)}\" r=\"{R2(min * StateFinalRing)}\" {common}/><circle cx=\"{R2(cx)}\" cy=\"{R2(cy)}\" r=\"{R2(min * StateFinalDot)}\" {solid}/>\n"),
            "choice" => Inv($"<polygon points=\"{R2(cx)},{R2(y)} {R2(x + w)},{R2(cy)} {R2(cx)},{R2(y + h)} {R2(x)},{R2(cy)}\" {common}/>\n"),
            "fork-join" => Inv($"<rect x=\"{R2(x)}\" y=\"{R2(cy - StateBarThickness(h) / 2)}\" width=\"{R2(w)}\" height=\"{R2(StateBarThickness(h))}\" {solid}/>\n"),
            "shallow-history" => HistoryMarkup(false, x, y, w, h, common, text),
            "deep-history" => HistoryMarkup(true, x, y, w, h, common, text),
            "entry-point" => PointMarkup(true, x, y, w, h, common, stroke, strokeWidth),
            "exit-point" => PointMarkup(false, x, y, w, h, common, stroke, strokeWidth),
            "composite-state" => CompositeStateMarkup(x, y, w, h, common, stroke, strokeWidth),
            _ => Inv($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" {common}/>\n"),
        };
    }

    /// <summary>A history marker: a ringed circle with H (shallow) or H* (deep) in the middle.</summary>
    private static string HistoryMarkup(bool deep, double x, double y, double w, double h, string common, string text)
    {
        double cx = x + w / 2;
        double cy = y + h / 2;
        double r = Math.Min(w, h) * StateHistoryRadius;
        double font = Math.Min(w, h) * StateHFont;
        return Inv($"<circle cx=\"{R2(cx)}\" cy=\"{R2(cy)}\" r=\"{R2(r)}\" {common}/><text x=\"{R2(cx)}\" y=\"{R2(cy)}\" fill=\"{Escape(text)}\" font-family=\"sans-serif\" font-size=\"{R2(font)}\" font-weight=\"bold\" text-anchor=\"middle\" dominant-baseline=\"middle\">{(deep ? "H*" : "H")}</text>\n");
    }

    /// <summary>An entry or exit point: a ringed circle with an arrow crossing the boundary, pointing in or out.</summary>
    private static string PointMarkup(bool inbound, double x, double y, double w, double h, string common, string stroke, double strokeWidth)
    {
        double cx = x + w / 2;
        double cy = y + h / 2;
        double r = Math.Min(w, h) * StatePointRadius;
        double dir = inbound ? -1 : 1;
        double tip = cx + dir * r;
        double tail = inbound ? x : x + w;
        string paint = Inv($"fill=\"{Escape(stroke)}\" stroke=\"none\"");
        return Inv($"<circle cx=\"{R2(cx)}\" cy=\"{R2(cy)}\" r=\"{R2(r)}\" {common}/><path d=\"M{R2(tail)},{R2(cy)} H{R2(tip - dir * 6)}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"/><polygon points=\"{R2(tip)},{R2(cy)} {R2(tip - dir * 7)},{R2(cy - 4)} {R2(tip - dir * 7)},{R2(cy + 4)}\" {paint}/>\n");
    }

    /// <summary>A composite state: a rounded boundary with a horizontal line under its name compartment.</summary>
    private static string CompositeStateMarkup(double x, double y, double w, double h, string common, string stroke, double strokeWidth)
    {
        double rx = Math.Min(StateRoundedRadius, h / 2);
        double band = y + StateNameBand(h);
        return Inv($"<rect x=\"{R2(x)}\" y=\"{R2(y)}\" width=\"{R2(w)}\" height=\"{R2(h)}\" rx=\"{R2(rx)}\" {common}/><path d=\"M{R2(x)},{R2(band)} H{R2(x + w)}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"/>\n");
    }

    /// <summary>The wavy-bottom page outline (ISO 5807 document). Mirrors web/src/canvas/shapes.tsx's documentOutline.</summary>
    private static string DocumentOutline(double x, double y, double w, double h)
    {
        return Inv($"M{x},{y} H{x + w} V{y + h * 0.85} C{x + w * 0.75},{y + h * 0.55} {x + w * 0.25},{y + h * 1.15} {x},{y + h * 0.85} Z");
    }

    /// <summary>A stack of three offset pages; the front page paints last. Mirrors web/src/canvas/shapes.tsx's multi-document.</summary>
    private static string MultiDocument(double x, double y, double w, double h, string common)
    {
        double stepX = w * 0.1;
        double stepY = h * 0.1;
        double pageW = w * 0.8;
        double pageH = h * 0.8;
        var svg = new StringBuilder();
        svg.Append(Inv($"<path d=\"{DocumentOutline(x + stepX * 2, y, pageW, pageH)}\" {common}/>\n"));
        svg.Append(Inv($"<path d=\"{DocumentOutline(x + stepX, y + stepY, pageW, pageH)}\" {common}/>\n"));
        svg.Append(Inv($"<path d=\"{DocumentOutline(x, y + stepY * 2, pageW, pageH)}\" {common}/>\n"));
        return svg.ToString();
    }

    /// <summary>A rectangle with the top-left and top-right corners bevelled. Mirrors web/src/canvas/shapes.tsx's loop-limit.</summary>
    private static string LoopLimit(double x, double y, double w, double h, string common)
    {
        double bevel = Math.Min(w * 0.2, h * 0.5);
        return Inv($"<polygon points=\"{x + bevel},{y} {x + w - bevel},{y} {x + w},{y + bevel} {x + w},{y + h} {x},{y + h} {x},{y + bevel}\" {common}/>\n");
    }

    /// <summary>A rectangle with the top-left corner cut off. Mirrors web/src/canvas/shapes.tsx's card.</summary>
    private static string Card(double x, double y, double w, double h, string common)
    {
        double cut = Math.Min(w * 0.2, h * 0.5);
        return Inv($"<polygon points=\"{x + cut},{y} {x + w},{y} {x + w},{y + h} {x},{y + h} {x},{y + cut}\" {common}/>\n");
    }

    /// <summary>An image node's asset id, from its <c>props.assetId</c>, or null when it names none.</summary>
    private static string? AssetIdOf(Node node)
    {
        if (node.Props is null || !node.Props.TryGetValue("assetId", out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? id = value.GetString();
        return string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>
    /// An image node's picture, inlined as a data URI so the SVG is self-contained wherever it is opened or printed
    /// (docs/api.md). <c>meet</c> keeps the picture's aspect ratio inside the node's box without cropping.
    /// </summary>
    private static string ImageMarkup(double x, double y, double w, double h, AssetImage image)
    {
        string href = Inv($"data:{image.ContentType};base64,{Convert.ToBase64String(image.Bytes)}");
        return Inv($"<image x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" preserveAspectRatio=\"xMidYMid meet\" href=\"{Escape(href)}\"/>\n");
    }

    private static string Hexagon(double x, double y, double w, double h, string common)
    {
        double cut = w * 0.2;
        return Inv($"<polygon points=\"{x + cut},{y} {x + w - cut},{y} {x + w},{y + h / 2} {x + w - cut},{y + h} {x + cut},{y + h} {x},{y + h / 2}\" {common}/>\n");
    }

    private static string Cylinder(double x, double y, double w, double h, string common)
    {
        double ellipseHeight = Math.Min(h * 0.2, w * 0.2);
        string body = Inv($"<path d=\"M {x} {y + ellipseHeight} A {w / 2} {ellipseHeight} 0 0 1 {x + w} {y + ellipseHeight} L {x + w} {y + h - ellipseHeight} A {w / 2} {ellipseHeight} 0 0 1 {x} {y + h - ellipseHeight} Z\" {common}/>\n");
        string cap = Inv($"<path d=\"M {x} {y + ellipseHeight} A {w / 2} {ellipseHeight} 0 0 0 {x + w} {y + ellipseHeight}\" fill=\"none\" stroke=\"{StrokeOf(common)}\"/>\n");
        return body + cap;
    }

    private static string StrokeOf(string common)
    {
        int start = common.IndexOf("stroke=\"", StringComparison.Ordinal) + "stroke=\"".Length;
        int end = common.IndexOf('"', start);
        return common[start..end];
    }

    /// <summary>
    /// Draws a node's label in <paramref name="rect"/> (the node's label rectangle, in its own space, whose top-left is at
    /// <paramref name="x"/>, <paramref name="y"/>): wrapped to the rectangle's width and centred in it, or, for a container's
    /// <paramref name="topLeft"/> label, set from its top-left corner with the lines running down (web/src/canvas/NodeView.tsx).
    /// </summary>
    private static void AppendNodeLabel(StringBuilder svg, string label, double x, double y, LabelRect rect, string color, bool topLeft)
    {
        double maxWidth = rect.W - (NodeLabelLayout.Padding * 2);
        if (topLeft)
        {
            AppendLabel(svg, label, R2(x + rect.X + BoundaryLabelPadding), R2(y + rect.Y + BoundaryLabelOffset), color, "start", maxWidth);
        }
        else
        {
            AppendLabel(svg, label, R2(x + rect.X + (rect.W / 2)), R2(y + rect.Y + (rect.H / 2)), color, "middle", maxWidth);
        }
    }

    /// <summary>
    /// Draws a label at a point. With <paramref name="maxWidth"/> it wraps there (<see cref="NodeLabelLayout.Wrap"/>), the way
    /// the canvas wraps a node's label; without it only explicit newlines break, which is how an edge or sequence message label
    /// is drawn. A <c>start</c>-anchored label is a container's: its first line sits on <paramref name="centerY"/> and the rest
    /// run down from it; any other block is centred on it.
    /// </summary>
    private static void AppendLabel(StringBuilder svg, string label, double centerX, double centerY, string color, string anchor = "middle", double? maxWidth = null)
    {
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        // An explicit newline is a forced break, as the web canvas draws it (web/src/canvas/text.ts); a role box's
        // name-and-title label is the everyday case. A single-line label keeps the exact markup it always had.
        IReadOnlyList<string> lines = maxWidth is { } width
            ? NodeLabelLayout.Wrap(label, width)
            : label.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Count == 1)
        {
            svg.Append(Inv($"<text x=\"{centerX}\" y=\"{centerY}\" fill=\"{Escape(color)}\" font-family=\"sans-serif\" font-size=\"13\" text-anchor=\"{anchor}\" dominant-baseline=\"middle\">{Escape(lines[0])}</text>\n"));
            return;
        }

        const double lineHeight = 17;
        double firstY = anchor == "start" && maxWidth is not null ? centerY : centerY - ((lines.Count - 1) * lineHeight) / 2;
        svg.Append(Inv($"<text x=\"{centerX}\" y=\"{firstY}\" fill=\"{Escape(color)}\" font-family=\"sans-serif\" font-size=\"13\" text-anchor=\"{anchor}\" dominant-baseline=\"middle\">\n"));
        for (int i = 0; i < lines.Count; i++)
        {
            svg.Append(Inv($"<tspan x=\"{centerX}\" y=\"{firstY + (i * lineHeight)}\">{Escape(lines[i])}</tspan>\n"));
        }

        svg.Append("</text>\n");
    }

    /// <summary>Draws the edge, or returns the warning explaining why it could not be drawn.</summary>
    private static string? AppendEdge(StringBuilder svg, Edge edge, IReadOnlyDictionary<string, Node> byId, double offsetX, double offsetY, IReadOnlyDictionary<string, string> markers, IReadOnlyDictionary<string, (double X, double Y)> labelPositions, IReadOnlyDictionary<string, IReadOnlyList<Point>>? routes, IReadOnlyDictionary<string, double> portOffsets, SurfacePaint surface)
    {
        var dangling = new List<string>();
        if (!byId.TryGetValue(edge.From.Node, out Node? from))
        {
            dangling.Add(edge.From.Node);
        }

        if (!byId.TryGetValue(edge.To.Node, out Node? to))
        {
            dangling.Add(edge.To.Node);
        }

        if (dangling.Count > 0)
        {
            string missing = string.Join(" or ", dangling.Distinct(StringComparer.Ordinal).Select(id => $"'{id}'"));
            return $"edge '{edge.Id}' was not drawn: the document has no node with id {missing}.";
        }

        EdgeGeometry geometry = GetEdgeGeometry(edge, from!, to!, offsetX, offsetY, routes, portOffsets);
        (double sx, double sy) = geometry.Points[0];
        (double ex, double ey) = geometry.Points[^1];

        string stroke = edge.Style?.Stroke ?? DefaultStroke;
        double strokeWidth = edge.Style?.StrokeWidth ?? 1.5;
        string dash = DashArray(edge.Style?.Dash);
        ArrowHead end = edge.Style?.ArrowEnd ?? ArrowHead.Arrow;
        ArrowHead? start = edge.Style?.ArrowStart;
        string suffix = markers[stroke];
        string markerEnd = end == ArrowHead.None ? "" : $" marker-end=\"url(#arrow-{MarkerName(end)}{suffix})\"";
        string markerStart = start is null or ArrowHead.None ? "" : $" marker-start=\"url(#arrow-{MarkerName(start.Value)}{suffix})\"";

        svg.Append(Inv($"<g data-id=\"{Escape(edge.Id)}\">\n"));
        if (geometry.Loop)
        {
            svg.Append(Inv($"<path d=\"M {sx} {sy} C {geometry.Control1.X} {geometry.Control1.Y} {geometry.Control2.X} {geometry.Control2.Y} {ex} {ey}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}{markerStart}{markerEnd}/>\n"));
        }
        else if (edge.Routing?.Mode == RoutingMode.Curved && geometry.Points.Count > 2)
        {
            string curve = SmoothCurvePath(geometry.Points);
            svg.Append(Inv($"<path d=\"{curve}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}{markerStart}{markerEnd}/>\n"));
        }
        else if (geometry.Points.Count > 2)
        {
            string points = string.Join(" ", geometry.Points.Select(p => Inv($"{p.X},{p.Y}")));
            svg.Append(Inv($"<polyline points=\"{points}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}{markerStart}{markerEnd}/>\n"));
        }
        else
        {
            svg.Append(Inv($"<line x1=\"{sx}\" y1=\"{sy}\" x2=\"{ex}\" y2=\"{ey}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"{dash}{markerStart}{markerEnd}/>\n"));
        }
        if (!string.IsNullOrEmpty(edge.Label))
        {
            (double midX, double midY) = labelPositions.TryGetValue(edge.Id, out (double X, double Y) placed) ? placed : geometry.Label;
            double width = LabelWidth(edge.Label);
            svg.Append(Inv($"<rect x=\"{midX - width / 2}\" y=\"{midY - LabelHeight / 2}\" width=\"{width}\" height=\"{LabelHeight}\" fill=\"{Escape(surface.Background)}\"/>\n"));
            AppendLabel(svg, edge.Label, midX, midY, surface.Text);
        }

        svg.Append("</g>\n");
        return null;
    }

    /// <summary>The drawn polyline, source to target through any waypoints, plus the control points a self-loop bends through.</summary>
    private sealed record EdgeGeometry(
        IReadOnlyList<(double X, double Y)> Points,
        (double X, double Y) Control1, (double X, double Y) Control2,
        (double X, double Y) Label, bool Loop);

    /// <summary>
    /// Mirrors <c>web/src/canvas/documentIndex.ts</c>'s <c>edgePolyline</c>: an edge ELK bent arrives carrying its
    /// bend points in <see cref="Routing.Waypoints"/>, and the drawn line has to follow them or it cuts through the
    /// nodes the route was computed to avoid. Ends still clip to the node boundary, aimed at the nearest waypoint.
    /// </summary>
    private static EdgeGeometry GetEdgeGeometry(Edge edge, Node from, Node to, double offsetX, double offsetY, IReadOnlyDictionary<string, IReadOnlyList<Point>>? routes, IReadOnlyDictionary<string, double> portOffsets)
    {
        if (edge.Routing?.Mode == RoutingMode.Orthogonal && routes is not null && routes.TryGetValue(edge.Id, out IReadOnlyList<Point>? route) && route.Count >= 2)
        {
            // The editor's own route (ISidecarClient.RouteAsync): axis-aligned, port to port, already around the nodes.
            (double X, double Y)[] routed = route.Select(p => (p.X - offsetX, p.Y - offsetY)).ToArray();
            return new EdgeGeometry(routed, routed[0], routed[^1], Midpoint(routed), false);
        }

        (double fx, double fy) = Center(from, offsetX, offsetY);
        (double tx, double ty) = Center(to, offsetX, offsetY);
        (double X, double Y)[] waypoints = (edge.Routing?.Waypoints ?? [])
            .Select(p => (p.X - offsetX, p.Y - offsetY))
            .ToArray();

        // A column port (an ER entity's row, ErShapeLibrary.ColumnPort) sits on the side facing the far end: the first
        // waypoint if the edge is pinned, else the far node's aim point. Mirrors documentIndex.ts `edgePolyline`.
        var fromPort = PortPoint(from, edge.From.Port, offsetX, offsetY, waypoints.Length > 0 ? waypoints[0].X : ColumnAimX(from, edge.From.Port, to, edge.To.Port, offsetX, offsetY), portOffsets.GetValueOrDefault($"{edge.Id}:from"));
        var toPort = PortPoint(to, edge.To.Port, offsetX, offsetY, waypoints.Length > 0 ? waypoints[^1].X : ColumnAimX(to, edge.To.Port, from, edge.From.Port, offsetX, offsetY), portOffsets.GetValueOrDefault($"{edge.Id}:to"));
        if (waypoints.Length == 0 && fx == tx && fy == ty && fromPort == toPort)
        {
            Geometry f = from.Geometry!;
            Geometry t = to.Geometry!;
            var start = Rotate(f.W / 2, -f.H / 4, f.Rotation ?? 0);
            var end = Rotate(t.W / 2, t.H / 4, t.Rotation ?? 0);
            var c1 = Rotate(f.W / 2 + 24, -f.H / 4 - 24, f.Rotation ?? 0);
            var c2 = Rotate(t.W / 2 + 24, t.H / 4 + 24, t.Rotation ?? 0);
            return new EdgeGeometry([(fx + start.X, fy + start.Y), (tx + end.X, ty + end.Y)],
                (fx + c1.X, fy + c1.Y), (tx + c2.X, ty + c2.Y),
                (fx + (start.X + 3 * c1.X + 3 * c2.X + end.X) / 8,
                 fy + (start.Y + 3 * c1.Y + 3 * c2.Y + end.Y) / 8), true);
        }

        (double X, double Y) towardSource = waypoints.Length > 0 ? waypoints[0] : toPort ?? (tx, ty);
        (double X, double Y) towardTarget = waypoints.Length > 0 ? waypoints[^1] : fromPort ?? (fx, fy);
        // An orthogonal connector with routed bends (ELK's) leaves its node straight towards the first one (QA-01).
        bool bent = edge.Routing?.Mode == RoutingMode.Orthogonal && waypoints.Length > 0;
        var source = fromPort ?? ClipToBounds(fx, fy, towardSource.X, towardSource.Y, from, offsetX, offsetY, bent);
        var target = toPort ?? ClipToBounds(tx, ty, towardTarget.X, towardTarget.Y, to, offsetX, offsetY, bent);
        (double X, double Y)[] points = [source, .. waypoints, target];
        // A curved connector's label sits on the curve by arc length; the polyline midpoint falls off a bent curve.
        (double X, double Y) label = edge.Routing?.Mode == RoutingMode.Curved && points.Length > 2 ? CurveMidpoint(points) : Midpoint(points);
        return new EdgeGeometry(points, source, target, label, false);
    }

    /// <summary>
    /// Every labelled edge's plate centre, in document order: mirrors <c>web/src/canvas/labelLayout.ts</c>. A label
    /// starts at its edge's own <see cref="EdgeGeometry.Label"/> point; one whose plate would overlap an
    /// already-placed plate, or a node's own box other than its edge's two endpoints (M2 final-check Finding C),
    /// slides along the polyline segment nearest that point (away from it, never off the segment) until it clears
    /// every earlier label and every such node box or the segment runs out. An earlier edge's label never moves for
    /// a later one, so placement does not depend on any edge not yet drawn. A dangling edge (its endpoint missing)
    /// is left out, same as <see cref="AppendEdge"/> leaves it undrawn.
    /// </summary>
    private static Dictionary<string, (double X, double Y)> PlaceEdgeLabels(Document document, IReadOnlyDictionary<string, Node> byId, double offsetX, double offsetY, IReadOnlyDictionary<string, IReadOnlyList<Point>>? routes)
    {
        IReadOnlyDictionary<string, double> portOffsets = EdgeAttachment.SharedPortOffsets(document.Edges);
        Dictionary<string, (double X, double Y, double W, double H)> nodeBoxes = byId.Values.ToDictionary(
            n => n.Id,
            n => NodeBox(n, offsetX, offsetY),
            StringComparer.Ordinal);

        var placed = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        var boxes = new List<(double X, double Y, double W, double H)>();
        foreach (Edge edge in document.Edges)
        {
            if (string.IsNullOrEmpty(edge.Label) || !byId.TryGetValue(edge.From.Node, out Node? from) || !byId.TryGetValue(edge.To.Node, out Node? to))
            {
                continue;
            }

            (double X, double Y, double W, double H)[] nodeObstacles = nodeBoxes
                .Where(pair => pair.Key != edge.From.Node && pair.Key != edge.To.Node)
                .Select(pair => pair.Value)
                .ToArray();

            // A plate may sit on its own endpoints but never across one's border (labelLayout.ts `straddles`).
            (double X, double Y, double W, double H)[] ownBorders = [nodeBoxes[edge.From.Node], nodeBoxes[edge.To.Node]];
            bool Clear((double X, double Y, double W, double H) candidate) =>
                !boxes.Any(other => BoxesOverlap(candidate, other)) && !nodeObstacles.Any(other => BoxesOverlap(candidate, other))
                && !ownBorders.Any(other => BoxesOverlap(candidate, other) && !BoxWithin(candidate, other));

            EdgeGeometry geometry = GetEdgeGeometry(edge, from, to, offsetX, offsetY, routes, portOffsets);
            double width = LabelWidth(edge.Label);
            (double X, double Y) centre = geometry.Label;
            (double X, double Y, double W, double H) box = LabelBoxAt(centre, width, LabelHeight);
            if (!Clear(box))
            {
                ((double X, double Y) Start, (double X, double Y) End, double Offset)? segment = SegmentNear(geometry.Points, geometry.Label);
                if (segment is { } found)
                {
                    for (int step = 1; step <= LabelNudgeMaxSteps; step++)
                    {
                        bool resolved = false;
                        foreach (double candidateOffset in new[] { found.Offset + step * LabelNudgeStep, found.Offset - step * LabelNudgeStep })
                        {
                            (double X, double Y) point = PointAtOffset(found, candidateOffset);
                            (double X, double Y, double W, double H) candidate = LabelBoxAt(point, width, LabelHeight);
                            if (Clear(candidate))
                            {
                                centre = point;
                                box = candidate;
                                resolved = true;
                                break;
                            }
                        }

                        if (resolved)
                        {
                            break;
                        }
                    }
                }
            }

            boxes.Add(box);
            placed[edge.Id] = centre;
        }

        return placed;
    }

    private static bool BoxWithin((double X, double Y, double W, double H) inner, (double X, double Y, double W, double H) outer) =>
        inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.W <= outer.X + outer.W && inner.Y + inner.H <= outer.Y + outer.H;

    /// <summary>A node's own axis-aligned box in the render's offset-adjusted space, rotation included (RotatedExtent).</summary>
    private static (double X, double Y, double W, double H) NodeBox(Node node, double offsetX, double offsetY)
    {
        Geometry g = node.Geometry!;
        var extent = RotatedExtent(g.X - offsetX + g.W / 2, g.Y - offsetY + g.H / 2, g.W / 2, g.H / 2, g.Rotation ?? 0);
        return (extent.Left, extent.Top, extent.Right - extent.Left, extent.Bottom - extent.Top);
    }

    private static (double X, double Y, double W, double H) LabelBoxAt((double X, double Y) centre, double width, double height) =>
        (centre.X - width / 2, centre.Y - height / 2, width, height);

    private static bool BoxesOverlap((double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b) =>
        a.X < b.X + b.W && a.X + a.W > b.X && a.Y < b.Y + b.H && a.Y + a.H > b.Y;

    /// <summary>The consecutive pair of `points` closest to `basePoint` (the segment its default position falls on or nearest to).</summary>
    private static ((double X, double Y) Start, (double X, double Y) End, double Offset)? SegmentNear(IReadOnlyList<(double X, double Y)> points, (double X, double Y) basePoint)
    {
        if (points.Count == 0)
        {
            return null;
        }

        if (points.Count == 1)
        {
            return (points[0], points[0], 0);
        }

        ((double X, double Y) Start, (double X, double Y) End, double Offset)? best = null;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < points.Count - 1; i++)
        {
            (double X, double Y) start = points[i];
            (double X, double Y) end = points[i + 1];
            double length = Length(start, end);
            double t = length == 0 ? 0 : Math.Clamp((((basePoint.X - start.X) * (end.X - start.X)) + ((basePoint.Y - start.Y) * (end.Y - start.Y))) / (length * length), 0, 1);
            (double X, double Y) projected = (start.X + (end.X - start.X) * t, start.Y + (end.Y - start.Y) * t);
            double gap = Length(basePoint, projected);
            if (gap < bestDistance)
            {
                bestDistance = gap;
                best = (start, end, t * length);
            }
        }

        return best;
    }

    private static (double X, double Y) PointAtOffset(((double X, double Y) Start, (double X, double Y) End, double Offset) segment, double offset)
    {
        double length = Length(segment.Start, segment.End);
        if (length == 0)
        {
            return segment.Start;
        }

        double t = Math.Clamp(offset, 0, length) / length;
        return (segment.Start.X + (segment.End.X - segment.Start.X) * t, segment.Start.Y + (segment.End.Y - segment.Start.Y) * t);
    }

    /// <summary>
    /// The absolute position of a named port on a node, or null when the node has no such port: one it declares, or a
    /// column of an ER entity (<c>col:&lt;name&gt;</c>), which sits on the edge of the table facing
    /// <paramref name="towardsX"/>. A name the entity has no row for is null too, so the end attaches to the table.
    /// </summary>
    private static (double X, double Y)? PortPoint(Node node, string? id, double offsetX, double offsetY, double towardsX = double.PositiveInfinity, double portOffset = 0)
    {
        Geometry g = node.Geometry!;
        Port? port = node.Ports?.FirstOrDefault(p => p.Id == id) ?? ErShapeLibrary.ColumnPort(node, id, towardsX >= g.X - offsetX + (g.W / 2));
        if (port is null)
        {
            return null;
        }

        return EdgeAttachment.NudgedPort(node, port, portOffset, offsetX, offsetY);
    }

    /// <summary>
    /// The x an end is aimed at: the other end's <see cref="AimPoint"/>, except between two column ports whose tables
    /// overlap horizontally. There both ends take the same side, the one where the two tables' edges lie closest (a
    /// tie is the right), so the connector is one short bracket instead of a hook round the table. Mirrors
    /// <c>web/src/canvas/ports.ts</c> <c>towardsPoint</c>; only the side it implies is read.
    /// </summary>
    private static double ColumnAimX(Node self, string? selfPort, Node other, string? otherPort, double offsetX, double offsetY)
    {
        double aim = AimPoint(other, otherPort, offsetX, offsetY).X;
        bool IsColumn(Node node, string? id) => node.Ports?.Any(p => p.Id == id) != true && ErShapeLibrary.ColumnPort(node, id, true) is not null;
        if (!IsColumn(self, selfPort) || !IsColumn(other, otherPort))
        {
            return aim;
        }

        Geometry a = self.Geometry!;
        Geometry b = other.Geometry!;
        if (a.X >= b.X + b.W || b.X >= a.X + a.W)
        {
            return aim;
        }

        double side = Math.Abs(a.X + a.W - (b.X + b.W)) <= Math.Abs(a.X - b.X) ? 1 : -1;
        return a.X - offsetX + a.W / 2 + side;
    }

    /// <summary>The point an edge's other end is aimed from: the declared port's position, else the node's centre (a column port aims from the centre too, since its side is decided by this point).</summary>
    private static (double X, double Y) AimPoint(Node node, string? id, double offsetX, double offsetY)
    {
        return node.Ports?.Any(p => p.Id == id) == true ? PortPoint(node, id, offsetX, offsetY)!.Value : Center(node, offsetX, offsetY);
    }

    /// <summary>The point half way along the polyline by length, so a label on a bent route sits on the route.</summary>
    private static (double X, double Y) Midpoint(IReadOnlyList<(double X, double Y)> points)
    {
        double remaining = 0;
        for (int i = 1; i < points.Count; i++)
        {
            remaining += Length(points[i - 1], points[i]);
        }

        remaining /= 2;
        for (int i = 1; i < points.Count; i++)
        {
            double segment = Length(points[i - 1], points[i]);
            if (segment >= remaining)
            {
                double t = segment == 0 ? 0 : remaining / segment;
                return (points[i - 1].X + (points[i].X - points[i - 1].X) * t,
                        points[i - 1].Y + (points[i].Y - points[i - 1].Y) * t);
            }

            remaining -= segment;
        }

        return points[^1];
    }

    private static double Length((double X, double Y) a, (double X, double Y) b)
    {
        return Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
    }

    /// <summary>
    /// The point half way along the smooth Catmull-Rom curve by arc length, so a curved connector's label sits on the
    /// curve rather than at the polyline midpoint that falls off it. Mirrors <c>web/src/canvas/curve.ts</c>'s
    /// <c>curveMidpoint</c>: each cubic is sampled in straight steps, 32 of which is well under a pixel of error.
    /// </summary>
    private static (double X, double Y) CurveMidpoint(IReadOnlyList<(double X, double Y)> points)
    {
        const int samples = 32;
        const double tension = 0.5;
        if (points.Count < 2)
        {
            return points.Count == 0 ? (0, 0) : points[0];
        }

        var cubics = new List<((double X, double Y) P0, (double X, double Y) C1, (double X, double Y) C2, (double X, double Y) P3)>();
        for (int i = 0; i < points.Count - 1; i++)
        {
            (double X, double Y) p0 = i - 1 >= 0 ? points[i - 1] : points[i];
            (double X, double Y) p1 = points[i];
            (double X, double Y) p2 = points[i + 1];
            (double X, double Y) p3 = i + 2 < points.Count ? points[i + 2] : p2;
            (double X, double Y) c1 = (p1.X + (p2.X - p0.X) * tension / 2, p1.Y + (p2.Y - p0.Y) * tension / 2);
            (double X, double Y) c2 = (p2.X - (p3.X - p1.X) * tension / 2, p2.Y - (p3.Y - p1.Y) * tension / 2);
            cubics.Add((p1, c1, c2, p2));
        }

        double total = 0;
        var lengths = new double[cubics.Count];
        for (int s = 0; s < cubics.Count; s++)
        {
            double length = 0;
            (double X, double Y) previous = cubics[s].P0;
            for (int i = 1; i <= samples; i++)
            {
                (double X, double Y) at = CubicAt(cubics[s], (double)i / samples);
                length += Length(previous, at);
                previous = at;
            }

            lengths[s] = length;
            total += length;
        }

        if (total == 0)
        {
            return points[0];
        }

        double remaining = total / 2;
        for (int s = 0; s < cubics.Count; s++)
        {
            if (remaining > lengths[s] && s < cubics.Count - 1)
            {
                remaining -= lengths[s];
                continue;
            }

            (double X, double Y) previous = cubics[s].P0;
            double travelled = 0;
            for (int i = 1; i <= samples; i++)
            {
                (double X, double Y) at = CubicAt(cubics[s], (double)i / samples);
                double step = Length(previous, at);
                if (travelled + step >= remaining)
                {
                    double within = step == 0 ? 0 : (remaining - travelled) / step;
                    return CubicAt(cubics[s], (i - 1 + within) / samples);
                }

                travelled += step;
                previous = at;
            }

            return cubics[s].P3;
        }

        return points[^1];
    }

    /// <summary>A point on a cubic Bézier at parameter <paramref name="t"/>.</summary>
    private static (double X, double Y) CubicAt(
        ((double X, double Y) P0, (double X, double Y) C1, (double X, double Y) C2, (double X, double Y) P3) cubic,
        double t)
    {
        double mt = 1 - t;
        double a = mt * mt * mt;
        double b = 3 * mt * mt * t;
        double c = 3 * mt * t * t;
        double d = t * t * t;
        return (a * cubic.P0.X + b * cubic.C1.X + c * cubic.C2.X + d * cubic.P3.X,
                a * cubic.P0.Y + b * cubic.C1.Y + c * cubic.C2.Y + d * cubic.P3.Y);
    }

    /// <summary>
    /// The `d` attribute of a smooth Catmull-Rom curve through <paramref name="points"/>, one cubic Bézier per interior
    /// segment, mirroring <c>web/src/canvas/curve.ts</c>. Passes exactly through every point, and the Bézier leading
    /// into each end carries the spline's tangent there, so an <c>orient="auto"</c> marker faces the right way. Two
    /// points collapse to a straight line; one or none to nothing (never reached: those draw as a line).
    /// </summary>
    private static string SmoothCurvePath(IReadOnlyList<(double X, double Y)> points)
    {
        var path = new StringBuilder();
        path.Append(Inv($"M {points[0].X},{points[0].Y}"));
        if (points.Count == 2)
        {
            path.Append(Inv($" L {points[1].X},{points[1].Y}"));
            return path.ToString();
        }
        foreach ((double X, double Y)[] pair in CurveSegments(points))
        {
            (double X, double Y) c1 = pair[0];
            (double X, double Y) c2 = pair[1];
            (double X, double Y) p2 = pair[2];
            path.Append(Inv($" C {c1.X},{c1.Y} {c2.X},{c2.Y} {p2.X},{p2.Y}"));
        }
        return path.ToString();
    }

    /// <summary>The two Bézier control points of every curved segment, as triples (control1, control2, end).</summary>
    private static IEnumerable<(double X, double Y)[]> CurveSegments(IReadOnlyList<(double X, double Y)> points)
    {
        const double tension = 0.5;
        for (int i = 0; i < points.Count - 1; i++)
        {
            (double X, double Y) p0 = i - 1 >= 0 ? points[i - 1] : points[i];
            (double X, double Y) p1 = points[i];
            (double X, double Y) p2 = points[i + 1];
            (double X, double Y) p3 = i + 2 < points.Count ? points[i + 2] : p2;
            (double X, double Y) c1 = (p1.X + (p2.X - p0.X) * tension / 2, p1.Y + (p2.Y - p0.Y) * tension / 2);
            (double X, double Y) c2 = (p2.X - (p3.X - p1.X) * tension / 2, p2.Y - (p3.Y - p1.Y) * tension / 2);
            yield return [c1, c2, p2];
        }
    }

    /// <summary>Every Bézier control point of a curved edge, for the export bounds so the curve never clips.</summary>
    private static (double X, double Y)[] CurveControlPoints(IReadOnlyList<(double X, double Y)> points) =>
        CurveSegments(points).SelectMany(segment => segment.Take(2)).ToArray();

    private static (double X, double Y) Rotate(double x, double y, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        return (x * Math.Cos(radians) - y * Math.Sin(radians), x * Math.Sin(radians) + y * Math.Cos(radians));
    }

    private static (double, double) Center(Node node, double offsetX, double offsetY)
    {
        Geometry g = node.Geometry!;
        return (g.X - offsetX + g.W / 2, g.Y - offsetY + g.H / 2);
    }

    /// <summary>Where the line from (fromX,fromY) toward (towardX,towardY) crosses the boundary of <paramref name="node"/>'s box.</summary>
    private static (double X, double Y) ClipToBounds(double fromX, double fromY, double towardX, double towardY, Node node, double offsetX, double offsetY, bool aligned = false)
    {
        EdgeAttachment.Outline outline = EdgeAttachment.OutlineOf(node.Shape);
        if (outline != EdgeAttachment.Outline.None)
        {
            // A diamond (a decision, a BPMN gateway) or ellipse (a BPMN event) stops at its outline, not its box corner.
            return EdgeAttachment.Exit(node, outline, towardX, towardY, aligned, offsetX, offsetY);
        }

        Geometry g = node.Geometry!;
        double x = g.X - offsetX;
        double y = g.Y - offsetY;
        double halfW = g.W / 2;
        double halfH = g.H / 2;
        double cx = x + halfW;
        double cy = y + halfH;
        (double dx, double dy) = Rotate(towardX - fromX, towardY - fromY, -(g.Rotation ?? 0));
        if (dx == 0 && dy == 0)
        {
            return (cx, cy);
        }

        double scaleX = dx == 0 ? double.PositiveInfinity : halfW / Math.Abs(dx);
        double scaleY = dy == 0 ? double.PositiveInfinity : halfH / Math.Abs(dy);
        double t = Math.Min(scaleX, scaleY);
        double nx = dx / g.W;
        double ny = dy / g.H;
        if (node.Shape == "manual-input" && ny + 0.25 * nx < 0)
        {
            t = Math.Min(t, -0.375 / (ny + 0.25 * nx));
        }
        if (node.Shape == "off-page-connector" && ny + 0.7 * Math.Abs(nx) > 0)
        {
            t = Math.Min(t, 0.5 / (ny + 0.7 * Math.Abs(nx)));
        }
        if (node.Shape == "document" && ny > 0 && Math.Abs(nx / ny) <= 0.5 / 0.35)
        {
            double low = 0;
            double high = 1;
            for (int i = 0; i < 48; i++)
            {
                double u = (low + high) / 2;
                double bx = 0.75 * u + 0.75 * u * u - 0.5 * u * u * u - 0.5;
                double by = 0.35 + 0.9 * u - 2.7 * u * u + 1.8 * u * u * u;
                if (bx * ny < by * nx)
                {
                    low = u;
                }
                else
                {
                    high = u;
                }
            }
            double parameter = (low + high) / 2;
            t = Math.Min(t, (0.35 + 0.9 * parameter - 2.7 * parameter * parameter + 1.8 * parameter * parameter * parameter) / ny);
        }
        var rotated = Rotate(dx * t, dy * t, g.Rotation ?? 0);
        return (cx + rotated.X, cy + rotated.Y);
    }

    /// <summary>
    /// An arrowhead is part of the edge, so it has to carry the edge's own stroke colour. Each distinct colour gets its
    /// own set of markers, keyed by the suffix this returns; the first edge's colour (the neutral default when there are
    /// none) keeps the unsuffixed ids, which is the one colour a themed single-stroke diagram uses.
    /// </summary>
    private static Dictionary<string, string> MarkerSuffixes(Document document)
    {
        var suffixes = new Dictionary<string, string>(StringComparer.Ordinal) { [document.Edges.Select(e => e.Style?.Stroke).FirstOrDefault(c => c is not null) ?? DefaultStroke] = "" };
        foreach (Edge edge in document.Edges)
        {
            string stroke = edge.Style?.Stroke ?? DefaultStroke;
            if (!suffixes.ContainsKey(stroke))
            {
                suffixes[stroke] = Inv($"-{suffixes.Count}");
            }
        }

        return suffixes;
    }

    private static void AppendMarkers(StringBuilder svg, IReadOnlyDictionary<string, string> markers)
    {
        svg.Append("<defs>\n");
        foreach ((string colour, string suffix) in markers)
        {
            string paint = Escape(colour);
            svg.Append(Inv($"<marker id=\"arrow-arrow{suffix}\" markerWidth=\"10\" markerHeight=\"10\" refX=\"8\" refY=\"4\" orient=\"auto-start-reverse\"><path d=\"M0,0 L8,4 L0,8 Z\" fill=\"{paint}\"/></marker>\n"));
            svg.Append(Inv($"<marker id=\"arrow-open{suffix}\" markerWidth=\"10\" markerHeight=\"10\" refX=\"8\" refY=\"4\" orient=\"auto-start-reverse\"><path d=\"M0,0 L8,4 L0,8\" fill=\"none\" stroke=\"{paint}\"/></marker>\n"));
            svg.Append(Inv($"<marker id=\"arrow-circle{suffix}\" markerWidth=\"10\" markerHeight=\"10\" refX=\"5\" refY=\"4\" orient=\"auto\"><circle cx=\"4\" cy=\"4\" r=\"3\" fill=\"{paint}\"/></marker>\n"));
            svg.Append(Inv($"<marker id=\"arrow-diamond{suffix}\" markerWidth=\"10\" markerHeight=\"10\" refX=\"5\" refY=\"4\" orient=\"auto\"><polygon points=\"0,4 4,0 8,4 4,8\" fill=\"{paint}\"/></marker>\n"));

            // ER crow's foot ends: a tick for "one", three toes for "many", an optional circle when zero is allowed,
            // and a double tick for "one and only one". Each draws with the line approaching from the left and the
            // entity on the right, mirroring the canvas markers (web/src/canvas/markers.tsx).
            svg.Append(Inv($"<marker id=\"arrow-one{suffix}\" markerWidth=\"12\" markerHeight=\"12\" refX=\"10\" refY=\"6\" orient=\"auto-start-reverse\"><path d=\"M10,1 L10,11\" fill=\"none\" stroke=\"{paint}\"/></marker>\n"));
            svg.Append(Inv($"<marker id=\"arrow-many{suffix}\" markerWidth=\"12\" markerHeight=\"12\" refX=\"10\" refY=\"6\" orient=\"auto-start-reverse\"><path d=\"M3,6 L11,2 M3,6 L11,6 M3,6 L11,10\" fill=\"none\" stroke=\"{paint}\"/></marker>\n"));
            svg.Append(Inv($"<marker id=\"arrow-zeroOrOne{suffix}\" markerWidth=\"12\" markerHeight=\"12\" refX=\"10\" refY=\"6\" orient=\"auto-start-reverse\"><circle cx=\"5\" cy=\"6\" r=\"2.5\" fill=\"none\" stroke=\"{paint}\"/><path d=\"M10,1 L10,11\" fill=\"none\" stroke=\"{paint}\"/></marker>\n"));
            svg.Append(Inv($"<marker id=\"arrow-oneOrMany{suffix}\" markerWidth=\"12\" markerHeight=\"12\" refX=\"10\" refY=\"6\" orient=\"auto-start-reverse\"><path d=\"M10,1 L10,11\" fill=\"none\" stroke=\"{paint}\"/><path d=\"M3,6 L11,2 M3,6 L11,6 M3,6 L11,10\" fill=\"none\" stroke=\"{paint}\"/></marker>\n"));
            svg.Append(Inv($"<marker id=\"arrow-zeroOrMany{suffix}\" markerWidth=\"12\" markerHeight=\"12\" refX=\"10\" refY=\"6\" orient=\"auto-start-reverse\"><circle cx=\"5\" cy=\"6\" r=\"2.5\" fill=\"none\" stroke=\"{paint}\"/><path d=\"M3,6 L11,2 M3,6 L11,6 M3,6 L11,10\" fill=\"none\" stroke=\"{paint}\"/></marker>\n"));
            svg.Append(Inv($"<marker id=\"arrow-oneAndOnlyOne{suffix}\" markerWidth=\"12\" markerHeight=\"12\" refX=\"10\" refY=\"6\" orient=\"auto-start-reverse\"><path d=\"M10,1 L10,11 M8,1 L8,11\" fill=\"none\" stroke=\"{paint}\"/></marker>\n"));

            // UML ends: an open triangle for inheritance, a hollow diamond for aggregation, a filled one for composition.
            svg.Append(Inv($"<marker id=\"arrow-inheritance{suffix}\" markerWidth=\"12\" markerHeight=\"12\" refX=\"10\" refY=\"6\" orient=\"auto-start-reverse\"><path d=\"M2,1 L12,6 L2,11 Z\" fill=\"none\" stroke=\"{paint}\"/></marker>\n"));
            svg.Append(Inv($"<marker id=\"arrow-aggregation{suffix}\" markerWidth=\"12\" markerHeight=\"12\" refX=\"10\" refY=\"6\" orient=\"auto-start-reverse\"><polygon points=\"5,6 9,2 13,6 9,10\" fill=\"none\" stroke=\"{paint}\"/></marker>\n"));
            svg.Append(Inv($"<marker id=\"arrow-composition{suffix}\" markerWidth=\"12\" markerHeight=\"12\" refX=\"10\" refY=\"6\" orient=\"auto-start-reverse\"><polygon points=\"5,6 9,2 13,6 9,10\" fill=\"{paint}\"/></marker>\n"));
        }

        svg.Append("</defs>\n");
    }

    private static string DashArray(Dash? dash)
    {
        return dash switch
        {
            Dash.Dashed => " stroke-dasharray=\"6 4\"",
            Dash.Dotted => " stroke-dasharray=\"2 3\"",
            _ => "",
        };
    }

    /// <summary>The marker id tail for an arrowhead: its camelCase name, matching the model's JSON/DSL spelling.</summary>
    private static string MarkerName(ArrowHead head)
    {
        return JsonNamingPolicy.CamelCase.ConvertName(head.ToString());
    }

    /// <summary>
    /// Escapes markup and drops characters XML 1.0 cannot represent at all (they have no numeric escape either), so a
    /// label carrying a stray control byte still renders as a well-formed document.
    /// </summary>
    private static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                escaped.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            if (!XmlConvert.IsXmlChar(c))
            {
                continue;
            }

            switch (c)
            {
                case '&': escaped.Append("&amp;"); break;
                case '<': escaped.Append("&lt;"); break;
                case '>': escaped.Append("&gt;"); break;
                case '"': escaped.Append("&quot;"); break;
                default: escaped.Append(c); break;
            }
        }

        return escaped.ToString();
    }

    private static string Inv(FormattableString formattable)
    {
        return formattable.ToString(CultureInfo.InvariantCulture);
    }
}
