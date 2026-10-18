using System.Security.Claims;
using Draughtsman.Core.Auth;

namespace Draughtsman.Server.Auth;

public static class ClaimsPrincipalExtensions
{
    public static string? UserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    public static bool IsAdmin(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.IsInRole(nameof(UserRole.Admin));
    }

    /// <summary>True for a request authenticated by a read-scoped API token (never a session, never a full-scope token).</summary>
    public static bool IsReadOnlyToken(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.HasClaim(PrincipalFactory.TokenScopeClaim, PrincipalFactory.ReadScope);
    }
}
