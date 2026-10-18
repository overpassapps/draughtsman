using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The flythrough (docs/flythrough.md) through the HTTP contract: stored and served back with the document, refused when
/// the schema refuses it, carried through layout untouched, read from and written to the DSL, and ignored by the Mermaid
/// export. The shipped example carries a tour, so it is the fixture; every assertion has the opposite case beside it.
/// </summary>
public class FlythroughEndpointTests(DraughtsmanServerFactory factory) : IClassFixture<DraughtsmanServerFactory>
{
    private static JsonNode Example()
    {
        return JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "order-service.json")))!;
    }

    private static StringContent Json(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task<JsonNode> Read(HttpResponseMessage response)
    {
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None))!;
    }

    private static async Task<(string Id, JsonNode Body)> Create(HttpClient client, JsonNode document)
    {
        // The server assigns the id of a document it creates; the unsaved-document endpoints below need the one the example has.
        JsonNode fresh = document.DeepClone();
        fresh.AsObject().Remove("id");
        using HttpResponseMessage created = await client.PostAsync("/api/documents", Json(fresh), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonNode body = await Read(created);
        return (body["id"]!.GetValue<string>(), body);
    }

    private static async Task<HttpResponseMessage> Replace(HttpClient client, string id, JsonNode document)
    {
        using HttpResponseMessage current = await client.GetAsync($"/api/documents/{id}", CancellationToken.None);
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{id}") { Content = Json(document) };
        request.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.ToString());
        return await client.SendAsync(request, CancellationToken.None);
    }

    [Fact]
    public async Task A_document_is_served_back_with_the_tour_it_was_stored_with_and_without_one_it_carries_none()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        JsonNode withTour = Example();
        JsonNode without = Example();
        without.AsObject().Remove("flythrough");

        (string tourId, JsonNode tourBody) = await Create(client, withTour);
        (string plainId, JsonNode plainBody) = await Create(client, without);

        Assert.True(JsonNode.DeepEquals(withTour["flythrough"], tourBody["flythrough"]));
        Assert.Equal(["k_overview", "k_web", "k_vpc", "k_data"], tourBody["flythrough"]!["keyframes"]!.AsArray().Select(k => k!["id"]!.GetValue<string>()));
        Assert.Null(plainBody["flythrough"]);
        Assert.Equal(1, tourBody["schemaVersion"]!.GetValue<int>());
        using HttpResponseMessage fetched = await client.GetAsync($"/api/documents/{tourId}", CancellationToken.None);
        Assert.True(JsonNode.DeepEquals(tourBody["flythrough"], (await Read(fetched))["flythrough"]));
        using HttpResponseMessage fetchedPlain = await client.GetAsync($"/api/documents/{plainId}", CancellationToken.None);
        Assert.Null((await Read(fetchedPlain))["flythrough"]);
    }

    [Fact]
    public async Task Replacing_a_document_changes_the_tour_and_removing_it_removes_the_member()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        (string id, JsonNode stored) = await Create(client, Example());

        JsonNode edited = stored.DeepClone();
        edited["flythrough"]!["keyframes"]![1]!["caption"] = "Edited over the API";
        edited["flythrough"]!["keyframes"]!.AsArray().RemoveAt(3);
        using HttpResponseMessage changed = await Replace(client, id, edited);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        JsonNode afterEdit = await Read(changed);
        Assert.Equal("Edited over the API", afterEdit["flythrough"]!["keyframes"]![1]!["caption"]!.GetValue<string>());
        Assert.Equal(3, afterEdit["flythrough"]!["keyframes"]!.AsArray().Count);

        JsonNode removed = afterEdit.DeepClone();
        removed.AsObject().Remove("flythrough");
        using HttpResponseMessage cleared = await Replace(client, id, removed);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await Read(cleared))["flythrough"]);
    }

    [Theory]
    [InlineData("/flythrough/keyframes/0/durationMs", -1, "durationMs")]
    [InlineData("/flythrough/keyframes/0/holdMs", 600001, "holdMs")]
    [InlineData("/flythrough/keyframes/0/view/w", 0, "/w")]
    [InlineData("/flythrough/keyframes/0/view/h", -4, "/h")]
    public async Task A_tour_the_schema_refuses_is_a_400_naming_where(string pointer, double value, string expected)
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        JsonNode document = Example();
        JsonNode node = document;
        string[] path = pointer.Trim('/').Split('/');
        foreach (string step in path[..^1])
        {
            node = int.TryParse(step, out int index) ? node[index]! : node[step]!;
        }

        node[path[^1]] = value;
        using HttpResponseMessage response = await client.PostAsync("/api/documents", Json(document), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string errors = (await Read(response))["errors"]!.ToJsonString();
        Assert.Contains(expected, errors, StringComparison.Ordinal);
        Assert.Contains("/flythrough/keyframes/0", errors, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tour_with_no_keyframes_and_one_with_an_unknown_member_are_refused_too()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        JsonNode empty = Example();
        empty["flythrough"]!["keyframes"] = new JsonArray();
        JsonNode extra = Example();
        extra["flythrough"]!["autoplay"] = true;

        using HttpResponseMessage emptyResponse = await client.PostAsync("/api/documents", Json(empty), CancellationToken.None);
        using HttpResponseMessage extraResponse = await client.PostAsync("/api/documents", Json(extra), CancellationToken.None);
        using HttpResponseMessage fine = await client.PostAsync("/api/documents", Json(Example()), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, extraResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
    }

    [Fact]
    public async Task Layout_returns_the_tour_it_was_given_untouched_and_does_not_invent_one()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        JsonNode input = Example();
        foreach (JsonNode? node in input["nodes"]!.AsArray())
        {
            node!.AsObject().Remove("geometry");
        }

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/layout", input, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode laidOut = (await Read(response))["document"]!;
        Assert.True(JsonNode.DeepEquals(input["flythrough"], laidOut["flythrough"]));

        JsonNode plain = input.DeepClone();
        plain.AsObject().Remove("flythrough");
        using HttpResponseMessage plainResponse = await client.PostAsJsonAsync("/api/layout", plain, CancellationToken.None);
        Assert.Null((await Read(plainResponse))["document"]!["flythrough"]);
    }

    [Fact]
    public async Task The_mermaid_export_is_the_same_text_with_the_tour_and_without_it()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        JsonNode withTour = Example();
        JsonNode without = Example();
        without.AsObject().Remove("flythrough");

        using HttpResponseMessage tourResponse = await client.PostAsync("/api/export/mermaid", Json(withTour), CancellationToken.None);
        using HttpResponseMessage plainResponse = await client.PostAsync("/api/export/mermaid", Json(without), CancellationToken.None);
        string tourText = await tourResponse.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, tourResponse.StatusCode);
        Assert.Equal(await plainResponse.Content.ReadAsStringAsync(CancellationToken.None), tourText);
        Assert.DoesNotContain("Traffic enters", tourText, StringComparison.Ordinal);
        // Not blind to the graph: a renamed node changes it.
        JsonNode renamed = Example();
        renamed["nodes"]![0]!["label"] = "Storefront";
        using HttpResponseMessage renamedResponse = await client.PostAsync("/api/export/mermaid", Json(renamed), CancellationToken.None);
        Assert.NotEqual(tourText, await renamedResponse.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_dsl_endpoints_write_and_read_the_keyframe_lines()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        using HttpResponseMessage printed = await client.PostAsync("/api/export/dsl", Json(Example()), CancellationToken.None);
        string dsl = await printed.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Contains("\nflythrough loop=true\n", dsl, StringComparison.Ordinal);
        Assert.Contains("keyframe k_web @ 0,0 400x250 caption=\"Traffic enters through the web app over HTTPS\" duration=1600\n", dsl, StringComparison.Ordinal);

        const string typed = "draughtsman 1\ntype freeform\ntitle \"T\"\nnode n_a x \"A\" @ 0,0\nkeyframe k_1 @ 0,0 300x200 caption=\"Hello\" easing=cut\nkeyframe k_2 @ 0,0 300x0\n";
        using HttpResponseMessage parsed = await client.PostAsync("/api/import/dsl", new StringContent(typed, Encoding.UTF8, "text/plain"), CancellationToken.None);
        JsonNode result = await Read(parsed);

        Assert.Equal(HttpStatusCode.OK, parsed.StatusCode);
        JsonNode keyframe = Assert.Single(result["document"]!["flythrough"]!["keyframes"]!.AsArray())!;
        Assert.Equal("cut", keyframe["easing"]!.GetValue<string>());
        // The keyframe with no area was skipped with a warning, not turned into a document the schema refuses.
        Assert.Contains(result["warnings"]!.AsArray(), warning => warning!["message"]!.GetValue<string>().Contains("no area", StringComparison.Ordinal));
    }
}
