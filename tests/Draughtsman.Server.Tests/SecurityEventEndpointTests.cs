using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// AU-04: every sign-in, throttle trip, password change, role change, token create/revoke and account create or
/// deactivate leaves a row an admin can read, with actor, target, source IP and time, and never a secret.
/// </summary>
public class SecurityEventEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public SecurityEventEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Sign_in_success_and_both_kinds_of_failure_are_recorded_and_the_responses_do_not_tell_them_apart()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("who-signs-in@example.com");

        HttpClient stranger = _factory.CreateClient();
        HttpResponseMessage wrongPassword = await stranger.PostAsJsonAsync("/api/auth/login", new { email = "who-signs-in@example.com", password = "definitely-not-the-password" });
        HttpResponseMessage noSuchAccount = await stranger.PostAsJsonAsync("/api/auth/login", new { email = "nobody-home@example.com", password = "definitely-not-the-password" });
        await _factory.SignIn("who-signs-in@example.com");

        // The caller cannot tell an unknown account from a wrong password: same status, same body.
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noSuchAccount.StatusCode);
        Assert.Equal(WithoutTrace(await wrongPassword.Json()), WithoutTrace(await noSuchAccount.Json()));

        // The admin can: the reason is in the log.
        JsonArray failures = await admin.SecurityEvents("?type=sign_in.failed&limit=200");
        JsonNode wrong = failures.Single(e => e!["targetLabel"]!.GetValue<string>() == "who-signs-in@example.com")!;
        Assert.Equal(id, wrong["targetUserId"]!.GetValue<string>());
        Assert.Equal("wrong password", wrong["detail"]!.GetValue<string>());
        JsonNode unknown = failures.Single(e => e!["targetLabel"]!.GetValue<string>() == "nobody-home@example.com")!;
        Assert.Null(unknown["targetUserId"]);
        Assert.Equal("unknown account", unknown["detail"]!.GetValue<string>());

        JsonArray successes = await admin.SecurityEvents($"?type=sign_in.succeeded&user={id}");
        JsonNode success = Assert.Single(successes)!;
        Assert.Equal(id, success["actorUserId"]!.GetValue<string>());
        Assert.Equal("who-signs-in@example.com", success["actorLabel"]!.GetValue<string>());
        Assert.True(DateTimeOffset.UtcNow - success["at"]!.GetValue<DateTimeOffset>() < TimeSpan.FromMinutes(1));
    }

    /// <summary>The problem body minus the per-request trace id, which legitimately differs.</summary>
    private static string WithoutTrace(JsonNode problem)
    {
        JsonObject copy = problem.DeepClone().AsObject();
        copy.Remove("traceId");
        return copy.ToJsonString();
    }

    [Fact]
    public async Task No_password_hash_or_typed_secret_ever_reaches_the_log()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        await admin.CreateUser("no-secrets@example.com");
        HttpClient stranger = _factory.CreateClient();

        // A password typed into the email box: it must not be stored as a "target label".
        await stranger.PostAsJsonAsync("/api/auth/login", new { email = "my secret passphrase is hunter2", password = "x" });
        await stranger.PostAsJsonAsync("/api/auth/login", new { email = "no-secrets@example.com", password = "wrong-but-very-specific-secret-value" });
        await _factory.SignIn("no-secrets@example.com");

        string everything = (await admin.SecurityEvents("?limit=200")).ToJsonString();
        Assert.DoesNotContain("hunter2", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-but-very-specific-secret-value", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(AuthBarHelpers.UserPassword, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("argon2id", everything, StringComparison.Ordinal);

        // And the same assertion has teeth: the failed attempts themselves ARE there, so a missing secret is not a missing log.
        Assert.Contains(await admin.SecurityEvents("?type=sign_in.failed&limit=200"), e => e!["detail"]!.GetValue<string>() == "unknown account" && e["targetLabel"] is null);
    }

    [Fact]
    public async Task Account_and_role_and_password_and_deactivation_changes_are_recorded_with_who_did_what_to_whom()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string adminId = (await (await admin.GetAsync("/api/auth/status")).Json())["user"]!["id"]!.GetValue<string>();
        string id = await admin.CreateUser("changes@example.com");

        foreach (object change in new object[] { new { role = "Admin" }, new { password = "another-long-password" }, new { active = false }, new { active = true } })
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Patch, $"/api/users/{id}", change, admin))).StatusCode);
        }

        JsonArray events = await admin.SecurityEvents($"?user={id}&limit=200");
        string[] types = events.Select(e => e!["type"]!.GetValue<string>()).ToArray();
        Assert.Equal(
            ["user.reactivated", "user.deactivated", "password.reset", "user.role_changed", "user.created"],
            types.Where(t => t.StartsWith("user.", StringComparison.Ordinal) || t == "password.reset").ToArray());
        Assert.All(events, e =>
        {
            Assert.Equal(adminId, e!["actorUserId"]!.GetValue<string>());
            Assert.Equal(id, e["targetUserId"]!.GetValue<string>());
            Assert.Equal("session", e["method"]!.GetValue<string>());
        });
        Assert.Equal("Editor to Admin", events.Single(e => e!["type"]!.GetValue<string>() == "user.role_changed")!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_change_that_changes_nothing_is_not_recorded_as_a_change()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("no-op@example.com", role: "Editor");

        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Patch, $"/api/users/{id}", new { role = "Editor", active = true }, admin))).StatusCode);

        string[] types = (await admin.SecurityEvents($"?user={id}")).Select(e => e!["type"]!.GetValue<string>()).ToArray();
        Assert.Equal(["user.created"], types);
    }

    [Fact]
    public async Task Token_create_and_revoke_are_recorded_without_the_secret()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string ownerId = await admin.CreateUser("token-events@example.com");
        HttpClient owner = await _factory.SignIn("token-events@example.com");
        (string tokenId, string secret) = await owner.CreateToken("laptop agent");

        Assert.Equal(HttpStatusCode.NoContent, (await owner.SendAsync(AuthBarHelpers.Request(HttpMethod.Delete, $"/api/tokens/{tokenId}", withCsrfFrom: owner))).StatusCode);

        JsonArray events = await admin.SecurityEvents($"?user={ownerId}&limit=200");
        JsonNode created = events.Single(e => e!["type"]!.GetValue<string>() == "token.created")!;
        JsonNode revoked = events.Single(e => e!["type"]!.GetValue<string>() == "token.revoked")!;
        Assert.Contains(tokenId, created["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("laptop agent", created["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(tokenId, revoked["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(ownerId, created["actorUserId"]!.GetValue<string>());
        Assert.DoesNotContain(secret, events.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret[4..], events.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoking_a_token_through_a_token_is_recorded_as_a_token_action_and_an_admin_revoking_anothers_names_the_admin()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string adminId = (await (await admin.GetAsync("/api/auth/status")).Json())["user"]!["id"]!.GetValue<string>();
        string ownerId = await admin.CreateUser("revoked-by-admin@example.com");
        HttpClient owner = await _factory.SignIn("revoked-by-admin@example.com");
        (string tokenId, string secret) = await owner.CreateToken("to be revoked");

        // The owner's own token revokes itself: method is token.
        (string selfId, string selfSecret) = await owner.CreateToken("revokes itself");
        HttpClient selfBearer = _factory.Bearer(selfSecret);
        Assert.Equal(HttpStatusCode.NoContent, (await selfBearer.DeleteAsync($"/api/tokens/{selfId}")).StatusCode);

        // The admin revokes the owner's other token: actor is the admin, target the owner.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Delete, $"/api/tokens/{tokenId}", withCsrfFrom: admin))).StatusCode);

        JsonArray revocations = await admin.SecurityEvents($"?type=token.revoked&user={ownerId}");
        JsonNode bySelf = revocations.Single(e => e!["detail"]!.GetValue<string>().Contains(selfId, StringComparison.Ordinal))!;
        Assert.Equal("token", bySelf["method"]!.GetValue<string>());
        Assert.Equal(ownerId, bySelf["actorUserId"]!.GetValue<string>());
        JsonNode byAdmin = revocations.Single(e => e!["detail"]!.GetValue<string>().Contains(tokenId, StringComparison.Ordinal))!;
        Assert.Equal("session", byAdmin["method"]!.GetValue<string>());
        Assert.Equal(adminId, byAdmin["actorUserId"]!.GetValue<string>());
        Assert.Equal(ownerId, byAdmin["targetUserId"]!.GetValue<string>());
        Assert.NotNull(secret);
    }

    [Fact]
    public async Task Sign_out_is_recorded()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("signs-out@example.com");
        HttpClient user = await _factory.SignIn("signs-out@example.com");

        Assert.Equal(HttpStatusCode.NoContent, (await user.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/auth/logout", withCsrfFrom: user))).StatusCode);

        JsonNode signedOut = Assert.Single(await admin.SecurityEvents($"?type=sign_out&user={id}"))!;
        Assert.Equal(id, signedOut["actorUserId"]!.GetValue<string>());
    }

    [Fact]
    public async Task First_run_and_a_bad_setup_code_are_recorded()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();
        string setupCode = factory.Services.GetRequiredService<SetupCodeGate>().Issue();
        await client.PostAsJsonAsync("/api/auth/first-run", new { email = "boss@example.com", displayName = "Boss", password = AuthBarHelpers.UserPassword, setupCode = "WRONGCODE1" });
        HttpResponseMessage ok = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "boss@example.com", displayName = "Boss", password = AuthBarHelpers.UserPassword, setupCode });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        client.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, ok.Cookie("XSRF-TOKEN")!);

        string[] types = (await client.SecurityEvents()).Select(e => e!["type"]!.GetValue<string>()).ToArray();
        Assert.Equal(["first_run.completed", "first_run.failed"], types);
        Assert.DoesNotContain("WRONGCODE1", (await client.SecurityEvents()).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_throttle_trip_is_recorded_once_however_many_requests_it_then_refuses()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("throttled@example.com");
        HttpClient attacker = factory.CreateClient();

        for (int i = 0; i < FailedSignInThrottle.MaxAttempts; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.PostAsJsonAsync("/api/auth/login", new { email = "throttled@example.com", password = "wrong-" + i })).StatusCode);
        }

        // Five refused requests in a row after the budget is spent: each is a 429, but the trip is one event.
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, (await attacker.PostAsJsonAsync("/api/auth/login", new { email = "throttled@example.com", password = "wrong-again" })).StatusCode);
        }

        JsonArray trips = await admin.SecurityEvents("?type=sign_in.throttled");
        JsonNode trip = Assert.Single(trips)!;
        Assert.Equal("throttled@example.com", trip["targetLabel"]!.GetValue<string>());
        Assert.Contains("one account", trip["detail"]!.GetValue<string>(), StringComparison.Ordinal);
        // The failures themselves are each recorded (bounded by the throttle), the refused requests are not.
        Assert.Equal(FailedSignInThrottle.MaxAttempts, (await admin.SecurityEvents($"?type=sign_in.failed&user={id}")).Count);
    }

    [Fact]
    public async Task The_source_ip_is_recorded_and_filterable()
    {
        using var factory = new FakeIpServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string id = await admin.CreateUser("ip-user@example.com");

        HttpClient user = factory.CreateClient();
        user.DefaultRequestHeaders.Add(FakeIpServerFactory.Header, "203.0.113.77");
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsJsonAsync("/api/auth/login", new { email = "ip-user@example.com", password = AuthBarHelpers.UserPassword })).StatusCode);
        // A different client (the first is signed in now, so a cookie POST would need CSRF) from another address.
        HttpClient elsewhere = factory.CreateClient();
        elsewhere.DefaultRequestHeaders.Add(FakeIpServerFactory.Header, "198.51.100.5");
        await elsewhere.PostAsJsonAsync("/api/auth/login", new { email = "ip-user@example.com", password = "wrong-password-here" });

        JsonNode fromFirst = Assert.Single(await admin.SecurityEvents($"?ip=203.0.113.77&user={id}"))!;
        Assert.Equal("sign_in.succeeded", fromFirst["type"]!.GetValue<string>());
        Assert.Equal("203.0.113.77", fromFirst["sourceIp"]!.GetValue<string>());
        JsonNode fromSecond = Assert.Single(await admin.SecurityEvents("?ip=198.51.100.5"))!;
        Assert.Equal("sign_in.failed", fromSecond["type"]!.GetValue<string>());
        Assert.Empty(await admin.SecurityEvents("?ip=192.0.2.1"));
    }

    [Fact]
    public async Task Hostile_free_text_cannot_forge_a_log_line_or_overflow_a_column()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        HttpClient owner;
        await admin.CreateUser("hostile-name@example.com");
        owner = await _factory.SignIn("hostile-name@example.com");

        (string id, _) = await owner.CreateToken("line one\nFAKE LOG LINE: admin signed in\r\n" + new string('x', 200));

        JsonNode created = (await admin.SecurityEvents("?type=token.created&limit=200")).Single(e => e!["detail"]!.GetValue<string>().Contains(id, StringComparison.Ordinal))!;
        string detail = created["detail"]!.GetValue<string>();
        Assert.DoesNotContain('\n', detail);
        Assert.DoesNotContain('\r', detail);
        Assert.True(detail.Length <= 512);
    }
}
