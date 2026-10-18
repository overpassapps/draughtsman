namespace Draughtsman.Core.Models;

/// <summary>Document header timestamps, author and AI audit trail.</summary>
public sealed record Meta(DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string CreatedBy)
{
    public IReadOnlyList<AiProvenance>? AiProvenance { get; init; }
}
