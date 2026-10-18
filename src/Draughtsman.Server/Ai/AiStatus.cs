namespace Draughtsman.Server.Ai;

/// <summary>
/// Whether AI features are on for this instance, computed once at startup from <c>ai.provider</c> (build-spec-v0.1.md
/// section 7: no provider by default, so an unset or unrecognised provider means AI features are hidden, not broken).
/// A singleton, so <c>GET /api/health</c> and the generate-from-text endpoint see the same answer.
/// </summary>
public sealed record AiStatus(bool Enabled, string Provider, string Model)
{
    public static readonly AiStatus Off = new(false, string.Empty, string.Empty);
}
