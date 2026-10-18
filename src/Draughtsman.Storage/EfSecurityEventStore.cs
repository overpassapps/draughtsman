using Draughtsman.Core.Auth;
using Draughtsman.Storage.Entities;
using Microsoft.EntityFrameworkCore;

namespace Draughtsman.Storage;

/// <summary><see cref="ISecurityEventStore"/> over EF Core, sharing <see cref="DraughtsmanDbContext"/> with the other stores. Append-only.</summary>
public sealed class EfSecurityEventStore(DraughtsmanDbContext db) : ISecurityEventStore
{
    private readonly DraughtsmanDbContext _db = db;

    public async Task<SecurityEvent> Append(SecurityEvent securityEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(securityEvent.Type);

        var row = new SecurityEventRow
        {
            Type = securityEvent.Type,
            At = EfStore.ToUtc(securityEvent.At),
            ActorUserId = securityEvent.ActorUserId,
            ActorLabel = Clip(securityEvent.ActorLabel, 256),
            TargetUserId = securityEvent.TargetUserId,
            TargetLabel = Clip(securityEvent.TargetLabel, 256),
            SourceIp = Clip(securityEvent.SourceIp, 64),
            Method = securityEvent.Method,
            Detail = Clip(securityEvent.Detail, 512),
        };
        _db.SecurityEvents.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        _db.Entry(row).State = EntityState.Detached;
        return ToEvent(row);
    }

    public async Task<SecurityEventPage> List(SecurityEventQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        IQueryable<SecurityEventRow> rows = _db.SecurityEvents.AsNoTracking();
        if (query.Types is { Count: > 0 } types)
        {
            string[] wanted = [.. types];
            rows = rows.Where(e => wanted.Contains(e.Type));
        }

        if (!string.IsNullOrEmpty(query.UserId))
        {
            string userId = query.UserId;
            rows = rows.Where(e => e.ActorUserId == userId || e.TargetUserId == userId);
        }

        if (!string.IsNullOrEmpty(query.SourceIp))
        {
            string ip = query.SourceIp;
            rows = rows.Where(e => e.SourceIp == ip);
        }

        if (query.Since is DateTimeOffset since)
        {
            DateTime sinceUtc = EfStore.ToUtc(since);
            rows = rows.Where(e => e.At >= sinceUtc);
        }

        if (query.Until is DateTimeOffset until)
        {
            DateTime untilUtc = EfStore.ToUtc(until);
            rows = rows.Where(e => e.At < untilUtc);
        }

        int total = await rows.CountAsync(cancellationToken);
        List<SecurityEventRow> page = await rows
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Id)
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Clamp(query.Limit, 1, 200))
            .ToListAsync(cancellationToken);
        return new SecurityEventPage(page.Select(ToEvent).ToList(), total);
    }

    private static string? Clip(string? value, int max)
    {
        return value is null || value.Length <= max ? value : value[..max];
    }

    private static SecurityEvent ToEvent(SecurityEventRow row)
    {
        return new SecurityEvent(row.Type, EfStore.FromUtc(row.At), row.ActorUserId, row.ActorLabel, row.TargetUserId, row.TargetLabel, row.SourceIp, row.Method, row.Detail)
        {
            Id = row.Id,
        };
    }
}
