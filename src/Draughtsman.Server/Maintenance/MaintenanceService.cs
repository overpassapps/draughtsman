using Draughtsman.Core.Auth;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Configuration;
using Microsoft.Extensions.Hosting;

namespace Draughtsman.Server.Maintenance;

/// <summary>
/// The daily housekeeping pass (AU-15): prunes automatic checkpoints past <c>retention.checkpointsPerDocument</c> and
/// deletes sessions that ended more than <c>retention.expiredSessionDays</c> ago, and removes documents that have been in
/// the bin longer than <c>retention.binDays</c> (OP-02; each one recorded as an audit row and a security event, since it is a
/// person's work being destroyed). The first pass runs a couple of
/// minutes after start, so a server that is restarted often still does it, then once a day. A failure is logged and
/// the next pass tries again; housekeeping never stops the server.
/// </summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, DraughtsmanConfig config, TimeProvider clock, ILogger<MaintenanceService> logger) : BackgroundService
{
    public static readonly TimeSpan FirstPassDelay = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstPassDelay, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    logger.LogWarning(error, "Maintenance pass failed; it will be tried again at the next interval");
                }

                await Task.Delay(Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>One pass. Public so a test, or an operator tool, can run it without waiting for the timer.</summary>
    public async Task<MaintenanceResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = scopes.CreateScope();
        IStoreMaintenance maintenance = scope.ServiceProvider.GetRequiredService<IStoreMaintenance>();

        int checkpoints = await maintenance.PruneCheckpoints(config.Retention.CheckpointsPerDocument, cancellationToken);
        int sessions = 0;
        if (config.Retention.ExpiredSessionDays > 0)
        {
            sessions = await maintenance.PurgeSessions(clock.GetUtcNow().AddDays(-config.Retention.ExpiredSessionDays), cancellationToken);
        }

        // Reset links are short-lived credentials: once ended they are only a trace, kept as long as an ended session is.
        int resetLinks = 0;
        if (config.Retention.ExpiredSessionDays > 0)
        {
            resetLinks = await maintenance.PurgePasswordResetLinks(clock.GetUtcNow().AddDays(-config.Retention.ExpiredSessionDays), cancellationToken);
        }

        int binned = 0;
        if (config.Retention.BinDays > 0)
        {
            binned = await PurgeBinAsync(scope.ServiceProvider, maintenance, cancellationToken);
        }

        if (checkpoints > 0 || sessions > 0 || binned > 0 || resetLinks > 0)
        {
            logger.LogInformation("Maintenance removed {Checkpoints} old checkpoint(s), {Sessions} ended session(s), {ResetLinks} ended reset link(s) and {Binned} document(s) past the bin's retention", checkpoints, sessions, resetLinks, binned);
        }

        return new MaintenanceResult(checkpoints, sessions, binned, resetLinks);
    }

    private async Task<int> PurgeBinAsync(IServiceProvider services, IStoreMaintenance maintenance, CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        IReadOnlyList<BinnedDocument> purged = await maintenance.PurgeBin(now.AddDays(-config.Retention.BinDays), cancellationToken);
        if (purged.Count == 0)
        {
            return 0;
        }

        // The document is gone for good, so the trail that says so is what remains. A failure to write one is logged, never
        // allowed to undo or stop the pass: the documents are already removed.
        IStore store = services.GetRequiredService<IStore>();
        SecurityEventLog securityEvents = services.GetRequiredService<SecurityEventLog>();
        var system = new Actor(ActorKind.User, RetentionActor);
        foreach (BinnedDocument document in purged)
        {
            try
            {
                await store.AppendAudit(new AuditEntry(document.Id, system, AuditActions.Purged, now), cancellationToken);
                await securityEvents.Record(new SecurityEvent(
                    SecurityEventTypes.DocumentPurged,
                    now,
                    ActorLabel: RetentionActor,
                    TargetLabel: document.Title,
                    Method: SecurityEventMethods.Host,
                    Detail: $"{document.Id}: in the bin since {document.DeletedAt:u}, past the {config.Retention.BinDays}-day retention"));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Could not record the retention purge of document {DocumentId}", document.Id);
            }
        }

        return purged.Count;
    }

    /// <summary>The name the audit trail and the security log give the retention pass.</summary>
    public const string RetentionActor = "retention policy";
}

/// <summary>What one maintenance pass removed.</summary>
public readonly record struct MaintenanceResult(int CheckpointsRemoved, int SessionsRemoved, int BinnedDocumentsRemoved = 0, int ResetLinksRemoved = 0);
