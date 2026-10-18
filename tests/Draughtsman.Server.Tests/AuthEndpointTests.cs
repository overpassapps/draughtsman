using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// First run, sign-in, sign-out and its generic failure message (build-spec-v0.1.md section 7). Every test that
/// needs its own successful first-run gets its own freshly constructed <see cref="DraughtsmanServerFactory"/>
/// (not an <c>IClassFixture</c> shared with its siblings): first-run only ever succeeds once per database, so two
/// tests sharing one factory would fight over which of them gets to be "the first run".
/// </summary>
public class AuthEndpointTests
{
    [Fact]
    public async Task First_run_creates_the_admin_signs_them_in_and_is_then_closed_for_good()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();

        JsonNode before = await ReadJson(await client.GetAsync("/api/auth/status"));
        Assert.True(before["firstRunRequired"]!.GetValue<bool>());
        Assert.Null(before["user"]);

        string setupCode = factory.Services.GetRequiredService<SetupCodeGate>().Issue();
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/auth/first-run",
            new { email = "admin@example.com", displayName = "Admin", password = "correcthorsebattery", setupCode });
        JsonNode createdBody = await ReadJson(created);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("admin@example.com", createdBody["email"]!.GetValue<string>());
        Assert.Equal("Admin", createdBody["role"]!.GetValue<string>());
        Assert.True(createdBody["active"]!.GetValue<bool>());

        // The response signs the caller in: the same client can now reach a protected route with no separate login.
        HttpResponseMessage protectedRoute = await client.GetAsync("/api/documents");
        Assert.Equal(HttpStatusCode.OK, protectedRoute.StatusCode);

        JsonNode after = await ReadJson(await client.GetAsync("/api/auth/status"));
        Assert.False(after["firstRunRequired"]!.GetValue<bool>());
        Assert.Equal("admin@example.com", after["user"]!["email"]!.GetValue<string>());

        // Closed for good: a second attempt from a fresh, not-yet-signed-in caller is refused and creates nobody.
        HttpClient freshCaller = factory.CreateClient();
        HttpResponseMessage secondAttempt = await freshCaller.PostAsJsonAsync("/api/auth/first-run",
            new { email = "someone-else@example.com", displayName = "Someone Else", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.Conflict, secondAttempt.StatusCode);

        HttpClient anotherClient = factory.CreateClient();
        HttpResponseMessage loginAsSomeoneElse = await anotherClient.PostAsJsonAsync("/api/auth/login",
            new { email = "someone-else@example.com", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.Unauthorized, loginAsSomeoneElse.StatusCode);
    }

    [Fact]
    public async Task First_run_rejects_a_password_shorter_than_the_minimum_and_creates_nobody()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();

        string setupCode = factory.Services.GetRequiredService<SetupCodeGate>().Issue();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/first-run",
            new { email = "short@example.com", displayName = "Short", password = "tooshort", setupCode });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        JsonNode status = await ReadJson(await client.GetAsync("/api/auth/status"));
        Assert.True(status["firstRunRequired"]!.GetValue<bool>());

        HttpResponseMessage loginAttempt = await client.PostAsJsonAsync("/api/auth/login", new { email = "short@example.com", password = "tooshort" });
        Assert.Equal(HttpStatusCode.Unauthorized, loginAttempt.StatusCode);
    }

    [Fact]
    public async Task Login_succeeds_with_correct_credentials_and_fails_with_the_same_generic_message_either_way()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient setup = factory.CreateClient();
        string setupCode = factory.Services.GetRequiredService<SetupCodeGate>().Issue();
        HttpResponseMessage firstRun = await setup.PostAsJsonAsync("/api/auth/first-run", new { email = "login@example.com", displayName = "Login", password = "correcthorsebattery", setupCode });
        Assert.Equal(HttpStatusCode.Created, firstRun.StatusCode);

        HttpClient client = factory.CreateClient();
        HttpResponseMessage success = await client.PostAsJsonAsync("/api/auth/login", new { email = "login@example.com", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/documents")).StatusCode);

        HttpClient wrongPasswordClient = factory.CreateClient();
        HttpResponseMessage wrongPassword = await wrongPasswordClient.PostAsJsonAsync("/api/auth/login", new { email = "login@example.com", password = "not-the-password" });
        HttpClient unknownUserClient = factory.CreateClient();
        HttpResponseMessage unknownUser = await unknownUserClient.PostAsJsonAsync("/api/auth/login", new { email = "nobody-by-this-email@example.com", password = "correcthorsebattery" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        JsonNode wrongPasswordBody = await ReadJson(wrongPassword);
        JsonNode unknownUserBody = await ReadJson(unknownUser);
        // design/atlas/login-admin.md: never "user not found" - a wrong password and an unknown email read identically.
        Assert.Equal("Email or password is incorrect.", wrongPasswordBody["detail"]!.GetValue<string>());
        Assert.Equal(wrongPasswordBody["detail"]!.GetValue<string>(), unknownUserBody["detail"]!.GetValue<string>());
        Assert.DoesNotContain("not found", wrongPasswordBody.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not found", unknownUserBody.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Logout_revokes_the_session_so_the_same_cookie_no_longer_authenticates()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/documents")).StatusCode);

        string xsrf = client.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();
        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add(AuthTestHelpers.XsrfHeaderName, xsrf);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logout)).StatusCode);

        // The very same client (same cookie jar) is refused now: revocation, not merely "stop sending the cookie".
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/documents")).StatusCode);
    }

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body.");
    }
}

/// <summary>Also its own factory, for the same reason as <see cref="AuthEndpointTests"/>: it needs a real first-run.</summary>
public class SignInThrottleTests
{
    [Fact]
    public async Task Repeated_failed_sign_ins_are_throttled_and_then_block_even_the_correct_password()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient setup = factory.CreateClient();
        string setupCode = factory.Services.GetRequiredService<SetupCodeGate>().Issue();
        HttpResponseMessage firstRun = await setup.PostAsJsonAsync("/api/auth/first-run", new { email = "throttle@example.com", displayName = "Throttle", password = "correcthorsebattery", setupCode });
        Assert.Equal(HttpStatusCode.Created, firstRun.StatusCode);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            HttpClient client = factory.CreateClient();
            HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", new { email = "throttle@example.com", password = "wrong" });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        HttpClient blockedClient = factory.CreateClient();
        HttpResponseMessage blocked = await blockedClient.PostAsJsonAsync("/api/auth/login", new { email = "throttle@example.com", password = "wrong" });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // The account is now blocked even for the caller who knows the real password - the throttle guards the
        // account and the IP, not merely "reject repeats of the same wrong guess".
        HttpClient correctPasswordClient = factory.CreateClient();
        HttpResponseMessage stillBlocked = await correctPasswordClient.PostAsJsonAsync("/api/auth/login", new { email = "throttle@example.com", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.TooManyRequests, stillBlocked.StatusCode);
    }
}
