using System.Threading.RateLimiting;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Configuration;
using Microsoft.AspNetCore.RateLimiting;

namespace Draughtsman.Server.Hardening;

/// <summary>
/// Concurrency caps and per-user rate limits on the routes that cost something to serve (AU-09): a PDF is a Chromium of
/// 150 to 300 MB, a thumbnail is a render (only its per-user rate is here: its concurrency cap is the render queue in
/// <c>ThumbnailService</c>, which makes a request wait where the caps below refuse it), layout and snapshots share one Node
/// sidecar that is killed when a call runs long, an import parses up to a megabyte of text, and an AI call spends the organisation's provider budget. A caller over a limit gets 429 with
/// <c>Retry-After</c> and costs the server nothing further. Limits are <see cref="LimitsConfig"/> values; 0 turns one off.
/// One global limiter chains a limit per rule, each applying only to its own routes, so the route files stay untouched and
/// a new route in a class is covered by its path.
/// </summary>
public static class RateLimiting
{
    /// <summary>The partition a request outside a rule's routes lands in. Never the key of a real limiter: a partition key names one limiter for good, so sharing one would hand every later request the no-limit one.</summary>
    private const string NotLimited = "(not limited)";

    public static IServiceCollection AddDraughtsmanRateLimits(this IServiceCollection services, LimitsConfig limits)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                Concurrency("pdf", IsPdf, limits.PdfConcurrency),
                PerUser("pdf", IsPdf, limits.PdfPerUserPerMinute),
                Concurrency("layout", IsLayout, limits.LayoutConcurrency),
                PerUser("layout", IsLayout, limits.LayoutPerUserPerMinute),
                PerUser("import", IsImport, limits.ImportPerUserPerMinute),
                Concurrency("ai", IsAi, limits.AiConcurrency),
                PerUser("ai", IsAi, limits.AiPerUserPerMinute),
                PerUser("mcp", IsMcp, limits.McpPerUserPerMinute),
                PerUser("thumbnail", IsThumbnail, limits.ThumbnailPerUserPerMinute));
            options.OnRejected = WriteRejection;
        });
        return services;
    }

    /// <summary>Must run after authentication, which is what names the user a limit is per, and after authorization, so a refused request never holds a permit.</summary>
    public static WebApplication UseDraughtsmanRateLimits(this WebApplication app)
    {
        app.UseRateLimiter();
        return app;
    }

    internal static bool IsPdf(HttpContext context)
    {
        PathString path = context.Request.Path;
        return HttpMethods.IsGet(context.Request.Method)
            && path.StartsWithSegments("/api/documents", StringComparison.OrdinalIgnoreCase)
            && path.Value!.EndsWith("/pdf", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsThumbnail(HttpContext context)
    {
        PathString path = context.Request.Path;
        return HttpMethods.IsGet(context.Request.Method)
            && path.StartsWithSegments("/api/documents", StringComparison.OrdinalIgnoreCase)
            && path.Value!.EndsWith("/thumbnail", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsLayout(HttpContext context)
    {
        return HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api/layout", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsImport(HttpContext context)
    {
        return HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api/import", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAi(HttpContext context)
    {
        return HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api/ai", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsMcp(HttpContext context)
    {
        return context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);
    }

    private static PartitionedRateLimiter<HttpContext> Concurrency(string rule, Func<HttpContext, bool> applies, int permits)
    {
        return PartitionedRateLimiter.Create<HttpContext, string>(context =>
            permits > 0 && applies(context)
                ? RateLimitPartition.GetConcurrencyLimiter(rule, _ => new ConcurrencyLimiterOptions { PermitLimit = permits, QueueLimit = 0 })
                : RateLimitPartition.GetNoLimiter(NotLimited));
    }

    private static PartitionedRateLimiter<HttpContext> PerUser(string rule, Func<HttpContext, bool> applies, int permitsPerMinute)
    {
        return PartitionedRateLimiter.Create<HttpContext, string>(context =>
            permitsPerMinute > 0 && applies(context)
                ? RateLimitPartition.GetFixedWindowLimiter($"{rule}:{CallerKey(context)}", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                })
                : RateLimitPartition.GetNoLimiter(NotLimited));
    }

    /// <summary>The signed-in user's id, or the address when there is none (the route is refused a moment later, but must not share a budget with a real user).</summary>
    private static string CallerKey(HttpContext context)
    {
        return context.User.UserId() is { } id ? "user:" + id : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    private static async ValueTask WriteRejection(OnRejectedContext context, CancellationToken cancellationToken)
    {
        HttpContext http = context.HttpContext;
        int retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 1;
        http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Draughtsman.Server.RateLimit")
            .LogWarning("Refused {Method} {Path} from {Caller}: over a rate or concurrency limit", http.Request.Method, http.Request.Path.Value, CallerKey(http));
        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests",
                detail: $"This route is limited; try again in {retryAfterSeconds} second{(retryAfterSeconds == 1 ? string.Empty : "s")}.")
            .ExecuteAsync(http);
    }
}
