namespace Draughtsman.Core.Storage;

/// <summary>Thrown by store operations that need an existing document (append update, create version) when there is none.</summary>
public sealed class DocumentNotFoundException(string documentId)
    : Exception($"Document '{documentId}' does not exist.")
{
    public string DocumentId { get; } = documentId;
}
