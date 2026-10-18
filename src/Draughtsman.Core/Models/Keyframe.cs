using System.Text.Json.Serialization;
using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Models;

/// <summary>
/// One stop of a <see cref="Flythrough"/>: a camera framing <see cref="View"/>, an optional caption shown while it is
/// there, and how the camera gets in and how long it stays. Timings and easing are sparse: absent means the player's
/// default (web/src/model/flythrough.ts, mirrored by the standalone viewer).
/// </summary>
public sealed record Keyframe(
    [property: JsonPropertyOrder(0)] string Id,
    [property: JsonPropertyOrder(1)] ViewRect View)
{
    /// <summary>Text shown over the diagram while the camera is here.</summary>
    [JsonPropertyOrder(2)]
    public string? Caption { get; init; }

    /// <summary>
    /// Milliseconds the camera takes to travel here from the previous keyframe; on the first keyframe, from the last
    /// one when the tour loops. Ignored by <see cref="KeyframeEasing.Cut"/>.
    /// </summary>
    [JsonPropertyOrder(3)]
    public double? DurationMs { get; init; }

    /// <summary>Milliseconds an autoplaying tour stays here before it travels on.</summary>
    [JsonPropertyOrder(4)]
    public double? HoldMs { get; init; }

    [JsonPropertyOrder(5)]
    public KeyframeEasing? Easing { get; init; }
}
