using Draughtsman.Core.Auth;

namespace Draughtsman.Storage.Tests;

/// <summary>
/// The specification of <see cref="IUserStore"/> behaviour, the same shape as <see cref="StoreContractTests"/> for
/// <c>IStore</c> (ADR 0003, rule 5). Every provider subclass runs every test here unchanged. Each test starts with an
/// empty store supplied by <see cref="CreateStore"/>.
/// </summary>
public abstract class UserStoreContractTests : IAsyncLifetime
{
    protected IUserStore Store { get; private set; } = null!;

    protected abstract Task<IUserStore> CreateStore();

    /// <summary>
    /// A second store instance over the same underlying database as <see cref="Store"/>, the way a real
    /// request-scoped <c>DbContext</c> would be. Needed for tests exercising real cross-connection concurrency:
    /// a single EF Core <c>DbContext</c> is not thread-safe for parallel calls on one instance.
    /// </summary>
    protected abstract Task<IUserStore> CreateAdditionalStore();

    protected virtual Task DisposeStore(IUserStore store)
    {
        return Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        Store = await CreateStore();
    }

    public async Task DisposeAsync()
    {
        await DisposeStore(Store);
    }

    [SkippableFact]
    public async Task Empty_store_has_no_users()
    {
        Assert.False(await Store.HasAnyUsers());
        Assert.Null(await Store.GetUserByEmail("nobody@example.com"));
        Assert.Null(await Store.GetUserById("usr_none"));
        Assert.Empty(await Store.ListUsers());
    }

    [SkippableFact]
    public async Task Create_then_get_round_trips_the_user_and_flips_HasAnyUsers()
    {
        DateTimeOffset createdAt = DateTimeOffset.Parse("2026-09-21T10:00:00.000Z");
        User created = await Store.CreateUser("Alice@Example.com", "Alice Admin", UserRole.Admin, "argon2id$fake", createdAt);

        Assert.True(await Store.HasAnyUsers());
        Assert.StartsWith("usr_", created.Id, StringComparison.Ordinal);
        Assert.Equal("alice@example.com", created.Email);
        Assert.Equal("Alice Admin", created.DisplayName);
        Assert.Equal(UserRole.Admin, created.Role);
        Assert.Equal("argon2id$fake", created.PasswordHash);
        Assert.Equal(createdAt, created.CreatedAt);
        Assert.Null(created.LastSignInAt);
        Assert.True(created.Active);

        User? byId = await Store.GetUserById(created.Id);
        Assert.NotNull(byId);
        Assert.Equal(created, byId);
    }

    [SkippableFact]
    public async Task Try_create_first_user_succeeds_once_on_an_empty_store()
    {
        DateTimeOffset createdAt = DateTimeOffset.Parse("2026-09-29T10:00:00.000Z");
        User? admin = await Store.TryCreateFirstUser("admin@example.com", "Admin", UserRole.Admin, "argon2id$fake", createdAt);

        Assert.NotNull(admin);
        Assert.StartsWith("usr_", admin!.Id, StringComparison.Ordinal);
        Assert.Equal(UserRole.Admin, admin.Role);
        Assert.True(await Store.HasAnyUsers());
    }

    [SkippableFact]
    public async Task Try_create_first_user_reports_null_and_writes_nothing_once_a_user_already_exists()
    {
        await Store.CreateUser("existing@example.com", "Existing", UserRole.Admin, "hash", DateTimeOffset.UtcNow);

        User? second = await Store.TryCreateFirstUser("second@example.com", "Second", UserRole.Admin, "hash", DateTimeOffset.UtcNow);

        Assert.Null(second);
        IReadOnlyList<User> all = await Store.ListUsers();
        Assert.Single(all);
        Assert.Equal("existing@example.com", all[0].Email);
    }

