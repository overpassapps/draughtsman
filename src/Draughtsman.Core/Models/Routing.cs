using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Models;

public sealed record Routing(RoutingMode Mode)
{
    public IReadOnlyList<Point>? Waypoints { get; init; }
}
