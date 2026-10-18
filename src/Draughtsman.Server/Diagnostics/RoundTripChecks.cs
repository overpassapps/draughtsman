using System.Globalization;

namespace Draughtsman.Server.Diagnostics;

/// <summary>
/// What the browser saw of one call to the self-check echo: the response's own <c>ETag</c> and <c>Content-Encoding</c> headers (which only a
/// browser can read after a proxy has had its way with them) and what the echo's body said. Every member is optional: a failed call is
/// reported as what is missing. The names are the wire contract with the page.
/// </summary>
public sealed record RoundTripStep(
    int? Status = null,
    string? ETagHeader = null,
    string? ContentEncoding = null,
    string? BodyTag = null,
    bool? CookieReceived = null,
    bool? CookieWasSecure = null,
    DateTimeOffset? ServerTime = null);

/// <summary>
/// The page's round trip, handed back to the server to judge (<c>POST /api/admin/selfcheck/roundtrip</c>). The judging lives here, in the
/// language the rest of the checks are in, so every rule and every line of fix text is tested in one place, against fake proxies that
/// weaken, strip or compress the echo; the page only runs the calls and draws the answer. <see cref="ClientTimeMs"/> is the browser's clock
/// (milliseconds since 1970) at the middle of the first call.
/// </summary>
public sealed record RoundTripObservation(
    string? PageScheme = null,
    RoundTripStep? First = null,
    RoundTripStep? Second = null,
    long? ClientTimeMs = null,
    string? Failure = null);

/// <summary>The checks that need a browser in the loop: does the session cookie come back, does an ETag survive the proxy, did anything compress the response, and do the two clocks agree.</summary>
public static class RoundTripChecks
{
    public static readonly TimeSpan ClockWarnAfter = TimeSpan.FromMinutes(2);

    /// <summary>The session cookie lives this long, so a server clock this far behind the browser's makes it expired the moment it is set.</summary>
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(30);

    public const int MaxText = 200;

    public static IReadOnlyList<DiagnosticItem> Evaluate(RoundTripObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        RoundTripStep? first = observation.First;
        RoundTripStep? second = observation.Second;
        if (first is null || first.Status is not 200)
        {
            return [Incomplete(observation, first)];
        }

        return
        [
            Cookie(observation, first, second),
            ETag(first, second),
            Encoding(first, second),
            Clock(observation, first),
        ];
    }

    // ------------------------------------------------------------------------------------------------------ the round trip itself

    private static DiagnosticItem Incomplete(RoundTripObservation o, RoundTripStep? first)
    {
        string why = first?.Status is { } status
            ? $"the first call to the echo answered {status.ToString(CultureInfo.InvariantCulture)}, not 200"
            : Clean(o.Failure) ?? "the page could not call the server";
        return DiagnosticItem.Fail(
            "roundtrip",
            "Browser round trip",
            $"The browser round trip could not run: {why}. A proxy, firewall or web application firewall is probably answering for this server on /api/admin/selfcheck/echo, or the connection dropped.",
            new Dictionary<string, string> { ["First call status"] = first?.Status?.ToString(CultureInfo.InvariantCulture) ?? "(no answer)" },
            ["Reload the page and run the check again. If it repeats, look at what sits in front of the server (a CDN challenge page, a WAF rule, a timeout)."]);
    }

    // ------------------------------------------------------------------------------------------------------ cookie

