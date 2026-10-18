using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.Tests;

/// <summary>Who may read the two audit trails (admin, in a browser session only), what the filters and paging do, and that nothing here writes.</summary>
public class SecurityEventAccessTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public SecurityEventAccessTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Only_an_admin_with_a_browser_session_can_read_either_trail()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        await admin.CreateUser("just-an-editor@example.com");
        HttpClient editor = await _factory.SignIn("just-an-editor@example.com");
        (_, string adminTokenSecret) = await admin.CreateToken("admin token");
        HttpClient adminBearer = _factory.Bearer(adminTokenSecret);
        HttpClient anonymous = _factory.CreateClient();

        foreach (string url in new[] { "/api/admin/security-events", "/api/admin/audit?documentId=doc_x" })
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync(url)).StatusCode);
            // A token minted by an admin carries the admin role, but the trail is cookie-only (security review F3).
            Assert.Equal(HttpStatusCode.Forbidden, (await adminBearer.GetAsync(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task There_is_no_way_to_write_or_delete_an_event_through_the_api()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        foreach (HttpMethod method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            HttpResponseMessage response = await admin.SendAsync(AuthBarHelpers.Request(method, "/api/admin/security-events", new { type = "x" }, admin));
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} answered {response.StatusCode}");
        }
    }

    [Fact]
    public async Task Paging_and_type_and_time_filters_narrow_the_result_and_bad_parameters_are_refused()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("filter-target@example.com");
        await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Patch, $"/api/users/{id}", new { role = "Admin" }, admin));
        await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Patch, $"/api/users/{id}", new { active = false }, admin));

        JsonNode all = await (await admin.GetAsync($"/api/admin/security-events?user={id}")).Json();
        Assert.Equal(3, all["total"]!.GetValue<int>());

        JsonNode page = await (await admin.GetAsync($"/api/admin/security-events?user={id}&limit=1&offset=1")).Json();
        Assert.Equal(3, page["total"]!.GetValue<int>());
        JsonNode only = Assert.Single(page["items"]!.AsArray())!;
        Assert.Equal("user.role_changed", only["type"]!.GetValue<string>());
        Assert.Equal(1, page["limit"]!.GetValue<int>());

        Assert.Single(await admin.SecurityEvents($"?user={id}&type=user.deactivated,password.reset"));
        Assert.Equal(2, (await admin.SecurityEvents($"?user={id}&type=user.deactivated,user.created")).Count);

        string future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("o"));
        string past = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("o"));
        Assert.Empty(await admin.SecurityEvents($"?user={id}&since={future}"));
        Assert.Equal(3, (await admin.SecurityEvents($"?user={id}&since={past}")).Count);
        Assert.Empty(await admin.SecurityEvents($"?user={id}&until={past}"));

        foreach (string bad in new[] { "?limit=0", "?limit=201", "?offset=-1", "?limit=abc", "?since=not-a-date" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/security-events" + bad)).StatusCode);
        }
    }

    [Fact]
    public async Task The_document_audit_read_returns_a_documents_rows_newest_first_and_requires_an_id()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/documents", new { title = "Audited", type = "freeform", nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string docId = (await created.Json())["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Delete, $"/api/documents/{docId}", withCsrfFrom: admin))).StatusCode);

        JsonNode audit = await (await admin.GetAsync($"/api/admin/audit?documentId={docId}")).Json();
        string[] actions = audit["items"]!.AsArray().Select(a => a!["action"]!.GetValue<string>()).ToArray();
        // Survives the document's own deletion, newest first.
        Assert.Equal(["deleted", "created"], actions);
        Assert.Equal(2, audit["total"]!.GetValue<int>());
        Assert.Equal("user", audit["items"]![0]!["actorKind"]!.GetValue<string>());
        Assert.NotNull(audit["items"]![0]!["actorUserId"]);

        Assert.Empty((await (await admin.GetAsync("/api/admin/audit?documentId=doc_never_existed")).Json())["items"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/audit")).StatusCode);
    }
}
