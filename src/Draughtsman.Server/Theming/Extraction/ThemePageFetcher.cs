using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Theming.Extraction;

/// <summary>Why a fetch was refused. The endpoint turns each into a fixed message and a fixed outcome word for the security log.</summary>
internal enum FetchRefusal
{
    Disabled,
    InvalidUrl,
    SchemeNotAllowed,
    BlockedAddress,
    RedirectRefused,
    TooLarge,
    TimedOut,
    NotCss,
    Unreachable,
}

/// <summary>
/// A refused fetch. The message is fixed text written for the administrator and never carries an address, a header, a status line
/// or any of the page; the HTTP stack wraps one thrown from the connect step, so <see cref="Find"/> looks down the chain.
/// </summary>
internal sealed class ThemeFetchException(FetchRefusal reason, string message) : Exception(message)
{
    public FetchRefusal Reason { get; } = reason;

    public static ThemeFetchException Of(FetchRefusal reason)
    {
        return new ThemeFetchException(reason, MessageFor(reason));
    }

    public static ThemeFetchException? Find(Exception? error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            if (e is ThemeFetchException refused)
            {
                return refused;
            }
        }

        return null;
    }

    /// <summary>The outcome word the security log records.</summary>
    public string Outcome => Reason switch
    {
        FetchRefusal.Disabled => "disabled",
        FetchRefusal.InvalidUrl => "invalid-url",
        FetchRefusal.SchemeNotAllowed => "scheme-refused",
        FetchRefusal.BlockedAddress => "blocked-address",
        FetchRefusal.RedirectRefused => "redirect-refused",
        FetchRefusal.TooLarge => "too-large",
        FetchRefusal.TimedOut => "timed-out",
        FetchRefusal.NotCss => "not-css",
        _ => "unreachable",
    };

    public static string MessageFor(FetchRefusal reason)
    {
        return reason switch
        {
            FetchRefusal.Disabled => "Fetching a page by its address is not enabled on this server. Paste the CSS instead.",
            FetchRefusal.InvalidUrl => "That is not a web address the server can fetch. Use a full address such as https://example.com.",
            FetchRefusal.SchemeNotAllowed => "Only https addresses can be fetched.",
            FetchRefusal.BlockedAddress => "The server will not fetch that address: it points at a private, internal or reserved address.",
            FetchRefusal.RedirectRefused => "That page redirects to a different host, and the server does not follow that. Use the address it ends up at.",
            FetchRefusal.TooLarge => "That page and its stylesheets are larger than the server will read (2 MiB in all).",
            FetchRefusal.TimedOut => "The page took too long to fetch (the server allows 15 seconds in all).",
            FetchRefusal.NotCss => "That address did not return a web page or a stylesheet (only text/html and text/css are read).",
            _ => "The server could not fetch that address.",
        };
    }
}

/// <summary>The limits of one fetch. The defaults are the ones docs/admin-guide.md states; tests shrink them.</summary>
internal sealed record ThemeFetchLimits
{
    public long MaxBytes { get; init; } = 2 * 1024 * 1024;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public int MaxStylesheets { get; init; } = 8;

    public int MaxRedirects { get; init; } = 3;
}

/// <summary>What a fetch read: the sources for the extractor, the host asked for, and what was and was not read of its stylesheets.</summary>
internal sealed record FetchedPage(ThemeSource Source, string Host, int StylesheetsRead, int StylesheetsSkipped);

/// <summary>
/// The two things the guard needs from the network, as a seam: name resolution and the TCP connect itself. Production is
/// <see cref="SystemOutboundNetwork"/>; a test substitutes an answer for any name and a connect that records where it was told to go,
/// so a policy test touches neither DNS nor a socket.
/// </summary>
internal interface IOutboundNetwork
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);

    ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken);
}

internal sealed class SystemOutboundNetwork : IOutboundNetwork
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        return await Dns.GetHostAddressesAsync(host, cancellationToken);
    }

    public async ValueTask<Stream> ConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Fetches a web page and a few of its stylesheets for the Themes page's "From a website" (opt-in:
