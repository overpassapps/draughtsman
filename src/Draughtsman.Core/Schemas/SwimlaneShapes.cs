namespace Draughtsman.Core.Schemas;

/// <summary>
/// The swimlane container shapes and the geometry both renderers share. A pool and a lane are drawn natively
/// (web/src/canvas/shapes.tsx and <c>SvgRenderer.cs</c>) as a rectangle with a title band along its top; only their
/// band height differs. The kind names and shape names are the contract the document schema, the DSL vocabulary and
/// the palette all use; a test on each side sweeps <see cref="Shapes"/> so a shape added to one renderer and not the
/// other fails rather than silently drawing as a plain box. Mirrors web/src/model/swimlanes.ts.
/// </summary>
public static class SwimlaneShapes
{
    /// <summary>The semantic kind of a swimlane pool and of a lane.</summary>
    public const string PoolKind = "pool";
    public const string LaneKind = "lane";

    /// <summary>A pool's presentation shape: <c>custom:swimlane/pool</c>.</summary>
    public const string PoolShape = "custom:swimlane/pool";

    /// <summary>A lane's presentation shape: <c>custom:swimlane/lane</c>.</summary>
    public const string LaneShape = "custom:swimlane/lane";

    /// <summary>Every swimlane shape, for the render sweeps.</summary>
    public static readonly IReadOnlyList<string> Shapes = [PoolShape, LaneShape];

    private const double PoolHeaderRatio = 0.2;
    private const double PoolHeaderMin = 24;
    private const double PoolHeaderMax = 48;
    private const double LaneHeaderRatio = 0.15;
    private const double LaneHeaderMin = 16;
    private const double LaneHeaderMax = 32;

    /// <summary>True when the shape is a swimlane pool or lane.</summary>
    public static bool IsSwimlane(string shape)
    {
        return string.Equals(shape, PoolShape, StringComparison.Ordinal) || string.Equals(shape, LaneShape, StringComparison.Ordinal);
    }

    /// <summary>True when the kind is a swimlane container (a pool or a lane).</summary>
    public static bool IsSwimlaneKind(string kind)
    {
        return string.Equals(kind, PoolKind, StringComparison.Ordinal) || string.Equals(kind, LaneKind, StringComparison.Ordinal);
    }

    /// <summary>The pool title band's height for a box of height <paramref name="h"/>. Mirrors shapes.tsx.</summary>
    public static double PoolHeaderHeight(double h) => Math.Min(h, Math.Max(PoolHeaderMin, Math.Min(h * PoolHeaderRatio, PoolHeaderMax)));

    /// <summary>The lane title band's height for a box of height <paramref name="h"/>. Mirrors shapes.tsx.</summary>
    public static double LaneHeaderHeight(double h) => Math.Min(h, Math.Max(LaneHeaderMin, Math.Min(h * LaneHeaderRatio, LaneHeaderMax)));
}
