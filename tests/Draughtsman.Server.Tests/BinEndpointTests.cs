using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Documents;
using Draughtsman.Server.Maintenance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Draughtsman.Server.Tests;

/// <summary>
/// OP-02 as a running server shows it: deleting moves a document to the bin, it can come back whole, removing it for good is a
/// separate and narrower step, who sees which entry follows the Editor rule in docs/api.md, each step is a security event, and
/// the retention pass removes only what has been in the bin past the configured days.
/// </summary>
public sealed class BinEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public BinEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Delete_moves_a_document_to_the_bin_and_restore_brings_it_back_identical()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await Create(admin, "Order flow binned and back");
        HttpResponseMessage before = await admin.GetAsync($"/api/documents/{id}");
        string original = await before.Content.ReadAsStringAsync();
        string tag = before.Headers.ETag!.ToString();

        Assert.Equal(HttpStatusCode.NoContent, (await Delete(admin, $"/api/documents/{id}")).StatusCode);

        // Hidden from everything that reads or writes a live document.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{id}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(Put(id, original, tag, admin))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(admin, $"/api/documents/{id}")).StatusCode);
        Assert.DoesNotContain(await ListTitles(admin), t => t == "Order flow binned and back");
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/documents", JsonNode.Parse(original))).StatusCode);

        JsonNode entry = await FindInBin(admin, id);
        Assert.Equal("local", entry["deletedBy"]!.GetValue<string>());
        Assert.True(entry["canPurge"]!.GetValue<bool>());
        DateTimeOffset deletedAt = entry["deletedAt"]!.GetValue<DateTimeOffset>();
        Assert.Equal(deletedAt.AddDays(30), entry["purgesAt"]!.GetValue<DateTimeOffset>());

        HttpResponseMessage restored = await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, $"/api/bin/{id}/restore", withCsrfFrom: admin));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        // Back exactly as it was, down to the version a save must quote.
        HttpResponseMessage after = await admin.GetAsync($"/api/documents/{id}");
        Assert.Equal(original, await after.Content.ReadAsStringAsync());
        Assert.Equal(tag, after.Headers.ETag!.ToString());
        Assert.Contains(await ListTitles(admin), t => t == "Order flow binned and back");
        Assert.Null(await TryFindInBin(admin, id));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, $"/api/bin/{id}/restore", withCsrfFrom: admin))).StatusCode);
    }

    [Fact]
    public async Task Removing_for_good_is_a_second_step_that_a_live_document_cannot_take()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await Create(admin, "Live and unharmed");

        // The second step is refused for a document that was never put in the bin: one request cannot destroy it.
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(admin, $"/api/bin/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/documents/{id}")).StatusCode);

        await Delete(admin, $"/api/documents/{id}");
        Assert.NotNull(await TryFindInBin(admin, id));
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(admin, $"/api/bin/{id}")).StatusCode);

        Assert.Null(await TryFindInBin(admin, id));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, $"/api/bin/{id}/restore", withCsrfFrom: admin))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(admin, $"/api/bin/{id}")).StatusCode);
        using IServiceScope scope = _factory.Services.CreateScope();
        IReadOnlyList<AuditEntry> audit = await scope.ServiceProvider.GetRequiredService<IStore>().ListAudit(id);
        Assert.Equal([AuditActions.Created, AuditActions.Deleted, AuditActions.Purged], audit.Select(a => a.Action));
    }

    [Fact]
    public async Task An_api_token_can_delete_and_restore_but_never_remove_for_good()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        (_, string secret) = await admin.CreateToken("bin token");
        HttpClient token = _factory.Bearer(secret);
        string id = await Create(admin, "Token cannot purge");

        Assert.Equal(HttpStatusCode.NoContent, (await token.DeleteAsync($"/api/documents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync("/api/bin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await token.PostAsync($"/api/bin/{id}/restore", null)).StatusCode);
        await token.DeleteAsync($"/api/documents/{id}");

        // The owner is an administrator and the entry is theirs, yet the token may not do the one thing nothing can undo.
        Assert.Equal(HttpStatusCode.Forbidden, (await token.DeleteAsync($"/api/bin/{id}")).StatusCode);
        Assert.NotNull(await TryFindInBin(admin, id));
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(admin, $"/api/bin/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_read_only_token_cannot_delete_restore_or_remove_anything()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        (_, string secret) = await admin.CreateToken("read token", new { scope = "read" });
        HttpClient readOnly = _factory.Bearer(secret);
        string id = await Create(admin, "Read token cannot touch");

        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.DeleteAsync($"/api/documents/{id}")).StatusCode);
        await Delete(admin, $"/api/documents/{id}");
        Assert.Equal(HttpStatusCode.OK, (await readOnly.GetAsync("/api/bin")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.PostAsync($"/api/bin/{id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.DeleteAsync($"/api/bin/{id}")).StatusCode);
        Assert.NotNull(await TryFindInBin(admin, id));
    }

    [Fact]
    public async Task The_bin_is_not_reachable_without_signing_in()
    {
        HttpClient anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/bin")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/bin/doc_x/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync("/api/bin/doc_x")).StatusCode);
    }

    [Fact]
    public async Task An_editor_sees_and_restores_what_they_created_or_deleted_and_removes_for_good_only_their_own()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        await admin.CreateUser("ed-one@example.com");
        await admin.CreateUser("ed-two@example.com");
        HttpClient one = await _factory.SignIn("ed-one@example.com");
        HttpClient two = await _factory.SignIn("ed-two@example.com");
        string adminsDoc = await Create(admin, "Admin's document");
        string onesDoc = await Create(one, "Editor one's document");
        string adminsHidden = await Create(admin, "Admin's document, deleted by the admin");

        // An Editor can delete a colleague's document (the organisation-wide trust model), and it is recoverable.
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(one, $"/api/documents/{adminsDoc}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(one, $"/api/documents/{onesDoc}")).StatusCode);
        await Delete(admin, $"/api/documents/{adminsHidden}");

        // Editor one sees what they deleted and what they created; not what the admin deleted of the admin's own.
        Assert.NotNull(await TryFindInBin(one, adminsDoc));
        Assert.NotNull(await TryFindInBin(one, onesDoc));
        Assert.Null(await TryFindInBin(one, adminsHidden));
        // Editor two is party to none of it, and is told nothing about it.
        Assert.Null(await TryFindInBin(two, adminsDoc));
        Assert.Null(await TryFindInBin(two, onesDoc));
        Assert.Equal(HttpStatusCode.NotFound, (await two.PostAsync($"/api/bin/{onesDoc}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(two, $"/api/bin/{onesDoc}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await one.PostAsync($"/api/bin/{adminsHidden}/restore", null)).StatusCode);
        // The admin sees everything.
        foreach (string id in new[] { adminsDoc, onesDoc, adminsHidden })
        {
            Assert.NotNull(await TryFindInBin(admin, id));
        }

        // The deleter can restore a colleague's document, but it is not theirs to destroy: that is for its creator or an admin.
        Assert.Equal(HttpStatusCode.OK, (await one.PostAsync($"/api/bin/{adminsDoc}/restore", null)).StatusCode);
        await Delete(one, $"/api/documents/{adminsDoc}");
        Assert.False((await FindInBin(one, adminsDoc))["canPurge"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, (await Delete(one, $"/api/bin/{adminsDoc}")).StatusCode);
        Assert.NotNull(await TryFindInBin(admin, adminsDoc));

        // Their own, they can remove for good; an admin can remove anything.
        Assert.True((await FindInBin(one, onesDoc))["canPurge"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(one, $"/api/bin/{onesDoc}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(admin, $"/api/bin/{adminsDoc}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(admin, $"/api/bin/{adminsHidden}")).StatusCode);
    }

    [Fact]
    public async Task The_actor_header_on_a_token_never_widens_who_can_see_or_restore_a_bin_entry()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string editorId = await admin.CreateUser("ed-token@example.com");
        HttpClient editor = await _factory.SignIn("ed-token@example.com");
        (_, string secret) = await editor.CreateToken("editor token");
        HttpClient editorToken = _factory.Bearer(secret);
        string adminsDoc = await Create(admin, "Admin only, in the bin");
        await Delete(admin, $"/api/documents/{adminsDoc}");

        // Claiming to be the admin, or any other name, is an annotation for the audit trail and nothing else.
        foreach (string claimed in new[] { "agent:local/claude", $"agent:{editorId}/claude", "agent:admin/claude" })
        {
            var restore = new HttpRequestMessage(HttpMethod.Post, $"/api/bin/{adminsDoc}/restore");
            restore.Headers.Add(ActorHeader.Name, claimed);
            Assert.Equal(HttpStatusCode.NotFound, (await editorToken.SendAsync(restore)).StatusCode);
        }

        Assert.DoesNotContain(((await (await editorToken.GetAsync("/api/bin")).Json())["items"]!.AsArray()), item => item!["id"]!.GetValue<string>() == adminsDoc);
        Assert.NotNull(await TryFindInBin(admin, adminsDoc));
    }

    [Fact]
    public async Task Delete_restore_and_permanent_delete_each_leave_a_security_event_and_a_document_audit_row()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await Create(admin, "Evented document");

        await Delete(admin, $"/api/documents/{id}");
        await admin.PostAsync($"/api/bin/{id}/restore", null);
        await Delete(admin, $"/api/documents/{id}");
        await Delete(admin, $"/api/bin/{id}");

        foreach ((string type, int expected) in new[] { (SecurityEventTypes.DocumentDeleted, 2), (SecurityEventTypes.DocumentRestored, 1), (SecurityEventTypes.DocumentPurged, 1) })
        {
            JsonArray events = await admin.SecurityEvents($"?type={type}");
            JsonNode[] mine = events.Select(e => e!).Where(e => e["detail"]!.GetValue<string>().StartsWith(id, StringComparison.Ordinal)).ToArray();
            Assert.Equal(expected, mine.Length);
            Assert.All(mine, e =>
            {
                Assert.Equal("Evented document", e["targetLabel"]!.GetValue<string>());
                Assert.Equal("local@example.com", e["actorLabel"]!.GetValue<string>());
                Assert.Equal("session", e["method"]!.GetValue<string>());
            });
        }

        JsonNode audit = await (await admin.GetAsync($"/api/admin/audit?documentId={id}")).Json();
        Assert.Equal(["purged", "deleted", "restored", "deleted", "created"], audit["items"]!.AsArray().Select(a => a!["action"]!.GetValue<string>()));
    }

    [Fact]
    public async Task The_retention_pass_removes_only_documents_past_the_configured_days_and_records_each_one()
    {
        using var factory = new DraughtsmanServerFactory();
        Directory.CreateDirectory(factory.DataFolder);
        File.WriteAllText(Path.Combine(factory.DataFolder, "draughtsman.yaml"), "retention:\n  binDays: 10\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string expired = await Create(admin, "Expired in the bin");
        string recent = await Create(admin, "Recent in the bin");
        string live = await Create(admin, "Old but live");
        await Create(admin, "Another live one");
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            IStore store = scope.ServiceProvider.GetRequiredService<IStore>();
            await store.SoftDeleteDocument(expired, DateTimeOffset.UtcNow.AddDays(-11), null, "Someone");
            await store.SoftDeleteDocument(recent, DateTimeOffset.UtcNow.AddDays(-9), null, "Someone");
        }

        MaintenanceService pass = factory.Services.GetServices<IHostedService>().OfType<MaintenanceService>().Single();
        MaintenanceResult result = await pass.RunOnceAsync();

        Assert.Equal(1, result.BinnedDocumentsRemoved);
        Assert.Null(await TryFindInBin(admin, expired));
        Assert.NotNull(await TryFindInBin(admin, recent));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/documents/{live}")).StatusCode);
        JsonNode purge = Assert.Single(await admin.SecurityEvents($"?type={SecurityEventTypes.DocumentPurged}"))!;
        Assert.Equal("Expired in the bin", purge["targetLabel"]!.GetValue<string>());
        Assert.Equal(MaintenanceService.RetentionActor, purge["actorLabel"]!.GetValue<string>());
        Assert.StartsWith(expired, purge["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, (await pass.RunOnceAsync()).BinnedDocumentsRemoved);
    }

    [Fact]
    public async Task A_retention_of_zero_days_never_removes_anything_from_the_bin()
    {
        using var factory = new DraughtsmanServerFactory();
        Directory.CreateDirectory(factory.DataFolder);
        File.WriteAllText(Path.Combine(factory.DataFolder, "draughtsman.yaml"), "retention:\n  binDays: 0\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await Create(admin, "Kept for ever");
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStore>().SoftDeleteDocument(id, DateTimeOffset.UtcNow.AddYears(-2), null, null);
        }

        MaintenanceResult result = await factory.Services.GetServices<IHostedService>().OfType<MaintenanceService>().Single().RunOnceAsync();

        Assert.Equal(0, result.BinnedDocumentsRemoved);
        JsonNode entry = await FindInBin(admin, id);
        // Nothing is scheduled to go: the entry carries no purge date, so the UI does not promise one.
        Assert.True(entry["purgesAt"] is null);
    }

    // ---- plumbing ----

    internal static async Task<string> Create(HttpClient client, string title)
    {
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", new { title, type = "flowchart", nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Json())["id"]!.GetValue<string>();
    }

    private static Task<HttpResponseMessage> Delete(HttpClient client, string url)
    {
        return client.DeleteAsync(url);
    }

    private static HttpRequestMessage Put(string id, string json, string tag, HttpClient csrfFrom)
    {
        HttpRequestMessage request = AuthBarHelpers.Request(HttpMethod.Put, $"/api/documents/{id}", JsonNode.Parse(json), csrfFrom);
        request.Headers.TryAddWithoutValidation("If-Match", tag);
        return request;
    }

    private static async Task<List<string>> ListTitles(HttpClient client)
    {
        JsonNode page = await (await client.GetAsync("/api/documents?limit=200")).Json();
        return page["items"]!.AsArray().Select(i => i!["title"]!.GetValue<string>()).ToList();
    }

    private static async Task<JsonNode?> TryFindInBin(HttpClient client, string id)
    {
        JsonNode page = await (await client.GetAsync("/api/bin?limit=200")).Json();
        return page["items"]!.AsArray().FirstOrDefault(i => i!["id"]!.GetValue<string>() == id);
    }

    private static async Task<JsonNode> FindInBin(HttpClient client, string id)
    {
        return await TryFindInBin(client, id) ?? throw new InvalidOperationException($"{id} is not in this caller's bin.");
    }
}
