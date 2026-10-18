using System.Net;
using System.Net.Sockets;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Theming.Extraction;
using Microsoft.AspNetCore.Http;

namespace Draughtsman.Server.Tests.ThemeColourExtraction;

/// <summary>
/// The rules of the opt-in "From a website" fetch (docs/admin-guide.md), one test each, and each fails when its rule is removed:
/// off by default, https only, blocked addresses (literal, numeric and bracketed forms, and names that resolve to them, every
/// answer judged), redirects to another host refused, the size cap across all resources, the time cap, text/html and text/css only,
/// the allow-list, no credentials or cookies, at most 8 stylesheets. Addresses are judged in the connect step, which these tests
/// reach through a fake network that records what it was asked to resolve and connect to; real traffic stays on 127.0.0.1.
/// </summary>
public class ThemePageFetcherTests
{
    private const string Page = """
        <!doctype html><html><head><meta name="theme-color" content="#0f766e">
        <link rel="stylesheet" href="/site.css">
        <style>body { background: #ffffff; color: #1c1917 }</style></head><body></body></html>
        """;

    private sealed class FakeNetwork : IOutboundNetwork
    {
        private readonly Dictionary<string, IPAddress[]> _answers = new(StringComparer.Ordinal);
        private readonly List<string> _resolved = [];
        private readonly List<(IPAddress Address, int Port)> _connected = [];

        public IReadOnlyList<string> Resolved => _resolved;

        public IReadOnlyList<(IPAddress Address, int Port)> Connected => _connected;

        public FakeNetwork Answer(string host, params string[] addresses)
        {
            _answers[host] = [.. addresses.Select(IPAddress.Parse)];
            return this;
        }

        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            _resolved.Add(host);
            return ValueTask.FromResult<IReadOnlyList<IPAddress>>(_answers.TryGetValue(host, out IPAddress[]? found) ? found : []);
        }

