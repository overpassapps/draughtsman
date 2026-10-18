using System.Data;
using Draughtsman.Core.Auth;
using Draughtsman.Storage.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Draughtsman.Storage;

/// <summary>
/// <see cref="IUserStore"/> over EF Core, sharing <see cref="DraughtsmanDbContext"/> with <see cref="EfStore"/>. Email
/// is folded to lower case before every write and lookup (case is not significant to sign-in, but the two providers'
/// default collations disagree on that, so the fold is .NET's rather than the database's - the same reasoning as
/// <c>EfStore</c>'s <c>title_search</c>).
/// </summary>
public sealed class EfUserStore(DraughtsmanDbContext db) : IUserStore
{
    private readonly DraughtsmanDbContext _db = db;

    public Task<bool> HasAnyUsers(CancellationToken cancellationToken = default)
    {
        return _db.Users.AsNoTracking().AnyAsync(cancellationToken);
    }

    public async Task<User> CreateUser(string email, string displayName, UserRole role, string passwordHash, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var row = new UserRow
        {
            Id = Core.Storage.StoreIds.NewUserId(),
            Email = Fold(email),
            DisplayName = displayName,
            Role = role.ToString(),
            PasswordHash = passwordHash,
            CreatedAt = EfStore.ToUtc(createdAt),
            LastSignInAt = null,
            Active = true,
        };
        _db.Users.Add(row);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            _db.Entry(row).State = EntityState.Detached;
            throw new InvalidOperationException($"A user with email '{email}' already exists.");
        }

