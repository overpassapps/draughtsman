namespace Draughtsman.Core.Auth;

/// <summary>
/// A signed-in browser session (build-spec-v0.1.md section 7). <see cref="TokenHash"/> is the session cookie value's
/// SHA-256 hash at rest, so the store never sees the raw cookie. Server-side and revocable: sign-out and an admin
/// revoke both just set <see cref="Revoked"/>, which every request re-checks before trusting the cookie.
/// </summary>
public sealed record Session(
    string Id,
    string UserId,
    string TokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool Revoked);
