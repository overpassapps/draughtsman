namespace Draughtsman.Storage;

/// <summary>
/// A SQLite upgrade failed (a migration threw, SQLite's integrity check complained, or a table lost rows) and the
/// pre-migration copy was put back over the database. The server refuses to start; the data file is exactly what it was before
/// this start, and the copy is kept beside it. Raised by <see cref="StorageMigrator.Migrate"/>.
/// </summary>
public sealed class MigrationRolledBackException(string databasePath, string copyPath, IReadOnlyList<string> reasons, string? restoreFailure, Exception? cause)
    : Exception(BuildMessage(databasePath, copyPath, reasons, restoreFailure), cause)
{
    public string DatabasePath { get; } = databasePath;

    public string CopyPath { get; } = copyPath;

    public IReadOnlyList<string> Reasons { get; } = reasons;

    /// <summary>Null when the copy was restored and checked; otherwise why it could not be, in which case the copy must be restored by hand.</summary>
    public string? RestoreFailure { get; } = restoreFailure;

    public bool Restored => RestoreFailure is null;

    private static string BuildMessage(string databasePath, string copyPath, IReadOnlyList<string> reasons, string? restoreFailure)
    {
        string what = "The storage upgrade of " + databasePath + " did not pass its checks: " + string.Join("; ", reasons) + ". ";
        string state = restoreFailure is null
            ? "The pre-upgrade copy " + copyPath + " was put back automatically, so " + Path.GetFileName(databasePath) + " is exactly as it was before this start (the copy is kept beside it) and nothing was served. "
                + "Start the previous version again, and report this failure with the log."
            : "THE COPY COULD NOT BE PUT BACK (" + restoreFailure + "). Stop the server, delete " + Path.GetFileName(databasePath) + "-wal and " + Path.GetFileName(databasePath) + "-shm if they exist, "
                + "and copy " + copyPath + " over " + Path.GetFileName(databasePath) + " by hand before starting any version.";
        return what + state;
    }
}
