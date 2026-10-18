using System.Net;
using System.Text.Json;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Mcp.Tools;
using Draughtsman.Server;
using Draughtsman.Server.Ai;
using Draughtsman.Server.Assets;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Auth.Endpoints;
using Draughtsman.Server.Auth.PasswordReset;
using Draughtsman.Server.Cli;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Diagnostics;
using Draughtsman.Server.Docs;
using Draughtsman.Server.Email;
using Draughtsman.Server.Endpoints;
using Draughtsman.Server.Export;
using Draughtsman.Server.Hardening;
using Draughtsman.Server.Hosting;
using Draughtsman.Server.KeyRing;
using Draughtsman.Server.Maintenance;
using Draughtsman.Server.Theming;
using Draughtsman.Server.Theming.Extraction;
using Draughtsman.Server.Thumbnails;
using Draughtsman.Sidecar;
using Draughtsman.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Verb dispatcher (src/Draughtsman.Server/Cli): `mcp --stdio`, `service`, `--version` and `--help` run there and never
// reach the web host; `serve`, and a bare run, hand back the web host builder.
if (await ServerCli.StartAsync(args) is not { } builder)
{
    return;
}

// DRAUGHTSMAN_DATA is read through configuration so tests can point an instance at a temp folder with UseSetting.
string dataFolder = ConfigLoader.ResolveDataFolder(builder.Configuration[DraughtsmanConfig.DataFolderEnvironmentVariable]);
// Owner-only data folder and files (AU-12): the umask is set before anything below can create a file.
// What it finds open to other users is logged once the host exists, so a Windows service reports it in the Event Log.
IReadOnlyList<string> dataFolderNotes = [];
string? dataFolderProblem;
try
{
    dataFolderNotes = DataFolderPermissions.Prepare(dataFolder, echo: false);
    // Before anything else writes to it: a folder this account cannot write ends here with one plain line, not an unhandled exception.
    dataFolderProblem = DataFolderWriteProbe.Check(dataFolder, null);
}
catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
{
    dataFolderProblem = DataFolderWriteProbe.FromException(dataFolder, ex);
}

if (dataFolderProblem is not null)
{
    await DataFolderWriteProbe.Refuse(dataFolderProblem);
    return;
}

(DraughtsmanConfig config, IReadOnlyList<ConfigWarning> configWarnings) = ConfigLoader.LoadWithWarnings(dataFolder);
StorageOptions storage = StorageOptions.FromConfig(config.Storage.Provider, config.Storage.ConnectionString, dataFolder);
// A database file (or its write-ahead-log files) left owned by another account, say by a restore run as root, opens and reads but cannot save.
if (DataFolderWriteProbe.Check(dataFolder, storage.SqliteDatabasePath) is { } databaseProblem)
{
    await DataFolderWriteProbe.Refuse(databaseProblem);
    return;
}


string? urlsOverride = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (string.IsNullOrEmpty(urlsOverride))
{
    builder.WebHost.UseUrls($"http://{config.Server.Host}:{config.Server.Port}");
}

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(new DataFolder(dataFolder));
// The customer documentation an archive carries, served at /docs and offered by Help > Documentation when about.docsUrl is unset.
builder.Services.AddSingleton(DocsFolder.Find(AppContext.BaseDirectory));
// Request size and rate limits, headers, host filtering and logging policy (AU-07 to AU-19): see Hardening/.
builder.AddDraughtsmanHardening(config, dataFolder);
builder.Services.AddDraughtsmanStorage(storage);
// Chrome theme and admin branding overrides (spec section 8, M3): the theme file is the fallback, the data folder's
// branding/ overrides layer over it, and the single ChromeBrandingService is what every theme endpoint reads, so an
// admin's upload lands on the next request with no restart.
builder.Services.AddSingleton<BrandingStore>();
// Organisation themes (admin Themes page): stored under <data>/themes/org/, one file per theme; the default one, when
// set, replaces the chrome theme file's tokens for every user.
builder.Services.AddSingleton<OrgThemeStore>();
builder.Services.AddSingleton<OrgThemeService>();
builder.Services.AddSingleton(services => new ThemePageFetcher(services.GetRequiredService<DraughtsmanConfig>().Themes.FetchFromUrl));
// The default organisation theme also paints the server-side picture (PDF export, render_document).
builder.Services.AddSingleton<IDiagramThemeProvider, OrgDiagramThemeProvider>();
builder.Services.AddSingleton(services =>
{
    ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Draughtsman.Server.Theming");
    string folder = services.GetRequiredService<DataFolder>().Path;
    LoadedChromeTheme theme = ChromeThemeLoader.Load(folder, config.Theme.Chrome, logger);
    logger.LogInformation("Chrome theme {Theme} ({ProductName}) loaded from {Source}", theme.Theme.Name, theme.Theme.ProductName, theme.Source);
    return new ChromeBrandingService(theme, services.GetRequiredService<BrandingStore>(), services.GetRequiredService<OrgThemeService>());
});
// Asset bytes live on disk under the data folder, their metadata rows behind IStore (ADR 0003): AssetStore is the one
// service that knows both, and IAssetReader is what the renderers use to turn a node's assetId into a picture.
builder.Services.AddScoped<AssetStore>();
builder.Services.AddScoped<IAssetReader>(services => services.GetRequiredService<AssetStore>());
builder.Services.AddProblemDetails();
// A database that refuses writes is answered with a 503 that says the instance cannot save, not a bare 500 (Maintenance/StorageWriteFailureHandler).
builder.Services.AddExceptionHandler<StorageWriteFailureHandler>();
builder.Services.AddSingleton<StorageWriteHealth>();
// Daily housekeeping (AU-15): old checkpoints and ended sessions, per the retention section of draughtsman.yaml.
builder.Services.AddHostedService<MaintenanceService>();

