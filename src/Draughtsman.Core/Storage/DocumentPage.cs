namespace Draughtsman.Core.Storage;

/// <summary>One page of a listing plus the total so a client can render paging controls.</summary>
public sealed record DocumentPage(IReadOnlyList<DocumentSummary> Items, int Total, int Offset, int Limit);
