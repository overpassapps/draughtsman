using Draughtsman.Core.Storage;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Ai;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Diagnostics;
using Draughtsman.Server.Hosting;
using Draughtsman.Sidecar;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// The admin status page's data (design/atlas/help-about-status.md, PR-05): the components whose failure the product
/// otherwise survives quietly (a sidecar that did not start leaves layout broken but editing working; a PDF component
/// that is off or absent hides one menu entry). Admin only, on a cookie session like the other instance-wide admin
/// reads, because it carries host detail: the data folder, the sidecar's paths and error text, the storage file.
/// Read-only. Never a connection string, key or token.
/// </summary>
public static class AdminStatusEndpoints
{
    public static IEndpointRouteBuilder MapAdminStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/status", async (HttpContext http, SelfCheckService selfCheck, IStore store, ISidecarClient sidecar, IPdfRenderer pdf, AiStatus ai, BuildInfo build, DataFolder data, DraughtsmanConfig config, CancellationToken cancellationToken) =>
            {
                SidecarStatus status = sidecar.Status;
                HostKind host = HostKinds.Detect();
                bool sqlite = string.Equals(store.ProviderName, "sqlite", StringComparison.OrdinalIgnoreCase);
                ChromiumConfig chromium = config.Export.Chromium;
                // The same probe /api/health reports from, never configuration alone: a browser that is enabled but will not
                // launch is not "OK" here while health says the component is absent.
                PdfStatus pdfStatus = await pdf.ProbeAsync(cancellationToken);
                ConnectionStatusResponse connection = await ConnectionStatus(http, selfCheck, cancellationToken);
                return Results.Ok(new AdminStatusResponse(
                    build.Version,
                    build.BuildDate.ToString("yyyy-MM-dd"),
                    data.Path,
                    HostKinds.Name(host),
                    HostKinds.LogHint(host, data.Path),
                    new StorageStatusResponse(store.ProviderName, sqlite ? Path.Combine(data.Path, "data.db") : null),
                    new SidecarStatusResponse(SidecarName(status.State), status.Version, status.NodeVersion, status.Restarts, status.Error, status.NodePath, status.ScriptPath),
                    new PdfStatusResponse(chromium.Enabled, pdfStatus.Available, string.IsNullOrWhiteSpace(chromium.Path) ? null : chromium.Path, pdfStatus.Available ? null : pdfStatus.Hint, pdfStatus.Available ? null : pdfStatus.Problem),
                    new AiStatusResponse(ai.Enabled, ai.Enabled ? ai.Provider : null, ai.Enabled ? ai.Model : null),
                    connection));
            })
            .WithName("GetAdminStatus")
            .RequireAuthorization(AuthPolicies.CookieAdminOnly);
        return app;
    }

    /// <summary>The connection self-check in brief: who is talking to the server, whether the proxy is believed, and how many findings came out each way. The full findings are at /api/admin/selfcheck.</summary>
    private static async Task<ConnectionStatusResponse> ConnectionStatus(HttpContext http, SelfCheckService selfCheck, CancellationToken cancellationToken)
    {
        ConnectionInputs inputs = selfCheck.Inputs(http, pageScheme: null);
        IReadOnlyList<DiagnosticItem> items = await selfCheck.RunAsync(http, pageScheme: null, cancellationToken);
        return new ConnectionStatusResponse(
            inputs.Snapshot.Peer?.ToString() ?? "unknown",
            inputs.Snapshot.PeerTrusted,
            inputs.Snapshot.HasForwardedHeaders,
            inputs.FinalScheme,
            inputs.ThrottleAddress?.ToString() ?? "unknown",
            SelfCheckSummary.Of(items),
            [.. items.Where(i => i.Status is DiagnosticStatus.Warn or DiagnosticStatus.Fail).Select(i => i.Title)]);
    }

    private static string SidecarName(SidecarState state)
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

/// <summary>Body of GET /api/admin/status. <c>Host</c> is console | container | systemd | launchd | windowsService.</summary>
public sealed record AdminStatusResponse(string Version, string BuildDate, string DataFolder, string Host, string LogHint, StorageStatusResponse Storage, SidecarStatusResponse Sidecar, PdfStatusResponse Pdf, AiStatusResponse Ai, ConnectionStatusResponse? Connection = null);

/// <summary>
/// The connection check in a line (docs/api.md "## About and status"): the direct peer address, whether it is a proxy the server believes, whether
/// the request carried proxy headers, the scheme the server believes, the address the sign-in throttle uses, and how many findings passed, warned and failed;
/// <c>Problems</c> names the ones that did not pass. The browser round trip is not part of it (only the page can run that).
/// </summary>
public sealed record ConnectionStatusResponse(string Peer, bool PeerTrusted, bool ForwardedHeadersSeen, string Scheme, string ThrottleAddress, SelfCheckSummary Checks, IReadOnlyList<string> Problems);

/// <summary>The provider and, for SQLite, the database file. A Postgres connection string is never reported.</summary>
public sealed record StorageStatusResponse(string Provider, string? File);

/// <summary>The supervisor's view of the layout service: running | starting | unavailable | stopped, plus what it reported.</summary>
public sealed record SidecarStatusResponse(string Status, string? Version, string? Node, int Restarts, string? Error, string? NodePath, string? ScriptPath);

/// <summary>PDF export: whether the admin turned it on, whether a browser launch has just worked (the health probe), the configured browser path if any, and while it is unavailable how to fix it and which part is the problem (<c>disabled</c> | <c>driver</c> | <c>browser</c>).</summary>
public sealed record PdfStatusResponse(bool Enabled, bool Available, string? Path, string? Hint = null, string? Problem = null);

public sealed record AiStatusResponse(bool Enabled, string? Provider, string? Model);
