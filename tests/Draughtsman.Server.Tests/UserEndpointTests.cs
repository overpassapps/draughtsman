using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>The admin Users page's API: list, add local user, change role, reset password, deactivate (build-spec-v0.1.md section 7).</summary>
public class UserEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public UserEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Admin_can_create_list_and_update_a_user()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string xsrf = admin.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();

        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/users",
            new { email = "jane@example.com", displayName = "Jane", role = "Editor", password = "correcthorsebattery" });
        JsonNode createdBody = await ReadJson(created);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string janeId = createdBody["id"]!.GetValue<string>();
        Assert.Equal("Editor", createdBody["role"]!.GetValue<string>());
        Assert.True(createdBody["active"]!.GetValue<bool>());

        JsonNode listed = await ReadJson(await admin.GetAsync("/api/users"));
        Assert.Contains(listed.AsArray(), u => u!["id"]!.GetValue<string>() == janeId);

        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/users/{janeId}") { Content = JsonContent.Create(new { role = "Admin" }) };
        patch.Headers.Add(AuthTestHelpers.XsrfHeaderName, xsrf);
        HttpResponseMessage patched = await admin.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("Admin", (await ReadJson(patched))["role"]!.GetValue<string>());

        // A partial PATCH body touches only the field it names - role changed above, but Jane is still active.
        JsonNode reloaded = await ReadJson(await admin.GetAsync("/api/users"));
        JsonNode janeReloaded = reloaded.AsArray().Single(u => u!["id"]!.GetValue<string>() == janeId)!;
        Assert.True(janeReloaded["active"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Deactivating_a_user_signs_out_their_existing_session()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string adminXsrf = admin.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();

        await admin.PostAsJsonAsync("/api/users", new { email = "deactivate-me@example.com", displayName = "Bye", role = "Editor", password = "correcthorsebattery" });
        HttpClient bob = _factory.CreateClient();
        HttpResponseMessage login = await bob.PostAsJsonAsync("/api/auth/login", new { email = "deactivate-me@example.com", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/documents")).StatusCode);

        JsonNode listed = await ReadJson(await admin.GetAsync("/api/users"));
        string bobId = listed.AsArray().Single(u => u!["email"]!.GetValue<string>() == "deactivate-me@example.com")!["id"]!.GetValue<string>();

        var deactivate = new HttpRequestMessage(HttpMethod.Patch, $"/api/users/{bobId}") { Content = JsonContent.Create(new { active = false }) };
        deactivate.Headers.Add(AuthTestHelpers.XsrfHeaderName, adminXsrf);
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(deactivate)).StatusCode);

        // Bob's still-cookied client is refused the moment his account is deactivated - not merely "cannot log in again".
        Assert.Equal(HttpStatusCode.Unauthorized, (await bob.GetAsync("/api/documents")).StatusCode);
        HttpResponseMessage loginAgain = await bob.PostAsJsonAsync("/api/auth/login", new { email = "deactivate-me@example.com", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.Unauthorized, loginAgain.StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_deactivate_or_demote_their_own_account()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string xsrf = admin.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();
        JsonNode status = await ReadJson(await admin.GetAsync("/api/auth/status"));
        string selfId = status["user"]!["id"]!.GetValue<string>();

        var deactivateSelf = new HttpRequestMessage(HttpMethod.Patch, $"/api/users/{selfId}") { Content = JsonContent.Create(new { active = false }) };
        deactivateSelf.Headers.Add(AuthTestHelpers.XsrfHeaderName, xsrf);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(deactivateSelf)).StatusCode);

        var demoteSelf = new HttpRequestMessage(HttpMethod.Patch, $"/api/users/{selfId}") { Content = JsonContent.Create(new { role = "Editor" }) };
        demoteSelf.Headers.Add(AuthTestHelpers.XsrfHeaderName, xsrf);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(demoteSelf)).StatusCode);

        // Neither guard actually changed anything.
        JsonNode reloaded = await ReadJson(await admin.GetAsync("/api/auth/status"));
        Assert.True(reloaded["user"]!["active"]!.GetValue<bool>());
        Assert.Equal("Admin", reloaded["user"]!["role"]!.GetValue<string>());
    }

    /// <summary>Security review F8: a password reset revokes the target's API tokens too, not only their sessions.</summary>
    [Fact]
    public async Task Resetting_a_users_password_revokes_their_api_tokens()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string adminXsrf = admin.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();

        await admin.PostAsJsonAsync("/api/users", new { email = "token-owner@example.com", displayName = "Token Owner", role = "Editor", password = "correcthorsebattery" });
        HttpClient owner = _factory.CreateClient();
        HttpResponseMessage login = await owner.PostAsJsonAsync("/api/auth/login", new { email = "token-owner@example.com", password = "correcthorsebattery" });
        owner.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, ExtractCookieValue(login, "XSRF-TOKEN")!);
        HttpResponseMessage tokenResponse = await owner.PostAsJsonAsync("/api/tokens", new { name = "owner's token" });
        string secret = (await ReadJson(tokenResponse))["token"]!.GetValue<string>();

        HttpClient bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/api/documents")).StatusCode);

        JsonNode listed = await ReadJson(await admin.GetAsync("/api/users"));
        string ownerId = listed.AsArray().Single(u => u!["email"]!.GetValue<string>() == "token-owner@example.com")!["id"]!.GetValue<string>();
        var resetPassword = new HttpRequestMessage(HttpMethod.Patch, $"/api/users/{ownerId}") { Content = JsonContent.Create(new { password = "a-brand-new-password" }) };
        resetPassword.Headers.Add(AuthTestHelpers.XsrfHeaderName, adminXsrf);
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(resetPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/documents")).StatusCode);
    }

    /// <summary>
    /// Security review F9: demoting or deactivating a non-last admin is unaffected - the guard only refuses a change
    /// that would leave zero active admins, never a change that leaves one or more.
    /// </summary>
    [Fact]
    public async Task Demoting_a_non_last_admin_still_succeeds()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string adminXsrf = admin.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();

        HttpResponseMessage createSecond = await admin.PostAsJsonAsync("/api/users",
            new { email = "demotable-admin@example.com", displayName = "Demotable Admin", role = "Admin", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.Created, createSecond.StatusCode);
        string secondId = (await ReadJson(createSecond))["id"]!.GetValue<string>();

        // The caller remains an active admin throughout, so demoting this other admin leaves one (the caller) - fine.
        var demote = new HttpRequestMessage(HttpMethod.Patch, $"/api/users/{secondId}") { Content = JsonContent.Create(new { role = "Editor" }) };
        demote.Headers.Add(AuthTestHelpers.XsrfHeaderName, adminXsrf);
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(demote)).StatusCode);
    }

    /// <summary>
    /// Security review F9: two admins concurrently demoting *each other* (never themselves, so the pre-existing
    /// self-check does not apply to either) must not both succeed - each one's own check-then-write happening to
    /// interleave with the other's must not be able to leave the instance with zero active admins between them.
    /// Its own, fresh factory: this deliberately reduces the instance to exactly two admins, which the class's
    /// shared fixture (every other test in this file) must not have touched out from under it.
    /// </summary>
    [Fact]
    public async Task Two_admins_concurrently_demoting_each_other_cannot_both_succeed()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string adminXsrf = admin.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();
        JsonNode adminStatus = await ReadJson(await admin.GetAsync("/api/auth/status"));
        string adminId = adminStatus["user"]!["id"]!.GetValue<string>();

        HttpResponseMessage createSecond = await admin.PostAsJsonAsync("/api/users",
            new { email = "race-admin@example.com", displayName = "Race Admin", role = "Admin", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.Created, createSecond.StatusCode);
        string secondId = (await ReadJson(createSecond))["id"]!.GetValue<string>();

        HttpClient second = factory.CreateClient();
        HttpResponseMessage secondLogin = await second.PostAsJsonAsync("/api/auth/login", new { email = "race-admin@example.com", password = "correcthorsebattery" });
        string secondXsrf = ExtractCookieValue(secondLogin, "XSRF-TOKEN")!;
        second.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, secondXsrf);

        var adminDemotesSecond = new HttpRequestMessage(HttpMethod.Patch, $"/api/users/{secondId}") { Content = JsonContent.Create(new { role = "Editor" }) };
        adminDemotesSecond.Headers.Add(AuthTestHelpers.XsrfHeaderName, adminXsrf);
        var secondDemotesAdmin = new HttpRequestMessage(HttpMethod.Patch, $"/api/users/{adminId}") { Content = JsonContent.Create(new { role = "Editor" }) };
        secondDemotesAdmin.Headers.Add(AuthTestHelpers.XsrfHeaderName, secondXsrf);

        Task<HttpResponseMessage> first = admin.SendAsync(adminDemotesSecond);
        Task<HttpResponseMessage> other = second.SendAsync(secondDemotesAdmin);
        await Task.WhenAll(first, other);

        using IServiceScope scope = factory.Services.CreateScope();
        IUserStore users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        IReadOnlyList<User> all = await users.ListUsers();
        Assert.Contains(all, u => u.Role == UserRole.Admin && u.Active);
    }

    [Fact]
    public async Task An_editor_cannot_reach_the_admin_only_users_endpoint()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        await admin.PostAsJsonAsync("/api/users", new { email = "not-an-admin@example.com", displayName = "Editor", role = "Editor", password = "correcthorsebattery" });

        HttpClient editor = _factory.CreateClient();
        await editor.PostAsJsonAsync("/api/auth/login", new { email = "not-an-admin@example.com", password = "correcthorsebattery" });

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/users")).StatusCode);
    }

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body.");
    }

    private static string? ExtractCookieValue(HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return null;
        }

        foreach (string raw in values)
        {
            string namePart = raw.Split(';')[0];
            int eq = namePart.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && string.Equals(namePart[..eq], cookieName, StringComparison.Ordinal))
            {
                return namePart[(eq + 1)..];
            }
        }

        return null;
    }
}
