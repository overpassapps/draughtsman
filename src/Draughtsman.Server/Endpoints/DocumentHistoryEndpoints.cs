using System.Text.Json.Nodes;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Diffing;
using Draughtsman.Core.Models;
using Draughtsman.Core.Storage;
using Draughtsman.Mcp.Tools;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Documents;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// What the editor's History and Review panels need and MCP already had (PR-08, docs/api.md "History, compare and
/// restore"): name a snapshot, compare two revisions, restore one, and run the critique. None of it is new logic. Compare is
/// <see cref="DocumentDiff"/> and critique is <see cref="CritiqueAnalyser"/>, the very code behind the <c>diff_documents</c> and
/// <c>critique_document</c> tools, returned in the same shapes; restore is the guarded replace every save goes through,
/// preceded by a checkpoint of what it replaces so the restore is itself undoable.
/// </summary>
public static class DocumentHistoryEndpoints
{
    /// <summary>The revision name the diff takes for the document as it is stored now (the same word the MCP tool uses).</summary>
    public const string Current = "current";

    /// <summary>Longest name a snapshot can carry (the column's width).</summary>
    public const int MaxSnapshotName = 256;

    public static IEndpointRouteBuilder MapDocumentHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/documents/{id}");
        group.MapPost("/versions", NameSnapshot).WithName("NameSnapshot");
        group.MapPost("/versions/{versionId}/restore", RestoreVersion).WithName("RestoreVersion");
        group.MapGet("/diff", DiffRevisions).WithName("DiffRevisions");
        group.MapGet("/critique", CritiqueStored).WithName("CritiqueDocument");
        // The editor asks about what is on the canvas, which can be ahead of the saved copy; the body is the document as it stands.
        app.MapPost("/api/critique", CritiqueBody).WithName("CritiqueBody");
        return app;
    }

    /// <summary>A named snapshot of the document as it is stored now: a restore point a person chose, which retention never prunes.</summary>
    private static async Task<IResult> NameSnapshot(string id, HttpRequest request, IStore store, CancellationToken cancellationToken)
    {
        if (!AuthenticatedActor.TryResolve(request, out Actor actor, out string? actorUserId, out IResult? problem))
        {
            return problem!;
        }

        JsonObject? body = await DocumentEndpoints.ReadBody(request, cancellationToken);
        string? name = body?["name"] is JsonValue { } nameValue && nameValue.TryGetValue(out string? text) ? text.Trim() : null;
        string? reason = body?["reason"] is JsonValue { } reasonValue && reasonValue.TryGetValue(out string? reasonText) && !string.IsNullOrWhiteSpace(reasonText) ? reasonText.Trim() : null;
        if (string.IsNullOrEmpty(name) || name.Length > MaxSnapshotName)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: $"The body must be a JSON object with a non-empty \"name\" string of at most {MaxSnapshotName} characters.");
        }

        if (string.Equals(name, DocumentEndpoints.CheckpointVersionName, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Reserved name", detail: $"\"{DocumentEndpoints.CheckpointVersionName}\" is the name of the automatic snapshots taken before an edit, which the server prunes; choose another name for one you want to keep.");
        }

        try
        {
            DocumentVersion version = await store.CreateVersion(id, name, actor, reason, cancellationToken);
            await store.AppendAudit(new AuditEntry(id, actor, AuditActions.Snapshotted, DateTimeOffset.UtcNow, actorUserId), cancellationToken);
            return Results.Created($"/api/documents/{Uri.EscapeDataString(id)}/versions/{Uri.EscapeDataString(version.Id)}", version.ToSummary());
        }
        catch (DocumentNotFoundException)
        {
            return DocumentEndpoints.DocumentNotFound(id);
        }
    }

    /// <summary>
    /// Puts the document back to an earlier version. A guarded save like any other (<c>If-Match</c> is the version the caller is
    /// looking at, so a restore over an edit it never saw is refused), and it checkpoints the snapshot it replaces first, under
    /// the same name an agent's edit uses, so "restore" can itself be reversed from the same list.
    /// The document's identity and creation header stay; its title, content and theme are the version's.
    /// </summary>
    private static async Task<IResult> RestoreVersion(string id, string versionId, HttpRequest request, IStore store, CancellationToken cancellationToken)
    {
        if (!AuthenticatedActor.TryResolve(request, out Actor actor, out string? actorUserId, out IResult? problem)
            || !BaseVersionHeader.TryResolve(request, out string? baseTag, out problem))
        {
            return problem!;
        }

        StoredDocument? stored = await store.GetStoredDocument(id, cancellationToken);
        if (stored is null)
        {
            return DocumentEndpoints.DocumentNotFound(id);
        }

        string baseVersion = BaseVersionHeader.Unquote(baseTag);
        if (baseVersion != stored.Version)
        {
            return DocumentEndpoints.StaleSave(id, stored.Version, baseTag);
        }

        DocumentVersion? version = await store.GetVersion(id, versionId, cancellationToken);
        if (version is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Version not found", detail: $"No version '{versionId}' of document '{id}'.");
        }

        Document existing = stored.Document;
        Document restored = version.Document with
        {
            Id = id,
            Meta = version.Document.Meta with
            {
                CreatedAt = existing.Meta.CreatedAt,
                CreatedBy = existing.Meta.CreatedBy,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        };

        try
        {
            // Checkpoint first, replace second, like an agent's replace (DocumentEndpoints.ReplaceDocument): if the replace then
            // loses a race the checkpoint stays, one more restore point of a snapshot that really existed.
            await store.CreateVersion(id, DocumentEndpoints.CheckpointVersionName, actor, $"Before restoring \"{version.Name}\" ({version.CreatedAt:u})", cancellationToken);
            ReplaceOutcome outcome = await store.ReplaceDocument(restored, baseVersion, cancellationToken);
            if (outcome == ReplaceOutcome.NotFound)
            {
                return DocumentEndpoints.DocumentNotFound(id);
            }

            if (outcome == ReplaceOutcome.VersionMismatch)
            {
                string? moved = await store.GetDocumentTag(id, cancellationToken);
                return moved is null ? DocumentEndpoints.DocumentNotFound(id) : DocumentEndpoints.StaleSave(id, moved, baseTag);
            }

            await store.AppendAudit(new AuditEntry(id, actor, AuditActions.VersionRestored, DateTimeOffset.UtcNow, actorUserId), cancellationToken);
        }
        catch (DocumentNotFoundException)
        {
            return DocumentEndpoints.DocumentNotFound(id);
        }

        BaseVersionHeader.SetETag(request.HttpContext.Response, restored);
        return Results.Ok(restored);
    }

    /// <summary>The structured diff between two revisions, in the shape of the <c>diff_documents</c> tool (<c>current</c> or a version id for each side).</summary>
    private static async Task<IResult> DiffRevisions(string id, HttpRequest request, IStore store, CancellationToken cancellationToken)
    {
        string from = request.Query["from"].FirstOrDefault() ?? string.Empty;
        string to = request.Query["to"].FirstOrDefault() ?? Current;
        if (string.IsNullOrWhiteSpace(from))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid query parameter", detail: "'from' is required: a version id, or 'current'. 'to' defaults to 'current'.");
        }

        if (await store.GetDocument(id, cancellationToken) is null)
        {
            return DocumentEndpoints.DocumentNotFound(id);
        }

        Document? older = await Resolve(store, id, from, cancellationToken);
        Document? newer = await Resolve(store, id, to, cancellationToken);
        if (older is null || newer is null)
        {
            string missing = older is null ? from : to;
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Version not found", detail: $"No version '{missing}' of document '{id}'.");
        }

        try
        {
            return Results.Ok(new DiffDocumentsResult(from, to, DocumentDiff.Compare(older, newer)));
        }
        catch (InvalidOperationException error)
        {
            // Two elements sharing an id: a diff matches by id, so it says so rather than guessing.
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Cannot compare", detail: error.Message);
        }
    }

    private static async Task<IResult> CritiqueStored(string id, IStore store, CancellationToken cancellationToken)
    {
        Document? document = await store.GetDocument(id, cancellationToken);
        return document is null ? DocumentEndpoints.DocumentNotFound(id) : Results.Ok(new CritiqueDocumentResult(CritiqueAnalyser.Analyse(document)));
    }

    private static async Task<IResult> CritiqueBody(HttpRequest request, CancellationToken cancellationToken)
    {
        JsonObject? body = await DocumentEndpoints.ReadBody(request, cancellationToken);
        if (body is null)
        {
            return DocumentEndpoints.MalformedBody();
        }

        if (!DocumentEndpoints.TryParseDocument(body, out Document? document, out IResult? problem))
        {
            return problem!;
        }

        return Results.Ok(new CritiqueDocumentResult(CritiqueAnalyser.Analyse(document)));
    }

    private static async Task<Document?> Resolve(IStore store, string id, string revision, CancellationToken cancellationToken)
    {
        if (string.Equals(revision, Current, StringComparison.Ordinal))
        {
            return await store.GetDocument(id, cancellationToken);
        }

        return (await store.GetVersion(id, revision, cancellationToken))?.Document;
    }
}
