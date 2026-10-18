using System.Text.Json;
using System.Text.Json.Serialization;

namespace Draughtsman.Core.Models;

/// <summary>A directed edge between two nodes, optionally via named ports.</summary>
public sealed record Edge(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] string Kind,
    [property: JsonPropertyOrder(3)] Endpoint From,
    [property: JsonPropertyOrder(4)] Endpoint To)
{
    [JsonPropertyOrder(2)]
    public string? Label { get; init; }

    /// <summary>Null means the theme's default routing (orthogonal unless the theme says otherwise).</summary>
    [JsonPropertyOrder(5)]
    public Routing? Routing { get; init; }

    [JsonPropertyOrder(6)]
    public EdgeStyle? Style { get; init; }

    [JsonPropertyOrder(7)]
    public IReadOnlyDictionary<string, JsonElement>? Props { get; init; }
}
