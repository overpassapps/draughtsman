using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Tests.ThemeColourExtraction;

/// <summary>One request the loopback site saw: what a fetch really sent, so a test can assert what it did not send.</summary>
internal sealed record SiteRequest(string Path, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A throwaway web site on 127.0.0.1 for the URL-fetch tests: whatever the test's handler writes is what the fetcher is shown. It is
/// only ever reached over the loopback interface, so a test using it sends nothing off this machine.
/// </summary>
internal sealed class LoopbackSite : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<SiteRequest> _requests = [];

    private LoopbackSite(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public string Origin => $"http://127.0.0.1:{Port}";

    public IReadOnlyList<SiteRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public static async Task<LoopbackSite> Start(Func<HttpContext, Task> handler)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        WebApplication app = builder.Build();
        LoopbackSite? site = null;
        app.Run(async context =>
        {
            lock (site!._requests)
            {
                site._requests.Add(new SiteRequest(context.Request.Path + context.Request.QueryString, context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            }

            await handler(context);
        });
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        site = new LoopbackSite(app, new Uri(address).Port);
        return site;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TimeSpan.FromSeconds(2));
        await _app.DisposeAsync();
    }

    public static Task Html(HttpContext context, string body)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(body);
    }

    public static Task Css(HttpContext context, string body)
    {
        context.Response.ContentType = "text/css";
        return context.Response.WriteAsync(body);
    }
}
