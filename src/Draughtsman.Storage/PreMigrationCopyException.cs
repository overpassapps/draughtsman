namespace Draughtsman.Storage;

/// <summary>
/// The copy a SQLite upgrade takes first could not be written (disk full, no permission) or did not read back whole, and
/// <c>storage.allowMigrationWithoutBackup</c> is not set, so the upgrade was refused before anything changed. Raised by
/// <see cref="StorageMigrator.Migrate"/>.
/// </summary>
public sealed class PreMigrationCopyException(string databasePath, Exception cause)
    : Exception(
        "The storage upgrade of " + databasePath + " was not started: the safety copy it takes first could not be made (" + cause.Message + "). "
        + "Nothing was changed and nothing was served. Free some disk space or fix the folder's permissions and start again. "
        + "To upgrade WITHOUT a safety copy (no automatic way back if the upgrade fails), take your own backup first, then set storage.allowMigrationWithoutBackup: true in draughtsman.yaml; see docs/upgrade-backup.md.",
        cause)
{
    public string DatabasePath { get; } = databasePath;
}