// Local sign-in (build-spec-v0.1.md section 7): a cookie session for the browser and a bearer API token for MCP and
// scripting, picked per request by whether an Authorization header is present. Both handlers re-check the store on
// every request rather than trusting a signed ticket, so a revoke or deactivation takes effect on the very next call.
builder.Services.AddSingleton<FailedSignInThrottle>();
// Security event log (AU-04) and API token lifetimes (AU-05): see docs/api.md's Auth section.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new TokenPolicy(config.Auth.Tokens));
builder.Services.AddScoped<SecurityEventLog>();
builder.Services.AddSingleton(new SetupCodeFile(dataFolder));
// Password recovery (docs/api.md "Password reset"): outgoing email from `email:` and `server.publicUrl`, the lifetimes from
// `auth.passwordReset`, and the queue that keeps the work behind a "Forgot password?" request off the request itself.
builder.Services.AddEmail(config, dataFolder);
builder.Services.AddSingleton(services => new PasswordResetPolicy(config.Auth.PasswordReset, services.GetRequiredService<EmailSettingsService>()));
builder.Services.AddSingleton<PasswordResetMailQueue>();
builder.Services.AddScoped<PasswordResetMailer>();
builder.Services.AddHostedService<PasswordResetMailWorker>();
builder.Services.AddSingleton<SetupCodeGate>();
// Connection self-check, proxy-header observations and the log line for a refused version tag (docs/troubleshooting.md).
builder.Services.AddDraughtsmanDiagnostics();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = CsrfMiddleware.XsrfHeaderName;
    // Secure whenever the request is https, which server.forceHttps makes true by overriding the scheme: the framework's default
    // sets no Secure flag at all, against what docs/deployment.md promises ("makes every cookie Secure").
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
// Trusted only from server.knownProxies (security review F4): forwarding X-Forwarded-For/-Proto from an untrusted
// caller would let it spoof the IP the throttle keys on and the scheme cookies decide Secure from.
// Addresses and CIDR ranges (AU-02); an entry that is neither is logged rather than silently dropped.
builder.Services.ConfigureKnownProxies(config.Server.KnownProxies);
// Antiforgery/data-protection keys survive a restart or container recreation (security review F7); otherwise every
// outstanding XSRF pair is invalidated the moment the process restarts. The same ring encrypts the admin's stored AI
// API key, so it is protected at rest (DPAPI, the macOS Keychain, or a key-encryption key held outside the data
// folder) and an existing plain-text ring is migrated on this start, before anything reads it. Nothing in here can
// stop the server starting, with one deliberate exception: a Mac whose Keychain cannot be used stops with a message
// naming the two safe choices rather than carrying on with a ring that should be protected left as plain text;
// see KeyRingSetup.
try
{
    using ILoggerFactory keyRingLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
    KeyRingSetup.Configure(
        builder.Services,
        dataFolder,
        config.KeyRing,
        builder.Environment.ContentRootPath,
        keyRingLoggerFactory.CreateLogger("Draughtsman.Server.KeyRing"),
        name => builder.Configuration[name]);
}
catch (KeyRingProtectionRequiredException ex)
{
    await Console.Error.WriteLineAsync($"Draughtsman cannot start: {ex.Message}");
    if (OperatingSystem.IsWindows())
    {
        // A service has no console: the Application log is where "the service failed to start" gets looked into.
        WindowsEventSource.TryWriteError($"Draughtsman cannot start: {ex.Message}");
    }

    Environment.ExitCode = 1;
    return;
}

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = AuthSchemes.Combined;
        options.DefaultAuthenticateScheme = AuthSchemes.Combined;
        options.DefaultChallengeScheme = AuthSchemes.Combined;
    })
    .AddPolicyScheme(AuthSchemes.Combined, AuthSchemes.Combined, options =>
    {
        options.ForwardDefaultSelector = context => context.Request.Headers.ContainsKey("Authorization") ? AuthSchemes.Token : AuthSchemes.Cookie;
    })
    .AddScheme<AuthenticationSchemeOptions, CookieSessionAuthenticationHandler>(AuthSchemes.Cookie, _ => { })
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(AuthSchemes.Token, _ => { });
builder.Services.AddAuthorization(options =>
{
    // Every route requires sign-in unless it opts out with AllowAnonymous (health, theme, sign-in/first-run, the
    // SPA's own static assets and client-routing fallback) - see docs/api.md's Conventions section.
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy(AuthPolicies.AdminOnly, policy => policy.RequireRole(nameof(UserRole.Admin)));
    // Security review F3: an API token carries its owner's role, so AdminOnly alone would let a token minted by an
    // admin manage accounts or mint further tokens through it - account and token management stay cookie-only.
    options.AddPolicy(AuthPolicies.CookieOnly, policy => policy.RequireAssertion(ctx => AuthPolicies.IsCookieAuthenticated(ctx.User)));
    options.AddPolicy(AuthPolicies.CookieAdminOnly, policy => policy.RequireRole(nameof(UserRole.Admin)).RequireAssertion(ctx => AuthPolicies.IsCookieAuthenticated(ctx.User)));
});
// MCP is transport-agnostic (src/Draughtsman.Mcp) and must not depend on ASP.NET Core, so it learns who is calling
// through IActorContext rather than HttpContext directly (security review F2): over HTTP, /mcp's actor argument may
// only annotate which agent is acting on top of the already-authenticated caller, never claim a different identity.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IActorContext, HttpActorContext>();
// Optional PDF export via Chromium (build-spec-v0.1.md section 7). IsAvailable is false wherever the component is
// absent, and the MCP render_document tool and the PDF endpoint both gate on it. The ChromiumConfig is a singleton
// so the renderer and the health check see the same enabled/path values.
builder.Services.AddSingleton(config.Export.Chromium);
builder.Services.AddSingleton<IPdfRenderer, ChromiumPdfRenderer>();
// Document thumbnails for the documents page: the same SvgRenderer, no browser, drawn at most limits.thumbnailConcurrency at a time
// and kept in a bounded in-memory cache (Thumbnails/).
builder.Services.AddSingleton(services => new ThumbnailService(new ThumbnailLimits(), config.Limits.ThumbnailConcurrency, services.GetRequiredService<ILogger<ThumbnailService>>()));
// The admin's AI overrides and encrypted key live under <data>/ai/; AiSettingsService layers them over the file.
builder.Services.AddSingleton<AiSettingsStore>();
// At startup, moves a stored secret an earlier build bound to its install path onto the pinned Data Protection name (KeyRing/StoredSecrets.cs).
builder.Services.AddSingleton<StoredSecretsMigration>();
// Bring-your-own AI (build-spec-v0.1.md section 7): unset or unrecognised ai.provider leaves AiStatus off rather
// than failing startup. Logged with a bootstrap logger because builder.Services is still being configured; app.Logger
// (used everywhere else in this file) only exists after Build().
using (ILoggerFactory bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole()))
{
    builder.Services.AddAi(config.Ai, bootstrapLoggerFactory.CreateLogger("Draughtsman.Server.Ai"));
}

