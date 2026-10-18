using System.Net.Http.Json;
using Draughtsman.Server.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Security review F5: server.forceHttps must force the Secure cookie flag regardless of what this process sees on
/// the raw connection - the scenario a proxy terminating TLS without a trusted forwarded-proto header would
/// otherwise silently leave unprotected (Request.IsHttps false even though the real client connection is HTTPS).
/// HSTS is also wired to server.forceHttps in Program.cs, but ASP.NET Core's own HstsMiddleware only ever writes the
/// header on a request it already sees as HTTPS, which an in-memory TestServer request never is - that part is not
/// observable through this harness and is covered by reading Program.cs instead.
/// </summary>
public class ForceHttpsTests
{
    [Fact]
    public async Task Force_https_makes_every_cookie_secure()
    {
        using var factory = new ForceHttpsServerFactory();
        Directory.CreateDirectory(factory.DataFolder);
        File.WriteAllText(Path.Combine(factory.DataFolder, DraughtsmanConfig.FileName), "server:\n  forceHttps: true\n");

        HttpClient client = factory.CreateClient();
        string setupCode = factory.Services.GetRequiredService<Draughtsman.Server.Auth.SetupCodeGate>().Issue();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/first-run",
            new { email = "force-https@example.com", displayName = "Force Https", password = "correcthorsebattery", setupCode });

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies));
        List<string> sessionAndXsrf = cookies!.Where(c => c.StartsWith("ds_session=", StringComparison.Ordinal) || c.StartsWith("__Host-ds_session=", StringComparison.Ordinal) || c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(sessionAndXsrf);
        Assert.All(sessionAndXsrf, cookie => Assert.Contains("Secure", cookie, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Without_force_https_a_plain_http_test_request_gets_no_secure_flag()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();
        string setupCode = factory.Services.GetRequiredService<Draughtsman.Server.Auth.SetupCodeGate>().Issue();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/first-run",
            new { email = "plain-http@example.com", displayName = "Plain Http", password = "correcthorsebattery", setupCode });

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies));
        List<string> sessionAndXsrf = cookies!.Where(c => c.StartsWith("ds_session=", StringComparison.Ordinal) || c.StartsWith("__Host-ds_session=", StringComparison.Ordinal) || c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(sessionAndXsrf);
        Assert.All(sessionAndXsrf, cookie => Assert.DoesNotContain("Secure", cookie, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The framework's own antiforgery cookie used to carry no Secure flag even under forceHttps (the scheme override reaches it, but
    /// its SecurePolicy default is SameAsRequest only in name: Program.cs now says so explicitly). Every response of the sign-in
    /// round trip is checked, not only the first-run one, and for every cookie it sets, not only the two this app names.
    /// </summary>
    [Fact]
    public async Task Force_https_makes_every_set_cookie_secure_including_the_frameworks_antiforgery_cookie()
    {
        using var factory = new ForceHttpsServerFactory();
        Directory.CreateDirectory(factory.DataFolder);
        File.WriteAllText(Path.Combine(factory.DataFolder, DraughtsmanConfig.FileName), "server:\n  forceHttps: true\n");
        HttpClient client = factory.CreateClient();
        string setupCode = factory.Services.GetRequiredService<Draughtsman.Server.Auth.SetupCodeGate>().Issue();

        var responses = new List<HttpResponseMessage>
        {
            await client.GetAsync("/api/auth/status"),
            await client.PostAsJsonAsync("/api/auth/first-run", new { email = "all-cookies@example.com", displayName = "All Cookies", password = "correcthorsebattery", setupCode }),
            await client.PostAsJsonAsync("/api/auth/login", new { email = "all-cookies@example.com", password = "correcthorsebattery" }),
        };
        HttpResponseMessage logout = await client.SendAsync(WithXsrf(new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout"), responses[2]));
        responses.Add(logout);

        List<string> cookies = responses
            .SelectMany(r => r.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values) ? values : [])
            .ToList();
        Assert.Contains(cookies, c => c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));
        Assert.Contains(cookies, c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
        Assert.All(cookies, cookie => Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Without_force_https_the_antiforgery_cookie_follows_the_request_and_is_not_secure_over_plain_http()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();
        string setupCode = factory.Services.GetRequiredService<Draughtsman.Server.Auth.SetupCodeGate>().Issue();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/first-run",
            new { email = "plain-antiforgery@example.com", displayName = "Plain", password = "correcthorsebattery", setupCode });

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies));
        string antiforgery = Assert.Single(cookies!, c => c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));
        Assert.DoesNotContain("secure", antiforgery, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage WithXsrf(HttpRequestMessage request, HttpResponseMessage from)
    {
        string? token = from.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal))?["XSRF-TOKEN=".Length..]
            : null;
        if (token is not null)
        {
            request.Headers.Add(AuthTestHelpers.XsrfHeaderName, token);
        }

        return request;
    }

    private sealed class ForceHttpsServerFactory : DraughtsmanServerFactory;
}
