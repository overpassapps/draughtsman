using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.Tests;

/// <summary>
/// A CDN, WAF or gateway that compresses or rewrites a response turns the strong ETag the server issued into a weak one
/// (Cloudflare, nginx with gzip on) or drops it, and a browser then quotes back whatever it was given. The document
/// version is an opaque server-issued identifier, so <c>If-Match</c> accepts the weak spelling of it, and the same
/// version travels in <c>X-Document-Version</c>, which intermediaries leave alone (docs/api.md "Base versions").
/// </summary>
public class WeakBaseVersionTests : IClassFixture<DraughtsmanServerFactory>
{
    private const string VersionHeader = "X-Document-Version";
    private const string StaleButWellFormed = "\"dv1:00000000000000000000000000000000\"";

    private readonly DraughtsmanServerFactory _factory;

    public WeakBaseVersionTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Every_response_that_hands_out_a_version_as_an_ETag_also_sends_it_unquoted_in_X_Document_Version()
    {
        HttpClient client = await CreateTokenClient();

        HttpResponseMessage created = await client.PostAsync("/api/documents", Json(Body("Versioned")));
        string id = (await Read(created))["id"]!.GetValue<string>();
        AssertVersionHeaderMatchesETag(created);

        HttpResponseMessage read = await client.GetAsync($"/api/documents/{id}");
        AssertVersionHeaderMatchesETag(read);
        Assert.Equal(VersionOf(created), VersionOf(read));

        // The 304 an open editor polls for carries it too, so a gateway that strips the ETag from it changes nothing.
        HttpResponseMessage notModified = await client.SendAsync(Request(HttpMethod.Get, $"/api/documents/{id}", ifNoneMatch: ETagOf(read)));
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        AssertVersionHeaderMatchesETag(notModified);

        HttpResponseMessage saved = await client.SendAsync(Request(HttpMethod.Put, $"/api/documents/{id}", Json(Body("Saved", id)), ifMatch: ETagOf(read)));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        AssertVersionHeaderMatchesETag(saved);
        Assert.NotEqual(VersionOf(read), VersionOf(saved));

        HttpResponseMessage renamed = await client.SendAsync(Request(HttpMethod.Patch, $"/api/documents/{id}", Json("{\"title\":\"Renamed\"}"), ifMatch: ETagOf(saved)));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        AssertVersionHeaderMatchesETag(renamed);

        string versionId = await NameVersion(client, id);
        HttpResponseMessage restored = await client.SendAsync(Request(HttpMethod.Post, $"/api/documents/{id}/versions/{versionId}/restore", ifMatch: ETagOf(renamed)));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        AssertVersionHeaderMatchesETag(restored);

        HttpResponseMessage duplicated = await client.PostAsync($"/api/documents/{id}/duplicate", content: null);
        Assert.Equal(HttpStatusCode.Created, duplicated.StatusCode);
        AssertVersionHeaderMatchesETag(duplicated);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("rename")]
    [InlineData("restore")]
    public async Task If_Match_takes_the_strong_or_weak_form_of_the_current_version_and_nothing_else(string write)
    {
        HttpClient client = await CreateTokenClient();

        // Strong and weak spellings of the current version are the same base version.
        foreach (Func<string, string> spelling in new Func<string, string>[] { tag => tag, tag => "W/" + tag })
        {
            (string id, string versionId, string current, _) = await Prepare(client);
            HttpResponseMessage accepted = await Write(client, write, id, versionId, spelling(current));
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            AssertVersionHeaderMatchesETag(accepted);
            Assert.NotEqual(current, ETagOf(accepted));
        }

        // A stale version is a 412 whether it is spelled strongly or weakly, and it writes nothing.
        foreach (Func<string, string> spelling in new Func<string, string>[] { tag => tag, tag => "W/" + tag })
        {
            (string id, string versionId, string current, string stale) = await Prepare(client);
            HttpResponseMessage refused = await Write(client, write, id, versionId, spelling(stale));
            JsonNode problem = await AssertProblem(refused, HttpStatusCode.PreconditionFailed, "Document has changed");
            Assert.Equal(stale, problem["providedVersion"]!.GetValue<string>());
            Assert.Equal(current, problem["expectedVersion"]!.GetValue<string>());
            Assert.Equal(current, await CurrentTag(client, id));
        }

        // A well-formed tag the document never had is stale too, not malformed: the shape of a tag is not what is checked.
        {
            (string id, string versionId, string current, _) = await Prepare(client);
            await AssertProblem(await Write(client, write, id, versionId, "W/" + StaleButWellFormed), HttpStatusCode.PreconditionFailed, "Document has changed");
            Assert.Equal(current, await CurrentTag(client, id));
        }

        // Anything that is not exactly one entity tag is a 400 and writes nothing, weak or not.
        (string unchangedId, string unchangedVersion, string unchangedTag, _) = await Prepare(client);
        string[] refused400 =
        [
            "*",
            "W/*",
            "not-a-tag",
            "W/not-a-tag",
            "W/",
            $"{unchangedTag}, {unchangedTag}",
            $"W/{unchangedTag}, W/{unchangedTag}",
            $"W/{unchangedTag}, {StaleButWellFormed}",
        ];
        foreach (string header in refused400)
        {
            await AssertProblem(await Write(client, write, unchangedId, unchangedVersion, header), HttpStatusCode.BadRequest, "Invalid If-Match header");
        }

        // No If-Match at all stays a 428: a save that names no base version cannot be arbitrated.
        await AssertProblem(await Write(client, write, unchangedId, unchangedVersion, ifMatch: null), HttpStatusCode.PreconditionRequired, "Base version required");
        Assert.Equal(unchangedTag, await CurrentTag(client, unchangedId));
    }

