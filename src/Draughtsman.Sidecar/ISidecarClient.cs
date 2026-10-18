using Draughtsman.Core.Models;

namespace Draughtsman.Sidecar;

/// <summary>
/// The server's view of the Node sidecar (ADR 0002): server-side ELK layout and Yjs materialisation. One
/// implementation per server process; requests are multiplexed over a single child process and may complete in
/// any order. Every method throws <see cref="SidecarUnavailableException"/> when the process cannot be started
/// (retryable), <see cref="SidecarTimeoutException"/> after the configured timeout, and
/// <see cref="SidecarRpcException"/> when the sidecar rejects the request.
/// </summary>
public interface ISidecarClient
{
    SidecarStatus Status { get; }

    /// <summary>Starts the process if needed and pings it. The hosted service calls this at startup.</summary>
    Task<SidecarHealth> PingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lays out a document: every position is recomputed, sizes are kept, zero sizes get a default. Nodes may
    /// arrive with no geometry (<see cref="Node.Geometry"/> is null until layout places them), which is how DSL,
    /// Mermaid and MCP create_document input comes in; every node comes back with one.
    /// </summary>
    Task<LayoutResult> LayoutAsync(Document document, LayoutOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Routes every connector the editor would route itself: <c>routing.mode</c> orthogonal with no stored waypoints,
    /// between two placed nodes. It runs the web canvas's own router (<c>web/src/canvas/routeDocument.ts</c>), so a
    /// server render draws the paths the editor draws. Returns the routes by edge id (edges it does not route are
    /// absent); the document must already have geometry, so lay it out first.
    /// </summary>
    Task<IReadOnlyDictionary<string, EdgeRoute>> RouteAsync(Document document, CancellationToken cancellationToken = default);

    /// <summary>
    /// The derived geometry of a <c>sequence</c> document (<c>sequence.scene</c>): headers, lifelines, message polylines,
    /// activation bars and bounds, from the editor's own <c>sequenceScene</c> function. A sequence document stores no
    /// positions, so a server render asks for this instead of laying the document out. Throws
    /// <see cref="SidecarRpcException"/> for a document of another type.
    /// </summary>
    Task<SequenceScene> SequenceSceneAsync(Document document, CancellationToken cancellationToken = default);

    /// <summary>Materialises a Yjs state, with optional further updates applied in order, as the JSON document.</summary>
    Task<Document> YjsToJsonAsync(byte[]? state, IReadOnlyList<byte[]>? updates = null, CancellationToken cancellationToken = default);

    /// <summary>Encodes a JSON document as a fresh Yjs state (a full update).</summary>
    Task<byte[]> YjsFromJsonAsync(Document document, CancellationToken cancellationToken = default);

    /// <summary>Applies updates in order to an optional starting state and returns the resulting full state.</summary>
    Task<YjsState> ApplyUpdatesAsync(byte[]? state, IReadOnlyList<byte[]> updates, CancellationToken cancellationToken = default);
}
