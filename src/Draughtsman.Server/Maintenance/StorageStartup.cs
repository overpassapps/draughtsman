using Draughtsman.Server.Configuration;
using Draughtsman.Storage;

namespace Draughtsman.Server.Maintenance;

/// <summary>
/// Startup storage work shared by the web host and <c>mcp --stdio</c>: applies the migrations with the retention
/// settings, and turns "this database is newer than this build" (OP-03), "the upgrade failed its checks and was undone" and
/// "the safety copy could not be made" into a clear message and a non-zero exit code rather than an unhandled exception and
/// a crash loop under a service manager.
/// </summary>
public static class StorageStartup
{
    /// <summary>The process exit code for a database a newer build has migrated.</summary>
    public const int SchemaNewerExitCode = 3;

    /// <summary>The exit code for an upgrade that failed its post-migration checks (or a migration that threw) and was undone from the pre-migration copy.</summary>
    public const int MigrationRolledBackExitCode = 4;

    /// <summary>The exit code for an upgrade refused before it began because its safety copy could not be made, or the database could not be opened for it; nothing was changed.</summary>
    public const int PreMigrationCopyFailedExitCode = 5;

    /// <summary>True when the storage is ready; false after logging why not and setting the process exit code, in which case the caller must not start serving.</summary>
    public static async Task<bool> MigrateOrRefuseAsync(DraughtsmanDbContext db, StorageOptions storage, DraughtsmanConfig config, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        try
        {
            await StorageMigrator.Migrate(db, storage, logger, cancellationToken, config.Retention.PreMigrationCopies, config.Storage.AllowMigrationWithoutBackup);
            return true;
        }
        catch (SchemaNewerThanBuildException refusal)
        {
            logger.LogCritical("{Refusal}", refusal.Message);
            Environment.ExitCode = SchemaNewerExitCode;
            return false;
        }
        catch (MigrationRolledBackException undone)
        {
            logger.LogCritical("{Refusal}", undone.Message);
            Environment.ExitCode = MigrationRolledBackExitCode;
            return false;
        }
        catch (PreMigrationCopyException refused)
        {
            logger.LogCritical("{Refusal}", refused.Message);
            Environment.ExitCode = PreMigrationCopyFailedExitCode;
            return false;
        }
        catch (StorageOpenException refused)
        {
            logger.LogCritical("{Refusal}", refused.Message);
            Environment.ExitCode = PreMigrationCopyFailedExitCode;
            return false;
        }
    }
}
