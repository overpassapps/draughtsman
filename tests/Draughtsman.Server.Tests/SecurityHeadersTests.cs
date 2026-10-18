using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Draughtsman.Server.Hardening;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Draughtsman.Server.Tests;

/// <summary>
/// AU-13: CSP, frame-ancestors, nosniff, Referrer-Policy and no-store on the API, on every kind of response, without
/// overwriting what a route chose for itself. (That the real SPA still loads and works under this policy is the browser
/// smoke test, scripts/security-headers-smoke.mjs; a header assertion cannot tell.)
/// </summary>
public class SecurityHeadersTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public SecurityHeadersTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/theme")]
    [InlineData("/api/auth/status")]
    [InlineData("/api/documents")]
    [InlineData("/api/no-such-route")]
    public async Task Every_api_response_carries_the_full_set_and_is_not_storable(string path)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(path);

        AssertFullSet(response);
        // Some routes (the antiforgery-issuing auth status) already say "no-store, no-cache"; any form of no-store will do.
        Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: " + response.Headers.CacheControl);
    }

    [Fact]
    public async Task Api_responses_that_set_no_policy_of_their_own_ask_intermediaries_not_to_transform_them()
    {
        // A request to a CDN or gateway to leave the payload (and so its ETag) alone, not a guarantee that it does: a
        // document's version also travels in X-Document-Version for the intermediary that compresses it anyway.
        HttpClient client = await _factory.CreateAuthenticatedClient();
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", new { title = "Untransformed", type = "flowchart", nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
        string id = (await created.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>())!["id"]!.GetValue<string>();

        foreach (HttpResponseMessage response in new[] { created, await client.GetAsync($"/api/documents/{id}"), await client.GetAsync("/api/documents"), await _factory.CreateClient().GetAsync("/api/documents") })
        {
            Assert.True(response.Headers.CacheControl?.NoStore, "Cache-Control: " + response.Headers.CacheControl);
            Assert.True(response.Headers.CacheControl?.NoTransform, "Cache-Control: " + response.Headers.CacheControl);
        }
    }

    [Fact]
    public async Task An_unauthenticated_401_carries_them_too()
    {
        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/api/documents");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        AssertFullSet(response);
    }

    [Fact]
    public async Task The_policy_forbids_framing_plugins_and_a_base_element_and_allows_only_the_spa_needs()
    {
        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/api/health");
        string csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));

        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        // The one concession, found by the browser smoke test: Ajv compiles the schema with new Function. Never inline script.
        Assert.Contains("script-src 'self' 'unsafe-eval';", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("https:", csp, StringComparison.Ordinal);
        Assert.DoesNotContain(" *", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_route_that_chose_its_own_cache_policy_and_csp_keeps_them()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        using var upload = new MultipartFormDataContent();
        var svg = new ByteArrayContent("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"1\" height=\"1\"/></svg>"u8.ToArray());
        svg.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
        upload.Add(svg, "file", "a.svg");
        HttpResponseMessage uploaded = await client.PostAsync("/api/assets", upload);
        string id = (await uploaded.Json())["id"]!.GetValue<string>();

        HttpResponseMessage served = await client.GetAsync($"/api/assets/{id}");

        Assert.Equal("max-age=31536000, private, immutable", served.Headers.CacheControl?.ToString());
        Assert.Equal(Endpoints.AssetEndpoints.SvgContentSecurityPolicy, Assert.Single(served.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(served.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task The_spa_shell_and_its_static_files_carry_the_policy_but_are_not_marked_no_store()
    {
        string contentRoot = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        string dist = Path.Combine(contentRoot, "web", "dist");
        Directory.CreateDirectory(Path.Combine(dist, "assets"));
        await File.WriteAllTextAsync(Path.Combine(dist, "index.html"), "<!doctype html><title>SPA</title>", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(dist, "assets", "app.js"), "console.log(1)", CancellationToken.None);
        try
        {
            using WebApplicationFactory<Program> spa = _factory.WithWebHostBuilder(builder => builder.UseContentRoot(contentRoot));
            HttpClient client = spa.CreateClient();

            HttpResponseMessage route = await client.GetAsync("/documents/doc_1");
            HttpResponseMessage script = await client.GetAsync("/assets/app.js");

            foreach (HttpResponseMessage response in new[] { route, script })
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                AssertFullSet(response);
                Assert.NotEqual("no-store", response.Headers.CacheControl?.ToString());
            }
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    private static void AssertFullSet(HttpResponseMessage response)
    {
        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal(SecurityHeadersMiddleware.PermissionsPolicy, Assert.Single(response.Headers.GetValues("Permissions-Policy")));
    }
}
