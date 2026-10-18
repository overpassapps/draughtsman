using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Storage;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Export;
using Draughtsman.Sidecar;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// GET /api/documents/{id}/pdf: a single-page PDF of a document, rendered through the same <see cref="SvgRenderer"/>
/// the MCP <c>render_document</c> path uses, then printed by headless Chromium (build-spec-v0.1.md section 7). The
/// component is optional: when it is absent the endpoint answers 501 with how to enable it, and nothing else changes.
/// The document is laid out first if any node has no geometry, exactly as render_document does.
/// </summary>
public static class ExportEndpoints
{
    /// <summary>What a caller is told when the sidecar failed: its own message can carry the server's file paths, so it goes to the log instead (AU-18).</summary>
    private const string SidecarFailureDetail = "The layout engine is not available or failed; the server log has the reason.";

    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/documents/{id}/pdf", RenderPdfAsync)
            .WithName("RenderPdf")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status501NotImplemented)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return app;
    }

    private static async Task<IResult> RenderPdfAsync(string id, string? pageSize, string? orientation, string? margin, IStore store, ISidecarClient sidecar, IPdfRenderer pdf, IAssetReader assets, IDiagramThemeProvider themes, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (!TryParseOptions(pageSize, orientation, margin, out PdfOptions options, out string? optionsError))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The PDF options are not valid", detail: optionsError);
        }

        Document? document = await store.GetDocument(id, cancellationToken);
        if (document is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Document not found", detail: $"No document with id '{id}'.");
        }

        if (!pdf.IsAvailable)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status501NotImplemented,
                title: "PDF export is not available",
                detail: (await pdf.ProbeAsync(cancellationToken)).Hint ?? ChromiumPdfRenderer.EnableHint);
        }

        if (document.Nodes.Any(n => n.Geometry is null))
        {
            try
            {
                LayoutResult result = await sidecar.LayoutAsync(document, null, cancellationToken);
                document = result.Document;
            }
            catch (SidecarException ex)
            {
                loggers.CreateLogger("Draughtsman.Server.Export").LogWarning(ex, "PDF export could not lay out {DocumentId}", id);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The document could not be laid out.", detail: SidecarFailureDetail);
            }
        }

        try
        {
            Dictionary<string, AssetImage> images = await ResolveAssets(document, assets, cancellationToken);
            // A PDF has no warnings channel, so a document whose connectors cannot be routed is refused rather than
            // exported with straight lines the editor never showed.
            IReadOnlyDictionary<string, IReadOnlyList<Point>> routes = await ConnectorRouting.RouteAsync(sidecar, document, cancellationToken);
            // A sequence diagram stores no positions; the sidecar derives the scene the picture is drawn from.
            SequenceScene? scene = document.Type == DocumentType.Sequence ? await sidecar.SequenceSceneAsync(document, cancellationToken) : null;
            SvgRenderResult rendered = SvgRenderer.Render(document, 1, assetId => images.GetValueOrDefault(assetId), routes, scene, themes.Default);
            byte[] bytes = await pdf.RenderAsync(rendered.Svg, options, cancellationToken);
            return Results.File(bytes, contentType: "application/pdf", fileDownloadName: $"{document.Id}.pdf");
        }
        catch (SidecarException ex)
        {
            loggers.CreateLogger("Draughtsman.Server.Export").LogWarning(ex, "PDF export could not route {DocumentId}", id);
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The connectors could not be routed.", detail: SidecarFailureDetail);
        }
        catch (PdfTimeoutException ex)
        {
            // Before the generic InvalidOperationException below, which it derives from: a hung browser is a gateway timeout with its own advice, not a 500.
            loggers.CreateLogger("Draughtsman.Server.Export").LogWarning("PDF export of {DocumentId} timed out: {Reason}", id, ex.Message);
            return Results.Problem(statusCode: StatusCodes.Status504GatewayTimeout, title: "PDF export timed out", detail: ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            loggers.CreateLogger("Draughtsman.Server.Export").LogError(ex, "PDF export could not render {DocumentId}", id);
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The document could not be rendered.", detail: "The server log has the reason.");
        }
        catch (PdfBusyException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "PDF export is busy", detail: ex.Message);
        }
        catch (PdfUnavailableException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "PDF export is not available", detail: ex.Message);
        }
    }

    /// <summary>
    /// The query's <c>pageSize</c> (<c>fit</c>, <c>a4</c>, <c>a3</c>, <c>letter</c>; default fit), <c>orientation</c> (<c>landscape</c> or
    /// <c>portrait</c>; default landscape) and <c>margin</c> (millimetres, 0 to 50; default none for fit, 10 otherwise).
    /// </summary>
    private static bool TryParseOptions(string? pageSize, string? orientation, string? margin, out PdfOptions options, out string? error)
    {
        options = new PdfOptions();
        error = null;
        PdfPageSize size = PdfPageSize.Fit;
        PdfOrientation direction = PdfOrientation.Landscape;
        // Enum.TryParse would also take a bare number ("2"), which is not a named option.
        if (pageSize is not null && !TryParseName(pageSize, out size))
        {
            error = $"pageSize must be one of fit, a4, a3, letter (got '{pageSize}').";
            return false;
        }

        if (orientation is not null && !TryParseName(orientation, out direction))
        {
            error = $"orientation must be landscape or portrait (got '{orientation}').";
            return false;
        }

        double? marginMm = null;
        if (margin is not null)
        {
            if (!double.TryParse(margin, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            {
                error = $"margin must be a number of millimetres (got '{margin}').";
                return false;
            }

            marginMm = parsed;
        }

        options = new PdfOptions(size, direction, marginMm);
        error = options.Validate();
        return error is null;
    }

    private static bool TryParseName<T>(string text, out T value)
        where T : struct, Enum
    {
        value = default;
        return Enum.GetNames<T>().Contains(text, StringComparer.OrdinalIgnoreCase) && Enum.TryParse(text, ignoreCase: true, out value);
    }

    /// <summary>Every image node's bytes, keyed by asset id, so the render can inline them as data URIs.</summary>
    private static async Task<Dictionary<string, AssetImage>> ResolveAssets(Document document, IAssetReader assets, CancellationToken cancellationToken)
    {
        var images = new Dictionary<string, AssetImage>(StringComparer.Ordinal);
        foreach (Node node in document.Nodes)
        {
            string? assetId = AssetIdOf(node);
            if (node.Shape != "image" || assetId is null || images.ContainsKey(assetId))
            {
                continue;
            }

            AssetContent? content = await assets.ReadAsset(assetId, cancellationToken);
            if (content is not null)
            {
                images[assetId] = new AssetImage(content.ContentType, content.Bytes);
            }
        }

        return images;
    }

    private static string? AssetIdOf(Node node)
    {
        if (node.Props is null || !node.Props.TryGetValue("assetId", out System.Text.Json.JsonElement value) || value.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return null;
        }

        return string.IsNullOrEmpty(value.GetString()) ? null : value.GetString();
    }
}
