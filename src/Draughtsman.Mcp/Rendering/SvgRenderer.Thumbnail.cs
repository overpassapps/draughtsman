using System.Text;
using Draughtsman.Core.Models;
using Draughtsman.Sidecar;

namespace Draughtsman.Mcp.Rendering;

/// <summary>How much of a document a thumbnail draws.</summary>
public enum ThumbnailDetail
{
    /// <summary>The whole picture, exactly as <see cref="SvgRenderer.Render"/> draws it, scaled down into the thumbnail box.</summary>
    Full,

    /// <summary>Boxes and straight lines in the theme's colours: no labels, markers, routes or shape detail. The cheap picture for a diagram too large to draw in full.</summary>
    Simplified,
}

/// <summary>Why a thumbnail is a neutral placeholder instead of a drawing of the document.</summary>
public enum ThumbnailPlaceholder
{
    /// <summary>The document has no nodes and no edges yet: a dashed empty frame.</summary>
    Empty,

    /// <summary>The document is larger than the thumbnail limits allow even in simplified form.</summary>
    TooLarge,

    /// <summary>The document could not be drawn (an id used twice, or the layout engine was not available).</summary>
    Unavailable,
}

/// <summary>
/// The thumbnail of a document, an SVG scaled to a fixed <see cref="ThumbnailWidth"/> by <see cref="ThumbnailHeight"/> box
/// for the documents page (docs/api.md <c>GET /api/documents/{id}/thumbnail</c>). It is the same renderer the PDF export and
/// <c>render_document</c> use, so it carries the organisation's diagram theme and needs no browser: <see cref="ThumbnailDetail.Full"/>
/// wraps <see cref="SvgRenderer.Render"/>'s own picture, and <see cref="ThumbnailDetail.Simplified"/> is a separate, cheap drawing
/// for a diagram too large to draw in full. Neither changes what <see cref="SvgRenderer.Render"/> writes. Image nodes draw as the plain
/// frame (no asset bytes are read or embedded), and labels are escaped by the same code as every other render, so a label
/// can never put markup in the file; the endpoint serves it as an image with a locked-down policy besides.
/// </summary>
public static partial class SvgRenderer
{
    /// <summary>The thumbnail's width, in the SVG's own units (the endpoint's contract; the web card reserves the same ratio).</summary>
    public const int ThumbnailWidth = 320;

    /// <summary>The thumbnail's height, in the SVG's own units.</summary>
    public const int ThumbnailHeight = 200;

    /// <summary>A small diagram is shown a little larger than life, never blown up to fill the box.</summary>
    private const double ThumbnailMaxScale = 1.5;

    /// <summary>The width, in thumbnail pixels, every line of a simplified thumbnail is drawn with.</summary>
    private const double ThumbnailLinePixels = 1;

    private const string PlaceholderStroke = "#94a3b8";

    /// <summary>
    /// A thumbnail of <paramref name="document"/>. Every node must already have geometry, exactly as for
    /// <see cref="Render"/>; <paramref name="routes"/> and <paramref name="sequence"/> mean what they do there. A
    /// <see cref="ThumbnailDetail.Simplified"/> thumbnail ignores both (it draws a straight line from node to node, and has no
    /// picture for a <c>sequence</c> document, which stores no positions: the caller gives that type a placeholder).
    /// </summary>
    public static SvgRenderResult RenderThumbnail(Document document, ThumbnailDetail detail, IReadOnlyDictionary<string, IReadOnlyList<Point>>? routes = null, SequenceScene? sequence = null, DiagramThemeChoice? theme = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (detail == ThumbnailDetail.Simplified)
        {
            return new SvgRenderResult(SimplifiedThumbnail(document, theme), []);
        }

        Rendered rendered = RenderCore(document, 1, null, routes, sequence, theme);
        const string closing = "</svg>\n";
        string body = rendered.Svg.Substring(rendered.BodyStart, rendered.Svg.Length - rendered.BodyStart - closing.Length);
        double scale = Math.Min(Math.Min(ThumbnailWidth / rendered.Width, ThumbnailHeight / rendered.Height), ThumbnailMaxScale);
        return new SvgRenderResult(ThumbnailFrame(rendered.Background, rendered.Width * scale, rendered.Height * scale, Inv($"0 0 {rendered.Width} {rendered.Height}"), body), rendered.Warnings);
    }

