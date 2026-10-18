namespace Draughtsman.Core.Storage;

/// <summary>
/// The answer to "can this database be written to right now?": a database can open and read perfectly well while every write fails (a file restored by
/// another account, a read-only mount, a Postgres replica). <see cref="Problem"/> is the provider's own words for why not, for the server log;
/// it is never shown to a caller who is not an administrator.
/// </summary>
public sealed record StoreWriteProbe(bool Writable, string? Problem = null);
