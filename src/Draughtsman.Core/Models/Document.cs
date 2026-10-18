using System.Text.Json.Serialization;
using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Models;

/// <summary>
/// A diagram: a typed graph of nodes and edges plus a small header.
/// The JSON shape is fixed by docs/schema/document.schema.json and mirrored by web/src/model/types.ts;
/// change all three together.
/// </summary>
public sealed record Document(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] int SchemaVersion,
    [property: JsonPropertyOrder(2)] string Title,
    [property: JsonPropertyOrder(3)] DocumentType Type,
    [property: JsonPropertyOrder(5)] IReadOnlyList<Node> Nodes,
    [property: JsonPropertyOrder(6)] IReadOnlyList<Edge> Edges,
    [property: JsonPropertyOrder(7)] Meta Meta)
{
    /// <summary>The schema version this build reads and writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Document-level diagram-theme token overrides, token name to value. Null means none.</summary>
    [JsonPropertyOrder(4)]
    public IReadOnlyDictionary<string, string>? Theme { get; init; }

    /// <summary>The camera path a presentation flies along, in tour order. Null means none; the JSON key is omitted.</summary>
    [JsonPropertyOrder(8)]
    public Flythrough? Flythrough { get; init; }
}
