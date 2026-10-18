using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Storage;

/// <summary>
/// A document in the bin (OP-02): still stored whole, with its versions, but hidden from every read and listing until it
/// is restored or purged. <paramref name="DeletedByUserId"/> is the authenticated user row that deleted it (null only for a
/// delete with no authenticated identity, the stdio host), <paramref name="DeletedByLabel"/> their display name then.
/// <paramref name="CreatedByUserId"/> is the user row that created it, read from the audit trail (null when the creation
/// predates the audit's user id or had no authenticated identity): together the two decide whose bin entry it is.
/// </summary>
public sealed record BinnedDocument(
    string Id,
    string Title,
    DocumentType Type,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset DeletedAt,
    string? DeletedByUserId,
    string? DeletedByLabel,
    string? CreatedByUserId);

/// <summary>
/// Paged bin listing, newest deletion first. <paramref name="InvolvingUserId"/> narrows it to the entries a given user is
/// party to (they created it or they deleted it); null lists the whole bin.
/// </summary>
public sealed record BinQuery(string? InvolvingUserId = null, int Offset = 0, int Limit = DocumentQuery.DefaultLimit);

public sealed record BinPage(IReadOnlyList<BinnedDocument> Items, int Total, int Offset, int Limit);

/// <summary>What a restore from the bin did.</summary>
public enum RestoreOutcome
{
    /// <summary>The document is live again, exactly as it was when deleted.</summary>
    Restored,

    /// <summary>No document with that id is in the bin (it never existed, was purged, or is not deleted).</summary>
    NotInBin,
}
