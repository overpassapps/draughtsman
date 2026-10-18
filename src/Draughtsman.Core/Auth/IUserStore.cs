namespace Draughtsman.Core.Auth;

/// <summary>
/// Local accounts, sessions and API tokens (build-spec-v0.1.md section 7), kept separate from <see cref="Draughtsman.Core.Storage.IStore"/>
/// since it is a different concern with a different lifecycle. Composition (hashing a password, generating a token,
/// deciding who may call what) lives in the server, not here; this is persistence only, exercised unchanged by
/// <c>UserStoreContractTests</c> the way <c>IStore</c> is exercised by <c>StoreContractTests</c> (ADR 0003).
/// </summary>
public interface IUserStore
{
    /// <summary>True once any user exists. First-run is only ever open while this is false.</summary>
    Task<bool> HasAnyUsers(CancellationToken cancellationToken = default);

    Task<User> CreateUser(string email, string displayName, UserRole role, string passwordHash, DateTimeOffset createdAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically checks <see cref="HasAnyUsers"/> and creates the user in one step, so two concurrent first-run
    /// requests cannot both observe an empty table and both create an admin (security review F1.1). Returns null,
    /// having created nothing, if any user already existed the instant this ran - the caller reports that as first
    /// run already being complete, exactly as a plain <see cref="HasAnyUsers"/> check followed by <see cref="CreateUser"/>
    /// would have, but without the race between the two calls.
    /// </summary>
    Task<User?> TryCreateFirstUser(string email, string displayName, UserRole role, string passwordHash, DateTimeOffset createdAt, CancellationToken cancellationToken = default);

    /// <summary>Case-insensitive: email is the sign-in identifier and must not collide on case.</summary>
    Task<User?> GetUserByEmail(string email, CancellationToken cancellationToken = default);

    Task<User?> GetUserById(string id, CancellationToken cancellationToken = default);

    /// <summary>Ordered by email for a stable admin listing.</summary>
    Task<IReadOnlyList<User>> ListUsers(CancellationToken cancellationToken = default);

    Task<bool> UpdateUserRole(string id, UserRole role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically changes an active admin's role to <paramref name="role"/> (never <see cref="UserRole.Admin"/>
    /// itself - there is nothing to guard there) only if at least one other active admin would remain afterwards;
    /// otherwise changes nothing and returns false (security review F9). Serializable, the same shape as
    /// <see cref="TryCreateFirstUser"/>, so two callers concurrently demoting two different admins can never both
    /// succeed and leave none - a plain check-then-write would leave exactly that race open.
    /// </summary>
    Task<bool> TryDemoteAdmin(string id, UserRole role, CancellationToken cancellationToken = default);

    /// <summary>Same guarantee as <see cref="TryDemoteAdmin"/>, for deactivating an active admin.</summary>
    Task<bool> TryDeactivateAdmin(string id, CancellationToken cancellationToken = default);

    Task<bool> UpdateUserPassword(string id, string passwordHash, CancellationToken cancellationToken = default);

    Task<bool> SetUserActive(string id, bool active, CancellationToken cancellationToken = default);

    Task<bool> RecordSignIn(string id, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Creates a token. <paramref name="expiresAt"/> null stores a token that never expires (what the grandfathering migration left); the server always passes one for a new token.</summary>
    Task<ApiToken> CreateApiToken(string userId, string name, string tokenHash, DateTimeOffset createdAt, DateTimeOffset? expiresAt = null, ApiTokenScope scope = ApiTokenScope.Full, CancellationToken cancellationToken = default);

    /// <summary>A user's own tokens, ordered by creation, newest first.</summary>
    Task<IReadOnlyList<ApiToken>> ListApiTokens(string userId, CancellationToken cancellationToken = default);

    /// <summary>Every token of every user, newest first, for the admin credential list. Never carries a secret (only the hash, which no response exposes).</summary>
    Task<IReadOnlyList<ApiToken>> ListAllApiTokens(CancellationToken cancellationToken = default);

    Task<ApiToken?> GetApiTokenById(string id, CancellationToken cancellationToken = default);

    /// <summary>Looked up by hash on every bearer-authenticated request; the raw token is never persisted.</summary>
    Task<ApiToken?> GetApiTokenByHash(string tokenHash, CancellationToken cancellationToken = default);

    Task<bool> RevokeApiToken(string id, CancellationToken cancellationToken = default);

    /// <summary>Every token for a user, e.g. an admin resetting their password (security review F8). Returns how many were revoked.</summary>
    Task<int> RevokeApiTokensForUser(string userId, CancellationToken cancellationToken = default);

    Task<bool> RecordApiTokenUse(string id, DateTimeOffset at, CancellationToken cancellationToken = default);

    Task<Session> CreateSession(string userId, string tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>Looked up by hash on every cookie-authenticated request; the raw cookie value is never persisted.</summary>
    Task<Session?> GetSessionByHash(string tokenHash, CancellationToken cancellationToken = default);

    Task<bool> RevokeSession(string id, CancellationToken cancellationToken = default);

    /// <summary>Every session for a user, e.g. when an admin deactivates them. Returns how many were revoked.</summary>
    Task<int> RevokeSessionsForUser(string userId, CancellationToken cancellationToken = default);

    /// <summary>Every session for a user except <paramref name="keepSessionId"/>: a user changing their own password keeps the one they are using. Returns how many were revoked.</summary>
    Task<int> RevokeOtherSessionsForUser(string userId, string keepSessionId, CancellationToken cancellationToken = default);
}
