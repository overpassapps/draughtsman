using System.Net;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Hardening;
using Draughtsman.Server.Hosting;
using Draughtsman.Storage;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Draughtsman.Server.Diagnostics;

/// <summary>Body of <c>GET /api/admin/selfcheck</c>: the findings only the server can make, and how many came out each way.</summary>
public sealed record SelfCheckResponse(DateTimeOffset GeneratedAt, SelfCheckSummary Summary, IReadOnlyList<DiagnosticItem> Items);

public sealed record SelfCheckSummary(int Pass, int Warn, int Fail)
{
    public static SelfCheckSummary Of(IEnumerable<DiagnosticItem> items)
    {
        DiagnosticItem[] all = [.. items];
        return new SelfCheckSummary(
            all.Count(i => i.Status is DiagnosticStatus.Pass or DiagnosticStatus.Info),
            all.Count(i => i.Status == DiagnosticStatus.Warn),
            all.Count(i => i.Status == DiagnosticStatus.Fail));
    }
}

/// <summary>Body of <c>GET /api/admin/selfcheck/echo</c>, which the page calls twice (step 1 sets a test cookie, step 2 reads it back and clears it).</summary>
public sealed record SelfCheckEcho(int Step, string Tag, bool CookieReceived, bool CookieWasSecure, string Scheme, DateTimeOffset ServerTime);

/// <summary>
/// The admin Connection check (design: <c>docs/troubleshooting.md</c>, "Troubleshooting an install"). Two routes, both cookie-session
/// administrators only like <c>/api/admin/status</c>, and neither shows anything secret:
/// <list type="bullet">
/// <item><c>GET /api/admin/selfcheck</c>: what the server can see of the request (the proxy headers, the peer, the scheme it believes, the address the sign-in throttle will
/// use, the Host header) plus the data folder, the disk and what this server is. Pass, warn or fail with the exact fix for nginx, Caddy, Cloudflare and draughtsman.yaml.</item>
/// <item><c>GET /api/admin/selfcheck/echo</c>: a round trip for the PAGE to run, because only a browser can see what a proxy did to a response.
/// It sets a test cookie with the attributes the session cookie has, and answers with an ETag and the same tag in its body; called a second
/// time it says whether the browser sent the cookie back. The page then knows whether the cookie survives, whether an ETag survives a CDN
/// (a weakened or stripped one is the cause of issue 63's failed saves) and whether anything compressed the response on the way.</item>
/// </list>
/// The echo is excluded from the server's own response compression (<c>ResponseCompressionStartupFilter</c>), so a <c>content-encoding</c> the browser
/// sees on it was added in transit and not by this process.
/// </summary>
public static class SelfCheckEndpoints
{
    public const string EchoPath = "/api/admin/selfcheck/echo";

    private const string CookieName = "ds_selfcheck";

    private const string HostCookieName = "__Host-ds_selfcheck";

    public static IEndpointRouteBuilder MapSelfCheckEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/selfcheck", async (HttpContext http, SelfCheckService service, string? pageScheme, CancellationToken cancellationToken) =>
            {
                IReadOnlyList<DiagnosticItem> items = await service.RunAsync(http, pageScheme, cancellationToken);
                return Results.Ok(new SelfCheckResponse(DateTimeOffset.UtcNow, SelfCheckSummary.Of(items), items));
            })
            .WithName("GetSelfCheck")
            .RequireAuthorization(AuthPolicies.CookieAdminOnly);

        app.MapGet(EchoPath, (HttpContext http, TimeProvider time, int? step) =>
            {
                HttpRequest request = http.Request;
                HttpResponse response = http.Response;
                bool secure = request.IsHttps;
                string name = secure ? HostCookieName : CookieName;
                int which = step == 2 ? 2 : 1;
                bool received = false;
                string tag = "sc-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
                if (which == 1)
                {
                    response.Cookies.Append(name, Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), SessionCookie.Options(request, time.GetUtcNow() + SessionCookie.Lifetime));
                }
                else
                {
                    received = request.Cookies.TryGetValue(name, out string? value) && !string.IsNullOrEmpty(value);
                    // The test cookie has done its job either way: clear it, so nothing is left behind in the browser.
                    response.Cookies.Delete(name, SessionCookie.DeleteOptions(request));
                }

                // No Cache-Control here: the security-headers middleware gives every /api response the policy a document response has (no-store, no-transform), and the
                // echo must travel exactly as a document does for a proxy that ignores it to show.
                response.Headers.ETag = "\"" + tag + "\"";
                return Results.Ok(new SelfCheckEcho(which, "\"" + tag + "\"", received, secure, request.Scheme, time.GetUtcNow()));
            })
            .WithName("GetSelfCheckEcho")
            .RequireAuthorization(AuthPolicies.CookieAdminOnly);

        // The page ran the round trip; the judging (and every word of fix text) stays here, next to the other checks.
        app.MapPost("/api/admin/selfcheck/roundtrip", (RoundTripObservation observation) =>
            {
                IReadOnlyList<DiagnosticItem> items = RoundTripChecks.Evaluate(observation ?? new RoundTripObservation());
                return Results.Ok(new SelfCheckResponse(DateTimeOffset.UtcNow, SelfCheckSummary.Of(items), items));
            })
            .WithName("EvaluateSelfCheckRoundTrip")
            .RequireAuthorization(AuthPolicies.CookieAdminOnly);

        return app;
    }
}

