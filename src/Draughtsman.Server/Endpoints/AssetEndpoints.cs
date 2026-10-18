using Draughtsman.Core.Media;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Assets;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// The asset API (build-spec-v0.1.md section 7, docs/api.md): <c>POST /api/assets</c> stores an uploaded image
/// content-addressed on disk and returns its id, and <c>GET /api/assets/{id}</c> serves it. An image node's
/// <c>props.assetId</c> points at the id. The file content, not its name, decides the type, and an SVG is served
/// with a restrictive Content-Security-Policy and as an image so a script inside it can never run as this origin.
/// Authentication, when it lands, wraps these like every other <c>/api</c> route; nothing here adds its own.
/// </summary>
public static class AssetEndpoints
{
    public const string SvgContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";

    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/assets");
        group.MapPost("", UploadAsset).WithName("UploadAsset").DisableAntiforgery();
        group.MapGet("/{id}", GetAsset).WithName("GetAsset");
        return app;
    }

    private static async Task<IResult> UploadAsset(HttpRequest request, AssetStore assets, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The body must be multipart/form-data carrying one image file.");
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The multipart body could not be read.");
        }

        if (form.Files.Count == 0)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No file", detail: "The multipart body carried no file.");
        }

        IFormFile file = form.Files[0];
        try
        {
            await using Stream stream = file.OpenReadStream();
            AssetInfo asset = await assets.UploadAsync(stream, file.Length, cancellationToken);
            return Results.Created($"/api/assets/{Uri.EscapeDataString(asset.Id)}", new AssetResponse(asset.Id, asset.ContentType, asset.Size));
        }
        catch (AssetTooLargeException error)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "File too large",
                detail: $"The file is larger than the {error.MaxSizeBytes} byte limit.");
        }
        catch (AssetQuotaExceededException error)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status507InsufficientStorage,
                title: "Asset quota reached",
                detail: $"Storing this image would pass the instance's {error.QuotaBytes} byte quota for uploaded images (assets.quotaBytes in draughtsman.yaml).");
        }
        catch (UnsupportedAssetException error)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "Unsupported image type",
                detail: error.Message);
        }
    }

    private static async Task<IResult> GetAsset(string id, HttpResponse response, IAssetReader assets, CancellationToken cancellationToken)
    {
        AssetContent? asset = await assets.ReadAsset(id, cancellationToken);
        if (asset is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Asset not found", detail: $"No asset with id '{id}'.");
        }

        // An uploaded image never changes under its id, so a client may cache it forever (docs/api.md). Private, not
        // public: assets sit behind sign-in, so a shared proxy must never hold a signed-in user's image.
        response.Headers["Cache-Control"] = "private, max-age=31536000, immutable";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        if (asset.ContentType == AssetTypes.Svg)
        {
            // An SVG is a document that can carry script; served as an image with no scripts and no external loads, a
            // script inside it is inert. The canvas draws it through <image href>, never inlined into the DOM, too.
            response.Headers["Content-Security-Policy"] = SvgContentSecurityPolicy;
        }

        return Results.File(asset.Bytes, asset.ContentType);
    }

    /// <summary>The body of a successful upload: the asset id an image node stores in <c>props.assetId</c>.</summary>
    public sealed record AssetResponse(string Id, string ContentType, long Size);
}
