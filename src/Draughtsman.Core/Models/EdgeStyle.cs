using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Models;

/// <summary>Sparse edge style. Every member is optional; null means inherit from the diagram theme plus the edge kind default.</summary>
public sealed record EdgeStyle
{
    public string? Stroke { get; init; }
    public double? StrokeWidth { get; init; }
    public Dash? Dash { get; init; }
    public ArrowHead? ArrowStart { get; init; }
    public ArrowHead? ArrowEnd { get; init; }
}
