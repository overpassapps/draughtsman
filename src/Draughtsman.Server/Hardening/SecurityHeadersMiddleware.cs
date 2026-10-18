namespace Draughtsman.Server.Hardening;

/// <summary>
/// The browser security headers on every response (AU-13), SPA shell, static assets, API and error pages alike. Until this
/// existed only asset and theme-image responses carried any: the app could be framed, had no Content-Security-Policy to
/// contain an injection should one ever be introduced, and authenticated JSON carried no cache directive.
/// Headers are added as the response starts and only where the route has not already chosen its own (an uploaded SVG's
/// sandboxing policy, an asset's long-lived private cache), so nothing a route set deliberately is overwritten.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    /// <summary>
    /// What the SPA needs and nothing more: its own scripts (and <c>'unsafe-eval'</c>, below), styles (inline style attributes included, which React and SVG
    /// write), images from itself, data: and blob: (PNG export draws the SVG through an image; uploads are sized the same
    /// way), its routing worker, and same-origin requests. No framing, no plug-ins, no <c>&lt;base&gt;</c>, forms only to itself.
    /// </summary>
    /// <remarks>
    /// <c>'unsafe-eval'</c> is here because the browser smoke test (scripts/security-headers-smoke.mjs) showed the SPA blank
    /// without it: <c>web/src/model/parse.ts</c> compiles the document schema with Ajv at run time, and Ajv compiles with
    /// <c>new Function</c>. It is the one concession. There is still no <c>'unsafe-inline'</c> and no foreign script host, so
    /// injected markup cannot run script, which is what the policy is for; <c>'unsafe-eval'</c> only lets script that is
    /// already running evaluate a string, and nothing in the app passes one a string it did not write. The way to drop it is to
    /// precompile the validator at build time (Ajv's standalone output, like <c>npm run tokens</c> does for themes); that is a
    /// web build change, left as a follow-up rather than made inside this task.
    /// </remarks>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; " +
        "connect-src 'self'; worker-src 'self' blob:; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var http = (HttpContext)state;
            IHeaderDictionary headers = http.Response.Headers;
            headers.TryAdd("Content-Security-Policy", ContentSecurityPolicy);
            headers.TryAdd("X-Content-Type-Options", "nosniff");
            headers.TryAdd("Referrer-Policy", "same-origin");
            headers.TryAdd("X-Frame-Options", "DENY");
            headers.TryAdd("Permissions-Policy", PermissionsPolicy);
            if (http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                // Authenticated JSON must not be kept by a browser or an intermediary after sign-out. Routes that chose a
                // policy of their own (an immutable private asset) keep it. no-transform asks an intermediary to pass the
                // payload through as sent; it is a request, not a guarantee (Cloudflare ignored it in our test), and a document's version is also sent in
                // X-Document-Version so a gateway that compresses or re-tags the response anyway does not break a save.
                headers.TryAdd("Cache-Control", "no-store, no-transform");
            }

            return Task.CompletedTask;
        }, context);
        return _next(context);
    }
}
