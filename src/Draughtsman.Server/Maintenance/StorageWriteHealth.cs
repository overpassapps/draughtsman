using Draughtsman.Core.Storage;
using Draughtsman.Server.Hardening;

namespace Draughtsman.Server.Maintenance;

/// <summary>
/// Whether the database can be written to, asked of the store at most every <see cref="Lifetime"/> and remembered between asks, so a monitor polling
/// <c>/api/health</c> (or anyone who can reach it: the route is anonymous) never turns into a stream of write transactions. A change is logged once, when it
/// happens: the first time it cannot be written, with what to check, and again when it can be.
/// </summary>
public sealed class StorageWriteHealth(IServiceScopeFactory scopes, TimeProvider clock, DataFolder dataFolder, ILogger<StorageWriteHealth> logger)
{
    /// <summary>How long an answer is reused.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private StoreWriteProbe? _last;
    private DateTimeOffset _asked;
    private bool? _wasWritable;

    public async Task<StoreWriteProbe> CurrentAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_last is not null && now - _asked < Lifetime)
            {
                return _last;
            }
        }

        StoreWriteProbe probe;
        using (IServiceScope scope = scopes.CreateScope())
        {
            probe = await scope.ServiceProvider.GetRequiredService<IStoreMaintenance>().ProbeWrite(cancellationToken);
        }

        bool? before;
        lock (_gate)
        {
            _last = probe;
            _asked = now;
            before = _wasWritable;
            _wasWritable = probe.Writable;
        }

        if (!probe.Writable && before != false)
        {
            logger.LogError(
                "The database cannot be written to ({Problem}), so this instance cannot save anything and /api/health reports degraded. Check that the account running the server ({Account}) owns the data folder {Folder} and everything in it "
                + "(for example chown -R <uid> <folder>), that the disk is neither full nor read-only, and that a restored data.db was not left owned by another account; see docs/troubleshooting.md.",
                probe.Problem,
                DataFolderWriteProbe.CurrentAccount(),
                dataFolder.Path);
        }
        else if (probe.Writable && before == false)
        {
            logger.LogInformation("The database can be written to again.");
        }

        return probe;
    }
}
