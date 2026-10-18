using Draughtsman.Server.Configuration;
using Draughtsman.Server.Docs;
using Draughtsman.Server.Hosting;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// The About screen's data (design/atlas/help-about-status.md, PR-04), for every signed-in user. The body is the same
/// for an Editor and an Administrator and deliberately holds nothing an Editor should not see: there is no data folder
/// or any other host detail (that is <see cref="AdminStatusEndpoints"/>). Protected by the global fallback policy.
/// </summary>
public static class AboutEndpoints
{
    /// <summary>Where a running instance serves its bundled licences; the release archive and image carry the file in the web root.</summary>
    public const string NoticesPath = "/THIRD-PARTY-NOTICES.txt";

    public static IEndpointRouteBuilder MapAboutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/about", (BuildInfo build, DraughtsmanConfig config, DocsFolder docs) =>
            {
                AboutConfig about = config.About;
                return Results.Ok(new AboutResponse(
                    build.Version,
                    build.BuildDate.ToString("yyyy-MM-dd"),
                    about.SupportContact,
                    EffectiveDocsUrl(about.DocsUrl, docs),
                    NoticesPath));
            })
            .WithName("GetAbout");
        return app;
    }

    /// <summary>Where the vendor keeps the documentation: the Help menu's target when nothing is configured and this install ships no copy.</summary>
    public const string VendorDocsUrl = "https://overpass.co.uk/draughtsman";

    /// <summary>
    /// What Help > Documentation opens (P21): the configured address when there is one, nothing for an explicitly empty one, and
    /// when it is unset this server's own copy of the guides (served at <c>/docs/</c> behind sign-in, so an air-gapped site has
    /// documentation) if the archive carries it, otherwise the vendor's site.
    /// </summary>
    public static string? EffectiveDocsUrl(string? configured, DocsFolder docs)
    {
        return configured is null ? (docs.Available ? DocsFolder.Route : VendorDocsUrl) : SafeHttpUrl(configured);
    }

    /// <summary>The configured URL only when it is an absolute http(s) address: it becomes an href in the SPA, so a <c>javascript:</c> value never gets that far.</summary>
    public static string? SafeHttpUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;
    }
}

/// <summary>Body of GET /api/about. DocsUrl is omitted when the entry is hidden, is `/docs/` while this server serves its own copy, and is otherwise the configured or vendor address.</summary>
public sealed record AboutResponse(string Version, string BuildDate, string SupportContact, string? DocsUrl, string NoticesUrl);