/// <c>themes.fetchFromUrl.enabled</c>, off by default, and only when an administrator presses the button). A static fetch: no
/// script runs, no font or image is requested, nothing is stored, and the text goes straight to <see cref="ThemeExtractor"/>.
/// <para>
/// Rules, each pinned by a test: https only (http only to <c>localhost</c> or a loopback address); no user name or password in the
/// address and none sent, no cookies sent or kept; redirects followed by hand, only to the same host and never from https to http, at
/// most <see cref="ThemeFetchLimits.MaxRedirects"/>; <c>text/html</c> for the page and <c>text/css</c> for a sheet, nothing else;
/// at most <see cref="ThemeFetchLimits.MaxStylesheets"/> sheets, never an <c>@import</c>; <see cref="ThemeFetchLimits.MaxBytes"/> in
/// all across every response (measured after decompression); <see cref="ThemeFetchLimits.Timeout"/> in all; the address rules of
/// <see cref="ThemeFetchHosts"/> on every host, the page's and each sheet's.
/// </para>
/// <para>
/// <b>DNS rebinding.</b> The address rules are applied in the HTTP client's connect step: the host is resolved there, every answer is
/// judged, and the connection is made to one of the addresses that were judged, by address, never by name again. There is no second
/// lookup between the check and the connect for a hostile resolver to answer differently, and every new connection is checked afresh.
/// TLS is still negotiated against the host name, so a certificate is checked against the name and not the address. A fresh client
/// is built for each fetch, so nothing is pooled across fetches.
/// </para>
/// <para>
/// <b>Residual risk.</b> No proxy is used (<c>UseProxy</c> is off, so the server's own HTTP_PROXY settings do not apply here): the fetch
/// goes direct or not at all, which keeps the checked address the connected address. The rule judges addresses, not intent: a public
/// host that answers with colours is read, whatever it is. An operator who lists a host in <c>allowedHosts</c> lifts the address rule
/// for that name for good, including for what it resolves to later.
/// </para>
/// </summary>
internal sealed class ThemePageFetcher
{
    private readonly bool _enabled;
    private readonly IReadOnlyList<string> _allowedHosts;
    private readonly IOutboundNetwork _network;
    private readonly ThemeFetchLimits _limits;

    public ThemePageFetcher(ThemeUrlFetchConfig config, IOutboundNetwork? network = null, ThemeFetchLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _enabled = config.Enabled;
        _allowedHosts = [.. config.AllowedHosts.Select(ThemeFetchHosts.NormaliseAllowedHost).OfType<string>().Distinct(StringComparer.Ordinal)];
        _network = network ?? new SystemOutboundNetwork();
        _limits = limits ?? new ThemeFetchLimits();
    }

    public bool Enabled => _enabled;

    /// <summary>The host of <paramref name="url"/> for the security log, or null when it has none: never the path, query or credentials.</summary>
    public static string? HostForLog(string url)
    {
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" && ThemeFetchHosts.ClassifyHost(uri, out string host, out _) != ThemeFetchHosts.HostKind.Numeric ? host : null;
    }

