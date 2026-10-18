using System.Text;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Sidecar;

namespace Draughtsman.Mcp.Rendering;

/// <summary>
/// A <c>sequence</c> document, drawn from its derived scene (web/src/model/sequenceScene.ts, run by the sidecar's
/// <c>sequence.scene</c>): this renderer places nothing itself. It turns the scene into what the rest of the renderer
/// already draws - a header box per participant (the node's own geometry), each message a routed polyline (the
/// orthogonal-route path with the scene's points), its label centred where the scene says, above the line - and adds the
/// two layers a sequence diagram has that a graph does not, the lifelines and the activation bars. The message look
/// (dash, arrowhead, colour) comes from the shipped diagram theme through <see cref="DiagramTheme"/> (the vocabulary's
/// <see cref="KindDefinition.DefaultStyle"/> where the theme does not name the kind); the sparse style on an element still wins.
/// </summary>
public static partial class SvgRenderer
{
    /// <summary>What a render paints behind and directly on the picture: the page and the plates that sit on it, and text drawn on the page itself (edge labels, notes).</summary>
    private readonly record struct SurfacePaint(string Background, string Text)
    {
        /// <summary>The neutral page every non-sequence document is drawn on.</summary>
        public static SurfacePaint Neutral => new(BackgroundFill, DefaultText);
    }

    /// <summary>
    /// The document (any type) with every node and edge carrying its fully resolved style (the diagram theme's
    /// kind default, the document's token overrides, its own sparse style over them: <see cref="DiagramTheme"/>), and the surface
    /// the export paints: the theme's paper and the text colour that reads on it. A label that sits straight on the paper (a
    /// see-through fill such as a fragment's) takes the surface's text colour unless the node or a document override named one,
    /// as web/src/canvas/renderedStyle.ts <c>withSurfaceText</c> decides for the client.
    /// </summary>
    private static (Document Source, SurfacePaint Surface) ApplyDiagramTheme(Document document, DiagramThemeChoice? choice)
    {
        DiagramTheme workspace = DiagramTheme.For(choice);
        DiagramTheme effective = workspace.WithOverrides(document.Theme);
        string? surfaceText = effective.SurfaceText();
        // A freeform document has no vocabulary: its kinds are the theme's own, and an edge has no kind default to fall back to.
        KindVocabulary.TryGet(document.Type, out KindVocabulary? vocabulary);

        List<Node> nodes = document.Nodes.Select(node =>
        {
            NodeStyle style = effective.Node(node.Kind, node.Style);
            bool onSurface = surfaceText is not null
                && string.Equals(style.Fill, "transparent", StringComparison.OrdinalIgnoreCase)
                && node.Style?.TextColor is null
                && string.Equals(effective.KindTextColour(node.Kind), workspace.KindTextColour(node.Kind), StringComparison.Ordinal);
            return node with { Style = onSurface ? style with { TextColor = surfaceText } : style };
        }).ToList();
        List<Edge> edges = document.Edges.Select(edge => edge with
        {
            Style = effective.Edge(edge.Kind, edge.Style, vocabulary?.EdgeKinds.FirstOrDefault(k => string.Equals(k.Name, edge.Kind, StringComparison.Ordinal))?.DefaultStyle),
        }).ToList();

        return (document with { Nodes = nodes, Edges = edges }, new SurfacePaint(effective.ExportBackground, surfaceText ?? DefaultText));
    }

    private static (Document Document, IReadOnlyDictionary<string, IReadOnlyList<Point>> Routes, IReadOnlyDictionary<string, (double X, double Y)> Labels) ApplySequenceScene(Document document, SequenceScene scene)
    {
        Dictionary<string, SequenceHeader> headers = scene.Headers.ToDictionary(h => h.Id, StringComparer.Ordinal);
        Dictionary<string, SequenceMessage> messages = scene.Messages.ToDictionary(m => m.Id, StringComparer.Ordinal);

        // A combined fragment is a frame, not a box, and a note is a box, not a line: both are drawn by AppendSequenceLayer
        // from the scene, so neither goes through the generic node and edge drawing.
        List<Node> nodes = document.Nodes
            .Where(node => headers.ContainsKey(node.Id) || !string.Equals(node.Kind, "fragment", StringComparison.Ordinal))
            .Select(node => headers.TryGetValue(node.Id, out SequenceHeader? header) ? node with { Shape = ParticipantShape(node), Geometry = new Geometry(header.X, header.Y, header.W, header.H) } : node)
            .ToList();

        var routes = new Dictionary<string, IReadOnlyList<Point>>(StringComparer.Ordinal);
        var labels = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        List<Edge> edges = document.Edges.Where(edge => !(messages.TryGetValue(edge.Id, out SequenceMessage? row) && row.Note is not null)).Select(edge =>
        {
            if (!messages.TryGetValue(edge.Id, out SequenceMessage? message))
            {
                return edge;
            }

            routes[edge.Id] = message.Points;
            if (message.LabelAt is { } at)
            {
                labels[edge.Id] = (at.X, at.Y);
            }

            // The message's route arrives through the orthogonal-route path, whatever routing the document names.
            return edge with { Routing = new Routing(RoutingMode.Orthogonal) };
        }).ToList();

        return (document with { Nodes = nodes, Edges = edges }, routes, labels);
    }

