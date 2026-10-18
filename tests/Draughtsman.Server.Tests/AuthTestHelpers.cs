using System.Net;
using System.Net.Http.Json;
using Draughtsman.Server.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Signs a test <see cref="HttpClient"/> in, for server test classes written before authentication existed: every
/// protected route now needs a signed-in caller. <see cref="DefaultDisplayName"/> is deliberately "local" so
/// assertions written before this task (e.g. <c>meta.createdBy == "user:local"</c>) keep passing unchanged once a
/// test class's client goes through <see cref="CreateAuthenticatedClient"/> instead of a bare <c>CreateClient()</c>.
/// </summary>
public static class AuthTestHelpers
{
    public const string DefaultEmail = "local@example.com";
    public const string DefaultDisplayName = "local";
    public const string DefaultPassword = "test-password-1234";

    public const string XsrfHeaderName = "X-XSRF-TOKEN";

    /// <summary>
    /// Creates a client and signs it in as the one shared admin for the factory's database: first-run if this is the
    /// first test method to touch it, or login if an earlier test method (sharing the same <c>IClassFixture</c>
    /// factory and so the same database) already completed first-run. The client's cookie jar carries the session
    /// automatically; the CSRF header is set once here as a default header for every request this client makes.
    /// </summary>
    public static async Task<HttpClient> CreateAuthenticatedClient(this WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        HttpClient client = factory.CreateClient();

        // Security review F1.2: first-run needs the one-time setup code. Tests mint their own known one directly
        // from the gate rather than scraping the log - Issue() is the same method Program.cs calls at startup, so
        // this is exactly what an operator would have gotten, just handed to the test instead of printed.
        string setupCode = factory.Services.GetRequiredService<SetupCodeGate>().Issue();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/first-run",
            new { email = DefaultEmail, displayName = DefaultDisplayName, password = DefaultPassword, setupCode });
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            response = await client.PostAsJsonAsync("/api/auth/login", new { email = DefaultEmail, password = DefaultPassword });
        }

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Test sign-in failed with {response.StatusCode}: {body}");
        }

        string? xsrf = ExtractCookieValue(response, "XSRF-TOKEN");
        if (xsrf is not null)
        {
            client.DefaultRequestHeaders.Remove(XsrfHeaderName);
            client.DefaultRequestHeaders.Add(XsrfHeaderName, xsrf);
        }

        return client;
    }

    /// <summary>Reads a cookie's value straight off a response's own Set-Cookie headers, before any cookie-handling delegating handler consumes them.</summary>
    private static string? ExtractCookieValue(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return null;
        }

        foreach (string raw in values)
        {
            string namePart = raw.Split(';')[0];
            int eq = namePart.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && string.Equals(namePart[..eq], cookieName, StringComparison.Ordinal))
            {
                return namePart[(eq + 1)..];
            }
        }

        return null;
    }
}
