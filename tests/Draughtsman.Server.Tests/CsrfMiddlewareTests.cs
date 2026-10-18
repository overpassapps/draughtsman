using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Auth;

namespace Draughtsman.Server.Tests;

/// <summary>CSRF protection on state-changing cookie-authenticated requests (build-spec-v0.1.md section 7).</summary>
public class CsrfMiddlewareTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public CsrfMiddlewareTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_mutating_cookie_authenticated_request_needs_the_csrf_header_and_succeeds_once_it_has_one()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string xsrf = client.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();

        client.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);
        HttpResponseMessage withoutHeader = await client.PostAsJsonAsync("/api/documents", MinimalDocument("no csrf"));
        Assert.Equal(HttpStatusCode.Forbidden, withoutHeader.StatusCode);
        JsonNode problem = JsonNode.Parse(await withoutHeader.Content.ReadAsStringAsync())!;
        Assert.Equal("CSRF validation failed", problem["title"]!.GetValue<string>());

        client.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, xsrf);
        HttpResponseMessage withHeader = await client.PostAsJsonAsync("/api/documents", MinimalDocument("with csrf"));
        Assert.Equal(HttpStatusCode.Created, withHeader.StatusCode);
    }

    [Fact]
    public async Task A_wrong_csrf_value_is_rejected_the_same_way_as_a_missing_one()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);
        client.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, "not-the-real-token");

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/documents", MinimalDocument("bad csrf"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_read_only_cookie_authenticated_request_needs_no_csrf_header()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/documents")).StatusCode);
    }

    [Fact]
    public async Task A_bearer_token_authenticated_mutating_request_never_needs_a_csrf_header()
    {
        HttpClient cookieClient = await _factory.CreateAuthenticatedClient();
        HttpResponseMessage tokenResponse = await cookieClient.PostAsJsonAsync("/api/tokens", new { name = "csrf test token" });
        string token = JsonNode.Parse(await tokenResponse.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();

        HttpClient bearerClient = _factory.CreateClient();
        bearerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage created = await bearerClient.PostAsJsonAsync("/api/documents", MinimalDocument("bearer, no csrf header at all"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    /// <summary>
    /// Security review F7: the XSRF cookie is not tied to the session cookie's own 30-day lifetime, so a browser
    /// that dropped it between visits must be able to recover a working one without signing out and back in -
    /// GET /api/auth/status reissues it for any authenticated caller.
    /// </summary>
    [Fact]
    public async Task A_lost_xsrf_cookie_is_recovered_by_checking_auth_status()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);

        HttpResponseMessage stillForbidden = await client.PostAsJsonAsync("/api/documents", MinimalDocument("no csrf yet"));
        Assert.Equal(HttpStatusCode.Forbidden, stillForbidden.StatusCode);

        HttpResponseMessage status = await client.GetAsync("/api/auth/status");
        string? recovered = status.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies)
            ? cookies.Where(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)).Select(c => c.Split(';')[0]["XSRF-TOKEN=".Length..]).SingleOrDefault()
            : null;
        Assert.NotNull(recovered);

        client.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, recovered);
        HttpResponseMessage nowWorks = await client.PostAsJsonAsync("/api/documents", MinimalDocument("recovered csrf"));
        Assert.Equal(HttpStatusCode.Created, nowWorks.StatusCode);
    }

    /// <summary>
    /// A tab signed in before an upgrade holds a pair the new server cannot read (the upgrade changed the key the framework protects them with). The
    /// refusal carries a fresh pair, so the retry works without a reload; and a request with no session never gets one.
    /// </summary>
    [Fact]
    public async Task A_refused_request_from_a_signed_in_browser_carries_a_fresh_pair_and_the_retry_with_it_succeeds()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);
        client.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, "a-pair-the-server-cannot-read");

        HttpResponseMessage refused = await client.PostAsJsonAsync("/api/documents", MinimalDocument("stale pair"));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(CsrfMiddleware.ReissuedValue, Assert.Single(refused.Headers.GetValues(CsrfMiddleware.ReissuedHeaderName)));
        string fresh = refused.Headers.GetValues("Set-Cookie").Where(c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)).Select(c => c.Split(';')[0]["XSRF-TOKEN=".Length..]).Single();
        client.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);
        client.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, Uri.UnescapeDataString(fresh));
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/documents", MinimalDocument("fresh pair"))).StatusCode);

        // Nothing is issued to a caller the session cookie did not authenticate.
        HttpClient anonymous = _factory.CreateClient();
        HttpResponseMessage unauthenticated = await anonymous.PostAsJsonAsync("/api/documents", MinimalDocument("no session"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.False(unauthenticated.Headers.Contains(CsrfMiddleware.ReissuedHeaderName));
        Assert.DoesNotContain(unauthenticated.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? set) ? set : [], c => c.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal));
    }

    private static object MinimalDocument(string title)
    {
        return new { title, type = "architecture", nodes = Array.Empty<object>(), edges = Array.Empty<object>() };
    }
}
