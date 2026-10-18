using Draughtsman.Core.Auth;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// The bin (OP-02, docs/api.md "Bin"): the documents <c>DELETE /api/documents/{id}</c> moved aside, listed, restored or
/// removed for good. Deleting is recoverable until the retention period ends (<c>retention.binDays</c>) and the last step,
/// <c>DELETE /api/bin/{id}</c>, is deliberately a second, separate request.
/// <para>
/// Who can do what (the Editor rule, written down once here and in docs/api.md): an Admin sees and restores every binned
/// document. An Editor sees and restores the entries they are party to: documents they created, and documents they deleted
/// (so an Editor who removes a colleague's document can put it back, and the colleague can too). Permanently deleting is
/// narrower still: an Admin, or the person who created the document, and only over a cookie session, never an API token,
/// because nothing can undo it. An entry the caller may not see is reported as not found, not forbidden, so the bin does not
/// reveal what others deleted. Every decision uses the authenticated user row's id and role; the actor header, which only
/// annotates, never enters into it.
/// </para>
/// </summary>
public static class BinEndpoints
{
    public static IEndpointRouteBuilder MapBinEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/bin");
        group.MapGet("", ListBin).WithName("ListBin");
        group.MapPost("/{id}/restore", RestoreDocument).WithName("RestoreDocument");
        group.MapDelete("/{id}", PurgeDocument).WithName("PurgeDocument").RequireAuthorization(AuthPolicies.CookieOnly);
        return app;
    }

    /// <summary>One bin entry as the API sends it: who deleted it, when it goes for good, and what this caller may do with it.</summary>
    public sealed record BinEntry(
        string Id,
        string Title,
        Draughtsman.Core.Enums.DocumentType Type,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset DeletedAt,
        string? DeletedBy,
        DateTimeOffset? PurgesAt,
        bool CanPurge);

    public sealed record BinListing(IReadOnlyList<BinEntry> Items, int Total, int Offset, int Limit, int RetentionDays);

    /// <summary>Whether the caller may see and restore this entry: an Admin always, anyone else only if they created or deleted it.</summary>
    public static bool InvolvesCaller(HttpContext context, BinnedDocument entry)
    {
        string? userId = context.User.UserId();
        return context.User.IsAdmin()
            || (userId is not null && (entry.DeletedByUserId == userId || entry.CreatedByUserId == userId));
    }

    /// <summary>Whether the caller may remove this entry for good: an Admin, or the user who created the document.</summary>
    public static bool MayPurge(HttpContext context, BinnedDocument entry)
    {
        string? userId = context.User.UserId();
        return context.User.IsAdmin() || (userId is not null && entry.CreatedByUserId == userId);
    }

    private static async Task<IResult> ListBin(HttpContext context, HttpRequest request, IStore store, DraughtsmanConfig config, CancellationToken cancellationToken)
    {
        if (!DocumentEndpoints.TryReadInt(request, "offset", 0, 0, int.MaxValue, out int offset, out IResult? problem)
            || !DocumentEndpoints.TryReadInt(request, "limit", DocumentQuery.DefaultLimit, 1, DocumentQuery.MaxLimit, out int limit, out problem))
        {
            return problem!;
        }

        string? userId = context.User.UserId();
        if (!context.User.IsAdmin() && userId is null)
        {
            // No identity to be party to anything: nothing to show (never the whole bin).
            return Results.Ok(new BinListing([], 0, offset, limit, config.Retention.BinDays));
        }

        BinPage page = await store.ListBin(new BinQuery(context.User.IsAdmin() ? null : userId, offset, limit), cancellationToken);
        List<BinEntry> items = page.Items.Select(entry => ToEntry(context, entry, config)).ToList();
        return Results.Ok(new BinListing(items, page.Total, page.Offset, page.Limit, config.Retention.BinDays));
    }

    private static async Task<IResult> RestoreDocument(string id, HttpContext context, HttpRequest request, IStore store, SecurityEventLog securityEvents, CancellationToken cancellationToken)
    {
        if (!AuthenticatedActor.TryResolve(request, out Actor actor, out string? actorUserId, out IResult? problem))
        {
            return problem!;
        }

        BinnedDocument? entry = await store.GetBinnedDocument(id, cancellationToken);
        if (entry is null || !InvolvesCaller(context, entry))
        {
            return NotInBin(id);
        }

        if (await store.RestoreDocument(id, cancellationToken) != RestoreOutcome.Restored)
        {
            return NotInBin(id);
        }

        await store.AppendAudit(new AuditEntry(id, actor, AuditActions.Restored, DateTimeOffset.UtcNow, actorUserId), cancellationToken);
        await securityEvents.Record(context, SecurityEventTypes.DocumentRestored, targetLabel: entry.Title, detail: $"{id}: taken out of the bin");
        return Results.Ok(new { id = entry.Id, title = entry.Title, type = entry.Type });
    }

    private static async Task<IResult> PurgeDocument(string id, HttpContext context, HttpRequest request, IStore store, SecurityEventLog securityEvents, CancellationToken cancellationToken)
    {
        if (!AuthenticatedActor.TryResolve(request, out Actor actor, out string? actorUserId, out IResult? problem))
        {
            return problem!;
        }

        BinnedDocument? entry = await store.GetBinnedDocument(id, cancellationToken);
        if (entry is null || !InvolvesCaller(context, entry))
        {
            return NotInBin(id);
        }

        if (!MayPurge(context, entry))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Not yours to delete permanently",
                detail: "Only an administrator, or the person who created this document, can delete it permanently. You can restore it instead.");
        }

        // Guarded inside the store: a document restored a moment ago is not in the bin, so this reports it not found rather than destroying it.
        if (!await store.PurgeBinnedDocument(id, cancellationToken))
        {
            return NotInBin(id);
        }

        await store.AppendAudit(new AuditEntry(id, actor, AuditActions.Purged, DateTimeOffset.UtcNow, actorUserId), cancellationToken);
        await securityEvents.Record(context, SecurityEventTypes.DocumentPurged, targetLabel: entry.Title, detail: $"{id}: deleted permanently from the bin");
        return Results.NoContent();
    }

    private static BinEntry ToEntry(HttpContext context, BinnedDocument entry, DraughtsmanConfig config)
    {
        int days = config.Retention.BinDays;
        return new BinEntry(entry.Id, entry.Title, entry.Type, entry.CreatedAt, entry.UpdatedAt, entry.DeletedAt, entry.DeletedByLabel, days > 0 ? entry.DeletedAt.AddDays(days) : null, MayPurge(context, entry));
    }

    private static IResult NotInBin(string id)
    {
        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not in the bin", detail: $"No document with id '{id}' is in the bin.");
    }
}
