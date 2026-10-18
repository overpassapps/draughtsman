namespace Draughtsman.Core.Storage;

/// <summary>One page of a version listing plus the total so a client can render paging controls.</summary>
public sealed record VersionPage(IReadOnlyList<VersionSummary> Items, int Total, int Offset, int Limit);
