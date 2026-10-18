namespace Draughtsman.Storage.Entities;

/// <summary>A signed-in browser session. Revocation is a flag so sign-out and admin revoke are the same write.</summary>
public sealed class SessionRow
{
    public required string Id { get; set; }

    public required string UserId { get; set; }

    public required string TokenHash { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public bool Revoked { get; set; }

    public UserRow? User { get; set; }
}
