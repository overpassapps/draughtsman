namespace Draughtsman.Storage.Entities;

/// <summary>A per-user API token for MCP and scripting. Revocation is a flag, not a delete, so "last used" history survives it.</summary>
public sealed class ApiTokenRow
{
    public required string Id { get; set; }

    public required string UserId { get; set; }

    public required string Name { get; set; }

    public required string TokenHash { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public bool Revoked { get; set; }

    /// <summary>Null for a token that pre-dates expiry: the migration grandfathered it, so it never expires until revoked.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary><c>full</c> or <c>read</c> (ApiTokenScope, stored lower case).</summary>
    public string Scope { get; set; } = "full";

    public UserRow? User { get; set; }
}
