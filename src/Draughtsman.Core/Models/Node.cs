using System.Text.Json;
using System.Text.Json.Serialization;

namespace Draughtsman.Core.Models;

/// <summary>
/// A node in the graph. <see cref="Kind"/> is semantics, <see cref="Shape"/> and <see cref="Style"/> are presentation.
/// Shape names follow document.schema.json, including document, predefined-process, manual-input, off-page-connector
/// and the wider ISO 5807 set (delay, manual-operation, display, stored-data, multi-document, merge, extract,
/// on-page-connector, summing-junction, or, collate, sort, loop-limit, internal-storage, card).
/// Containers are nodes whose children point back via <see cref="Parent"/>. Geometry is always absolute when present;
/// it is null for a node that has arrived without a position (DSL or Mermaid import) and is waiting for the layout step.
/// </summary>
public sealed record Node(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Kind,
    [property: JsonPropertyOrder(2)] string Label,
    [property: JsonPropertyOrder(3)] string Shape)
{
    /// <summary>Absolute canvas position and size. Null until layout has placed the node.</summary>
    [JsonPropertyOrder(4)]
    public Geometry? Geometry { get; init; }

    [JsonPropertyOrder(5)]
    public NodeStyle? Style { get; init; }

    /// <summary>Free key/values preserved verbatim.</summary>
    [JsonPropertyOrder(6)]
    public IReadOnlyDictionary<string, JsonElement>? Props { get; init; }

    /// <summary>Id of the container node this node sits inside.</summary>
    [JsonPropertyOrder(7)]
    public string? Parent { get; init; }

    [JsonPropertyOrder(8)]
    public IReadOnlyList<Port>? Ports { get; init; }
}