// The version and build date the About page and the diagnostics report (stamped by MSBuild, Hosting/BuildInfo.cs).
builder.Services.AddSingleton(BuildInfo.FromAssembly());

// API responses that embed a document serialise it exactly as DocumentJson does.
builder.Services.ConfigureHttpJsonOptions(options => DocumentJson.Configure(options.SerializerOptions));

if (config.Mcp.Enabled)
{
    // Tool arguments and results are serialised with the document model's conventions, but the MCP SDK's AI function
    // factory requires an explicit TypeInfoResolver on the options before it marks them read-only.
    var toolSerializerOptions = new JsonSerializerOptions(DocumentJson.Options)
    {
        TypeInfoResolver = JsonSerializerOptions.Default.TypeInfoResolver,
    };

    builder.Services.AddMcpServer()
        .WithHttpTransport()
        .WithTools<DraughtsmanTools>(toolSerializerOptions)
        .WithArgumentValidation();
}

// The Node sidecar for server-side ELK layout and Yjs materialisation (ADR 0002). Started with the host.
TimeSpan layoutTimeout = SidecarConfig.ResolveTimeout(config.Sidecar.LayoutTimeoutSeconds, SidecarConfig.DefaultLayoutTimeoutSeconds);
TimeSpan yjsTimeout = SidecarConfig.ResolveTimeout(config.Sidecar.YjsTimeoutSeconds, SidecarConfig.DefaultYjsTimeoutSeconds);
builder.Services.AddSidecar(new SidecarOptions
{
    NodePath = config.Sidecar.NodePath,
    ScriptPath = config.Sidecar.ScriptPath,
    LayoutTimeout = layoutTimeout,
    YjsTimeout = yjsTimeout,
});

