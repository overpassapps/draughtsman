using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Draughtsman.Server.Export;

namespace Draughtsman.Server.Tests;

[Collection(RealChromiumCollection.Name)]
public class ExportEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private const string DefaultChromiumCandidates = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";

    private readonly DraughtsmanServerFactory _factory;

    public ExportEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Pdf_reports_the_component_absent_when_chromium_is_disabled()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string id = await CreateLabeledDocument(client);

        HttpResponseMessage response = await client.GetAsync($"/api/documents/{id}/pdf", CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        JsonNode problem = await ReadJson(response);
        Assert.Equal("PDF export is not available", problem["title"]!.GetValue<string>());
        Assert.Contains("export.chromium.enabled", problem["detail"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pdf_reports_a_missing_document_as_not_found()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/documents/doc_missing/pdf", CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Health_reports_pdf_unavailable_when_chromium_is_disabled()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        JsonNode health = await ReadJson(await client.GetAsync("/api/health", CancellationToken.None));

        Assert.False(health["export"]!["pdf"]!.GetValue<bool>());
    }

    // Browser-dependent: reported Skipped, not Passed or Failed, where no Chromium is installed (RequiresChromiumFact
    // below). Set DRAUGHTSMAN_TEST_CHROMIUM to a specific executable, or leave it unset to use Google Chrome on this
    // Mac. The test boots its own instance with export.chromium.enabled so the shared (disabled) factory is unaffected.
    [RequiresChromiumFact]
    public async Task Pdf_produces_a_pdf_carrying_a_nodes_label_when_a_browser_is_present()
    {
        string chromiumPath = ResolveChromiumPath() ?? throw new InvalidOperationException("RequiresChromiumFact should have skipped this test with no Chromium available.");

        using var enabledFactory = new DraughtsmanServerFactory();
        Directory.CreateDirectory(enabledFactory.DataFolder);
        File.WriteAllText(
            Path.Combine(enabledFactory.DataFolder, "draughtsman.yaml"),
            $"export:\n  chromium:\n    enabled: true\n    path: \"{EscapeYaml(chromiumPath)}\"\n");
        HttpClient client = await enabledFactory.CreateAuthenticatedClient();
        string id = await CreateLabeledDocument(client, "Top", "Exported");

        HttpResponseMessage response = await GetPdfAllowingSlowLaunch(client, $"/api/documents/{id}/pdf");

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CancellationToken.None)}");
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(CancellationToken.None);
        Assert.True(bytes.AsSpan().StartsWith("%PDF"u8), "Response does not begin with a PDF header.");
        Assert.True(ContainsLabelText(bytes, "Exported"), "The PDF's text layer does not carry the node's label.");
    }

    // Browser-dependent, like the test above. The PDF a real browser prints carries the diagram and nothing else: no footer text, and a
    // page cut to the drawing. (Every rendered path is pinned without a browser, in ExportsUnmarkedTests.)
    [RequiresChromiumFact]
    public async Task Pdf_carries_the_diagram_and_no_notice()
    {
        string chromiumPath = ResolveChromiumPath() ?? throw new InvalidOperationException("RequiresChromiumFact should have skipped this test with no Chromium available.");
        using var factory = new DraughtsmanServerFactory();
        Directory.CreateDirectory(factory.DataFolder);
        File.WriteAllText(Path.Combine(factory.DataFolder, "draughtsman.yaml"), $"export:\n  chromium:\n    enabled: true\n    path: \"{EscapeYaml(chromiumPath)}\"\n");
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = await CreateLabeledDocument(client, "Top", "Exported");

        byte[] pdf = await PdfBytes(client, $"/api/documents/{id}/pdf");

        Assert.True(ContainsLabelText(pdf, "Exported"), "The PDF lost the node's label.");
        Assert.False(ContainsLabelText(pdf, "Evaluation copy"), "The PDF carries an evaluation notice.");
    }

    // Browser-dependent, like the test above. PR-07: the PDF carries the diagram theme's colours for a flowchart, not the
    // renderer's neutral ones, and the requested page is the page that is printed (MediaBox, in points).
    [RequiresChromiumFact]
    public async Task Pdf_is_themed_and_takes_the_requested_page_when_a_browser_is_present()
    {
        string chromiumPath = ResolveChromiumPath() ?? throw new InvalidOperationException("RequiresChromiumFact should have skipped this test with no Chromium available.");
        using var enabledFactory = new DraughtsmanServerFactory();
        Directory.CreateDirectory(enabledFactory.DataFolder);
        File.WriteAllText(
            Path.Combine(enabledFactory.DataFolder, "draughtsman.yaml"),
            $"export:\n  chromium:\n    enabled: true\n    path: \"{EscapeYaml(chromiumPath)}\"\n");
        HttpClient client = await enabledFactory.CreateAuthenticatedClient();
        string id = await CreateLabeledDocument(client);

        // Health now reports PDF available: a launch has been proved, not just configured. A first launch on a loaded host can overrun the
        // probe's limit once (that is remembered only for ChromiumPdfRenderer.TimeoutTtl), so ask again until it has been proved; a browser that
        // never launches still fails this.
        bool proved = false;
        for (int attempt = 0; attempt < 6 && !proved; attempt++)
        {
            proved = (await ReadJson(await client.GetAsync("/api/health", CancellationToken.None)))["export"]!["pdf"]!.GetValue<bool>();
            if (!proved)
            {
                await Task.Delay(ChromiumPdfRenderer.TimeoutTtl + TimeSpan.FromSeconds(1));
            }
        }

        Assert.True(proved, "health never reported PDF available");

        byte[] a4 = await PdfBytes(client, $"/api/documents/{id}/pdf?pageSize=a4&orientation=landscape&margin=10");
        (double w, double h) = MediaBox(a4);
        Assert.InRange(w, 841.0, 842.5);
        Assert.InRange(h, 594.5, 596.0);

        byte[] letterPortrait = await PdfBytes(client, $"/api/documents/{id}/pdf?pageSize=letter&orientation=portrait");
        (double lw, double lh) = MediaBox(letterPortrait);
        Assert.InRange(lw, 611.5, 612.5);
        Assert.InRange(lh, 791.5, 792.5);

        // Fit: the page follows the drawing, which is far smaller than any paper size.
        (double fw, double fh) = MediaBox(await PdfBytes(client, $"/api/documents/{id}/pdf?pageSize=fit"));
        Assert.True(fw < 400 && fh < 300, $"a fit page of {fw} x {fh} pt is not cut to a 120 x 60 drawing");

        // The process node's fill is the overpass theme's #202a42 (32, 42, 66), and the neutral default white fill is not painted for it.
        Assert.True(PaintsFill(a4, 32, 42, 66), "The PDF does not paint the theme's process fill (#202a42).");
        Assert.False(PaintsFill(a4, 0x33, 0x41, 0x55), "The PDF paints the neutral default stroke colour as a fill.");
    }

    /// <summary>
    /// GETs a PDF, asking again (up to four tries) when the browser ran out of its time limit (504): on a host busy with other builds a render can
    /// overrun once and succeed the next time. A browser that really hangs, or any other status, comes back unchanged for the test to fail on.
    /// </summary>
    private static async Task<HttpResponseMessage> GetPdfAllowingSlowLaunch(HttpClient client, string url)
    {
        HttpResponseMessage response = await client.GetAsync(url, CancellationToken.None);
        for (int attempt = 1; attempt < 4 && response.StatusCode == HttpStatusCode.GatewayTimeout; attempt++)
        {
            response = await client.GetAsync(url, CancellationToken.None);
        }

        return response;
    }

    /// <summary>The bytes of a PDF the server answered 200 to, or a failure that carries the server's own explanation (a bare status code says nothing about why).</summary>
    private static async Task<byte[]> PdfBytes(HttpClient client, string url)
    {
        HttpResponseMessage response = await GetPdfAllowingSlowLaunch(client, url);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(CancellationToken.None)}");
        return await response.Content.ReadAsByteArrayAsync(CancellationToken.None);
    }

    private static (double Width, double Height) MediaBox(byte[] pdf)
    {
        Match match = Regex.Match(Encoding.Latin1.GetString(pdf), @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]");
        Assert.True(match.Success, "No /MediaBox in the PDF.");
        return (double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>Whether any content stream sets a fill colour within a hair of this sRGB value (Chromium writes <c>r g b rg</c> as 0..1 floats).</summary>
    private static bool PaintsFill(byte[] pdf, int r, int g, int b)
    {
        foreach (string stream in StreamContents(pdf))
        {
            foreach (Match match in Regex.Matches(stream, @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+rg\b"))
            {
                double[] rgb = new[] { 1, 2, 3 }.Select(i => double.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture) * 255).ToArray();
                if (Math.Abs(rgb[0] - r) < 1.5 && Math.Abs(rgb[1] - g) < 1.5 && Math.Abs(rgb[2] - b) < 1.5)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async Task<string> CreateLabeledDocument(HttpClient client, string title = "Top", string label = "Exported")
    {
        var response = await client.PostAsJsonAsync("/api/documents", new
        {
            title,
            type = "flowchart",
            nodes = new[]
            {
                new
                {
                    id = "n_a",
                    kind = "process",
                    label,
                    shape = "rectangle",
                    geometry = new { x = 0, y = 0, w = 120, h = 60 },
                },
            },
            edges = Array.Empty<object>(),
        }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJson(response))["id"]!.GetValue<string>();
    }

    /// <summary>The Chromium executable: DRAUGHTSMAN_TEST_CHROMIUM when set, else Google Chrome on this Mac, else null (skip).</summary>
    private static string? ResolveChromiumPath()
    {
        string? configured = Environment.GetEnvironmentVariable("DRAUGHTSMAN_TEST_CHROMIUM");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        return File.Exists(DefaultChromiumCandidates) ? DefaultChromiumCandidates : null;
    }

    /// <summary>
    /// A Fact that xunit reports Skipped, not Passed, when no Chromium executable is available (this class's static
    /// <c>Skip</c> setter runs at test discovery, before the run). Mirrors the DRAUGHTSMAN_TEST_POSTGRES pattern in
    /// <c>PostgresStoreTests</c>, but native to xunit rather than the Xunit.SkippableFact package that pattern uses,
    /// since this task's only new dependency is Microsoft.Playwright.
    /// </summary>
    private sealed class RequiresChromiumFactAttribute : FactAttribute
    {
        public RequiresChromiumFactAttribute()
        {
            if (ResolveChromiumPath() is null)
            {
                Skip = "No Chromium executable found; set DRAUGHTSMAN_TEST_CHROMIUM or install Google Chrome on this Mac.";
            }
        }
    }

    private static string EscapeYaml(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the PDF embeds the label as text. Chromium subsets fonts over Identity-H, so the label is not stored as
    /// a contiguous string; it is carried as the ToUnicode CMap's destination codepoints. Every character of
    /// <paramref name="label"/> must appear as a destination codepoint in that CMap.
    /// </summary>
    private static bool ContainsLabelText(byte[] pdf, string label)
    {
        var needed = label.Select(c => (int)c).ToHashSet();
        var found = new HashSet<int>();

        foreach (string stream in StreamContents(pdf))
        {
            if (!stream.Contains("beginbfchar", StringComparison.Ordinal) && !stream.Contains("beginbfrange", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in Regex.Matches(stream, @"<([0-9A-Fa-f]{4})>\s*<([0-9A-Fa-f]{4})>"))
            {
                AddCodepoint(found, match.Groups[2].Value);
            }

            foreach (Match match in Regex.Matches(stream, @"<([0-9A-Fa-f]{4})>\s*<([0-9A-Fa-f]{4})>\s*<([0-9A-Fa-f]{4})>"))
            {
                if (int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int src)
                    && int.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int srcEnd)
                    && int.TryParse(match.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int dst)
                    && srcEnd >= src && srcEnd - src <= 1024)
                {
                    for (int i = 0; i <= srcEnd - src; i++)
                    {
                        found.Add(dst + i);
                    }
                }
            }
        }

        return needed.IsSubsetOf(found);
    }

    private static void AddCodepoint(HashSet<int> found, string hex)
    {
        if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
        {
            found.Add(value);
        }
    }

    /// <summary>The PDF's raw text plus every FlateDecode stream decompressed, since Chromium compresses content streams.</summary>
    private static IEnumerable<string> StreamContents(byte[] pdf)
    {
        yield return Encoding.Latin1.GetString(pdf);

        byte[] marker = "stream"u8.ToArray();
        byte[] endMarker = "endstream"u8.ToArray();
        int offset = 0;
        while (offset < pdf.Length - marker.Length)
        {
            int start = IndexOf(pdf, marker, offset);
            if (start < 0)
            {
                yield break;
            }

            if (IsEndStream(pdf, start))
            {
                offset = start + marker.Length;
                continue;
            }

            int body = start + marker.Length;
            while (body < pdf.Length && pdf[body] is (byte)'\r' or (byte)'\n')
            {
                body++;
            }

            int end = IndexOf(pdf, endMarker, body);
            if (end < 0)
            {
                yield break;
            }

            int length = end;
            while (length > body && pdf[length - 1] is (byte)'\r' or (byte)'\n')
            {
                length--;
            }

            string content = TryInflate(pdf[body..length]);
            if (content.Length > 0)
            {
                yield return content;
            }

            offset = end + endMarker.Length;
        }
    }

    private static bool IsEndStream(byte[] pdf, int start)
    {
        int prefixLength = 3;
        return start >= prefixLength && pdf.AsSpan(start - prefixLength, prefixLength).SequenceEqual("end"u8);
    }

    private static string TryInflate(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var output = new MemoryStream();
            using var deflate = new ZLibStream(input, CompressionMode.Decompress);
            deflate.CopyTo(output);
            return Encoding.Latin1.GetString(output.ToArray());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response)
    {
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None)) ?? throw new InvalidOperationException("Empty body.");
    }
}
