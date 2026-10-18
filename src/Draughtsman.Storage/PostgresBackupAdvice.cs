using System.Globalization;
using Npgsql;

namespace Draughtsman.Storage;

/// <summary>
/// What to tell a Postgres operator about backing up. Draughtsman cannot take a consistent Postgres backup itself (it would
/// need pg_dump on this host and the operator's credentials), and a verb that pretended to would be worse than none, so
/// the advice is an exact command built from the configured connection string. The password is never printed: the
/// command reads it from <c>PGPASSWORD</c> or the operator's <c>.pgpass</c>.
/// </summary>
public static class PostgresBackupAdvice
{
    public static string DumpCommand(string connectionString, string? outputFile = null)
    {
        NpgsqlConnectionStringBuilder connection = Parse(connectionString);
        string file = outputFile ?? $"draughtsman-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}.dump";
        return $"pg_dump --format=custom --no-owner {Connection(connection)} --file {Quote(file)}";
    }

    /// <summary>
    /// Restores a dump into an EMPTY schema. Not <c>--clean --if-exists</c>: that drops only what the dump holds, so a database a newer
    /// migration has added a table to (one with a foreign key to a table in the dump) cannot be cleaned and the restore is left half done
    /// (found by running it: "cannot drop table users because other objects depend on it"). See <see cref="EmptySchemaCommand"/>.
    /// </summary>
    public static string RestoreCommand(string connectionString, string dumpFile)
    {
        NpgsqlConnectionStringBuilder connection = Parse(connectionString);
        return $"pg_restore --no-owner {Connection(connection)} {Quote(dumpFile)}";
    }

    /// <summary>Empties the <c>public</c> schema (everything Draughtsman created is in it), ready for <see cref="RestoreCommand"/>. Run it only on the database you mean to roll back.</summary>
    public static string EmptySchemaCommand(string connectionString)
    {
        NpgsqlConnectionStringBuilder connection = Parse(connectionString);
        return $"psql {Connection(connection)} --command 'DROP SCHEMA public CASCADE; CREATE SCHEMA public;'";
    }

    private static NpgsqlConnectionStringBuilder Parse(string connectionString)
    {
        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            // An unparsable string is reported where it is used; the advice just falls back to placeholders.
            return new NpgsqlConnectionStringBuilder();
        }
    }

    private static string Connection(NpgsqlConnectionStringBuilder connection)
    {
        string host = string.IsNullOrEmpty(connection.Host) ? "<host>" : connection.Host;
        string database = string.IsNullOrEmpty(connection.Database) ? "<database>" : connection.Database;
        string user = string.IsNullOrEmpty(connection.Username) ? "<user>" : connection.Username;
        return $"--host {Quote(host)} --port {connection.Port.ToString(CultureInfo.InvariantCulture)} --username {Quote(user)} --dbname {Quote(database)}";
    }

    private static string Quote(string value)
    {
        return value.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '$' or '\\' or '<' or '>')
            ? "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'"
            : value;
    }
}