WebApplication app = builder.Build();

app.Logger.LogInformation("Draughtsman {Version}; data folder {DataFolder}; storage {Provider}",
    HealthEndpoints.ProductVersion, dataFolder, storage.ProviderName);

foreach (string note in dataFolderNotes)
{
    app.Logger.LogWarning("{Note}", note);
}

// An old or mistyped setting never stops the server (docs/upgrade-backup.md, "Settings"); it is named here, and by `draughtsman config check` beforehand.
foreach (ConfigWarning warning in configWarnings)
{
    app.Logger.LogWarning("draughtsman.yaml: {Warning}", warning.Message);
}

if (!string.IsNullOrEmpty(urlsOverride))
{
    app.Logger.LogInformation(
        "ASPNETCORE_URLS ({Urls}) chooses the bind address; sign-in is required for every route except health, theme and the sign-in endpoints themselves.",
        urlsOverride);
}
else if (IsLoopback(config.Server.Host))
{
    app.Logger.LogInformation(
        "Bound to loopback {Host}, so the API is reachable only from this machine; every route still requires sign-in except health, theme and the sign-in endpoints.",
        config.Server.Host);
}
else
{
    app.Logger.LogWarning(
        "server.host is {Host}, which is not loopback: the API is reachable from other hosts. Sign-in and per-account/IP throttling are in place, but a firewall or reverse proxy is still worth adding.",
        config.Server.Host);
}

static bool IsLoopback(string host)
{
    return IPAddress.TryParse(host, out IPAddress? address)
        ? IPAddress.IsLoopback(address)
        : string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
}

using (IServiceScope scope = app.Services.CreateScope())
{
    // A database a newer build already migrated is refused here (OP-03): logged with what to do, exit code 3, nothing served.
    if (!await StorageStartup.MigrateOrRefuseAsync(scope.ServiceProvider.GetRequiredService<DraughtsmanDbContext>(), storage, config, app.Logger))
    {
        return;
    }

    // First-run's one-time setup code (security review F1.2), printed loudly enough to find in a container's logs; a
    // Windows service, whose only log any local user can read, stores it in a restricted file instead (SetupCodeAnnouncer).
    // Lives only in the SetupCodeGate singleton: a restart before first-run completes issues a fresh one.
    IUserStore userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();
    if (!await userStore.HasAnyUsers())
    {
        string setupCode = scope.ServiceProvider.GetRequiredService<SetupCodeGate>().Issue();
        SetupCodeAnnouncer.Announce(app.Logger, HostKinds.Detect(), dataFolder, setupCode);
    }
    else
    {
        // A code file left by an earlier run (an instance restored from a backup, a crash before cleanup) is worthless now and must not linger.
        new SetupCodeFile(dataFolder).Delete();
    }
}

// After the schema is known to be one this build may serve and before any request: the AI key and SMTP password an earlier build tied to its
// install folder are moved onto the pinned application name, so a later move of the program cannot make them unreadable. Never stops the start.
app.Services.GetRequiredService<StoredSecretsMigration>().Run(app.Logger);

