using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Mcp.Rendering;

/// <summary>
/// Where an edge meets its node: the port fan-out for connectors sharing a port and the exit point of an end with no
/// port. Mirrors <c>web/src/canvas/ports.ts</c> (<c>sharedPortOffsets</c>, <c>outlineKind</c>, <c>outlineExit</c>,
/// <c>outlinePortPoint</c>, <c>nudgedPortPoint</c>) so the canvas and this renderer start every connector on the same
/// point of the node's drawn outline (QA-01). A diamond or ellipse is not its box: a point slid along the box's side
/// or clipped to the box floats off the outline.
/// </summary>
internal static class EdgeAttachment
{
    /// <summary>How far apart, in canvas units, two connectors sharing a port fan out (ports.ts <c>SHARED_PORT_STEP</c>).</summary>
    internal const double SharedPortStep = 16;

    /// <summary>The fan step for connectors sharing a column port, which is one table row high (ports.ts <c>COLUMN_PORT_STEP</c>).</summary>
    private const double ColumnPortStep = 6;

    /// <summary>How close a fanned connector may come to the end of its side (ports.ts <c>SIDE_MARGIN</c>).</summary>
    private const double SideMargin = 4;

    internal enum Outline { None, Diamond, Ellipse }

    private enum Side { Top, Right, Bottom, Left }

    /// <summary>The outline a node draws inside its box: a diamond (the decision shape, a BPMN gateway) or an ellipse (a BPMN event, the basic library's ellipse).</summary>
    internal static Outline OutlineOf(string shape)
    {
        if (shape == "diamond")
        {
            return Outline.Diamond;
        }

        if (shape == BasicShapeLibrary.Prefix + "ellipse")
        {
            return Outline.Ellipse;
        }

        if (BpmnShapeLibrary.TryGet(shape, out BpmnShape? bpmn))
        {
            return bpmn!.Family == BpmnShapeLibrary.GatewayFamily ? Outline.Diamond
                : bpmn.Family == BpmnShapeLibrary.EventFamily ? Outline.Ellipse
                : Outline.None;
        }

        return Outline.None;
    }

