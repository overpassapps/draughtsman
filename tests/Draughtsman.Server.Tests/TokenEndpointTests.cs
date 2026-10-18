using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Per-user API tokens for MCP and scripting (build-spec-v0.1.md section 7): create, list, revoke, and MCP over HTTP
/// accepting the bearer token.
/// </summary>
public class TokenEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public TokenEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Create_list_and_revoke_a_token_and_a_revoked_token_is_then_refused()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage created = await client.PostAsJsonAsync("/api/tokens", new { name = "CI token" });
        JsonNode createdBody = await ReadJson(created);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string tokenId = createdBody["id"]!.GetValue<string>();
        string secret = createdBody["token"]!.GetValue<string>();
        Assert.StartsWith("dst_", secret, StringComparison.Ordinal);

        JsonNode listed = await ReadJson(await client.GetAsync("/api/tokens"));
        Assert.Contains(listed.AsArray(), t => t!["id"]!.GetValue<string>() == tokenId && t["name"]!.GetValue<string>() == "CI token");
        // The secret is shown exactly once, at creation; the list never carries it.
        Assert.DoesNotContain(secret, listed.ToJsonString(), StringComparison.Ordinal);

        HttpClient bearerClient = _factory.CreateClient();
        bearerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        Assert.Equal(HttpStatusCode.OK, (await bearerClient.GetAsync("/api/documents")).StatusCode);

        string xsrf = client.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();
        var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/api/tokens/{tokenId}");
        revoke.Headers.Add(AuthTestHelpers.XsrfHeaderName, xsrf);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(revoke)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await bearerClient.GetAsync("/api/documents")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_bearer_token_is_refused()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "dst_this-token-was-never-issued");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/documents")).StatusCode);
    }

    [Fact]
    public async Task Mcp_over_http_requires_a_bearer_token_and_a_revoked_one_is_refused()
    {
        HttpClient anonymous = _factory.CreateClient();
        HttpResponseMessage anonymousResponse = await anonymous.SendAsync(JsonRpcInitialize());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        HttpClient owner = await _factory.CreateAuthenticatedClient();
        HttpResponseMessage tokenResponse = await owner.PostAsJsonAsync("/api/tokens", new { name = "mcp token" });
        JsonNode tokenBody = await ReadJson(tokenResponse);
        string tokenId = tokenBody["id"]!.GetValue<string>();
        string secret = tokenBody["token"]!.GetValue<string>();

        HttpClient mcpClient = _factory.CreateClient();
        mcpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        HttpResponseMessage withToken = await mcpClient.SendAsync(JsonRpcInitialize());
        Assert.NotEqual(HttpStatusCode.Unauthorized, withToken.StatusCode);

        string xsrf = owner.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();
        var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/api/tokens/{tokenId}");
        revoke.Headers.Add(AuthTestHelpers.XsrfHeaderName, xsrf);
        await owner.SendAsync(revoke);

        HttpResponseMessage afterRevoke = await mcpClient.SendAsync(JsonRpcInitialize());
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task A_user_can_only_revoke_their_own_token_but_an_admin_can_revoke_anyones()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string adminXsrf = admin.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();

        HttpResponseMessage createEditor = await admin.PostAsJsonAsync("/api/users",
            new { email = "editor-token-owner@example.com", displayName = "Editor", role = "Editor", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.Created, createEditor.StatusCode);

        HttpClient editor = _factory.CreateClient();
        HttpResponseMessage editorLogin = await editor.PostAsJsonAsync("/api/auth/login", new { email = "editor-token-owner@example.com", password = "correcthorsebattery" });
        string editorXsrf = ExtractCookieValue(editorLogin, "XSRF-TOKEN")!;
        editor.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, editorXsrf);

        HttpResponseMessage tokenResponse = await editor.PostAsJsonAsync("/api/tokens", new { name = "editor's own token" });
        string tokenId = (await ReadJson(tokenResponse))["id"]!.GetValue<string>();

        // A third, unrelated non-admin user cannot revoke the editor's token - not found, not forbidden, so an
        // unauthorized caller learns nothing about whether the id exists.
        HttpResponseMessage createOther = await admin.PostAsJsonAsync("/api/users",
            new { email = "unrelated@example.com", displayName = "Unrelated", role = "Editor", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.Created, createOther.StatusCode);
        HttpClient other = _factory.CreateClient();
        HttpResponseMessage otherLogin = await other.PostAsJsonAsync("/api/auth/login", new { email = "unrelated@example.com", password = "correcthorsebattery" });
        other.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, ExtractCookieValue(otherLogin, "XSRF-TOKEN")!);

        var otherRevokeAttempt = new HttpRequestMessage(HttpMethod.Delete, $"/api/tokens/{tokenId}");
        Assert.Equal(HttpStatusCode.NotFound, (await other.SendAsync(otherRevokeAttempt)).StatusCode);

        // The admin, who owns neither the token nor is the unrelated user, can still revoke it.
        var adminRevoke = new HttpRequestMessage(HttpMethod.Delete, $"/api/tokens/{tokenId}");
        adminRevoke.Headers.Add(AuthTestHelpers.XsrfHeaderName, adminXsrf);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(adminRevoke)).StatusCode);
    }

    /// <summary>
    /// Security review F3: an API token carries its owner's role, so an admin-owned token must still be refused by
    /// /api/users and by minting a further token through itself - account and token management are cookie-only.
    /// </summary>
    [Fact]
    public async Task A_bearer_token_cannot_mint_a_token_or_reach_the_users_api_even_when_its_owner_is_admin()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        HttpResponseMessage adminTokenResponse = await admin.PostAsJsonAsync("/api/tokens", new { name = "admin's own token" });
        string adminSecret = (await ReadJson(adminTokenResponse))["token"]!.GetValue<string>();

        HttpClient bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminSecret);

        HttpResponseMessage mintAttempt = await bearer.PostAsJsonAsync("/api/tokens", new { name = "minted via a token" });
        Assert.Equal(HttpStatusCode.Forbidden, mintAttempt.StatusCode);

        HttpResponseMessage usersListAttempt = await bearer.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, usersListAttempt.StatusCode);

        HttpResponseMessage createUserAttempt = await bearer.PostAsJsonAsync("/api/users",
            new { email = "should-not-exist@example.com", displayName = "Nope", role = "Editor", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.Forbidden, createUserAttempt.StatusCode);
    }

    /// <summary>
    /// Security review F3: a token can still revoke itself (or another of its own owner's tokens) - only reaching
    /// across to someone *else's* token requires a cookie-authenticated admin.
    /// </summary>
    [Fact]
    public async Task A_bearer_token_can_revoke_its_own_but_not_someone_elses_even_as_admin()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string adminXsrf = admin.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single();

        HttpResponseMessage createEditor = await admin.PostAsJsonAsync("/api/users",
            new { email = "f3-editor@example.com", displayName = "Editor", role = "Editor", password = "correcthorsebattery" });
        Assert.Equal(HttpStatusCode.Created, createEditor.StatusCode);
        HttpClient editor = _factory.CreateClient();
        HttpResponseMessage editorLogin = await editor.PostAsJsonAsync("/api/auth/login", new { email = "f3-editor@example.com", password = "correcthorsebattery" });
        editor.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, ExtractCookieValue(editorLogin, "XSRF-TOKEN")!);
        HttpResponseMessage editorTokenResponse = await editor.PostAsJsonAsync("/api/tokens", new { name = "editor's token" });
        string editorTokenId = (await ReadJson(editorTokenResponse))["id"]!.GetValue<string>();

        HttpResponseMessage adminTokenResponse = await admin.PostAsJsonAsync("/api/tokens", new { name = "admin's token" });
        JsonNode adminTokenBody = await ReadJson(adminTokenResponse);
        string adminTokenId = adminTokenBody["id"]!.GetValue<string>();
        string adminSecret = adminTokenBody["token"]!.GetValue<string>();

        HttpClient adminBearer = _factory.CreateClient();
        adminBearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminSecret);

        // The admin's own token, via the token itself: allowed.
        Assert.Equal(HttpStatusCode.NoContent, (await adminBearer.DeleteAsync($"/api/tokens/{adminTokenId}")).StatusCode);

        // The editor's token, via the admin's (still otherwise-valid) token: refused, exactly like a stranger's attempt.
        HttpResponseMessage adminTokenCreatedAgain = await admin.PostAsJsonAsync("/api/tokens", new { name = "admin's second token" });
        string adminSecondSecret = (await ReadJson(adminTokenCreatedAgain))["token"]!.GetValue<string>();
        adminBearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminSecondSecret);
        Assert.Equal(HttpStatusCode.NotFound, (await adminBearer.DeleteAsync($"/api/tokens/{editorTokenId}")).StatusCode);

        // The same cross-user revocation succeeds for the admin over a cookie session.
        var cookieRevoke = new HttpRequestMessage(HttpMethod.Delete, $"/api/tokens/{editorTokenId}");
        cookieRevoke.Headers.Add(AuthTestHelpers.XsrfHeaderName, adminXsrf);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(cookieRevoke)).StatusCode);
    }

    /// <summary>QA S6: an administrator sees every person's tokens with their owner, and revoking one from that list is recorded against the owner.</summary>
    [Fact]
    public async Task An_admin_lists_every_persons_tokens_with_owner_and_revokes_one_from_that_list()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string ownerId = await admin.CreateUser("s6-owner@example.com");
        HttpClient owner = await _factory.SignIn("s6-owner@example.com");
        (string ownerTokenId, string ownerSecret) = await owner.CreateToken("owner's laptop", new { scope = "read", expiresInDays = 30 });
        (string adminTokenId, _) = await admin.CreateToken("admin's own");

        HttpResponseMessage listed = await admin.GetAsync("/api/admin/tokens");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        string raw = await listed.Content.ReadAsStringAsync();
        JsonArray rows = JsonNode.Parse(raw)!.AsArray();

        JsonNode theirs = rows.Single(r => r!["id"]!.GetValue<string>() == ownerTokenId)!;
        Assert.Equal("owner's laptop", theirs["name"]!.GetValue<string>());
        Assert.Equal("s6-owner@example.com", theirs["ownerEmail"]!.GetValue<string>());
        Assert.Equal(ownerId, theirs["ownerId"]!.GetValue<string>());
        Assert.Equal("read", theirs["scope"]!.GetValue<string>());
        Assert.NotNull(theirs["createdAt"]);
        Assert.NotNull(theirs["expiresAt"]);
        Assert.False(theirs["revoked"]!.GetValue<bool>());
        Assert.Contains(rows, r => r!["id"]!.GetValue<string>() == adminTokenId);
        // Never the secret, and never the stored hash either.
        Assert.DoesNotContain(ownerSecret, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("tokenHash", raw, StringComparison.OrdinalIgnoreCase);

        // Revoke from the list: the token stops working, shows as revoked, and the security event names the admin and the owner.
        Assert.Equal(HttpStatusCode.OK, (await _factory.Bearer(ownerSecret).GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Delete, $"/api/tokens/{ownerTokenId}", withCsrfFrom: admin))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.Bearer(ownerSecret).GetAsync("/api/documents")).StatusCode);
        JsonArray after = (await (await admin.GetAsync("/api/admin/tokens")).Json()).AsArray();
        Assert.True(after.Single(r => r!["id"]!.GetValue<string>() == ownerTokenId)!["revoked"]!.GetValue<bool>());
        JsonNode evt = (await admin.SecurityEvents($"?type=token.revoked&user={ownerId}")).Single(e => e!["detail"]!.GetValue<string>().Contains(ownerTokenId, StringComparison.Ordinal))!;
        Assert.Equal(ownerId, evt["targetUserId"]!.GetValue<string>());
        Assert.NotEqual(ownerId, evt["actorUserId"]!.GetValue<string>());
    }

    /// <summary>QA S6, the other direction: an Editor, and a token even of an admin, can neither list everyone's tokens nor see another's through the ordinary list.</summary>
    [Fact]
    public async Task Only_a_cookie_admin_can_list_all_tokens_and_an_editor_still_sees_only_their_own()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        await admin.CreateUser("s6-editor@example.com");
        await admin.CreateUser("s6-bystander@example.com");
        HttpClient editor = await _factory.SignIn("s6-editor@example.com");
        HttpClient bystander = await _factory.SignIn("s6-bystander@example.com");
        (string bystanderTokenId, _) = await bystander.CreateToken("not for the editor's eyes");
        (string editorTokenId, _) = await editor.CreateToken("editor's own");
        (_, string adminSecret) = await admin.CreateToken("admin's bearer");

        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/admin/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.Bearer(adminSecret).GetAsync("/api/admin/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/admin/tokens")).StatusCode);

        JsonArray own = (await (await editor.GetAsync("/api/tokens?all=true")).Json()).AsArray();
        Assert.Contains(own, r => r!["id"]!.GetValue<string>() == editorTokenId);
        Assert.DoesNotContain(own, r => r!["id"]!.GetValue<string>() == bystanderTokenId);
        // The editor cannot revoke the bystander's token either, so the admin list is the only way across.
        Assert.Equal(HttpStatusCode.NotFound, (await editor.SendAsync(AuthBarHelpers.Request(HttpMethod.Delete, $"/api/tokens/{bystanderTokenId}", withCsrfFrom: editor))).StatusCode);
    }

    /// <summary>A minimal MCP streamable-HTTP request; the Accept header the transport requires goes on the request, not the content.</summary>
    private static HttpRequestMessage JsonRpcInitialize()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "test", version = "1" } },
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
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

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body.");
    }
}