    [Fact]
    public async Task Behind_a_gateway_that_weakens_the_ETag_a_client_quoting_what_it_was_given_still_saves_renames_and_restores()
    {
        HttpClient client = await CreateTokenClient(new Gateway(GatewayMode.WeakenETag));

        // The old client's whole behaviour: keep the ETag it was handed and quote it back unchanged.
        HttpResponseMessage created = await client.PostAsync("/api/documents", Json(Body("Behind a CDN")));
        Assert.True(created.Headers.ETag!.IsWeak, "The gateway should have weakened the ETag, or this test proves nothing.");
        string id = (await Read(created))["id"]!.GetValue<string>();

        HttpResponseMessage opened = await client.GetAsync($"/api/documents/{id}");
        string held = ETagOf(opened);
        Assert.StartsWith("W/", held, StringComparison.Ordinal);

        HttpResponseMessage saved = await client.SendAsync(Request(HttpMethod.Put, $"/api/documents/{id}", Json(Body("Edited", id)), ifMatch: held));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        held = ETagOf(saved);

        HttpResponseMessage renamed = await client.SendAsync(Request(HttpMethod.Patch, $"/api/documents/{id}", Json("{\"title\":\"Renamed\"}"), ifMatch: held));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        held = ETagOf(renamed);

        string versionId = await NameVersion(client, id);
        HttpResponseMessage edited = await client.SendAsync(Request(HttpMethod.Put, $"/api/documents/{id}", Json(Body("Edited again", id)), ifMatch: held));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        held = ETagOf(edited);
        HttpResponseMessage restored = await client.SendAsync(Request(HttpMethod.Post, $"/api/documents/{id}/versions/{versionId}/restore", ifMatch: held));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        held = ETagOf(restored);

        await AssertStaleVersionsAreStill412(client, id, held);
    }

