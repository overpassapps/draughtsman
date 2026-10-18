namespace Draughtsman.Storage.Entities;

/// <summary>A local account. Never deleted by this task's scope; deactivation is the only removal path (Active = false).</summary>
public sealed class UserRow
{
    public required string Id { get; set; }

    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    public required string Role { get; set; }

    public required string PasswordHash { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? LastSignInAt { get; set; }

    public bool Active { get; set; }

    public List<ApiTokenRow> ApiTokens { get; } = [];

    public List<SessionRow> Sessions { get; } = [];

    public List<PasswordResetTokenRow> PasswordResetTokens { get; } = [];
}
