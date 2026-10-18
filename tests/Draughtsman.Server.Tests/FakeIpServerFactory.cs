using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Tests;

/// <summary>
/// A server whose requests can claim a remote address (the TestServer has none): a request carrying
/// <see cref="Header"/> gets that value as <c>Connection.RemoteIpAddress</c>, set before anything in the real
/// pipeline runs. Test-only; the real server never reads this header.
/// </summary>
public sealed class FakeIpServerFactory(string? yaml = null, ILoggerProvider? logs = null, Action<ILoggingBuilder>? configureLogging = null) : DraughtsmanServerFactory
{
    public const string Header = "X-Test-Remote-Ip";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (yaml is not null)
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(Path.Combine(DataFolder, Draughtsman.Server.Configuration.DraughtsmanConfig.FileName), yaml);
        }

        if (logs is not null)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
        }

        if (configureLogging is not null)
        {
            builder.ConfigureLogging(configureLogging);
        }

        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, SetRemoteIpFilter>());
    }

    private sealed class SetRemoteIpFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use((context, pipeline) =>
                {
                    if (context.Request.Headers.TryGetValue(Header, out Microsoft.Extensions.Primitives.StringValues value) && IPAddress.TryParse(value.ToString(), out IPAddress? ip))
                    {
                        context.Connection.RemoteIpAddress = ip;
                    }

                    return pipeline(context);
                });
                next(app);
            };
        }
    }
}