    [Fact]
    public async Task Behind_a_gateway_that_removes_the_ETag_a_client_reading_X_Document_Version_still_saves_renames_and_restores()
    {
        HttpClient client = await CreateTokenClient(new Gateway(GatewayMode.RemoveETag));

        // The fixed client's behaviour: X-Document-Version first, quoted. There is no ETag to fall back on here.
        HttpResponseMessage created = await client.PostAsync("/api/documents", Json(Body("Behind a gateway")));
        Assert.Null(created.Headers.ETag);
        string id = (await Read(created))["id"]!.GetValue<string>();

        HttpResponseMessage opened = await client.GetAsync($"/api/documents/{id}");
        Assert.Null(opened.Headers.ETag);
        string held = QuotedVersionHeader(opened);

        HttpResponseMessage saved = await client.SendAsync(Request(HttpMethod.Put, $"/api/documents/{id}", Json(Body("Edited", id)), ifMatch: held));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        held = QuotedVersionHeader(saved);

        HttpResponseMessage renamed = await client.SendAsync(Request(HttpMethod.Patch, $"/api/documents/{id}", Json("{\"title\":\"Renamed\"}"), ifMatch: held));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        held = QuotedVersionHeader(renamed);

        string versionId = await NameVersion(client, id);
        HttpResponseMessage edited = await client.SendAsync(Request(HttpMethod.Put, $"/api/documents/{id}", Json(Body("Edited again", id)), ifMatch: held));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        held = QuotedVersionHeader(edited);
        HttpResponseMessage restored = await client.SendAsync(Request(HttpMethod.Post, $"/api/documents/{id}/versions/{versionId}/restore", ifMatch: held));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        held = QuotedVersionHeader(restored);

        // The 304 an open editor polls for has no ETag either, and still says which version is current.
        HttpResponseMessage notModified = await client.SendAsync(Request(HttpMethod.Get, $"/api/documents/{id}", ifNoneMatch: held));
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(held, QuotedVersionHeader(notModified));

        await AssertStaleVersionsAreStill412(client, id, held);
    }

    /// <summary>The lost-update protection is exactly what it was: a writer holding an old version is refused, whichever way the version reached it.</summary>
    private static async Task AssertStaleVersionsAreStill412(HttpClient client, string id, string current)
    {
        // Another writer saves from the current version, so `current` is now stale for the first writer.
        HttpResponseMessage other = await client.SendAsync(Request(HttpMethod.Put, $"/api/documents/{id}", Json(Body("Other writer", id)), ifMatch: current));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        HttpResponseMessage stalePut = await client.SendAsync(Request(HttpMethod.Put, $"/api/documents/{id}", Json(Body("Stale save", id)), ifMatch: current));
        await AssertProblem(stalePut, HttpStatusCode.PreconditionFailed, "Document has changed");
        HttpResponseMessage stalePatch = await client.SendAsync(Request(HttpMethod.Patch, $"/api/documents/{id}", Json("{\"title\":\"Stale rename\"}"), ifMatch: current));
        await AssertProblem(stalePatch, HttpStatusCode.PreconditionFailed, "Document has changed");

        JsonNode stored = await Read(await client.GetAsync($"/api/documents/{id}"));
        Assert.Equal("Other writer", stored["title"]!.GetValue<string>());
    }

    /// <summary>A fresh document with a named version, its current tag, and an older tag that is now stale.</summary>
    private static async Task<(string Id, string VersionId, string Current, string Stale)> Prepare(HttpClient client)
    {
        HttpResponseMessage created = await client.PostAsync("/api/documents", Json(Body("Matrix")));
        string id = (await Read(created))["id"]!.GetValue<string>();
        string stale = ETagOf(created);
        string versionId = await NameVersion(client, id);
        HttpResponseMessage moved = await client.SendAsync(Request(HttpMethod.Put, $"/api/documents/{id}", Json(Body("Matrix, moved on", id)), ifMatch: stale));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        return (id, versionId, ETagOf(moved), stale);
    }

