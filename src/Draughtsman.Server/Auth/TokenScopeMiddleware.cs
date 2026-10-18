namespace Draughtsman.Server.Auth;

/// <summary>
/// Enforces a read-scoped API token (AU-05): the one place a scope is checked for the HTTP API, so a route added later
/// is covered without anyone remembering to. A read-scoped token may make GET, HEAD and OPTIONS requests and nothing
/// else; every other method is refused with 403 before authorization or the endpoint runs. <c>/mcp</c> is the one
/// POST surface a read token can still use: it speaks JSON-RPC over POST, so the MCP tools that write refuse a
/// read-only caller themselves (<c>DraughtsmanTools</c>), and everything else it offers only reads. Runs after
/// <c>UseAuthentication</c>, so <see cref="HttpContext.User"/> already says which token, if any, is calling.
/// </summary>
public sealed class TokenScopeMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User.IsReadOnlyToken()
            && !SafeMethods.Contains(context.Request.Method)
            && !context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Read-only token", detail: "This API token is read-only; it may only make GET requests.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