        public ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
        {
            _connected.Add((address, port));
            throw new SocketException((int)SocketError.ConnectionRefused);
        }
    }

    private static ThemePageFetcher Fetcher(bool enabled = true, string[]? allowed = null, IOutboundNetwork? network = null, ThemeFetchLimits? limits = null)
    {
        return new ThemePageFetcher(new ThemeUrlFetchConfig { Enabled = enabled, AllowedHosts = [.. allowed ?? []] }, network, limits);
    }

    private static async Task<ThemeFetchException> Refused(ThemePageFetcher fetcher, string url)
    {
        return await Assert.ThrowsAsync<ThemeFetchException>(() => fetcher.FetchAsync(url, CancellationToken.None));
    }

    [Fact]
    public async Task The_feature_is_disabled_by_default_and_a_disabled_fetcher_touches_no_network()
    {
        Assert.False(new ThemeUrlFetchConfig().Enabled);
        Assert.Empty(new ThemeUrlFetchConfig().AllowedHosts);

        var network = new FakeNetwork().Answer("example.com", "93.184.216.34");
        ThemeFetchException refused = await Refused(Fetcher(enabled: false, network: network), "https://example.com/");

        Assert.Equal(FetchRefusal.Disabled, refused.Reason);
        Assert.Empty(network.Resolved);
        Assert.Empty(network.Connected);
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("http://93.184.216.34/")]
    [InlineData("http://localhost.example.com/")]
    [InlineData("HTTP://EXAMPLE.COM/")]
    public async Task Plain_http_is_refused_unless_the_host_is_this_machine_and_nothing_is_resolved_first(string url)
    {
        var network = new FakeNetwork().Answer("example.com", "93.184.216.34");
        ThemeFetchException refused = await Refused(Fetcher(network: network), url);

        Assert.Equal(FetchRefusal.SchemeNotAllowed, refused.Reason);
        Assert.Empty(network.Resolved);
        Assert.Empty(network.Connected);
    }

    [Theory]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com/")]
    public async Task Other_schemes_are_refused(string url)
    {
        Assert.Equal(FetchRefusal.SchemeNotAllowed, (await Refused(Fetcher(), url)).Reason);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@example.com/")]
    [InlineData("https://user@example.com/")]
    [InlineData("https://foo.123/")]
    public async Task Something_that_is_not_an_address_or_carries_credentials_is_refused_as_invalid(string url)
    {
        Assert.Equal(FetchRefusal.InvalidUrl, (await Refused(Fetcher(), url)).Reason);
    }

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[fd00:ec2::254]/")]
    [InlineData("https://[::ffff:169.254.169.254]/")]
    [InlineData("https://2852039166/")] // decimal
    [InlineData("https://0xa9fea9fe/")] // hex
    [InlineData("https://0251.0376.0251.0376/")] // octal
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://127.1/")]
    [InlineData("https://2130706433/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://[::ffff:127.0.0.1]/")]
    [InlineData("https://10.0.0.5/")]
    [InlineData("https://192.168.1.1/")]
    [InlineData("https://172.16.0.1/")]
    [InlineData("https://0.0.0.0/")]
    [InlineData("https://[fe80::1]/")]
    public async Task Internal_addresses_in_every_spelling_are_refused_before_any_connection(string url)
    {
        var network = new FakeNetwork();
        ThemeFetchException refused = await Refused(Fetcher(network: network), url);

        Assert.Equal(FetchRefusal.BlockedAddress, refused.Reason);
        Assert.Empty(network.Resolved);
        Assert.Empty(network.Connected);
        Assert.DoesNotContain("169", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ThemeFetchException.MessageFor(FetchRefusal.BlockedAddress), refused.Message);
    }

    [Fact]
    public async Task A_host_name_that_resolves_to_an_internal_address_is_refused_and_so_is_one_with_a_single_bad_answer()
    {
        var network = new FakeNetwork()
            .Answer("intranet.example.test", "10.1.2.3")
            .Answer("metadata.example.test", "169.254.169.254")
            .Answer("rebind.example.test", "93.184.216.34", "127.0.0.1")
            .Answer("v6.example.test", "fd00:ec2::254")
            .Answer("localhost", "127.0.0.1");
        ThemePageFetcher fetcher = Fetcher(network: network);

        foreach (string host in new[] { "intranet.example.test", "metadata.example.test", "rebind.example.test", "v6.example.test", "localhost" })
        {
            ThemeFetchException refused = await Refused(fetcher, $"https://{host}/");
            Assert.Equal(FetchRefusal.BlockedAddress, refused.Reason);
        }

        Assert.Empty(network.Connected); // refused in the connect step: no connection was ever attempted
        Assert.Equal(5, network.Resolved.Count);
    }

    [Fact]
    public async Task A_host_that_resolves_to_a_public_address_is_connected_to_by_that_exact_address_after_one_lookup()
    {
        var network = new FakeNetwork().Answer("example.test", "93.184.216.34");

        ThemeFetchException failed = await Refused(Fetcher(network: network), "https://example.test:8443/");

        // The fake refuses the connection, so this is the "could not fetch" outcome, but it got that far: the address that was judged is
        // the address that was dialled, with the request's port, and the name was looked up once, not again between check and connect.
        Assert.Equal(FetchRefusal.Unreachable, failed.Reason);
        Assert.Equal([("example.test")], network.Resolved);
        Assert.Equal([(IPAddress.Parse("93.184.216.34"), 8443)], network.Connected);
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_the_generic_unreachable_refusal()
    {
        var network = new FakeNetwork();

        ThemeFetchException refused = await Refused(Fetcher(network: network), "https://no-such-host.example.test/");

        Assert.Equal(FetchRefusal.Unreachable, refused.Reason);
        Assert.Empty(network.Connected);
        Assert.DoesNotContain("no-such-host", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_and_its_stylesheet_and_inline_style_are_read_and_the_theme_color_is_kept()
    {
        await using LoopbackSite site = await LoopbackSite.Start(context => context.Request.Path == "/site.css"
            ? LoopbackSite.Css(context, "a { color: #0f766e } .btn { background: #0f766e; color: #fff }")
            : LoopbackSite.Html(context, Page));

        FetchedPage page = await Fetcher(allowed: ["127.0.0.1"]).FetchAsync(site.Origin + "/", CancellationToken.None);

        Assert.Equal("127.0.0.1", page.Host);
        Assert.Equal(1, page.StylesheetsRead);
        Assert.Equal(0, page.StylesheetsSkipped);
        Assert.Equal(["#0f766e"], page.Source.ThemeColours);
        Assert.Equal(2, page.Source.Sheets.Count);
        Assert.Contains(page.Source.Sheets, s => s.Contains("body { background: #ffffff", StringComparison.Ordinal));
        Assert.Contains(page.Source.Sheets, s => s.Contains(".btn { background: #0f766e", StringComparison.Ordinal));
        Assert.Equal(["/", "/site.css"], site.Requests.Select(r => r.Path).ToArray());
    }

    [Fact]
    public async Task Loopback_is_blocked_by_default_and_reachable_only_when_the_operator_lists_the_host()
    {
        await using LoopbackSite site = await LoopbackSite.Start(context => LoopbackSite.Html(context, Page));

        ThemeFetchException refused = await Refused(Fetcher(), site.Origin + "/");
        Assert.Equal(FetchRefusal.BlockedAddress, refused.Reason);
        Assert.Empty(site.Requests); // nothing reached the site

        Assert.Equal(FetchRefusal.BlockedAddress, (await Refused(Fetcher(allowed: ["localhost", "10.0.0.5"]), site.Origin + "/")).Reason); // another host listed: no help
        Assert.Empty(site.Requests);

        FetchedPage page = await Fetcher(allowed: ["127.0.0.1"]).FetchAsync(site.Origin + "/", CancellationToken.None);
        Assert.NotEmpty(page.Source.Sheets);
        Assert.NotEmpty(site.Requests);
    }

    [Fact]
    public async Task A_listed_name_is_allowed_whatever_it_resolves_to_but_only_that_name()
    {
        var network = new FakeNetwork().Answer("wiki.corp.test", "10.9.8.7").Answer("other.corp.test", "10.9.8.7");

        ThemeFetchException listed = await Refused(Fetcher(allowed: ["wiki.corp.test"], network: network), "https://wiki.corp.test/");
        Assert.Equal(FetchRefusal.Unreachable, listed.Reason); // passed the address rule: the fake then refused the connection
        Assert.Equal([(IPAddress.Parse("10.9.8.7"), 443)], network.Connected);

        ThemeFetchException unlisted = await Refused(Fetcher(allowed: ["wiki.corp.test"], network: network), "https://other.corp.test/");
        Assert.Equal(FetchRefusal.BlockedAddress, unlisted.Reason);
        Assert.Single(network.Connected);
    }

    [Fact]
    public async Task A_redirect_to_the_same_host_is_followed_and_one_to_another_host_is_refused()
    {
        await using LoopbackSite site = await LoopbackSite.Start(context =>
        {
            switch (context.Request.Path.Value)
            {
                case "/same":
                    context.Response.Redirect("/final");
                    return Task.CompletedTask;
                case "/final":
                    return LoopbackSite.Html(context, "<style>body{background:#101820;color:#f2f2f2}</style>");
                case "/other":
                    context.Response.Redirect($"http://localhost:{context.Connection.LocalPort}/final"); // same machine, different host name
                    return Task.CompletedTask;
                case "/loop":
                    context.Response.Redirect("/loop");
                    return Task.CompletedTask;
                default:
                    return LoopbackSite.Html(context, "<style>body{}</style>");
            }
        });
        ThemePageFetcher fetcher = Fetcher(allowed: ["127.0.0.1", "localhost"]);

        FetchedPage followed = await fetcher.FetchAsync(site.Origin + "/same", CancellationToken.None);
        Assert.Contains(followed.Source.Sheets, s => s.Contains("#101820", StringComparison.Ordinal));

        ThemeFetchException refused = await Refused(fetcher, site.Origin + "/other");
        Assert.Equal(FetchRefusal.RedirectRefused, refused.Reason);
        Assert.DoesNotContain(site.Requests, r => r.Path == "/final" && r.Headers.TryGetValue("Host", out string? host) && host.StartsWith("localhost", StringComparison.Ordinal));

        Assert.Equal(FetchRefusal.RedirectRefused, (await Refused(fetcher, site.Origin + "/loop")).Reason);
    }

    [Fact]
    public async Task A_redirect_to_a_blocked_address_is_refused_even_when_it_looks_like_the_same_site()
    {
        await using LoopbackSite site = await LoopbackSite.Start(context =>
        {
            context.Response.Redirect("https://169.254.169.254/latest/meta-data/");
            return Task.CompletedTask;
        });

        ThemeFetchException refused = await Refused(Fetcher(allowed: ["127.0.0.1"]), site.Origin + "/");

        Assert.Equal(FetchRefusal.RedirectRefused, refused.Reason);
        Assert.Single(site.Requests);
    }

    [Fact]
    public async Task A_page_over_the_size_cap_is_refused_whether_it_declares_its_size_or_streams_without_one()
    {
        string big = new('a', 3 * 1024 * 1024);
        await using LoopbackSite site = await LoopbackSite.Start(async context =>
        {
            if (context.Request.Path == "/declared-and-silent")
            {
                // Headers promise 3 MiB and the body never comes: only the declared size can refuse this before the time limit does.
                context.Response.ContentType = "text/html";
                context.Response.ContentLength = big.Length;
                await context.Response.Body.FlushAsync();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            else if (context.Request.Path == "/declared")
            {
                context.Response.ContentType = "text/html";
                context.Response.ContentLength = big.Length;
                await context.Response.WriteAsync(big);
            }
            else
            {
                context.Response.ContentType = "text/html"; // chunked: no Content-Length to check, so the count has to
                for (int i = 0; i < 3 * 64; i++)
                {
                    await context.Response.WriteAsync(big[..16384]);
                }
            }
        });
        ThemePageFetcher fetcher = Fetcher(allowed: ["127.0.0.1"]);

        Assert.Equal(FetchRefusal.TooLarge, (await Refused(fetcher, site.Origin + "/declared")).Reason);
        Assert.Equal(FetchRefusal.TooLarge, (await Refused(Fetcher(allowed: ["127.0.0.1"], limits: new ThemeFetchLimits { Timeout = TimeSpan.FromSeconds(30) }), site.Origin + "/declared-and-silent")).Reason);
        Assert.Equal(FetchRefusal.TooLarge, (await Refused(fetcher, site.Origin + "/streamed")).Reason);
    }

    [Fact]
    public async Task The_size_cap_is_across_every_resource_so_a_sheet_that_does_not_fit_is_left_out_not_the_page()
    {
        string sheet = "/* " + new string('x', 70_000) + " */ a { color: #ff0000 }";
        await using LoopbackSite site = await LoopbackSite.Start(context => context.Request.Path.Value!.StartsWith("/s", StringComparison.Ordinal)
            ? LoopbackSite.Css(context, sheet)
            : LoopbackSite.Html(context, "<link rel=stylesheet href=/s1.css><link rel=stylesheet href=/s2.css><link rel=stylesheet href=/s3.css><style>body{background:#fff;color:#111}</style>"));
        // 100 KB for everything: the page and the first sheet fit (about 70 KB), the second does not.
        ThemePageFetcher fetcher = Fetcher(allowed: ["127.0.0.1"], limits: new ThemeFetchLimits { MaxBytes = 100_000 });

        FetchedPage page = await fetcher.FetchAsync(site.Origin + "/", CancellationToken.None);

        Assert.Equal(1, page.StylesheetsRead);
        Assert.Equal(2, page.StylesheetsSkipped);
        Assert.Equal(2, page.Source.Sheets.Count); // the inline style and the one sheet that fit
    }

    [Fact]
    public async Task A_page_that_never_answers_times_out_within_the_limit_and_a_caller_who_cancels_is_not_told_it_timed_out()
    {
        await using LoopbackSite site = await LoopbackSite.Start(context => Task.Delay(Timeout.Infinite, context.RequestAborted));
        ThemePageFetcher fetcher = Fetcher(allowed: ["127.0.0.1"], limits: new ThemeFetchLimits { Timeout = TimeSpan.FromMilliseconds(400) });

        ThemeFetchException refused = await Refused(fetcher, site.Origin + "/");
        Assert.Equal(FetchRefusal.TimedOut, refused.Reason);

        using var cancel = new CancellationTokenSource();
        Task<FetchedPage> running = Fetcher(allowed: ["127.0.0.1"]).FetchAsync(site.Origin + "/", cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task A_page_that_drips_its_body_slowly_is_cut_off_by_the_same_total_time_limit()
    {
        await using LoopbackSite site = await LoopbackSite.Start(async context =>
        {
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync("<style>");
            await context.Response.Body.FlushAsync();
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
        });

        ThemeFetchException refused = await Refused(Fetcher(allowed: ["127.0.0.1"], limits: new ThemeFetchLimits { Timeout = TimeSpan.FromMilliseconds(400) }), site.Origin + "/");

        Assert.Equal(FetchRefusal.TimedOut, refused.Reason);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData("image/png")]
    [InlineData("text/css")] // the right type for a sheet, the wrong one for the page
    public async Task Only_text_html_is_accepted_for_the_page(string contentType)
    {
        await using LoopbackSite site = await LoopbackSite.Start(context =>
        {
            context.Response.ContentType = contentType;
            return context.Response.WriteAsync("body { background: #fff }");
        });

        ThemeFetchException refused = await Refused(Fetcher(allowed: ["127.0.0.1"]), site.Origin + "/");

        Assert.Equal(FetchRefusal.NotCss, refused.Reason);
    }

    [Fact]
    public async Task A_stylesheet_of_the_wrong_type_is_left_out_and_never_read()
    {
        await using LoopbackSite site = await LoopbackSite.Start(context => context.Request.Path.Value switch
        {
            "/ok.css" => LoopbackSite.Css(context, "a { color: #123456 }"),
            "/script.css" => Task.Run(() => { context.Response.ContentType = "text/plain"; return context.Response.WriteAsync("a { color: #abcdef }"); }),
            _ => LoopbackSite.Html(context, "<link rel=stylesheet href=/script.css><link rel=stylesheet href=/ok.css>"),
        });

        FetchedPage page = await Fetcher(allowed: ["127.0.0.1"]).FetchAsync(site.Origin + "/", CancellationToken.None);

        Assert.Equal(1, page.StylesheetsRead);
        Assert.Equal(1, page.StylesheetsSkipped);
        Assert.DoesNotContain(page.Source.Sheets, s => s.Contains("#abcdef", StringComparison.Ordinal));
        Assert.Contains(page.Source.Sheets, s => s.Contains("#123456", StringComparison.Ordinal));
    }

    [Fact]
    public async Task At_most_eight_stylesheets_are_fetched_and_import_rules_are_never_followed()
    {
        string links = string.Concat(Enumerable.Range(1, 12).Select(i => $"<link rel=stylesheet href=/s{i}.css>"));
        await using LoopbackSite site = await LoopbackSite.Start(context => context.Request.Path.Value!.EndsWith(".css", StringComparison.Ordinal)
            ? LoopbackSite.Css(context, "@import url(/imported.css); a { color: #123456 }")
            : LoopbackSite.Html(context, links));

        FetchedPage page = await Fetcher(allowed: ["127.0.0.1"]).FetchAsync(site.Origin + "/", CancellationToken.None);

        Assert.Equal(8, page.StylesheetsRead);
        Assert.Equal(4, page.StylesheetsSkipped);
        Assert.Equal(9, site.Requests.Count); // the page and eight sheets
        Assert.DoesNotContain(site.Requests, r => r.Path == "/imported.css");
        Assert.DoesNotContain(site.Requests, r => r.Path is "/s9.css" or "/s12.css");
    }

    [Fact]
    public async Task No_credentials_are_sent_and_no_cookie_is_kept_or_replayed()
    {
        await using LoopbackSite site = await LoopbackSite.Start(context =>
        {
            context.Response.Headers.SetCookie = "session=abc123; Path=/";
            return context.Request.Path == "/site.css" ? LoopbackSite.Css(context, "a { color: #123456 }") : LoopbackSite.Html(context, "<link rel=stylesheet href=/site.css><style>body{background:#fff;color:#111}</style>");
        });

        await Fetcher(allowed: ["127.0.0.1"]).FetchAsync(site.Origin + "/", CancellationToken.None);

        Assert.Equal(2, site.Requests.Count);
        Assert.All(site.Requests, request =>
        {
            Assert.DoesNotContain("Cookie", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Referer", request.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.StartsWith("Draughtsman-theme-reader", request.Headers["User-Agent"], StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_non_success_answer_is_the_generic_refusal_and_echoes_nothing()
    {
        await using LoopbackSite site = await LoopbackSite.Start(context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return context.Response.WriteAsync("secret stack trace at Internal.Class.Method");
        });

        ThemeFetchException refused = await Refused(Fetcher(allowed: ["127.0.0.1"]), site.Origin + "/");

        Assert.Equal(FetchRefusal.Unreachable, refused.Reason);
        Assert.DoesNotContain("stack", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("500", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_names_the_host_only_never_the_path_query_or_credentials()
    {
        Assert.Equal("example.com", ThemePageFetcher.HostForLog("https://user:pw@Example.com:8443/secret/path?token=abc#frag"));
        Assert.Equal("10.0.0.1", ThemePageFetcher.HostForLog("https://10.0.0.1/x"));
        Assert.Null(ThemePageFetcher.HostForLog("not a url"));
        Assert.Null(ThemePageFetcher.HostForLog("ftp://example.com/"));
        Assert.Null(ThemePageFetcher.HostForLog("https://foo.123/"));
    }
}