    private static DiagnosticItem Cookie(RoundTripObservation o, RoundTripStep first, RoundTripStep? second)
    {
        const string id = "cookie";
        const string title = "Session cookie round trip";
        var facts = new Dictionary<string, string>
        {
            ["Test cookie set as Secure"] = first.CookieWasSecure is { } secure ? (secure ? "yes" : "no") : "(unknown)",
            ["Scheme of the address in your browser"] = PageSchemeOf(o),
            ["Cookie came back on the second call"] = second?.CookieReceived is { } back ? (back ? "yes" : "no") : "(second call did not run)",
        };

        if (second is null || second.Status is not 200 || second.CookieReceived is null)
        {
            return DiagnosticItem.Warn(id, title, "The second call of the round trip did not complete, so it is not known whether the browser keeps the session cookie.", facts, [Clean(o.Failure) ?? "Run the check again."]);
        }

        if (second.CookieReceived is true)
        {
            return DiagnosticItem.Pass(id, title, "The browser kept a test cookie set with the same attributes as the session cookie and sent it back. Signing in will stick.", facts);
        }

        if (first.CookieWasSecure is true && string.Equals(o.PageScheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticItem.Fail(
                id,
                title,
                "The test cookie was marked Secure, but you are on plain http://, so the browser dropped it. This is the \"Signing in…\" that never finishes: the server accepts the password, sends a Secure session cookie, and the browser throws it away.",
                facts,
                ["Open the https:// address of this instance. If there is no TLS yet, turn server.forceHttps off."],
                new DiagnosticFix(Yaml: "server:\n  forceHttps: false   # only while the site is reached over plain http", Cloudflare: "SSL/TLS > Edge Certificates > Always Use HTTPS: on, so the http:// address is never used."));
        }

        TimeSpan? skew = SkewOf(o, first);
        if (skew is { } behind && behind <= -CookieLifetime)
        {
            return DiagnosticItem.Fail(
                id,
                title,
                $"The browser did not send the test cookie back, and this server's clock is {Days(behind)} days behind yours. The session cookie expires 30 days after the server's time, so it is expired the moment it is set: no sign-in can stick until the server's clock is right.",
                facts,
                ["Set the server's clock (and keep it right with NTP). In a container the clock is the host's."]);
        }

        return DiagnosticItem.Fail(
            id,
            title,
            "The browser did not send the test cookie back. Sign-in would appear to work and then not stick. Something between the server and the browser removed the cookie, or the browser refuses it.",
            facts,
            [
                "A proxy or CDN that strips or rewrites Set-Cookie (check its cookie, header and caching rules for /api/).",
                "A browser setting or extension that blocks cookies for this site.",
                "A Secure cookie over http: if the address starts with http:// while server.forceHttps is on.",
            ],
            new DiagnosticFix(
                Nginx: "# nginx passes Set-Cookie through by default. Look for proxy_hide_header Set-Cookie, proxy_ignore_headers Set-Cookie or proxy_cookie_* lines and remove them for this site.",
                Caddy: "# Caddy passes Set-Cookie through. Look for header -Set-Cookie or header_down -Set-Cookie and remove it.",
                Cloudflare: "Cloudflare does not strip cookies. Check Transform Rules > Modify Response Header for a rule that removes Set-Cookie, and that no Cache Rule caches /api/."));
    }

    // ------------------------------------------------------------------------------------------------------ etag

    private static DiagnosticItem ETag(RoundTripStep first, RoundTripStep? second)
    {
        const string id = "etag";
        const string title = "ETag survives the proxy";
        RoundTripStep[] steps = second is { Status: 200 } ? [first, second] : [first];
        var facts = new Dictionary<string, string>
        {
            ["ETag header the browser received"] = Show(first.ETagHeader),
            ["Tag in the response body"] = Show(first.BodyTag),
        };

        string? problem = null;
        bool weak = false;
        bool stripped = false;
        foreach (RoundTripStep step in steps)
        {
            if (string.IsNullOrEmpty(step.ETagHeader))
            {
                stripped = true;
                problem ??= "the ETag header never reached the browser";
            }
            else if (step.ETagHeader.TrimStart().StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            {
                weak = true;
                problem ??= $"the ETag header arrived weakened ({Clean(step.ETagHeader)}), not as the strong tag the server sent ({Show(step.BodyTag)})";
            }
            else if (!string.Equals(step.ETagHeader.Trim(), step.BodyTag?.Trim(), StringComparison.Ordinal))
            {
                problem ??= $"the ETag header arrived as {Clean(step.ETagHeader)}, not as the {Show(step.BodyTag)} the server sent";
            }
        }

        if (problem is null)
        {
            return DiagnosticItem.Pass(id, title, "The ETag header the browser received is exactly the one the server sent. Saves, which quote a document's ETag back in If-Match, will work.", facts);
        }

        return DiagnosticItem.Warn(
            id,
            title,
            $"A proxy or CDN between the server and your browser changed the ETag: {problem}. Draughtsman uses a document's ETag as its base version. This version accepts a weakened tag, and the editor also sends the version in X-Document-Version, which a proxy does not rewrite, so editing keeps working; scripts and API clients that quote the ETag in If-Match are another matter (a stripped tag leaves them with 428, and an older Draughtsman refuses a weakened one with \"400 Invalid If-Match header\": issue 63). A CDN weakens the ETag when it compresses a response; some strip it.",
            facts,
            [
                weak ? "The W/ prefix is added by whatever compressed the response (Cloudflare, nginx gzip, a WAF or gateway)." : stripped ? "Some gateways remove validators from responses they cache or rewrite." : "Something rewrote the header.",
                "The server logs \"accepted a weakened version tag\" once an hour while this happens.",
                "Nothing needs fixing for saves. If a script of yours quotes the ETag and you would rather it stayed strong, the proxy settings below are optional, for /api/ only.",
            ],
            new DiagnosticFix(
                Nginx: "location /api/ {\n    proxy_pass http://127.0.0.1:5180;   # your Draughtsman address\n    gzip off;                           # nginx weakens an ETag when it compresses the response\n}",
                Caddy: "draughtsman.example.com {\n    handle /api/* {\n        reverse_proxy 127.0.0.1:5180   # no encode here: Caddy's encode weakens ETags\n    }\n    handle {\n        encode zstd gzip\n        reverse_proxy 127.0.0.1:5180\n    }\n}",
                Cloudflare: "Nothing needs changing in Cloudflare for saves to work: this version accepts the weakened tag. no-transform is only a request that some intermediaries, Cloudflare included, ignore, so it does not stop this. In our one test behind Cloudflare Free, switching compression off for the host (a Compression Rule) removed the Content-Encoding but the browser still received the weakened ETag, so we do not recommend it as a fix. \"Respect Strong ETags\" (Caching > Configuration) is Cloudflare's documented setting for keeping strong ETags; we have not tested it."));
    }

    // ------------------------------------------------------------------------------------------------------ encoding

    private static DiagnosticItem Encoding(RoundTripStep first, RoundTripStep? second)
    {
        const string id = "encoding";
        const string title = "Responses are not re-compressed in transit";
        string? seen = new[] { first, second }.Where(s => s is not null).Select(s => s!.ContentEncoding).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e) && !string.Equals(e.Trim(), "identity", StringComparison.OrdinalIgnoreCase));
        var facts = new Dictionary<string, string>
        {
            ["content-encoding the browser received"] = Show(first.ContentEncoding),
            ["content-encoding this server sent"] = "(none: the echo is never compressed by the server)",
        };

        if (seen is null)
        {
            return DiagnosticItem.Pass(id, title, "The response arrived exactly as the server sent it: nothing added a content-encoding on the way.", facts);
        }

        return DiagnosticItem.Warn(
            id,
            title,
            $"The response arrived with content-encoding: {Clean(seen)}, which this server did not add. A proxy or CDN compressed it on the way. That is harmless: nothing needs fixing for saves. It is usually why a CDN weakens the ETag, and this version accepts a weakened ETag (see the ETag check).",
            facts,
            ["Compression in transit does no harm here, whatever the ETag check says."],
            new DiagnosticFix(
                Nginx: "gzip off;   # optional, in the location /api/ block",
                Caddy: "# keep `encode` out of the /api/* handle (see the ETag check)",
                Cloudflare: "Nothing to change: compression in transit is harmless (no-transform is only a request, and Cloudflare ignored it in our test). See the ETag check for what we did and did not find with Cloudflare's settings."));
    }

