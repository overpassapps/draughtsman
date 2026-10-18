using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// PR-08 and PR-06 over HTTP: naming a snapshot, comparing two revisions, restoring one (checkpointing first, so the restore is
/// itself undoable), the critique, duplicating a document, and the list's search, sort and type filter.
/// </summary>
public sealed class DocumentHistoryEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public DocumentHistoryEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Naming_a_snapshot_lists_it_with_its_size_and_the_reserved_or_empty_names_are_refused()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await CreateWithNodes(admin, "Snapshots", ["Start", "Finish"]);

        HttpResponseMessage named = await admin.PostAsJsonAsync($"/api/documents/{id}/versions", new { name = "  Before the rework  ", reason = "Review with the team" });

        Assert.Equal(HttpStatusCode.Created, named.StatusCode);
        JsonNode summary = await named.Json();
        Assert.Equal("Before the rework", summary["name"]!.GetValue<string>());
        Assert.Equal("Review with the team", summary["reason"]!.GetValue<string>());
        Assert.True(summary["size"]!.GetValue<int>() > 100);
        Assert.Equal("user:local", summary["actor"]!.GetValue<string>());
        JsonNode listed = Assert.Single((await (await admin.GetAsync($"/api/documents/{id}/versions")).Json())["items"]!.AsArray())!;
        Assert.Equal(summary["id"]!.GetValue<string>(), listed["id"]!.GetValue<string>());
        Assert.Equal(summary["size"]!.GetValue<int>(), listed["size"]!.GetValue<int>());

        foreach (object bad in new object[] { new { name = "" }, new { name = "   " }, new { }, new { name = "checkpoint" }, new { name = "CheckPoint" }, new { name = new string('x', 257) } })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/documents/{id}/versions", bad)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/documents/doc_missing/versions", new { name = "x" })).StatusCode);
        Assert.Single((await (await admin.GetAsync($"/api/documents/{id}/versions")).Json())["items"]!.AsArray());
    }

    [Fact]
    public async Task Restoring_a_version_checkpoints_first_so_the_restore_can_itself_be_undone()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await CreateWithNodes(admin, "Restorable", ["Start", "Finish"]);
        JsonNode v1 = await (await admin.PostAsJsonAsync($"/api/documents/{id}/versions", new { name = "v1" })).Json();
        string v1Id = v1["id"]!.GetValue<string>();
        JsonNode edited = await SaveWithNodes(admin, id, "Restorable, reworked", ["Start", "Finish", "Extra"]);
        Assert.Equal(3, edited["nodes"]!.AsArray().Count);

        (string tag, JsonNode current) = await Read(admin, id);
        HttpResponseMessage restored = await admin.SendAsync(Restore(admin, id, v1Id, tag));

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        JsonNode body = await restored.Json();
        Assert.Equal("Restorable", body["title"]!.GetValue<string>());
        Assert.Equal(["Start", "Finish"], body["nodes"]!.AsArray().Select(n => n!["label"]!.GetValue<string>()));
        // Its identity and creation header are the document's own, and the save moved the version a later save must quote.
        Assert.Equal(id, body["id"]!.GetValue<string>());
        Assert.Equal(current["meta"]!["createdAt"]!.GetValue<string>(), body["meta"]!["createdAt"]!.GetValue<string>());
        Assert.NotEqual(tag, restored.Headers.ETag!.ToString());
        (string afterTag, JsonNode stored) = await Read(admin, id);
        Assert.Equal(restored.Headers.ETag!.ToString(), afterTag);
        Assert.True(JsonNode.DeepEquals(body, stored));

        // The snapshot it replaced is a checkpoint, newest first, that says what it was taken for.
        JsonArray versions = (await (await admin.GetAsync($"/api/documents/{id}/versions")).Json())["items"]!.AsArray();
        Assert.Equal(2, versions.Count);
        Assert.Equal("checkpoint", versions[0]!["name"]!.GetValue<string>());
        Assert.Contains("Before restoring \"v1\"", versions[0]!["reason"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonNode checkpoint = await (await admin.GetAsync($"/api/documents/{id}/versions/{versions[0]!["id"]!.GetValue<string>()}")).Json();
        Assert.Equal("Restorable, reworked", checkpoint["document"]!["title"]!.GetValue<string>());

        // Undo: restoring that checkpoint puts the reworked document back.
        HttpResponseMessage undone = await admin.SendAsync(Restore(admin, id, versions[0]!["id"]!.GetValue<string>(), afterTag));
        Assert.Equal(HttpStatusCode.OK, undone.StatusCode);
        JsonNode undoneBody = await undone.Json();
        Assert.Equal("Restorable, reworked", undoneBody["title"]!.GetValue<string>());
        Assert.Equal(3, undoneBody["nodes"]!.AsArray().Count);

        using IServiceScope scope = _factory.Services.CreateScope();
        IReadOnlyList<AuditEntry> audit = await scope.ServiceProvider.GetRequiredService<IStore>().ListAudit(id);
        Assert.Equal([AuditActions.Created, AuditActions.Snapshotted, AuditActions.Replaced, AuditActions.VersionRestored, AuditActions.VersionRestored], audit.Select(a => a.Action));
    }

    [Fact]
    public async Task A_restore_from_a_stale_view_or_with_no_base_version_changes_nothing()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await CreateWithNodes(admin, "Guarded restore", ["One"]);
        string v1 = (await (await admin.PostAsJsonAsync($"/api/documents/{id}/versions", new { name = "v1" })).Json())["id"]!.GetValue<string>();
        (string staleTag, _) = await Read(admin, id);
        await SaveWithNodes(admin, id, "Guarded restore, moved on", ["One", "Two"]);

        HttpResponseMessage stale = await admin.SendAsync(Restore(admin, id, v1, staleTag));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        HttpResponseMessage unguarded = await admin.SendAsync(Restore(admin, id, v1, null));
        Assert.Equal(HttpStatusCode.PreconditionRequired, unguarded.StatusCode);

        (_, JsonNode stored) = await Read(admin, id);
        Assert.Equal("Guarded restore, moved on", stored["title"]!.GetValue<string>());
        // Refused before it checkpointed anything: the only version is the one that was named.
        Assert.Single((await (await admin.GetAsync($"/api/documents/{id}/versions")).Json())["items"]!.AsArray());

        (string tag, _) = await Read(admin, id);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(Restore(admin, id, "v_missing", tag))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(Restore(admin, "doc_missing", v1, tag))).StatusCode);
        Assert.Single((await (await admin.GetAsync($"/api/documents/{id}/versions")).Json())["items"]!.AsArray());
    }

    [Fact]
    public async Task A_version_of_a_binned_document_cannot_be_restored_or_read()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await CreateWithNodes(admin, "Binned history", ["One"]);
        string v1 = (await (await admin.PostAsJsonAsync($"/api/documents/{id}/versions", new { name = "v1" })).Json())["id"]!.GetValue<string>();
        (string tag, _) = await Read(admin, id);
        await admin.DeleteAsync($"/api/documents/{id}");

        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(Restore(admin, id, v1, tag))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{id}/versions/{v1}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{id}/diff?from={v1}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/documents/{id}/versions", new { name = "late" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{id}/critique")).StatusCode);
    }

    [Fact]
    public async Task Compare_reports_what_was_added_removed_and_changed_between_a_version_and_the_current_document()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await CreateWithNodes(admin, "Comparable", ["Keep", "Rename me", "Drop me"]);
        string v1 = (await (await admin.PostAsJsonAsync($"/api/documents/{id}/versions", new { name = "v1" })).Json())["id"]!.GetValue<string>();
        JsonNode original = (await Read(admin, id)).Document;
        string[] ids = original["nodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()).ToArray();

        // Rename the second node, drop the third, add a fourth.
        JsonNode next = original.DeepClone();
        JsonArray nodes = next["nodes"]!.AsArray();
        nodes[1]!["label"] = "Renamed";
        nodes.RemoveAt(2);
        nodes.Add(new JsonObject { ["id"] = "n_new", ["kind"] = "process", ["label"] = "Brand new", ["shape"] = "rectangle" });
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(Put(admin, id, next, (await Read(admin, id)).Tag))).StatusCode);

        JsonNode diff = await (await admin.GetAsync($"/api/documents/{id}/diff?from={v1}")).Json();

        Assert.Equal(v1, diff["fromVersion"]!.GetValue<string>());
        Assert.Equal("current", diff["toVersion"]!.GetValue<string>());
        JsonNode result = diff["diff"]!;
        Assert.Equal(["n_new"], result["addedNodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        Assert.Equal([ids[2]], result["removedNodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        JsonNode changed = Assert.Single(result["changedNodes"]!.AsArray())!;
        Assert.Equal(ids[1], changed["id"]!.GetValue<string>());
        Assert.Equal("label", Assert.Single(changed["fields"]!.AsArray())!["field"]!.GetValue<string>());
        Assert.Empty(result["addedEdges"]!.AsArray());

        // The same pair the other way round swaps added and removed; a version against itself is empty.
        JsonNode reverse = await (await admin.GetAsync($"/api/documents/{id}/diff?from=current&to={v1}")).Json();
        Assert.Equal([ids[2]], reverse["diff"]!["addedNodes"]!.AsArray().Select(n => n!["id"]!.GetValue<string>()));
        JsonNode same = await (await admin.GetAsync($"/api/documents/{id}/diff?from={v1}&to={v1}")).Json();
        Assert.Empty(same["diff"]!["addedNodes"]!.AsArray());
        Assert.Empty(same["diff"]!["changedNodes"]!.AsArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/documents/{id}/diff")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{id}/diff?from=v_missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{id}/diff?from={v1}&to=v_missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/documents/doc_missing/diff?from=current")).StatusCode);
    }

    [Fact]
    public async Task Compare_reports_a_retitle_and_each_changed_prop_key_and_keeps_the_saves_bookkeeping_apart()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await CreateWithNodes(admin, "Header diff", ["One"]);
        string v1 = (await (await admin.PostAsJsonAsync($"/api/documents/{id}/versions", new { name = "v1" })).Json())["id"]!.GetValue<string>();
        JsonNode next = (await Read(admin, id)).Document.DeepClone();
        next["title"] = "Header diff, renamed";
        next["nodes"]![0]!["props"] = new JsonObject { ["owner"] = "platform", ["tier"] = 1 };
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(Put(admin, id, next, (await Read(admin, id)).Tag))).StatusCode);

        JsonNode result = (await (await admin.GetAsync($"/api/documents/{id}/diff?from={v1}")).Json())["diff"]!;

        JsonNode title = Assert.Single(result["documentChanges"]!.AsArray())!;
        Assert.Equal("title", title["field"]!.GetValue<string>());
        Assert.Equal("Header diff", title["from"]!.GetValue<string>());
        Assert.Equal("Header diff, renamed", title["to"]!.GetValue<string>());
        JsonNode changed = Assert.Single(result["changedNodes"]!.AsArray())!;
        Assert.Equal(["props"], changed["fields"]!.AsArray().Select(f => f!["field"]!.GetValue<string>()));
        Assert.Equal(["owner", "tier"], changed["props"]!.AsArray().Select(f => f!["field"]!.GetValue<string>()));
        Assert.All(changed["props"]!.AsArray(), f => Assert.Null(f!["from"]));
        Assert.All(result["volatileChanges"]!.AsArray(), f => Assert.StartsWith("meta.", f!["field"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Critique_returns_the_same_findings_the_analyser_gives_for_the_stored_document_and_for_a_posted_one()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await CreateWithNodes(admin, "Critiqued", ["Alone"]);

        JsonNode stored = await (await admin.GetAsync($"/api/documents/{id}/critique")).Json();

        JsonNode finding = Assert.Single(stored["findings"]!.AsArray(), f => f!["rule"]!.GetValue<string>() == "isolated-node")!;
        Assert.Single(finding["nodeIds"]!.AsArray());

        // A posted document is judged as sent: a connected pair has nothing isolated, whatever is stored.
        JsonNode connected = (await Read(admin, id)).Document.DeepClone();
        connected["nodes"]!.AsArray().Add(new JsonObject { ["id"] = "n_other", ["kind"] = "process", ["label"] = "Other", ["shape"] = "rectangle" });
        string aloneId = connected["nodes"]![0]!["id"]!.GetValue<string>();
        connected["edges"]!.AsArray().Add(new JsonObject { ["id"] = "e_1", ["kind"] = "flow", ["from"] = new JsonObject { ["node"] = aloneId }, ["to"] = new JsonObject { ["node"] = "n_other" } });
        JsonNode posted = await (await admin.PostAsJsonAsync("/api/critique", connected)).Json();
        Assert.DoesNotContain(posted["findings"]!.AsArray(), f => f!["rule"]!.GetValue<string>() == "isolated-node");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/critique", new { nope = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/documents/doc_missing/critique")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_copies_the_content_under_a_new_id_with_a_fresh_header_and_leaves_the_original_alone()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await CreateWithNodes(admin, "Original", ["Start", "Finish"]);
        await admin.PostAsJsonAsync($"/api/documents/{id}/versions", new { name = "original history" });
        (string tag, JsonNode original) = await Read(admin, id);

        HttpResponseMessage duplicated = await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, $"/api/documents/{id}/duplicate", withCsrfFrom: admin));

        Assert.Equal(HttpStatusCode.Created, duplicated.StatusCode);
        JsonNode copy = await duplicated.Json();
        string copyId = copy["id"]!.GetValue<string>();
        Assert.NotEqual(id, copyId);
        Assert.Equal($"/api/documents/{copyId}", duplicated.Headers.Location!.ToString());
        Assert.Equal("Original (copy)", copy["title"]!.GetValue<string>());
        Assert.Equal("local", copy["meta"]!["createdBy"]!.GetValue<string>().Split(':')[1]);
        Assert.True(JsonNode.DeepEquals(original["nodes"], copy["nodes"]));
        Assert.Equal(original["type"]!.GetValue<string>(), copy["type"]!.GetValue<string>());
        // Its own history starts empty, and the original is exactly as it was.
        Assert.Empty((await (await admin.GetAsync($"/api/documents/{copyId}/versions")).Json())["items"]!.AsArray());
        Assert.Equal(tag, (await Read(admin, id)).Tag);
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            IReadOnlyList<AuditEntry> audit = await scope.ServiceProvider.GetRequiredService<IStore>().ListAudit(copyId);
            Assert.Equal([AuditActions.Created], audit.Select(a => a.Action));
        }

        // A copy of a copy is not "(copy) (copy)", and a chosen title wins.
        JsonNode again = await (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, $"/api/documents/{copyId}/duplicate", withCsrfFrom: admin))).Json();
        Assert.Equal("Original (copy)", again["title"]!.GetValue<string>());
        JsonNode named = await (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, $"/api/documents/{id}/duplicate", new { title = "Fork for the audit" }, admin))).Json();
        Assert.Equal("Fork for the audit", named["title"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, $"/api/documents/{id}/duplicate", new { title = "  " }, admin))).StatusCode);

        // A document in the bin, or one that is not there, cannot be copied.
        await admin.DeleteAsync($"/api/documents/{id}");
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, $"/api/documents/{id}/duplicate", withCsrfFrom: admin))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/documents/doc_missing/duplicate", withCsrfFrom: admin))).StatusCode);
    }

    [Fact]
    public async Task The_list_searches_sorts_and_filters_and_refuses_what_it_does_not_understand()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string banana = await Create(admin, "banana plan", "architecture");
        await Task.Delay(20);
        string cherry = await Create(admin, "Cherry flow", "flowchart");
        await Task.Delay(20);
        string apple = await Create(admin, "apple flow", "flowchart");

        async Task<string[]> Ids(string query) => (await (await admin.GetAsync("/api/documents" + query)).Json())["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToArray();

        Assert.Equal([apple, cherry, banana], await Ids(""));
        Assert.Equal([apple, banana, cherry], await Ids("?sort=title"));
        Assert.Equal([apple, banana, cherry], await Ids("?sort=title&dir=asc"));
        Assert.Equal([cherry, banana, apple], await Ids("?sort=title&dir=desc"));
        Assert.Equal([banana, cherry, apple], await Ids("?sort=modified&dir=asc"));
        Assert.Equal([apple, cherry, banana], await Ids("?sort=created&dir=desc"));
        Assert.Equal([banana, apple, cherry], await Ids("?sort=type"));
        Assert.Equal([apple, cherry], await Ids("?type=flowchart&sort=title"));
        Assert.Equal([banana], await Ids("?type=architecture"));
        Assert.Equal([cherry, apple], (await Ids("?q=flow&sort=title&dir=desc")));
        Assert.Empty(await Ids("?type=sequence"));

        // Whatever is in the bin is not in the list, searched or not.
        await admin.DeleteAsync($"/api/documents/{apple}");
        Assert.Equal([cherry, banana], await Ids(""));
        Assert.Equal([cherry], await Ids("?q=flow"));

        foreach (string bad in new[] { "?sort=colour", "?dir=sideways", "?type=pie" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/documents" + bad)).StatusCode);
        }
    }

    // ---- plumbing ----

    private static async Task<string> Create(HttpClient client, string title, string type)
    {
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", new { title, type, nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Json())["id"]!.GetValue<string>();
    }

    private static async Task<string> CreateWithNodes(HttpClient client, string title, string[] labels)
    {
        var nodes = labels.Select((label, i) => new { id = $"n_{i + 1}", kind = "process", label, shape = "rectangle" }).ToArray();
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", new { title, type = "flowchart", nodes, edges = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Json())["id"]!.GetValue<string>();
    }

    private static async Task<JsonNode> SaveWithNodes(HttpClient client, string id, string title, string[] labels)
    {
        (string tag, JsonNode document) = await Read(client, id);
        JsonNode next = document.DeepClone();
        next["title"] = title;
        next["nodes"] = new JsonArray(labels.Select((label, i) => (JsonNode)new JsonObject { ["id"] = $"n_{i + 1}", ["kind"] = "process", ["label"] = label, ["shape"] = "rectangle" }).ToArray());
        HttpResponseMessage saved = await client.SendAsync(Put(client, id, next, tag));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return await saved.Json();
    }

    private static async Task<(string Tag, JsonNode Document)> Read(HttpClient client, string id)
    {
        HttpResponseMessage response = await client.GetAsync($"/api/documents/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (response.Headers.ETag!.ToString(), await response.Json());
    }

    private static HttpRequestMessage Put(HttpClient client, string id, JsonNode body, string tag)
    {
        HttpRequestMessage request = AuthBarHelpers.Request(HttpMethod.Put, $"/api/documents/{id}", body, client);
        request.Headers.TryAddWithoutValidation("If-Match", tag);
        return request;
    }

    private static HttpRequestMessage Restore(HttpClient client, string id, string versionId, string? tag)
    {
        HttpRequestMessage request = AuthBarHelpers.Request(HttpMethod.Post, $"/api/documents/{id}/versions/{versionId}/restore", withCsrfFrom: client);
        if (tag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", tag);
        }

        return request;
    }
}
