using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Auth;

namespace Draughtsman.Server.Tests;

/// <summary>AU-03: a signed-in user changes their own password (current one required), and every other session of theirs ends.</summary>
public class PasswordChangeTests : IClassFixture<DraughtsmanServerFactory>
{
    private const string NewPassword = "a-brand-new-password";

    private readonly DraughtsmanServerFactory _factory;

    public PasswordChangeTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Changing_the_password_switches_which_password_signs_in_and_ends_other_sessions_but_not_this_one()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("changer@example.com");
        HttpClient thisBrowser = await _factory.SignIn("changer@example.com");
        HttpClient otherBrowser = await _factory.SignIn("changer@example.com");
        HttpClient thirdBrowser = await _factory.SignIn("changer@example.com");
        Assert.Equal(HttpStatusCode.OK, (await otherBrowser.GetAsync("/api/documents")).StatusCode);

        HttpResponseMessage changed = await thisBrowser.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/auth/password",
            new { currentPassword = AuthBarHelpers.UserPassword, newPassword = NewPassword }, thisBrowser));
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // This session survives; the others are gone.
        Assert.Equal(HttpStatusCode.OK, (await thisBrowser.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherBrowser.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await thirdBrowser.GetAsync("/api/documents")).StatusCode);

        // The old password is dead, the new one works.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "changer@example.com", password = AuthBarHelpers.UserPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "changer@example.com", password = NewPassword })).StatusCode);

        JsonNode recorded = Assert.Single(await admin.SecurityEvents($"?type=password.changed&user={id}"))!;
        Assert.Equal(id, recorded["actorUserId"]!.GetValue<string>());
        Assert.Equal("2 other session(s) revoked", recorded["detail"]!.GetValue<string>());
        Assert.DoesNotContain(NewPassword, (await admin.SecurityEvents("?limit=200")).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_current_password_changes_nothing_and_says_so_generically()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("wrong-current@example.com");
        HttpClient user = await _factory.SignIn("wrong-current@example.com");
        HttpClient other = await _factory.SignIn("wrong-current@example.com");

        HttpResponseMessage refused = await user.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/auth/password",
            new { currentPassword = "not-my-current-password", newPassword = NewPassword }, user));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("current password is incorrect", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        // Nothing moved: the old password still signs in, the new one does not, and the other session is alive.
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "wrong-current@example.com", password = AuthBarHelpers.UserPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "wrong-current@example.com", password = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/documents")).StatusCode);
        JsonNode failed = Assert.Single(await admin.SecurityEvents($"?type=password.change_failed&user={id}"))!;
        Assert.Equal("wrong current password", failed["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_too_short_or_unchanged_new_password_or_a_missing_current_one_is_refused()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        await admin.CreateUser("validation@example.com");
        HttpClient user = await _factory.SignIn("validation@example.com");

        foreach (object bad in new object[]
                 {
                     new { currentPassword = AuthBarHelpers.UserPassword, newPassword = "short" },
                     new { currentPassword = AuthBarHelpers.UserPassword, newPassword = AuthBarHelpers.UserPassword },
                     new { currentPassword = "", newPassword = NewPassword },
                     new { newPassword = NewPassword },
                     new { currentPassword = AuthBarHelpers.UserPassword },
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await user.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/auth/password", bad, user))).StatusCode);
        }

        // Still the old password.
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "validation@example.com", password = AuthBarHelpers.UserPassword })).StatusCode);
    }

    [Fact]
    public async Task Guessing_the_current_password_is_throttled_even_for_the_correct_guess()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await admin.CreateUser("guessed-at@example.com");
        HttpClient hijacked = await factory.SignIn("guessed-at@example.com");

        for (int i = 0; i < FailedSignInThrottle.MaxAttempts; i++)
        {
            HttpResponseMessage guess = await hijacked.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/auth/password", new { currentPassword = "wrong-guess-" + i, newPassword = NewPassword }, hijacked));
            Assert.Equal(HttpStatusCode.BadRequest, guess.StatusCode);
        }

        // Budget spent: now even the right current password is refused, and unchanged.
        HttpResponseMessage blocked = await hijacked.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/auth/password", new { currentPassword = AuthBarHelpers.UserPassword, newPassword = NewPassword }, hijacked));
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "guessed-at@example.com", password = AuthBarHelpers.UserPassword })).StatusCode);
        Assert.Single(await admin.SecurityEvents("?type=sign_in.throttled"));
    }

    [Fact]
    public async Task A_correct_change_does_not_spend_the_guess_budget()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        await admin.CreateUser("changes-often@example.com");
        HttpClient user = await factory.SignIn("changes-often@example.com");

        string current = AuthBarHelpers.UserPassword;
        for (int i = 0; i < FailedSignInThrottle.MaxAttempts + 2; i++)
        {
            string next = $"rotating-password-{i:00}";
            Assert.Equal(HttpStatusCode.NoContent, (await user.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/auth/password", new { currentPassword = current, newPassword = next }, user))).StatusCode);
            current = next;
        }
    }

    [Fact]
    public async Task Only_a_browser_session_can_change_a_password_not_a_token_and_not_without_csrf()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        await admin.CreateUser("session-only@example.com");
        HttpClient user = await _factory.SignIn("session-only@example.com");
        (_, string secret) = await user.CreateToken("agent");
        object body = new { currentPassword = AuthBarHelpers.UserPassword, newPassword = NewPassword };

        // A token holder, even knowing the current password, cannot change it through the API.
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.Bearer(secret).PostAsJsonAsync("/api/auth/password", body)).StatusCode);
        // Anonymous: no.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/password", body)).StatusCode);
        // A cookie session that does not send the CSRF header: no. (A client that signed in but never adds the header.)
        HttpClient noCsrf = _factory.CreateClient();
        await noCsrf.PostAsJsonAsync("/api/auth/login", new { email = "session-only@example.com", password = AuthBarHelpers.UserPassword });
        Assert.Equal(HttpStatusCode.Forbidden, (await noCsrf.PostAsJsonAsync("/api/auth/password", body)).StatusCode);
        // With it: yes (so the refusals above were for the reasons given).
        Assert.Equal(HttpStatusCode.NoContent, (await user.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/auth/password", body, user))).StatusCode);
    }

    [Fact]
    public async Task A_users_tokens_survive_a_self_service_change_unlike_an_admin_reset()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("keeps-tokens@example.com");
        HttpClient user = await _factory.SignIn("keeps-tokens@example.com");
        (_, string secret) = await user.CreateToken("agent");

        await user.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/auth/password", new { currentPassword = AuthBarHelpers.UserPassword, newPassword = NewPassword }, user));
        Assert.Equal(HttpStatusCode.OK, (await _factory.Bearer(secret).GetAsync("/api/documents")).StatusCode);

        // The admin reset, by contrast, revokes it (security review F8, unchanged).
        await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Patch, $"/api/users/{id}", new { password = "admin-set-password-1" }, admin));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.Bearer(secret).GetAsync("/api/documents")).StatusCode);
    }
}
