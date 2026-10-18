using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Draughtsman.Server.Email;
using Draughtsman.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Tests;

/// <summary>A clock a test moves by hand, so an expiry is proven by waiting a day without waiting a day.</summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public void Advance(TimeSpan by)
    {
        _now += by;
    }

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }
}

/// <summary>
/// A server for the password-recovery tests: an optional draughtsman.yaml, an optional clock, an optional replacement mail sender,
/// an optional log sink that sees every line at every level, an optional stand-in web build (<c>spa</c>: a one-line index.html the server
/// serves for every page route, so "the reset page is reachable without signing in" can be asserted whether or not the real SPA has been
/// built where the tests run: a clean checkout runs them before the web build, and then every page route is a 401), and requests that can claim a remote address with
/// <see cref="IpHeader"/> (the TestServer has none), so the per-address throttles can be exercised from two "places".
/// </summary>
public sealed class ResetServerFactory(string? yaml = null, TimeProvider? clock = null, IEmailSender? sender = null, ILoggerProvider? logs = null, bool spa = false) : DraughtsmanServerFactory
{
    public const string IpHeader = "X-Test-Remote-Ip";

    public const string PublicUrl = "https://draughtsman.test";

    /// <summary>The configuration of an instance that can email: the file provider, a public address, nothing else.</summary>
    public const string EmailYaml = """
        server:
          publicUrl: https://draughtsman.test
        email:
          provider: file
          from: Draughtsman <noreply@draughtsman.test>
        """;

    public string Outbox => Path.Combine(DataFolder, "outbox");

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

        if (spa)
        {
            // The server looks for web/dist beside its content root, so give it a content root that has one.
            string contentRoot = Path.Combine(DataFolder, "content-root");
            Directory.CreateDirectory(Path.Combine(contentRoot, "web", "dist"));
            File.WriteAllText(Path.Combine(contentRoot, "web", "dist", "index.html"), "<!doctype html><title>Draughtsman test shell</title>");
            builder.UseContentRoot(contentRoot);
        }

        builder.ConfigureTestServices(services =>
        {
            if (clock is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            }

            if (sender is not null)
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton(sender);
            }

            services.AddTransient<IStartupFilter, RemoteIpFilter>();
        });
    }

    private sealed class RemoteIpFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use((context, pipeline) =>
                {
                    if (context.Request.Headers.TryGetValue(IpHeader, out Microsoft.Extensions.Primitives.StringValues value) && IPAddress.TryParse(value.ToString(), out IPAddress? ip))
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

/// <summary>One message the file provider wrote.</summary>
public sealed record OutboxMessage(string Path, string To, string Subject, string Body)
{
    /// <summary>The secret in the reset link in the body, or null if there is none.</summary>
    public string? Secret => Regex.Match(Body, @"/reset-password#token=(dpr_[A-Za-z0-9_-]+)") is { Success: true } match ? match.Groups[1].Value : null;

    public string? Link => Regex.Match(Body, @"https?://\S+/reset-password#token=dpr_[A-Za-z0-9_-]+") is { Success: true } match ? match.Value : null;
}

/// <summary>The plumbing the password-recovery test classes share.</summary>
internal static class PasswordResetHelpers
{
    public const string NewPassword = "a-freshly-chosen-password";

    public static async Task WaitForMail(this ResetServerFactory factory)
    {
        await factory.Services.GetRequiredService<Draughtsman.Server.Auth.PasswordReset.PasswordResetMailQueue>().WaitForIdleAsync(LoadScale.Scaled(TimeSpan.FromSeconds(20)));
    }

    public static IReadOnlyList<OutboxMessage> ReadOutbox(this ResetServerFactory factory)
    {
        if (!Directory.Exists(factory.Outbox))
        {
            return [];
        }

        var messages = new List<OutboxMessage>();
        foreach (string path in Directory.GetFiles(factory.Outbox).OrderBy(p => p, StringComparer.Ordinal))
        {
            string text = File.ReadAllText(path);
            int blank = text.IndexOf("\n\n", StringComparison.Ordinal);
            string[] headers = text[..blank].Split('\n');
            string Header(string name) => headers.Single(h => h.StartsWith(name + ": ", StringComparison.Ordinal))[(name.Length + 2)..];
            messages.Add(new OutboxMessage(path, Header("To"), Header("Subject"), text[(blank + 2)..]));
        }

        return messages;
    }

    /// <summary>An administrator creates a link for <paramref name="userId"/>; returns the response and, when it was created, the secret.</summary>
    public static async Task<(HttpResponseMessage Response, string? Secret)> CreateLink(this HttpClient admin, string userId, IReadOnlyDictionary<string, string>? headers = null)
    {
        HttpRequestMessage request = AuthBarHelpers.Request(HttpMethod.Post, $"/api/users/{userId}/password-reset-link", null, admin);
        foreach (KeyValuePair<string, string> header in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        HttpResponseMessage response = await admin.SendAsync(request);
        string? secret = response.StatusCode == HttpStatusCode.Created ? (await response.Json())["token"]!.GetValue<string>() : null;
        return (response, secret);
    }

    /// <summary>Spends a link as an anonymous browser would: a POST with the secret in the body.</summary>
    public static async Task<HttpResponseMessage> Complete(this HttpClient client, string? token, string? password = NewPassword, string? ip = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password-reset/complete") { Content = JsonContent.Create(new { token, newPassword = password }) };
        if (ip is not null)
        {
            request.Headers.Add(ResetServerFactory.IpHeader, ip);
        }

        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> RequestReset(this HttpClient client, string email, string? ip = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password-reset/request") { Content = JsonContent.Create(new { email }) };
        if (ip is not null)
        {
            request.Headers.Add(ResetServerFactory.IpHeader, ip);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Status, title and detail of a Problem response: what the caller is told, without the per-request trace id.</summary>
    public static async Task<string> Problem(this HttpResponseMessage response)
    {
        JsonNode body = await response.Json();
        return $"{(int)response.StatusCode}|{body["title"]}|{body["detail"]}";
    }

    public static async Task<bool> CanSignIn(this DraughtsmanServerFactory factory, string email, string password)
    {
        HttpResponseMessage login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email, password });
        return login.StatusCode == HttpStatusCode.OK;
    }

    /// <summary>Everything the security log holds, as one string, newest page first.</summary>
    public static async Task<string> WholeSecurityLog(this HttpClient admin)
    {
        return (await admin.SecurityEvents("?limit=200")).ToJsonString();
    }
}
