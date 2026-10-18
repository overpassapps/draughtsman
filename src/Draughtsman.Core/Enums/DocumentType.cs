namespace Draughtsman.Core.Enums;

/// <summary>Document type. Selects the kind vocabulary and the default layout algorithm.</summary>
public enum DocumentType
{
    Flowchart,
    Architecture,
    Freeform,

    /// <summary>Participants (nodes) and messages (edges); the edge order is time and every position is derived, never stored.</summary>
    Sequence,
}
