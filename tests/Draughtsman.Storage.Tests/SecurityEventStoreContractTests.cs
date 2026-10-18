using Draughtsman.Core.Auth;

namespace Draughtsman.Storage.Tests;

/// <summary>
/// The specification of <see cref="ISecurityEventStore"/> (ADR 0003, rule 5): every provider subclass runs every test
/// here unchanged. Each test starts with an empty store supplied by <see cref="CreateStore"/>.
/// </summary>
public abstract class SecurityEventStoreContractTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-09-30T10:00:00.000Z");

    protected ISecurityEventStore Store { get; private set; } = null!;

    protected abstract Task<ISecurityEventStore> CreateStore();

    protected virtual Task DisposeStore()
    {
        return Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        Store = await CreateStore();
    }

    public async Task DisposeAsync()
    {
        await DisposeStore();
    }

    [SkippableFact]
    public async Task An_event_round_trips_with_every_field_and_gets_an_id()
    {
        SecurityEvent written = await Store.Append(new SecurityEvent(
            SecurityEventTypes.UserRoleChanged, T0, "usr_actor", "admin@example.com", "usr_target", "jane@example.com", "203.0.113.9", SecurityEventMethods.Session, "Editor to Admin"));

        Assert.True(written.Id > 0);
        SecurityEventPage page = await Store.List(new SecurityEventQuery());
        Assert.Equal(1, page.Total);
        SecurityEvent read = Assert.Single(page.Items);
        Assert.Equal(written.Id, read.Id);
        Assert.Equal(SecurityEventTypes.UserRoleChanged, read.Type);
        Assert.Equal(T0, read.At);
        Assert.Equal("usr_actor", read.ActorUserId);
        Assert.Equal("admin@example.com", read.ActorLabel);
        Assert.Equal("usr_target", read.TargetUserId);
        Assert.Equal("jane@example.com", read.TargetLabel);
        Assert.Equal("203.0.113.9", read.SourceIp);
        Assert.Equal(SecurityEventMethods.Session, read.Method);
        Assert.Equal("Editor to Admin", read.Detail);
    }

    [SkippableFact]
    public async Task Optional_fields_stay_null_and_listing_is_newest_first()
    {
        await Store.Append(new SecurityEvent(SecurityEventTypes.SignInFailed, T0));
        await Store.Append(new SecurityEvent(SecurityEventTypes.SignInSucceeded, T0.AddMinutes(5)));
        await Store.Append(new SecurityEvent(SecurityEventTypes.SignedOut, T0.AddMinutes(5)));

        SecurityEventPage page = await Store.List(new SecurityEventQuery());

        Assert.Equal(3, page.Total);
        // Newest first; the two at the same instant are ordered by id, later write first.
        Assert.Equal([SecurityEventTypes.SignedOut, SecurityEventTypes.SignInSucceeded, SecurityEventTypes.SignInFailed], page.Items.Select(e => e.Type).ToArray());
        Assert.All(page.Items, e =>
        {
            Assert.Null(e.ActorUserId);
            Assert.Null(e.TargetUserId);
            Assert.Null(e.SourceIp);
            Assert.Null(e.Detail);
        });
    }

    [SkippableFact]
    public async Task Filters_combine_and_each_one_narrows()
    {
        await Store.Append(new SecurityEvent(SecurityEventTypes.SignInFailed, T0, TargetUserId: "usr_a", SourceIp: "10.0.0.1"));
        await Store.Append(new SecurityEvent(SecurityEventTypes.SignInSucceeded, T0.AddHours(1), ActorUserId: "usr_a", SourceIp: "10.0.0.1"));
        await Store.Append(new SecurityEvent(SecurityEventTypes.SignInSucceeded, T0.AddHours(2), ActorUserId: "usr_b", SourceIp: "10.0.0.2"));
        await Store.Append(new SecurityEvent(SecurityEventTypes.TokenCreated, T0.AddHours(3), ActorUserId: "usr_b", TargetUserId: "usr_b", SourceIp: "10.0.0.2"));

        Assert.Equal(2, (await Store.List(new SecurityEventQuery(Types: [SecurityEventTypes.SignInSucceeded]))).Total);
        Assert.Equal(3, (await Store.List(new SecurityEventQuery(Types: [SecurityEventTypes.SignInSucceeded, SecurityEventTypes.TokenCreated]))).Total);
        // UserId matches the actor or the target.
        Assert.Equal(2, (await Store.List(new SecurityEventQuery(UserId: "usr_a"))).Total);
        Assert.Equal(2, (await Store.List(new SecurityEventQuery(UserId: "usr_b"))).Total);
        Assert.Equal(2, (await Store.List(new SecurityEventQuery(SourceIp: "10.0.0.2"))).Total);
        // Since is inclusive and until is exclusive.
        Assert.Equal(2, (await Store.List(new SecurityEventQuery(Since: T0.AddHours(2)))).Total);
        Assert.Equal(2, (await Store.List(new SecurityEventQuery(Until: T0.AddHours(2)))).Total);
        SecurityEventPage combined = await Store.List(new SecurityEventQuery(Types: [SecurityEventTypes.SignInSucceeded], UserId: "usr_a", SourceIp: "10.0.0.1"));
        Assert.Equal(1, combined.Total);
        Assert.Equal("usr_a", combined.Items[0].ActorUserId);
        Assert.Equal(0, (await Store.List(new SecurityEventQuery(Types: [SecurityEventTypes.PasswordReset]))).Total);
    }

    [SkippableFact]
    public async Task Paging_returns_the_requested_slice_and_the_full_total()
    {
        for (int i = 0; i < 5; i++)
        {
            await Store.Append(new SecurityEvent(SecurityEventTypes.SignInSucceeded, T0.AddMinutes(i), Detail: "n" + i));
        }

        SecurityEventPage page = await Store.List(new SecurityEventQuery(Offset: 1, Limit: 2));

        Assert.Equal(5, page.Total);
        Assert.Equal(new[] { "n3", "n2" }, page.Items.Select(e => e.Detail!).ToArray());
        Assert.Empty((await Store.List(new SecurityEventQuery(Offset: 5, Limit: 2))).Items);
    }

    [SkippableFact]
    public async Task Over_long_free_text_is_clipped_to_the_column_not_rejected()
    {
        SecurityEvent written = await Store.Append(new SecurityEvent(SecurityEventTypes.TokenCreated, T0, TargetLabel: new string('t', 400), Detail: new string('d', 900)));

        Assert.Equal(256, written.TargetLabel!.Length);
        Assert.Equal(512, written.Detail!.Length);
        Assert.Equal(512, (await Store.List(new SecurityEventQuery())).Items[0].Detail!.Length);
    }
}
