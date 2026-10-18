using System.Text.Json.Serialization;

namespace Draughtsman.Core.Models;

/// <summary>
/// A rectangle of the canvas in canvas units, absolute like <see cref="Geometry"/>: the region a keyframe's camera
/// frames. The camera fits the whole of it into whatever window plays the tour, so width and height are above zero.
/// </summary>
public sealed record ViewRect(
    [property: JsonPropertyOrder(0)] double X,
    [property: JsonPropertyOrder(1)] double Y,
    [property: JsonPropertyOrder(2)] double W,
    [property: JsonPropertyOrder(3)] double H);
