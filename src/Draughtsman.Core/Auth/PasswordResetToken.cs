namespace Draughtsman.Core.Auth;

/// <summary>Who started a reset link: the person themselves (it was emailed to them) or an administrator (who copies it to them).</summary>
public enum PasswordResetPurpose
{
    /// <summary>Self-service "Forgot password?": the link was emailed to the address on the account.</summary>
    Email,

    /// <summary>An administrator chose "Create reset link" and passes it on by a channel of their own.</summary>
    Admin,
}

/// <summary>
/// One password-reset link as stored. <see cref="TokenHash"/> is the SHA-256 of the link's secret; the secret itself is never
/// persisted, logged or recorded anywhere, so a copy of the database cannot be turned into a working link. A link is live
/// while it is unused, not invalidated and not past <see cref="ExpiresAt"/>; using it sets <see cref="UsedAt"/> (single use).
/// <see cref="InvalidatedAt"/> is set when something made it unusable without using it: a newer administrator link, a completed
/// reset, a password change, a deactivation.
/// </summary>
public sealed record PasswordResetToken(
    string Id,
    string UserId,
    string TokenHash,
    PasswordResetPurpose Purpose,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedAt,
    DateTimeOffset? InvalidatedAt,
    string? CreatedByUserId)
{
    public bool IsLive(DateTimeOffset now)
    {
        return UsedAt is null && InvalidatedAt is null && ExpiresAt > now;
    }
}

/// <summary>Why <see cref="IPasswordResetStore.Redeem"/> did or did not change a password. Everything but <see cref="Redeemed"/> is refused with one uniform message; the class is for the security log only.</summary>
public enum PasswordResetRedeemStatus
{
    Redeemed,

    /// <summary>No link has this hash (wrong, truncated or tampered with).</summary>
    NotFound,

    /// <summary>The link was already used, including by a concurrent request that won the race.</summary>
    Used,

    /// <summary>A newer link, a password change or a deactivation made it unusable.</summary>
    Invalidated,

    Expired,

    /// <summary>The account is deactivated (or gone). The link is left alone.</summary>
    AccountInactive,
}

/// <param name="Token">The link as stored.</param>
/// <param name="LinksInvalidated">Other live links of the account ended by this one (only when asked to).</param>
public sealed record PasswordResetCreation(PasswordResetToken Token, int LinksInvalidated);

/// <param name="User">The account the link belonged to, when it exists; null for <see cref="PasswordResetRedeemStatus.NotFound"/>.</param>
/// <param name="SessionsRevoked">Every session the account had, including any the caller held. Zero unless redeemed.</param>
/// <param name="LinksInvalidated">Other live links the account had, ended by this reset. Zero unless redeemed.</param>
public sealed record PasswordResetRedemption(PasswordResetRedeemStatus Status, PasswordResetToken? Token = null, User? User = null, int SessionsRevoked = 0, int LinksInvalidated = 0);

/// <summary>
/// Persistence for password-reset links (build-spec-v0.1.md section 7, docs/api.md "Password reset"), a sibling to
/// <see cref="IUserStore"/> exercised unchanged by the same provider contract tests. Composition (generating the secret,
/// choosing an expiry, sending the email, deciding who may ask) is the server's; this is persistence plus the one operation
/// that has to be atomic, <see cref="Redeem"/>.
/// </summary>
public interface IPasswordResetStore
{
    /// <summary>
    /// Stores a link. With <paramref name="invalidateOthers"/> every other live link of the account (either purpose) is ended in
    /// the same transaction, so there is never a moment with two administrator links live.
    /// </summary>
    Task<PasswordResetCreation> Create(string userId, PasswordResetPurpose purpose, string tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt, string? createdByUserId, bool invalidateOthers, CancellationToken cancellationToken = default);

    Task<PasswordResetToken?> GetByHash(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>How many links of this purpose the account was given at or after <paramref name="since"/>, used or not: the budget a stranger repeating a request cannot exceed.</summary>
    Task<int> CountCreatedSince(string userId, PasswordResetPurpose purpose, DateTimeOffset since, CancellationToken cancellationToken = default);

    /// <summary>Ends every live link of the account (either purpose). Returns how many.</summary>
    Task<int> InvalidateLive(string userId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Ends one link that never reached anyone (the email failed to send).</summary>
    Task<bool> Invalidate(string id, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends the link and sets the password, in one serializable transaction: claims the link only if it is still live (so of
    /// two concurrent requests exactly one wins), then writes <paramref name="newPasswordHash"/>, revokes every session of the
    /// account and ends its other live links. The account's API tokens are left alone. Nothing changes unless the status is
    /// <see cref="PasswordResetRedeemStatus.Redeemed"/>.
    /// </summary>
    Task<PasswordResetRedemption> Redeem(string tokenHash, string newPasswordHash, DateTimeOffset now, CancellationToken cancellationToken = default);
}
