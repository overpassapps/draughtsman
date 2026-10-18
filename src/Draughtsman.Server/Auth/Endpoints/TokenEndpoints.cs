using Draughtsman.Core.Auth;

namespace Draughtsman.Server.Auth.Endpoints;

/// <summary>
/// A user's own API tokens for MCP and scripting (build-spec-v0.1.md section 7). Every signed-in user manages their
/// own; an admin may additionally list (<c>GET /api/admin/tokens</c>) and revoke anyone's. The raw secret is returned only from <see cref="CreateToken"/>,
/// once, and is never retrievable again. Minting is cookie-session-only (security review F3): a token can never
/// mint another token through itself, regardless of its owner's role, and a token is only ever minted for the
/// signed-in caller themselves - there is no way to create one for someone else. Every new token expires
/// (<see cref="TokenPolicy"/>) and may be read-only (AU-05); creation and revocation are security events (AU-04).
/// </summary>
public static class TokenEndpoints
{
    private const int MaxNameLength = 256; // the api_tokens.name column


    public static IEndpointRouteBuilder MapTokenEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/tokens");
        group.MapGet("", ListTokens).WithName("ListApiTokens");
        group.MapPost("", CreateToken).WithName("CreateApiToken").RequireAuthorization(AuthPolicies.CookieOnly);
        group.MapDelete("/{id}", RevokeToken).WithName("RevokeApiToken");
        // Every person's tokens, for the admin credential list (QA S6). Cookie-admin only, like /api/users: a leaked
        // admin token must not be able to enumerate the other credentials. Revoking goes through DELETE /api/tokens/{id}.
        app.MapGet("/api/admin/tokens", ListAllTokens).WithName("ListAllApiTokens").RequireAuthorization(AuthPolicies.CookieAdminOnly);
        return app;
    }

    private static async Task<IResult> ListAllTokens(IUserStore users, CancellationToken cancellationToken)
    {
        IReadOnlyList<ApiToken> tokens = await users.ListAllApiTokens(cancellationToken);
        IReadOnlyList<User> everyone = await users.ListUsers(cancellationToken);
        Dictionary<string, User> owners = everyone.ToDictionary(u => u.Id, StringComparer.Ordinal);
        return Results.Ok(tokens.Select(t => AdminApiTokenResponse.From(t, owners.GetValueOrDefault(t.UserId))));
    }

    private static async Task<IResult> ListTokens(HttpContext context, IUserStore users, CancellationToken cancellationToken)
    {
        string userId = context.User.UserId() ?? string.Empty;
        IReadOnlyList<ApiToken> tokens = await users.ListApiTokens(userId, cancellationToken);
        return Results.Ok(tokens.Select(ApiTokenResponse.From));
    }

    private static async Task<IResult> CreateToken(CreateApiTokenRequest? body, HttpContext context, IUserStore users, TokenPolicy policy, SecurityEventLog events, CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return MalformedBody();
        }

        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: "A token name is required.");
        }

        if (body.Name.Length > MaxNameLength)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: $"A token name may be at most {MaxNameLength} characters.");
        }

        ApiTokenScope scope = ApiTokenScope.Full;
        if (body.Scope is not null && !TryParseScope(body.Scope, out scope))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: "Scope must be \"full\" or \"read\".");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!policy.TryResolveExpiry(body.ExpiresInDays, now, out DateTimeOffset expiresAt, out string? problem))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: problem);
        }

        string userId = context.User.UserId() ?? string.Empty;
        string secret = TokenGenerator.NewApiTokenSecret();
        ApiToken token = await users.CreateApiToken(userId, body.Name, TokenGenerator.Hash(secret), now, expiresAt, scope, cancellationToken);
        User? owner = await users.GetUserById(userId, cancellationToken);
        // The id and name, never the secret: nothing in this line could be used to authenticate.
        await events.Record(context, SecurityEventTypes.TokenCreated, target: owner, detail: $"token {token.Id} \"{body.Name}\", {ApiTokenResponse.ScopeName(scope)}, expires {expiresAt:yyyy-MM-dd}");
        return Results.Created($"/api/tokens/{Uri.EscapeDataString(token.Id)}", new ApiTokenCreatedResponse(token.Id, token.Name, token.CreatedAt, expiresAt, ApiTokenResponse.ScopeName(scope), secret));
    }

    /// <summary>404, not 403, for a token that exists but belongs to someone else and the caller may not reach it: an
    /// unauthorized caller learns nothing about whether the id exists at all. Revoking a token other than your own
    /// requires a cookie-authenticated admin (security review F3) - a token can revoke itself (or another of its own
    /// owner's), but never reach across to someone else's, however privileged its own owner is.</summary>
    private static async Task<IResult> RevokeToken(string id, HttpContext context, IUserStore users, SecurityEventLog events, CancellationToken cancellationToken)
    {
        ApiToken? token = await users.GetApiTokenById(id, cancellationToken);
        if (token is null)
        {
            return TokenNotFound(id);
        }

        string userId = context.User.UserId() ?? string.Empty;
        bool isOwnToken = token.UserId == userId;
        if (!isOwnToken && (!AuthPolicies.IsCookieAuthenticated(context.User) || !context.User.IsAdmin()))
        {
            return TokenNotFound(id);
        }

        await users.RevokeApiToken(id, cancellationToken);
        if (!token.Revoked)
        {
            User? owner = await users.GetUserById(token.UserId, cancellationToken);
            await events.Record(context, SecurityEventTypes.TokenRevoked, target: owner, detail: $"token {token.Id} \"{token.Name}\"");
        }

        return Results.NoContent();
    }

    private static bool TryParseScope(string value, out ApiTokenScope scope)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "full":
                scope = ApiTokenScope.Full;
                return true;
            case "read":
                scope = ApiTokenScope.Read;
                return true;
            default:
                scope = default;
                return false;
        }
    }

    private static IResult MalformedBody()
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The body must be a JSON object.");
    }

    private static IResult TokenNotFound(string id)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Token not found", detail: $"No token with id '{id}'.");
    }
}