    private static Task<HttpResponseMessage> Write(HttpClient client, string write, string id, string versionId, string? ifMatch)
    {
        return write switch
        {
            "save" => client.SendAsync(Request(HttpMethod.Put, $"/api/documents/{id}", Json(Body("Matrix, saved", id)), ifMatch)),
            "rename" => client.SendAsync(Request(HttpMethod.Patch, $"/api/documents/{id}", Json("{\"title\":\"Matrix, renamed\"}"), ifMatch)),
            "restore" => client.SendAsync(Request(HttpMethod.Post, $"/api/documents/{id}/versions/{versionId}/restore", ifMatch: ifMatch)),
            _ => throw new ArgumentOutOfRangeException(nameof(write), write, null),
        };
    }

    private static async Task<string> NameVersion(HttpClient client, string id)
    {
        HttpResponseMessage named = await client.PostAsJsonAsync($"/api/documents/{id}/versions", new { name = "v1" });
        Assert.Equal(HttpStatusCode.Created, named.StatusCode);
        return (await Read(named))["id"]!.GetValue<string>();
    }

    private static async Task<string> CurrentTag(HttpClient client, string id)
    {
        return ETagOf(await client.GetAsync($"/api/documents/{id}"));
    }

    /// <summary>A bearer-token client (no CSRF header to carry), optionally behind a gateway that rewrites what the server sent.</summary>
    private async Task<HttpClient> CreateTokenClient(DelegatingHandler? gateway = null)
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/tokens", new { name = "weak base version test" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        string token = (await Read(created))["token"]!.GetValue<string>();

        HttpClient client = gateway is null ? _factory.CreateClient() : _factory.CreateDefaultClient(gateway);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, HttpContent? content = null, string? ifMatch = null, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = content };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return request;
    }

    private static void AssertVersionHeaderMatchesETag(HttpResponseMessage response)
    {
        string tag = ETagOf(response);
        Assert.Equal(tag.Trim('"'), VersionOf(response));
        Assert.DoesNotContain('"', VersionOf(response));
    }

    /// <summary>The ETag exactly as it went on the wire, weak marker and quotes included.</summary>
    private static string ETagOf(HttpResponseMessage response)
    {
        string? tag = response.Headers.ETag?.ToString();
        Assert.False(string.IsNullOrEmpty(tag), "The response carries no ETag.");
        return tag!;
    }

    private static string VersionOf(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues(VersionHeader, out IEnumerable<string>? values), $"The response carries no {VersionHeader}.");
        return Assert.Single(values!);
    }

    private static string QuotedVersionHeader(HttpResponseMessage response)
    {
        return $"\"{VersionOf(response)}\"";
    }

    private static StringContent Json(string json)
    {
        return new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
    }

    private static string Body(string title, string? id = null)
    {
        var body = new JsonObject
        {
            ["title"] = title,
            ["type"] = "flowchart",
            ["nodes"] = new JsonArray(),
            ["edges"] = new JsonArray(),
        };
        if (id is not null)
        {
            body["id"] = id;
        }

        return body.ToJsonString();
    }

    private static async Task<JsonNode> Read(HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync(CancellationToken.None);
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body.");
    }

    private static async Task<JsonNode> AssertProblem(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        JsonNode body = await Read(response);
        Assert.Equal(title, body["title"]!.GetValue<string>());
        return body;
    }

    private enum GatewayMode
    {
        WeakenETag,
        RemoveETag,
    }

    /// <summary>What a CDN, WAF or gateway does to the ETag of a response it compresses or re-serves.</summary>
    private sealed class Gateway : DelegatingHandler
    {
        private readonly GatewayMode _mode;

        public Gateway(GatewayMode mode)
        {
            _mode = mode;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.ETag is { } tag)
            {
                if (_mode == GatewayMode.WeakenETag)
                {
                    response.Headers.ETag = new EntityTagHeaderValue(tag.Tag, isWeak: true);
                }
                else
                {
                    response.Headers.Remove("ETag");
                }
            }

            return response;
        }
    }
}