    /// <summary>
    /// Every edge endpoint sharing a declared (node, port) with another, keyed <c>{edge.Id}:from</c> or
    /// <c>{edge.Id}:to</c>, to its tangential offset: 0 for the first sharer in document order, then alternating sides
    /// at growing distance. A self-loop never contributes.
    /// </summary>
    internal static IReadOnlyDictionary<string, double> SharedPortOffsets(IEnumerable<Edge> edges)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string node, string? port, string key)
        {
            if (port is null)
            {
                return;
            }

            string groupKey = $"{node}#{port}";
            if (!groups.TryGetValue(groupKey, out List<string>? list))
            {
                groups[groupKey] = list = [];
            }

            list.Add(key);
        }

        foreach (Edge edge in edges)
        {
            if (edge.From.Node == edge.To.Node)
            {
                continue;
            }

            Add(edge.From.Node, edge.From.Port, $"{edge.Id}:from");
            Add(edge.To.Node, edge.To.Port, $"{edge.Id}:to");
        }

        var offsets = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string groupKey, List<string> list) in groups)
        {
            if (list.Count < 2)
            {
                continue;
            }

            double step = ErShapeLibrary.IsColumnPortId(groupKey[(groupKey.IndexOf('#') + 1)..]) ? ColumnPortStep : SharedPortStep;

            for (int i = 1; i < list.Count; i++)
            {
                offsets[list[i]] = (i % 2 == 1 ? 1 : -1) * Math.Ceiling(i / 2.0) * step;
            }
        }

        return offsets;
    }

    /// <summary>
    /// A declared port of a node, nudged <paramref name="offset"/> units along its side (never off the outline for a
    /// diamond or ellipse), in the renderer's coordinates (<paramref name="offsetX"/>/<paramref name="offsetY"/> removed).
    /// </summary>
    internal static (double X, double Y) NudgedPort(Node node, Port port, double offset, double offsetX, double offsetY)
    {
        Geometry g = node.Geometry!;
        double cx = g.X - offsetX + g.W / 2;
        double cy = g.Y - offsetY + g.H / 2;
        double rotation = g.Rotation ?? 0;
        Outline outline = offset == 0 ? Outline.None : OutlineOf(node.Shape);
        if (outline != Outline.None)
        {
            Side local = SideOf(0, port);
            double halfW = g.W / 2;
            double halfH = g.H / 2;
            double x = (port.X - 0.5) * g.W;
            double y = (port.Y - 0.5) * g.H;
            if (local is Side.Top or Side.Bottom)
            {
                x = Math.Clamp(x + WithinSide(offset, g.W), -halfW, halfW);
                y = (local == Side.Top ? -1 : 1) * halfH * Reach(outline, x / halfW);
            }
            else
            {
                y = Math.Clamp(y + WithinSide(offset, g.H), -halfH, halfH);
                x = (local == Side.Left ? -1 : 1) * halfW * Reach(outline, y / halfH);
            }

            var turned = Turn(x, y, rotation);
            return (cx + turned.X, cy + turned.Y);
        }

        var delta = Turn((port.X - 0.5) * g.W, (port.Y - 0.5) * g.H, rotation);
        (double px, double py) = (cx + delta.X, cy + delta.Y);
        if (offset == 0)
        {
            return (px, py);
        }

        double along = SideOf(0, port) is Side.Top or Side.Bottom ? g.W : g.H;
        double nudge = WithinSide(offset, along);
        return SideOf(rotation, port) is Side.Top or Side.Bottom ? (px + nudge, py) : (px, py + nudge);
    }

    /// <summary><paramref name="offset"/> limited so the fanned point stays on its side, <see cref="SideMargin"/> from each end (ports.ts <c>withinSide</c>).</summary>
    private static double WithinSide(double offset, double span)
    {
        double limit = Math.Max(0, (span / 2) - SideMargin);
        return Math.Max(-limit, Math.Min(limit, offset));
    }

    /// <summary>
    /// Where a connector leaves an ellipse or diamond node heading for (<paramref name="towardX"/>,
    /// <paramref name="towardY"/>): where the line from the centre crosses the outline, or, when
    /// <paramref name="aligned"/> (an orthogonal connector with a routed bend), straight towards the bend along its
    /// axis. A target inside the outline is returned unchanged.
    /// </summary>
    internal static (double X, double Y) Exit(Node node, Outline outline, double towardX, double towardY, bool aligned, double offsetX, double offsetY)
    {
        Geometry g = node.Geometry!;
        double cx = g.X - offsetX + g.W / 2;
        double cy = g.Y - offsetY + g.H / 2;
        double halfW = g.W / 2;
        double halfH = g.H / 2;
        var local = Turn(towardX - cx, towardY - cy, -(g.Rotation ?? 0));
        double nx = local.X / halfW;
        double ny = local.Y / halfH;
        double lx;
        double ly;
        if (aligned && Math.Abs(ny) >= Math.Abs(nx) && Math.Abs(nx) < 1)
        {
            lx = nx;
            ly = Math.Sign(ny) * Math.Min(Math.Abs(ny), Reach(outline, nx));
        }
        else if (aligned && Math.Abs(nx) > Math.Abs(ny) && Math.Abs(ny) < 1)
        {
            lx = Math.Sign(nx) * Math.Min(Math.Abs(nx), Reach(outline, ny));
            ly = ny;
        }
        else
        {
            double reach = outline == Outline.Diamond ? Math.Abs(nx) + Math.Abs(ny) : Math.Sqrt((nx * nx) + (ny * ny));
            double scale = reach == 0 ? 1 : Math.Min(1, 1 / reach);
            lx = nx * scale;
            ly = ny * scale;
        }

        var turned = Turn(lx * halfW, ly * halfH, g.Rotation ?? 0);
        return (cx + turned.X, cy + turned.Y);
    }

    private static double Reach(Outline outline, double t) =>
        outline == Outline.Diamond ? 1 - Math.Abs(t) : Math.Sqrt(Math.Max(0, 1 - (t * t)));

    /// <summary>A clockwise turn by <paramref name="degrees"/>, exact for quarter-turns like ports.ts <c>turn</c>.</summary>
    private static (double X, double Y) Turn(double x, double y, double degrees)
    {
        double cos;
        double sin;
        if (degrees % 90 == 0)
        {
            int quarter = (((int)(degrees / 90) % 4) + 4) % 4;
            cos = new double[] { 1, 0, -1, 0 }[quarter];
            sin = new double[] { 0, 1, 0, -1 }[quarter];
        }
        else
        {
            double radians = degrees * Math.PI / 180;
            cos = Math.Cos(radians);
            sin = Math.Sin(radians);
        }

        return (x * cos - y * sin, x * sin + y * cos);
    }

    /// <summary>The side a port leaves by: the nearest side of the unrotated box (ties top, right, bottom, left), turned by the rotation's quarter-turns. Mirrors ports.ts <c>portSide</c>.</summary>
    private static Side SideOf(double rotation, Port port)
    {
        double top = port.Y;
        double right = 1 - port.X;
        double bottom = 1 - port.Y;
        double left = port.X;
        double nearest = Math.Min(Math.Min(top, right), Math.Min(bottom, left));
        int local = top == nearest ? 0 : right == nearest ? 1 : bottom == nearest ? 2 : 3;
        int quarters = (int)Math.Floor((rotation / 90) + 0.5);
        return (Side)((((local + quarters) % 4) + 4) % 4);
    }
}
