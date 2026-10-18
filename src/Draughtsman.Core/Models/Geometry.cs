using System.Text.Json.Serialization;

namespace Draughtsman.Core.Models;

/// <summary>
/// Absolute canvas position and size in canvas units (CSS pixels at zoom 1).
/// Rotation is degrees clockwise about the centre; null means 0 and is omitted from JSON, matching the schema and the TypeScript type.
/// </summary>
public sealed record Geometry(
    [property: JsonPropertyOrder(0)] double X,
    [property: JsonPropertyOrder(1)] double Y,
    [property: JsonPropertyOrder(2)] double W,
    [property: JsonPropertyOrder(3)] double H)
{
    [JsonPropertyOrder(4)]
    public double? Rotation { get; init; }
}
