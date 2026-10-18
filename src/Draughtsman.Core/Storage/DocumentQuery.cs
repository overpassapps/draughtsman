using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Storage;

/// <summary>
/// Paged, optionally filtered document listing. <paramref name="Query"/> matches the title case-insensitively as a
/// substring on every provider; a provider may add a faster native search later but must return the same rows.
/// A document in the bin (<see cref="IStore.SoftDeleteDocument"/>) is never listed here: the bin has its own listing.
/// </summary>
public sealed record DocumentQuery(string? Query = null, int Offset = 0, int Limit = DocumentQuery.DefaultLimit)
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    /// <summary>The default workspace until workspaces are real (spec section 7 lists them for a later task).</summary>
    public const string DefaultWorkspace = "default";

    public string Workspace { get; init; } = DefaultWorkspace;

    /// <summary>What the list is ordered by. Ties always break on id, so a page boundary never reshuffles rows.</summary>
    public DocumentSort Sort { get; init; } = DocumentSort.Modified;

    /// <summary>
    /// Flips the natural direction: <see cref="DocumentSort.Modified"/> and <see cref="DocumentSort.Created"/> are newest
    /// first by default, <see cref="DocumentSort.Title"/> and <see cref="DocumentSort.Type"/> A to Z. The flag means the
    /// same thing for every sort.
    /// </summary>
    public bool Reverse { get; init; }

    /// <summary>Only documents of this type, or every type when null.</summary>
    public DocumentType? Type { get; init; }
}

/// <summary>The columns the document list can be ordered by (PR-06).</summary>
public enum DocumentSort
{
    Modified,
    Created,
    Title,
    Type,
}
