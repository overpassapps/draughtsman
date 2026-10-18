using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Documents;
using Microsoft.Net.Http.Headers;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// The document REST API (docs/api.md). Bodies are validated against the embedded JSON Schema before they reach the
/// store; every error is Problem+JSON. Composition rules live here, not in the store: a create records an audit row,
/// a replace by an agent takes a checkpoint of the previous snapshot first, a delete moves the document to the bin
/// (<see cref="BinEndpoints"/> restores or purges it) and leaves its audit row and a security event. Versions
/// are named snapshots and pre-agent-edit checkpoints only; a human save is not one (spec section 7); the history
/// endpoints (name a snapshot, compare, restore, critique) are <see cref="DocumentHistoryEndpoints"/>.
/// <para>
/// Every read carries the stored document's version as an <c>ETag</c> and every save must quote it back in
/// <c>If-Match</c> (<see cref="BaseVersionHeader"/>). The rule is the same for both writers: a person saving from
/// the browser and an agent amending over the API are the same <c>PUT</c>, and a guard that held only one of them
/// would leave the hole it was built to close.
/// </para>
/// </summary>
public static class DocumentEndpoints
{
    public const string CheckpointVersionName = "checkpoint";

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/documents");
        group.MapGet("", ListDocuments).WithName("ListDocuments");
        group.MapPost("", CreateDocument).WithName("CreateDocument");
        group.MapGet("/{id}", GetDocument).WithName("GetDocument");
        group.MapPut("/{id}", ReplaceDocument).WithName("ReplaceDocument");
        group.MapDelete("/{id}", DeleteDocument).WithName("DeleteDocument");
        group.MapPost("/{id}/duplicate", DuplicateDocument).WithName("DuplicateDocument");
        group.MapPatch("/{id}", RenameDocument).WithName("RenameDocument");
        group.MapGet("/{id}/versions", ListVersions).WithName("ListVersions");
        group.MapGet("/{id}/versions/{versionId}", GetVersion).WithName("GetVersion");
        return app;
    }

    private static async Task<IResult> ListDocuments(HttpRequest request, IStore store, CancellationToken cancellationToken)
    {
        string? q = request.Query["q"].FirstOrDefault();
        if (!TryReadInt(request, "offset", 0, 0, int.MaxValue, out int offset, out IResult? problem)
            || !TryReadInt(request, "limit", DocumentQuery.DefaultLimit, 1, DocumentQuery.MaxLimit, out int limit, out problem)
            || !TryReadSort(request, out DocumentSort sort, out bool reverse, out DocumentType? type, out problem))
        {
            return problem!;
        }

        DocumentPage page = await store.ListDocuments(new DocumentQuery(q, offset, limit) { Sort = sort, Reverse = reverse, Type = type }, cancellationToken);
        return Results.Ok(page);
    }

    private static async Task<IResult> GetDocument(string id, HttpRequest request, HttpResponse response, IStore store, CancellationToken cancellationToken)
    {
        // An open editor polls with the version it has (live sync, docs/api.md): while that is still the stored
        // version the answer is 304 with no body, so watching a document for another writer's change stays cheap.
        // The version is the stored etag column (AU-16), so the poll never reads, parses or hashes the snapshot.
        if (request.Headers.ContainsKey(HeaderNames.IfNoneMatch))
        {
            string? tag = await store.GetDocumentTag(id, cancellationToken);
            if (tag is null)
            {
                return DocumentNotFound(id);
            }

            if (BaseVersionHeader.IsCurrent(request, tag))
            {
                BaseVersionHeader.SetETag(response, tag);
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
        }

        // The document and its tag in one read, so the ETag is the one of exactly the body sent.
        StoredDocument? stored = await store.GetStoredDocument(id, cancellationToken);
        if (stored is null)
        {
            return DocumentNotFound(id);
        }

        BaseVersionHeader.SetETag(response, stored.Version);
        return Results.Ok(stored.Document);
    }

    private static async Task<IResult> CreateDocument(HttpRequest request, IStore store, CancellationToken cancellationToken)
    {
        if (!AuthenticatedActor.TryResolve(request, out Actor actor, out string? actorUserId, out IResult? problem))
        {
            return problem!;
        }

        JsonObject? body = await ReadBody(request, cancellationToken);
        if (body is null)
        {
            return MalformedBody();
        }

        // The server assigns what the caller left out: id, schema version and the meta header.
        if (body["id"] is null || (body["id"] is JsonValue idValue && idValue.TryGetValue(out string? idText) && string.IsNullOrWhiteSpace(idText)))
        {
            body["id"] = StoreIds.NewDocumentId();
        }

        string now = UtcTimestampConverter.Format(DateTimeOffset.UtcNow);
        body["schemaVersion"] ??= Document.CurrentSchemaVersion;
        JsonObject meta = EnsureMetaObject(body);
        meta["createdAt"] ??= now;
        meta["updatedAt"] ??= now;
        // Who created it, and what an agent has done to it, are the server's to say, not the body's (AU-18): the audit table is
        // the evidence, and a body that could claim another creator or another agent's edits would make the header a forgery.
        meta["createdBy"] = actor.ToString();
        meta.Remove("aiProvenance");

        if (!TryParseDocument(body, out Document? document, out problem))
        {
            return problem!;
        }

        try
        {
            await store.CreateDocument(document, cancellationToken);
        }
        catch (DocumentExistsException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Document already exists", detail: $"A document with id '{document.Id}' already exists (it may be in the bin: restore it, or delete it permanently, from the bin). Leave the id out to have a new one assigned; a document imported from DSL text with no id line takes an id from its title, so import it with ?newId=true (POST /api/import/dsl) to get a fresh one.");
        }

        await store.AppendAudit(new AuditEntry(document.Id, actor, AuditActions.Created, DateTimeOffset.UtcNow, actorUserId), cancellationToken);
        BaseVersionHeader.SetETag(request.HttpContext.Response, document);
        return Results.Created($"/api/documents/{Uri.EscapeDataString(document.Id)}", document);
    }

    private static async Task<IResult> ReplaceDocument(string id, HttpRequest request, IStore store, CancellationToken cancellationToken)
    {
        if (!AuthenticatedActor.TryResolve(request, out Actor actor, out string? actorUserId, out IResult? problem)
            || !BaseVersionHeader.TryResolve(request, out string? baseTag, out problem))
        {
            return problem!;
        }

        JsonObject? body = await ReadBody(request, cancellationToken);
        if (body is null)
        {
            return MalformedBody();
        }

        if (body["id"] is null)
        {
            body["id"] = id;
        }
        else if (!(body["id"] is JsonValue idValue && idValue.TryGetValue(out string? bodyId) && bodyId == id))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Document id mismatch", detail: $"The body id must match the route id '{id}'.");
        }

        StoredDocument? stored = await store.GetStoredDocument(id, cancellationToken);
        if (stored is null)
        {
            return DocumentNotFound(id);
        }

        Document existing = stored.Document;

        // Refuse a save written from a base version that is no longer current, before anything is written or
        // checkpointed. What to do about it is the caller's: the refusal says what the stored version is and nothing more.
        string baseVersion = BaseVersionHeader.Unquote(baseTag);
        if (baseVersion != stored.Version)
        {
            return StaleSave(id, stored.Version, baseTag);
        }

        // updatedAt is always the server's clock: the snapshot is regenerated on every save and listings order by it.
        body["schemaVersion"] ??= Document.CurrentSchemaVersion;
        JsonObject meta = EnsureMetaObject(body);
        meta["createdAt"] ??= UtcTimestampConverter.Format(existing.Meta.CreatedAt);
        // Immutable through this route: the creator, and the record of agent edits, which only the server appends to.
        meta["createdBy"] = existing.Meta.CreatedBy;
        if (existing.Meta.AiProvenance is { Count: > 0 } provenance)
        {
            meta["aiProvenance"] = JsonSerializer.SerializeToNode(provenance, DocumentJson.Options);
        }
        else
        {
            meta.Remove("aiProvenance");
        }
        meta["updatedAt"] = UtcTimestampConverter.Format(DateTimeOffset.UtcNow);

        if (!TryParseDocument(body, out Document? document, out problem))
        {
            return problem!;
        }

        // Checkpoint the snapshot being replaced, then replace. Not one transaction: the store composes nothing (IStore).
        try
        {
            if (actor.Kind == ActorKind.Agent)
            {
                await store.CreateVersion(id, CheckpointVersionName, actor, $"Before replace by {actor}", cancellationToken);
            }

            // The store re-checks the base version inside the UPDATE, so a write that lands between the check above
            // and this call takes the row out of the statement rather than being overwritten by it.
            ReplaceOutcome outcome = await store.ReplaceDocument(document, baseVersion, cancellationToken);
            if (outcome == ReplaceOutcome.NotFound)
            {
                return DocumentNotFound(id);
            }

            if (outcome == ReplaceOutcome.VersionMismatch)
            {
                // A save that lost the race here still wrote nothing. An agent's checkpoint was taken before the
                // attempt and stays: one more restore point of a snapshot that really existed, not lost work.
                string? moved = await store.GetDocumentTag(id, cancellationToken);
                return moved is null ? DocumentNotFound(id) : StaleSave(id, moved, baseTag);
            }

            await store.AppendAudit(new AuditEntry(id, actor, AuditActions.Replaced, DateTimeOffset.UtcNow, actorUserId), cancellationToken);
        }
        catch (DocumentNotFoundException)
        {
            return DocumentNotFound(id);
        }

        BaseVersionHeader.SetETag(request.HttpContext.Response, document);
        return Results.Ok(document);
    }

    /// <summary>
    /// Moves the document to the bin (OP-02): nothing is destroyed, so the verb is as cheap as it was and an accidental one is
    /// recoverable. Any signed-in writer may do it (the organisation-wide trust model: an Editor can already change any
    /// document), and the bin entry remembers who, so the document's creator finds it in their own bin. The permanent step
    /// is <c>DELETE /api/bin/{id}</c>.
    /// </summary>
    private static async Task<IResult> DeleteDocument(string id, HttpRequest request, IStore store, SecurityEventLog securityEvents, CancellationToken cancellationToken)
    {
        if (!AuthenticatedActor.TryResolve(request, out Actor actor, out string? actorUserId, out IResult? problem))
        {
            return problem!;
        }

        // The title is read first only so the security event can name what was deleted; the delete itself is the one guarded statement.
        Document? document = await store.GetDocument(id, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (document is null || !await store.SoftDeleteDocument(id, now, actorUserId, actor.Name, cancellationToken))
        {
            return DocumentNotFound(id);
        }

        await store.AppendAudit(new AuditEntry(id, actor, AuditActions.Deleted, now, actorUserId), cancellationToken);
        await securityEvents.Record(request.HttpContext, SecurityEventTypes.DocumentDeleted, targetLabel: document.Title, detail: $"{id}: moved to the bin");
        return Results.NoContent();
    }

    /// <summary>
    /// Stores a copy of a document under a new id (PR-06): same type, content and theme, a title that says it is a copy
    /// (or the <c>title</c> in the optional body), and a fresh header with the caller as creator. The copy's history starts
    /// empty: the original's versions belong to the original. Images are shared by reference, which is safe because assets
    /// are content-addressed and never deleted.
    /// </summary>
    private static async Task<IResult> DuplicateDocument(string id, HttpRequest request, IStore store, CancellationToken cancellationToken)
    {
        if (!AuthenticatedActor.TryResolve(request, out Actor actor, out string? actorUserId, out IResult? problem))
        {
            return problem!;
        }

        string? requested = null;
        if (request.ContentLength is > 0 || request.Headers.ContainsKey(HeaderNames.TransferEncoding))
        {
            JsonObject? body = await ReadBody(request, cancellationToken);
            if (body is null)
            {
                return MalformedBody();
            }

            if (body["title"] is not null && !(body["title"] is JsonValue { } titleValue && titleValue.TryGetValue(out requested) && !string.IsNullOrWhiteSpace(requested)))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "\"title\", when given, must be a non-empty string.");
            }
        }

        Document? original = await store.GetDocument(id, cancellationToken);
        if (original is null)
        {
            return DocumentNotFound(id);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Document copy = original with
        {
            Id = StoreIds.NewDocumentId(),
            Title = requested?.Trim() ?? CopyTitle(original.Title),
            Meta = new Meta(now, now, actor.ToString()) { AiProvenance = original.Meta.AiProvenance },
        };
        await store.CreateDocument(copy, cancellationToken);
        await store.AppendAudit(new AuditEntry(copy.Id, actor, AuditActions.Created, now, actorUserId), cancellationToken);
        BaseVersionHeader.SetETag(request.HttpContext.Response, copy);
        return Results.Created($"/api/documents/{Uri.EscapeDataString(copy.Id)}", copy);
    }

    /// <summary>"Order service" becomes "Order service (copy)"; a copy of a copy says so once, not twice.</summary>
    public static string CopyTitle(string title)
    {
        const string Suffix = " (copy)";
        return title.EndsWith(Suffix, StringComparison.Ordinal) ? title : title + Suffix;
    }

    private static async Task<IResult> RenameDocument(string id, HttpRequest request, IStore store, CancellationToken cancellationToken)
    {
        if (!AuthenticatedActor.TryResolve(request, out Actor actor, out string? actorUserId, out IResult? problem)
            || !BaseVersionHeader.TryResolve(request, out string? baseTag, out problem))
        {
            return problem!;
        }

        JsonObject? body = await ReadBody(request, cancellationToken);
        if (body is null || body["title"] is not JsonValue { } titleValue || !titleValue.TryGetValue(out string? title) || string.IsNullOrWhiteSpace(title))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The body must be a JSON object with a non-empty \"title\" string.");
        }

        string? current = await store.GetDocumentTag(id, cancellationToken);
        if (current is null)
        {
            return DocumentNotFound(id);
        }

        if (BaseVersionHeader.Unquote(baseTag) != current)
        {
            return StaleSave(id, current, baseTag);
        }

        try
        {
            RenameOutcome outcome = await store.RenameDocument(id, title, current, cancellationToken);
            if (outcome == RenameOutcome.NotFound)
            {
                return DocumentNotFound(id);
            }

            if (outcome == RenameOutcome.VersionMismatch)
            {
                string? moved = await store.GetDocumentTag(id, cancellationToken);
                return moved is null ? DocumentNotFound(id) : StaleSave(id, moved, baseTag);
            }
        }
        catch (DocumentNotFoundException)
        {
            return DocumentNotFound(id);
        }

        await store.AppendAudit(new AuditEntry(id, actor, AuditActions.Renamed, DateTimeOffset.UtcNow, actorUserId), cancellationToken);
        StoredDocument? renamed = await store.GetStoredDocument(id, cancellationToken);
        if (renamed is null)
        {
            return DocumentNotFound(id);
        }

        BaseVersionHeader.SetETag(request.HttpContext.Response, renamed.Version);
        return Results.Ok(renamed.Document);
    }

    private static bool TryReadSort(HttpRequest request, out DocumentSort sort, out bool reverse, out DocumentType? type, [NotNullWhen(false)] out IResult? problem)
    {
        sort = DocumentSort.Modified;
        reverse = false;
        type = null;
        problem = null;
        string? sortText = request.Query["sort"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(sortText) && !Enum.TryParse(sortText, ignoreCase: true, out sort))
        {
            problem = Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid query parameter", detail: $"'sort' must be one of modified, created, title or type; got '{sortText}'.");
            return false;
        }

        // "dir" is the direction as the reader sees it: asc is A to Z or oldest first, desc is Z to A or newest first.
        string? dir = request.Query["dir"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(dir))
        {
            bool newestOrZFirst;
            if (string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase))
            {
                newestOrZFirst = true;
            }
            else if (string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase))
            {
                newestOrZFirst = false;
            }
            else
            {
                problem = Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid query parameter", detail: $"'dir' must be asc or desc; got '{dir}'.");
                return false;
            }

            bool naturallyDescending = sort is DocumentSort.Modified or DocumentSort.Created;
            reverse = newestOrZFirst != naturallyDescending;
        }

        string? typeText = request.Query["type"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(typeText))
        {
            if (!Enum.TryParse(typeText, ignoreCase: true, out DocumentType parsed) || !Enum.IsDefined(parsed))
            {
                problem = Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid query parameter", detail: $"'type' must be flowchart, architecture, freeform or sequence; got '{typeText}'.");
                return false;
            }

            type = parsed;
        }

        return true;
    }

    private static async Task<IResult> ListVersions(string id, HttpRequest request, IStore store, CancellationToken cancellationToken)
    {
        if (await store.GetDocument(id, cancellationToken) is null)
        {
            return DocumentNotFound(id);
        }

        if (!TryReadInt(request, "offset", 0, 0, int.MaxValue, out int offset, out IResult? problem)
            || !TryReadInt(request, "limit", VersionQuery.DefaultLimit, 1, VersionQuery.MaxLimit, out int limit, out problem))
        {
            return problem!;
        }

        VersionPage page = await store.ListVersions(id, new VersionQuery(offset, limit), cancellationToken);
        return Results.Ok(page);
    }

    private static async Task<IResult> GetVersion(string id, string versionId, IStore store, CancellationToken cancellationToken)
    {
        DocumentVersion? version = await store.GetVersion(id, versionId, cancellationToken);
        return version is null
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Version not found", detail: $"No version '{versionId}' of document '{id}'.")
            : Results.Ok(version);
    }

    /// <summary>Reads the body as a JSON object. Null for an empty body, malformed JSON, or a non-object root.</summary>
    internal static async Task<JsonObject?> ReadBody(HttpRequest request, CancellationToken cancellationToken)
    {
        try
        {
            JsonNode? node = await JsonNode.ParseAsync(request.Body, cancellationToken: cancellationToken);
            return node as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static JsonObject EnsureMetaObject(JsonObject body)
    {
        if (body["meta"] is JsonObject meta)
        {
            return meta;
        }

        // A non-object meta is left for the schema to reject; only an absent one is filled in.
        if (body["meta"] is not null)
        {
            return [];
        }

        var created = new JsonObject();
        body["meta"] = created;
        return created;
    }

    internal static bool TryParseDocument(JsonObject body, [NotNullWhen(true)] out Document? document, [NotNullWhen(false)] out IResult? problem)
    {
        document = null;
        string json = body.ToJsonString();
        IReadOnlyList<string> errors = DocumentSchema.Validate(json);
        if (errors.Count > 0)
        {
            problem = ValidationProblem(errors);
            return false;
        }

        try
        {
            document = DocumentJson.Deserialize(json);
        }
        catch (JsonException error)
        {
            problem = ValidationProblem([error.Message]);
            return false;
        }

        problem = null;
        return true;
    }

    internal static IResult ValidationProblem(IReadOnlyList<string> errors)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Document is not valid",
            detail: "The body does not conform to the document schema.",
            extensions: new Dictionary<string, object?> { ["errors"] = errors });
    }

    internal static IResult MalformedBody()
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The body must be a JSON object.");
    }

    /// <summary>
    /// A save whose base version is no longer the stored one. It names both versions, so a caller can tell a stale
    /// save from a bad request and knows which version it has to reach; what to do next is the caller's to decide.
    /// </summary>
    internal static IResult StaleSave(string id, string storedVersion, string provided)
    {
        string expected = BaseVersionHeader.ETagOf(storedVersion);
        return Results.Problem(
            statusCode: StatusCodes.Status412PreconditionFailed,
            title: "Document has changed",
            detail: $"This save was made from version {provided}, but document '{id}' is now at {expected}; it was written to in between. Nothing was written. GET /api/documents/{id} returns the current version.",
            extensions: new Dictionary<string, object?>
            {
                ["expectedVersion"] = expected,
                ["providedVersion"] = provided,
            });
    }

    internal static IResult DocumentNotFound(string id)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Document not found", detail: $"No document with id '{id}'.");
    }

    internal static bool TryReadInt(HttpRequest request, string name, int fallback, int min, int max, out int value, out IResult? problem)
    {
        problem = null;
        string? text = request.Query[name].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(text))
        {
            value = fallback;
            return true;
        }

        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value) || value < min || value > max)
        {
            problem = Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid query parameter", detail: $"'{name}' must be an integer between {min} and {max}; got '{text}'.");
            return false;
        }

        return true;
    }
}
