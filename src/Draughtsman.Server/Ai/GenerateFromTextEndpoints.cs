using Draughtsman.Core.Serialization;

namespace Draughtsman.Server.Ai;

/// <summary>POST /api/ai/generate-from-text (build-spec-v0.1.md section 9, MVP AI feature 1). 404 while AI is off, per <see cref="AiStatus"/>.</summary>
public static class GenerateFromTextEndpoints
{
    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ai/generate-from-text", GenerateFromText)
            .WithName("GenerateFromText")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        return app;
    }

    private static async Task<IResult> GenerateFromText(
        GenerateFromTextBody? body, AiStatus status, GenerateFromTextService service, CancellationToken cancellationToken)
    {
        // Hidden entirely when AI is off (design/atlas/ai-panel.md): the same answer a route that does not exist
        // would give, not a 501/503 that would let a client detect a configured-but-unavailable provider.
        if (!status.Enabled || !service.IsAvailable)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "AI is not configured",
                detail: "Set the ai section in draughtsman.yaml to enable Generate from text.");
        }

        if (body is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The body must be a JSON object with a \"description\" and \"diagramType\".");
        }

        try
        {
            GenerateFromTextResult result = await service.GenerateAsync(
                new GenerateFromTextRequest(body.DocumentId, body.Description ?? string.Empty, body.DiagramType ?? string.Empty),
                cancellationToken);
            return Results.Json(new GenerateFromTextResponse(
                result.Document, result.Warnings, result.AddedNodeIds, result.AddedEdgeIds,
                result.ChangedElementCount, result.RemovedElementCount,
                new GenerateFromTextProvenance(result.Provider, result.Model)), DocumentJson.Options);
        }
        catch (GenerateFromTextValidationException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: e.Message);
        }
        catch (GenerateFromTextDocumentNotFoundException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Document not found", detail: e.Message);
        }
        catch (GenerateFromTextParseException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The model's output could not be parsed", detail: e.Message);
        }
        catch (GenerateFromTextLayoutException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Layout failed", detail: e.Message);
        }
        catch (GenerateFromTextWriteException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The generated diagram could not be saved", detail: e.Message);
        }
        catch (Draughtsman.Ai.ModelProviderException e)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: e.Reachable ? "The AI provider rejected the request" : "The AI provider is unreachable",
                detail: e.Message);
        }
    }
}

/// <summary>Request body: a description (paragraph, notes or a pasted Mermaid block per the panel's detected-input line) and a diagram type. documentId is omitted to create a new document, or names one to update: the model sees and rewrites that document's whole content, and the reply is applied to it as a diff (GenerateFromTextService).</summary>
public sealed record GenerateFromTextBody(string? DocumentId = null, string? Description = null, string? DiagramType = null);

public sealed record GenerateFromTextResponse(
    Draughtsman.Core.Models.Document Document,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> AddedNodeIds,
    IReadOnlyList<string> AddedEdgeIds,
    int ChangedElementCount,
    int RemovedElementCount,
    GenerateFromTextProvenance Provenance);

public sealed record GenerateFromTextProvenance(string Provider, string Model);
