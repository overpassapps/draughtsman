using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Storage;

/// <summary>
/// A named snapshot: a manual save point or the checkpoint taken before an agent (or anyone) replaces a document.
/// Semantic diff runs between two of these.
/// </summary>
public sealed record DocumentVersion(string Id, string DocumentId, string Name, DateTimeOffset CreatedAt, Actor Actor, string? Reason, Document Document)
{
    public VersionSummary ToSummary()
    {
        // The size a listing reports is the length of the stored snapshot text, which is the model's own serialisation.
        return new VersionSummary(Id, DocumentId, Name, CreatedAt, Actor, Reason, DocumentJson.Serialize(Document).Length);
    }
}