    /// <summary>A neutral thumbnail, in the organisation's paper colour, for a document that has nothing to draw or cannot be drawn.</summary>
    public static string RenderThumbnailPlaceholder(ThumbnailPlaceholder kind, DiagramThemeChoice? theme = null)
    {
        string paper = DiagramTheme.For(theme).ExportBackground;
        double cx = ThumbnailWidth / 2.0;
        double cy = ThumbnailHeight / 2.0;
        var svg = new StringBuilder();
        AppendThumbnailRoot(svg, paper);
        if (kind == ThumbnailPlaceholder.Empty)
        {
            // An empty document: a dashed frame with a plus, the "nothing here yet" mark of the editor's own empty canvas.
            svg.Append(Inv($"<rect x=\"{cx - 56}\" y=\"{cy - 36}\" width=\"112\" height=\"72\" rx=\"8\" fill=\"none\" stroke=\"{PlaceholderStroke}\" stroke-width=\"1.5\" stroke-dasharray=\"5 4\"/>\n"));
            svg.Append(Inv($"<path d=\"M{cx - 9},{cy} H{cx + 9} M{cx},{cy - 9} V{cy + 9}\" fill=\"none\" stroke=\"{PlaceholderStroke}\" stroke-width=\"1.5\" stroke-linecap=\"round\"/>\n"));
        }
        else
        {
            // Three boxes and the lines joining them: any diagram, drawn small and grey, so a card still reads as one.
            svg.Append(Inv($"<g fill=\"none\" stroke=\"{PlaceholderStroke}\" stroke-width=\"1.5\" stroke-linecap=\"round\" stroke-linejoin=\"round\">\n"));
            svg.Append(Inv($"<rect x=\"{cx - 22}\" y=\"{cy - 44}\" width=\"44\" height=\"24\" rx=\"5\"/>\n"));
            svg.Append(Inv($"<rect x=\"{cx - 70}\" y=\"{cy + 20}\" width=\"44\" height=\"24\" rx=\"5\"/>\n"));
            svg.Append(Inv($"<rect x=\"{cx + 26}\" y=\"{cy + 20}\" width=\"44\" height=\"24\" rx=\"5\"/>\n"));
            svg.Append(Inv($"<path d=\"M{cx},{cy - 20} V{cy} M{cx - 48},{cy + 20} V{cy} H{cx + 48} V{cy + 20}\"/>\n"));
            svg.Append("</g>\n");
        }

        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static void AppendThumbnailRoot(StringBuilder svg, string paper)
    {
        svg.Append(Inv($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {ThumbnailWidth} {ThumbnailHeight}\" width=\"{ThumbnailWidth}\" height=\"{ThumbnailHeight}\">\n"));
        svg.Append(Inv($"<rect x=\"0\" y=\"0\" width=\"{ThumbnailWidth}\" height=\"{ThumbnailHeight}\" fill=\"{Escape(paper)}\"/>\n"));
    }

    /// <summary>The fixed-size thumbnail with a picture of <paramref name="fittedWidth"/> by <paramref name="fittedHeight"/> centred in it, the picture's own coordinates given by <paramref name="viewBox"/>.</summary>
    private static string ThumbnailFrame(string paper, double fittedWidth, double fittedHeight, string viewBox, string body)
    {
        var svg = new StringBuilder(body.Length + 400);
        AppendThumbnailRoot(svg, paper);
        double x = (ThumbnailWidth - fittedWidth) / 2;
        double y = (ThumbnailHeight - fittedHeight) / 2;
        svg.Append(Inv($"<svg x=\"{Math.Round(x, 2)}\" y=\"{Math.Round(y, 2)}\" width=\"{Math.Round(fittedWidth, 2)}\" height=\"{Math.Round(fittedHeight, 2)}\" viewBox=\"{viewBox}\">\n"));
        svg.Append(body);
        svg.Append("</svg>\n</svg>\n");
        return svg.ToString();
    }

    /// <summary>
    /// Boxes and lines only. The themed document is walked once: one straight line per edge (centre to centre, so the node
    /// drawn over its end hides the join) in a single path, and each node as a plain shape, grouped by its colours so a large
    /// diagram's markup stays small. Positions are rounded to whole units, which at thumbnail scale is under a pixel.
    /// </summary>
    private static string SimplifiedThumbnail(Document document, DiagramThemeChoice? theme)
    {
        (Document themed, SurfacePaint surface) = ApplyDiagramTheme(document, theme);
        Node[] nodes = themed.Nodes.Where(n => n.Geometry is not null).ToArray();
        if (nodes.Length == 0)
        {
            return RenderThumbnailPlaceholder(ThumbnailPlaceholder.Empty, theme);
        }

        var boxes = nodes.Select(node =>
        {
            Geometry g = node.Geometry!;
            return RotatedExtent(g.X + g.W / 2, g.Y + g.H / 2, g.W / 2, g.H / 2, g.Rotation ?? 0);
        }).ToArray();
        double minX = boxes.Min(b => b.Left) - Padding;
        double minY = boxes.Min(b => b.Top) - Padding;
        double width = Math.Max(boxes.Max(b => b.Right) + Padding - minX, 1);
        double height = Math.Max(boxes.Max(b => b.Bottom) + Padding - minY, 1);
        double scale = Math.Min(Math.Min(ThumbnailWidth / width, ThumbnailHeight / height), ThumbnailMaxScale);
        double line = ThumbnailLinePixels / scale;

        var body = new StringBuilder();
        Dictionary<string, Node> byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var path = new StringBuilder();
        foreach (Edge edge in themed.Edges)
        {
            if (byId.TryGetValue(edge.From.Node, out Node? from) && byId.TryGetValue(edge.To.Node, out Node? to) && !ReferenceEquals(from, to))
            {
                path.Append(Inv($"M{Math.Round(from.Geometry!.X + from.Geometry.W / 2)},{Math.Round(from.Geometry.Y + from.Geometry.H / 2)}L{Math.Round(to.Geometry!.X + to.Geometry.W / 2)},{Math.Round(to.Geometry.Y + to.Geometry.H / 2)}"));
            }
        }

        if (path.Length > 0)
        {
            body.Append(Inv($"<path d=\"{path}\" fill=\"none\" stroke=\"{Escape(surface.Text)}\" stroke-opacity=\"0.55\" stroke-width=\"{Math.Round(line, 2)}\"/>\n"));
        }

        // Containers first, so what they hold paints on top; each style is one group, in the order its first node appears.
        var groups = new List<(string Fill, string Stroke, StringBuilder Shapes)>();
        foreach (Node node in OrderForDrawing(nodes))
        {
            string fill = node.Style?.Fill ?? DefaultFill;
            string stroke = node.Style?.Stroke ?? DefaultStroke;
            int index = groups.FindIndex(group => group.Fill == fill && group.Stroke == stroke);
            if (index < 0)
            {
                groups.Add((fill, stroke, new StringBuilder()));
                index = groups.Count - 1;
            }

            AppendSimplifiedShape(groups[index].Shapes, node);
        }

        foreach ((string fill, string stroke, StringBuilder shapes) in groups)
        {
            body.Append(Inv($"<g fill=\"{Escape(fill)}\" stroke=\"{Escape(stroke)}\" stroke-width=\"{Math.Round(line, 2)}\">\n"));
            body.Append(shapes);
            body.Append("</g>\n");
        }

        return ThumbnailFrame(surface.Background, width * scale, height * scale, Inv($"{Math.Round(minX, 2)} {Math.Round(minY, 2)} {Math.Round(width, 2)} {Math.Round(height, 2)}"), body.ToString());
    }

    private static void AppendSimplifiedShape(StringBuilder svg, Node node)
    {
        Geometry g = node.Geometry!;
        double x = Math.Round(g.X);
        double y = Math.Round(g.Y);
        double w = Math.Round(g.W);
        double h = Math.Round(g.H);
        string transform = g.Rotation is { } rotation && rotation != 0
            ? Inv($" transform=\"rotate({rotation} {Math.Round(g.X + g.W / 2)} {Math.Round(g.Y + g.H / 2)})\"")
            : "";
        string shape = node.Shape;
        if (shape == "diamond")
        {
            svg.Append(Inv($"<polygon points=\"{x + (w / 2)},{y} {x + w},{y + (h / 2)} {x + (w / 2)},{y + h} {x},{y + (h / 2)}\"{transform}/>\n"));
        }
        else if (shape is "ellipse" or "circle")
        {
            svg.Append(Inv($"<ellipse cx=\"{x + (w / 2)}\" cy=\"{y + (h / 2)}\" rx=\"{w / 2}\" ry=\"{h / 2}\"{transform}/>\n"));
        }
        else
        {
            // A pill is a rectangle rounded to half its height; every other shape (and a container) is drawn as its box.
            double radius = shape == "pill" ? h / 2 : shape == "rounded" ? Math.Min(8, h / 2) : 0;
            svg.Append(radius > 0
                ? Inv($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" rx=\"{radius}\"{transform}/>\n")
                : Inv($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\"{transform}/>\n"));
        }
    }
}
