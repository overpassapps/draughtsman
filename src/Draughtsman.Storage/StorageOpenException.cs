namespace Draughtsman.Storage;

/// <summary>
/// The database could not be opened to find out what upgrade it needs: a SQLite file in a folder the account cannot write (SQLite needs to create its
/// write-ahead-log files beside it), on a read-only or full disk, or already damaged; or a Postgres server that cannot be reached. Nothing was changed
/// and the server refuses to start with a message that says what to check, instead of an unhandled exception. Raised by <see cref="StorageMigrator.Migrate"/>.
/// </summary>
public sealed class StorageOpenException(StorageOptions options, Exception cause)
    : Exception(BuildMessage(options, cause), cause)
{
    private static string BuildMessage(StorageOptions options, Exception cause)
    {
        string what = options.Provider == StorageProvider.Sqlite
            ? "The SQLite database could not be opened for upgrade (" + cause.Message + "). Nothing was changed and nothing was served. Check that the account running the server may read and write the whole data folder (SQLite creates its -wal and -shm files beside data.db), that the disk is not full or read-only, and that the file is not damaged (if it is, restore a backup: docs/upgrade-backup.md)."
            : "The Postgres database could not be reached (" + cause.GetType().Name + "). Nothing was changed and nothing was served. Check storage.connectionString, that the database is running, and that the role may connect.";
        return what;
    }
}
