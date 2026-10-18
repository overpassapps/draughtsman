using System.Data;
using Draughtsman.Core.Auth;
using Draughtsman.Storage.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Draughtsman.Storage;

/// <summary>
/// <see cref="IPasswordResetStore"/> over EF Core, sharing <see cref="DraughtsmanDbContext"/> with the other stores. The
/// secret behind a link is never seen here, only its hash. Every write is a conditional <c>UPDATE</c> (a link is claimed with
/// <c>WHERE used_at IS NULL AND invalidated_at IS NULL AND expires_at &gt; now</c>), so single use holds under concurrency
/// without relying on a read followed by a write.
/// </summary>
public sealed class EfPasswordResetStore(DraughtsmanDbContext db) : IPasswordResetStore
{
    private const string EmailPurpose = "email";
    private const string AdminPurpose = "admin";

    public async Task<PasswordResetCreation> Create(string userId, PasswordResetPurpose purpose, string tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt, string? createdByUserId, bool invalidateOthers, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        var row = new PasswordResetTokenRow
        {
            Id = Core.Storage.StoreIds.NewPasswordResetId(),
            UserId = userId,
            TokenHash = tokenHash,
            Purpose = PurposeToColumn(purpose),
            CreatedAt = EfStore.ToUtc(createdAt),
            ExpiresAt = EfStore.ToUtc(expiresAt),
            UsedAt = null,
            InvalidatedAt = null,
            CreatedByUserId = createdByUserId,
        };

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        int ended = 0;
        if (invalidateOthers)
        {
            ended = await EndLiveLinks(userId, EfStore.ToUtc(createdAt), exceptId: null, cancellationToken);
        }

        db.PasswordResetTokens.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.Entry(row).State = EntityState.Detached;
        return new PasswordResetCreation(ToToken(row), ended);
    }

    public async Task<PasswordResetToken?> GetByHash(string tokenHash, CancellationToken cancellationToken = default)
    {
        PasswordResetTokenRow? row = await db.PasswordResetTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        return row is null ? null : ToToken(row);
    }

    public async Task<int> CountCreatedSince(string userId, PasswordResetPurpose purpose, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        DateTime cutoff = EfStore.ToUtc(since);
        string column = PurposeToColumn(purpose);
        return await db.PasswordResetTokens.AsNoTracking().CountAsync(t => t.UserId == userId && t.Purpose == column && t.CreatedAt >= cutoff, cancellationToken);
    }

    public async Task<int> InvalidateLive(string userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        int ended = await EndLiveLinks(userId, EfStore.ToUtc(now), exceptId: null, cancellationToken);
        db.ChangeTracker.Clear();
        return ended;
    }

    public async Task<bool> Invalidate(string id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        DateTime at = EfStore.ToUtc(now);
        int updated = await db.PasswordResetTokens
            .Where(t => t.Id == id && t.UsedAt == null && t.InvalidatedAt == null)
            .ExecuteUpdateAsync(row => row.SetProperty(t => t.InvalidatedAt, at), cancellationToken);
        db.ChangeTracker.Clear();
        return updated > 0;
    }

    /// <summary>
    /// Serializable, the same shape as <c>EfUserStore.TryCreateFirstUser</c>: the claim, the password write, the session
    /// revocation and the ending of the other links commit together or not at all. A concurrent caller that loses the race
    /// (a unique or serialization failure on Postgres, a busy database on SQLite) is told the link is used, which is
    /// what it is by the time it could look again.
    /// </summary>
    public async Task<PasswordResetRedemption> Redeem(string tokenHash, string newPasswordHash, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPasswordHash);
        DateTime at = EfStore.ToUtc(now);

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            PasswordResetTokenRow? row = await db.PasswordResetTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
            if (row is null)
            {
                return new PasswordResetRedemption(PasswordResetRedeemStatus.NotFound);
            }

