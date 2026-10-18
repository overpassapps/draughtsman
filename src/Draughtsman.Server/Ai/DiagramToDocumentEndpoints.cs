namespace Draughtsman.Server.Ai;

/// <summary>POST /api/ai/diagram-to-document (build-spec-v0.1.md section 9, MVP AI feature 2). 404 while AI is off, per <see cref="AiStatus"/>.</summary>
public static class DiagramToDocumentEndpoints
{
    public static IEndpointRouteBuilder MapDiagramToDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ai/diagram-to-document", DiagramToDocument)
            .WithName("DiagramToDocument")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status502BadGateway);
        return app;
    }

    private static async Task<IResult> DiagramToDocument(
        DiagramToDocumentBody? body, AiStatus status, DiagramToDocumentService service, CancellationToken cancellationToken)
    {
        // Hidden entirely when AI is off (design/atlas/ai-panel.md): the same answer a route that does not exist
        // would give, the same rule generate-from-text follows.
        if (!status.Enabled || !service.IsAvailable)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "AI is not configured",
                detail: "Set the ai section in draughtsman.yaml to enable Diagram to document.");
        }

        if (body is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The body must be a JSON object with a \"documentId\" and an \"outputKind\".");
        }

        try
        {
            DiagramToDocumentResult result = await service.GenerateAsync(
                new DiagramToDocumentRequest(body.DocumentId, body.OutputKind), cancellationToken);
            return Results.Json(new DiagramToDocumentResponse(result.Markdown, new DiagramToDocumentProvenance(result.Provider, result.Model)));
        }
        catch (DiagramToDocumentValidationException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: e.Message);
        }
        catch (DiagramToDocumentDocumentNotFoundException e)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Document not found", detail: e.Message);
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

/// <summary>Request body: the document to read and the output the model should write.</summary>
public sealed record DiagramToDocumentBody(string? DocumentId = null, string? OutputKind = null);

public sealed record DiagramToDocumentResponse(string Markdown, DiagramToDocumentProvenance Provenance);

public sealed record DiagramToDocumentProvenance(string Provider, string Model);
