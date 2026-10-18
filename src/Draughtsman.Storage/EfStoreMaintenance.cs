using Draughtsman.Core.Storage;
using Draughtsman.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace Draughtsman.Storage;

/// <summary><see cref="IStoreMaintenance"/> over EF Core; the same lowest-common-denominator SQL as <see cref="EfStore"/>.</summary>
public sealed class EfStoreMaintenance(DraughtsmanDbContext db) : IStoreMaintenance
{
    /// <summary>The name an agent edit gives the snapshot it takes first (DocumentEndpoints.CheckpointVersionName, the MCP patch tool).</summary>
    public const string CheckpointName = "checkpoint";

    private const int DeleteBatch = 500;

    public async Task<int> PruneCheckpoints(int keepPerDocument, CancellationToken cancellationToken = default)
    {
        if (keepPerDocument <= 0)
        {
            return 0;
        }

        // Documents that have more checkpoints than they may keep; a document within its allowance costs nothing further.
        List<string> over = await db.DocumentVersions.AsNoTracking()
            .Where(v => v.Name == CheckpointName)
            .GroupBy(v => v.DocumentId)
            .Where(g => g.Count() > keepPerDocument)
            .Select(g => g.Key)
            .ToListAsync(cancellationToken);

        int deleted = 0;
        foreach (string documentId in over)
        {
            // Newest first, the order ListVersions uses (creation time, then the time-ordered id); everything past the allowance goes.
            List<string> doomed = await db.DocumentVersions.AsNoTracking()
                .Where(v => v.DocumentId == documentId && v.Name == CheckpointName)
                .OrderByDescending(v => v.CreatedAt)
                .ThenByDescending(v => v.Id)
                .Skip(keepPerDocument)
                .Select(v => v.Id)
                .ToListAsync(cancellationToken);
            foreach (string[] batch in doomed.Chunk(DeleteBatch))
            {
                deleted += await db.DocumentVersions
                    .Where(v => v.DocumentId == documentId && batch.Contains(v.Id))
                    .ExecuteDeleteAsync(cancellationToken);
            }
        }

        db.ChangeTracker.Clear();
        return deleted;
    }

    public async Task<IReadOnlyList<BinnedDocument>> PurgeBin(DateTimeOffset deletedBefore, CancellationToken cancellationToken = default)
    {
        DateTime cutoff = EfStore.ToUtc(deletedBefore);
        List<EfStore.BinRow> due = await EfStore.BinRows(db.Documents.AsNoTracking().Where(d => d.DeletedAt != null && d.DeletedAt < cutoff), db)
            .ToListAsync(cancellationToken);
        var purged = new List<BinnedDocument>(due.Count);
        foreach (EfStore.BinRow row in due)
        {
            // A restore that lands between the listing and this delete wins: the delete re-checks the bin under a row lock.
            if (await EfStore.DeleteDocumentRows(db, row.Id, binnedOnly: true, cancellationToken))
            {
                purged.Add(EfStore.ToBinned(row));
            }
        }

        return purged;
    }

    public async Task<int> PurgeSessions(DateTimeOffset expiredBefore, CancellationToken cancellationToken = default)
    {
        DateTime cutoff = EfStore.ToUtc(expiredBefore);
        int deleted = await db.Sessions
            .Where(s => s.ExpiresAt < cutoff || (s.Revoked && s.CreatedAt < cutoff))
            .ExecuteDeleteAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return deleted;
    }

    public async Task<int> PurgePasswordResetLinks(DateTimeOffset endedBefore, CancellationToken cancellationToken = default)
    {
        DateTime cutoff = EfStore.ToUtc(endedBefore);
        // Ended means used, invalidated or past its expiry; a link that is still live is never deleted, whatever its age.
        int deleted = await db.PasswordResetTokens
            .Where(t => t.ExpiresAt < cutoff || (t.UsedAt != null && t.UsedAt < cutoff) || (t.InvalidatedAt != null && t.InvalidatedAt < cutoff))
            .ExecuteDeleteAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return deleted;
    }

    public async Task<IReadOnlyList<AssetInfo>> ListAssets(int offset, int limit, CancellationToken cancellationToken = default)
    {
        List<AssetRow> rows = await db.Assets.AsNoTracking()
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return rows.Select(a => new AssetInfo(a.Id, a.Hash, a.ContentType, a.Size, a.Path, EfStore.FromUtc(a.CreatedAt))).ToList();
    }

    public async Task<long> TotalAssetBytes(CancellationToken cancellationToken = default)
    {
        return await db.Assets.AsNoTracking().SumAsync(a => (long?)a.Size, cancellationToken) ?? 0;
    }

    public async Task<StoreWriteProbe> ProbeWrite(CancellationToken cancellationToken = default)
    {
        try
        {
            // An UPDATE that matches no row still asks for the write lock before it looks, so a read-only database refuses it, and nothing changes.
            await db.Sessions.Where(s => s.Id == string.Empty).ExecuteUpdateAsync(s => s.SetProperty(x => x.Revoked, x => x.Revoked), cancellationToken);
            return new StoreWriteProbe(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new StoreWriteProbe(false, error.GetBaseException().Message);
        }
    }
}
