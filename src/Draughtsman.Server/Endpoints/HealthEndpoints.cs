using System.Reflection;
using System.Security.Claims;
using Draughtsman.Core.Storage;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Ai;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Maintenance;
using Draughtsman.Sidecar;

namespace Draughtsman.Server.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", async (ClaimsPrincipal user, IStore store, ISidecarClient sidecar, IPdfRenderer pdf, AiStatus ai, StorageWriteHealth storageWrite, CancellationToken cancellationToken) =>
            {
                SidecarStatus status = sidecar.Status;
                // Degraded, not down: editing and storage work without the sidecar; layout and snapshots wait for it. A database that opens and
                // reads but cannot be written is degraded too, whatever the sidecar says: nothing can be saved, and a monitor must be told.
                StoreWriteProbe write = await storageWrite.CurrentAsync(cancellationToken);
                string overall = status.State == SidecarState.Running && write.Writable ? "ok" : "degraded";

                // AU-19: this route is anonymous (a load balancer or a monitor probes it), so a caller with no session gets the
                // status and nothing a scanner could use. A signed-in user gets what the editor gates its menus on; an
                // administrator gets the rest (version, storage provider, Node version, restarts, AI provider name).
                if (user.Identity?.IsAuthenticated != true)
                {
                    return Results.Ok(new HealthResponse(overall));
                }

                // PDF is reported from a probe that has actually launched the browser (cached briefly), not from configuration
                // alone. Only a signed-in caller can make it launch one: an anonymous probe must not start a Chromium.
                PdfStatus pdfStatus = await pdf.ProbeAsync(cancellationToken);
                var capabilities = new HealthResponse(overall, Export: new ExportHealthResponse(pdfStatus.Available), Ai: new AiHealthResponse(ai.Enabled));
                if (!user.IsAdmin())
                {
                    return Results.Ok(capabilities);
                }

                var sidecarHealth = new SidecarHealthResponse(StatusName(status.State), status.Version, status.NodeVersion, status.Restarts);
                return Results.Ok(capabilities with
                {
                    Version = ProductVersion,
                    Storage = store.ProviderName,
                    StorageWritable = write.Writable,
                    StorageProblem = write.Problem,
                    Sidecar = sidecarHealth,
                    // The hint names the install command and, where one is configured, the executable path: administrators only.
                    Export = new ExportHealthResponse(pdfStatus.Available, pdfStatus.Hint, pdfStatus.Problem),
                    Ai = new AiHealthResponse(ai.Enabled, ai.Enabled ? ai.Provider : null),
                });
            })
            .WithName("GetHealth")
            .AllowAnonymous();
        return app;
    }

    /// <summary>Informational version from the assembly (Version in Directory.Build.props), without the source hash suffix.</summary>
    public static string ProductVersion
    {
        get
        {
            string? informational = typeof(HealthEndpoints).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(informational))
            {
                return typeof(HealthEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            }

            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }
    }

    private static string StatusName(SidecarState state)
    {
        return state switch
        {
            SidecarState.Running => "running",
            SidecarState.Starting => "starting",
            SidecarState.Stopped => "stopped",
            _ => "unavailable",
        };
    }
}

/// <summary>
/// Body of GET /api/health. Status is ok when every component is up, degraded when the sidecar is not or the database cannot be written to (administrators
/// also get <c>StorageWritable</c> and, when it is false, <c>StorageProblem</c>: the database's own words, which name no path). Everything else is
/// left out (not null, omitted) unless the caller may see it: <c>Export</c> and <c>Ai.Enabled</c> for a signed-in user,
/// the rest for an administrator (docs/api.md).
/// </summary>
public sealed record HealthResponse(
    string Status,
    string? Version = null,
    string? Storage = null,
    bool? StorageWritable = null,
    string? StorageProblem = null,
    SidecarHealthResponse? Sidecar = null,
    ExportHealthResponse? Export = null,
    AiHealthResponse? Ai = null);

/// <summary>
/// Capabilities the web app gates its menu on: <c>Pdf</c> is true only when a Chromium launch has succeeded, never on
/// configuration alone. <c>PdfHint</c> says how to fix it while it is false and <c>PdfProblem</c> (<c>disabled</c> | <c>driver</c> |
/// <c>browser</c>, <see cref="PdfProblems"/>) says which part, so a missing driver is never answered with "install Chromium".
/// Both are for administrators only (the hint names paths).
/// </summary>
public sealed record ExportHealthResponse(bool Pdf, string? PdfHint = null, string? PdfProblem = null);

/// <summary>The AI panel (design/atlas/ai-panel.md) is hidden entirely unless Enabled; Provider is null while off.</summary>
public sealed record AiHealthResponse(bool Enabled, string? Provider = null);

/// <summary>
/// The sidecar as seen by the supervisor: running | starting | unavailable | stopped, plus what it reported.
/// Why it is unavailable is in the server log only; this endpoint is unauthenticated.
/// </summary>
public sealed record SidecarHealthResponse(string Status, string? Version, string? Node, int Restarts);
