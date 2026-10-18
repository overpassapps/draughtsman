using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.Tests;

/// <summary>
/// An anonymous 401 must say a bearer token is what is missing: MCP clients read a bare 401 as "start OAuth", which
/// this server does not implement, and then fail with a message about client registration instead.
/// </summary>
public class BearerChallengeTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public BearerChallengeTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/api/documents")]
    public async Task An_anonymous_request_gets_a_bearer_challenge_and_a_plain_text_hint(string path)
    {
        using HttpClient anonymous = _factory.CreateClient();

        using var request = new HttpRequestMessage(path == "/mcp" ? HttpMethod.Post : HttpMethod.Get, path);
        if (path == "/mcp")
        {
            request.Headers.Accept.ParseAdd("application/json, text/event-stream");
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response = await anonymous.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).ToString());
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer", await response.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_token_gets_the_same_challenge()
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "dst_not-a-real-token");

        HttpResponseMessage response = await client.GetAsync("/api/documents", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }

    /// <summary>
    /// Claude Code probes OAuth discovery before it reads the 401. An HTML 200 (the SPA fallback) made it fail with a
    /// JSON parse error ("Unrecognized token '&lt;'"); a 404 lets it fall through to the bearer message.
    /// </summary>
    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-authorization-server")]
    public async Task OAuth_discovery_probes_are_not_answered_with_the_spa_shell(string path)
    {
        using HttpClient anonymous = _factory.CreateClient();

        HttpResponseMessage response = await anonymous.GetAsync(path, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("html", response.Content.Headers.ContentType?.MediaType ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>dotnet run</c> prints "Building..." to stdout unless the profile turns <c>dotnetRunMessages</c> off. The
    /// "Using launch settings..." line is not suppressible that way, which is why the docs say
    /// <c>dotnet run --no-launch-profile</c> for stdio (proved by hand, not here: it needs the SDK on PATH).
    /// </summary>
    [Fact]
    public void Launch_profiles_keep_dotnet_run_messages_off_so_stdio_stays_clean()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "src", "Draughtsman.Server", "Properties", "launchSettings.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        JsonObject profiles = JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "src", "Draughtsman.Server", "Properties", "launchSettings.json")))!["profiles"]!.AsObject();
        foreach ((string name, JsonNode? profile) in profiles)
        {
            Assert.False(profile!["dotnetRunMessages"]?.GetValue<bool>() ?? false, $"profile '{name}' must not enable dotnetRunMessages");
        }
    }
}
