namespace Draughtsman.Storage;

/// <summary>
/// The database has migrations applied that this build does not contain: a newer Draughtsman has already been run on it
/// (OP-03). Opening it with this older build would fail at runtime or write rows the newer build misreads, so the
/// server refuses to start, and the message says what to do instead. Raised by <see cref="StorageMigrator.Migrate"/>
/// before it changes anything.
/// </summary>
public sealed class SchemaNewerThanBuildException(IReadOnlyList<string> unknownMigrations, StorageOptions options, string recovery)
    : Exception(BuildMessage(unknownMigrations, options, recovery))
{
    public IReadOnlyList<string> UnknownMigrations { get; } = unknownMigrations;

    private static string BuildMessage(IReadOnlyList<string> unknownMigrations, StorageOptions options, string recovery)
    {
        return $"This {options.ProviderName} database was migrated by a newer version of Draughtsman than this one "
            + $"(it has {unknownMigrations.Count} migration(s) this build does not know: {string.Join(", ", unknownMigrations)}). "
            + "Running an older version against a newer database can fail at runtime or corrupt data, so it was not opened and nothing was changed. "
            + recovery;
    }
}