    /// <summary>The shape name an actor header is drawn with (sequenceScene.ts <c>ACTOR_SHAPE</c>).</summary>
    private const string ActorShape = "custom:sequence/actor";

    /// <summary>The top part of an actor header the stick figure takes, centred over the lifeline; the name is under it (SEQUENCE_METRICS.actorFigureHeight).</summary>
    private const double ActorFigureHeight = 26;

    /// <summary>
    /// The shape a participant is drawn with, as sequenceScene.ts <c>participantShape</c>: an <c>actor</c> is a stick figure
    /// whatever its stored shape says when that is <c>rounded</c> (the kind's default) or the figure itself.
    /// </summary>
    private static string ParticipantShape(Node node) =>
        string.Equals(node.Kind, "actor", StringComparison.Ordinal) && (node.Shape == "rounded" || node.Shape == ActorShape) ? ActorShape : node.Shape;

    /// <summary>
    /// The stick figure of an actor header, mirroring web/src/canvas/shapes.tsx <c>actorRender</c>: head, body, arms and
    /// legs as fractions of the figure's height (that band less 2 either side), in the top <see cref="ActorFigureHeight"/>, centred on the header.
    /// </summary>
    private static string ActorMarkup(double x, double y, double w, double h, string fill, string stroke, double strokeWidth)
    {
        double fh = ActorFigureHeight - 4;
        double top = y + 2;
        double cx = x + w / 2;
        double armReach = fh * 0.36;
        double legReach = fh * 0.28;
        double r = fh * 0.16;
        double neck = top + 2 * r;
        double arms = neck + fh * 0.12;
        double hip = top + fh * 0.65;
        return Inv($"<rect class=\"shape\" x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"transparent\" stroke=\"none\"/>\n<circle class=\"shape-detail\" cx=\"{R2(cx)}\" cy=\"{R2(top + r)}\" r=\"{R2(r)}\" fill=\"{Escape(fill)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"/>\n<path class=\"shape-detail\" d=\"M{R2(cx)},{R2(neck)} V{R2(hip)} M{R2(cx - armReach)},{R2(arms)} H{R2(cx + armReach)} M{R2(cx - legReach)},{R2(top + fh)} L{R2(cx)},{R2(hip)} L{R2(cx + legReach)},{R2(top + fh)}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\" stroke-linecap=\"round\"/>\n");
    }

