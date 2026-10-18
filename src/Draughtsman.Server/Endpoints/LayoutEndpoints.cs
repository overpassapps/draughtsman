using System.Text.Json;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Sidecar;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// POST /api/layout: a document in, <c>{ document, warnings }</c> out with the document laid out by ELK. Stateless,
/// so importers, the SPA's "Tidy" and agents all use it the same way. Nodes may arrive with no geometry; every node
/// leaves with it. Warnings list what the layout left alone (an edge to a missing node, a self-loop, and so on).
/// The body is read into <see cref="Document"/> with the document converters before anything is sent, so a body
/// the model cannot express is the caller's 422 and a 502 only ever means the sidecar's answer could not be read.
/// </summary>
public static class LayoutEndpoints
{
    public static IEndpointRouteBuilder MapLayoutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/layout", LayoutAsync)
            .WithName("LayoutDocument")
            .Accepts<JsonElement>("application/json")
            .Produces<LayoutResponse>(StatusCodes.Status200OK, contentType: "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
        return app;
    }

    private static async Task<IResult> LayoutAsync(
        JsonElement document,
        string? algorithm,
        string? direction,
        ISidecarClient sidecar,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The body must be a document object.");
        }

        if (!TryParseAlgorithm(algorithm, out LayoutAlgorithm? parsedAlgorithm))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "algorithm must be layered, stress or tree.");
        }

        if (!TryParseDirection(direction, out LayoutDirection? parsedDirection))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "direction must be down, right, up or left.");
        }

        Document input;
        try
        {
            input = JsonSerializer.Deserialize<Document>(document, DocumentJson.Options)
                ?? throw new JsonException("The body is null.");
        }
        catch (JsonException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The document could not be laid out.", detail: ex.Message);
        }

        ILogger logger = loggerFactory.CreateLogger("Draughtsman.Server.Layout");
        var options = new LayoutOptions { Algorithm = parsedAlgorithm, Direction = parsedDirection };
        try
        {
            LayoutResult result = await sidecar.LayoutAsync(input, options, cancellationToken);
            if (result.Warnings.Count > 0)
            {
                logger.LogInformation("Layout finished with warnings: {Warnings}", string.Join("; ", result.Warnings));
            }

            return Results.Json(new LayoutResponse(result.Document, result.Warnings), DocumentJson.Options);
        }
        catch (SidecarRpcException ex) when (ex.IsClientFault)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The document could not be laid out.", detail: ex.Message);
        }
        catch (SidecarUnavailableException ex)
        {
            logger.LogWarning(ex, "Layout refused: the sidecar is not available");
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The layout engine is not available.", detail: "The sidecar is not running; the server log has the reason. It restarts on its own.");
        }
        catch (SidecarTimeoutException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status504GatewayTimeout, title: "The layout engine did not answer in time.", detail: ex.Message);
        }
        catch (SidecarException ex)
        {
            // Its message can carry the server's file paths; the log has it, the caller does not (AU-18).
            logger.LogWarning(ex, "Layout failed in the sidecar");
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The layout engine failed.", detail: "The server log has the reason.");
        }
    }

    /// <summary>Exactly the documented lower-case values (docs/api.md); absent means the sidecar's default.</summary>
    private static bool TryParseAlgorithm(string? value, out LayoutAlgorithm? parsed)
    {
        parsed = value switch
        {
            null or "" => null,
            "layered" => LayoutAlgorithm.Layered,
            "stress" => LayoutAlgorithm.Stress,
            "tree" => LayoutAlgorithm.Tree,
            _ => null,
        };
        return parsed is not null || string.IsNullOrEmpty(value);
    }

    private static bool TryParseDirection(string? value, out LayoutDirection? parsed)
    {
        parsed = value switch
        {
            null or "" => null,
            "down" => LayoutDirection.Down,
            "right" => LayoutDirection.Right,
            "up" => LayoutDirection.Up,
            "left" => LayoutDirection.Left,
            _ => null,
        };
        return parsed is not null || string.IsNullOrEmpty(value);
    }
}

/// <summary>Body of a successful POST /api/layout; the same envelope the importers return.</summary>
public sealed record LayoutResponse(Document Document, IReadOnlyList<string> Warnings);
