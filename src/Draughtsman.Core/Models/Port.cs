namespace Draughtsman.Core.Models;

/// <summary>A named attachment point. X and Y are fractions of the node's width and height, 0 to 1.</summary>
public sealed record Port(string Id, double X, double Y);