/// <summary>Gathers what the self-check needs from a request and the running server, and runs the rules. Shared by the self-check route and by <c>/api/admin/status</c>, which prints its summary.</summary>
public sealed class SelfCheckService(
    DraughtsmanConfig config,
    EffectiveAllowedHosts allowedHosts,
    IOptions<ForwardedHeadersOptions> forwarded,
    BuildInfo build,
    DataFolder data,
    Draughtsman.Server.Maintenance.StorageWriteHealth writeHealth)
{
    public async Task<IReadOnlyList<DiagnosticItem>> RunAsync(HttpContext http, string? pageScheme, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ConnectionInputs inputs = Inputs(http, pageScheme);
        (string provider, string? file) = DatabaseLocation(config, data.Path);
        MachineProbeResult probe = MachineProbe.Run(data.Path, provider, file);
        // The database's own answer to a write that changes nothing (the same probe /api/health reports from), for SQLite and Postgres alike: better than opening the file.
        Draughtsman.Core.Storage.StoreWriteProbe write = await writeHealth.CurrentAsync(cancellationToken);
        if (!write.Writable && probe.DatabaseWriteError is null)
        {
            probe = probe with { DatabaseWriteError = write.Problem ?? "the database refused a write" };
        }

        return
        [
            .. ConnectionChecks.Evaluate(inputs),
            .. MachineChecks.Evaluate(probe),
            BuildChecks.Describe(build.Version, build.BuildDate, HostKinds.Name(HostKinds.Detect())),
        ];
    }

    public ConnectionInputs Inputs(HttpContext http, string? pageScheme)
    {
        ForwardedSnapshot snapshot = ForwardedSnapshot.Of(http) ?? FromRequest(http, forwarded.Value);
        IReadOnlyList<string> proxies = [.. config.Server.KnownProxies.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())];
        string? normalisedScheme = pageScheme?.Trim().ToLowerInvariant() is "http" or "https" ? pageScheme.Trim().ToLowerInvariant() : null;
        return new ConnectionInputs(
            snapshot,
            http.Request.Scheme,
            http.Connection.RemoteIpAddress,
            config.Server.ForceHttps,
            allowedHosts.Hosts,
            config.Server.AllowedHosts.Any(h => !string.IsNullOrWhiteSpace(h)),
            proxies,
            normalisedScheme);
    }

    /// <summary>A snapshot built from the request as it is now, for a host that did not run <see cref="ForwardedHeaderObserver"/> (the headers a trusted proxy sent have then been consumed, so this is a best effort).</summary>
    private static ForwardedSnapshot FromRequest(HttpContext http, ForwardedHeadersOptions options)
    {
        IPAddress? peer = http.Connection.RemoteIpAddress;
        string? Header(string name) => http.Request.Headers.TryGetValue(name, out Microsoft.Extensions.Primitives.StringValues v) && v.Count > 0 ? v.ToString() : null;
        return new ForwardedSnapshot(peer, http.Request.Scheme, http.Request.Host.Value ?? string.Empty, Header("X-Forwarded-Proto"), Header("X-Forwarded-For"), Header("X-Forwarded-Host"), Header("CF-Connecting-IP"), Header("Forwarded"), ForwardedTrust.IsTrusted(peer, options), Header("X-Real-IP"), Header("True-Client-IP"));
    }

    /// <summary>The storage provider's name and, for SQLite, the database file (the connection string's data source), exactly as the server will open it.</summary>
    public static (string Provider, string? File) DatabaseLocation(DraughtsmanConfig config, string dataFolder)
    {
        StorageOptions storage = StorageOptions.FromConfig(config.Storage.Provider, config.Storage.ConnectionString, dataFolder);
        return storage.Provider == StorageProvider.Sqlite
            ? (storage.ProviderName, Path.GetFullPath(new SqliteConnectionStringBuilder(storage.ConnectionString).DataSource))
            : (storage.ProviderName, null);
    }
}

/// <summary>The wiring: the observer ahead of the forwarded-headers middleware, the counters, and the service the routes use.</summary>
public static class DiagnosticsRegistration
{
    public static IServiceCollection AddDraughtsmanDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<ForwardedObservations>();
        services.AddHostedService<ForwardedObservationsFlusher>();
        services.AddSingleton<SelfCheckService>();
        services.AddSingleton<IfMatchRefusalLog>();
        return services;
    }

    /// <summary>Takes each request's proxy headers as they arrive. Must sit before <c>UseForwardedHeaders</c>, which removes them.</summary>
    public static WebApplication UseForwardedHeaderObserver(this WebApplication app)
    {
        app.UseMiddleware<ForwardedHeaderObserver>();
        return app;
    }

    /// <summary>Logs one Warning when a document write is refused for its version tag. After routing, so the route is known.</summary>
    public static WebApplication UseIfMatchRefusalLogging(this WebApplication app)
    {
        app.UseMiddleware<IfMatchRefusalMiddleware>();
        return app;
    }
}