// Browser security headers on every response, error pages included (AU-13).
app.UseSecurityHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
// Before anything that reads the connection (auth, the throttle, cookie Secure decisions): only trusted from
// server.knownProxies (security review F4), so an untrusted caller cannot spoof its own IP or scheme this way.
app.UseUntrustedForwardedHeadersWarning();
app.UseForwardedHeaderObserver();
app.UseForwardedHeaders();
if (config.Server.ForceHttps)
{
    // server.forceHttps (security review F5): the operator has confirmed every request reaches this instance over
    // HTTPS, directly or via a proxy, even where forwarded headers cannot make Request.IsHttps reflect that on their
    // own. Overriding the scheme here - before anything downstream reads it - makes every Secure-cookie decision
    // (ours and the framework's own antiforgery cookie) and app.UseHsts() correct without threading a second,
    // parallel "is this secure" flag through every one of them.
    app.Use((context, next) =>
    {
        context.Request.Scheme = "https";
        return next(context);
    });
    app.UseHsts();
}

// Request body limits per route class (AU-07), ahead of everything that reads a body or a credential.
app.UseRequestBodyLimits();

// Static assets before routing/auth, the standard ordering, so a signed-out browser can still load the SPA shell
// that shows the login page; the fallback route that serves index.html for a client-side route like /login still
// goes through routing/auth below and needs its own AllowAnonymous (StaticWeb.MapSpaFallback).
string? webRoot = StaticWeb.FindWebRoot(app.Environment.ContentRootPath);
app.UseSpaStaticFiles(webRoot);
app.UseRouting();
// One Warning per route per minute when a document write is refused over its If-Match tag; needs the route, so after UseRouting.
app.UseIfMatchRefusalLogging();
app.UseAuthentication();
// A read-scoped API token may only GET (AU-05); checked before CSRF and authorization so no route can forget it.
app.UseMiddleware<TokenScopeMiddleware>();
app.UseMiddleware<CsrfMiddleware>();
app.UseAuthorization();
// Concurrency caps and per-user rate limits on PDF, layout, import, AI and MCP (AU-09); after auth so it knows the user.
app.UseDraughtsmanRateLimits();

if (layoutTimeout.TotalSeconds != config.Sidecar.LayoutTimeoutSeconds)
{
    app.Logger.LogWarning("sidecar.layoutTimeoutSeconds must be greater than zero; {Configured} ignored and {Used} s used", config.Sidecar.LayoutTimeoutSeconds, layoutTimeout.TotalSeconds);
}

if (yjsTimeout.TotalSeconds != config.Sidecar.YjsTimeoutSeconds)
{
    app.Logger.LogWarning("sidecar.yjsTimeoutSeconds must be greater than zero; {Configured} ignored and {Used} s used", config.Sidecar.YjsTimeoutSeconds, yjsTimeout.TotalSeconds);
}

// Force the theme/branding singleton to construct now, so the startup log line and any fallback warning appear
// before the first request rather than lazily.
_ = app.Services.GetRequiredService<ChromeBrandingService>();

app.MapThemeEndpoints();
app.MapHealthEndpoints();
app.MapAssetEndpoints();
app.MapAuthEndpoints();
app.MapPasswordResetEndpoints();
app.MapEmailEndpoints();
app.MapUserEndpoints();
app.MapTokenEndpoints();
app.MapSecurityEventEndpoints();
app.MapAdminExportEndpoints();
app.MapBrandingEndpoints();
app.MapOrgThemeEndpoints();
app.MapAiSettingsEndpoints();
app.MapAboutEndpoints();
app.MapDocsEndpoints();
app.MapAdminStatusEndpoints();
app.MapSelfCheckEndpoints();
app.MapImportEndpoints();
app.MapLayoutEndpoints();
app.MapDocumentEndpoints();
app.MapDocumentHistoryEndpoints();
app.MapBinEndpoints();
app.MapExportEndpoints();
app.MapThumbnailEndpoints();
app.MapAiEndpoints();
app.MapDiagramToDocumentEndpoints();
if (config.Mcp.Enabled)
{
    // Streamable HTTP on the main port (build-spec-v0.1.md section 9): the same tools the stdio host serves, but
    // over HTTP it requires a bearer token (the fallback authorization policy above) - `draughtsman mcp --stdio`
    // stays unauthenticated, since it never binds HTTP at all (README's Sign-in section).
    app.MapMcp("/mcp");
}

app.MapSpaFallback(webRoot);

await app.RunAsync();

/// <summary>Exposes Program to WebApplicationFactory in the test project.</summary>
public partial class Program
{
}
