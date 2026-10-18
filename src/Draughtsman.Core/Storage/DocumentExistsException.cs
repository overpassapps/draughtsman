namespace Draughtsman.Core.Storage;

/// <summary>Thrown by <see cref="IStore.CreateDocument"/> when the id is already taken.</summary>
public sealed class DocumentExistsException(string documentId)
    : Exception($"Document '{documentId}' already exists.")
{
    public string DocumentId { get; } = documentId;
}
