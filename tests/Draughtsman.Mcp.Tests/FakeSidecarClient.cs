using Draughtsman.Core.Models;
using Draughtsman.Sidecar;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// A fake <see cref="ISidecarClient"/>: either lays a document out with a trivial deterministic grid (every node
/// gets a position and a default size where it had none) or, when <see cref="Failure"/> is set, throws it the way
/// the real client does - exactly the shape the sidecar-failure tests need without spawning anything. Taking the
/// exception itself rather than a flag lets a test pick which of the three failure modes it is modelling.
/// </summary>
public sealed class FakeSidecarClient : ISidecarClient
{
    public SidecarException? Failure { get; set; }

    public SidecarStatus Status => new(Failure is SidecarUnavailableException ? SidecarState.Unavailable : SidecarState.Running);

    public Task<SidecarHealth> PingAsync(CancellationToken cancellationToken = default)
    {
        return Failure is not null
            ? throw Failure
            : Task.FromResult(new SidecarHealth(true, "0.0.0-fake", "v0-fake", 0));
    }

    public Task<LayoutResult> LayoutAsync(Document document, LayoutOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        double x = 0;
        List<Node> laidOut = document.Nodes.Select(n =>
        {
            Geometry geometry = n.Geometry ?? new Geometry(x, 0, 120, 60);
            x += 160;
            return n with { Geometry = geometry };
        }).ToList();

        return Task.FromResult(new LayoutResult(document with { Nodes = laidOut }, []));
    }

    /// <summary>What <see cref="RouteAsync"/> answers, by edge id; empty by default, as for a document with no orthogonal edges.</summary>
    public Dictionary<string, EdgeRoute> Routes { get; } = [];

    /// <summary>How many times routing was asked for, so a test can pin that a document with nothing to route never asks.</summary>
    public int RouteCalls { get; private set; }

    public Task<IReadOnlyDictionary<string, EdgeRoute>> RouteAsync(Document document, CancellationToken cancellationToken = default)
    {
        RouteCalls++;
        return Failure is not null
            ? throw Failure
            : Task.FromResult<IReadOnlyDictionary<string, EdgeRoute>>(Routes);
    }

    /// <summary>What <see cref="SequenceSceneAsync"/> answers; a test sets it to the scene the renderer should draw.</summary>
    public SequenceScene? Scene { get; set; }

    /// <summary>How many times a scene was asked for.</summary>
    public int SceneCalls { get; private set; }

    public Task<SequenceScene> SequenceSceneAsync(Document document, CancellationToken cancellationToken = default)
    {
        SceneCalls++;
        return Failure is not null
            ? throw Failure
            : Task.FromResult(Scene ?? throw new NotSupportedException("FakeSidecarClient has no Scene set."));
    }

    public Task<Document> YjsToJsonAsync(byte[]? state, IReadOnlyList<byte[]>? updates = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("FakeSidecarClient does not model Yjs; the MCP tools never call this.");
    }

    public Task<byte[]> YjsFromJsonAsync(Document document, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("FakeSidecarClient does not model Yjs; the MCP tools never call this.");
    }

    public Task<YjsState> ApplyUpdatesAsync(byte[]? state, IReadOnlyList<byte[]> updates, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("FakeSidecarClient does not model Yjs; the MCP tools never call this.");
    }
}
