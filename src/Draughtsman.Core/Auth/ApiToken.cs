namespace Draughtsman.Core.Auth;

/// <summary>
/// A per-user API token for MCP and scripting (build-spec-v0.1.md section 7). <see cref="TokenHash"/> is the token's
/// SHA-256 hash at rest; the raw secret is returned to the caller once, at creation, and never stored or logged. A
/// caller may annotate which agent is acting through the token per request via <c>X-Draughtsman-Actor</c>, but that
/// header never changes which token or user this row identifies (it is not stored here).
/// <see cref="ExpiresAt"/> null means the token pre-dates expiry and was grandfathered by the migration that added it
/// (it never expires until revoked); every token created since has one.
/// </summary>
public sealed record ApiToken(
    string Id,
    string UserId,
    string Name,
    string TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    bool Revoked,
    DateTimeOffset? ExpiresAt = null,
    ApiTokenScope Scope = ApiTokenScope.Full);
