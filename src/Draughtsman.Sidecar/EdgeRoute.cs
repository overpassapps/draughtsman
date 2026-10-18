using Draughtsman.Core.Models;

namespace Draughtsman.Sidecar;

/// <summary>
/// One connector routed by the editor's own router (sidecar method <c>route.document</c>): the polyline from the
/// source port to the target port, in document coordinates. <see cref="Status"/> is <c>routed</c> or <c>fallback</c>
/// (no obstacle-free path existed or the search budget ran out, so it is the router's marked elbow, which is what the
/// editor draws too); <see cref="Reason"/> says why on a fallback.
/// </summary>
public sealed record EdgeRoute(string Status, IReadOnlyList<Point> Points, string? Reason = null);
