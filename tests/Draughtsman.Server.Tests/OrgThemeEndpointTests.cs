using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Theming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The organisation themes API (admin Themes page; docs/api.md "## Themes"). Each rule is asserted in both
/// directions: a clean theme is saved and a hostile one is refused with nothing written, an admin may change themes
/// and an editor, an API token and an anonymous caller may not, and the default theme reaches an anonymous caller's
/// stylesheet only after an admin makes it the default, then stops reaching it when they clear it.
/// </summary>
public class OrgThemeEndpointTests
{
    private const string ShippedBrand = "#c0392b";
    private const string NewBrand = "#123abc";

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body.");
    }

    private static async Task<JsonNode> Create(HttpClient admin, string name, string? baseId = null)
    {
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/themes", new { name, baseId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJson(response);
    }

    private static async Task<HttpClient> EditorClient(DraughtsmanServerFactory factory, HttpClient admin)
    {
        await admin.PostAsJsonAsync("/api/users", new { email = "editor@example.com", displayName = "Editor", role = "Editor", password = "correcthorsebattery" });
        HttpClient editor = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync("/api/auth/login", new { email = "editor@example.com", password = "correcthorsebattery" })).StatusCode);
        return editor;
    }

    [Fact]
    public async Task An_admin_can_create_a_theme_from_the_overpass_default_and_it_is_listed_but_not_yet_applied()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode created = await Create(admin, "Acme Corp");
        Assert.Equal("acme-corp", created["id"]!.GetValue<string>());
        Assert.Equal("Acme Corp", created["name"]!.GetValue<string>());
        // A copy of the shipped Overpass theme: its brand red, in both modes, and the brand fonts.
        Assert.Equal(ShippedBrand, created["chrome"]!["dark"]!["brand"]!.GetValue<string>());
        Assert.Equal(ShippedBrand, created["chrome"]!["light"]!["brand"]!.GetValue<string>());
        Assert.Equal("JetBrains Mono, monospace", created["chrome"]!["fonts"]!["mono"]!.GetValue<string>());
        Assert.False(created["isDefault"]!.GetValue<bool>());

        JsonNode list = await ReadJson(await admin.GetAsync("/api/admin/themes"));
        Assert.Null(list["defaultId"]);
        Assert.Equal(["acme-corp"], list["themes"]!.AsArray().Select(t => t!["id"]!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task Editing_a_theme_persists_and_only_the_default_reaches_the_chrome_stylesheet()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        HttpClient anonymous = factory.CreateClient();

        JsonNode theme = await Create(admin, "Acme");
        theme["chrome"]!["dark"]!["brand"] = NewBrand;
        theme["chrome"]!["light"]!["brand"] = "#ABC";
        theme["mode"] = "light";
        HttpResponseMessage saved = await admin.PutAsJsonAsync("/api/admin/themes/acme", theme);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        JsonNode savedBody = await ReadJson(saved);
        Assert.Equal(NewBrand, savedBody["chrome"]!["dark"]!["brand"]!.GetValue<string>());
        // Colours are stored normalised to lower-case #rrggbb.
        Assert.Equal("#aabbcc", savedBody["chrome"]!["light"]!["brand"]!.GetValue<string>());

        // Not the default yet: the anonymous stylesheet is still the shipped theme's.
        string before = await anonymous.GetStringAsync("/api/theme/chrome.css");
        Assert.Contains($"--chrome-brand: {ShippedBrand};", before, StringComparison.Ordinal);
        Assert.DoesNotContain(NewBrand, before, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/themes/default", new { id = "acme" })).StatusCode);

        string after = await anonymous.GetStringAsync("/api/theme/chrome.css");
        Assert.Contains($"--chrome-brand: #aabbcc;", after, StringComparison.Ordinal);
        Assert.Contains($"--chrome-brand: {NewBrand};", after, StringComparison.Ordinal);
        Assert.DoesNotContain($"--chrome-brand: {ShippedBrand};", after, StringComparison.Ordinal);
        JsonNode manifest = await ReadJson(await anonymous.GetAsync("/api/theme"));
        Assert.Equal("Acme", manifest["name"]!.GetValue<string>());
        Assert.Equal("light", manifest["mode"]!.GetValue<string>());

        // Back to the Overpass default: the stylesheet and manifest return to the shipped theme.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/themes/default", new { id = (string?)null })).StatusCode);
        string cleared = await anonymous.GetStringAsync("/api/theme/chrome.css");
        Assert.Contains($"--chrome-brand: {ShippedBrand};", cleared, StringComparison.Ordinal);
        Assert.DoesNotContain(NewBrand, cleared, StringComparison.Ordinal);
        Assert.Equal("dark", (await ReadJson(await anonymous.GetAsync("/api/theme")))["mode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#gggggg")]
    [InlineData("rgb(0,0,0)")]
    [InlineData("url(https://evil.example/x)")]
    [InlineData("#fff;} body { display: none } :root{--x:")]
    [InlineData("#fff\n;}")]
    public async Task A_malformed_or_hostile_colour_is_refused_and_nothing_is_written(string colour)
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode theme = await Create(admin, "Acme");
        theme["chrome"]!["dark"]!["accent"] = colour;
        HttpResponseMessage response = await admin.PutAsJsonAsync("/api/admin/themes/acme", theme);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        JsonNode problem = await ReadJson(response);
        Assert.Equal("dark.accent", problem["problems"]![0]!["field"]!.GetValue<string>());

        // Refused means refused: the stored theme still has the shipped accent.
        JsonNode stored = await ReadJson(await admin.GetAsync("/api/admin/themes/acme"));
        Assert.Equal("#e04832", stored["chrome"]!["dark"]!["accent"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Inter;} body{display:none")]
    [InlineData("Inter, url(https://evil.example/f.woff)")]
    [InlineData("Inter</style><script>alert(1)</script>")]
    [InlineData("\"Inter")]
    [InlineData("Inter\\;")]
    public async Task A_hostile_font_is_refused_in_the_chrome_and_the_diagram(string font)
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode chrome = await Create(admin, "Chrome font");
        chrome["chrome"]!["fonts"]!["ui"] = font;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PutAsJsonAsync("/api/admin/themes/chrome-font", chrome)).StatusCode);

        JsonNode diagram = await Create(admin, "Diagram font");
        diagram["diagram"]!["fonts"]!["label"] = font;
        HttpResponseMessage response = await admin.PutAsJsonAsync("/api/admin/themes/diagram-font", diagram);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("diagram.fontLabel", (await ReadJson(response))["problems"]![0]!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_plain_quoted_font_list_and_a_palette_override_are_accepted()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode theme = await Create(admin, "Acme");
        theme["chrome"]!["fonts"]!["ui"] = "\"Source Sans 3\", Inter, system-ui, sans-serif";
        theme["diagram"]!["palette"] = new JsonObject { ["red"] = "#00aa55" };
        theme["diagram"]!["fonts"]!["label"] = "'Fira Sans', sans-serif";
        theme["diagram"]!["canvasBackground"] = "#101820";

        HttpResponseMessage response = await admin.PutAsJsonAsync("/api/admin/themes/acme", theme);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode saved = await ReadJson(response);
        Assert.Equal("#00aa55", saved["diagram"]!["palette"]!["red"]!.GetValue<string>());
        Assert.Equal("#101820", saved["diagram"]!["canvasBackground"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_radius_out_of_range_an_unknown_base_and_a_missing_token_are_refused_together()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode theme = await Create(admin, "Acme");
        theme["chrome"]!["radius"] = 99;
        theme["diagram"]!["base"] = "../../etc/passwd";
        ((JsonObject)theme["chrome"]!["light"]!).Remove("danger");
        HttpResponseMessage response = await admin.PutAsJsonAsync("/api/admin/themes/acme", theme);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        string[] fields = [.. (await ReadJson(response))["problems"]!.AsArray().Select(p => p!["field"]!.GetValue<string>())];
        Assert.Contains("radius", fields);
        Assert.Contains("diagram.base", fields);
        Assert.Contains("light.danger", fields);
    }

    [Fact]
    public async Task Duplicate_delete_and_name_clashes_behave()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        await Create(admin, "Acme");
        HttpResponseMessage copy = await admin.PostAsync("/api/admin/themes/acme/duplicate", null);
        Assert.Equal(HttpStatusCode.Created, copy.StatusCode);
        JsonNode copied = await ReadJson(copy);
        Assert.Equal("Acme copy", copied["name"]!.GetValue<string>());
        Assert.Equal("acme-copy", copied["id"]!.GetValue<string>());

        // Renaming one onto the other's name is refused; a unique name is fine.
        JsonNode clash = await ReadJson(await admin.GetAsync("/api/admin/themes/acme-copy"));
        clash["name"] = "acme";
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/admin/themes/acme-copy", clash)).StatusCode);
        clash["name"] = "Acme dark";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/themes/acme-copy", clash)).StatusCode);

        // The default cannot be deleted; once it is not the default it can.
        await admin.PutAsJsonAsync("/api/admin/themes/default", new { id = "acme" });
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync("/api/admin/themes/acme")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/admin/themes/acme-copy")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/themes/acme-copy")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/admin/themes/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/admin/themes/default", new { id = "nope" })).StatusCode);

        // A second organisation theme is created from a copy of the first, keeping its edits.
        JsonNode acme = await ReadJson(await admin.GetAsync("/api/admin/themes/acme"));
        acme["chrome"]!["dark"]!["brand"] = NewBrand;
        await admin.PutAsJsonAsync("/api/admin/themes/acme", acme);
        JsonNode second = await Create(admin, "Acme Labs", "acme");
        Assert.Equal(NewBrand, second["chrome"]!["dark"]!["brand"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_exported_theme_imports_back_and_is_also_a_valid_chrome_theme_file()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode theme = await Create(admin, "Acme");
        theme["chrome"]!["dark"]!["brand"] = NewBrand;
        theme["diagram"]!["palette"] = new JsonObject { ["red"] = "#00aa55" };
        theme["diagram"]!["base"] = "neutral";
        await admin.PutAsJsonAsync("/api/admin/themes/acme", theme);

        HttpResponseMessage exported = await admin.GetAsync("/api/admin/themes/acme/export");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("attachment; filename=\"acme.theme.yaml\"", exported.Content.Headers.ContentDisposition!.ToString());
        string yaml = await exported.Content.ReadAsStringAsync();
        Assert.Contains(NewBrand, yaml, StringComparison.Ordinal);

        // Import it back: a second theme (the name is taken, so it is numbered) with the same values.
        HttpResponseMessage imported = await admin.PostAsJsonAsync("/api/admin/themes/import", new { yaml });
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        JsonNode copy = await ReadJson(imported);
        Assert.Equal("Acme 2", copy["name"]!.GetValue<string>());
        Assert.Equal(NewBrand, copy["chrome"]!["dark"]!["brand"]!.GetValue<string>());
        Assert.Equal(theme["chrome"]!["light"]!["surface"]!.GetValue<string>(), copy["chrome"]!["light"]!["surface"]!.GetValue<string>());
        Assert.Equal("neutral", copy["diagram"]!["base"]!.GetValue<string>());
        Assert.Equal("#00aa55", copy["diagram"]!["palette"]!["red"]!.GetValue<string>());

        // One format: the same file is a chrome theme file the files mechanism loads, with the same brand colour.
        string themesFolder = Path.Combine(factory.DataFolder, "themes");
        Directory.CreateDirectory(themesFolder);
        await File.WriteAllTextAsync(Path.Combine(themesFolder, "acme-export.yaml"), yaml);
        LoadedChromeTheme loaded = ChromeThemeLoader.Load(factory.DataFolder, "acme-export.yaml", NullLogger.Instance);
        Assert.Equal("Acme", loaded.Theme.Name);
        Assert.Equal(NewBrand, loaded.Theme.Colors["brand"]);
    }

    [Fact]
    public async Task Importing_a_hostile_or_broken_file_is_refused_and_adds_nothing()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        const string hostile = """
            name: Evil
            productName: x
            mode: dark
            colors: { brand: "red; } body { display:none", brandText: "#fff", surface: "#000", surfaceRaised: "#111", border: "#222", text: "#fff", textMuted: "#ccc", accent: "#f00", danger: "#f00" }
            radius: 6
            font: { ui: "Inter", mono: "Mono" }
            """;
        HttpResponseMessage response = await admin.PostAsJsonAsync("/api/admin/themes/import", new { yaml = hostile });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/admin/themes/import", new { yaml = "not: [valid" })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await admin.PostAsJsonAsync("/api/admin/themes/import", new { yaml = new string('a', 70 * 1024) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/themes/import", new { })).StatusCode);

        JsonNode list = await ReadJson(await admin.GetAsync("/api/admin/themes"));
        Assert.Empty(list["themes"]!.AsArray());
    }

    [Fact]
    public async Task Every_signed_in_user_can_read_the_org_default_but_only_an_admin_can_change_themes()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await Create(admin, "Acme");
        await admin.PutAsJsonAsync("/api/admin/themes/default", new { id = "acme" });
        HttpClient editor = await EditorClient(factory, admin);

        // The editor receives the default and the themes' diagram halves, and nothing of the chrome half.
        HttpResponseMessage read = await editor.GetAsync("/api/themes");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        JsonNode body = await ReadJson(read);
        Assert.Equal("acme", body["defaultId"]!.GetValue<string>());
        Assert.Equal("overpass", body["themes"]![0]!["diagram"]!["base"]!.GetValue<string>());
        Assert.Null(body["themes"]![0]!["chrome"]);

        // Even claiming to be an agent acting for an admin does not open the admin routes: the header annotates, never authorises.
        editor.DefaultRequestHeaders.Add("X-Draughtsman-Actor", "agent:admin/claude");
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/admin/themes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync("/api/admin/themes", new { name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync("/api/admin/themes/acme", new { name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PutAsJsonAsync("/api/admin/themes/default", new { id = (string?)null })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.DeleteAsync("/api/admin/themes/acme")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsync("/api/admin/themes/acme/duplicate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/admin/themes/acme/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsJsonAsync("/api/admin/themes/import", new { yaml = "name: x" })).StatusCode);

        // And nothing changed.
        Assert.Equal("acme", (await ReadJson(await admin.GetAsync("/api/admin/themes")))["defaultId"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_api_token_cannot_manage_themes_even_for_an_admin_but_is_not_locked_out_of_reading()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await Create(admin, "Acme");
        string secret = (await ReadJson(await admin.PostAsJsonAsync("/api/tokens", new { name = "agent token" })))["token"]!.GetValue<string>();

        HttpClient bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);

        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.GetAsync("/api/admin/themes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.PostAsJsonAsync("/api/admin/themes", new { name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.PutAsJsonAsync("/api/admin/themes/default", new { id = "acme" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/themes")).StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_and_a_cookie_write_without_csrf_is_403()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/themes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/themes")).StatusCode);

        HttpClient admin = await factory.CreateAuthenticatedClient();
        admin.DefaultRequestHeaders.Remove(AuthTestHelpers.XsrfHeaderName);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/admin/themes", new { name = "No token" })).StatusCode);
    }

    [Fact]
    public void Themes_and_the_default_survive_a_restart_because_they_are_plain_files_in_the_data_folder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = new OrgThemeService(new OrgThemeStore(new DataFolder(folder), NullLogger<OrgThemeStore>.Instance), NullLogger<OrgThemeService>.Instance);
            OrgTheme acme = first.Create("Acme", null);
            first.Create("Globex", null);
            first.SetDefault(acme.Id);

            // A fresh service over the same folder, as after a restart.
            var second = new OrgThemeService(new OrgThemeStore(new DataFolder(folder), NullLogger<OrgThemeStore>.Instance), NullLogger<OrgThemeService>.Instance);
            Assert.Equal(["Acme", "Globex"], second.List.Select(t => t.Name).ToArray());
            Assert.Equal("acme", second.DefaultId);
            Assert.NotNull(second.DefaultCss);
            Assert.True(File.Exists(Path.Combine(folder, "themes", "org", "acme.yaml")));

            // Deleting removes the file, so a backup of the folder is the whole story.
            second.SetDefault(null);
            second.Delete("globex");
            Assert.False(File.Exists(Path.Combine(folder, "themes", "org", "globex.yaml")));
            Assert.Null(second.DefaultCss);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void A_hand_edited_theme_file_with_an_unsafe_value_is_skipped_not_served()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new OrgThemeService(new OrgThemeStore(new DataFolder(folder), NullLogger<OrgThemeStore>.Instance), NullLogger<OrgThemeService>.Instance);
            service.Create("Good", null);
            string good = File.ReadAllText(Path.Combine(folder, "themes", "org", "good.yaml"));
            File.WriteAllText(Path.Combine(folder, "themes", "org", "bad.yaml"), good.Replace("#c0392b", "red;}*{display:none", StringComparison.Ordinal));

            var restarted = new OrgThemeService(new OrgThemeStore(new DataFolder(folder), NullLogger<OrgThemeStore>.Instance), NullLogger<OrgThemeService>.Instance);
            Assert.Equal(["Good"], restarted.List.Select(t => t.Name).ToArray());
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
