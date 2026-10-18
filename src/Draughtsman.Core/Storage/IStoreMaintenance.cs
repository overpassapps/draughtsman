namespace Draughtsman.Core.Storage;

/// <summary>
/// The housekeeping a provider does for the daily maintenance pass and the asset quota (AU-15), plus the one whole-instance
/// read the export needs: the operations that keep automatically-made rows from growing without bound. Separate from <see cref="IStore"/> because no request path needs it
/// and a fake store has no reason to model it.
/// </summary>
public interface IStoreMaintenance
{
    /// <summary>
    /// Deletes all but the newest <paramref name="keepPerDocument"/> automatic checkpoints of every document (versions named
    /// <c>checkpoint</c>; a version a person named is never deleted). Returns how many were deleted. Zero or less deletes nothing.
    /// </summary>
    Task<int> PruneCheckpoints(int keepPerDocument, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes sessions that expired before <paramref name="expiredBefore"/>, and revoked sessions created before it (a
    /// session records no revocation time). Returns how many were deleted.
    /// </summary>
    Task<int> PurgeSessions(DateTimeOffset expiredBefore, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes password-reset links (<see cref="Draughtsman.Core.Auth.IPasswordResetStore"/>) that ended before <paramref name="endedBefore"/>:
    /// used, invalidated or past their expiry. A link still live is never deleted, whatever its age. Returns how many were deleted.
    /// </summary>
    Task<int> PurgePasswordResetLinks(DateTimeOffset endedBefore, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes every document that has been in the bin since before <paramref name="deletedBefore"/> (its update
    /// log and versions with it) and returns what it removed, so the caller can record each one. Live documents are never
    /// touched, whatever their age.
    /// </summary>
    Task<IReadOnlyList<BinnedDocument>> PurgeBin(DateTimeOffset deletedBefore, CancellationToken cancellationToken = default);

    /// <summary>A page of asset rows in creation order, for the whole-instance export. Empty past the end.</summary>
    Task<IReadOnlyList<AssetInfo>> ListAssets(int offset, int limit, CancellationToken cancellationToken = default);

    /// <summary>The sum of the sizes of every stored asset, for the instance quota.</summary>
    Task<long> TotalAssetBytes(CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the database to start a write that changes nothing, which a read-only file or a read-only Postgres session refuses even though
    /// reads work (a full disk is not seen: a write that matches no row needs no space), so health and the sign-in error can say "this instance cannot save" instead of being unable to tell. Never throws for a database that
    /// is merely unwritable or unreachable (that is the answer); a cancelled token still throws.
    /// </summary>
    Task<StoreWriteProbe> ProbeWrite(CancellationToken cancellationToken = default);
}
