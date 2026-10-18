using Draughtsman.Core.Enums;

namespace Draughtsman.Core.Models;

/// <summary>Which agent and model created or last changed which elements. Kept for audit in regulated environments.</summary>
public sealed record AiProvenance(string Agent, DateTimeOffset At, AiAction Action, IReadOnlyList<string> ElementIds)
{
    public string? Model { get; init; }
}
