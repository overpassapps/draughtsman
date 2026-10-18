using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Sidecar;

namespace Draughtsman.Mcp.Rendering;

/// <summary>
/// Fetches the orthogonal connector paths <see cref="SvgRenderer"/> draws. The router is the editor's own, run by the
/// sidecar (<c>route.document</c>, web/src/canvas/routeDocument.ts), because a second implementation here would drift
/// from what the editor shows; the renderer itself only draws what it is handed.
/// </summary>
public static class ConnectorRouting
{
    /// <summary>
    /// Whether any connector needs a route: orthogonal and carrying no stored waypoints (a pinned connector draws its
    /// waypoints). A document without one never calls the sidecar.
    /// </summary>
    public static bool NeedsRoutes(Document document)
    {
        return document.Edges.Any(e => e.Routing is { Mode: RoutingMode.Orthogonal } routing && (routing.Waypoints is null || routing.Waypoints.Count == 0));
    }

    /// <summary>
    /// The routes to hand <see cref="SvgRenderer.Render"/>, empty when nothing needs routing. Throws
    /// <see cref="SidecarException"/> when the sidecar cannot route, which the caller decides how to report.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<Point>>> RouteAsync(ISidecarClient sidecar, Document document, CancellationToken cancellationToken)
    {
        if (!NeedsRoutes(document))
        {
            return new Dictionary<string, IReadOnlyList<Point>>();
        }

        IReadOnlyDictionary<string, EdgeRoute> routes = await sidecar.RouteAsync(document, cancellationToken).ConfigureAwait(false);
        return routes.ToDictionary(pair => pair.Key, pair => pair.Value.Points, StringComparer.Ordinal);
    }
}
