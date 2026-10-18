using Draughtsman.Core.Media;
using Draughtsman.Server.Assets;
using Draughtsman.Server.Theming;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// Serves the chrome theme (spec section 8) so the shipped SPA picks it up at runtime without a rebuild: the CSS
/// custom properties compiled from the configured YAML, a small manifest naming the product, mode and logo/favicon,
/// and the logo and favicon assets themselves. An admin can override the product name, logo, light-on-dark logo and
/// favicon from the admin settings page; those are read from <see cref="ChromeBrandingService"/> on every request, so
/// a change lands on the next load with no restart. See docs/api.md.
/// <para>
/// Every route here is anonymous: the login page renders the instance's logo and product name from the chrome theme
/// (design/atlas/login-admin.md), so these are reachable before sign-in the same way the static assets that render
/// the login page's HTML/CSS/JS are.
/// </para>
/// </summary>
internal static class ChromeThemeEndpoints
{
    public static IEndpointRouteBuilder MapThemeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/theme/chrome.css", (HttpContext context, ChromeBrandingService branding) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.Text(branding.Css, "text/css; charset=utf-8");
            })
            .WithName("GetChromeThemeCss")
            .AllowAnonymous();

        app.MapGet("/api/theme", (HttpContext context, ChromeBrandingService branding) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                EffectiveBranding current = branding.Current;
                return Results.Ok(new ChromeThemeManifestResponse(
                    current.ThemeName,
                    current.ProductName,
                    current.Logo is null ? null : "/api/theme/logo",
                    current.LogoLight is null ? null : "/api/theme/logo-light",
                    current.LogoCompact is null ? null : "/api/theme/logo-compact",
                    current.LogoCompactLight is null ? null : "/api/theme/logo-compact-light",
                    current.Favicon is null ? null : "/api/theme/favicon",
                    current.Mode));
            })
            .WithName("GetChromeTheme")
            .AllowAnonymous();

        app.MapGet("/api/theme/logo", (HttpContext context, ChromeBrandingService branding) =>
                ServeAsset(context, branding.Current.Logo))
            .WithName("GetChromeThemeLogo")
            .AllowAnonymous();

        app.MapGet("/api/theme/logo-light", (HttpContext context, ChromeBrandingService branding) =>
                ServeAsset(context, branding.Current.LogoLight))
            .WithName("GetChromeThemeLogoLight")
            .AllowAnonymous();

        app.MapGet("/api/theme/logo-compact", (HttpContext context, ChromeBrandingService branding) =>
                ServeAsset(context, branding.Current.LogoCompact))
            .WithName("GetChromeThemeLogoCompact")
            .AllowAnonymous();

        app.MapGet("/api/theme/logo-compact-light", (HttpContext context, ChromeBrandingService branding) =>
                ServeAsset(context, branding.Current.LogoCompactLight))
            .WithName("GetChromeThemeLogoCompactLight")
            .AllowAnonymous();

        app.MapGet("/api/theme/favicon", (HttpContext context, ChromeBrandingService branding) =>
                ServeAsset(context, branding.Current.Favicon))
            .WithName("GetChromeThemeFavicon")
            .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Serves one resolved asset. An SVG is served as an image under a restrictive Content-Security-Policy and with
    /// nosniff - even an admin-uploaded one that passed the sanitiser - so script in it stays inert and it is never
    /// treated as a document this origin runs. No-store: the admin can replace any of these at any time.
    /// </summary>
    private static IResult ServeAsset(HttpContext context, ChromeThemeAsset? asset)
    {
        if (asset is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No branding asset", detail: "The chrome theme names no asset for this slot.");
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (asset.ContentType == AssetTypes.Svg)
        {
            context.Response.Headers["Content-Security-Policy"] = AssetEndpoints.SvgContentSecurityPolicy;
        }

        return Results.Bytes(asset.Content, asset.ContentType);
    }
}

/// <summary>
/// Body of GET /api/theme. Logo, logoLight and favicon are endpoint paths, or null when the theme (and any overrides)
/// name no asset that resolved. `logoLight` falls back to `logo` in the service when no light-on-dark variant exists,
/// so it is non-null exactly when `logo` is. `logoCompact`/`logoCompactLight` are the theme's optional header-only marks
/// and are null when the theme names none or an admin has uploaded a logo.
/// </summary>
internal sealed record ChromeThemeManifestResponse(string Name, string ProductName, string? Logo, string? LogoLight, string? LogoCompact, string? LogoCompactLight, string? Favicon, string Mode);
