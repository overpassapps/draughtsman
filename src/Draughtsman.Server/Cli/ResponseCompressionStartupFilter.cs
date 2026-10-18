using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.ResponseCompression;

namespace Draughtsman.Server.Cli;

/// <summary>
/// Brotli and gzip for the SPA bundle and the JSON API (PK-08: the editor's JS is over a megabyte and a 2,000-node
/// document is megabytes of JSON). A startup filter so the middleware sits first in the pipeline without a line in
/// Program.cs; it only acts on a request that sends <c>Accept-Encoding</c>. ASP.NET Core leaves compression off over
/// HTTPS on purpose (BREACH), so behind a TLS-terminating proxy, or with <c>server.forceHttps</c>, the proxy compresses.
/// The MIME list is the framework's plus <c>image/svg+xml</c> (a documents page of fifty thumbnails, each an SVG of a few to a few
/// hundred kilobytes, is otherwise sent as plain text; it compresses five to ten times): <c>text/event-stream</c> (MCP streaming) is not
/// in it, so it is never buffered.
/// </summary>
public sealed class ResponseCompressionStartupFilter : IStartupFilter
{
    public static void AddServices(IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Append("image/svg+xml");
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        services.AddTransient<IStartupFilter, ResponseCompressionStartupFilter>();
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Not the self-check echo: a content-encoding its response carries must have been added in transit, never by this process.
            app.UseWhen(context => !context.Request.Path.StartsWithSegments(Diagnostics.SelfCheckEndpoints.EchoPath, StringComparison.OrdinalIgnoreCase), branch => branch.UseResponseCompression());
            next(app);
        };
    }
}
