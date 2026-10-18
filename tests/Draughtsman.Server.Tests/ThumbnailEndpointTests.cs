using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Draughtsman.Core.Models;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Endpoints;
using Draughtsman.Server.Thumbnails;
using Draughtsman.Sidecar;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// GET /api/documents/{id}/thumbnail (docs/api.md): the document drawn small as an SVG in a fixed box. Work is counted through
/// <see cref="ThumbnailService.Renders"/> and the gated sidecar below, never timed: a render that did not happen is a count that did
/// not move, whatever the machine's load.
/// </summary>
public class ThumbnailEndpointTests : IClassFixture<DraughtsmanServerFactory>, IDisposable
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private readonly DraughtsmanServerFactory _factory;
    private readonly List<DraughtsmanServerFactory> _isolated = [];

    public ThumbnailEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    public void Dispose()
    {
        foreach (DraughtsmanServerFactory isolated in _isolated)
        {
            isolated.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private ThumbnailService Thumbnails => _factory.Services.GetRequiredService<ThumbnailService>();

    [Fact]
    public async Task A_thumbnail_is_an_svg_in_the_fixed_box_served_as_an_image_that_can_run_nothing()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string id = await Create(client, Flowchart("Alpha", "Beta"));

        HttpResponseMessage response = await client.GetAsync($"/api/documents/{id}/thumbnail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        XElement root = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
        Assert.Equal("0 0 320 200", (string?)root.Attribute("viewBox"));
        Assert.Equal("320", (string?)root.Attribute("width"));
        Assert.Equal("200", (string?)root.Attribute("height"));
        // The policy an uploaded SVG is served under, so a script in the file is inert even if it is opened on its own.
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; sandbox", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        AssertRevalidated(response);
        Assert.Equal("full", Assert.Single(response.Headers.GetValues(ThumbnailEndpoints.KindHeader)));
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task A_thumbnail_is_compressed_for_a_client_that_accepts_it_and_is_the_same_picture_when_unpacked()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string id = await Create(client, Flowchart("Alpha", "Beta"));
        string plain = await (await client.GetAsync($"/api/documents/{id}/thumbnail")).Content.ReadAsStringAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{id}/thumbnail");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        byte[] packed = await response.Content.ReadAsByteArrayAsync();
        using var unpacker = new System.IO.Compression.GZipStream(new MemoryStream(packed), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(unpacker, Encoding.UTF8);
        Assert.Equal(plain, await reader.ReadToEndAsync());
        Assert.True(packed.Length < plain.Length / 2, $"{packed.Length} bytes packed from {plain.Length}");
        // Compression leaves the validator alone: the browser's If-None-Match is the same whatever it accepts.
        Assert.Equal(HttpStatusCode.NotModified, (await Conditional(client, id, response.Headers.ETag!.ToString())).StatusCode);
    }

    [Fact]
    public async Task The_etag_is_the_documents_version_tag_and_the_thumbnails_variant()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string id = await Create(client, Flowchart("Alpha", "Beta"));
        string version = ETagOf(await client.GetAsync($"/api/documents/{id}"));

        string etag = ETagOf(await client.GetAsync($"/api/documents/{id}/thumbnail"));

        Assert.StartsWith(version + ".", etag, StringComparison.Ordinal);
        Assert.Equal(ThumbnailService.VariantOf(null), etag[(version.Length + 1)..]);
    }

    [Fact]
    public async Task A_matching_if_none_match_is_a_304_with_no_body_and_draws_nothing()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string id = await Create(client, Flowchart("Alpha", "Beta"));
        HttpResponseMessage first = await client.GetAsync($"/api/documents/{id}/thumbnail");
        long rendersBefore = Thumbnails.Renders;

        HttpResponseMessage second = await Conditional(client, id, first.Headers.ETag!.ToString());

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
        Assert.Equal(first.Headers.ETag, second.Headers.ETag);
        AssertRevalidated(second);
        Assert.Equal(rendersBefore, Thumbnails.Renders);
    }

    [Fact]
    public async Task A_weak_validator_or_a_list_of_them_is_accepted_and_a_stale_one_gets_the_thumbnail()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string id = await Create(client, Flowchart("Alpha", "Beta"));
        string etag = (await client.GetAsync($"/api/documents/{id}/thumbnail")).Headers.ETag!.ToString();

        Assert.Equal(HttpStatusCode.NotModified, (await Conditional(client, id, "W/" + etag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, (await Conditional(client, id, "\"dv1:nope\", " + etag)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Conditional(client, id, "\"dv1:nope\"")).StatusCode);
    }

    [Fact]
    public async Task An_edit_gives_a_new_etag_and_a_new_picture_and_the_old_etag_no_longer_matches()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string id = await Create(client, Flowchart("Alpha", "Beta"));
        HttpResponseMessage before = await client.GetAsync($"/api/documents/{id}/thumbnail");
        string beforeSvg = await before.Content.ReadAsStringAsync();

        await Edit(client, id, document => document["nodes"]![0]!["label"] = "Renamed");
        HttpResponseMessage stale = await Conditional(client, id, before.Headers.ETag!.ToString());
        HttpResponseMessage after = await client.GetAsync($"/api/documents/{id}/thumbnail");

        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.NotEqual(before.Headers.ETag, after.Headers.ETag);
        string afterSvg = await after.Content.ReadAsStringAsync();
        Assert.Contains(">Renamed</text>", afterSvg, StringComparison.Ordinal);
        Assert.DoesNotContain(">Alpha</text>", afterSvg, StringComparison.Ordinal);
        Assert.Contains(">Alpha</text>", beforeSvg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeat_request_is_served_from_the_cache_and_an_edit_is_drawn_again()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string id = await Create(client, Flowchart("Alpha", "Beta"));
        long start = Thumbnails.Renders;

        string first = await (await client.GetAsync($"/api/documents/{id}/thumbnail")).Content.ReadAsStringAsync();
        Assert.Equal(start + 1, Thumbnails.Renders);
        string second = await (await client.GetAsync($"/api/documents/{id}/thumbnail")).Content.ReadAsStringAsync();
        string third = await (await client.GetAsync($"/api/documents/{id}/thumbnail")).Content.ReadAsStringAsync();

        Assert.Equal(start + 1, Thumbnails.Renders);
        Assert.Equal(first, second);
        Assert.Equal(first, third);

        await Edit(client, id, document => document["nodes"]![0]!["label"] = "Again");
        await client.GetAsync($"/api/documents/{id}/thumbnail");
        Assert.Equal(start + 2, Thumbnails.Renders);
    }

    [Fact]
    public async Task Signing_in_is_required_and_an_anonymous_caller_gets_no_picture()
    {
        HttpClient signedIn = await _factory.CreateAuthenticatedClient();
        string id = await Create(signedIn, Flowchart("Secret title", "Beta"));

        HttpResponseMessage anonymous = await _factory.CreateClient().GetAsync($"/api/documents/{id}/thumbnail");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.DoesNotContain("Secret title", await anonymous.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_read_only_token_may_fetch_a_thumbnail()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await Create(admin, Flowchart("Alpha", "Beta"));
        (_, string secret) = await admin.CreateToken("thumbnail reader", new { scope = "read" });

        HttpResponseMessage response = await _factory.Bearer(secret).GetAsync($"/api/documents/{id}/thumbnail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_document_is_a_problem_json_404()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/documents/doc_missing/thumbnail");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_binned_document_has_no_thumbnail_until_it_is_restored()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string id = await Create(client, Flowchart("Alpha", "Beta"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/documents/{id}/thumbnail")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/documents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/documents/{id}/thumbnail")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/bin/{id}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/documents/{id}/thumbnail")).StatusCode);
    }

    [Fact]
    public async Task An_empty_document_gets_the_dashed_empty_frame_with_no_text()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string id = await Create(client, new JsonObject { ["title"] = "Blank", ["type"] = "flowchart", ["nodes"] = new JsonArray(), ["edges"] = new JsonArray() });

        HttpResponseMessage response = await client.GetAsync($"/api/documents/{id}/thumbnail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("empty", Assert.Single(response.Headers.GetValues(ThumbnailEndpoints.KindHeader)));
        string svg = await response.Content.ReadAsStringAsync();
        Assert.Contains("stroke-dasharray", svg, StringComparison.Ordinal);
        Assert.Empty(XDocument.Parse(svg).Descendants(Svg + "text"));
    }

    [Theory]
    [InlineData("order-service")]
    [InlineData("sample-order-approval")]
    [InlineData("editor-session")]
    [InlineData("checkout-sequence")]
    [InlineData("checkout-fragments")]
    public async Task Every_document_type_has_a_full_thumbnail(string example)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        JsonNode document = Example(example);
        string id = await Create(client, document);

        HttpResponseMessage response = await client.GetAsync($"/api/documents/{id}/thumbnail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("full", Assert.Single(response.Headers.GetValues(ThumbnailEndpoints.KindHeader)));
        XElement root = XDocument.Parse(await response.Content.ReadAsStringAsync()).Root!;
        Assert.Equal("0 0 320 200", (string?)root.Attribute("viewBox"));
        // The picture really is the document's: a label from it is in the file, drawn as text.
        string firstLabel = document["nodes"]!.AsArray().Select(node => node!["label"]?.GetValue<string>()).First(label => !string.IsNullOrWhiteSpace(label))!;
        Assert.Contains(root.Descendants(Svg + "text"), text => text.Value.Contains(firstLabel[..Math.Min(firstLabel.Length, 6)], StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_flowchart_with_no_positions_is_laid_out_first_and_an_orthogonal_connector_is_routed()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        JsonObject document = Flowchart("Start", "Finish");
        foreach (JsonNode? node in document["nodes"]!.AsArray())
        {
            node!.AsObject().Remove("geometry");
        }

        string id = await Create(client, document);
        HttpResponseMessage response = await client.GetAsync($"/api/documents/{id}/thumbnail");

        Assert.Equal("full", Assert.Single(response.Headers.GetValues(ThumbnailEndpoints.KindHeader)));
        string svg = await response.Content.ReadAsStringAsync();
        Assert.Contains(">Start</text>", svg, StringComparison.Ordinal);
        Assert.Contains(">Finish</text>", svg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_label_that_is_markup_is_text_in_the_thumbnail_never_markup()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string id = await Create(client, Flowchart("<script>alert(document.cookie)</script>", "<img src=x onerror=alert(1)>"));

        string svg = await (await client.GetAsync($"/api/documents/{id}/thumbnail")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", svg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", svg, StringComparison.Ordinal);
        Assert.Null(Record.Exception(() => XDocument.Parse(svg)));
    }

    [Fact]
    public async Task The_two_thousand_node_benchmark_is_drawn_as_boxes_and_lines_with_no_labels()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string id = await Create(client, Example("perf-2000n-2500e"));

        HttpResponseMessage response = await client.GetAsync($"/api/documents/{id}/thumbnail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("simplified", Assert.Single(response.Headers.GetValues(ThumbnailEndpoints.KindHeader)));
        string svg = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<text", svg, StringComparison.Ordinal);
        Assert.True(svg.Length < 400_000, $"the benchmark's simplified thumbnail is {svg.Length:N0} characters");
    }

    [Fact]
    public async Task A_diagram_over_each_limit_drops_a_tier_and_a_diagram_that_needs_the_layout_engine_over_the_full_limit_is_a_placeholder()
    {
        var sidecar = new CountingSidecar();
        using WebApplicationFactory<Program> factory = With(new ThumbnailLimits(FullMaxElements: 4, SimplifiedMaxElements: 8), sidecar: sidecar);
        HttpClient client = await factory.CreateAuthenticatedClient();

        string small = await Create(client, Chain(2));
        string medium = await Create(client, Chain(4));
        string large = await Create(client, Chain(9));
        JsonObject unplaced = Chain(4);
        foreach (JsonNode? node in unplaced["nodes"]!.AsArray())
        {
            node!.AsObject().Remove("geometry");
        }

        string needsLayout = await Create(client, unplaced);

        Assert.Equal("full", await Kind(client, small));
        Assert.Equal("simplified", await Kind(client, medium));
        Assert.Equal("too-large", await Kind(client, large));
        Assert.Equal("too-large", await Kind(client, needsLayout));
        // The layout engine is never asked to place a diagram too large for a full drawing, and nothing else here needs it.
        Assert.Equal(0, sidecar.Layouts);
    }

    [Fact]
    public async Task A_large_sequence_diagram_is_a_placeholder_because_it_has_no_boxes_and_lines_form()
    {
        var sidecar = new CountingSidecar();
        using WebApplicationFactory<Program> factory = With(new ThumbnailLimits(FullMaxElements: 6, SimplifiedMaxElements: 100), sidecar: sidecar);
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = await Create(client, Example("checkout-fragments"));

        Assert.Equal("too-large", await Kind(client, id));
        Assert.Equal(0, sidecar.SceneCalls);
    }

    [Fact]
    public async Task A_full_drawing_over_the_size_limit_falls_back_to_the_simplified_one()
    {
        using WebApplicationFactory<Program> factory = With(new ThumbnailLimits(FullMaxSvgBytes: 2000));
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = await Create(client, Chain(12));

        HttpResponseMessage response = await client.GetAsync($"/api/documents/{id}/thumbnail");

        Assert.Equal("simplified", Assert.Single(response.Headers.GetValues(ThumbnailEndpoints.KindHeader)));
    }

    [Fact]
    public async Task A_failing_layout_engine_gives_a_placeholder_that_is_not_cached_and_has_no_etag()
    {
        var sidecar = new CountingSidecar { Failure = new SidecarUnavailableException("node is not running") };
        using WebApplicationFactory<Program> factory = With(new ThumbnailLimits(), sidecar: sidecar);
        HttpClient client = await factory.CreateAuthenticatedClient();
        JsonObject unplaced = Chain(2);
        foreach (JsonNode? node in unplaced["nodes"]!.AsArray())
        {
            node!.AsObject().Remove("geometry");
        }

        string id = await Create(client, unplaced);
        ThumbnailService service = factory.Services.GetRequiredService<ThumbnailService>();

        HttpResponseMessage first = await client.GetAsync($"/api/documents/{id}/thumbnail");
        HttpResponseMessage second = await client.GetAsync($"/api/documents/{id}/thumbnail");

        // A page of cards must still load: a placeholder, not a 500.
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("unavailable", Assert.Single(first.Headers.GetValues(ThumbnailEndpoints.KindHeader)));
        Assert.Null(first.Headers.ETag);
        Assert.True(first.Headers.CacheControl is { Private: true, NoStore: true }, "a placeholder drawn while the layout engine was down must not be kept");
        Assert.Equal(2, service.Renders);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        // Once the engine answers, the next request draws the real thing.
        sidecar.Failure = null;
        HttpResponseMessage third = await client.GetAsync($"/api/documents/{id}/thumbnail");
        Assert.Equal("full", Assert.Single(third.Headers.GetValues(ThumbnailEndpoints.KindHeader)));
        Assert.NotNull(third.Headers.ETag);
    }

    [Fact]
    public async Task A_connector_that_could_not_be_routed_draws_straight_and_is_tried_again_next_time()
    {
        var sidecar = new CountingSidecar { RouteFailure = new SidecarUnavailableException("node is not running") };
        using WebApplicationFactory<Program> factory = With(new ThumbnailLimits(), sidecar: sidecar);
        HttpClient client = await factory.CreateAuthenticatedClient();
        JsonObject document = Chain(2);
        document["edges"]!.AsArray()[0]!["routing"] = new JsonObject { ["mode"] = "orthogonal" };
        string id = await Create(client, document);

        HttpResponseMessage first = await client.GetAsync($"/api/documents/{id}/thumbnail");
        HttpResponseMessage second = await client.GetAsync($"/api/documents/{id}/thumbnail");

        Assert.Equal("full", Assert.Single(first.Headers.GetValues(ThumbnailEndpoints.KindHeader)));
        Assert.Null(first.Headers.ETag);
        Assert.Equal(2, sidecar.Routes);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_thumbnail_share_one_render()
    {
        var sidecar = new CountingSidecar { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using WebApplicationFactory<Program> factory = With(new ThumbnailLimits(), sidecar: sidecar);
        HttpClient client = await factory.CreateAuthenticatedClient();
        JsonObject unplaced = Chain(2);
        foreach (JsonNode? node in unplaced["nodes"]!.AsArray())
        {
            node!.AsObject().Remove("geometry");
        }

        string id = await Create(client, unplaced);

        Task<HttpResponseMessage>[] requests = Enumerable.Range(0, 5).Select(_ => client.GetAsync($"/api/documents/{id}/thumbnail")).ToArray();
        await sidecar.FirstLayoutStarted.Task.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None); // a hang guard, not a speed assertion
        sidecar.Gate!.SetResult();
        HttpResponseMessage[] responses = await Task.WhenAll(requests);

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(1, factory.Services.GetRequiredService<ThumbnailService>().Renders);
        Assert.Equal(1, sidecar.Layouts);
    }

    [Fact]
    public async Task At_most_the_configured_number_of_thumbnails_are_drawn_at_once_and_the_rest_wait_their_turn()
    {
        var sidecar = new CountingSidecar { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using WebApplicationFactory<Program> factory = With(new ThumbnailLimits(), concurrency: 2, sidecar: sidecar);
        HttpClient client = await factory.CreateAuthenticatedClient();
        var ids = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            JsonObject unplaced = Chain(2);
            unplaced["title"] = "Doc " + i;
            foreach (JsonNode? node in unplaced["nodes"]!.AsArray())
            {
                node!.AsObject().Remove("geometry");
            }

            ids.Add(await Create(client, unplaced));
        }

        Task<HttpResponseMessage>[] requests = ids.Select(id => client.GetAsync($"/api/documents/{id}/thumbnail")).ToArray();
        await sidecar.WaitForLayouts(2);
        // Two are in the layout engine; the other three are queued behind the semaphore, not refused and not started.
        Assert.Equal(2, sidecar.Layouts);
        Assert.DoesNotContain(requests, request => request.IsCompleted);

        sidecar.Gate!.SetResult();
        HttpResponseMessage[] responses = await Task.WhenAll(requests);

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(5, sidecar.Layouts);
        Assert.Equal(2, sidecar.PeakLayouts);
    }

    [Fact]
    public async Task The_organisations_diagram_theme_paints_the_thumbnail_and_a_change_of_theme_is_a_new_etag()
    {
        var themes = new StubThemes();
        using WebApplicationFactory<Program> factory = With(new ThumbnailLimits(), themes: themes);
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = await Create(client, Flowchart("Alpha", "Beta"));
        HttpResponseMessage shipped = await client.GetAsync($"/api/documents/{id}/thumbnail");

        themes.Current = new DiagramThemeChoice("neutral", new Dictionary<string, string>(), "#123456");
        HttpResponseMessage stale = await Conditional(client, id, shipped.Headers.ETag!.ToString());
        HttpResponseMessage themed = await client.GetAsync($"/api/documents/{id}/thumbnail");

        // The thumbnail's version of the document is unchanged; the theme alone made the browser's copy stale.
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.NotEqual(shipped.Headers.ETag, themed.Headers.ETag);
        XElement root = XDocument.Parse(await themed.Content.ReadAsStringAsync()).Root!;
        Assert.Equal("#123456", (string?)root.Elements(Svg + "rect").First().Attribute("fill"));
        Assert.Equal(HttpStatusCode.NotModified, (await Conditional(client, id, themed.Headers.ETag!.ToString())).StatusCode);
    }

    [Fact]
    public async Task Thumbnail_requests_are_limited_per_user_a_revalidation_counts_and_zero_turns_the_limit_off()
    {
        using var limited = new ConfiguredServerFactory("limits:\n  thumbnailPerUserPerMinute: 2\n");
        HttpClient alice = await limited.CreateAuthenticatedClient();
        string id = await Create(alice, Flowchart("Alpha", "Beta"));
        await alice.CreateUser("bob@example.com");
        HttpClient bob = await limited.SignIn("bob@example.com");

        HttpResponseMessage first = await alice.GetAsync($"/api/documents/{id}/thumbnail");
        HttpResponseMessage revalidation = await Conditional(alice, id, first.Headers.ETag!.ToString());
        HttpResponseMessage refused = await alice.GetAsync($"/api/documents/{id}/thumbnail");
        HttpResponseMessage bobsFirst = await bob.GetAsync($"/api/documents/{id}/thumbnail");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotModified, revalidation.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(int.Parse(Assert.Single(refused.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture) >= 1);
        Assert.Equal(HttpStatusCode.OK, bobsFirst.StatusCode);

        using var unlimited = new ConfiguredServerFactory("limits:\n  thumbnailPerUserPerMinute: 0\n");
        HttpClient client = await unlimited.CreateAuthenticatedClient();
        string other = await Create(client, Flowchart("Alpha", "Beta"));
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/documents/{other}/thumbnail")).StatusCode);
        }
    }

    [Fact]
    public void The_default_limits_leave_room_for_a_full_page_of_cards()
    {
        // Fifty cards ask once each and a repeat visit revalidates them: a lower default would show broken images on an ordinary page.
        Assert.True(new Draughtsman.Server.Configuration.LimitsConfig().ThumbnailPerUserPerMinute >= 200);
        Assert.True(new ThumbnailLimits().CacheEntries >= 200);
    }

    /// <summary>
    /// A server of its own, with its own data folder, for a test that replaces the thumbnail service, the sidecar or the theme: two live
    /// hosts must not share one SQLite file (a host derived from the class's factory would), or a lock between them is a flaky test.
    /// </summary>
    private WebApplicationFactory<Program> With(ThumbnailLimits limits, int concurrency = 2, ISidecarClient? sidecar = null, IDiagramThemeProvider? themes = null)
    {
        var own = new DraughtsmanServerFactory();
        _isolated.Add(own);
        return own.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ThumbnailService>();
            services.AddSingleton(new ThumbnailService(limits, concurrency, NullLogger<ThumbnailService>.Instance));
            if (sidecar is not null)
            {
                services.AddSingleton(sidecar);
            }

            if (themes is not null)
            {
                services.AddSingleton(themes);
            }
        }));
    }

    /// <summary>Kept by the browser, never by a shared cache, and revalidated every time: <c>private, no-cache</c>, whatever order the client lists the directives in.</summary>
    private static void AssertRevalidated(HttpResponseMessage response)
    {
        CacheControlHeaderValue? cache = response.Headers.CacheControl;
        Assert.True(cache is { Private: true, NoCache: true, NoStore: false }, $"Cache-Control was '{cache}'");
        Assert.Null(cache!.MaxAge);
    }

    private static async Task<string> Kind(HttpClient client, string id)
    {
        HttpResponseMessage response = await client.GetAsync($"/api/documents/{id}/thumbnail");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(response.Headers.GetValues(ThumbnailEndpoints.KindHeader));
    }

    private static async Task<HttpResponseMessage> Conditional(HttpClient client, string id, string ifNoneMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/documents/{id}/thumbnail");
        request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return await client.SendAsync(request);
    }

    private static string ETagOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.ETag!.Tag.Trim('"');
    }

    private static async Task<string> Create(HttpClient client, JsonNode document)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/documents", document);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();
    }

    private static async Task Edit(HttpClient client, string id, Action<JsonNode> change)
    {
        HttpResponseMessage read = await client.GetAsync($"/api/documents/{id}");
        JsonNode document = (await read.Content.ReadFromJsonAsync<JsonNode>())!;
        change(document);
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{id}") { Content = JsonContent.Create(document) };
        request.Headers.IfMatch.Add(read.Headers.ETag!);
        HttpResponseMessage put = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    /// <summary>A flowchart of two placed nodes with one orthogonal connector between them.</summary>
    private static JsonObject Flowchart(string first, string second)
    {
        return new JsonObject
        {
            ["title"] = "Thumbnail test",
            ["type"] = "flowchart",
            ["nodes"] = new JsonArray(
                new JsonObject { ["id"] = "n_a", ["kind"] = "process", ["label"] = first, ["shape"] = "rectangle", ["geometry"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["w"] = 160, ["h"] = 60 } },
                new JsonObject { ["id"] = "n_b", ["kind"] = "process", ["label"] = second, ["shape"] = "rectangle", ["geometry"] = new JsonObject { ["x"] = 320, ["y"] = 200, ["w"] = 160, ["h"] = 60 } }),
            ["edges"] = new JsonArray(
                new JsonObject { ["id"] = "e_1", ["kind"] = "flow", ["from"] = new JsonObject { ["node"] = "n_a" }, ["to"] = new JsonObject { ["node"] = "n_b" }, ["routing"] = new JsonObject { ["mode"] = "orthogonal" } }),
        };
    }

    /// <summary>A chain of <paramref name="nodes"/> placed nodes with a straight connector between each pair: <c>2 * nodes - 1</c> elements.</summary>
    private static JsonObject Chain(int nodes)
    {
        var nodeArray = new JsonArray();
        var edgeArray = new JsonArray();
        for (int i = 0; i < nodes; i++)
        {
            nodeArray.Add(new JsonObject { ["id"] = "n_" + i, ["kind"] = "process", ["label"] = "Step " + i, ["shape"] = "rectangle", ["geometry"] = new JsonObject { ["x"] = i * 200, ["y"] = 0, ["w"] = 160, ["h"] = 60 } });
            if (i > 0)
            {
                edgeArray.Add(new JsonObject { ["id"] = "e_" + i, ["kind"] = "flow", ["from"] = new JsonObject { ["node"] = "n_" + (i - 1) }, ["to"] = new JsonObject { ["node"] = "n_" + i }, ["routing"] = new JsonObject { ["mode"] = "straight" } });
            }
        }

        return new JsonObject { ["title"] = "Chain " + nodes, ["type"] = "flowchart", ["nodes"] = nodeArray, ["edges"] = edgeArray };
    }

    private static JsonNode Example(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "contract", name + ".json");
        JsonNode document = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))!;
        // The server assigns a fresh id, so the same example can be created by more than one test.
        document.AsObject().Remove("id");
        return document;
    }

    private sealed class StubThemes : IDiagramThemeProvider
    {
        public DiagramThemeChoice? Current { get; set; }

        public DiagramThemeChoice? Default => Current;
    }

    /// <summary>
    /// A sidecar that lays a document out with a trivial grid, counts what it is asked, can be told to fail, and can hold every layout
    /// until a test opens the gate, so a test sees how many run at once without a stopwatch.
    /// </summary>
    private sealed class CountingSidecar : ISidecarClient
    {
        private readonly object _gate = new();
        private int _inLayout;
        private int _layouts;
        private int _scenes;
        private int _routes;
        private int _peak;
        private TaskCompletionSource _layoutStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SidecarException? Failure { get; set; }

        public SidecarException? RouteFailure { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public TaskCompletionSource FirstLayoutStarted => _layoutStarted;

        public int Layouts => Volatile.Read(ref _layouts);

        public int SceneCalls => Volatile.Read(ref _scenes);

        public int Routes => Volatile.Read(ref _routes);

        public int PeakLayouts => Volatile.Read(ref _peak);

        public SidecarStatus Status => new(SidecarState.Running);

        public Task<SidecarHealth> PingAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SidecarHealth(true, "0", "v0", 0));

        public async Task<LayoutResult> LayoutAsync(Document document, LayoutOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                Interlocked.Increment(ref _layouts);
                throw Failure;
            }

            int now;
            lock (_gate)
            {
                now = ++_inLayout;
                _peak = Math.Max(_peak, now);
            }

            Interlocked.Increment(ref _layouts);
            _layoutStarted.TrySetResult();
            try
            {
                if (Gate is not null)
                {
                    await Gate.Task.WaitAsync(cancellationToken);
                }

                double x = 0;
                List<Node> placed = document.Nodes.Select(node =>
                {
                    Geometry geometry = node.Geometry ?? new Geometry(x, 0, 120, 60);
                    x += 160;
                    return node with { Geometry = geometry };
                }).ToList();
                return new LayoutResult(document with { Nodes = placed }, []);
            }
            finally
            {
                lock (_gate)
                {
                    _inLayout--;
                }
            }
        }

        public async Task WaitForLayouts(int count)
        {
            // A hang guard, not a speed assertion: it returns the moment the count is reached.
            using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (Layouts < count)
            {
                await Task.Delay(10, giveUp.Token);
            }
        }

        public Task<IReadOnlyDictionary<string, EdgeRoute>> RouteAsync(Document document, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _routes);
            return RouteFailure is not null
                ? throw RouteFailure
                : Task.FromResult<IReadOnlyDictionary<string, EdgeRoute>>(new Dictionary<string, EdgeRoute>());
        }

        public Task<SequenceScene> SequenceSceneAsync(Document document, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _scenes);
            throw new NotSupportedException("No scene in this fake.");
        }

        public Task<Document> YjsToJsonAsync(byte[]? state, IReadOnlyList<byte[]>? updates = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<byte[]> YjsFromJsonAsync(Document document, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<YjsState> ApplyUpdatesAsync(byte[]? state, IReadOnlyList<byte[]> updates, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
