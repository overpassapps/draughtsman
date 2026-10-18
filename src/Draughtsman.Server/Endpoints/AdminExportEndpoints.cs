using Draughtsman.Core.Auth;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Backup;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// <c>GET /api/admin/export</c>: every document (JSON and DSL) and every uploaded image in one zip (OP-01, docs/api.md
/// "Export everything"). Cookie-authenticated administrators only, like the other instance-wide admin routes: an API token
/// carries its owner's role and must not be able to pull the whole instance out through itself. Each call is recorded as a
/// security event. Documents in the bin are left out unless <c>?includeBin=true</c> asks for them (they then go under <c>bin/</c>).
/// </summary>
public static class AdminExportEndpoints
{
    public static IEndpointRouteBuilder MapAdminExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/export", ExportAsync)
            .WithName("ExportEverything")
            .RequireAuthorization(AuthPolicies.CookieAdminOnly)
            .Produces(StatusCodes.Status200OK, contentType: "application/zip");
        return app;
    }

    private static async Task<IResult> ExportAsync(HttpContext context, IStore store, IStoreMaintenance maintenance, DataFolder dataFolder, SecurityEventLog securityEvents, CancellationToken cancellationToken)
    {
        // A temp file rather than the response body: ZipArchive writes synchronously, which Kestrel refuses on the response
        // stream, and a finished file gives the download a length. DeleteOnClose removes it when the response ends.
        string temp = Path.Combine(Path.GetTempPath(), "draughtsman-export-" + Guid.NewGuid().ToString("N") + ".zip");
        var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            bool includeBin = string.Equals(context.Request.Query["includeBin"].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);
            ExportManifest manifest = await InstanceExport.WriteAsync(file, store, maintenance, dataFolder, HealthEndpoints.ProductVersion, includeBin, cancellationToken);
            file.Position = 0;
            await securityEvents.Record(context, SecurityEventTypes.DataExported, detail: $"{manifest.Documents.Count} documents{(includeBin ? $" ({manifest.Documents.Count(d => d.DeletedAt is not null)} from the bin)" : string.Empty)}, {manifest.Assets.Count} assets");
            string name = $"draughtsman-export-{DateTime.UtcNow:yyyyMMddHHmmss}.zip";
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(file, "application/zip", name);
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }
}
