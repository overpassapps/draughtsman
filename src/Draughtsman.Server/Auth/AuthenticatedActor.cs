using System.Security.Claims;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Documents;

namespace Draughtsman.Server.Auth;

/// <summary>
/// The actor recorded in audit and aiProvenance comes from the authenticated identity, never from
/// <see cref="ActorHeader.Name"/> - a standing rule from the 2026-09-21 MCP spec amendment. A signed-in user's
/// display name is always the actor, and <paramref name="actorUserId"/> (out param of <see cref="TryResolve"/>) is
/// always that user's own row id - a display name is neither unique nor immutable (security review F9), so the id
/// is what a caller should key on. A caller authenticated with an API token may additionally set the header to
/// annotate which agent is acting through that token (<c>agent:&lt;name&gt;/&lt;model&gt;</c> only); it is honoured as
/// that annotation on top of the owner and nothing more (security review F2) - it can never make the request act as
/// a different user, <paramref name="actorUserId"/> stays the token's real owner either way, and a cookie-authenticated
/// human's header is ignored outright rather than rejected, since it was never meant for them.
/// </summary>
public static class AuthenticatedActor
{
    private const int MaxHeaderLength = 512;
    private const int MaxNameLength = 256;
    private const int MaxModelLength = 256;

    public static bool TryResolve(HttpRequest request, out Actor actor, out string? actorUserId, out IResult? problem)
    {
        ArgumentNullException.ThrowIfNull(request);
        problem = null;
        ClaimsPrincipal user = request.HttpContext.User;
        string name = user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue(ClaimTypes.Email) ?? "user";
        actor = new Actor(ActorKind.User, name);
        actorUserId = user.UserId();

        string? header = request.Headers[ActorHeader.Name].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header))
        {
            return true;
        }

        bool isToken = string.Equals(user.Identity?.AuthenticationType, AuthSchemes.Token, StringComparison.Ordinal);
        if (!isToken)
        {
            // Defined only as a token annotation (build-spec-v0.1.md section 7); a session's stray header is inert.
            return true;
        }

        string wire = header.Trim();
        if (wire.Length > MaxHeaderLength || !Actor.TryParse(wire, out Actor? parsed) || parsed.Kind != ActorKind.Agent)
        {
            problem = Malformed($"{ActorHeader.Name} must be agent:<name>/<model> of at most {MaxHeaderLength} characters when set on a token-authenticated request.");
            return false;
        }

        if (parsed.Name.Length > MaxNameLength || parsed.Model?.Length > MaxModelLength)
        {
            problem = Malformed($"{ActorHeader.Name} name and model must each be at most {MaxNameLength} characters.");
            return false;
        }

        // The annotation replaces the *display* actor for provenance/audit readability, but never the owner's id.
        actor = parsed;
        return true;
    }

    private static IResult Malformed(string detail)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid actor header", detail: detail);
    }
}