        return ToUser(row);
    }

    /// <summary>
    /// A serializable transaction around the check-then-insert closes the race a plain <see cref="HasAnyUsers"/> plus
    /// <see cref="CreateUser"/> leaves open: under Postgres's serializable snapshot isolation two concurrent callers
    /// racing this method can never both observe "no users" and both commit a row, and SQLite's single-writer lock
    /// gives the same guarantee for free. The loser sees a serialization or uniqueness failure, not an exception it
    /// has to know how to handle - it just gets null back, exactly as if it had checked first and lost.
    /// </summary>
    public async Task<User?> TryCreateFirstUser(string email, string displayName, UserRole role, string passwordHash, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        await using IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            if (await _db.Users.AsNoTracking().AnyAsync(cancellationToken))
            {
                return null;
            }

            var row = new UserRow
            {
                Id = Core.Storage.StoreIds.NewUserId(),
                Email = Fold(email),
                DisplayName = displayName,
                Role = role.ToString(),
                PasswordHash = passwordHash,
                CreatedAt = EfStore.ToUtc(createdAt),
                LastSignInAt = null,
                Active = true,
            };
            _db.Users.Add(row);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToUser(row);
        }
        catch (Exception error) when (IsRaceLoss(error))
        {
            _db.ChangeTracker.Clear();
            return null;
        }
    }

    public async Task<User?> GetUserByEmail(string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        string folded = Fold(email);
        UserRow? row = await _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Email == folded, cancellationToken);
        return row is null ? null : ToUser(row);
    }

    public async Task<User?> GetUserById(string id, CancellationToken cancellationToken = default)
    {
        UserRow? row = await _db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, cancellationToken);
        return row is null ? null : ToUser(row);
    }

    public async Task<IReadOnlyList<User>> ListUsers(CancellationToken cancellationToken = default)
    {
        List<UserRow> rows = await _db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(cancellationToken);
        return rows.Select(ToUser).ToList();
    }

    public async Task<bool> UpdateUserRole(string id, UserRole role, CancellationToken cancellationToken = default)
    {
        int updated = await _db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(row => row.SetProperty(u => u.Role, role.ToString()), cancellationToken);
        _db.ChangeTracker.Clear();
        return updated > 0;
    }

    /// <summary>
    /// A serializable transaction around "count other active admins, then write" so two concurrent callers each
    /// removing a different admin's status can never both succeed and leave none (security review F9), the same
    /// reasoning as <see cref="TryCreateFirstUser"/>.
    /// </summary>
    public async Task<bool> TryDemoteAdmin(string id, UserRole role, CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            int otherActiveAdmins = await _db.Users.CountAsync(u => u.Role == nameof(UserRole.Admin) && u.Active && u.Id != id, cancellationToken);
            if (otherActiveAdmins == 0)
            {
                return false;
            }

            int updated = await _db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(row => row.SetProperty(u => u.Role, role.ToString()), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return updated > 0;
        }
        catch (Exception error) when (IsRaceLoss(error))
        {
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>Same guarantee as <see cref="TryDemoteAdmin"/>, for deactivating an active admin.</summary>
    public async Task<bool> TryDeactivateAdmin(string id, CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            int otherActiveAdmins = await _db.Users.CountAsync(u => u.Role == nameof(UserRole.Admin) && u.Active && u.Id != id, cancellationToken);
            if (otherActiveAdmins == 0)
            {
                return false;
            }

            int updated = await _db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(row => row.SetProperty(u => u.Active, false), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _db.ChangeTracker.Clear();
            return updated > 0;
        }
        catch (Exception error) when (IsRaceLoss(error))
        {
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    public async Task<bool> UpdateUserPassword(string id, string passwordHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        int updated = await _db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(row => row.SetProperty(u => u.PasswordHash, passwordHash), cancellationToken);
        _db.ChangeTracker.Clear();
        return updated > 0;
    }

    public async Task<bool> SetUserActive(string id, bool active, CancellationToken cancellationToken = default)
    {
        int updated = await _db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(row => row.SetProperty(u => u.Active, active), cancellationToken);
        _db.ChangeTracker.Clear();
        return updated > 0;
    }

    public async Task<bool> RecordSignIn(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        DateTime atUtc = EfStore.ToUtc(at);
        int updated = await _db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(row => row.SetProperty(u => u.LastSignInAt, atUtc), cancellationToken);
        _db.ChangeTracker.Clear();
        return updated > 0;
    }

    public async Task<ApiToken> CreateApiToken(string userId, string name, string tokenHash, DateTimeOffset createdAt, DateTimeOffset? expiresAt = null, ApiTokenScope scope = ApiTokenScope.Full, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        var row = new ApiTokenRow
        {
            Id = Core.Storage.StoreIds.NewApiTokenId(),
            UserId = userId,
            Name = name,
            TokenHash = tokenHash,
            CreatedAt = EfStore.ToUtc(createdAt),
            LastUsedAt = null,
            Revoked = false,
            ExpiresAt = expiresAt is null ? null : EfStore.ToUtc(expiresAt.Value),
            Scope = ScopeToColumn(scope),
        };
        _db.ApiTokens.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return ToApiToken(row);
    }

    public async Task<IReadOnlyList<ApiToken>> ListApiTokens(string userId, CancellationToken cancellationToken = default)
    {
        List<ApiTokenRow> rows = await _db.ApiTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(ToApiToken).ToList();
    }

    public async Task<IReadOnlyList<ApiToken>> ListAllApiTokens(CancellationToken cancellationToken = default)
    {
        List<ApiTokenRow> rows = await _db.ApiTokens.AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(ToApiToken).ToList();
    }

    public async Task<ApiToken?> GetApiTokenById(string id, CancellationToken cancellationToken = default)
    {
        ApiTokenRow? row = await _db.ApiTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        return row is null ? null : ToApiToken(row);
    }

    public async Task<ApiToken?> GetApiTokenByHash(string tokenHash, CancellationToken cancellationToken = default)
    {
        ApiTokenRow? row = await _db.ApiTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
        return row is null ? null : ToApiToken(row);
    }

    public async Task<bool> RevokeApiToken(string id, CancellationToken cancellationToken = default)
    {
        int updated = await _db.ApiTokens.Where(t => t.Id == id).ExecuteUpdateAsync(row => row.SetProperty(t => t.Revoked, true), cancellationToken);
        _db.ChangeTracker.Clear();
        return updated > 0;
    }

    public async Task<int> RevokeApiTokensForUser(string userId, CancellationToken cancellationToken = default)
    {
        int updated = await _db.ApiTokens.Where(t => t.UserId == userId && !t.Revoked).ExecuteUpdateAsync(row => row.SetProperty(t => t.Revoked, true), cancellationToken);
        _db.ChangeTracker.Clear();
        return updated;
    }

    public async Task<bool> RecordApiTokenUse(string id, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        DateTime atUtc = EfStore.ToUtc(at);
        int updated = await _db.ApiTokens.Where(t => t.Id == id).ExecuteUpdateAsync(row => row.SetProperty(t => t.LastUsedAt, atUtc), cancellationToken);
        _db.ChangeTracker.Clear();
        return updated > 0;
    }

    public async Task<Session> CreateSession(string userId, string tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        var row = new SessionRow
        {
            Id = Core.Storage.StoreIds.NewSessionId(),
            UserId = userId,
            TokenHash = tokenHash,
            CreatedAt = EfStore.ToUtc(createdAt),
            ExpiresAt = EfStore.ToUtc(expiresAt),
            Revoked = false,
        };
        _db.Sessions.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return ToSession(row);
    }

    public async Task<Session?> GetSessionByHash(string tokenHash, CancellationToken cancellationToken = default)
    {
        SessionRow? row = await _db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);
        return row is null ? null : ToSession(row);
    }

    public async Task<bool> RevokeSession(string id, CancellationToken cancellationToken = default)
    {
        int updated = await _db.Sessions.Where(s => s.Id == id).ExecuteUpdateAsync(row => row.SetProperty(s => s.Revoked, true), cancellationToken);
        _db.ChangeTracker.Clear();
        return updated > 0;
    }

    public async Task<int> RevokeSessionsForUser(string userId, CancellationToken cancellationToken = default)
    {
        int updated = await _db.Sessions.Where(s => s.UserId == userId && !s.Revoked).ExecuteUpdateAsync(row => row.SetProperty(s => s.Revoked, true), cancellationToken);
        _db.ChangeTracker.Clear();
        return updated;
    }

    public async Task<int> RevokeOtherSessionsForUser(string userId, string keepSessionId, CancellationToken cancellationToken = default)
    {
        int updated = await _db.Sessions.Where(s => s.UserId == userId && s.Id != keepSessionId && !s.Revoked).ExecuteUpdateAsync(row => row.SetProperty(s => s.Revoked, true), cancellationToken);
        _db.ChangeTracker.Clear();
        return updated;
    }

    private static string Fold(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static User ToUser(UserRow row)
    {
        return new User(row.Id, row.Email, row.DisplayName, Enum.Parse<UserRole>(row.Role), row.PasswordHash, EfStore.FromUtc(row.CreatedAt), row.LastSignInAt is null ? null : EfStore.FromUtc(row.LastSignInAt.Value), row.Active);
    }

    private static ApiToken ToApiToken(ApiTokenRow row)
    {
        return new ApiToken(
            row.Id,
            row.UserId,
            row.Name,
            row.TokenHash,
            EfStore.FromUtc(row.CreatedAt),
            row.LastUsedAt is null ? null : EfStore.FromUtc(row.LastUsedAt.Value),
            row.Revoked,
            row.ExpiresAt is null ? null : EfStore.FromUtc(row.ExpiresAt.Value),
            ScopeFromColumn(row.Scope));
    }

    private static string ScopeToColumn(ApiTokenScope scope)
    {
        return scope == ApiTokenScope.Read ? "read" : "full";
    }

    /// <summary>Anything but an exact "read" reads as Full only because the column's own default is "full"; an unknown value is treated as the narrower Read, never a wider right.</summary>
    private static ApiTokenScope ScopeFromColumn(string value)
    {
        return value == "full" ? ApiTokenScope.Full : ApiTokenScope.Read;
    }

    private static Session ToSession(SessionRow row)
    {
        return new Session(row.Id, row.UserId, row.TokenHash, EfStore.FromUtc(row.CreatedAt), EfStore.FromUtc(row.ExpiresAt), row.Revoked);
    }

    /// <summary>Only a key clash means the email or hash is taken; every other save failure has to reach the caller as itself.</summary>
    private static bool IsUniqueViolation(DbUpdateException error)
    {
        const int SqliteConstraint = 19;
        return error.InnerException switch
        {
            SqliteException sqlite => sqlite.SqliteErrorCode == SqliteConstraint,
            PostgresException postgres => postgres.SqlState == PostgresErrorCodes.UniqueViolation,
            _ => false,
        };
    }

    /// <summary>
    /// The signal that a concurrent <see cref="TryCreateFirstUser"/> call lost the race, whether that surfaces as a
    /// unique-index violation (SQLite: the second writer blocks until the first commits, then fails the insert) or a
    /// serializable-transaction conflict (Postgres: <c>40001</c>, possibly not until <c>COMMIT</c>). EF Core's default
    /// execution strategy wraps a "transient-looking" provider exception in its own <see cref="InvalidOperationException"/>
    /// even with no retry configured, so this walks the whole inner-exception chain rather than assuming one depth.
    /// </summary>
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
