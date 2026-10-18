namespace Draughtsman.Storage;

/// <summary>Which provider to use and how to reach it. Built by the server from the storage section of draughtsman.yaml.</summary>
public sealed record StorageOptions(StorageProvider Provider, string ConnectionString)
{
    public const string SqliteFileName = "data.db";

    /// <summary>
    /// Resolves the config keys: an empty connection string means <c>data.db</c> in the data folder for SQLite and is an
    /// error for Postgres. Unknown provider names are an error rather than a silent fallback.
    /// </summary>
    public static StorageOptions FromConfig(string provider, string connectionString, string dataFolder)
    {
        ArgumentNullException.ThrowIfNull(dataFolder);
        string name = (provider ?? string.Empty).Trim().ToLowerInvariant();
        string connection = (connectionString ?? string.Empty).Trim();
        switch (name)
        {
            case "" or "sqlite":
                return new StorageOptions(StorageProvider.Sqlite, connection.Length > 0 ? connection : DefaultSqliteConnectionString(dataFolder));
            case "postgres":
                if (connection.Length == 0)
                {
                    throw new InvalidOperationException("storage.provider is postgres but storage.connectionString is empty.");
                }

                return new StorageOptions(StorageProvider.Postgres, connection);
            default:
                throw new InvalidOperationException($"Unknown storage.provider '{provider}'. Expected sqlite or postgres.");
        }
    }

    public static string DefaultSqliteConnectionString(string dataFolder)
    {
        return $"Data Source={Path.Combine(dataFolder, SqliteFileName)}";
    }

    /// <summary>
    /// The SQLite database file this points at, or null for Postgres and for a connection string that names no file (in-memory, or a URI). The
    /// server probes it, and the files SQLite keeps beside it, for write access before it starts serving.
    /// </summary>
    public string? SqliteDatabasePath
    {
        get
        {
            if (Provider != StorageProvider.Sqlite)
            {
                return null;
            }

            string source = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(ConnectionString).DataSource;
            return source.Length == 0 || source.StartsWith(':') || source.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? null : Path.GetFullPath(source);
        }
    }

    /// <summary>The name /api/health reports.</summary>
    public string ProviderName => Provider switch
    {
        StorageProvider.Sqlite => "sqlite",
        StorageProvider.Postgres => "postgres",
        _ => Provider.ToString().ToLowerInvariant(),
    };
}

public enum StorageProvider
{
    Sqlite,
    Postgres,
}
