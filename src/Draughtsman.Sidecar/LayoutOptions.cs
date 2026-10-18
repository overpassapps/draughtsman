namespace Draughtsman.Sidecar;

/// <summary>ELK algorithm. Layered for flowcharts and architecture, stress for freeform graphs, tree for org charts.</summary>
public enum LayoutAlgorithm
{
    Layered,
    Stress,
    Tree,
}

/// <summary>Flow direction for the layered algorithm.</summary>
public enum LayoutDirection
{
    Down,
    Right,
    Up,
    Left,
}

/// <summary>
/// Options for layout.elk. Every member is optional; null means the sidecar's default, which for the algorithm
/// follows the document type (stress for freeform, layered otherwise). Serialised with the document JSON options,
/// so enum values cross the wire as camelCase strings.
/// </summary>
public sealed record LayoutOptions
{
    public LayoutAlgorithm? Algorithm { get; init; }

    public LayoutDirection? Direction { get; init; }
}