            PasswordResetToken token = ToToken(row);
            UserRow? user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == row.UserId, cancellationToken);
            if (row.UsedAt is not null)
            {
                return new PasswordResetRedemption(PasswordResetRedeemStatus.Used, token, user is null ? null : ToUser(user));
            }

            if (row.InvalidatedAt is not null)
            {
                return new PasswordResetRedemption(PasswordResetRedeemStatus.Invalidated, token, user is null ? null : ToUser(user));
            }

            if (row.ExpiresAt <= at)
            {
                return new PasswordResetRedemption(PasswordResetRedeemStatus.Expired, token, user is null ? null : ToUser(user));
            }

            if (user is null || !user.Active)
            {
                return new PasswordResetRedemption(PasswordResetRedeemStatus.AccountInactive, token, user is null ? null : ToUser(user));
            }

            // The claim itself: exactly one of any number of concurrent callers sees a row updated.
            int claimed = await db.PasswordResetTokens
                .Where(t => t.Id == row.Id && t.UsedAt == null && t.InvalidatedAt == null && t.ExpiresAt > at)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.UsedAt, at), cancellationToken);
            if (claimed == 0)
            {
                return new PasswordResetRedemption(PasswordResetRedeemStatus.Used, token, ToUser(user));
            }

            await db.Users.Where(u => u.Id == row.UserId).ExecuteUpdateAsync(set => set.SetProperty(u => u.PasswordHash, newPasswordHash), cancellationToken);
            int sessions = await db.Sessions.Where(s => s.UserId == row.UserId && !s.Revoked).ExecuteUpdateAsync(set => set.SetProperty(s => s.Revoked, true), cancellationToken);
            int others = await EndLiveLinks(row.UserId, at, exceptId: row.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PasswordResetRedemption(PasswordResetRedeemStatus.Redeemed, token with { UsedAt = now }, ToUser(user) with { PasswordHash = newPasswordHash }, sessions, others);
        }
        catch (Exception error) when (IsRaceLoss(error))
        {
            return new PasswordResetRedemption(PasswordResetRedeemStatus.Used);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private async Task<int> EndLiveLinks(string userId, DateTime at, string? exceptId, CancellationToken cancellationToken)
    {
        return await db.PasswordResetTokens
            .Where(t => t.UserId == userId && t.UsedAt == null && t.InvalidatedAt == null && t.ExpiresAt > at && t.Id != exceptId)
            .ExecuteUpdateAsync(row => row.SetProperty(t => t.InvalidatedAt, at), cancellationToken);
    }

    private static string PurposeToColumn(PasswordResetPurpose purpose)
    {
        return purpose == PasswordResetPurpose.Admin ? AdminPurpose : EmailPurpose;
    }

    /// <summary>An unknown stored purpose reads as the narrower, emailed one, never as an administrator's.</summary>
    private static PasswordResetPurpose PurposeFromColumn(string value)
    {
        return value == AdminPurpose ? PasswordResetPurpose.Admin : PasswordResetPurpose.Email;
    }

    private static PasswordResetToken ToToken(PasswordResetTokenRow row)
    {
        return new PasswordResetToken(
            row.Id,
            row.UserId,
            row.TokenHash,
            PurposeFromColumn(row.Purpose),
            EfStore.FromUtc(row.CreatedAt),
            EfStore.FromUtc(row.ExpiresAt),
            row.UsedAt is null ? null : EfStore.FromUtc(row.UsedAt.Value),
            row.InvalidatedAt is null ? null : EfStore.FromUtc(row.InvalidatedAt.Value),
            row.CreatedByUserId);
    }

    private static User ToUser(UserRow row)
    {
        return new User(row.Id, row.Email, row.DisplayName, Enum.Parse<UserRole>(row.Role), row.PasswordHash, EfStore.FromUtc(row.CreatedAt), row.LastSignInAt is null ? null : EfStore.FromUtc(row.LastSignInAt.Value), row.Active);
    }

    /// <summary>The same classification as <c>EfUserStore.IsRaceLoss</c>: a unique-index clash, a busy or locked SQLite file, a Postgres serialization failure, anywhere in the inner-exception chain.</summary>
    private static bool IsRaceLoss(Exception error)
    {
        const int SqliteConstraint = 19;
        const int SqliteBusy = 5;
        const int SqliteLocked = 6;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SqliteException sqlite when sqlite.SqliteErrorCode is SqliteConstraint or SqliteBusy or SqliteLocked:
                    return true;
                case PostgresException postgres when postgres.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure:
                    return true;
            }
        }

        return false;
    }
}
