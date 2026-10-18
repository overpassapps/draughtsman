using System.Globalization;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Storage;

namespace Draughtsman.Server.Auth.Endpoints;

/// <summary>
/// The admin's read side of the two audit trails (AU-04): instance-level security events (sign-ins, password and role
/// changes, tokens, throttle trips) and a document's own audit rows. Both are <see cref="AuthPolicies.CookieAdminOnly"/>:
/// a leaked admin API token must not be able to read who signed in when, let alone tidy its own tracks (nothing here
/// writes or deletes). Read-only by construction: there is no edit or delete endpoint for either trail.
/// </summary>
public static class SecurityEventEndpoints
{
    public static IEndpointRouteBuilder MapSecurityEventEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/admin").RequireAuthorization(AuthPolicies.CookieAdminOnly);
        group.MapGet("/security-events", ListSecurityEvents).WithName("ListSecurityEvents");
        group.MapGet("/audit", ListDocumentAudit).WithName("ListDocumentAudit");
        return app;
    }

    private static async Task<IResult> ListSecurityEvents(HttpRequest request, ISecurityEventStore events, CancellationToken cancellationToken)
    {
        if (!TryReadInt(request, "offset", 0, 0, int.MaxValue, out int offset, out IResult? problem)
            || !TryReadInt(request, "limit", 50, 1, 200, out int limit, out problem)
            || !TryReadDate(request, "since", out DateTimeOffset? since, out problem)
            || !TryReadDate(request, "until", out DateTimeOffset? until, out problem))
        {
            return problem!;
        }

        string[] types = (request.Query["type"].FirstOrDefault() ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? user = request.Query["user"].FirstOrDefault();
        string? ip = request.Query["ip"].FirstOrDefault();

        SecurityEventPage page = await events.List(
            new SecurityEventQuery(types.Length == 0 ? null : types, string.IsNullOrWhiteSpace(user) ? null : user.Trim(), string.IsNullOrWhiteSpace(ip) ? null : ip.Trim(), since, until, offset, limit),
            cancellationToken);
        return Results.Ok(new { items = page.Items.Select(SecurityEventResponse.From), total = page.Total, offset, limit });
    }

    /// <summary>One document's audit rows, newest first, through the existing <see cref="IStore.ListAudit"/> (which survives the document's deletion).</summary>
    private static async Task<IResult> ListDocumentAudit(HttpRequest request, IStore store, CancellationToken cancellationToken)
    {
        string? documentId = request.Query["documentId"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(documentId))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid query parameter", detail: "'documentId' is required.");
        }

        if (!TryReadInt(request, "offset", 0, 0, int.MaxValue, out int offset, out IResult? problem)
            || !TryReadInt(request, "limit", 50, 1, 200, out int limit, out problem))
        {
            return problem!;
        }

        IReadOnlyList<AuditEntry> all = await store.ListAudit(documentId.Trim(), cancellationToken);
        var items = all
            .OrderByDescending(a => a.At)
            .ThenByDescending(a => a.Id)
            .Skip(offset)
            .Take(limit)
            .Select(a => new
            {
                id = a.Id,
                documentId = a.DocumentId,
                actor = a.Actor.Name,
                actorKind = a.Actor.Kind.ToString().ToLowerInvariant(),
                model = a.Actor.Model,
                action = a.Action,
                at = a.At,
                actorUserId = a.ActorUserId,
            })
            .ToList();
        return Results.Ok(new { items, total = all.Count, offset, limit });
    }

    private static bool TryReadInt(HttpRequest request, string name, int fallback, int min, int max, out int value, out IResult? problem)
    {
        problem = null;
        string? text = request.Query[name].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(text))
        {
            value = fallback;
            return true;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < min || value > max)
        {
            problem = Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid query parameter", detail: $"'{name}' must be an integer between {min} and {max}; got '{text}'.");
            return false;
        }

        return true;
    }

    private static bool TryReadDate(HttpRequest request, string name, out DateTimeOffset? value, out IResult? problem)
    {
        problem = null;
        value = null;
        string? text = request.Query[name].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
        {
            problem = Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid query parameter", detail: $"'{name}' must be an ISO 8601 date or time; got '{text}'.");
            return false;
        }

        value = parsed;
        return true;
    }
}
