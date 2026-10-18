using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Theming.Extraction;
using Microsoft.AspNetCore.Http;

namespace Draughtsman.Server.Tests.ThemeColourExtraction;

/// <summary>
/// <c>POST /api/admin/themes/extract</c> with <c>{ "url": ... }</c>: off unless the operator turned on
/// <c>themes.fetchFromUrl.enabled</c>, for a cookie-authenticated administrator only, every attempt recorded in the security log with
/// the host and the outcome and nothing of the path, the query or the page, and every refusal a fixed message that echoes no address.
/// The site is a throwaway server on 127.0.0.1, reached only because the test lists that address in <c>allowedHosts</c>.
/// </summary>
public class ThemeExtractUrlEndpointTests
{
    private const string Enabled = "themes:\n  fetchFromUrl:\n    enabled: true\n    allowedHosts: [\"127.0.0.1\"]\n";

    private const string Page = """
        <!doctype html><html><head><meta name="theme-color" content="#0f766e"><link rel="stylesheet" href="/site.css">
        <style>body { background: #ffffff; color: #1c1917 }</style></head></html>
        """;

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        return JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("Empty body.");
    }

    private static Task<LoopbackSite> Site()
    {
        return LoopbackSite.Start(context => context.Request.Path == "/site.css"
            ? LoopbackSite.Css(context, ".btn { background: #0f766e; color: #fff } a { color: #0f766e }")
            : LoopbackSite.Html(context, Page));
    }

    [Fact]
    public async Task By_default_a_url_is_refused_with_a_403_the_page_says_so_and_the_attempt_is_logged()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        Assert.False((await ReadJson(await admin.GetAsync("/api/admin/themes/extract/options")))["url"]!.GetValue<bool>());
        Assert.Null((await ReadJson(await admin.GetAsync("/api/admin/themes")))["extractFromUrl"]); // the list response is unchanged
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/themes/extract", new { url = "https://example.com/pricing?x=1" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        JsonNode problem = await ReadJson(response);
        Assert.Equal("disabled", problem["reason"]!.GetValue<string>());
        Assert.Equal(ThemeFetchException.MessageFor(FetchRefusal.Disabled), problem["detail"]!.GetValue<string>());
        JsonArray events = await admin.SecurityEvents("?type=theme.url_fetch");
        JsonNode logged = Assert.Single(events)!;
        Assert.Equal("host=example.com outcome=disabled", logged["detail"]!.GetValue<string>());
        Assert.Equal("session", logged["method"]!.GetValue<string>());
        Assert.Equal(AuthTestHelpers.DefaultEmail, logged["actorLabel"]!.GetValue<string>());
    }

    [Fact]
    public async Task When_enabled_an_admin_gets_a_draft_from_the_page_and_its_stylesheet_and_the_fetch_is_logged_as_ok()
    {
        await using LoopbackSite site = await Site();
        using var factory = new ConfiguredServerFactory(Enabled);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        Assert.True((await ReadJson(await admin.GetAsync("/api/admin/themes/extract/options")))["url"]!.GetValue<bool>());
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/themes/extract", new { url = site.Origin + "/about?token=abc" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        Assert.Equal("url", body["source"]!.GetValue<string>());
        Assert.Equal("#0f766e", body["draft"]!["chrome"]!["light"]!["brand"]!.GetValue<string>());
        Assert.Equal("Theme from 127.0.0.1", body["draft"]!["name"]!.GetValue<string>());
        Assert.StartsWith("Read the page and 1 of its stylesheets.", body["reasons"]![0]!.GetValue<string>(), StringComparison.Ordinal);

        // Nothing stored: no theme was created by the fetch.
        Assert.Empty((await ReadJson(await admin.GetAsync("/api/admin/themes")))["themes"]!.AsArray());
        string detail = Assert.Single(await admin.SecurityEvents("?type=theme.url_fetch"))!["detail"]!.GetValue<string>();
        Assert.Equal("host=127.0.0.1 outcome=ok", detail);
        Assert.DoesNotContain("about", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("token", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("0f766e", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_internal_address_is_refused_with_a_fixed_message_and_nothing_reaches_the_site_and_the_log_has_the_host_only()
    {
        await using LoopbackSite site = await Site();
        using var factory = new ConfiguredServerFactory("themes:\n  fetchFromUrl:\n    enabled: true\n"); // enabled, nothing allow-listed
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage loopback = await admin.PostAsJsonAsync("/api/admin/themes/extract", new { url = site.Origin + "/" });
        HttpResponseMessage metadata = await admin.PostAsJsonAsync("/api/admin/themes/extract", new { url = "https://169.254.169.254/latest/meta-data/iam?token=secret" });

        foreach (HttpResponseMessage response in new[] { loopback, metadata })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            JsonNode problem = await ReadJson(response);
            Assert.Equal("blocked-address", problem["reason"]!.GetValue<string>());
            Assert.Equal(ThemeFetchException.MessageFor(FetchRefusal.BlockedAddress), problem["detail"]!.GetValue<string>());
        }

        Assert.Empty(site.Requests);
        string[] details = [.. (await admin.SecurityEvents("?type=theme.url_fetch")).Select(e => e!["detail"]!.GetValue<string>()).Order()];
        Assert.Equal(["host=127.0.0.1 outcome=blocked-address", "host=169.254.169.254 outcome=blocked-address"], details);
        Assert.DoesNotContain(details, d => d.Contains("meta-data", StringComparison.Ordinal) || d.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_refusal_has_its_status_its_reason_and_a_fixed_message_without_internal_detail()
    {
        await using LoopbackSite site = await LoopbackSite.Start(context =>
        {
            context.Response.StatusCode = 500;
            return context.Response.WriteAsync("internal stack trace Draughtsman.Internal.Method");
        });
        using var factory = new ConfiguredServerFactory(Enabled);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        async Task<(HttpStatusCode Status, string Reason, string Detail)> Ask(string url)
        {
            HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/themes/extract", new { url });
            JsonNode problem = await ReadJson(response);
            return (response.StatusCode, problem["reason"]!.GetValue<string>(), problem["detail"]!.GetValue<string>());
        }

        (HttpStatusCode, string, string) http = await Ask("http://example.com/");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "scheme-refused", ThemeFetchException.MessageFor(FetchRefusal.SchemeNotAllowed)), http);
        (HttpStatusCode, string, string) invalid = await Ask("https://user:pw@example.com/");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "invalid-url", ThemeFetchException.MessageFor(FetchRefusal.InvalidUrl)), invalid);
        (HttpStatusCode, string, string) failed = await Ask(site.Origin + "/");
        Assert.Equal((HttpStatusCode.BadGateway, "unreachable", ThemeFetchException.MessageFor(FetchRefusal.Unreachable)), failed);
        Assert.DoesNotContain("stack trace", failed.Item3, StringComparison.Ordinal);

        string[] outcomes = [.. (await admin.SecurityEvents("?type=theme.url_fetch")).Select(e => e!["detail"]!.GetValue<string>()).Order(StringComparer.Ordinal)];
        Assert.Equal(new[] { "host=127.0.0.1 outcome=unreachable", "host=example.com outcome=invalid-url", "host=example.com outcome=scheme-refused" }.Order(StringComparer.Ordinal), outcomes);
    }

    [Fact]
    public async Task A_page_with_no_colours_is_a_422_that_says_so_and_is_logged_as_no_colours()
    {
        await using LoopbackSite site = await LoopbackSite.Start(context => LoopbackSite.Html(context, "<html><style>p { margin: 0 }</style></html>"));
        using var factory = new ConfiguredServerFactory(Enabled);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/themes/extract", new { url = site.Origin + "/" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("No colours could be read", (await ReadJson(response))["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("host=127.0.0.1 outcome=no-colours", Assert.Single(await admin.SecurityEvents("?type=theme.url_fetch"))!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sending_both_css_and_a_url_or_neither_is_a_400_and_fetches_nothing()
    {
        await using LoopbackSite site = await Site();
        using var factory = new ConfiguredServerFactory(Enabled);
        HttpClient admin = await factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/themes/extract", new { css = "body{color:red}", url = site.Origin + "/" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/themes/extract", new { url = "  " })).StatusCode);
        Assert.Empty(site.Requests);
        Assert.Empty(await admin.SecurityEvents("?type=theme.url_fetch"));
    }

    [Fact]
    public async Task Pasting_css_works_whether_or_not_fetching_is_enabled_and_is_not_a_fetch_so_it_is_not_logged()
    {
        using var disabled = new DraughtsmanServerFactory();
        HttpClient admin = await disabled.CreateAuthenticatedClient();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/themes/extract", new { css = ThemeFixtures.DarkBrandSite });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("css", (await ReadJson(response))["source"]!.GetValue<string>());
        Assert.Empty(await admin.SecurityEvents("?type=theme.url_fetch"));
    }

    [Fact]
    public async Task An_editor_an_api_token_and_an_anonymous_caller_cannot_make_the_server_fetch_and_nothing_is_fetched()
    {
        await using LoopbackSite site = await Site();
        using var factory = new ConfiguredServerFactory(Enabled);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        var request = new { url = site.Origin + "/" };

        await admin.PostAsJsonAsync("/api/users", new { email = "editor@example.com", displayName = "Editor", role = "Editor", password = "correcthorsebattery" });
        HttpClient editor = factory.CreateClient();
        await editor.PostAsJsonAsync("/api/auth/login", new { email = "editor@example.com", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync("/api/admin/themes/extract", request)).StatusCode);

        string secret = (await ReadJson(await admin.PostAsJsonAsync("/api/tokens", new { name = "agent token" })))["token"]!.GetValue<string>();
        HttpClient bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.PostAsJsonAsync("/api/admin/themes/extract", request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/admin/themes/extract", request)).StatusCode);

        Assert.Empty(site.Requests);
        Assert.Empty(await admin.SecurityEvents("?type=theme.url_fetch"));
    }
}
