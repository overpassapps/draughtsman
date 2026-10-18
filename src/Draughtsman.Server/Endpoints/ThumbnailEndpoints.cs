using Draughtsman.Core.Storage;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Documents;
using Draughtsman.Server.Thumbnails;
using Draughtsman.Sidecar;
using Microsoft.Net.Http.Headers;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// GET /api/documents/{id}/thumbnail: the document drawn small, as an SVG that fits a fixed 320 by 200 box, for the documents page
/// (docs/api.md). It is the renderer behind the PDF export and <c>render_document</c> (no browser, so it works on every install),
/// drawn in the organisation's diagram theme on its paper colour. It is served as an image, never inline: the page shows it
/// through <c>&lt;img src&gt;</c> and the response carries the same locked-down policy as an uploaded SVG, so nothing in it can run
/// even if it were opened on its own. Same sign-in and the same per-document rules as reading the document; a binned or missing
/// document is a 404.
/// <para>
/// The ETag is the document's version tag and the thumbnail's variant (the organisation theme and the build) joined, so a browser
/// revalidates a thumbnail whenever the document, the theme or the drawing could have changed and gets a 304 with no body, and
/// the 304 is answered from the stored tag without reading or drawing anything. <c>Cache-Control</c> is <c>private, no-cache</c>:
/// kept by the browser, never by a shared cache, always revalidated.
/// </para>
/// </summary>
public static class ThumbnailEndpoints
{
    /// <summary>Names what was drawn: <c>full</c>, <c>simplified</c>, <c>empty</c>, <c>too-large</c> or <c>unavailable</c> (<see cref="ThumbnailKind"/>).</summary>
    public const string KindHeader = "X-Draughtsman-Thumbnail";

    public static IEndpointRouteBuilder MapThumbnailEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/documents/{id}/thumbnail", GetThumbnailAsync)
            .WithName("GetDocumentThumbnail")
            .Produces(StatusCodes.Status200OK, contentType: "image/svg+xml")
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return app;
    }

    private static async Task<IResult> GetThumbnailAsync(string id, HttpRequest request, HttpResponse response, IStore store, ISidecarClient sidecar, IDiagramThemeProvider themes, ThumbnailService thumbnails, CancellationToken cancellationToken)
    {
        // The stored tag is one column read, so a revalidation (the common request) never parses or draws anything.
        string? tag = await store.GetDocumentTag(id, cancellationToken);
        if (tag is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Document not found", detail: $"No document with id '{id}'.");
        }

        DiagramThemeChoice? theme = themes.Default;
        string variant = ThumbnailService.VariantOf(theme);
        SetCacheHeaders(response);
        if (BaseVersionHeader.IsCurrent(request, ETagValue(tag, variant)))
        {
            response.Headers.ETag = BaseVersionHeader.ETagOf(ETagValue(tag, variant));
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        Thumbnail? thumbnail = await thumbnails.GetAsync(id, tag, theme, token => store.GetStoredDocument(id, token), sidecar, cancellationToken);
        if (thumbnail is null)
        {
            // Deleted between the tag read and the draw.
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Document not found", detail: $"No document with id '{id}'.");
        }

        response.Headers[KindHeader] = KindName(thumbnail.Kind);
        if (thumbnail.Degraded)
        {
            // Worse than it will be once the layout engine answers: no ETag, so the next request asks again.
            response.Headers.CacheControl = "private, no-store";
        }
        else
        {
            response.Headers.ETag = BaseVersionHeader.ETagOf(ETagValue(thumbnail.Version, thumbnail.Variant));
        }

        return Results.Bytes(thumbnail.Svg, "image/svg+xml");
    }

    private static void SetCacheHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "private, no-cache";
        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        // An SVG is a document that can carry script; served as an image under this policy, a script inside one is inert (AssetEndpoints).
        response.Headers.ContentSecurityPolicy = AssetEndpoints.SvgContentSecurityPolicy;
    }

    private static string ETagValue(string version, string variant)
    {
        return version + "." + variant;
    }

    private static string KindName(ThumbnailKind kind)
    {
        return kind switch
        {
            ThumbnailKind.Full => "full",
            ThumbnailKind.Simplified => "simplified",
            ThumbnailKind.Empty => "empty",
            ThumbnailKind.TooLarge => "too-large",
            _ => "unavailable",
        };
    }
}
