using System.Net;
using System.Net.Http.Json;
using Draughtsman.Server.Auth.PasswordReset;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Where a reset link could leak, and the proof that it does not: the framework's access log at its most talkative, every other log
/// line, the security log, the response headers around the reset page, and the shape of the link itself. Each assertion is paired
/// with a control that shows the same search would have found the secret if it had been there.
/// </summary>
public class PasswordResetLeakTests
{
    private const string TalkativeYaml = """
        server:
          publicUrl: https://draughtsman.test
        email:
          provider: file
          from: noreply@draughtsman.test
        logging:
          level: Trace
          frameworkLevel: Trace
        """;

    [Fact]
    public async Task The_secret_of_a_link_is_in_no_log_line_even_with_every_level_of_logging_on()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new ResetServerFactory(TalkativeYaml, logs: logs, spa: true);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("leaky@example.com");
        HttpClient browser = factory.CreateClient();

        (_, string? adminSecret) = await admin.CreateLink(id);
        await browser.RequestReset("leaky@example.com", ip: "10.9.0.1");
        await factory.WaitForMail();
        string emailedSecret = Assert.Single(factory.ReadOutbox()).Secret!;
        string link = Assert.Single(factory.ReadOutbox()).Link!;
        // What a browser does with the emailed link: it opens the page, and the fragment stays in the browser. (The link names the public host;
        // a browser would send it there, so the test sends the same path to this server.)
        HttpResponseMessage page = await browser.GetAsync(new Uri(link).AbsolutePath);
        await browser.Complete("dpr_junk_that_was_never_issued");
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Complete(adminSecret)).StatusCode);
        await browser.Complete(emailedSecret);
        await admin.GetAsync("/api/admin/security-events?limit=200");

        string everything = string.Join('\n', logs.Lines.Select(l => l.Message));

        // The control: the log really was recording requests, including this one, so a secret in an address would have shown.
        Assert.Contains("Request starting", everything, StringComparison.Ordinal);
        Assert.Contains("/api/auth/password-reset/complete", everything, StringComparison.Ordinal);
        Assert.Contains("/reset-password", everything, StringComparison.Ordinal);
        foreach (string secret in new[] { adminSecret!, emailedSecret, "dpr_junk_that_was_never_issued" })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
            Assert.DoesNotContain(secret[4..], everything, StringComparison.Ordinal);
            Assert.DoesNotContain(PasswordResetSecrets.Hash(secret), everything, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("token=dpr_", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(PasswordResetHelpers.NewPassword, everything, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Draughtsman test shell", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_address_a_browser_requests_for_the_page_has_no_secret_in_it()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new ResetServerFactory(TalkativeYaml, logs: logs, spa: true);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        (HttpResponseMessage created, string? secret) = await admin.CreateLink(await admin.CreateUser("fragment@example.com"));
        string link = (await created.Json())["link"]!.GetValue<string>();

        // The link names the public host; a browser sends the request there, here it goes to the test server with the same path and fragment.
        HttpClient client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(client.BaseAddress!, "/reset-password#" + new Uri(link).Fragment.TrimStart('#')));
        await client.SendAsync(request);

        // The framework strips the fragment before the request leaves the client, so the server's view of it is just the path...
        Assert.Equal("/reset-password", request.RequestUri!.AbsolutePath);
        Assert.Equal(string.Empty, request.RequestUri.Query);
        Assert.Contains(logs.Lines, l => l.Message.Contains("GET", StringComparison.Ordinal) && l.Message.Contains("/reset-password", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Lines, l => l.Message.Contains(secret!, StringComparison.Ordinal));
        // ...which is why the link puts it there: in the fragment, never the path or the query.
        var uri = new Uri(link);
        Assert.Equal("/reset-password", uri.AbsolutePath);
        Assert.Equal(string.Empty, uri.Query);
        Assert.Equal($"#token={secret}", uri.Fragment);
    }

    [Theory]
    [InlineData("https://draughtsman.example.com")]
    [InlineData("http://localhost:5180")]
    [InlineData("https://example.com/sub/path")]
    public void A_link_always_carries_the_secret_in_the_fragment_and_nowhere_else(string publicUrl)
    {
        string secret = PasswordResetSecrets.NewSecret();

        var uri = new Uri(PasswordResetSecrets.BuildLink(publicUrl, secret));

        Assert.EndsWith("/reset-password", uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(string.Empty, uri.Query);
        Assert.DoesNotContain(secret, uri.AbsolutePath + uri.Query, StringComparison.Ordinal);
        Assert.Equal($"#token={secret}", uri.Fragment);
    }

    [Fact]
    public void A_secret_is_thirty_two_random_bytes_under_a_prefix_and_never_repeats()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 200; i++)
        {
            string secret = PasswordResetSecrets.NewSecret();

            Assert.StartsWith("dpr_", secret, StringComparison.Ordinal);
            string body = secret[4..];
            Assert.Equal(43, body.Length);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", body);
            Assert.Equal(32, Convert.FromBase64String(body.Replace('-', '+').Replace('_', '/') + "=").Length);
            Assert.True(PasswordResetSecrets.IsWellFormed(secret));
            Assert.True(seen.Add(secret), "a secret was issued twice");
        }

        // Only a SHA-256 of it is ever kept.
        string hash = PasswordResetSecrets.Hash("dpr_example");
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.NotEqual(PasswordResetSecrets.Hash("dpr_example2"), hash);
    }

    [Fact]
    public async Task The_reset_page_and_its_calls_carry_the_hardening_headers_that_keep_the_secret_from_travelling()
    {
        using var factory = new ResetServerFactory(ResetServerFactory.EmailYaml, spa: true);
        HttpClient browser = factory.CreateClient();

        HttpResponseMessage page = await browser.GetAsync("/reset-password");
        HttpResponseMessage complete = await browser.Complete("dpr_junk");
        HttpResponseMessage request = await browser.RequestReset("someone@example.com");

        foreach (HttpResponseMessage response in new[] { page, complete, request })
        {
            Assert.Equal("same-origin", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            string csp = response.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
            Assert.Contains("connect-src 'self'", csp, StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
            Assert.Contains("form-action 'self'", csp, StringComparison.Ordinal);
        }

        // no-store is the point; the API also adds no-transform (a request to intermediaries, see SecurityHeadersMiddleware).
        Assert.True(complete.Headers.CacheControl?.NoStore, "Cache-Control: " + complete.Headers.CacheControl);
        Assert.True(request.Headers.CacheControl?.NoStore, "Cache-Control: " + request.Headers.CacheControl);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Draughtsman test shell", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await factory.WaitForMail();
    }

    [Fact]
    public async Task The_response_that_hands_an_administrator_a_secret_is_not_cacheable()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("cache@example.com");

        (HttpResponseMessage created, _) = await admin.CreateLink(id);

        Assert.True(created.Headers.CacheControl?.NoStore, "Cache-Control: " + created.Headers.CacheControl);
        Assert.False(created.Headers.Contains("Location"), "a Location header would put the secret's neighbourhood in a redirect target");
    }

    [Fact]
    public async Task A_reset_request_cannot_be_made_from_another_origin_with_a_session_cookie_riding_along()
    {
        using var factory = new ResetServerFactory(ResetServerFactory.EmailYaml);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("cross-site@example.com");
        admin.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);

        // A signed-in browser's session cookie without the anti-forgery header: what a forged cross-site POST looks like.
        HttpResponseMessage forged = await admin.PostAsJsonAsync("/api/auth/password-reset/complete", new { token = "dpr_x", newPassword = PasswordResetHelpers.NewPassword });
        HttpResponseMessage forgedLink = await admin.PostAsync($"/api/users/{id}/password-reset-link", null);

        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forgedLink.StatusCode);
    }
}
