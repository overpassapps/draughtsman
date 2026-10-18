using Microsoft.Data.Sqlite;
using Npgsql;

namespace Draughtsman.Storage;

/// <summary>
/// Which exceptions mean "the database cannot save" as opposed to "this write was wrong" or "someone else holds the lock". The server answers the first kind
/// with a sentence an administrator can act on (the instance cannot save; check the data folder) and a 503 rather than a bare 500, and leaves every other
/// exception to be what it is.
/// </summary>
public static class StorageFaults
{
    private const int SqliteIoError = 10;
    private const int SqliteReadOnly = 8;
    private const int SqliteFull = 13;

    /// <summary>Insufficient privilege, a read-only transaction (a replica, or <c>default_transaction_read_only</c>) and a full disk.</summary>
    private static readonly string[] s_postgresStates = ["42501", "25006", "53100"];

    /// <summary>True when <paramref name="error"/> or anything it wraps is the database refusing writes: read-only, out of space or an I/O failure under it.</summary>
    public static bool IsCannotWrite(Exception? error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SqliteException sqlite when sqlite.SqliteErrorCode is SqliteReadOnly or SqliteFull or SqliteIoError:
                case PostgresException postgres when s_postgresStates.Contains(postgres.SqlState, StringComparer.Ordinal):
                    return true;
            }
        }

        return false;
    }
}