    /// <summary>
    /// Each lifeline, a dashed line from its header to the bottom of the picture, then the activation bars over them,
    /// both in the owning participant's own stroke (and, for a bar, fill), then the combined-fragment frames, the note
    /// boxes and the X that ends each destroyed lifeline, as the canvas's SequenceLayer draws them, in the same order.
    /// Drawn before the nodes and edges so a message's arrowhead lies over a bar. <paramref name="source"/> is the
    /// document as it was stored: the fragment nodes and note edges the generic drawing no longer sees.
    /// </summary>
    private static void AppendSequenceLayer(StringBuilder svg, Document source, SequenceScene scene, SurfacePaint surface, double offsetX, double offsetY)
    {
        Dictionary<string, Node> byId = source.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        Dictionary<string, Edge> edgesById = source.Edges.ToDictionary(e => e.Id, StringComparer.Ordinal);
        svg.Append("<g class=\"sequence-layer\">\n");
        foreach (SequenceLifeline lifeline in scene.Lifelines)
        {
            string stroke = byId.TryGetValue(lifeline.Id, out Node? node) ? node.Style?.Stroke ?? DefaultStroke : DefaultStroke;
            svg.Append(Inv($"<line data-lifeline=\"{Escape(lifeline.Id)}\" x1=\"{lifeline.X - offsetX}\" y1=\"{lifeline.Y0 - offsetY}\" x2=\"{lifeline.X - offsetX}\" y2=\"{lifeline.Y1 - offsetY}\" stroke=\"{Escape(stroke)}\" stroke-width=\"1\" stroke-dasharray=\"6 4\"/>\n"));
        }

        foreach (SequenceBar bar in scene.Bars)
        {
            Node? node = byId.GetValueOrDefault(bar.Participant);
            string fill = node?.Style?.Fill ?? DefaultFill;
            string stroke = node?.Style?.Stroke ?? DefaultStroke;
            svg.Append(Inv($"<rect data-lifeline=\"{Escape(bar.Participant)}\" x=\"{bar.X - offsetX}\" y=\"{bar.Y - offsetY}\" width=\"{bar.W}\" height=\"{bar.H}\" fill=\"{Escape(fill)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"1\"/>\n"));
        }

        foreach (SequenceFragment fragment in scene.Fragments)
        {
            AppendFragment(svg, fragment, byId.GetValueOrDefault(fragment.Id), surface, offsetX, offsetY);
        }

        foreach (SequenceMessage row in scene.Messages.Where(m => m.Note is not null))
        {
            Edge? edge = edgesById.GetValueOrDefault(row.Id);
            AppendNote(svg, row, edge?.Label, edge?.Style, surface, offsetX, offsetY);
        }

        foreach (SequenceDestroy destroy in scene.Destroys)
        {
            string stroke = byId.TryGetValue(destroy.Participant, out Node? node) ? node.Style?.Stroke ?? DefaultStroke : DefaultStroke;
            double x = destroy.X - offsetX;
            double y = destroy.Y - offsetY;
            svg.Append(Inv($"<path data-lifeline=\"{Escape(destroy.Participant)}\" d=\"M {x - DestroyMark} {y - DestroyMark} L {x + DestroyMark} {y + DestroyMark} M {x + DestroyMark} {y - DestroyMark} L {x - DestroyMark} {y + DestroyMark}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"2\"/>\n"));
        }

        svg.Append("</g>\n");
    }

    /// <summary>Half the side of the X that ends a destroyed lifeline (SEQUENCE_METRICS.destroyMark).</summary>
    private const double DestroyMark = 8;

    /// <summary>The folded corner of a note's box (SEQUENCE_METRICS.noteFold).</summary>
    private const double NoteFold = 8;

    /// <summary>The font size of an operator tab and a guard (SEQUENCE_METRICS.fragmentFontSize), which the scene's widths are estimated in.</summary>
    private const int FragmentFontSize = 12;