    // ------------------------------------------------------------------------------------------------------ clock

    private static DiagnosticItem Clock(RoundTripObservation o, RoundTripStep first)
    {
        const string id = "clock";
        const string title = "Server clock and your clock";
        if (SkewOf(o, first) is not { } skew)
        {
            return DiagnosticItem.Warn(id, title, "The two clocks could not be compared: the browser did not report its time or the server did not report its own.");
        }

        TimeSpan size = skew.Duration();
        var facts = new Dictionary<string, string>
        {
            ["Server clock minus your clock"] = Describe(skew),
        };

        if (skew <= -CookieLifetime)
        {
            return DiagnosticItem.Fail(
                id,
                title,
                $"The server's clock is {Days(skew)} days behind yours. The session cookie expires 30 days after the server's time, so it is already expired when your browser receives it: signing in can never stick. Correct the server's clock.",
                facts,
                ["Keep the clock right with NTP. In a container it is the host's clock."]);
        }

        if (size > ClockWarnAfter)
        {
            return DiagnosticItem.Warn(
                id,
                title,
                $"The server's clock and yours differ by {Describe(size)} ({(skew < TimeSpan.Zero ? "the server is behind" : "the server is ahead")}). Token dates, session expiry and log times will all look wrong; a difference of 30 days or more stops sign-in sticking at all.",
                facts,
                ["Keep the server's clock right with NTP (and check yours)."]);
        }

        return DiagnosticItem.Pass(id, title, $"The server's clock and yours differ by {Describe(size)}, which is nothing to worry about.", facts);
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    /// <summary>Server clock minus browser clock, at the moment of the first call; null when either side did not say.</summary>
    private static TimeSpan? SkewOf(RoundTripObservation o, RoundTripStep first)
    {
        if (o.ClientTimeMs is not { } client || first.ServerTime is not { } server)
        {
            return null;
        }

        return server - DateTimeOffset.FromUnixTimeMilliseconds(client);
    }

    private static string Describe(TimeSpan span)
    {
        TimeSpan size = span.Duration();
        string sign = span < TimeSpan.Zero ? "-" : "+";
        if (size.TotalDays >= 2)
        {
            return $"{sign}{Math.Round(size.TotalDays).ToString(CultureInfo.InvariantCulture)} days";
        }

        if (size.TotalHours >= 2)
        {
            return $"{sign}{Math.Round(size.TotalHours).ToString(CultureInfo.InvariantCulture)} hours";
        }

        return size.TotalMinutes >= 2
            ? $"{sign}{Math.Round(size.TotalMinutes).ToString(CultureInfo.InvariantCulture)} minutes"
            : $"{sign}{Math.Round(size.TotalSeconds).ToString(CultureInfo.InvariantCulture)} seconds";
    }

    private static string Days(TimeSpan span)
    {
        return Math.Round(span.Duration().TotalDays).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The scheme the page says it is on: only <c>http</c> or <c>https</c> are believed, and anything else is shown as not reported.</summary>
    private static string PageSchemeOf(RoundTripObservation o)
    {
        return string.Equals(o.PageScheme, "http", StringComparison.OrdinalIgnoreCase) ? "http" : string.Equals(o.PageScheme, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "(not reported)";
    }

    private static string Show(string? value)
    {
        return Clean(value) ?? "(none)";
    }

    /// <summary>Header values and the page's failure text come from the browser: control characters out and capped, so they cannot do more than print.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = value.Length > MaxText ? value[..MaxText] : value;
        return new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
    }
}
