using Draughtsman.Core.Auth;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Storage;
using Draughtsman.Mcp.Tools;
using ModelContextProtocol;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// Security review F2: over HTTP the caller is already authenticated, so <c>actor</c> may only annotate which agent
/// is acting on top of that identity - never replace it - and the owner's own id (never just a display name, F9)
/// is always the one recorded, whether or not an annotation is present.
/// </summary>
public class DraughtsmanToolsHttpActorTests
{
    private static readonly IActorContext AuthenticatedAsEric = new FakeActorContext(new AuthenticatedIdentity("usr_eric123", "Eric"));

    private static DraughtsmanTools ToolsFor(FakeStore store, IActorContext actorContext)
    {
        return new DraughtsmanTools(store, new FakeSidecarClient(), new FakePdfRenderer(), new FakeAssetReader(), actorContext);
    }

    [Fact]
    public async Task With_no_actor_argument_the_write_is_attributed_to_the_authenticated_user()
    {
        var store = new FakeStore();
        DraughtsmanTools tools = ToolsFor(store, AuthenticatedAsEric);

        CreateDocumentResult result = await tools.CreateDocument("Title", "flowchart", MinimalDsl, "dsl");

        AuditEntry entry = Assert.Single(await store.ListAudit(result.Id));
        Assert.Equal(ActorKind.User, entry.Actor.Kind);
        Assert.Equal("Eric", entry.Actor.Name);
        Assert.Equal("usr_eric123", entry.ActorUserId);
    }

    [Fact]
    public async Task An_agent_annotation_is_layered_on_top_of_the_owner_not_a_replacement()
    {
        var store = new FakeStore();
        DraughtsmanTools tools = ToolsFor(store, AuthenticatedAsEric);

        CreateDocumentResult result = await tools.CreateDocument("Title", "flowchart", MinimalDsl, "dsl", actor: "agent:claude-code/claude-fable-5-1");

        AuditEntry entry = Assert.Single(await store.ListAudit(result.Id));
        Assert.Equal(ActorKind.Agent, entry.Actor.Kind);
        Assert.Equal("claude-code", entry.Actor.Name);
        Assert.Equal("claude-fable-5-1", entry.Actor.Model);
        // The real owner is never lost behind the annotation - this is the exact gap the review found.
        Assert.Equal("usr_eric123", entry.ActorUserId);
    }

    [Fact]
    public async Task A_user_form_actor_argument_is_rejected_not_honoured_as_a_different_identity()
    {
        var store = new FakeStore();
        DraughtsmanTools tools = ToolsFor(store, AuthenticatedAsEric);

        McpException error = await Assert.ThrowsAsync<McpException>(
            () => tools.CreateDocument("Title", "flowchart", MinimalDsl, "dsl", actor: "user:Alice Admin"));

        Assert.Contains("agent:", error.Message, StringComparison.Ordinal);
        // Rejected before anything was written - not silently downgraded to the caller's own identity.
        Assert.Equal(0, (await store.ListDocuments(new DocumentQuery())).Total);
    }

    [Fact]
    public async Task Patch_document_records_the_real_owner_alongside_an_agent_annotation()
    {
        var store = new FakeStore();
        DraughtsmanTools stdioTools = ToolsFor(store, new NullActorContext());
        CreateDocumentResult created = await stdioTools.CreateDocument("Title", "flowchart", MinimalDsl, "dsl");

        DraughtsmanTools httpTools = ToolsFor(store, AuthenticatedAsEric);
        await httpTools.PatchDocument(created.Id, """[{"op":"restyle","target":"node","id":"n_a","style":{"stroke":"#00ff00"}}]""", actor: "agent:claude-code/claude-fable-5-1");

        IReadOnlyList<AuditEntry> audit = await store.ListAudit(created.Id);
        AuditEntry patch = Assert.Single(audit, a => a.Action == AuditActions.Replaced);
        Assert.Equal("usr_eric123", patch.ActorUserId);
    }

    /// <summary>AU-05: a read-scoped token can read through MCP but cannot create or patch; a full-scope token can.</summary>
    [Fact]
    public async Task A_read_only_identity_can_read_but_cannot_create_or_patch()
    {
        var store = new FakeStore();
        CreateDocumentResult existing = await ToolsFor(store, new NullActorContext()).CreateDocument("Title", "flowchart", MinimalDsl, "dsl");
        DraughtsmanTools readOnly = ToolsFor(store, new FakeActorContext(new AuthenticatedIdentity("usr_reader", "Reader", ReadOnly: true)));

        // Reads still work...
        Assert.Equal(1, (await readOnly.ListDocuments()).Total);
        Assert.False(string.IsNullOrEmpty(await readOnly.ReadDocument(existing.Id, "dsl")));

        // ...writes are refused before anything is stored.
        McpException create = await Assert.ThrowsAsync<McpException>(() => readOnly.CreateDocument("Nope", "flowchart", MinimalDsl, "dsl"));
        Assert.Contains("read-only", create.Message, StringComparison.Ordinal);
        McpException patch = await Assert.ThrowsAsync<McpException>(
            () => readOnly.PatchDocument(existing.Id, """[{"op":"restyle","target":"node","id":"n_a","style":{"stroke":"#00ff00"}}]"""));
        Assert.Contains("read-only", patch.Message, StringComparison.Ordinal);
        Assert.Equal(1, (await store.ListDocuments(new DocumentQuery())).Total);
        Assert.Single(await store.ListAudit(existing.Id)); // only the original create

        // The same call with a full-scope identity goes through, so the refusal above is the scope and nothing else.
        DraughtsmanTools full = ToolsFor(store, new FakeActorContext(new AuthenticatedIdentity("usr_writer", "Writer")));
        await full.CreateDocument("Yes", "flowchart", MinimalDsl, "dsl");
        Assert.Equal(2, (await store.ListDocuments(new DocumentQuery())).Total);
    }

    private const string MinimalDsl = "draughtsman 1\ntype flowchart\ntitle \"T\"\nnode n_a process \"A\" @ 0,0 120x60\n";

    private sealed class FakeActorContext(AuthenticatedIdentity? identity) : IActorContext
    {
        public AuthenticatedIdentity? Current => identity;
    }
}
