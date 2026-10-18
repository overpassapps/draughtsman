using Draughtsman.Server.Diagnostics;

namespace Draughtsman.Server.Tests;

/// <summary>The browser-side checks, judged by the server: each of the four findings in both directions, from the observations a page would send.</summary>
public class RoundTripChecksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static RoundTripStep Good(int step, bool cookie = false, string tag = "\"sc-aaaa\"", DateTimeOffset? serverTime = null)
    {
        return new RoundTripStep(200, tag, null, tag, step == 2 ? cookie : false, true, serverTime ?? Now);
    }

    private static RoundTripObservation Observe(RoundTripStep first, RoundTripStep? second, string pageScheme = "https", DateTimeOffset? clientTime = null)
    {
        return new RoundTripObservation(pageScheme, first, second, (clientTime ?? Now).ToUnixTimeMilliseconds());
    }

    private static DiagnosticItem Item(RoundTripObservation observation, string id)
    {
        return RoundTripChecks.Evaluate(observation).Single(i => i.Id == id);
    }

    [Fact]
    public void A_clean_round_trip_passes_all_four()
    {
        IReadOnlyList<DiagnosticItem> items = RoundTripChecks.Evaluate(Observe(Good(1), Good(2, cookie: true)));

        Assert.Equal(["cookie", "etag", "encoding", "clock"], items.Select(i => i.Id));
        Assert.All(items, i => Assert.Equal(DiagnosticStatus.Pass, i.Status));
    }

    // ------------------------------------------------------------------------------------------------------ cookie

    [Fact]
    public void A_cookie_that_does_not_come_back_fails()
    {
        DiagnosticItem item = Item(Observe(Good(1) with { CookieWasSecure = false }, Good(2, cookie: false)), "cookie");

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Contains("did not send the test cookie back", item.Summary, StringComparison.Ordinal);
        Assert.Equal("no", item.Facts!["Cookie came back on the second call"]);
        Assert.Contains("Set-Cookie", item.Fix!.Nginx, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secure_cookie_on_an_http_page_is_named_as_the_reason_it_was_dropped()
    {
        DiagnosticItem item = Item(Observe(Good(1) with { CookieWasSecure = true }, Good(2, cookie: false), pageScheme: "http"), "cookie");

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Contains("marked Secure, but you are on plain http://", item.Summary, StringComparison.Ordinal);
        Assert.Contains("forceHttps: false", item.Fix!.Yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_clock_thirty_days_behind_makes_the_cookie_born_expired_and_says_so()
    {
        DateTimeOffset serverBehind = Now - TimeSpan.FromDays(45);
        DiagnosticItem item = Item(Observe(Good(1, serverTime: serverBehind) with { CookieWasSecure = true }, Good(2, cookie: false)), "cookie");

        Assert.Equal(DiagnosticStatus.Fail, item.Status);
        Assert.Contains("45 days behind", item.Summary, StringComparison.Ordinal);
        Assert.Contains("expired the moment it is set", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_round_trip_whose_second_call_never_happened_is_a_warning_not_a_pass()
    {
        DiagnosticItem item = Item(Observe(Good(1), null), "cookie");

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
    }

    // ------------------------------------------------------------------------------------------------------ etag

    [Fact]
    public void A_weakened_etag_warns_and_names_the_failed_saves_of_issue_63()
    {
        RoundTripStep weakened = Good(1) with { ETagHeader = "W/\"sc-aaaa\"" };
        DiagnosticItem item = Item(Observe(weakened, Good(2, cookie: true) with { ETagHeader = "W/\"sc-aaaa\"" }), "etag");

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
        Assert.Contains("arrived weakened", item.Summary, StringComparison.Ordinal);
        Assert.Contains("issue 63", item.Summary, StringComparison.Ordinal);
        Assert.Contains("400 Invalid If-Match header", item.Summary, StringComparison.Ordinal);
        Assert.Contains("gzip off", item.Fix!.Nginx, StringComparison.Ordinal);
        Assert.Contains("Respect Strong ETags", item.Fix.Cloudflare, StringComparison.Ordinal);
        Assert.Contains("not tested", item.Fix.Cloudflare, StringComparison.Ordinal);
        Assert.Contains("do not recommend", item.Fix.Cloudflare, StringComparison.Ordinal);
        Assert.Contains("nothing needs fixing for saves", item.Summary + " " + string.Join(' ', item.Detail ?? []), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("handle /api/*", item.Fix.Caddy, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stripped_etag_warns()
    {
        DiagnosticItem item = Item(Observe(Good(1) with { ETagHeader = null }, Good(2, cookie: true)), "etag");

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
        Assert.Contains("never reached the browser", item.Summary, StringComparison.Ordinal);
        Assert.Equal("(none)", item.Facts!["ETag header the browser received"]);
    }

    [Fact]
    public void An_etag_that_arrives_as_a_different_tag_warns()
    {
        DiagnosticItem item = Item(Observe(Good(1) with { ETagHeader = "\"other\"" }, Good(2, cookie: true)), "etag");

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
        Assert.Contains("not as the", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_weakening_that_only_shows_on_the_second_call_is_still_found()
    {
        DiagnosticItem item = Item(Observe(Good(1), Good(2, cookie: true) with { ETagHeader = "W/\"sc-aaaa\"" }), "etag");

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
    }

    // ------------------------------------------------------------------------------------------------------ encoding

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("zstd")]
    public void A_content_encoding_the_server_did_not_add_warns(string encoding)
    {
        DiagnosticItem item = Item(Observe(Good(1) with { ContentEncoding = encoding }, Good(2, cookie: true)), "encoding");

        Assert.Equal(DiagnosticStatus.Warn, item.Status);
        Assert.Contains(encoding, item.Summary, StringComparison.Ordinal);
        Assert.Contains("did not add", item.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("identity")]
    public void No_content_encoding_passes(string? encoding)
    {
        DiagnosticItem item = Item(Observe(Good(1) with { ContentEncoding = encoding }, Good(2, cookie: true)), "encoding");

        Assert.Equal(DiagnosticStatus.Pass, item.Status);
    }

    // ------------------------------------------------------------------------------------------------------ clock

    [Theory]
    [InlineData(0, DiagnosticStatus.Pass)]
    [InlineData(100, DiagnosticStatus.Pass)]
    [InlineData(-110, DiagnosticStatus.Pass)]
    [InlineData(150, DiagnosticStatus.Warn)]
    [InlineData(-150, DiagnosticStatus.Warn)]
    [InlineData(3600 * 5, DiagnosticStatus.Warn)]
    public void The_clocks_may_differ_by_two_minutes_and_no_more(int serverMinusClientSeconds, DiagnosticStatus expected)
    {
        DiagnosticItem item = Item(Observe(Good(1, serverTime: Now.AddSeconds(serverMinusClientSeconds)), Good(2, cookie: true)), "clock");

        Assert.Equal(expected, item.Status);
    }

    [Fact]
    public void A_server_thirty_one_days_behind_fails_and_a_server_thirty_one_days_ahead_only_warns()
    {
        DiagnosticItem behind = Item(Observe(Good(1, serverTime: Now.AddDays(-31)), Good(2, cookie: true)), "clock");
        DiagnosticItem ahead = Item(Observe(Good(1, serverTime: Now.AddDays(31)), Good(2, cookie: true)), "clock");

        Assert.Equal(DiagnosticStatus.Fail, behind.Status);
        Assert.Contains("31 days behind", behind.Summary, StringComparison.Ordinal);
        Assert.Contains("expired when your browser receives it", behind.Summary, StringComparison.Ordinal);
        Assert.Equal(DiagnosticStatus.Warn, ahead.Status);
        Assert.Contains("the server is ahead", ahead.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreported_times_are_a_warning()
    {
        var observation = new RoundTripObservation("https", Good(1) with { ServerTime = null }, Good(2, cookie: true), ClientTimeMs: null);

        Assert.Equal(DiagnosticStatus.Warn, Item(observation, "clock").Status);
    }

    // ------------------------------------------------------------------------------------------------------ the round trip not running

    [Fact]
    public void A_first_call_that_was_not_answered_with_200_replaces_everything_with_one_failure()
    {
        IReadOnlyList<DiagnosticItem> blocked = RoundTripChecks.Evaluate(Observe(new RoundTripStep(403), null));
        IReadOnlyList<DiagnosticItem> dropped = RoundTripChecks.Evaluate(new RoundTripObservation("https", null, null, null, "TypeError: Failed to fetch"));

        DiagnosticItem one = Assert.Single(blocked);
        Assert.Equal("roundtrip", one.Id);
        Assert.Equal(DiagnosticStatus.Fail, one.Status);
        Assert.Contains("answered 403", one.Summary, StringComparison.Ordinal);
        Assert.Contains("TypeError: Failed to fetch", Assert.Single(dropped).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_scheme_that_is_not_http_or_https_is_not_repeated()
    {
        DiagnosticItem item = Item(Observe(Good(1), Good(2, cookie: true), pageScheme: "<script>alert(1)</script>"), "cookie");

        Assert.Equal("(not reported)", item.Facts!["Scheme of the address in your browser"]);
    }

    [Fact]
    public void Text_from_the_browser_is_cleaned_before_it_is_shown()
    {
        string hostile = "gzip\r\n<script>" + new string('x', 500);
        DiagnosticItem item = Item(Observe(Good(1) with { ContentEncoding = hostile }, Good(2, cookie: true)), "encoding");

        Assert.DoesNotContain('\r', item.Summary);
        Assert.DoesNotContain('\n', item.Summary);
        Assert.True(item.Summary.Length < 900, "a long header value must be capped");
    }
}