    /// <summary>
    /// Security review F1.1: a plain "HasAnyUsers then CreateUser" check-then-act leaves a window where many
    /// concurrent first-run requests can all observe an empty table and all create an admin. Firing many at once
    /// against a single store must leave exactly one winner, never more.
    /// </summary>
    [SkippableFact]
    public async Task Try_create_first_user_under_concurrent_callers_admits_exactly_one_winner()
    {
        const int callers = 12;
        var stores = new IUserStore[callers];
        stores[0] = Store;
        for (int i = 1; i < callers; i++)
        {
            stores[i] = await CreateAdditionalStore();
        }

        Task<User?>[] attempts = stores
            .Select((store, i) => store.TryCreateFirstUser($"racer{i}@example.com", $"Racer {i}", UserRole.Admin, "hash", DateTimeOffset.UtcNow))
            .ToArray();

        User?[] results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r is not null);
        IReadOnlyList<User> all = await Store.ListUsers();
        Assert.Single(all);
    }

    [SkippableFact]
    public async Task Email_lookup_is_case_insensitive_but_a_different_email_is_not_found()
    {
        await Store.CreateUser("bob@example.com", "Bob", UserRole.Editor, "hash", DateTimeOffset.UtcNow);

        Assert.NotNull(await Store.GetUserByEmail("BOB@EXAMPLE.COM"));
        Assert.NotNull(await Store.GetUserByEmail("bob@example.com"));
        Assert.Null(await Store.GetUserByEmail("bobby@example.com"));
    }

    [SkippableFact]
    public async Task A_second_user_with_the_same_email_is_rejected()
    {
        await Store.CreateUser("dup@example.com", "First", UserRole.Editor, "hash", DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.CreateUser("DUP@example.com", "Second", UserRole.Editor, "hash", DateTimeOffset.UtcNow));

        // The rejected attempt wrote nothing: only the first user exists.
        IReadOnlyList<User> all = await Store.ListUsers();
        Assert.Single(all);
        Assert.Equal("First", all[0].DisplayName);
    }

    [SkippableFact]
    public async Task List_users_is_ordered_by_email_and_includes_everyone()
    {
        await Store.CreateUser("zoe@example.com", "Zoe", UserRole.Editor, "hash", DateTimeOffset.UtcNow);
        await Store.CreateUser("amy@example.com", "Amy", UserRole.Admin, "hash", DateTimeOffset.UtcNow);

        IReadOnlyList<User> all = await Store.ListUsers();

        Assert.Equal(["amy@example.com", "zoe@example.com"], all.Select(u => u.Email));
    }

    [SkippableFact]
    public async Task Role_password_and_active_updates_change_only_the_named_user()
    {
        User target = await Store.CreateUser("target@example.com", "Target", UserRole.Editor, "old-hash", DateTimeOffset.UtcNow);
        User other = await Store.CreateUser("other@example.com", "Other", UserRole.Editor, "other-hash", DateTimeOffset.UtcNow);

        Assert.True(await Store.UpdateUserRole(target.Id, UserRole.Admin));
        Assert.True(await Store.UpdateUserPassword(target.Id, "new-hash"));
        Assert.True(await Store.SetUserActive(target.Id, false));

        User? updated = await Store.GetUserById(target.Id);
        Assert.NotNull(updated);
        Assert.Equal(UserRole.Admin, updated.Role);
        Assert.Equal("new-hash", updated.PasswordHash);
        Assert.False(updated.Active);

        // The other user was not touched by any of the three updates.
        User? untouched = await Store.GetUserById(other.Id);
        Assert.NotNull(untouched);
        Assert.Equal(UserRole.Editor, untouched.Role);
        Assert.Equal("other-hash", untouched.PasswordHash);
        Assert.True(untouched.Active);
    }

    [SkippableFact]
    public async Task Updating_an_unknown_user_reports_false_and_changes_nothing()
    {
        Assert.False(await Store.UpdateUserRole("usr_missing", UserRole.Admin));
        Assert.False(await Store.UpdateUserPassword("usr_missing", "hash"));
        Assert.False(await Store.SetUserActive("usr_missing", false));
        Assert.False(await Store.RecordSignIn("usr_missing", DateTimeOffset.UtcNow));
    }

    [SkippableFact]
    public async Task Recording_a_sign_in_sets_last_sign_in_at()
    {
        User user = await Store.CreateUser("signer@example.com", "Signer", UserRole.Editor, "hash", DateTimeOffset.UtcNow);
        Assert.Null(user.LastSignInAt);

        DateTimeOffset at = DateTimeOffset.Parse("2026-09-25T08:30:00.000Z");
        Assert.True(await Store.RecordSignIn(user.Id, at));

        User? reloaded = await Store.GetUserById(user.Id);
        Assert.Equal(at, reloaded!.LastSignInAt);
    }

    [SkippableFact]
    public async Task Api_token_round_trips_and_only_its_owners_tokens_are_listed()
    {
        User owner = await Store.CreateUser("owner@example.com", "Owner", UserRole.Editor, "hash", DateTimeOffset.UtcNow);
        User someoneElse = await Store.CreateUser("someone@example.com", "Someone", UserRole.Editor, "hash", DateTimeOffset.UtcNow);
        await Store.CreateApiToken(someoneElse.Id, "not mine", "hash-else", DateTimeOffset.UtcNow);

        DateTimeOffset createdAt = DateTimeOffset.Parse("2026-09-26T09:00:00.000Z");
        ApiToken token = await Store.CreateApiToken(owner.Id, "CI token", "hash-abc123", createdAt);

        Assert.StartsWith("tok_", token.Id, StringComparison.Ordinal);
        Assert.Equal(owner.Id, token.UserId);
        Assert.Equal("CI token", token.Name);
        Assert.Equal("hash-abc123", token.TokenHash);
        Assert.Equal(createdAt, token.CreatedAt);
        Assert.Null(token.LastUsedAt);
        Assert.False(token.Revoked);

        IReadOnlyList<ApiToken> owned = await Store.ListApiTokens(owner.Id);
        Assert.Single(owned);
        Assert.Equal(token.Id, owned[0].Id);

        ApiToken? byId = await Store.GetApiTokenById(token.Id);
        Assert.Equal(token, byId);

        ApiToken? byHash = await Store.GetApiTokenByHash("hash-abc123");
        Assert.Equal(token, byHash);
        Assert.Null(await Store.GetApiTokenByHash("no-such-hash"));
    }

    [SkippableFact]
    public async Task Revoking_and_using_a_token_changes_only_that_token()
    {
        User owner = await Store.CreateUser("tokowner@example.com", "Owner", UserRole.Editor, "hash", DateTimeOffset.UtcNow);
        ApiToken keep = await Store.CreateApiToken(owner.Id, "keep", "hash-keep", DateTimeOffset.UtcNow);
        ApiToken revoke = await Store.CreateApiToken(owner.Id, "revoke", "hash-revoke", DateTimeOffset.UtcNow);

        Assert.True(await Store.RevokeApiToken(revoke.Id));
        DateTimeOffset usedAt = DateTimeOffset.Parse("2026-09-27T00:00:00.000Z");
        Assert.True(await Store.RecordApiTokenUse(keep.Id, usedAt));

        ApiToken? reloadedRevoked = await Store.GetApiTokenById(revoke.Id);
        Assert.True(reloadedRevoked!.Revoked);
        Assert.Null(reloadedRevoked.LastUsedAt);

        ApiToken? reloadedKept = await Store.GetApiTokenById(keep.Id);
        Assert.False(reloadedKept!.Revoked);
        Assert.Equal(usedAt, reloadedKept.LastUsedAt);

        Assert.False(await Store.RevokeApiToken("tok_missing"));
        Assert.False(await Store.RecordApiTokenUse("tok_missing", DateTimeOffset.UtcNow));
    }

    /// <summary>Security review F8: a password reset revokes every one of the user's tokens, not just one.</summary>
    [SkippableFact]
    public async Task Revoking_all_of_a_users_tokens_leaves_other_users_tokens_alone()
    {
        User owner = await Store.CreateUser("multi-token@example.com", "Owner", UserRole.Editor, "hash", DateTimeOffset.UtcNow);
        User other = await Store.CreateUser("other-owner@example.com", "Other", UserRole.Editor, "hash", DateTimeOffset.UtcNow);
        ApiToken first = await Store.CreateApiToken(owner.Id, "first", "hash-first", DateTimeOffset.UtcNow);
        ApiToken second = await Store.CreateApiToken(owner.Id, "second", "hash-second", DateTimeOffset.UtcNow);
        ApiToken othersToken = await Store.CreateApiToken(other.Id, "other's", "hash-others", DateTimeOffset.UtcNow);

        int revoked = await Store.RevokeApiTokensForUser(owner.Id);

        Assert.Equal(2, revoked);
        Assert.True((await Store.GetApiTokenById(first.Id))!.Revoked);
        Assert.True((await Store.GetApiTokenById(second.Id))!.Revoked);
        Assert.False((await Store.GetApiTokenById(othersToken.Id))!.Revoked);

        // Already-revoked tokens do not get counted again.
        Assert.Equal(0, await Store.RevokeApiTokensForUser(owner.Id));
    }

    /// <summary>Security review F9: demoting or deactivating the last other active admin is refused atomically.</summary>
    [SkippableFact]
    public async Task Try_demote_and_deactivate_admin_refuse_to_leave_zero_active_admins()
    {
        User first = await Store.CreateUser("f9-first@example.com", "First", UserRole.Admin, "hash", DateTimeOffset.UtcNow);
        User second = await Store.CreateUser("f9-second@example.com", "Second", UserRole.Admin, "hash", DateTimeOffset.UtcNow);

        // Two active admins: demoting one leaves the other, so it succeeds.
        Assert.True(await Store.TryDemoteAdmin(second.Id, UserRole.Editor));
        Assert.Equal(UserRole.Editor, (await Store.GetUserById(second.Id))!.Role);

        // Now only "first" is an active admin: demoting or deactivating it must be refused, unchanged.
        Assert.False(await Store.TryDemoteAdmin(first.Id, UserRole.Editor));
        Assert.Equal(UserRole.Admin, (await Store.GetUserById(first.Id))!.Role);

        Assert.False(await Store.TryDeactivateAdmin(first.Id));
        Assert.True((await Store.GetUserById(first.Id))!.Active);

        // Promoting "second" back restores a second active admin, so deactivating "first" now succeeds.
        await Store.UpdateUserRole(second.Id, UserRole.Admin);
        Assert.True(await Store.TryDeactivateAdmin(first.Id));
        Assert.False((await Store.GetUserById(first.Id))!.Active);
    }

    [SkippableFact]
    public async Task Session_round_trips_and_revocation_is_scoped_to_the_right_rows()
    {
        User a = await Store.CreateUser("sessiona@example.com", "A", UserRole.Editor, "hash", DateTimeOffset.UtcNow);
        User b = await Store.CreateUser("sessionb@example.com", "B", UserRole.Editor, "hash", DateTimeOffset.UtcNow);

        DateTimeOffset createdAt = DateTimeOffset.Parse("2026-09-28T00:00:00.000Z");
        DateTimeOffset expiresAt = createdAt.AddDays(30);
        Session sessionA1 = await Store.CreateSession(a.Id, "hash-a1", createdAt, expiresAt);
        Session sessionA2 = await Store.CreateSession(a.Id, "hash-a2", createdAt, expiresAt);
        Session sessionB = await Store.CreateSession(b.Id, "hash-b", createdAt, expiresAt);

        Assert.StartsWith("sess_", sessionA1.Id, StringComparison.Ordinal);
        Assert.Equal(a.Id, sessionA1.UserId);
        Assert.Equal(expiresAt, sessionA1.ExpiresAt);
        Assert.False(sessionA1.Revoked);

        Session? byHash = await Store.GetSessionByHash("hash-a1");
        Assert.Equal(sessionA1, byHash);
        Assert.Null(await Store.GetSessionByHash("no-such-hash"));

        Assert.True(await Store.RevokeSession(sessionA1.Id));
        Assert.True((await Store.GetSessionByHash("hash-a1"))!.Revoked);
        // Revoking one session leaves this user's other session, and the other user's session, alone.
        Assert.False((await Store.GetSessionByHash("hash-a2"))!.Revoked);
        Assert.False((await Store.GetSessionByHash("hash-b"))!.Revoked);

        int revokedCount = await Store.RevokeSessionsForUser(a.Id);
        Assert.Equal(1, revokedCount); // sessionA1 was already revoked; only sessionA2 was still active.
        Assert.True((await Store.GetSessionByHash("hash-a2"))!.Revoked);
        Assert.False((await Store.GetSessionByHash("hash-b"))!.Revoked);

        Assert.False(await Store.RevokeSession("sess_missing"));
    }

    /// <summary>AU-05: expiry and scope are stored per token and come back exactly; an omitted expiry stays null (the grandfathered form).</summary>
    [SkippableFact]
    public async Task Api_token_expiry_and_scope_round_trip_and_default_to_no_expiry_and_full()
    {
        User owner = await Store.CreateUser("scope-owner@example.com", "Owner", UserRole.Editor, "hash", DateTimeOffset.UtcNow);
        DateTimeOffset createdAt = DateTimeOffset.Parse("2026-09-30T09:00:00.000Z");
        DateTimeOffset expiresAt = createdAt.AddDays(90);

        ApiToken limited = await Store.CreateApiToken(owner.Id, "limited", "hash-limited", createdAt, expiresAt, ApiTokenScope.Read);
        ApiToken plain = await Store.CreateApiToken(owner.Id, "plain", "hash-plain", createdAt);

        Assert.Equal(expiresAt, limited.ExpiresAt);
        Assert.Equal(ApiTokenScope.Read, limited.Scope);
        Assert.Null(plain.ExpiresAt);
        Assert.Equal(ApiTokenScope.Full, plain.Scope);

        // Reloaded from the database, not merely echoed back by the create call.
        Assert.Equal(limited, await Store.GetApiTokenByHash("hash-limited"));
        Assert.Equal(plain, await Store.GetApiTokenById(plain.Id));
        Assert.NotEqual(limited.Scope, (await Store.GetApiTokenById(plain.Id))!.Scope);
    }

    /// <summary>AU-03: changing your own password ends every other session of yours and only those, never the one in use.</summary>
    [SkippableFact]
    public async Task Revoking_other_sessions_keeps_the_named_one_and_other_users_sessions()
    {
        User a = await Store.CreateUser("keep-a@example.com", "A", UserRole.Editor, "hash", DateTimeOffset.UtcNow);
        User b = await Store.CreateUser("keep-b@example.com", "B", UserRole.Editor, "hash", DateTimeOffset.UtcNow);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Session current = await Store.CreateSession(a.Id, "hash-keep-current", now, now.AddDays(30));
        await Store.CreateSession(a.Id, "hash-keep-old1", now, now.AddDays(30));
        await Store.CreateSession(a.Id, "hash-keep-old2", now, now.AddDays(30));
        await Store.CreateSession(b.Id, "hash-keep-b", now, now.AddDays(30));

        int revoked = await Store.RevokeOtherSessionsForUser(a.Id, current.Id);

        Assert.Equal(2, revoked);
        Assert.False((await Store.GetSessionByHash("hash-keep-current"))!.Revoked);
        Assert.True((await Store.GetSessionByHash("hash-keep-old1"))!.Revoked);
        Assert.True((await Store.GetSessionByHash("hash-keep-old2"))!.Revoked);
        Assert.False((await Store.GetSessionByHash("hash-keep-b"))!.Revoked);
        Assert.Equal(0, await Store.RevokeOtherSessionsForUser(a.Id, current.Id));
    }
}
