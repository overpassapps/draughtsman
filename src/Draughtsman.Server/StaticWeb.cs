using Microsoft.Extensions.FileProviders;

namespace Draughtsman.Server;

/// <summary>
/// Serves the built SPA when a build is present, and does nothing when it is not (API-only dev runs, tests).
/// Looks in wwwroot next to the binary first (the published layout), then web/dist walking up from the content root
/// (the repository layout when running from source).
/// </summary>
public static class StaticWeb
{
    /// <summary>
    /// The product's own icons and brand images (web/public: the favicon and its PNG fallbacks, the apple touch icon, and
    /// <c>brand/</c>) keep fixed names, so a browser may reuse them for a day but must not hold them for ever; the hashed
    /// <c>assets/</c> files and index.html keep the framework's validator-only default, since a stale shell is worse.
    /// </summary>
    internal const string BrandAssetCacheControl = "public, max-age=86400";

    internal static bool IsBrandAsset(PathString path)
    {
        return path.StartsWithSegments("/brand", StringComparison.OrdinalIgnoreCase)
            || path.Value is { } value && (value.Equals("/favicon.svg", StringComparison.OrdinalIgnoreCase)
                || value.Equals("/favicon-16.png", StringComparison.OrdinalIgnoreCase)
                || value.Equals("/favicon-32.png", StringComparison.OrdinalIgnoreCase)
                || value.Equals("/apple-touch-icon.png", StringComparison.OrdinalIgnoreCase));
    }

    public static string? FindWebRoot(string contentRoot)
    {
        string published = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (HasIndex(published))
        {
            return published;
        }

        DirectoryInfo? directory = new(contentRoot);
        for (int depth = 0; directory is not null && depth < 4; depth++)
        {
            string candidate = Path.Combine(directory.FullName, "web", "dist");
            if (HasIndex(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Serves the built assets (JS, CSS, index.html by literal path) as branching middleware, called before
    /// <c>UseAuthentication</c>/<c>UseAuthorization</c> so a signed-out browser can still load the shell that shows
    /// the login page - these are "the static assets the login page needs" (build-spec-v0.1.md section 7's Auth work).
    /// A request for a file this provider does not have falls through to the rest of the pipeline unchanged.
    /// </summary>
    public static WebApplication UseSpaStaticFiles(this WebApplication app, string? webRoot)
    {
        if (webRoot is null)
        {
            app.Logger.LogInformation("No web build found; serving the API only. Run npm run build in web/ to serve the SPA.");
            return app;
        }

        app.Logger.LogInformation("Serving web build from {WebRoot}", webRoot);
        var provider = new PhysicalFileProvider(webRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            OnPrepareResponse = context =>
            {
                if (IsBrandAsset(context.Context.Request.Path))
                {
                    context.Context.Response.Headers.CacheControl = BrandAssetCacheControl;
                }
            },
        });
        return app;
    }

    /// <summary>
    /// Maps the SPA's client-side-routing fallback (any path that is not a real file or a known API route gets
    /// index.html, which is how a direct navigation or refresh on e.g. <c>/login</c> or <c>/documents/{id}</c> still
    /// works). Called alongside the other endpoints, after <c>UseAuthorization</c>, so it needs its own
    /// <c>AllowAnonymous</c>: a signed-out request for any SPA route must still get the shell, not a 401.
    /// </summary>
    public static WebApplication MapSpaFallback(this WebApplication app, string? webRoot)
    {
        // OAuth discovery probes from MCP clients: an HTML 200 here reads as broken JSON metadata ("Unrecognized token '<'")
        // instead of "this server has no OAuth", so the 401 + WWW-Authenticate: Bearer on /mcp is never reached.
        app.Map("/.well-known/{**rest}", () => Results.NotFound()).AllowAnonymous();
        // (Registered even with no web build: the fallback policy would otherwise answer these with a 401.)
        if (webRoot is null)
        {
            return app;
        }

        var provider = new PhysicalFileProvider(webRoot);
        // Unknown API routes stay 404 instead of receiving index.html; explicit routes outrank the fallback. This
        // predates authentication and stays true regardless of it: a typo'd path is still just a 404, not a 401.
        app.Map("/api/{**rest}", () => Results.NotFound()).AllowAnonymous();
        app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = provider }).AllowAnonymous();
        return app;
    }

    private static bool HasIndex(string folder)
    {
        return File.Exists(Path.Combine(folder, "index.html"));
    }
}
