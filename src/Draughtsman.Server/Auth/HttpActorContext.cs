using System.Security.Claims;
using Draughtsman.Core.Auth;

namespace Draughtsman.Server.Auth;

/// <summary>
/// <see cref="IActorContext"/> for the web host: the authenticated principal <c>/mcp</c> over HTTP runs behind, the
/// same fallback authorization policy protects every other route with. By the time a tool call reaches
/// <c>DraughtsmanTools</c>, the request has already been authenticated (or refused with 401 long before this class
/// runs), so <see cref="Current"/> is null only defensively, never in the ordinary path.
/// </summary>
public sealed class HttpActorContext(IHttpContextAccessor accessor) : IActorContext
{
    public AuthenticatedIdentity? Current
    {
        get
        {
            ClaimsPrincipal? user = accessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                return null;
            }

            string? userId = user.UserId();
            if (userId is null)
            {
                return null;
            }

            string displayName = user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue(ClaimTypes.Email) ?? "user";
            return new AuthenticatedIdentity(userId, displayName, user.IsReadOnlyToken());
        }
    }
}
