using Draughtsman.Core.Auth;
using Draughtsman.Core.Storage;

namespace Draughtsman.Storage.Tests;

/// <summary>
/// The specification of <see cref="IPasswordResetStore"/>, run unchanged by every provider (ADR 0003, rule 5). The behaviour that
/// matters is single use under concurrency, what a completed reset does to the account (password written, every session revoked,
/// other live links ended, API tokens left alone) and that each way a link can be dead is reported as itself and changes nothing.
/// </summary>
public abstract class PasswordResetStoreContractTests : IAsyncLifetime
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.Parse("2026-10-01T10:00:00.000Z");

    protected IPasswordResetStore Resets { get; private set; } = null!;

    protected IUserStore Users { get; private set; } = null!;

    protected IStoreMaintenance Maintenance { get; private set; } = null!;

    protected abstract Task<(IPasswordResetStore Resets, IUserStore Users, IStoreMaintenance Maintenance)> CreateStores();

    /// <summary>A second set of stores over the same database, for the tests that need real concurrent callers (one EF context is not thread-safe).</summary>
    protected abstract Task<IPasswordResetStore> CreateAdditionalResetStore();

    protected virtual Task DisposeStores()
    {
        return Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        (Resets, Users, Maintenance) = await CreateStores();
    }

    public async Task DisposeAsync()
    {
        await DisposeStores();
    }

    private async Task<User> NewUser(string email = "reset@example.com")
    {
        return await Users.CreateUser(email, "Reset Person", UserRole.Editor, "old-hash", s_now.AddDays(-10));
    }

    [SkippableFact]
    public async Task A_stored_link_round_trips_by_its_hash_and_nothing_else_finds_it()
    {
        User user = await NewUser();
        PasswordResetCreation created = await Resets.Create(user.Id, PasswordResetPurpose.Admin, "hash-a", s_now, s_now.AddHours(24), "usr_admin", invalidateOthers: true);

        Assert.StartsWith("prl_", created.Token.Id, StringComparison.Ordinal);
        Assert.Equal(0, created.LinksInvalidated);
        PasswordResetToken found = Assert.IsType<PasswordResetToken>(await Resets.GetByHash("hash-a"));
        Assert.Equal(user.Id, found.UserId);
        Assert.Equal(PasswordResetPurpose.Admin, found.Purpose);
        Assert.Equal(s_now, found.CreatedAt);
        Assert.Equal(s_now.AddHours(24), found.ExpiresAt);
        Assert.Equal("usr_admin", found.CreatedByUserId);
        Assert.Null(found.UsedAt);
        Assert.Null(found.InvalidatedAt);
        Assert.True(found.IsLive(s_now));
        Assert.False(found.IsLive(s_now.AddHours(24)));
        Assert.Null(await Resets.GetByHash("hash-b"));
        Assert.Null(await Resets.GetByHash("HASH-A"));
    }

    [SkippableFact]
    public async Task The_same_hash_cannot_be_stored_twice()
    {
        User user = await NewUser();
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "dup", s_now, s_now.AddHours(1), null, invalidateOthers: false);

        await Assert.ThrowsAnyAsync<Exception>(() => Resets.Create(user.Id, PasswordResetPurpose.Email, "dup", s_now, s_now.AddHours(1), null, invalidateOthers: false));
    }

    [SkippableFact]
    public async Task Creating_with_invalidateOthers_ends_that_persons_live_links_of_either_purpose_and_nobody_elses()
    {
        User alice = await NewUser("alice@example.com");
        User bob = await NewUser("bob@example.com");
        await Resets.Create(alice.Id, PasswordResetPurpose.Email, "alice-email", s_now, s_now.AddHours(1), null, invalidateOthers: false);
        await Resets.Create(alice.Id, PasswordResetPurpose.Admin, "alice-admin-1", s_now, s_now.AddHours(24), "usr_x", invalidateOthers: false);
        await Resets.Create(bob.Id, PasswordResetPurpose.Admin, "bob-admin", s_now, s_now.AddHours(24), "usr_x", invalidateOthers: false);

        PasswordResetCreation replacement = await Resets.Create(alice.Id, PasswordResetPurpose.Admin, "alice-admin-2", s_now.AddMinutes(1), s_now.AddHours(25), "usr_x", invalidateOthers: true);

        Assert.Equal(2, replacement.LinksInvalidated);
        Assert.NotNull((await Resets.GetByHash("alice-email"))!.InvalidatedAt);
        Assert.NotNull((await Resets.GetByHash("alice-admin-1"))!.InvalidatedAt);
        Assert.True((await Resets.GetByHash("alice-admin-2"))!.IsLive(s_now.AddMinutes(2)));
        Assert.True((await Resets.GetByHash("bob-admin"))!.IsLive(s_now.AddMinutes(2)));
    }

    [SkippableFact]
    public async Task Creating_without_invalidateOthers_leaves_earlier_links_alone()
    {
        User user = await NewUser();
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "first", s_now, s_now.AddHours(1), null, invalidateOthers: false);

        PasswordResetCreation second = await Resets.Create(user.Id, PasswordResetPurpose.Email, "second", s_now.AddMinutes(1), s_now.AddHours(1), null, invalidateOthers: false);

        Assert.Equal(0, second.LinksInvalidated);
        Assert.True((await Resets.GetByHash("first"))!.IsLive(s_now.AddMinutes(2)));
        Assert.True((await Resets.GetByHash("second"))!.IsLive(s_now.AddMinutes(2)));
    }

    [SkippableFact]
    public async Task CountCreatedSince_counts_one_purpose_for_one_person_inside_the_window()
    {
        User alice = await NewUser("alice@example.com");
        User bob = await NewUser("bob@example.com");
        await Resets.Create(alice.Id, PasswordResetPurpose.Email, "a1", s_now.AddMinutes(-20), s_now.AddHours(1), null, invalidateOthers: false);
        await Resets.Create(alice.Id, PasswordResetPurpose.Email, "a2", s_now.AddMinutes(-5), s_now.AddHours(1), null, invalidateOthers: false);
        await Resets.Create(alice.Id, PasswordResetPurpose.Email, "a3", s_now.AddMinutes(-1), s_now.AddHours(1), null, invalidateOthers: false);
        await Resets.Create(alice.Id, PasswordResetPurpose.Admin, "a-admin", s_now.AddMinutes(-1), s_now.AddHours(24), "usr_x", invalidateOthers: false);
        await Resets.Create(bob.Id, PasswordResetPurpose.Email, "b1", s_now.AddMinutes(-1), s_now.AddHours(1), null, invalidateOthers: false);

        Assert.Equal(2, await Resets.CountCreatedSince(alice.Id, PasswordResetPurpose.Email, s_now.AddMinutes(-15)));
        Assert.Equal(3, await Resets.CountCreatedSince(alice.Id, PasswordResetPurpose.Email, s_now.AddHours(-1)));
        Assert.Equal(1, await Resets.CountCreatedSince(alice.Id, PasswordResetPurpose.Admin, s_now.AddHours(-1)));
        Assert.Equal(1, await Resets.CountCreatedSince(bob.Id, PasswordResetPurpose.Email, s_now.AddHours(-1)));
        Assert.Equal(0, await Resets.CountCreatedSince(bob.Id, PasswordResetPurpose.Email, s_now));
    }

    [SkippableFact]
    public async Task Redeeming_a_live_link_sets_the_password_revokes_every_session_and_ends_the_other_links_but_leaves_api_tokens()
    {
        User user = await NewUser();
        User bystander = await NewUser("bystander@example.com");
        Session one = await Users.CreateSession(user.Id, "session-1", s_now.AddHours(-3), s_now.AddDays(30));
        Session two = await Users.CreateSession(user.Id, "session-2", s_now.AddHours(-2), s_now.AddDays(30));
        Session others = await Users.CreateSession(bystander.Id, "session-3", s_now.AddHours(-2), s_now.AddDays(30));
        ApiToken token = await Users.CreateApiToken(user.Id, "agent", "api-hash", s_now.AddDays(-1), s_now.AddDays(30));
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "other-link", s_now.AddMinutes(-30), s_now.AddMinutes(30), null, invalidateOthers: false);
        await Resets.Create(user.Id, PasswordResetPurpose.Admin, "the-link", s_now.AddMinutes(-10), s_now.AddHours(23), "usr_x", invalidateOthers: false);
        await Resets.Create(bystander.Id, PasswordResetPurpose.Admin, "bystander-link", s_now.AddMinutes(-10), s_now.AddHours(23), "usr_x", invalidateOthers: false);

        PasswordResetRedemption redeemed = await Resets.Redeem("the-link", "new-hash", s_now);

        Assert.Equal(PasswordResetRedeemStatus.Redeemed, redeemed.Status);
        Assert.Equal(user.Id, redeemed.User!.Id);
        Assert.Equal(PasswordResetPurpose.Admin, redeemed.Token!.Purpose);
        Assert.Equal(2, redeemed.SessionsRevoked);
        Assert.Equal(1, redeemed.LinksInvalidated);
        Assert.Equal("new-hash", (await Users.GetUserById(user.Id))!.PasswordHash);
        Assert.True((await Users.GetSessionByHash("session-1"))!.Revoked);
        Assert.True((await Users.GetSessionByHash("session-2"))!.Revoked);
        Assert.False((await Users.GetSessionByHash("session-3"))!.Revoked);
        Assert.False((await Users.GetApiTokenByHash("api-hash"))!.Revoked);
        Assert.Equal(s_now, (await Resets.GetByHash("the-link"))!.UsedAt);
        Assert.NotNull((await Resets.GetByHash("other-link"))!.InvalidatedAt);
        Assert.True((await Resets.GetByHash("bystander-link"))!.IsLive(s_now));
        _ = (one, two, others, token);
    }

    [SkippableFact]
    public async Task A_link_works_once()
    {
        User user = await NewUser();
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "once", s_now, s_now.AddHours(1), null, invalidateOthers: false);

        Assert.Equal(PasswordResetRedeemStatus.Redeemed, (await Resets.Redeem("once", "first-password-hash", s_now.AddMinutes(1))).Status);
        PasswordResetRedemption again = await Resets.Redeem("once", "second-password-hash", s_now.AddMinutes(2));

        Assert.Equal(PasswordResetRedeemStatus.Used, again.Status);
        Assert.Equal(0, again.SessionsRevoked);
        Assert.Equal("first-password-hash", (await Users.GetUserById(user.Id))!.PasswordHash);
    }

    [SkippableFact]
    public async Task Concurrent_redemptions_of_one_link_have_exactly_one_winner()
    {
        User user = await NewUser();
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "race", s_now, s_now.AddHours(1), null, invalidateOthers: false);
        const int Callers = 8;
        var stores = new List<IPasswordResetStore>();
        for (int i = 0; i < Callers; i++)
        {
            stores.Add(await CreateAdditionalResetStore());
        }

        PasswordResetRedemption[] results = await Task.WhenAll(stores.Select((store, i) => Task.Run(() => store.Redeem("race", "password-hash-" + i, s_now.AddMinutes(1)))));

        Assert.Equal(1, results.Count(r => r.Status == PasswordResetRedeemStatus.Redeemed));
        Assert.All(results.Where(r => r.Status != PasswordResetRedeemStatus.Redeemed), r => Assert.Equal(PasswordResetRedeemStatus.Used, r.Status));
        string winner = results.Single(r => r.Status == PasswordResetRedeemStatus.Redeemed).User!.PasswordHash;
        Assert.Equal(winner, (await Users.GetUserById(user.Id))!.PasswordHash);
        Assert.StartsWith("password-hash-", winner, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task An_expired_link_is_reported_as_expired_and_changes_nothing()
    {
        User user = await NewUser();
        await Users.CreateSession(user.Id, "still-signed-in", s_now.AddHours(-1), s_now.AddDays(30));
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "old", s_now, s_now.AddHours(1), null, invalidateOthers: false);

        PasswordResetRedemption refused = await Resets.Redeem("old", "new-hash", s_now.AddHours(1));

        Assert.Equal(PasswordResetRedeemStatus.Expired, refused.Status);
        Assert.Equal("old-hash", (await Users.GetUserById(user.Id))!.PasswordHash);
        Assert.False((await Users.GetSessionByHash("still-signed-in"))!.Revoked);
        Assert.Null((await Resets.GetByHash("old"))!.UsedAt);
    }

    [SkippableFact]
    public async Task An_invalidated_link_is_reported_as_invalidated_and_changes_nothing()
    {
        User user = await NewUser();
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "ended", s_now, s_now.AddHours(1), null, invalidateOthers: false);
        Assert.Equal(1, await Resets.InvalidateLive(user.Id, s_now.AddMinutes(1)));

        PasswordResetRedemption refused = await Resets.Redeem("ended", "new-hash", s_now.AddMinutes(2));

        Assert.Equal(PasswordResetRedeemStatus.Invalidated, refused.Status);
        Assert.Equal("old-hash", (await Users.GetUserById(user.Id))!.PasswordHash);
    }

    [SkippableFact]
    public async Task A_deactivated_account_cannot_redeem_and_its_link_is_not_spent()
    {
        User user = await NewUser();
        await Resets.Create(user.Id, PasswordResetPurpose.Admin, "inactive", s_now, s_now.AddHours(24), "usr_x", invalidateOthers: false);
        await Users.SetUserActive(user.Id, false);

        PasswordResetRedemption refused = await Resets.Redeem("inactive", "new-hash", s_now.AddMinutes(1));

        Assert.Equal(PasswordResetRedeemStatus.AccountInactive, refused.Status);
        Assert.Equal("old-hash", (await Users.GetUserById(user.Id))!.PasswordHash);
        Assert.Null((await Resets.GetByHash("inactive"))!.UsedAt);
    }

    [SkippableFact]
    public async Task A_link_nobody_issued_is_not_found_and_tampering_with_one_character_is_not_found_either()
    {
        User user = await NewUser();
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "abcdef0123456789", s_now, s_now.AddHours(1), null, invalidateOthers: false);

        Assert.Equal(PasswordResetRedeemStatus.NotFound, (await Resets.Redeem("abcdef0123456780", "new-hash", s_now)).Status);
        Assert.Equal(PasswordResetRedeemStatus.NotFound, (await Resets.Redeem("abcdef012345678", "new-hash", s_now)).Status);
        Assert.Equal("old-hash", (await Users.GetUserById(user.Id))!.PasswordHash);
        Assert.True((await Resets.GetByHash("abcdef0123456789"))!.IsLive(s_now));
    }

    [SkippableFact]
    public async Task Invalidate_ends_one_unused_link_and_cannot_undo_a_use()
    {
        User user = await NewUser();
        PasswordResetCreation spent = await Resets.Create(user.Id, PasswordResetPurpose.Email, "spent", s_now, s_now.AddHours(1), null, invalidateOthers: false);
        await Resets.Redeem("spent", "new-hash", s_now.AddMinutes(1));
        PasswordResetCreation lost = await Resets.Create(user.Id, PasswordResetPurpose.Email, "lost-in-the-post", s_now.AddMinutes(1), s_now.AddHours(1), null, invalidateOthers: false);

        Assert.True(await Resets.Invalidate(lost.Token.Id, s_now.AddMinutes(2)));
        Assert.False(await Resets.Invalidate(lost.Token.Id, s_now.AddMinutes(3)));
        Assert.False(await Resets.Invalidate(spent.Token.Id, s_now.AddMinutes(3)));
        Assert.Null((await Resets.GetByHash("spent"))!.InvalidatedAt);
    }

    [SkippableFact]
    public async Task Purging_removes_ended_links_and_never_a_live_one()
    {
        User user = await NewUser();
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "expired-long-ago", s_now.AddDays(-10), s_now.AddDays(-10).AddHours(1), null, invalidateOthers: false);
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "used-long-ago", s_now.AddDays(-9), s_now.AddDays(-9).AddHours(1), null, invalidateOthers: false);
        await Resets.Redeem("used-long-ago", "new-hash", s_now.AddDays(-9).AddMinutes(5));
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "ended-long-ago", s_now.AddDays(-8), s_now.AddDays(-8).AddHours(1), null, invalidateOthers: false);
        await Resets.InvalidateLive(user.Id, s_now.AddDays(-8).AddMinutes(5));
        await Resets.Create(user.Id, PasswordResetPurpose.Admin, "live", s_now, s_now.AddHours(24), "usr_x", invalidateOthers: false);
        await Resets.Create(user.Id, PasswordResetPurpose.Email, "recently-expired", s_now.AddDays(-2), s_now.AddDays(-2).AddHours(1), null, invalidateOthers: false);

        int deleted = await Maintenance.PurgePasswordResetLinks(s_now.AddDays(-7));

        Assert.Equal(3, deleted);
        Assert.Null(await Resets.GetByHash("expired-long-ago"));
        Assert.Null(await Resets.GetByHash("used-long-ago"));
        Assert.Null(await Resets.GetByHash("ended-long-ago"));
        Assert.NotNull(await Resets.GetByHash("live"));
        Assert.NotNull(await Resets.GetByHash("recently-expired"));
        Assert.Equal(0, await Maintenance.PurgePasswordResetLinks(s_now.AddDays(-7)));
    }
}
