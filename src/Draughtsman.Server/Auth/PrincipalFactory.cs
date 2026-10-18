using System.Security.Claims;
using Draughtsman.Core.Auth;

namespace Draughtsman.Server.Auth;

/// <summary>Builds the <see cref="ClaimsPrincipal"/> both authentication handlers produce from a <see cref="User"/> row.</summary>
public static class PrincipalFactory
{
    /// <summary>The cookie session's own id, so sign-out can revoke it without re-hashing the cookie.</summary>
    public const string SessionIdClaim = "ds:session_id";

    /// <summary>The API token's own id, carried for completeness; authorization never keys on it directly.</summary>
    public const string TokenIdClaim = "ds:token_id";

    /// <summary>"read" on a read-scoped API token's principal; absent for a session and for a full-scope token. TokenScopeMiddleware and HttpActorContext key on it.</summary>
    public const string TokenScopeClaim = "ds:token_scope";

    public const string ReadScope = "read";

    public static ClaimsPrincipal ForUser(User user, string authenticationType, string? sessionId = null, string? tokenId = null, ApiTokenScope tokenScope = ApiTokenScope.Full)
    {
        ArgumentNullException.ThrowIfNull(user);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
        };
        if (sessionId is not null)
        {
            claims.Add(new Claim(SessionIdClaim, sessionId));
        }

        if (tokenId is not null)
        {
            claims.Add(new Claim(TokenIdClaim, tokenId));
        }

        if (tokenScope == ApiTokenScope.Read)
        {
            claims.Add(new Claim(TokenScopeClaim, ReadScope));
        }

        var identity = new ClaimsIdentity(claims, authenticationType, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }
}
