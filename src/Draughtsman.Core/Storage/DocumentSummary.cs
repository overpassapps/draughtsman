using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Storage;

/// <summary>Listing row: what the document list and MCP list_documents show without loading the graph.</summary>
public sealed record DocumentSummary(
    string Id,
    string Title,
    DocumentType Type,
    string Workspace,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
