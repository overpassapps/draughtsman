using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>AU-05: tokens expire, may be read-only, are only ever minted for the caller themselves, and report when they were last used.</summary>
public class TokenExpiryAndScopeTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public TokenExpiryAndScopeTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_new_token_expires_after_the_default_and_a_requested_lifetime_is_honoured()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();

        JsonNode byDefault = await (await admin.PostAsJsonAsync("/api/tokens", new { name = "default lifetime" })).Json();
        JsonNode seven = await (await admin.PostAsJsonAsync("/api/tokens", new { name = "a week", expiresInDays = 7 })).Json();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.InRange((byDefault["expiresAt"]!.GetValue<DateTimeOffset>() - now).TotalDays, 89.9, 90.1);
        Assert.InRange((seven["expiresAt"]!.GetValue<DateTimeOffset>() - now).TotalDays, 6.9, 7.1);
        Assert.Equal("full", byDefault["scope"]!.GetValue<string>());

        // The list reports them too, with the expiry and that neither has expired yet.
        JsonNode listed = await (await admin.GetAsync("/api/tokens")).Json();
        JsonNode row = listed.AsArray().Single(t => t!["id"]!.GetValue<string>() == seven["id"]!.GetValue<string>())!;
        Assert.False(row["expired"]!.GetValue<bool>());
        Assert.InRange((row["expiresAt"]!.GetValue<DateTimeOffset>() - now).TotalDays, 6.9, 7.1);
    }

    [Fact]
    public async Task A_lifetime_outside_the_allowed_range_or_an_unknown_scope_is_refused_and_creates_nothing()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        int before = (await (await admin.GetAsync("/api/tokens")).Json()).AsArray().Count;

        foreach (object bad in new object[]
                 {
                     new { name = "zero", expiresInDays = 0 },
                     new { name = "negative", expiresInDays = -5 },
                     new { name = "forever", expiresInDays = 100000 },
                     new { name = "just over", expiresInDays = 366 },
                     new { name = "weird scope", scope = "admin" },
                     new { name = new string('n', 257) },
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/tokens", bad)).StatusCode);
        }

        Assert.Equal(before, (await (await admin.GetAsync("/api/tokens")).Json()).AsArray().Count);
        // The boundary itself is allowed.
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/tokens", new { name = "a year", expiresInDays = 365 })).StatusCode);
    }

    [Fact]
    public async Task An_expired_token_is_refused_while_an_unexpired_one_and_a_grandfathered_one_still_work()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string adminId = (await (await admin.GetAsync("/api/auth/status")).Json())["user"]!["id"]!.GetValue<string>();

        string expired = await SeedToken(adminId, "expired", DateTimeOffset.UtcNow.AddMinutes(-1), ApiTokenScope.Full);
        string expiresLater = await SeedToken(adminId, "live", DateTimeOffset.UtcNow.AddDays(1), ApiTokenScope.Full);
        string grandfathered = await SeedToken(adminId, "pre-expiry", null, ApiTokenScope.Full);

        HttpResponseMessage refused = await _factory.Bearer(expired).GetAsync("/api/documents");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.Bearer(expiresLater).GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.Bearer(grandfathered).GetAsync("/api/documents")).StatusCode);

        // The list says which is which, and only the expired one is flagged.
        JsonNode listed = await (await admin.GetAsync("/api/tokens")).Json();
        Assert.True(listed.AsArray().Single(t => t!["name"]!.GetValue<string>() == "expired")!["expired"]!.GetValue<bool>());
        JsonNode old = listed.AsArray().Single(t => t!["name"]!.GetValue<string>() == "pre-expiry")!;
        Assert.False(old["expired"]!.GetValue<bool>());
        Assert.Null(old["expiresAt"]);
    }

    [Fact]
    public async Task Last_used_is_empty_until_the_token_is_used_and_then_moves()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        (string id, string secret) = await admin.CreateToken("watch me");

        JsonNode before = (await (await admin.GetAsync("/api/tokens")).Json()).AsArray().Single(t => t!["id"]!.GetValue<string>() == id)!;
        Assert.Null(before["lastUsedAt"]);

        await _factory.Bearer(secret).GetAsync("/api/documents");

        JsonNode after = (await (await admin.GetAsync("/api/tokens")).Json()).AsArray().Single(t => t!["id"]!.GetValue<string>() == id)!;
        Assert.True(DateTimeOffset.UtcNow - after["lastUsedAt"]!.GetValue<DateTimeOffset>() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_read_only_token_can_read_but_a_full_token_can_write_the_same_request()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        (_, string readSecret) = await admin.CreateToken("reader", new { scope = "read" });
        (_, string fullSecret) = await admin.CreateToken("writer", new { scope = "full" });
        object document = new { title = "From a token", type = "freeform", nodes = Array.Empty<object>(), edges = Array.Empty<object>() };

        HttpClient reader = _factory.Bearer(readSecret);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/documents")).StatusCode);
        HttpResponseMessage refused = await reader.PostAsJsonAsync("/api/documents", document);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("Read-only token", (await refused.Json())["title"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.Created, (await _factory.Bearer(fullSecret).PostAsJsonAsync("/api/documents", document)).StatusCode);

        // Only the writer's document exists: the refused request never reached the handler.
        JsonNode list = await (await admin.GetAsync("/api/documents?q=From%20a%20token")).Json();
        Assert.Equal(1, list["total"]!.GetValue<int>());
    }

    /// <summary>
    /// The scope is enforced in one place for the whole API, so this sweeps every registered route: every method other
    /// than GET/HEAD/OPTIONS (bar /mcp, which the MCP tools gate themselves) is refused 403 "Read-only token" for a read
    /// token, and the very same requests with a full token are never refused for that reason.
    /// </summary>
    [Fact]
    public async Task Every_mutating_route_refuses_a_read_token_and_none_refuse_a_full_token_for_that_reason()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        (_, string readSecret) = await admin.CreateToken("sweep reader", new { scope = "read" });
        (_, string fullSecret) = await admin.CreateToken("sweep writer", new { scope = "full" });
        HttpClient reader = _factory.Bearer(readSecret);
        HttpClient writer = _factory.Bearer(fullSecret);

        List<(string Method, string Url)> mutating = _factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is not null)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"]).Select(m => (Method: m, Url: Concrete(e.RoutePattern.RawText!))))
            .Where(r => r.Method is not ("GET" or "HEAD" or "OPTIONS") && !r.Url.StartsWith("/mcp", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        // The sweep examines real things: these are known mutating routes.
        Assert.Contains(mutating, r => r is ("POST", "/api/documents"));
        Assert.Contains(mutating, r => r is ("DELETE", "/api/documents/placeholder"));
        Assert.Contains(mutating, r => r is ("PATCH", "/api/users/placeholder"));

        var failures = new List<string>();
        foreach ((string method, string url) in mutating)
        {
            HttpResponseMessage asReader = await reader.SendAsync(Empty(method, url));
            if (asReader.StatusCode != HttpStatusCode.Forbidden || !(await asReader.Content.ReadAsStringAsync()).Contains("Read-only token", StringComparison.Ordinal))
            {
                failures.Add($"{method} {url} was not refused as read-only for a read token ({(int)asReader.StatusCode}).");
            }

            HttpResponseMessage asWriter = await writer.SendAsync(Empty(method, url));
            if ((await asWriter.Content.ReadAsStringAsync()).Contains("Read-only token", StringComparison.Ordinal))
            {
                failures.Add($"{method} {url} refused a FULL token as read-only.");
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public async Task Tokens_are_only_ever_minted_for_the_caller_and_never_through_a_token()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string adminId = (await (await admin.GetAsync("/api/auth/status")).Json())["user"]!["id"]!.GetValue<string>();
        await admin.CreateUser("self-minter@example.com");
        HttpClient editor = await _factory.SignIn("self-minter@example.com");

        // Naming someone else in the body changes nothing: the token belongs to whoever is signed in.
        (string id, string secret) = await editor.CreateToken("mine despite the body", new { userId = adminId, ownerId = adminId });
        JsonNode whoAmI = await (await _factory.Bearer(secret).GetAsync("/api/auth/status")).Json();
        Assert.Equal("self-minter@example.com", whoAmI["user"]!["email"]!.GetValue<string>());
        // The admin's own list does not contain it, and the editor's does.
        Assert.DoesNotContain((await (await admin.GetAsync("/api/tokens")).Json()).AsArray(), t => t!["id"]!.GetValue<string>() == id);
        Assert.Contains((await (await editor.GetAsync("/api/tokens")).Json()).AsArray(), t => t!["id"]!.GetValue<string>() == id);

        // A token can never mint another token or reach the account APIs, read-only or full.
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.Bearer(secret).PostAsJsonAsync("/api/tokens", new { name = "via token" })).StatusCode);
    }

    [Fact]
    public async Task The_operators_ceiling_caps_the_lifetime_and_pulls_the_default_down_with_it()
    {
        using var factory = new TokenPolicyServerFactory("auth:\n  tokens:\n    defaultLifetimeDays: 90\n    maxLifetimeDays: 30\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/tokens", new { name = "too long", expiresInDays = 31 })).StatusCode);
        JsonNode atCeiling = await (await admin.PostAsJsonAsync("/api/tokens", new { name = "at the ceiling", expiresInDays = 30 })).Json();
        Assert.InRange((atCeiling["expiresAt"]!.GetValue<DateTimeOffset>() - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1);
        // The configured default (90) is above the ceiling, so it is clamped to it rather than minting a forbidden lifetime.
        JsonNode byDefault = await (await admin.PostAsJsonAsync("/api/tokens", new { name = "default" })).Json();
        Assert.InRange((byDefault["expiresAt"]!.GetValue<DateTimeOffset>() - DateTimeOffset.UtcNow).TotalDays, 29.9, 30.1);
    }

    [Fact]
    public void Nonsense_configuration_is_clamped_rather_than_honoured()
    {
        var policy = new TokenPolicy(new TokenPolicyConfig { DefaultLifetimeDays = 0, MaxLifetimeDays = -4 });

        Assert.Equal(1, policy.MaxLifetimeDays);
        Assert.Equal(1, policy.DefaultLifetimeDays);
        Assert.False(policy.TryResolveExpiry(2, DateTimeOffset.UtcNow, out _, out string? problem));
        Assert.Contains("between 1 and 1", problem, StringComparison.Ordinal);
        Assert.Equal(3650, new TokenPolicy(new TokenPolicyConfig { MaxLifetimeDays = int.MaxValue }).MaxLifetimeDays);
    }

    private async Task<string> SeedToken(string userId, string name, DateTimeOffset? expiresAt, ApiTokenScope scope)
    {
        using IServiceScope scope2 = _factory.Services.CreateScope();
        string secret = TokenGenerator.NewApiTokenSecret();
        await scope2.ServiceProvider.GetRequiredService<IUserStore>().CreateApiToken(userId, name, TokenGenerator.Hash(secret), DateTimeOffset.UtcNow.AddDays(-2), expiresAt, scope);
        return secret;
    }

    private static string Concrete(string pattern)
    {
        string rooted = pattern.StartsWith('/') ? pattern : "/" + pattern;
        rooted = rooted.Length > 1 && rooted.EndsWith('/') ? rooted[..^1] : rooted;
        return string.Join('/', rooted.Split('/').Select(s => s.StartsWith('{') && s.EndsWith('}') ? "placeholder" : s));
    }

    private static HttpRequestMessage Empty(string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method is "POST" or "PUT" or "PATCH")
        {
            string contentType = url.StartsWith("/api/import/", StringComparison.Ordinal) ? "text/plain" : "application/json";
            request.Content = new StringContent(contentType == "text/plain" ? "x" : "{}", System.Text.Encoding.UTF8, contentType);
        }

        return request;
    }

    private sealed class TokenPolicyServerFactory : DraughtsmanServerFactory
    {
        public TokenPolicyServerFactory(string yaml)
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(Path.Combine(DataFolder, DraughtsmanConfig.FileName), yaml);
        }
    }
}