    /// <summary>
    /// A combined fragment: the frame, a dashed divider at each operand after the first, the operator tab (a rectangle with its
    /// lower right corner cut, the operator centred in it) and each guard where the scene starts it. The tab's fill is the
    /// node's own fill unless that is transparent, else the page's.
    /// </summary>
    private static void AppendFragment(StringBuilder svg, SequenceFragment fragment, Node? node, SurfacePaint surface, double offsetX, double offsetY)
    {
        string stroke = node?.Style?.Stroke ?? DefaultStroke;
        double strokeWidth = node?.Style?.StrokeWidth ?? 1.5;
        string text = node?.Style?.TextColor ?? DefaultText;
        string tagFill = node?.Style?.Fill is { } fill && !string.Equals(fill, "transparent", StringComparison.OrdinalIgnoreCase) ? fill : surface.Background;
        double x = fragment.X - offsetX;
        double y = fragment.Y - offsetY;
        svg.Append(Inv($"<g data-id=\"{Escape(fragment.Id)}\" data-kind=\"fragment\">\n"));
        svg.Append(Inv($"<rect x=\"{x}\" y=\"{y}\" width=\"{fragment.W}\" height=\"{fragment.H}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"/>\n"));
        foreach (SequenceFragmentSection section in fragment.Sections.Skip(1))
        {
            svg.Append(Inv($"<line x1=\"{x}\" y1=\"{section.Y - offsetY}\" x2=\"{x + fragment.W}\" y2=\"{section.Y - offsetY}\" stroke=\"{Escape(stroke)}\" stroke-width=\"1\" stroke-dasharray=\"6 4\"/>\n"));
        }

        svg.Append(Inv($"<path d=\"M {x} {y} H {x + fragment.TagWidth} V {y + fragment.TagHeight - 6} L {x + fragment.TagWidth - 6} {y + fragment.TagHeight} H {x} Z\" fill=\"{Escape(tagFill)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"1\"/>\n"));
        svg.Append(Inv($"<text x=\"{R2(x + fragment.TagWidth / 2)}\" y=\"{R2(y + fragment.TagHeight / 2)}\" fill=\"{Escape(text)}\" font-family=\"sans-serif\" font-size=\"{FragmentFontSize}\" font-weight=\"600\" text-anchor=\"middle\" dominant-baseline=\"central\" xml:space=\"preserve\">{Escape(fragment.Operator)}</text>\n"));
        foreach (SequenceFragmentSection section in fragment.Sections.Where(s => s.Guard.Length > 0))
        {
            svg.Append(Inv($"<text x=\"{R2(section.GuardAt.X - offsetX)}\" y=\"{R2(section.GuardAt.Y - offsetY)}\" fill=\"{Escape(text)}\" font-family=\"sans-serif\" font-size=\"{FragmentFontSize}\" dominant-baseline=\"central\" xml:space=\"preserve\">{Escape(section.Guard)}</text>\n"));
        }

        svg.Append("</g>\n");
    }

    /// <summary>
    /// An interaction use: a <c>ref</c> frame filled with the page colour (so no lifeline shows through), the cut-corner tab at
    /// its top left with <c>ref</c> centred in it, and the name of the interaction centred where the scene says, under the tab.
    /// Mirrors web/src/canvas/SequenceLayer.tsx <c>InteractionUseFrame</c>.
    /// </summary>
    private static void AppendInteractionUse(StringBuilder svg, SequenceMessage row, string? label, SequenceNote note, double tagWidth, string stroke, double strokeWidth, SurfacePaint surface, double x, double y, double offsetX, double offsetY)
    {
        svg.Append(Inv($"<g data-id=\"{Escape(row.Id)}\" data-kind=\"interaction-use\">\n"));
        svg.Append(Inv($"<rect x=\"{x}\" y=\"{y}\" width=\"{note.W}\" height=\"{note.H}\" fill=\"{Escape(surface.Background)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"/>\n"));
        svg.Append(Inv($"<path d=\"M {x} {y} H {x + tagWidth} V {y + FragmentTagHeight - 6} L {x + tagWidth - 6} {y + FragmentTagHeight} H {x} Z\" fill=\"{Escape(surface.Background)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"1\"/>\n"));
        svg.Append(Inv($"<text x=\"{R2(x + tagWidth / 2)}\" y=\"{R2(y + FragmentTagHeight / 2)}\" fill=\"{Escape(surface.Text)}\" font-family=\"sans-serif\" font-size=\"{FragmentFontSize}\" font-weight=\"600\" text-anchor=\"middle\" dominant-baseline=\"central\" xml:space=\"preserve\">ref</text>\n"));
        if (!string.IsNullOrEmpty(label) && row.LabelAt is { } at)
        {
            AppendLabel(svg, label, at.X - offsetX, at.Y - offsetY, surface.Text);
        }

        svg.Append("</g>\n");
    }

    /// <summary>The height of a fragment's operator tab, and of an interaction use's (SEQUENCE_METRICS.fragmentTagHeight).</summary>
    private const double FragmentTagHeight = 18;

    /// <summary>A note: a box with its upper right corner folded, filled with the page colour, its label centred in it.</summary>
    private static void AppendNote(StringBuilder svg, SequenceMessage row, string? label, EdgeStyle? style, SurfacePaint surface, double offsetX, double offsetY)
    {
        if (row.Note is not { } note)
        {
            return;
        }

        string stroke = style?.Stroke ?? DefaultStroke;
        double strokeWidth = style?.StrokeWidth ?? 1;
        double x = note.X - offsetX;
        double y = note.Y - offsetY;
        if (note.TagWidth is { } tagWidth)
        {
            AppendInteractionUse(svg, row, label, note, tagWidth, stroke, strokeWidth, surface, x, y, offsetX, offsetY);
            return;
        }

        svg.Append(Inv($"<g data-id=\"{Escape(row.Id)}\" data-kind=\"note\">\n"));
        svg.Append(Inv($"<path d=\"M {x} {y} H {x + note.W - NoteFold} L {x + note.W} {y + NoteFold} V {y + note.H} H {x} Z\" fill=\"{Escape(surface.Background)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\" stroke-linejoin=\"round\"/>\n"));
        svg.Append(Inv($"<path d=\"M {x + note.W - NoteFold} {y} V {y + NoteFold} H {x + note.W}\" fill=\"none\" stroke=\"{Escape(stroke)}\" stroke-width=\"{strokeWidth}\"/>\n"));
        if (!string.IsNullOrEmpty(label) && row.LabelAt is { } at)
        {
            AppendLabel(svg, label, at.X - offsetX, at.Y - offsetY, surface.Text);
        }

        svg.Append("</g>\n");
    }
}
