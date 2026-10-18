using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Models;

/// <summary>Sparse node style. Every member is optional; null means inherit from the diagram theme plus the kind default.</summary>
public sealed record NodeStyle
{
    public string? Fill { get; init; }
    public string? Stroke { get; init; }
    public double? StrokeWidth { get; init; }
    public string? TextColor { get; init; }
    public string? Font { get; init; }
    public Dash? Dash { get; init; }
    public double? FontSize { get; init; }
    public bool? Bold { get; init; }
}