    public async Task<FetchedPage> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            throw ThemeFetchException.Of(FetchRefusal.Disabled);
        }

        Uri start = Judge(url.Trim());
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_limits.Timeout);
        using HttpClient client = CreateClient();
        long remaining = _limits.MaxBytes;
        try
        {
            (string html, Uri landed, long used) = await GetText(client, start, "text/html", remaining, timeout.Token);
            remaining -= used;

            List<string> sheets = [.. HtmlScanner.StyleBlocks(html)];
            List<Uri> links = [];
            foreach (string href in HtmlScanner.StylesheetLinks(html))
            {
                if (Uri.TryCreate(landed, href, out Uri? link) && link.Scheme is "http" or "https" && !links.Contains(link))
                {
                    links.Add(link);
                }
            }

            int read = 0;
            int skipped = Math.Max(0, links.Count - _limits.MaxStylesheets);
            foreach (Uri link in links.Take(_limits.MaxStylesheets))
            {
                try
                {
                    (string css, _, long cssBytes) = await GetText(client, link, "text/css", remaining, timeout.Token);
                    remaining -= cssBytes;
                    sheets.Add(css);
                    read++;
                }
                catch (ThemeFetchException skip) when (skip.Reason is not FetchRefusal.TimedOut)
                {
                    // A sheet that cannot be read (blocked, not css, too much for what is left, an error) is left out; the page's
                    // own styles and the other sheets still make a draft, and the count is reported.
                    skipped++;
                }
            }

            return new FetchedPage(new ThemeSource(sheets, HtmlScanner.ThemeColours(html)), start.IdnHost.ToLowerInvariant(), read, skipped);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw ThemeFetchException.Of(FetchRefusal.TimedOut);
        }
    }

    /// <summary>The address as a <see cref="Uri"/> if the rules allow asking for it at all; otherwise the refusal. No network is touched.</summary>
    private Uri Judge(string text)
    {
        if (text.Length is 0 or > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            // A scheme-less or non-http(s) text is not an address; ftp:, file: and javascript: land here too.
            throw ThemeFetchException.Of(text.Contains("://", StringComparison.Ordinal) && !text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? FetchRefusal.SchemeNotAllowed : FetchRefusal.InvalidUrl);
        }

        return JudgeUri(uri);
    }

    private Uri JudgeUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            throw ThemeFetchException.Of(FetchRefusal.SchemeNotAllowed);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw ThemeFetchException.Of(FetchRefusal.InvalidUrl);
        }

        ThemeFetchHosts.HostKind kind = ThemeFetchHosts.ClassifyHost(uri, out string host, out IPAddress? address);
        if (kind == ThemeFetchHosts.HostKind.Numeric)
        {
            throw ThemeFetchException.Of(FetchRefusal.InvalidUrl);
        }

        if (uri.Scheme == "http" && !ThemeFetchHosts.IsLoopbackHost(uri))
        {
            throw ThemeFetchException.Of(FetchRefusal.SchemeNotAllowed);
        }

        // An address literal is judged here, before any connection; a name is judged by what it resolves to, in the connect step.
        if (address is not null && !ThemeFetchHosts.IsAllowed(host, _allowedHosts) && ThemeFetchHosts.IsBlocked(address))
        {
            throw ThemeFetchException.Of(FetchRefusal.BlockedAddress);
        }

        return uri;
    }

    private HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectCallback = ConnectAsync,
            PooledConnectionLifetime = TimeSpan.FromSeconds(30),
        };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = _limits.MaxBytes };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Draughtsman-theme-reader", "1"));
        return client;
    }

    /// <summary>The address rules at the one place they cannot be talked round: the address about to be connected to.</summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        string host = context.DnsEndPoint.Host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(host, out IPAddress? literal) ? [literal] : await ResolveAsync(host, cancellationToken);
        string judged = literal?.ToString() ?? host;
        if (!ThemeFetchHosts.MayConnect(addresses, ThemeFetchHosts.IsAllowed(judged, _allowedHosts)))
        {
            throw ThemeFetchException.Of(addresses.Count == 0 ? FetchRefusal.Unreachable : FetchRefusal.BlockedAddress);
        }

        Exception? last = null;
        foreach (IPAddress address in addresses)
        {
            try
            {
                return await _network.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
            }
            catch (Exception error) when (error is SocketException or IOException)
            {
                last = error;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    private async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            return await _network.ResolveAsync(host, cancellationToken);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    /// <summary>One GET, following same-host redirects by hand, returning the decoded text and the address it ended at.</summary>
    private async Task<(string Text, Uri Landed, long Bytes)> GetText(HttpClient client, Uri address, string mediaType, long budget, CancellationToken cancellationToken)
    {
        Uri current = JudgeUri(address);
        for (int hop = 0; hop <= _limits.MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd(mediaType);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException error) when (ThemeFetchException.Find(error) is { } refused)
            {
                throw refused;
            }
            catch (HttpRequestException)
            {
                throw ThemeFetchException.Of(FetchRefusal.Unreachable);
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    current = FollowRedirect(current, response.Headers.Location);
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw ThemeFetchException.Of(FetchRefusal.Unreachable);
                }

                if (!string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
                {
                    throw ThemeFetchException.Of(FetchRefusal.NotCss);
                }

                if (response.Content.Headers.ContentLength is { } declared && declared > budget)
                {
                    throw ThemeFetchException.Of(FetchRefusal.TooLarge);
                }

                (string text, long bytes) = await ReadLimited(response, budget, cancellationToken);
                return (text, current, bytes);
            }
        }

        throw ThemeFetchException.Of(FetchRefusal.RedirectRefused);
    }

    private Uri FollowRedirect(Uri from, Uri? location)
    {
        if (location is null || !Uri.TryCreate(from, location, out Uri? target))
        {
            throw ThemeFetchException.Of(FetchRefusal.Unreachable);
        }

        bool sameHost = ThemeFetchHosts.ClassifyHost(from, out string fromHost, out _) != ThemeFetchHosts.HostKind.Numeric
            && ThemeFetchHosts.ClassifyHost(target, out string targetHost, out _) != ThemeFetchHosts.HostKind.Numeric
            && string.Equals(fromHost, targetHost, StringComparison.Ordinal);
        bool downgrade = from.Scheme == "https" && target.Scheme != "https";
        if (!sameHost || downgrade)
        {
            throw ThemeFetchException.Of(FetchRefusal.RedirectRefused);
        }

        return JudgeUri(target);
    }

    private static async Task<(string Text, long Bytes)> ReadLimited(HttpResponseMessage response, long budget, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > budget)
            {
                throw ThemeFetchException.Of(FetchRefusal.TooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        // Colours are ASCII in any encoding a stylesheet uses; invalid sequences become replacement characters rather than an error.
        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length), buffer.Length);
    }
}
