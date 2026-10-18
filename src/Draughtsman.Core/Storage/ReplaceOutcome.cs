namespace Draughtsman.Core.Storage;

/// <summary>What a replace did. A guarded replace that finds the stored version moved on writes nothing.</summary>
public enum ReplaceOutcome
{
    /// <summary>The stored snapshot is now the one passed in.</summary>
    Replaced,

    /// <summary>No document had that id.</summary>
    NotFound,

    /// <summary>The stored snapshot is no longer the base version the caller wrote from; nothing was written.</summary>
    VersionMismatch,
}
