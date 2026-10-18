namespace Draughtsman.Storage.Entities;

/// <summary>
/// A password-reset link, stored as the SHA-256 of its secret (never the secret). Kept after use or expiry until the daily
/// maintenance pass removes it, so the security log's references to it still mean something for a while.
/// </summary>
public sealed class PasswordResetTokenRow
{
    public required string Id { get; set; }

    public required string UserId { get; set; }

    public required string TokenHash { get; set; }

    /// <summary><c>email</c> or <c>admin</c> (PasswordResetPurpose, lower case).</summary>
    public required string Purpose { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? UsedAt { get; set; }

    public DateTime? InvalidatedAt { get; set; }

    /// <summary>The administrator who created an admin link; null for an emailed one.</summary>
    public string? CreatedByUserId { get; set; }

    public UserRow? User { get; set; }
}
