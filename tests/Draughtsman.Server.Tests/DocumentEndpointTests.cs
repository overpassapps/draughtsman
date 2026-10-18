using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Documents;
using Draughtsman.Server.Endpoints;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>The document API contract in docs/api.md, run against SQLite in a temporary data folder.</summary>
public class DocumentEndpointTests : IClassFixture<DraughtsmanServerFactory>, IAsyncLifetime
{
    private const string ProblemJson = "application/problem+json";

    private readonly DraughtsmanServerFactory _factory;
    private HttpClient _client = null!;

    /// <summary>
    /// A second client authenticated with an API token instead of a session cookie, for the tests below that need
    /// X-Draughtsman-Actor actually honoured: only a token-authenticated request treats it as an agent-name
    /// annotation (<see cref="Draughtsman.Server.Auth.AuthenticatedActor"/>); a cookie-authenticated one ignores it.
    /// </summary>
    private HttpClient _agentClient = null!;

    public DocumentEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        _client = await _factory.CreateAuthenticatedClient();
        _agentClient = await CreateTokenClient();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    private async Task<HttpClient> CreateTokenClient()
    {
        HttpResponseMessage created = await _client.PostAsJsonAsync("/api/tokens", new { name = "test agent token" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonNode body = await ReadJson(created);
        string token = body["token"]!.GetValue<string>();

        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Create_get_replace_delete_round_trip()
    {
        HttpResponseMessage created = await Post(MinimalBody(title: "Order service"));
        JsonNode createdBody = await ReadJson(created);
        string id = createdBody["id"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"/api/documents/{id}", created.Headers.Location?.ToString());
        Assert.StartsWith(StoreIds.DocumentPrefix, id, StringComparison.Ordinal);
        Assert.Equal(1, createdBody["schemaVersion"]!.GetValue<int>());
        Assert.Equal("user:local", createdBody["meta"]!["createdBy"]!.GetValue<string>());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", createdBody["meta"]!["createdAt"]!.GetValue<string>());
        Assert.Equal(createdBody["meta"]!["createdAt"]!.GetValue<string>(), createdBody["meta"]!["updatedAt"]!.GetValue<string>());

        HttpResponseMessage fetched = await _client.GetAsync($"/api/documents/{id}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.True(JsonNode.DeepEquals(createdBody, await ReadJson(fetched)));

        HttpResponseMessage listed = await _client.GetAsync("/api/documents?q=order", CancellationToken.None);
        JsonNode page = await ReadJson(listed);
        Assert.Contains(page["items"]!.AsArray(), item => item!["id"]!.GetValue<string>() == id && item["title"]!.GetValue<string>() == "Order service");

        string replacement = MinimalBody(title: "Order service v2", id: id, createdAt: "2020-01-01T00:00:00.000Z", updatedAt: "2020-01-01T00:00:00.000Z", createdBy: "user:someone");
        HttpResponseMessage replaced = await Put(id, replacement);
        JsonNode replacedBody = await ReadJson(replaced);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal("Order service v2", replacedBody["title"]!.GetValue<string>());
        Assert.Equal("2020-01-01T00:00:00.000Z", replacedBody["meta"]!["createdAt"]!.GetValue<string>());
        Assert.NotEqual("2020-01-01T00:00:00.000Z", replacedBody["meta"]!["updatedAt"]!.GetValue<string>());

        HttpResponseMessage deleted = await _client.DeleteAsync($"/api/documents/{id}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        await AssertProblem(await _client.GetAsync($"/api/documents/{id}", CancellationToken.None), HttpStatusCode.NotFound, "Document not found");
        await AssertProblem(await _client.DeleteAsync($"/api/documents/{id}", CancellationToken.None), HttpStatusCode.NotFound, "Document not found");
    }

    [Fact]
    public async Task Rename_replaces_the_title_preserving_the_graph_and_moves_the_version()
    {
        string id = await CreateDocument("Old title");
        string before = await Load(id);
        JsonNode loaded = await ReadJson(await _client.GetAsync($"/api/documents/{id}", CancellationToken.None));

        HttpResponseMessage renamed = await Rename(id, """{"title":"New title"}""", before);
        JsonNode body = await ReadJson(renamed);

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("New title", body["title"]!.GetValue<string>());
        Assert.Equal(id, body["id"]!.GetValue<string>());
        // Everything but the title and updatedAt survives the rename unchanged.
        Assert.True(JsonNode.DeepEquals(loaded["nodes"], body["nodes"]));
        Assert.True(JsonNode.DeepEquals(loaded["edges"], body["edges"]));
        Assert.Equal(loaded["meta"]!["createdAt"]!.GetValue<string>(), body["meta"]!["createdAt"]!.GetValue<string>());
        Assert.NotEqual(before, ETagOf(renamed));

        // The list shows the new title, and the old one is gone.
        JsonNode listed = await ReadJson(await _client.GetAsync($"/api/documents", CancellationToken.None));
        Assert.Contains(listed["items"]!.AsArray(), item => item!["id"]!.GetValue<string>() == id && item["title"]!.GetValue<string>() == "New title");
    }

    [Fact]
    public async Task Rename_requires_a_base_version_and_refuses_a_stale_one()
    {
        string id = await CreateDocument("Before rename");

        var noVersion = new HttpRequestMessage(HttpMethod.Patch, $"/api/documents/{id}") { Content = JsonContent("""{"title":"No version"}""") };
        await AssertProblem(await _client.SendAsync(noVersion, CancellationToken.None), HttpStatusCode.PreconditionRequired, "Base version required");

        string stale = await Load(id);
        Assert.Equal(HttpStatusCode.OK, (await Save(id, MinimalBody("Moved on", id: id), stale)).StatusCode);

        HttpResponseMessage refused = await Rename(id, """{"title":"Stale rename"}""", stale);
        await AssertProblem(refused, HttpStatusCode.PreconditionFailed, "Document has changed");
        Assert.Equal("Moved on", (await ReadJson(await _client.GetAsync($"/api/documents/{id}", CancellationToken.None)))["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rename_rejects_a_missing_or_blank_title_and_an_unknown_document()
    {
        string id = await CreateDocument("To rename");
        string version = await Load(id);

        await AssertProblem(await Rename(id, """{"title":""}""", version), HttpStatusCode.BadRequest, "Malformed body");
        await AssertProblem(await Rename(id, """{}""", version), HttpStatusCode.BadRequest, "Malformed body");
        await AssertProblem(await Rename(id, "not json", version), HttpStatusCode.BadRequest, "Malformed body");
        await AssertProblem(await Rename("doc_missing", """{"title":"x"}""", "\"dv1:00000000000000000000000000000000\""), HttpStatusCode.NotFound, "Document not found");
    }

    [Fact]
    public async Task Rename_records_an_audit_row()
    {
        string id = await CreateDocument("Audit rename");
        string version = await Load(id);
        Assert.Equal(HttpStatusCode.OK, (await Rename(id, """{"title":"Audit renamed"}""", version)).StatusCode);

        using IServiceScope scope = _factory.Services.CreateScope();
        IReadOnlyList<AuditEntry> audit = await scope.ServiceProvider.GetRequiredService<IStore>().ListAudit(id);

        Assert.Equal([AuditActions.Created, AuditActions.Renamed], audit.Select(a => a.Action));
    }

    [Fact]
    public async Task Replace_without_meta_keeps_creation_fields_and_stamps_updated_at()
    {
        string id = await CreateDocument("Keep meta");

        HttpResponseMessage replaced = await Put(id, """{ "title": "Keep meta (edited)", "type": "flowchart", "nodes": [], "edges": [] }""");
        JsonNode body = await ReadJson(replaced);

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(id, body["id"]!.GetValue<string>());
        Assert.Equal("user:local", body["meta"]!["createdBy"]!.GetValue<string>());
        Assert.NotNull(body["meta"]!["createdAt"]);
        Assert.NotNull(body["meta"]!["updatedAt"]);
    }

    [Fact]
    public async Task Only_an_agent_write_records_a_checkpoint_version()
    {
        string id = await CreateDocument("Before");
        Assert.Empty(await Versions(id));

        await Put(id, MinimalBody(title: "After user edit", id: id));
        Assert.Empty(await Versions(id));

        HttpResponseMessage agentPut = await Save(id, MinimalBody(title: "After agent edit", id: id), await Load(id), "agent:claude-code/claude-fable-5-1");
        Assert.Equal(HttpStatusCode.OK, agentPut.StatusCode);

        JsonArray versions = await Versions(id);

        Assert.Equal([DocumentEndpoints.CheckpointVersionName], versions.Select(v => v!["name"]!.GetValue<string>()));
        Assert.Equal("agent:claude-code/claude-fable-5-1", versions[0]!["actor"]!.GetValue<string>());
        Assert.Equal("Before replace by agent:claude-code/claude-fable-5-1", versions[0]!["reason"]!.GetValue<string>());
        Assert.Null(versions[0]!["document"]);

        string checkpointId = versions[0]!["id"]!.GetValue<string>();
        HttpResponseMessage version = await _client.GetAsync($"/api/documents/{id}/versions/{checkpointId}", CancellationToken.None);
        JsonNode versionBody = await ReadJson(version);

        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        Assert.Equal("After user edit", versionBody["document"]!["title"]!.GetValue<string>());
        Assert.Equal("After agent edit", (await ReadJson(await _client.GetAsync($"/api/documents/{id}", CancellationToken.None)))["title"]!.GetValue<string>());

        await AssertProblem(await _client.GetAsync($"/api/documents/{id}/versions/v_missing", CancellationToken.None), HttpStatusCode.NotFound, "Version not found");
        await AssertProblem(await _client.GetAsync("/api/documents/doc_missing/versions", CancellationToken.None), HttpStatusCode.NotFound, "Document not found");
    }

    [Fact]
    public async Task Version_listing_pages_and_validates_its_parameters()
    {
        string id = await CreateDocument("Paged");
        for (int i = 0; i < 3; i++)
        {
            HttpResponseMessage agentPut = await Save(id, MinimalBody(title: $"Edit {i}", id: id), await Load(id), "agent:claude-code/claude-fable-5-1");
            Assert.Equal(HttpStatusCode.OK, agentPut.StatusCode);
        }

        JsonNode all = await ReadJson(await _client.GetAsync($"/api/documents/{id}/versions", CancellationToken.None));
        JsonNode page = await ReadJson(await _client.GetAsync($"/api/documents/{id}/versions?offset=1&limit=1", CancellationToken.None));

        Assert.Equal(3, all["total"]!.GetValue<int>());
        Assert.Equal(3, all["items"]!.AsArray().Count);
        Assert.Equal(0, all["offset"]!.GetValue<int>());
        Assert.Equal(50, all["limit"]!.GetValue<int>());
        Assert.Equal(3, page["total"]!.GetValue<int>());
        Assert.Single(page["items"]!.AsArray());
        Assert.Equal(1, page["offset"]!.GetValue<int>());
        Assert.Equal(1, page["limit"]!.GetValue<int>());
        Assert.Null(page["items"]![0]!["document"]);

        await AssertProblem(await _client.GetAsync($"/api/documents/{id}/versions?limit=0", CancellationToken.None), HttpStatusCode.BadRequest, "Invalid query parameter");
        await AssertProblem(await _client.GetAsync($"/api/documents/{id}/versions?limit={VersionQuery.MaxLimit + 1}", CancellationToken.None), HttpStatusCode.BadRequest, "Invalid query parameter");
        await AssertProblem(await _client.GetAsync($"/api/documents/{id}/versions?offset=-1", CancellationToken.None), HttpStatusCode.BadRequest, "Invalid query parameter");
        await AssertProblem(await _client.GetAsync($"/api/documents/{id}/versions?offset=ten", CancellationToken.None), HttpStatusCode.BadRequest, "Invalid query parameter");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("agent:claude-code/claude-fable-5-1")]
    public async Task Delete_racing_a_replace_returns_the_committed_snapshot_or_not_found(string? actor)
    {
        // Both halves of the composition can find the row gone: the agent's checkpoint insert and the replace itself.
        for (int round = 0; round < 20; round++)
        {
            string id = await CreateDocument($"Race {round}");
            string baseVersion = await Load(id);

            Task<HttpResponseMessage> replacing = Save(id, MinimalBody(title: "Raced", id: id), baseVersion, actor);
            Task<HttpResponseMessage> deleting = _client.DeleteAsync($"/api/documents/{id}", CancellationToken.None);
            HttpResponseMessage replaced = await replacing;
            HttpResponseMessage deleted = await deleting;

            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            if (replaced.StatusCode == HttpStatusCode.OK)
            {
                JsonNode committed = await ReadJson(replaced);
                Assert.Equal(id, committed["id"]!.GetValue<string>());
                Assert.Equal("Raced", committed["title"]!.GetValue<string>());
                Assert.Equal(BaseVersionHeader.ETagOf(DocumentJson.Deserialize(committed.ToJsonString())), ETagOf(replaced));
                continue;
            }

            await AssertProblem(replaced, HttpStatusCode.NotFound, "Document not found");
        }
    }

    [Fact]
    public async Task An_agent_write_between_a_load_and_a_browser_save_makes_the_save_a_refusal_not_a_silent_overwrite()
    {
        // The reported sequence: the browser has the diagram open, an agent amends it, then the browser saves the
        // copy it loaded before the agent touched it. That save used to land and take the agent's work with it.
        string id = await CreateDocument("Order service");
        string loadedByBrowser = await Load(id);

        HttpResponseMessage agentWrite = await Save(
            id,
            MinimalBody("Order service with the payments box the agent added", id: id),
            loadedByBrowser,
            "agent:claude-code/claude-fable-5-1");
        Assert.Equal(HttpStatusCode.OK, agentWrite.StatusCode);

        HttpResponseMessage browserSave = await Save(id, MinimalBody("Order service with a box nudged", id: id), loadedByBrowser);
        JsonNode problem = await AssertProblem(browserSave, HttpStatusCode.PreconditionFailed, "Document has changed");

        // The agent's work is still there, and the refusal names both versions so the caller can act on it.
        JsonNode stored = await ReadJson(await _client.GetAsync($"/api/documents/{id}", CancellationToken.None));
        Assert.Equal("Order service with the payments box the agent added", stored["title"]!.GetValue<string>());
        Assert.Equal(loadedByBrowser, problem["providedVersion"]!.GetValue<string>());
        Assert.Equal(ETagOf(agentWrite), problem["expectedVersion"]!.GetValue<string>());
        Assert.NotEqual(loadedByBrowser, problem["expectedVersion"]!.GetValue<string>());

        // Saving from the version that is actually current lands. Reaching it is the caller's business, not the guard's.
        HttpResponseMessage rebased = await Save(id, MinimalBody("Order service, both edits", id: id), await Load(id));
        Assert.Equal(HttpStatusCode.OK, rebased.StatusCode);
        Assert.Equal("Order service, both edits", (await ReadJson(rebased))["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_browser_write_between_an_agent_load_and_its_save_is_refused_the_same_way()
    {
        // The guard has to hold the agent to the rule too: it is the writer whose work vanished in the report, and
        // one protected writer and one unprotected one is the same hole with the loser swapped.
        string id = await CreateDocument("Order service");
        string loadedByAgent = await Load(id);

        Assert.Equal(HttpStatusCode.OK, (await Save(id, MinimalBody("Nudged in the browser", id: id), loadedByAgent)).StatusCode);

        HttpResponseMessage agentSave = await Save(id, MinimalBody("Amended by the agent", id: id), loadedByAgent, "agent:claude-code/claude-fable-5-1");

        await AssertProblem(agentSave, HttpStatusCode.PreconditionFailed, "Document has changed");
        Assert.Equal("Nudged in the browser", (await ReadJson(await _client.GetAsync($"/api/documents/{id}", CancellationToken.None)))["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_refused_save_writes_nothing_at_all_not_even_the_agent_checkpoint()
    {
        // The refusal comes before the composition, so a stale agent save leaves no checkpoint and no audit row
        // behind: nothing on record suggests a write happened, because none did.
        string id = await CreateDocument("Untouched");
        string stale = await Load(id);
        Assert.Equal(HttpStatusCode.OK, (await Save(id, MinimalBody("First", id: id), stale)).StatusCode);
        string afterFirst = await Load(id);

        await AssertProblem(
            await Save(id, MinimalBody("Stale agent write", id: id), stale, "agent:claude-code/claude-fable-5-1"),
            HttpStatusCode.PreconditionFailed,
            "Document has changed");

        using IServiceScope scope = _factory.Services.CreateScope();
        IReadOnlyList<AuditEntry> audit = await scope.ServiceProvider.GetRequiredService<IStore>().ListAudit(id);

        Assert.Empty(await Versions(id));
        Assert.Equal([AuditActions.Created, AuditActions.Replaced], audit.Select(a => a.Action));
        Assert.Equal(afterFirst, await Load(id));
    }

    [Fact]
    public async Task Two_saves_from_the_same_base_version_do_not_both_land()
    {
        // The check and the write are one statement in the store, so the loser of a race is refused rather than
        // slipping through the window between them: the two writers arriving together is the case that matters.
        for (int round = 0; round < 20; round++)
        {
            string id = await CreateDocument($"Simultaneous {round}");
            string shared = await Load(id);

            Task<HttpResponseMessage> browser = Save(id, MinimalBody($"Browser {round}", id: id), shared);
            Task<HttpResponseMessage> agent = Save(id, MinimalBody($"Agent {round}", id: id), shared, "agent:claude-code/claude-fable-5-1");
            HttpResponseMessage[] responses = await Task.WhenAll(browser, agent);

            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            HttpResponseMessage refused = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.OK);
            await AssertProblem(refused, HttpStatusCode.PreconditionFailed, "Document has changed");

            // Whichever won, the stored document is its whole save and not a blend of the two.
            string title = (await ReadJson(await _client.GetAsync($"/api/documents/{id}", CancellationToken.None)))["title"]!.GetValue<string>();
            Assert.Contains(title, new[] { $"Browser {round}", $"Agent {round}" });
        }
    }

    [Fact]
    public async Task A_save_that_names_no_base_version_is_refused_rather_than_taken_as_the_current_one()
    {
        string id = await CreateDocument("No base version");

        await AssertProblem(await Save(id, MinimalBody("Blind write", id: id), baseVersion: null), HttpStatusCode.PreconditionRequired, "Base version required");
        await AssertProblem(await Save(id, MinimalBody("Blind write", id: id), "*"), HttpStatusCode.BadRequest, "Invalid If-Match header");
        await AssertProblem(await Save(id, MinimalBody("Blind write", id: id), "W/not-a-tag"), HttpStatusCode.BadRequest, "Invalid If-Match header");
        await AssertProblem(await Save(id, MinimalBody("Blind write", id: id), "not-a-tag"), HttpStatusCode.BadRequest, "Invalid If-Match header");
        Assert.Equal("No base version", (await ReadJson(await _client.GetAsync($"/api/documents/{id}", CancellationToken.None)))["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_base_version_a_read_hands_out_is_the_one_a_save_has_to_quote()
    {
        HttpResponseMessage created = await Post(MinimalBody("Tagged"));
        string id = (await ReadJson(created))["id"]!.GetValue<string>();

        // A create hands out the same version a read of it would, so a caller can save without reading back first.
        Assert.Equal(ETagOf(created), await Load(id));

        JsonNode problem = await AssertProblem(
            await Save(id, MinimalBody("Refused", id: id), $"\"dv1:00000000000000000000000000000000\", {await Load(id)}"),
            HttpStatusCode.BadRequest, "Invalid If-Match header");
        Assert.Contains("exactly one base version", problem["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(ETagOf(created), await Load(id));

        HttpResponseMessage saved = await Save(id, MinimalBody("Tagged again", id: id), await Load(id));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(ETagOf(saved), await Load(id));
        Assert.NotEqual(ETagOf(created), ETagOf(saved));
    }

    [Fact]
    public async Task A_conditional_read_is_304_while_the_version_holds_and_the_whole_document_once_it_moves()
    {
        HttpResponseMessage created = await Post(MinimalBody("Watched"));
        string id = (await ReadJson(created))["id"]!.GetValue<string>();
        string loaded = ETagOf(created);

        HttpResponseMessage unchanged = await ConditionalGet(id, loaded);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal(loaded, ETagOf(unchanged));
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotModified, (await ConditionalGet(id, "*")).StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, (await ConditionalGet(id, $"\"dv1:00000000000000000000000000000000\", {loaded}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ConditionalGet(id, "\"dv1:00000000000000000000000000000000\"")).StatusCode);

        HttpResponseMessage saved = await Save(id, MinimalBody("Watched, moved on", id: id), loaded, "agent:test/model");
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // The poll that still holds the loaded version now gets the whole new document and the version to save from.
        HttpResponseMessage moved = await ConditionalGet(id, loaded);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(ETagOf(saved), ETagOf(moved));
        Assert.Equal("Watched, moved on", (await ReadJson(moved))["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotModified, (await ConditionalGet(id, ETagOf(moved))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ConditionalGet("doc_missing", loaded)).StatusCode);
    }

    [Fact]
    public async Task Audit_rows_record_each_change_with_actor_kind_and_model()
    {
        string id = await CreateDocument("Audited");
        HttpResponseMessage agentPut = await Save(id, MinimalBody(title: "Audited by agent", id: id), await Load(id), "agent:claude-code/claude-fable-5-1");
        Assert.Equal(HttpStatusCode.OK, agentPut.StatusCode);

        // An attempt to claim a different identity through the header on a cookie-authenticated request. It must be
        // ignored outright (not even validated), so the recorded actor is still the real signed-in user, not "alice".
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{id}");
        delete.Headers.Add(ActorHeader.Name, "user:alice");
        Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(delete, CancellationToken.None)).StatusCode);

        using IServiceScope scope = _factory.Services.CreateScope();
        IReadOnlyList<AuditEntry> audit = await scope.ServiceProvider.GetRequiredService<IStore>().ListAudit(id);

        Assert.Equal([AuditActions.Created, AuditActions.Replaced, AuditActions.Deleted], audit.Select(a => a.Action));
        Assert.Equal(Actor.Local, audit[0].Actor);
        Assert.Equal(ActorKind.Agent, audit[1].Actor.Kind);
        Assert.Equal("claude-code", audit[1].Actor.Name);
        Assert.Equal("claude-fable-5-1", audit[1].Actor.Model);
        Assert.Equal(Actor.Local, audit[2].Actor);
        Assert.NotEqual(new Actor(ActorKind.User, "alice"), audit[2].Actor);
        Assert.True(audit[0].At <= audit[1].At && audit[1].At <= audit[2].At);

        // Security review F2: every one of these rows, whether cookie-authenticated or token-authenticated with an
        // agent annotation layered on top, must record the real caller's own user id - never null, never "alice".
        Draughtsman.Core.Auth.User admin = await scope.ServiceProvider.GetRequiredService<Draughtsman.Core.Auth.IUserStore>()
            .GetUserByEmail(AuthTestHelpers.DefaultEmail) ?? throw new InvalidOperationException("test admin missing");
        Assert.Equal(admin.Id, audit[0].ActorUserId);
        // audit[1] is the token-authenticated, agent-annotated PUT (Save's "agent:" branch runs it on _agentClient):
        // the review's exact gap was this row losing its real owner behind the annotation.
        Assert.Equal(admin.Id, audit[1].ActorUserId);
        Assert.Equal(admin.Id, audit[2].ActorUserId);
    }

    [Fact]
    public async Task Agent_actor_becomes_created_by_when_meta_is_absent()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = JsonContent("""{ "title": "By agent", "type": "architecture", "nodes": [], "edges": [] }""") };
        request.Headers.Add(ActorHeader.Name, "agent:claude-code/claude-fable-5-1");

        // A token-authenticated caller (not _client): only there is the header honoured, as an agent-name annotation.
        HttpResponseMessage response = await _agentClient.SendAsync(request, CancellationToken.None);
        JsonNode body = await ReadJson(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("agent:claude-code/claude-fable-5-1", body["meta"]!["createdBy"]!.GetValue<string>());
        Assert.Equal("architecture", body["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cookie_authenticated_callers_actor_header_is_ignored_not_validated()
    {
        // The same malformed header that Malformed_actor_header_is_rejected below gets a 400 for, on a token
        // caller - but on a cookie-authenticated one it is only ever an inert, unvalidated stray header.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = JsonContent(MinimalBody("x")) };
        request.Headers.Add(ActorHeader.Name, "robot:hal");

        HttpResponseMessage response = await _client.SendAsync(request, CancellationToken.None);
        JsonNode body = await ReadJson(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("user:local", body["meta"]!["createdBy"]!.GetValue<string>());
    }

    [Fact]
    public async Task Malformed_actor_header_is_rejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = JsonContent(MinimalBody("x")) };
        request.Headers.Add(ActorHeader.Name, "robot:hal");

        await AssertProblem(await _agentClient.SendAsync(request, CancellationToken.None), HttpStatusCode.BadRequest, "Invalid actor header");
    }

    [Fact]
    public async Task A_user_form_actor_header_on_a_token_caller_is_rejected_not_silently_ignored()
    {
        // "user:" is not a valid annotation for a token (only agent:<name>/<model> is), so it is rejected the same
        // way a malformed header is, rather than being quietly accepted or quietly ignored.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = JsonContent(MinimalBody("x")) };
        request.Headers.Add(ActorHeader.Name, "user:alice");

        await AssertProblem(await _agentClient.SendAsync(request, CancellationToken.None), HttpStatusCode.BadRequest, "Invalid actor header");
    }

    [Fact]
    public async Task Over_long_actor_header_is_rejected_rather_than_reaching_the_store()
    {
        // audit.model is varchar(256): unbounded here would be a Postgres write error after the document was written.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = JsonContent(MinimalBody("x")) };
        request.Headers.Add(ActorHeader.Name, "agent:claude-code/" + new string('n', 300));

        await AssertProblem(await _agentClient.SendAsync(request, CancellationToken.None), HttpStatusCode.BadRequest, "Invalid actor header");
    }

    [Fact]
    public async Task Schema_violations_are_problem_json_with_errors()
    {
        HttpResponseMessage unknownMember = await Post("""{ "title": "x", "type": "flowchart", "nodes": [], "edges": [], "picture": "data:..." }""");
        JsonNode problem = await AssertProblem(unknownMember, HttpStatusCode.BadRequest, "Document is not valid");
        Assert.NotEmpty(problem["errors"]!.AsArray());

        await AssertProblem(await Post("""{ "type": "flowchart", "nodes": [], "edges": [] }"""), HttpStatusCode.BadRequest, "Document is not valid");
        await AssertProblem(await Post("""{ "title": "x", "type": "mindmap", "nodes": [], "edges": [] }"""), HttpStatusCode.BadRequest, "Document is not valid");
        await AssertProblem(await Post("""{ "title": "x", "type": "flowchart", "nodes": [], "edges": [], "meta": { "createdAt": "yesterday", "updatedAt": "now", "createdBy": "me" } }"""), HttpStatusCode.BadRequest, "Document is not valid");
        await AssertProblem(await Post("""{ "id": "bad id!", "title": "x", "type": "flowchart", "nodes": [], "edges": [] }"""), HttpStatusCode.BadRequest, "Document is not valid");
        await AssertProblem(await Post("""{ "title": "x", "type": "flowchart", "nodes": [ { "id": "n_1" } ], "edges": [] }"""), HttpStatusCode.BadRequest, "Document is not valid");
    }

    [Fact]
    public async Task Malformed_bodies_are_rejected()
    {
        await AssertProblem(await Post("{ not json"), HttpStatusCode.BadRequest, "Malformed body");
        await AssertProblem(await Post("[]"), HttpStatusCode.BadRequest, "Malformed body");
        await AssertProblem(await Post(string.Empty), HttpStatusCode.BadRequest, "Malformed body");
    }

    [Fact]
    public async Task Create_with_a_taken_id_is_a_conflict()
    {
        string id = await CreateDocument("First");

        await AssertProblem(await Post(MinimalBody("Second", id: id)), HttpStatusCode.Conflict, "Document already exists");
    }

    [Fact]
    public async Task Replace_rejects_a_mismatched_id_and_an_unknown_document()
    {
        string id = await CreateDocument("Mismatch");

        await AssertProblem(await Put(id, MinimalBody("x", id: "doc_other")), HttpStatusCode.BadRequest, "Document id mismatch");
        await AssertProblem(await Save("doc_missing", MinimalBody("x"), "\"dv1:00000000000000000000000000000000\""), HttpStatusCode.NotFound, "Document not found");
        await AssertProblem(await Put(id, "{ nope"), HttpStatusCode.BadRequest, "Malformed body");
    }

    [Fact]
    public async Task List_pages_filters_and_validates_its_parameters()
    {
        string prefix = Guid.NewGuid().ToString("N")[..8];
        for (int i = 0; i < 3; i++)
        {
            await CreateDocument($"{prefix} list {i}", $"2026-09-21T10:0{i}:00.000Z");
        }

        JsonNode all = await ReadJson(await _client.GetAsync($"/api/documents?q={prefix}", CancellationToken.None));
        JsonNode page = await ReadJson(await _client.GetAsync($"/api/documents?q={prefix}&offset=1&limit=1", CancellationToken.None));

        Assert.Equal(3, all["total"]!.GetValue<int>());
        Assert.Equal(
            [$"{prefix} list 2", $"{prefix} list 1", $"{prefix} list 0"],
            all["items"]!.AsArray().Select(item => item!["title"]!.GetValue<string>()));
        Assert.Equal(3, page["total"]!.GetValue<int>());
        Assert.Single(page["items"]!.AsArray());
        Assert.Equal(1, page["offset"]!.GetValue<int>());
        Assert.Equal(1, page["limit"]!.GetValue<int>());
        Assert.Equal($"{prefix} list 1", page["items"]![0]!["title"]!.GetValue<string>());
        Assert.Equal("flowchart", page["items"]![0]!["type"]!.GetValue<string>());

        await AssertProblem(await _client.GetAsync("/api/documents?limit=0", CancellationToken.None), HttpStatusCode.BadRequest, "Invalid query parameter");
        await AssertProblem(await _client.GetAsync($"/api/documents?limit={DocumentQuery.MaxLimit + 1}", CancellationToken.None), HttpStatusCode.BadRequest, "Invalid query parameter");
        await AssertProblem(await _client.GetAsync("/api/documents?offset=-1", CancellationToken.None), HttpStatusCode.BadRequest, "Invalid query parameter");
        await AssertProblem(await _client.GetAsync("/api/documents?offset=ten", CancellationToken.None), HttpStatusCode.BadRequest, "Invalid query parameter");
    }

    [Fact]
    public async Task Documents_survive_a_restart_of_the_server_over_the_same_data_folder()
    {
        string id = await CreateDocument("Persistent");

        using var restarted = new RestartedFactory(_factory.DataFolder);
        HttpClient restartedClient = await restarted.CreateAuthenticatedClient();
        HttpResponseMessage fetched = await restartedClient.GetAsync($"/api/documents/{id}", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal("Persistent", (await ReadJson(fetched))["title"]!.GetValue<string>());
    }

    private async Task<JsonArray> Versions(string id)
    {
        HttpResponseMessage listed = await _client.GetAsync($"/api/documents/{id}/versions", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        return (await ReadJson(listed))["items"]!.AsArray();
    }

    private async Task<string> CreateDocument(string title, string? updatedAt = null)
    {
        HttpResponseMessage response = await Post(MinimalBody(title, createdAt: updatedAt, updatedAt: updatedAt));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJson(response))["id"]!.GetValue<string>();
    }

    private Task<HttpResponseMessage> Post(string json)
    {
        return _client.PostAsync("/api/documents", JsonContent(json), CancellationToken.None);
    }

    /// <summary>A save from the version the document is at right now, which is what an unguarded save used to be.</summary>
    private async Task<HttpResponseMessage> Put(string id, string json)
    {
        return await Save(id, json, await Load(id));
    }

    /// <summary>
    /// A save written from <paramref name="baseVersion"/>, as the browser or an agent sends one. An "agent:..." actor
    /// goes out on <see cref="_agentClient"/> (a token-authenticated caller) with the header set, since that is the
    /// only way an agent-kind actor is still attributed; passing null is a plain signed-in person's save.
    /// </summary>
    private Task<HttpResponseMessage> Save(string id, string json, string? baseVersion, string? actor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{id}") { Content = JsonContent(json) };
        if (baseVersion is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", baseVersion);
        }

        if (actor is null)
        {
            return _client.SendAsync(request, CancellationToken.None);
        }

        Assert.StartsWith("agent:", actor, StringComparison.Ordinal);
        request.Headers.Add(ActorHeader.Name, actor);
        return _agentClient.SendAsync(request, CancellationToken.None);
    }

    /// <summary>A rename sent from <paramref name="baseVersion"/>, as the home screen sends one.</summary>
    private Task<HttpResponseMessage> Rename(string id, string json, string baseVersion)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/documents/{id}") { Content = JsonContent(json) };
        request.Headers.TryAddWithoutValidation("If-Match", baseVersion);
        return _client.SendAsync(request, CancellationToken.None);
    }

    /// <summary>A read that sends the version the caller already has, as an open editor polls.</summary>
    private Task<HttpResponseMessage> ConditionalGet(string id, string ifNoneMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{id}");
        request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return _client.SendAsync(request, CancellationToken.None);
    }

    /// <summary>Reads the document the way a client does, and keeps the base version the read hands out.</summary>
    private async Task<string> Load(string id)
    {
        HttpResponseMessage response = await _client.GetAsync($"/api/documents/{id}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return ETagOf(response);
    }

    private static string ETagOf(HttpResponseMessage response)
    {
        string? tag = response.Headers.ETag?.ToString();
        Assert.False(string.IsNullOrEmpty(tag), "The response carries no ETag, so a caller has no base version to save from.");
        return tag!;
    }

    private static StringContent JsonContent(string json)
    {
        return new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
    }

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync(CancellationToken.None);
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body.");
    }

    private static async Task<JsonNode> AssertProblem(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        JsonNode body = await ReadJson(response);
        Assert.Equal(title, body["title"]!.GetValue<string>());
        Assert.Equal((int)status, body["status"]!.GetValue<int>());
        return body;
    }

    private static string MinimalBody(string title, string? id = null, string? createdAt = null, string? updatedAt = null, string? createdBy = null)
    {
        var body = new JsonObject
        {
            ["title"] = title,
            ["type"] = "flowchart",
            ["nodes"] = new JsonArray(),
            ["edges"] = new JsonArray(),
        };
        if (id is not null)
        {
            body["id"] = id;
        }

        if (createdAt is not null || updatedAt is not null || createdBy is not null)
        {
            var meta = new JsonObject();
            if (createdAt is not null)
            {
                meta["createdAt"] = createdAt;
            }

            if (updatedAt is not null)
            {
                meta["updatedAt"] = updatedAt;
            }

            if (createdBy is not null)
            {
                meta["createdBy"] = createdBy;
            }

            body["meta"] = meta;
        }

        return body.ToJsonString();
    }

    /// <summary>A second server over an existing data folder; proves the data is on disk, not in the process.</summary>
    private sealed class RestartedFactory(string dataFolder) : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting(Configuration.DraughtsmanConfig.DataFolderEnvironmentVariable, dataFolder);
            builder.UseSetting(Draughtsman.Server.KeyRing.KeyRingSetup.ProtectionOverrideVariable, "none");
        }
    }
}
