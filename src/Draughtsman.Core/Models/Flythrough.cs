using System.Text.Json.Serialization;

namespace Draughtsman.Core.Models;

/// <summary>
/// A camera path over the diagram: keyframes in tour order. It is a presentation of the graph, never part of it: no
/// node or edge refers to it, the renderers and the Mermaid export ignore it, and a document without one is unchanged
/// (docs/flythrough.md). A flythrough with no keyframes is absent, never empty.
/// </summary>
public sealed record Flythrough([property: JsonPropertyOrder(0)] IReadOnlyList<Keyframe> Keyframes)
{
    /// <summary>Whether the tour returns from the last keyframe to the first and plays on. Absent means it stops at the last.</summary>
    [JsonPropertyOrder(1)]
    public bool? Loop { get; init; }
}
