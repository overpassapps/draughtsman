using Draughtsman.Server.Configuration;
using Microsoft.AspNetCore.HostFiltering;

namespace Draughtsman.Server.Hardening;

/// <summary>
/// The wiring for the L5 hardening work (AU-02, AU-07 to AU-19), kept here so Program.cs names each piece once, in the
/// pipeline order that matters: <see cref="AddDraughtsmanHardening"/> registers the services and host settings, and the
/// <c>Use...</c> methods add the middleware at the point Program.cs calls them.
/// </summary>
public static class HardeningRegistration
{
    public static WebApplicationBuilder AddDraughtsmanHardening(this WebApplicationBuilder builder, DraughtsmanConfig config, string dataFolder)
    {
        builder.ConfigureDraughtsmanLogging(config, dataFolder);
        var limits = new RequestBodyLimits(config.Limits, config.Assets);
        builder.Services.AddSingleton(limits);
        builder.Services.AddSingleton(config.Limits);
        builder.Services.AddDraughtsmanRateLimits(config.Limits);
        // Which Host headers are answered to (AU-19); the framework's host filtering middleware does the refusing.
        string[] allowedHosts = HostPolicy.Resolve(config.Server, Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
        builder.Services.Configure<HostFilteringOptions>(options => options.AllowedHosts = allowedHosts);
        builder.Services.AddSingleton(new EffectiveAllowedHosts(allowedHosts));
        builder.WebHost.ConfigureKestrel(options =>
        {
            // A backstop above every per-route limit, not the limit itself; it also stops Kestrel's 30 MB default applying
            // to a route RequestBodyLimits did not classify.
            options.Limits.MaxRequestBodySize = limits.Largest;
            // The Server: Kestrel header tells a scanner what to look up (AU-19).
            options.AddServerHeader = false;
        });
        return builder;
    }

    /// <summary>First in the pipeline, so error pages and static files carry the headers too.</summary>
    public static WebApplication UseSecurityHeaders(this WebApplication app)
    {
        app.UseMiddleware<SecurityHeadersMiddleware>();
        return app;
    }

    /// <summary>Warns once an hour about forwarding headers from a peer <c>server.knownProxies</c> does not trust; must run before <c>UseForwardedHeaders</c>.</summary>
    public static WebApplication UseUntrustedForwardedHeadersWarning(this WebApplication app)
    {
        app.UseMiddleware<UntrustedForwardedHeadersMiddleware>();
        return app;
    }

    /// <summary>Refuses an oversized body before authentication, so an anonymous caller cannot make the server buffer it.</summary>
    public static WebApplication UseRequestBodyLimits(this WebApplication app)
    {
        app.UseMiddleware<RequestBodyLimitMiddleware>();
        return app;
    }
}

/// <summary>The hosts <see cref="HostPolicy"/> resolved, for the start-up log line.</summary>
public sealed record EffectiveAllowedHosts(string[] Hosts);
