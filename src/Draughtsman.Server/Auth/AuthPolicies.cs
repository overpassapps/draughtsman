using System.Security.Claims;

namespace Draughtsman.Server.Auth;

/// <summary>
/// Authorization policy names. <see cref="CookieOnly"/> and <see cref="CookieAdminOnly"/> exist for security review
/// F3: an API token carries its owner's role (<see cref="PrincipalFactory.ForUser"/>), so <see cref="AdminOnly"/>
/// alone would let a token minted by an admin manage accounts or mint further tokens through it. Account and token
/// management are cookie-session-only regardless of the caller's role.
/// </summary>
public static class AuthPolicies
{
    /// <summary>Requires the Admin role, on top of the default fallback policy's RequireAuthenticatedUser.</summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>Requires a cookie-authenticated session; an API token, however privileged its owner, is refused.</summary>
    public const string CookieOnly = "CookieOnly";

    /// <summary>Both: a cookie-authenticated session whose user is an Admin.</summary>
    public const string CookieAdminOnly = "CookieAdminOnly";

    /// <summary>True only for a request authenticated by the cookie session scheme, never a bearer token.</summary>
    public static bool IsCookieAuthenticated(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return string.Equals(user.Identity?.AuthenticationType, AuthSchemes.Cookie, StringComparison.Ordinal);
    }
}
