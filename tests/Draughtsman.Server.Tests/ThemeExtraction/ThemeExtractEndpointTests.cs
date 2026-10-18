using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.Tests.ThemeColourExtraction;

/// <summary>
/// <c>POST /api/admin/themes/extract</c> with pasted CSS (docs/api.md "Extract a draft from CSS"): a cookie-authenticated admin
/// gets a draft theme and the list of how it was chosen; nothing is saved; a draft saves through the existing create and update
/// routes unchanged; an editor, an API token (even an admin's) and an anonymous caller are refused.
/// </summary>
public class ThemeExtractEndpointTests
{
    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        return JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("Empty body.");
    }

    [Fact]
    public async Task An_admin_gets_a_draft_and_the_reasons_and_nothing_is_saved()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/themes/extract", new { css = ThemeFixtures.DarkBrandSite });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode body = await ReadJson(response);
        Assert.Equal("css", body["source"]!.GetValue<string>());
        Assert.Equal("dark", body["draft"]!["mode"]!.GetValue<string>());
        Assert.Equal("#ff6a3d", body["draft"]!["chrome"]!["dark"]!["brand"]!.GetValue<string>());
        Assert.Equal("#0b1020", body["draft"]!["chrome"]!["dark"]!["surface"]!.GetValue<string>());
        Assert.Equal("overpass", body["draft"]!["diagram"]!["base"]!.GetValue<string>());
        Assert.NotEmpty(body["reasons"]!.AsArray());
        Assert.Contains(body["reasons"]!.AsArray(), r => r!.GetValue<string>().Contains("Dark brand #ff6a3d", StringComparison.Ordinal));
        Assert.NotNull(body["adjustments"]);

        // Nothing was written: no theme exists, and the default is still the shipped one.
        JsonNode list = await ReadJson(await admin.GetAsync("/api/admin/themes"));
        Assert.Empty(list["themes"]!.AsArray());
        Assert.Null(list["defaultId"]);
    }

    [Fact]
    public async Task A_draft_becomes_a_theme_through_the_existing_create_and_update_routes_unchanged()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        JsonNode draft = (await ReadJson(await admin.PostAsJsonAsync("/api/admin/themes/extract", new { css = ThemeFixtures.CustomPropertiesOnly })))["draft"]!;

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/themes", new { name = "From a site", baseId = (string?)null });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string id = (await ReadJson(created))["id"]!.GetValue<string>();
        draft["name"] = "From a site";
        HttpResponseMessage saved = await admin.PutAsJsonAsync($"/api/admin/themes/{id}", draft);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonNode stored = await ReadJson(saved);
        Assert.Equal("#7c3aed", stored["chrome"]!["light"]!["brand"]!.GetValue<string>());
        Assert.Equal("light", stored["mode"]!.GetValue<string>());
        Assert.Equal("neutral", stored["diagram"]!["base"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_page_pasted_whole_is_read_for_its_inline_styles()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode body = await ReadJson(await admin.PostAsJsonAsync("/api/admin/themes/extract", new { css = ThemeFixtures.HtmlPage }));

        Assert.Equal("#0f766e", body["draft"]!["chrome"]!["light"]!["brand"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_missing_or_blank_body_is_a_400_and_css_with_no_colours_is_a_422_that_names_no_internals()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/themes/extract", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/themes/extract", new { css = "   " })).StatusCode);

        HttpResponseMessage none = await admin.PostAsJsonAsync("/api/admin/themes/extract", new { css = "p { margin: 0; padding: 1px } @media print { a { display: none } }" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        string text = await none.Content.ReadAsStringAsync();
        Assert.Contains("No colours could be read", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" at Draughtsman", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_over_the_extract_limit_is_refused_with_a_413_and_one_under_it_is_read()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        string under = "body{background:#112233;color:#eeeeee}" + new string(' ', 3 * 1024 * 1024);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/admin/themes/extract", new { css = under })).StatusCode);

        string over = "body{background:#112233;color:#eeeeee}" + new string(' ', 4 * 1024 * 1024 + 1024);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await admin.PostAsJsonAsync("/api/admin/themes/extract", new { css = over })).StatusCode);
    }

    [Fact]
    public async Task An_editor_an_api_token_even_an_admins_and_an_anonymous_caller_are_refused_and_a_cookie_post_needs_csrf()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        var request = new { css = ThemeFixtures.DarkBrandSite };

        await admin.PostAsJsonAsync("/api/users", new { email = "editor@example.com", displayName = "Editor", role = "Editor", password = "correcthorsebattery" });
        HttpClient editor = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync("/api/auth/login", new { email = "editor@example.com", password = "correcthorsebattery" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync("/api/admin/themes/extract", request)).StatusCode);

        string secret = (await ReadJson(await admin.PostAsJsonAsync("/api/tokens", new { name = "agent token" })))["token"]!.GetValue<string>();
        HttpClient bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.PostAsJsonAsync("/api/admin/themes/extract", request)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/admin/themes/extract", request)).StatusCode);

        admin.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/admin/themes/extract", request)).StatusCode);
    }
}
