using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The admin branding API (M3; docs/api.md "## Branding"): upload a logo, light-on-dark logo and favicon, edit the
/// product name, reset to the chrome theme's default. Every rule is asserted in both directions - a clean image
/// passes and a malicious one is refused, an admin is allowed and an editor, an API token and an anonymous caller are
/// not - so breaking either the sanitiser or the authorization policy fails here rather than shipping.
/// </summary>
public class BrandingEndpointTests
{
    private static readonly byte[] PngBytes = Png(240, 120);
    private static readonly byte[] SvgBytes = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 40 20\"><rect width=\"40\" height=\"20\"/></svg>");
    private static readonly byte[] SvgWithScript = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
    private static readonly byte[] IcoBytes = Ico(32, 32);

    [Fact]
    public async Task An_admin_can_upload_a_logo_and_it_is_served()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage uploaded = await Upload(admin, "logo", PngBytes, "logo.png", "image/png");
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);

        JsonNode body = await ReadJson(uploaded);
        Assert.True(body["overridden"]!["logo"]!.GetValue<bool>());
        Assert.Equal("/api/theme/logo", body["logo"]!.GetValue<string>());

        // Anonymous, like the theme read the login page needs.
        HttpClient anonymous = factory.CreateClient();
        JsonNode manifest = await ReadJson(await anonymous.GetAsync("/api/theme"));
        Assert.Equal("/api/theme/logo", manifest["logo"]!.GetValue<string>());
        Assert.Equal("/api/theme/logo-light", manifest["logoLight"]!.GetValue<string>());

        HttpResponseMessage served = await anonymous.GetAsync("/api/theme/logo");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(served.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(PngBytes, await served.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_admin_can_upload_a_light_on_dark_logo_and_an_svg_is_served_with_a_restrictive_policy()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.OK, (await Upload(admin, "logo-light", SvgBytes, "mark.svg", "image/svg+xml")).StatusCode);

        HttpResponseMessage served = await factory.CreateClient().GetAsync("/api/theme/logo-light");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/svg+xml", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(served.Headers.GetValues("X-Content-Type-Options")));
        // Even a sanitiser-passing SVG is served as an image under a policy that forbids script and external loads.
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; sandbox", Assert.Single(served.Headers.GetValues("Content-Security-Policy")));
    }

    [Theory]
    [InlineData("logo")]
    [InlineData("logo-light")]
    public async Task An_uploaded_logo_is_not_replaced_by_the_themes_compact_header_mark(string slot)
    {
        using var factory = new LogoThemedServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        HttpClient anonymous = factory.CreateClient();

        // Before any upload a theme that names compact marks offers them (the shipped default names none).
        JsonNode before = await ReadJson(await anonymous.GetAsync("/api/theme"));
        Assert.Equal("/api/theme/logo-compact", before["logoCompact"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, (await Upload(admin, slot, SvgBytes, "mark.svg", "image/svg+xml")).StatusCode);

        JsonNode after = await ReadJson(await anonymous.GetAsync("/api/theme"));
        Assert.Null(after["logoCompact"]);
        Assert.Null(after["logoCompactLight"]);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/api/theme/logo-compact")).StatusCode);

        // Removing the override returns the theme's compact marks.
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/admin/branding/{slot}")).StatusCode);
        JsonNode restored = await ReadJson(await anonymous.GetAsync("/api/theme"));
        Assert.Equal("/api/theme/logo-compact-light", restored["logoCompactLight"]!.GetValue<string>());
    }

    [Fact]
    public async Task Uploading_only_a_favicon_keeps_the_themes_compact_header_mark()
    {
        using var factory = new LogoThemedServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.OK, (await Upload(admin, "favicon", SvgBytes, "f.svg", "image/svg+xml")).StatusCode);

        JsonNode manifest = await ReadJson(await factory.CreateClient().GetAsync("/api/theme"));
        Assert.Equal("/api/theme/logo-compact", manifest["logoCompact"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_admin_can_upload_an_ico_favicon()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage uploaded = await Upload(admin, "favicon", IcoBytes, "favicon.ico", "image/x-icon");
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);

        HttpResponseMessage served = await factory.CreateClient().GetAsync("/api/theme/favicon");
        Assert.Equal("image/x-icon", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(IcoBytes, await served.Content.ReadAsByteArrayAsync());
    }

    /// <summary>The SVG allowlist, proved failing one way and passing the other.</summary>
    [Fact]
    public async Task A_script_bearing_svg_logo_is_refused_but_a_clean_one_is_accepted()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage malicious = await Upload(admin, "logo", SvgWithScript, "logo.svg", "image/svg+xml");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, malicious.StatusCode);
        JsonNode problem = await ReadJson(malicious);
        Assert.Contains("script", problem["detail"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        HttpResponseMessage clean = await Upload(admin, "logo", SvgBytes, "logo.svg", "image/svg+xml");
        Assert.Equal(HttpStatusCode.OK, clean.StatusCode);
        Assert.True((await ReadJson(clean))["overridden"]!["logo"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_admin_can_set_and_reset_the_product_name()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode set = await ReadJson(await admin.PutAsJsonAsync("/api/admin/branding", new { productName = "Acme Diagrams" }));
        Assert.Equal("Acme Diagrams", set["productName"]!.GetValue<string>());
        Assert.True(set["overridden"]!["productName"]!.GetValue<bool>());
        Assert.Equal("Draughtsman", set["themeProductName"]!.GetValue<string>());
        Assert.Equal("Acme Diagrams", (await ReadJson(await factory.CreateClient().GetAsync("/api/theme")))["productName"]!.GetValue<string>());

        JsonNode reset = await ReadJson(await admin.PutAsJsonAsync("/api/admin/branding", new { productName = "" }));
        Assert.Equal("Draughtsman", reset["productName"]!.GetValue<string>());
        Assert.False(reset["overridden"]!["productName"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Removing_one_slot_and_resetting_all_return_to_the_theme_default()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await Upload(admin, "logo", PngBytes, "logo.png", "image/png");
        await Upload(admin, "favicon", IcoBytes, "favicon.ico", "image/x-icon");
        await admin.PutAsJsonAsync("/api/admin/branding", new { productName = "Acme" });

        HttpResponseMessage removed = await admin.DeleteAsync("/api/admin/branding/logo");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        JsonNode afterRemove = await ReadJson(removed);
        Assert.False(afterRemove["overridden"]!["logo"]!.GetValue<bool>());
        Assert.True(afterRemove["overridden"]!["favicon"]!.GetValue<bool>());

        JsonNode afterReset = await ReadJson(await admin.PostAsync("/api/admin/branding/reset", null));
        Assert.False(afterReset["overridden"]!["productName"]!.GetValue<bool>());
        Assert.False(afterReset["overridden"]!["favicon"]!.GetValue<bool>());
        Assert.Equal("Draughtsman", afterReset["productName"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_oversized_upload_is_413()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await Upload(admin, "logo", new byte[(512 * 1024) + 1], "big.png", "image/png");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task An_ico_as_a_logo_is_415_and_a_bad_dimension_is_400()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Upload(admin, "logo", IcoBytes, "logo.ico", "image/x-icon")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Upload(admin, "favicon", Png(512, 512), "favicon.png", "image/png")).StatusCode);
    }

    [Fact]
    public async Task An_editor_cannot_read_or_change_branding()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await admin.PostAsJsonAsync("/api/users", new { email = "editor@example.com", displayName = "Editor", role = "Editor", password = "correcthorsebattery" });
        HttpClient editor = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync("/api/auth/login", new { email = "editor@example.com", password = "correcthorsebattery" })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/admin/branding")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync("/api/admin/branding", new { productName = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(editor, "logo", PngBytes, "logo.png", "image/png")).StatusCode);
    }

    /// <summary>An API token carries its owner's role, so AdminOnly alone would let an admin's token change branding; CookieAdminOnly refuses it.</summary>
    [Fact]
    public async Task An_api_token_cannot_change_branding_even_for_an_admin()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        HttpResponseMessage tokenResponse = await admin.PostAsJsonAsync("/api/tokens", new { name = "agent token" });
        string secret = (await ReadJson(tokenResponse))["token"]!.GetValue<string>();

        HttpClient bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.GetAsync("/api/admin/branding")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.PutAsJsonAsync("/api/admin/branding", new { productName = "Nope" })).StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_and_a_cookie_write_without_csrf_is_403()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/branding")).StatusCode);

        // A signed-in cookie client that forgets the XSRF header is refused the write (CsrfMiddleware), read included.
        HttpClient admin = await factory.CreateAuthenticatedClient();
        admin.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);
        HttpResponseMessage noCsrf = await Upload(admin, "logo", PngBytes, "logo.png", "image/png");
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, string slot, byte[] bytes, string fileName, string contentType)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var form = new MultipartFormDataContent();
        form.Add(file, "file", fileName);
        return await client.PostAsync($"/api/admin/branding/{slot}", form);
    }

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body.");
    }

    private static byte[] Png(int width, int height)
    {
        byte[] bytes = new byte[24];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private static byte[] Ico(int width, int height)
    {
        byte[] bytes = new byte[22];
        bytes[2] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 1);
        bytes[6] = (byte)(width == 256 ? 0 : width);
        bytes[7] = (byte)(height == 256 ? 0 : height);
        return bytes;
    }
}
