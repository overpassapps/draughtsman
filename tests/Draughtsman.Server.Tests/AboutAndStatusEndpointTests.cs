using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Endpoints;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The About screen and the admin status page (PR-04, PR-05; design/atlas/help-about-status.md). The promise under test
/// runs in both directions: every signed-in user can read About, an Editor gets nothing host-specific from it, and the
/// status page, which carries the data folder and the layout service's own error text, is for an administrator's
/// cookie session only.
/// </summary>
public sealed class AboutAndStatusEndpointTests : IDisposable
{
    private readonly DraughtsmanServerFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task An_anonymous_caller_reads_neither()
    {
        using HttpClient anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/about")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/status")).StatusCode);
    }

    [Fact]
    public async Task An_editor_reads_about_but_nothing_in_it_is_admin_detail()
    {
        using HttpClient admin = await _factory.CreateAuthenticatedClient();
        await admin.CreateUser("editor@example.com", "Editor");
        using HttpClient editor = await _factory.SignIn("editor@example.com");

        HttpResponseMessage response = await editor.GetAsync("/api/about");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string raw = await response.Content.ReadAsStringAsync();
        JsonNode about = JsonNode.Parse(raw)!;
        Assert.False(string.IsNullOrEmpty(about["version"]!.GetValue<string>()));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", about["buildDate"]!.GetValue<string>());
        Assert.Null(about["licenceState"]);
        Assert.Equal("draughtsman@overpass.co.uk", about["supportContact"]!.GetValue<string>());
        Assert.Equal("/THIRD-PARTY-NOTICES.txt", about["noticesUrl"]!.GetValue<string>());
        // The negative direction: the data folder and any licence path must not be anywhere in the body.
        Assert.DoesNotContain(_factory.DataFolder, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organisation", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("licence.key", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dataFolder", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_editor_is_refused_the_status_page_and_an_admin_is_not()
    {
        using HttpClient admin = await _factory.CreateAuthenticatedClient();
        await admin.CreateUser("editor@example.com", "Editor");
        using HttpClient editor = await _factory.SignIn("editor@example.com");

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/admin/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/status")).StatusCode);
    }

    [Fact]
    public async Task A_token_minted_by_an_admin_cannot_read_the_status_page()
    {
        using HttpClient admin = await _factory.CreateAuthenticatedClient();
        (_, string secret) = await admin.CreateToken("status-probe");
        using HttpClient bearer = _factory.Bearer(secret);

        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.GetAsync("/api/admin/status")).StatusCode);
    }

    [Fact]
    public async Task The_status_page_reports_the_components_the_ui_used_to_hide()
    {
        using HttpClient admin = await _factory.CreateAuthenticatedClient();

        JsonNode status = JsonNode.Parse(await admin.GetStringAsync("/api/admin/status"))!;

        Assert.Equal(Path.GetFullPath(_factory.DataFolder), Path.GetFullPath(status["dataFolder"]!.GetValue<string>()));
        Assert.Equal("sqlite", status["storage"]!["provider"]!.GetValue<string>());
        Assert.EndsWith("data.db", status["storage"]!["file"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(status["sidecar"]!["status"]!.GetValue<string>(), new[] { "running", "starting", "unavailable", "stopped" });
        Assert.False(status["pdf"]!["enabled"]!.GetValue<bool>());
        Assert.False(status["pdf"]!["available"]!.GetValue<bool>());
        Assert.False(status["ai"]!["enabled"]!.GetValue<bool>());
        Assert.Contains(status["host"]!.GetValue<string>(), new[] { "console", "container", "systemd", "launchd", "windowsService" });
        Assert.False(string.IsNullOrEmpty(status["logHint"]!.GetValue<string>()));
        Assert.Null(status["licenceState"]);
    }

    [Fact]
    public void Only_an_absolute_http_or_https_docs_address_is_ever_passed_on()
    {
        Assert.Equal("https://docs.example.com/draughtsman", AboutEndpoints.SafeHttpUrl("https://docs.example.com/draughtsman"));
        Assert.Equal("http://intranet.local/", AboutEndpoints.SafeHttpUrl("http://intranet.local"));
        Assert.Null(AboutEndpoints.SafeHttpUrl("javascript:alert(1)"));
        Assert.Null(AboutEndpoints.SafeHttpUrl("/relative/docs"));
        Assert.Null(AboutEndpoints.SafeHttpUrl(""));
        Assert.Null(AboutEndpoints.SafeHttpUrl(null));
    }
}

/// <summary>The first-run hints (PR-02): the data folder reaches the page only while there is no administrator to sign in as.</summary>
public sealed class FirstRunHintsTests : IDisposable
{
    private readonly DraughtsmanServerFactory _factory = new();

    public void Dispose()
    {
        _factory.Dispose();
    }

    [Fact]
    public async Task The_anonymous_status_names_the_data_folder_only_while_first_run_is_open()
    {
        using HttpClient anonymous = _factory.CreateClient();

        JsonNode before = JsonNode.Parse(await anonymous.GetStringAsync("/api/auth/status"))!;
        Assert.True(before["firstRunRequired"]!.GetValue<bool>());
        Assert.Equal(Path.GetFullPath(_factory.DataFolder), Path.GetFullPath(before["firstRun"]!["dataFolder"]!.GetValue<string>()));
        Assert.Contains(before["firstRun"]!["host"]!.GetValue<string>(), new[] { "console", "container", "systemd", "launchd", "windowsService" });

        using HttpClient admin = await _factory.CreateAuthenticatedClient();
        JsonNode after = JsonNode.Parse(await anonymous.GetStringAsync("/api/auth/status"))!;
        Assert.False(after["firstRunRequired"]!.GetValue<bool>());
        Assert.Null(after["firstRun"]);
        Assert.DoesNotContain(_factory.DataFolder, after.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }
}
