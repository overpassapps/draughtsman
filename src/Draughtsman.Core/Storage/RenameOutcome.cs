namespace Draughtsman.Core.Storage;

/// <summary>What a rename did. Like a replace, a rename is a guarded write: it names the base version it was read from.</summary>
public enum RenameOutcome
{
    /// <summary>The stored title is now the one passed in.</summary>
    Renamed,

    /// <summary>No document had that id.</summary>
    NotFound,

    /// <summary>The stored snapshot is no longer the base version the caller read from; nothing was written.</summary>
    VersionMismatch,
}
