using Draughtsman.Core.Media;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Theming;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// The admin branding settings (M3; design/atlas/login-admin.md's Instance page, scoped to branding): upload a logo,
/// an optional light-on-dark logo and a favicon, and edit the product name, stored under the instance data folder and
/// applied without a restart (docs/api.md). Every route requires <see cref="AuthPolicies.CookieAdminOnly"/>: a
/// cookie-authenticated admin, never an API token regardless of its owner's role, matching Users and Tokens -
/// instance-wide appearance is not something an agent's token may change. The public read stays
/// <c>GET /api/theme</c>.
/// <para>
/// The upload endpoints are multipart and cookie-authenticated, so the browser attaches the CSRF header
/// (<c>CsrfMiddleware</c>); content is validated by <see cref="BrandingImages"/>, never by the file name. Each change
/// is logged with the acting admin's user id - the audit table is document-scoped, so an instance-level change has no
/// row to write, and this log line is the record (docs/api.md "Audit").
/// </para>
/// </summary>
public static class BrandingEndpoints
{
    public static IEndpointRouteBuilder MapBrandingEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/admin/branding").RequireAuthorization(AuthPolicies.CookieAdminOnly);
        group.MapGet("", Get).WithName("GetBranding");
        group.MapPut("", SetProductName).WithName("SetBrandingProductName");
        group.MapPost("/reset", Reset).WithName("ResetBranding");
        group.MapPost("/{slot}", Upload).WithName("UploadBrandingAsset").DisableAntiforgery();
        group.MapDelete("/{slot}", Delete).WithName("DeleteBrandingAsset");
        return app;
    }

    private static IResult Get(ChromeBrandingService branding)
    {
        return Results.Ok(Describe(branding));
    }

    private static IResult SetProductName(SetProductNameRequest? body, HttpContext context, ChromeBrandingService branding, ILoggerFactory loggerFactory)
    {
        if (body is null)
        {
            return MalformedBody();
        }

        branding.SetProductName(body.ProductName);
        Log(loggerFactory, context, "product name");
        return Results.Ok(Describe(branding));
    }

    private static async Task<IResult> Upload(string slot, HttpRequest request, HttpContext context, ChromeBrandingService branding, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        if (!TryParseSlot(slot, out BrandingSlot parsed))
        {
            return UnknownSlot(slot);
        }

        if (!request.HasFormContentType)
        {
            return MalformedBody();
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
        if (file.Length > BrandingImages.MaxSizeBytes)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "File too large", detail: $"The file is larger than the {BrandingImages.MaxSizeBytes / 1024} KiB limit for a branding image.");
        }

        byte[] bytes;
        await using (Stream stream = file.OpenReadStream())
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        ValidatedBrandingImage image;
        try
        {
            image = BrandingImages.Validate(bytes, parsed);
        }
        catch (InvalidBrandingImageException error)
        {
            return Results.Problem(statusCode: StatusFor(error.Failure), title: "Invalid branding image", detail: error.Message);
        }

        branding.SetAsset(parsed, image);
        Log(loggerFactory, context, slot);
        return Results.Ok(Describe(branding));
    }

    private static IResult Delete(string slot, HttpContext context, ChromeBrandingService branding, ILoggerFactory loggerFactory)
    {
        if (!TryParseSlot(slot, out BrandingSlot parsed))
        {
            return UnknownSlot(slot);
        }

        branding.RemoveAsset(parsed);
        Log(loggerFactory, context, slot);
        return Results.Ok(Describe(branding));
    }

    private static IResult Reset(HttpContext context, ChromeBrandingService branding, ILoggerFactory loggerFactory)
    {
        branding.Reset();
        Log(loggerFactory, context, "all overrides");
        return Results.Ok(Describe(branding));
    }

    private static BrandingResponse Describe(ChromeBrandingService branding)
    {
        EffectiveBranding current = branding.Current;
        BrandingOverrides overrides = current.Overrides;
        return new BrandingResponse(
            current.ThemeName,
            branding.ThemeProductName,
            current.ProductName,
            current.Mode,
            current.Logo is null ? null : "/api/theme/logo",
            current.LogoLight is null ? null : "/api/theme/logo-light",
            current.Favicon is null ? null : "/api/theme/favicon",
            new BrandingOverridden(overrides.HasProductName, overrides.Has(BrandingSlot.Logo), overrides.Has(BrandingSlot.LogoLight), overrides.Has(BrandingSlot.Favicon)));
    }

    private static bool TryParseSlot(string slot, out BrandingSlot parsed)
    {
        switch (slot)
        {
            case "logo":
                parsed = BrandingSlot.Logo;
                return true;
            case "logo-light":
                parsed = BrandingSlot.LogoLight;
                return true;
            case "favicon":
                parsed = BrandingSlot.Favicon;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    private static int StatusFor(BrandingImageFailure failure)
    {
        return failure switch
        {
            BrandingImageFailure.TooLarge => StatusCodes.Status413PayloadTooLarge,
            BrandingImageFailure.UnsupportedType => StatusCodes.Status415UnsupportedMediaType,
            _ => StatusCodes.Status400BadRequest,
        };
    }

    private static void Log(ILoggerFactory loggerFactory, HttpContext context, string what)
    {
        // Never the bytes or the product name text, only what slot changed and who changed it.
        loggerFactory.CreateLogger("Draughtsman.Server.Branding")
            .LogInformation("Branding changed ({What}) by user {ActorUserId}.", what, context.User.UserId() ?? "unknown");
    }

    private static IResult UnknownSlot(string slot)
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown branding slot", detail: $"'{slot}' is not one of logo, logo-light or favicon.");
    }

    private static IResult MalformedBody()
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The body must be multipart/form-data carrying one image file, or a JSON object with a productName.");
    }

    /// <summary>Body of <c>PUT /api/admin/branding</c>. A blank or absent name resets the product name to the theme's.</summary>
    public sealed record SetProductNameRequest(string? ProductName = null);

    /// <summary>Body of every branding response: the effective branding plus which slots the admin has overridden.</summary>
    public sealed record BrandingResponse(
        string ThemeName,
        string ThemeProductName,
        string ProductName,
        string Mode,
        string? Logo,
        string? LogoLight,
        string? Favicon,
        BrandingOverridden Overridden);

    public sealed record BrandingOverridden(bool ProductName, bool Logo, bool LogoLight, bool Favicon);
}
