using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The Connection check against a real server with fake proxies in front of it. Each fake does one thing a real proxy does to a response:
/// weakens the ETag (Cloudflare when it compresses), strips it, adds a content-encoding. A <see cref="SimBrowser"/> then does what the page
/// does (the two echo calls, then handing what it saw back for judging) and keeps cookies the way a browser does, including dropping a
/// Secure cookie that arrives over plain http. Nothing here is mocked on the server's side: the findings come from the endpoints.
/// </summary>
public class SelfCheckEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public SelfCheckEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    // ------------------------------------------------------------------------------------------------------ access

    [Fact]
    public async Task Only_an_administrators_browser_session_can_use_the_self_check()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        await admin.CreateUser("selfcheck-editor@example.com");
        HttpClient editor = await _factory.SignIn("selfcheck-editor@example.com");
        (_, string adminTokenSecret) = await admin.CreateToken("selfcheck admin token");
        HttpClient adminBearer = _factory.Bearer(adminTokenSecret);
        HttpClient anonymous = _factory.CreateClient();
        var body = new { pageScheme = "http" };

        foreach ((HttpMethod method, string url) in new[] { (HttpMethod.Get, "/api/admin/selfcheck"), (HttpMethod.Get, SelfCheckEndpoints.EchoPath), (HttpMethod.Post, "/api/admin/selfcheck/roundtrip") })
        {
            async Task<HttpResponseMessage> Send(HttpClient client)
            {
                // Only a signed-in browser session has a CSRF token to quote; the anonymous and bearer clients send none.
                return await client.SendAsync(AuthBarHelpers.Request(method, url, method == HttpMethod.Post ? body : null, client == anonymous || client == adminBearer ? null : client));
            }

            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(anonymous)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(editor)).StatusCode);
            // A token minted by an administrator carries the admin role, but this is a cookie-session page like the status page.
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(adminBearer)).StatusCode);
            Assert.True((await Send(admin)).IsSuccessStatusCode, $"{method} {url} should work for an administrator");
        }
    }

    // ------------------------------------------------------------------------------------------------------ the server-side findings

    [Fact]
    public async Task The_findings_name_the_peer_the_scheme_the_throttle_address_and_the_host_and_show_nothing_secret()
    {
        using var factory = new FakeIpServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/admin/selfcheck?pageScheme=http");
        request.Headers.TryAddWithoutValidation(FakeIpServerFactory.Header, "172.17.0.1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.7");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("CF-Connecting-IP", "198.51.100.7");
        HttpResponseMessage response = await admin.SendAsync(request);
        string raw = await response.Content.ReadAsStringAsync();
        JsonNode body = JsonNode.Parse(raw)!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode proxy = Item(body, "proxy");
        // Not trusted (knownProxies is empty and 172.17.0.1 is not loopback): the headers are shown raw, and ignored.
        Assert.Equal("warn", proxy["status"]!.GetValue<string>());
        Assert.Equal("172.17.0.1", proxy["facts"]!["Direct peer address"]!.GetValue<string>());
        Assert.Equal("no", proxy["facts"]!["Peer trusted (server.knownProxies)"]!.GetValue<string>());
        Assert.Equal("https", proxy["facts"]!["X-Forwarded-Proto"]!.GetValue<string>());
        Assert.Equal("198.51.100.7", proxy["facts"]!["X-Forwarded-For"]!.GetValue<string>());
        Assert.Equal("198.51.100.7", proxy["facts"]!["CF-Connecting-IP"]!.GetValue<string>());
        // The address the sign-in throttle will use is the proxy's, because the header was not believed.
        Assert.Equal("172.17.0.1", proxy["facts"]!["Client address the sign-in throttle uses"]!.GetValue<string>());
        Assert.Contains("172.17.0.1", proxy["fix"]!["yaml"]!.GetValue<string>(), StringComparison.Ordinal);
        // The JSON is the findings and nothing a helper property of the C# record would add to it.
        Assert.Null(proxy["fix"]!["isEmpty"]);
        Assert.Equal("localhost", Item(body, "host")["facts"]!["Host header received"]!.GetValue<string>());
        Assert.Equal("pass", Item(body, "data")["status"]!.GetValue<string>());
        Assert.Equal("pass", Item(body, "disk")["status"]!.GetValue<string>());
        Assert.Equal("pass", Item(body, "build")["status"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(Item(body, "build")["facts"]!["Version"]!.GetValue<string>()));
        // Nothing secret: no cookie value, no password, no token, no key.
        Assert.DoesNotContain(AuthTestHelpers.DefaultPassword, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("XSRF", raw, StringComparison.Ordinal);
        // The cookie's NAME is shown (which one the server would set); a cookie VALUE never is.
        Assert.DoesNotContain("ds_session=", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Cookie:", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_host_check_reads_the_hosts_the_server_answers_to_and_the_name_a_proxy_says_visitors_used()
    {
        using var factory = new FakeIpServerFactory("server:\n  allowedHosts:\n    - localhost\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode plain = await (await admin.GetAsync("/api/admin/selfcheck")).Json();
        using HttpRequestMessage forwarded = new(HttpMethod.Get, "/api/admin/selfcheck");
        forwarded.Headers.TryAddWithoutValidation("X-Forwarded-Host", "draughtsman.example.com");
        JsonNode viaProxy = await (await admin.SendAsync(forwarded)).Json();

        JsonNode host = Item(plain, "host");
        Assert.Equal("pass", host["status"]!.GetValue<string>());
        Assert.Equal("localhost", host["facts"]!["server.allowedHosts"]!.GetValue<string>());
        JsonNode warned = Item(viaProxy, "host");
        Assert.Equal("warn", warned["status"]!.GetValue<string>());
        Assert.Equal("draughtsman.example.com", warned["facts"]!["X-Forwarded-Host"]!.GetValue<string>());
        Assert.Contains("allowedHosts", warned["fix"]!["yaml"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trusted_proxy_makes_the_same_request_pass_and_moves_the_throttle_address_to_the_visitor()
    {
        using var factory = new FakeIpServerFactory("server:\n  knownProxies:\n    - 172.17.0.0/16\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/admin/selfcheck?pageScheme=https");
        request.Headers.TryAddWithoutValidation(FakeIpServerFactory.Header, "172.17.0.1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.7");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        JsonNode body = await (await admin.SendAsync(request)).Json();

        JsonNode proxy = Item(body, "proxy");
        Assert.Equal("pass", proxy["status"]!.GetValue<string>());
        Assert.Equal("yes", proxy["facts"]!["Peer trusted (server.knownProxies)"]!.GetValue<string>());
        Assert.Equal("198.51.100.7", proxy["facts"]!["Client address the sign-in throttle uses"]!.GetValue<string>());
        Assert.Equal("https", proxy["facts"]!["Scheme the server believes"]!.GetValue<string>());
        Assert.Equal("pass", Item(body, "secure")["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Force_https_on_a_plain_http_page_fails_the_secure_check_and_the_summary_counts_it()
    {
        using var factory = new ConfiguredServerFactory("server:\n  forceHttps: true\n");
        SimBrowser browser = await SimBrowser.SignedIn(factory, pageIsHttps: false);

        JsonNode http = await browser.GetJson("/api/admin/selfcheck?pageScheme=http");
        JsonNode https = await browser.GetJson("/api/admin/selfcheck?pageScheme=https");

        Assert.Equal("fail", Item(http, "secure")["status"]!.GetValue<string>());
        Assert.True(http["summary"]!["fail"]!.GetValue<int>() >= 1);
        Assert.Equal("pass", Item(https, "secure")["status"]!.GetValue<string>());
        Assert.Equal(0, https["summary"]!["fail"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_status_page_carries_the_peer_and_a_summary_of_the_checks()
    {
        using var factory = new FakeIpServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/admin/status");
        request.Headers.TryAddWithoutValidation(FakeIpServerFactory.Header, "172.17.0.1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.7");

        JsonNode status = await (await admin.SendAsync(request)).Json();

        JsonNode connection = status["connection"]!;
        Assert.Equal("172.17.0.1", connection["peer"]!.GetValue<string>());
        Assert.False(connection["peerTrusted"]!.GetValue<bool>());
        Assert.True(connection["forwardedHeadersSeen"]!.GetValue<bool>());
        Assert.Equal("172.17.0.1", connection["throttleAddress"]!.GetValue<string>());
        Assert.True(connection["checks"]!["warn"]!.GetValue<int>() >= 1);
        Assert.Contains(connection["problems"]!.AsArray(), p => p!.GetValue<string>() == "Reverse proxy and client address");
    }

    // ------------------------------------------------------------------------------------------------------ the browser round trip, through fake proxies

    [Fact]
    public async Task With_no_proxy_in_the_way_every_round_trip_check_passes()
    {
        SimBrowser browser = await SimBrowser.SignedIn(_factory, pageIsHttps: false);

        IReadOnlyDictionary<string, string> result = await browser.RoundTrip();

        Assert.Equal("pass", result["cookie"]);
        Assert.Equal("pass", result["etag"]);
        Assert.Equal("pass", result["encoding"]);
        Assert.Equal("pass", result["clock"]);
    }

    [Fact]
    public async Task A_proxy_that_weakens_the_etag_is_found()
    {
        SimBrowser browser = await SimBrowser.SignedIn(_factory, pageIsHttps: false, Rewrite(r => r.Headers.ETag = new EntityTagHeaderValue(r.Headers.ETag!.Tag, isWeak: true)));

        IReadOnlyDictionary<string, string> result = await browser.RoundTrip();

        Assert.Equal("warn", result["etag"]);
        Assert.Equal("pass", result["cookie"]);
        Assert.Contains("weakened", browser.LastSummaries["etag"], StringComparison.Ordinal);
        Assert.Contains("issue 63", browser.LastSummaries["etag"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_proxy_that_strips_the_etag_is_found()
    {
        SimBrowser browser = await SimBrowser.SignedIn(_factory, pageIsHttps: false, Rewrite(r => r.Headers.ETag = null));

        IReadOnlyDictionary<string, string> result = await browser.RoundTrip();

        Assert.Equal("warn", result["etag"]);
        Assert.Contains("never reached the browser", browser.LastSummaries["etag"], StringComparison.Ordinal);
        Assert.Equal("pass", result["encoding"]);
    }

    [Fact]
    public async Task A_proxy_that_adds_a_content_encoding_is_found()
    {
        SimBrowser browser = await SimBrowser.SignedIn(_factory, pageIsHttps: false, Rewrite(r => r.Content.Headers.ContentEncoding.Add("gzip")));

        IReadOnlyDictionary<string, string> result = await browser.RoundTrip();

        Assert.Equal("warn", result["encoding"]);
        Assert.Contains("gzip", browser.LastSummaries["encoding"], StringComparison.Ordinal);
        Assert.Equal("pass", result["etag"]);
    }

    [Fact]
    public async Task A_proxy_that_drops_set_cookie_is_found()
    {
        SimBrowser browser = await SimBrowser.SignedIn(_factory, pageIsHttps: false, Rewrite(r => r.Headers.Remove("Set-Cookie")));

        IReadOnlyDictionary<string, string> result = await browser.RoundTrip();

        Assert.Equal("fail", result["cookie"]);
        Assert.Contains("did not send the test cookie back", browser.LastSummaries["cookie"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secure_cookie_over_plain_http_with_force_https_on_is_dropped_by_the_browser_and_found_and_fine_over_https()
    {
        using var factory = new ConfiguredServerFactory("server:\n  forceHttps: true\n");
        SimBrowser http = await SimBrowser.SignedIn(factory, pageIsHttps: false);
        SimBrowser https = await SimBrowser.SignedIn(factory, pageIsHttps: true);

        IReadOnlyDictionary<string, string> overHttp = await http.RoundTrip();
        IReadOnlyDictionary<string, string> overHttps = await https.RoundTrip();

        Assert.Equal("fail", overHttp["cookie"]);
        Assert.Contains("marked Secure, but you are on plain http://", http.LastSummaries["cookie"], StringComparison.Ordinal);
        Assert.Equal("pass", overHttps["cookie"]);
    }

    [Fact]
    public async Task The_clock_check_sees_a_browser_whose_clock_is_far_off()
    {
        SimBrowser browser = await SimBrowser.SignedIn(_factory, pageIsHttps: false);

        IReadOnlyDictionary<string, string> skewed = await browser.RoundTrip(clientClockOffset: TimeSpan.FromDays(40));
        IReadOnlyDictionary<string, string> close = await browser.RoundTrip(clientClockOffset: TimeSpan.FromSeconds(30));
        IReadOnlyDictionary<string, string> minutes = await browser.RoundTrip(clientClockOffset: TimeSpan.FromMinutes(10));

        // The browser's clock 40 days ahead means the server's is 40 days behind: the session cookie would be born expired.
        Assert.Equal("fail", skewed["clock"]);
        Assert.Equal("pass", close["clock"]);
        Assert.Equal("warn", minutes["clock"]);
    }

    // ------------------------------------------------------------------------------------------------------ the echo

    [Fact]
    public async Task The_echo_is_never_compressed_by_the_server_even_when_the_browser_offers_every_encoding()
    {
        SimBrowser browser = await SimBrowser.SignedIn(_factory, pageIsHttps: false);
        browser.AcceptEncoding = "br, gzip";

        HttpResponseMessage echo = (await browser.Get(SelfCheckEndpoints.EchoPath + "?step=1")).Response;
        // The control: the same offer to an ordinary JSON route IS compressed, so a pass above proves the exclusion and not an unconfigured test.
        HttpResponseMessage ordinary = (await browser.Get("/api/health")).Response;

        Assert.Empty(echo.Content.Headers.ContentEncoding);
        Assert.NotEmpty(ordinary.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task The_echo_sets_a_cookie_like_the_session_cookie_and_a_strong_etag_that_matches_its_body()
    {
        SimBrowser browser = await SimBrowser.SignedIn(_factory, pageIsHttps: false);

        (HttpResponseMessage response, string raw) = await browser.Get(SelfCheckEndpoints.EchoPath + "?step=1");
        JsonNode body = JsonNode.Parse(raw)!;

        string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("ds_selfcheck=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        // Plain http here, so the test cookie is not Secure, exactly like the session cookie would be.
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.ETag!.IsWeak);
        Assert.Equal(body["tag"]!.GetValue<string>(), response.Headers.ETag.ToString());
        // The same policy every /api response has, which is what a document is sent with: a proxy that ignores no-transform shows here.
        Assert.Equal("no-store, no-transform", response.Headers.CacheControl!.ToString());
        Assert.False(body["cookieReceived"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Under_force_https_the_test_cookie_is_secure_and_host_prefixed_and_the_second_call_clears_it()
    {
        using var factory = new ConfiguredServerFactory("server:\n  forceHttps: true\n");
        SimBrowser browser = await SimBrowser.SignedIn(factory, pageIsHttps: true);

        (HttpResponseMessage first, _) = await browser.Get(SelfCheckEndpoints.EchoPath + "?step=1");
        (HttpResponseMessage second, string raw) = await browser.Get(SelfCheckEndpoints.EchoPath + "?step=2");

        string set = Assert.Single(first.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-ds_selfcheck=", StringComparison.Ordinal));
        Assert.Contains("secure", set, StringComparison.OrdinalIgnoreCase);
        Assert.True(JsonNode.Parse(raw)!["cookieReceived"]!.GetValue<bool>());
        string cleared = Assert.Single(second.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-ds_selfcheck=", StringComparison.Ordinal));
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    private static JsonNode Item(JsonNode body, string id)
    {
        return body["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == id)!;
    }

    private static DelegatingHandler Rewrite(Action<HttpResponseMessage> change)
    {
        return new RewriteHandler(change);
    }

    /// <summary>One thing a proxy does to every response that passes through it.</summary>
    private sealed class RewriteHandler(Action<HttpResponseMessage> change) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            // The proxy leaves the application's own responses to the sign-in and status calls alone: it is the echo it interferes with.
            if (request.RequestUri!.AbsolutePath.StartsWith(SelfCheckEndpoints.EchoPath, StringComparison.Ordinal))
            {
                change(response);
            }

            return response;
        }
    }

    /// <summary>
    /// What the Connection check page does, with a browser's cookie rules: the two echo calls, the browser's own clock, then handing what it
    /// saw to the server to be judged. The administrator's session and CSRF token are seeded as though they came from an earlier sign-in
    /// over https, so a Secure cookie that this plain-http test server sets afterwards is dropped exactly as a real browser would drop it.
    /// </summary>
    private sealed class SimBrowser
    {
        private readonly HttpClient _client;
        private readonly bool _pageIsHttps;
        private readonly Dictionary<string, string> _jar = new(StringComparer.Ordinal);
        private string? _xsrf;

        private SimBrowser(HttpClient client, bool pageIsHttps)
        {
            _client = client;
            _pageIsHttps = pageIsHttps;
        }

        public string? AcceptEncoding { get; set; }

        public IReadOnlyDictionary<string, string> LastSummaries { get; private set; } = new Dictionary<string, string>();

        public static async Task<SimBrowser> SignedIn(DraughtsmanServerFactory factory, bool pageIsHttps, params DelegatingHandler[] proxies)
        {
            // A client with no cookie handling of its own, so what a cookie does is decided here and not by the framework.
            using HttpClient raw = factory.CreateDefaultClient();
            string code = factory.Services.GetRequiredService<SetupCodeGate>().Issue();
            HttpResponseMessage signIn = await raw.PostAsJsonAsync("/api/auth/first-run", new { email = AuthTestHelpers.DefaultEmail, displayName = "local", password = AuthTestHelpers.DefaultPassword, setupCode = code });
            if (signIn.StatusCode == HttpStatusCode.Conflict)
            {
                signIn = await raw.PostAsJsonAsync("/api/auth/login", new { email = AuthTestHelpers.DefaultEmail, password = AuthTestHelpers.DefaultPassword });
            }

            signIn.EnsureSuccessStatusCode();
            var browser = new SimBrowser(factory.CreateDefaultClient(proxies), pageIsHttps);
            foreach (string header in signIn.Headers.GetValues("Set-Cookie"))
            {
                (string name, string value) = Pair(header);
                if (name == "XSRF-TOKEN")
                {
                    browser._xsrf = value;
                }

                // The session, the framework's antiforgery cookie and the rest: everything the sign-in gave, kept as it came.
                browser._jar[name] = value;
            }

            return browser;
        }

        public async Task<(HttpResponseMessage Response, string Body)> Get(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            return await Send(request);
        }

        public async Task<JsonNode> GetJson(string path)
        {
            (_, string body) = await Get(path);
            return JsonNode.Parse(body)!;
        }

        /// <summary>The page's round trip. <paramref name="clientClockOffset"/> is how far ahead of the server the browser's clock is.</summary>
        public async Task<IReadOnlyDictionary<string, string>> RoundTrip(TimeSpan clientClockOffset = default)
        {
            long before = DateTimeOffset.UtcNow.Add(clientClockOffset).ToUnixTimeMilliseconds();
            (HttpResponseMessage r1, string b1) = await Get(SelfCheckEndpoints.EchoPath + "?step=1");
            long after = DateTimeOffset.UtcNow.Add(clientClockOffset).ToUnixTimeMilliseconds();
            (HttpResponseMessage r2, string b2) = await Get(SelfCheckEndpoints.EchoPath + "?step=2");
            RoundTripStep first = StepOf(r1, b1);
            RoundTripStep second = StepOf(r2, b2);
            var observation = new RoundTripObservation(_pageIsHttps ? "https" : "http", first, second, (before + after) / 2);

            using var post = new HttpRequestMessage(HttpMethod.Post, "/api/admin/selfcheck/roundtrip") { Content = JsonContent.Create(observation, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
            (_, string judged) = await Send(post);
            JsonArray items = JsonNode.Parse(judged)!["items"]!.AsArray();
            LastSummaries = items.ToDictionary(i => i!["id"]!.GetValue<string>(), i => i!["summary"]!.GetValue<string>());
            return items.ToDictionary(i => i!["id"]!.GetValue<string>(), i => i!["status"]!.GetValue<string>());
        }

        private static RoundTripStep StepOf(HttpResponseMessage response, string body)
        {
            JsonNode json = JsonNode.Parse(body)!;
            return new RoundTripStep(
                (int)response.StatusCode,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.ContentEncoding.FirstOrDefault(),
                json["tag"]!.GetValue<string>(),
                json["cookieReceived"]!.GetValue<bool>(),
                json["cookieWasSecure"]!.GetValue<bool>(),
                json["serverTime"]!.GetValue<DateTimeOffset>());
        }

        private async Task<(HttpResponseMessage Response, string Body)> Send(HttpRequestMessage request)
        {
            if (_jar.Count > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _jar.Select(kv => kv.Key + "=" + kv.Value)));
            }

            if (_xsrf is not null && request.Method != HttpMethod.Get)
            {
                request.Headers.TryAddWithoutValidation(AuthTestHelpers.XsrfHeaderName, _xsrf);
            }

            if (AcceptEncoding is not null)
            {
                request.Headers.TryAddWithoutValidation("Accept-Encoding", AcceptEncoding);
            }

            HttpResponseMessage response = await _client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
            {
                foreach (string header in cookies)
                {
                    Store(header);
                }
            }

            return (response, body);
        }

        /// <summary>A browser keeps a cookie it is given, unless it is expired, or Secure and arrived on a page that is not https, or __Host- without being Secure.</summary>
        private void Store(string header)
        {
            (string name, string value) = Pair(header);
            string[] attributes = [.. header.Split(';').Skip(1).Select(a => a.Trim().ToLowerInvariant())];
            bool secure = attributes.Contains("secure");
            if (secure && !_pageIsHttps)
            {
                return;
            }

            if (name.StartsWith("__Host-", StringComparison.Ordinal) && !secure)
            {
                return;
            }

            if (attributes.Any(a => a.StartsWith("expires=", StringComparison.Ordinal) && a.Contains("1970", StringComparison.Ordinal)) || value.Length == 0)
            {
                _jar.Remove(name);
                return;
            }

            _jar[name] = value;
        }

        private static (string Name, string Value) Pair(string setCookie)
        {
            string first = setCookie.Split(';')[0];
            int eq = first.IndexOf('=', StringComparison.Ordinal);
            return (first[..eq], first[(eq + 1)..]);
        }
    }
}
