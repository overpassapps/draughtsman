using Draughtsman.Core.Models;

namespace Draughtsman.Sidecar;

/// <summary>
/// A laid-out document plus what the sidecar could not do: edges to missing nodes, self-loops and container-to-child
/// edges are left unrouted and named here rather than failing the whole layout.
/// </summary>
public sealed record LayoutResult(Document Document, IReadOnlyList<string> Warnings);
