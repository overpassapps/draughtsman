using Microsoft.AspNetCore.Antiforgery;

namespace Draughtsman.Server.Auth;

/// <summary>
/// CSRF protection on every state-changing cookie-authenticated request (build-spec-v0.1.md section 7). A bearer
/// token cannot be replayed by a browser the way an ambient cookie can, so a token-authenticated request is never
/// checked here, and first-run/login themselves are exempt too: there is no existing session cookie yet for a forged
/// request to ride on. Runs after <c>UseAuthentication</c>, so <see cref="HttpContext.User"/> already reflects which
/// scheme (if any) authenticated the request.
/// <para>
/// The antiforgery pair is issued only where the caller's identity actually changes - <c>AuthEndpoints</c> calls
/// <see cref="IssueTokenCookie"/> itself, from inside the first-run/login/logout handler, before returning the
/// <c>IResult</c> that writes the response. That has to happen there rather than unconditionally in this middleware:
/// a request token embeds the caller's claims identity, and <c>IAntiforgery.GetAndStoreTokens</c> caches its result
/// for the rest of the request the first time it is called, so an unconditional call before the endpoint runs would
/// bake in the identity the caller had a moment ago, and calling it again after <c>next()</c> returns is too late -
/// a minimal API result has already written and committed the response by then.
/// </para>
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string XsrfCookieName = "XSRF-TOKEN";

    public const string XsrfHeaderName = "X-XSRF-TOKEN";

    /// <summary>Set to <see cref="ReissuedValue"/> on the 403 that carries a fresh pair, so a client that can retry knows the retry will carry the right token.</summary>
    public const string ReissuedHeaderName = "X-Draughtsman-Csrf";

    public const string ReissuedValue = "reissued";

    private static readonly HashSet<string> StateChangingMethods = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH", "DELETE" };

    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(antiforgery);

        bool cookieAuthenticated = string.Equals(context.User.Identity?.AuthenticationType, AuthSchemes.Cookie, StringComparison.Ordinal);
        if (cookieAuthenticated && StateChangingMethods.Contains(context.Request.Method) && !await antiforgery.IsRequestValidAsync(context))
        {
            // A pair this server cannot read (a tab that outlived an upgrade, which changed the key the framework protects them with; a cookie lost
            // between visits) is replaced in the refusal itself, so the very next attempt carries a valid one instead of failing until the page is
            // reloaded. Reaching here means the session cookie authenticated the caller, so this is the same pair GET /api/auth/status hands out.
            IssueTokenCookie(context, antiforgery);
            context.Response.Headers[ReissuedHeaderName] = ReissuedValue;
            await Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "CSRF validation failed", detail: $"Missing or invalid {XsrfHeaderName} header.")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    /// <summary>
    /// (Re)issues the antiforgery pair for the current <see cref="HttpContext.User"/>: the framework's own HttpOnly
    /// secret cookie, plus the request-token half as a readable <see cref="XsrfCookieName"/> cookie the SPA reads and
    /// echoes back in <see cref="XsrfHeaderName"/> on its own mutating calls. Call this before returning the
    /// <c>IResult</c> that ends a request which just changed who <see cref="HttpContext.User"/> is.
    /// </summary>
    public static void IssueTokenCookie(HttpContext context, IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(antiforgery);

        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append(XsrfCookieName, tokens.RequestToken ?? string.Empty, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Lax,
            // server.forceHttps (security review F5) overrides the request scheme upstream of every handler that
            // calls this, so this reads true there even when this process cannot itself see that the connection is
            // HTTPS, e.g. behind a proxy terminating TLS without a trusted forwarded-proto header.
            Secure = context.Request.IsHttps,
            Path = "/",
        });
    }
}
