using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Draughtsman.Core.Models;
using Draughtsman.Server.Auth;
using Draughtsman.Sidecar;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>AU-18: the minor bundle. Each item is checked in both directions.</summary>
public class MinorHardeningTests
{
    [Fact]
    public async Task A_created_document_is_attributed_to_the_caller_whatever_the_body_claims_and_carries_no_forged_provenance()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();
        var body = new JsonObject
        {
            ["title"] = "Forged",
            ["type"] = "flowchart",
            ["nodes"] = new JsonArray(),
            ["edges"] = new JsonArray(),
            ["meta"] = new JsonObject
            {
                ["createdAt"] = "2026-01-01T00:00:00.000Z",
                ["updatedAt"] = "2026-01-01T00:00:00.000Z",
                ["createdBy"] = "user:the-ceo",
                ["aiProvenance"] = new JsonArray(new JsonObject { ["agent"] = "agent:trusted", ["at"] = "2026-01-01T00:00:00.000Z", ["action"] = "generated", ["elementIds"] = new JsonArray() }),
            },
        };

        HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", body);
        JsonNode stored = (await (await client.GetAsync("/api/documents/" + (await created.Json())["id"]!.GetValue<string>())).Json());

        Assert.Equal("user:local", stored["meta"]!["createdBy"]!.GetValue<string>());
        Assert.Null(stored["meta"]!["aiProvenance"]);
    }

    [Fact]
    public async Task A_save_cannot_rewrite_who_created_the_document_or_what_agents_did_to_it()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", new { title = "Mine", type = "flowchart", nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
        string id = (await created.Json())["id"]!.GetValue<string>();
        HttpResponseMessage read = await client.GetAsync("/api/documents/" + id);
        JsonNode document = await read.Json();
        string etag = read.Headers.ETag!.Tag;
        document["meta"]!["createdBy"] = "user:someone-else";
        document["meta"]!["aiProvenance"] = new JsonArray(new JsonObject { ["agent"] = "agent:forged", ["at"] = "2026-01-01T00:00:00.000Z", ["action"] = "generated", ["elementIds"] = new JsonArray() });
        using var put = new HttpRequestMessage(HttpMethod.Put, "/api/documents/" + id) { Content = JsonContent.Create(document) };
        put.Headers.TryAddWithoutValidation("If-Match", etag);

        HttpResponseMessage replaced = await client.SendAsync(put);
        JsonNode after = await (await client.GetAsync("/api/documents/" + id)).Json();

        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal("user:local", after["meta"]!["createdBy"]!.GetValue<string>());
        Assert.Null(after["meta"]!["aiProvenance"]);
    }

    [Fact]
    public void Empty_throttle_buckets_are_evicted_instead_of_growing_for_ever()
    {
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var throttle = new FailedSignInThrottle(clock);
        for (int i = 0; i < 1500; i++)
        {
            throttle.RecordFailure($"ip:10.0.{i / 250}.{i % 250}");
        }

        Assert.True(throttle.BucketCount >= 1500, "buckets inside the window must be kept: that is the throttle");
        clock.Advance(FailedSignInThrottle.Window + TimeSpan.FromMinutes(1));
        throttle.RecordFailure("ip:203.0.113.9");

        Assert.Equal(1, throttle.BucketCount);
        Assert.False(throttle.IsBlocked("ip:10.0.0.1"));
    }

    [Fact]
    public async Task An_overlong_display_name_or_email_is_a_400_not_a_500()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage longName = await admin.PostAsJsonAsync("/api/users", new { email = "long@example.com", displayName = new string('n', 300), role = "Editor", password = AuthBarHelpers.UserPassword });
        HttpResponseMessage longEmail = await admin.PostAsJsonAsync("/api/users", new { email = new string('e', 300) + "@example.com", displayName = "x", role = "Editor", password = AuthBarHelpers.UserPassword });
        HttpResponseMessage fine = await admin.PostAsJsonAsync("/api/users", new { email = "fine@example.com", displayName = new string('n', 200), role = "Editor", password = AuthBarHelpers.UserPassword });

        Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longEmail.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
    }

    [Fact]
    public async Task Over_https_the_session_cookie_carries_the_host_prefix_and_works_and_over_http_it_does_not()
    {
        using var https = new ConfiguredServerFactory("server:\n  forceHttps: true\n");
        HttpClient client = https.CreateClient();
        string code = https.Services.GetRequiredService<SetupCodeGate>().Issue();
        HttpResponseMessage first = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "host@example.com", displayName = "Host", password = AuthBarHelpers.UserPassword, setupCode = code });
        string setCookie = first.Headers.GetValues("Set-Cookie").Single(c => c.Contains("ds_session", StringComparison.Ordinal));

        Assert.StartsWith("__Host-ds_session=", setCookie, StringComparison.Ordinal);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
        me.Headers.Add("Cookie", setCookie.Split(';')[0]);
        Assert.True((await (await https.CreateClient().SendAsync(me)).Json())["user"] is not null, "the prefixed cookie must authenticate");

        using var http = new DraughtsmanServerFactory();
        string plainCode = http.Services.GetRequiredService<SetupCodeGate>().Issue();
        HttpResponseMessage plain = await http.CreateClient().PostAsJsonAsync("/api/auth/first-run", new { email = "plain@example.com", displayName = "Plain", password = AuthBarHelpers.UserPassword, setupCode = plainCode });
        Assert.StartsWith("ds_session=", plain.Headers.GetValues("Set-Cookie").Single(c => c.Contains("ds_session", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_issued_under_the_old_cookie_name_still_signs_in_after_the_upgrade()
    {
        using var https = new ConfiguredServerFactory("server:\n  forceHttps: true\n");
        HttpClient client = https.CreateClient();
        string code = https.Services.GetRequiredService<SetupCodeGate>().Issue();
        HttpResponseMessage first = await client.PostAsJsonAsync("/api/auth/first-run", new { email = "legacy@example.com", displayName = "Legacy", password = AuthBarHelpers.UserPassword, setupCode = code });
        string secret = first.Cookie("__Host-ds_session")!;
        using var legacy = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
        legacy.Headers.Add("Cookie", "ds_session=" + secret);

        JsonNode status = await (await client.SendAsync(legacy)).Json();

        Assert.True(status["user"] is not null, "the pre-prefix cookie name must keep working");
    }

    [Fact]
    public async Task A_sidecar_failure_is_reported_without_its_message_which_can_name_server_paths()
    {
        using var factory = new DraughtsmanServerFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ISidecarClient>(new ThrowingSidecar())));
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", new { title = "t", type = "flowchart", nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
        JsonNode document = await (await client.GetAsync("/api/documents/" + (await created.Json())["id"]!.GetValue<string>())).Json();

        HttpResponseMessage response = await client.PostAsync("/api/layout", new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("/srv/draughtsman", body, StringComparison.Ordinal);
        Assert.Contains("server log", body, StringComparison.Ordinal);
    }

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class ThrowingSidecar : ISidecarClient
    {
        public SidecarStatus Status => new(SidecarState.Running);

        public Task<SidecarHealth> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SidecarHealth(true, "0", "v0", 0));

        public Task<LayoutResult> LayoutAsync(Document document, LayoutOptions? options = null, CancellationToken cancellationToken = default)
            => throw new SidecarException("Could not start node /srv/draughtsman/sidecar/sidecar.js: permission denied");

        public Task<IReadOnlyDictionary<string, EdgeRoute>> RouteAsync(Document document, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SequenceScene> SequenceSceneAsync(Document document, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Document> YjsToJsonAsync(byte[]? state, IReadOnlyList<byte[]>? updates = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<byte[]> YjsFromJsonAsync(Document document, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<YjsState> ApplyUpdatesAsync(byte[]? state, IReadOnlyList<byte[]> updates, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
