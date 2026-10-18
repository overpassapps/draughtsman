using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Mcp.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Draughtsman.Server.Tests;

/// <summary>
/// Every rendered export the server makes (the PDF endpoint, MCP render_document as SVG and as PDF, the thumbnail) draws the diagram
/// and nothing else: no footer, no evaluation text, the same picture whatever is in the data folder. A data folder an earlier build
/// left a licence file and a <c>licence:</c> setting in starts cleanly and changes nothing.
/// </summary>
public sealed class ExportsUnmarkedTests : IDisposable
{
    private readonly CapturingPdfRenderer _pdf = new();
    private readonly CaptureFactory _factory;

    public ExportsUnmarkedTests()
    {
        _factory = new CaptureFactory(_pdf);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private static async Task<string> CreateDocument(HttpClient client)
    {
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Export fixture",
            type = "flowchart",
            nodes = new object[]
            {
                new { id = "n_a", kind = "process", label = "Start", shape = "rectangle", geometry = new { x = 0, y = 0, w = 120, h = 60 } },
                new { id = "n_b", kind = "process", label = "Finish", shape = "rectangle", geometry = new { x = 240, y = 0, w = 120, h = 60 } },
            },
            edges = new object[] { new { id = "e_1", from = new { node = "n_a" }, to = new { node = "n_b" }, kind = "flow", label = "then" } },
        }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return JsonNode.Parse(await created.Content.ReadAsStringAsync(CancellationToken.None))!["id"]!.GetValue<string>();
    }

    private static async Task<McpClient> Connect(HttpClient http)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http);
        return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
    }

    private static string TextOf(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    /// <summary>The SVG each rendered path of the server produces for the document, by path name.</summary>
    private async Task<Dictionary<string, string>> RenderedPaths(HttpClient http, string id)
    {
        var svgs = new Dictionary<string, string>();

        HttpResponseMessage pdf = await http.GetAsync($"/api/documents/{id}/pdf", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        svgs["GET /api/documents/{id}/pdf"] = _pdf.LastSvg ?? throw new InvalidOperationException("the PDF endpoint did not hand the renderer an SVG");

        await using McpClient mcp = await Connect(http);
        CallToolResult svg = await mcp.CallToolAsync("render_document", new Dictionary<string, object?> { ["id"] = id, ["format"] = "svg" }, cancellationToken: CancellationToken.None);
        Assert.NotEqual(true, svg.IsError);
        svgs["MCP render_document svg"] = JsonDocument.Parse(TextOf(svg)).RootElement.GetProperty("svg").GetString()!;

        _pdf.LastSvg = null;
        CallToolResult mcpPdf = await mcp.CallToolAsync("render_document", new Dictionary<string, object?> { ["id"] = id, ["format"] = "pdf" }, cancellationToken: CancellationToken.None);
        Assert.NotEqual(true, mcpPdf.IsError);
        svgs["MCP render_document pdf"] = _pdf.LastSvg ?? throw new InvalidOperationException("render_document pdf did not hand the renderer an SVG");

        svgs["GET /api/documents/{id}/thumbnail"] = await http.GetStringAsync($"/api/documents/{id}/thumbnail", CancellationToken.None);
        return svgs;
    }

    private static void AssertUnmarked(Dictionary<string, string> svgs)
    {
        Assert.Equal(4, svgs.Count);
        foreach ((string path, string svg) in svgs)
        {
            Assert.DoesNotContain("valuation", svg, StringComparison.OrdinalIgnoreCase);
            Assert.False(svg.Contains("notice", StringComparison.OrdinalIgnoreCase), $"{path} carries a notice element");
            Assert.False(svg.Contains("<text", StringComparison.Ordinal) && svg.Contains("licen", StringComparison.OrdinalIgnoreCase), $"{path} mentions a licence");
        }

        foreach (string path in svgs.Keys.Where(p => !p.EndsWith("thumbnail", StringComparison.Ordinal)))
        {
            Assert.True(svgs[path].Contains("data-id=\"n_a\"", StringComparison.Ordinal) && svgs[path].Contains("data-id=\"n_b\"", StringComparison.Ordinal) && svgs[path].Contains("data-id=\"e_1\"", StringComparison.Ordinal), $"{path} lost an element");
        }
    }

    [Fact]
    public async Task Every_rendered_export_draws_the_diagram_and_nothing_else()
    {
        using HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await CreateDocument(admin);

        AssertUnmarked(await RenderedPaths(admin, id));
    }

    [Fact]
    public async Task The_picture_is_exactly_the_diagram_bounds()
    {
        using HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await CreateDocument(admin);

        string svg = (await RenderedPaths(admin, id))["MCP render_document svg"];

        // Two 120x60 boxes 240 apart, with the renderer's 32 unit margin on every side: 424 by 124, with no band added beneath.
        Assert.Contains("viewBox=\"0 0 424 124\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_data_folder_an_earlier_build_left_a_licence_file_and_setting_in_starts_and_renders_the_same()
    {
        Directory.CreateDirectory(_factory.DataFolder);
        await File.WriteAllTextAsync(Path.Combine(_factory.DataFolder, "licence.key"), "{ \"payload\": \"from an earlier build\" }", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(_factory.DataFolder, "draughtsman.yaml"), "licence:\n  path: licence.key\n", CancellationToken.None);

        using HttpClient admin = await _factory.CreateAuthenticatedClient();
        string id = await CreateDocument(admin);

        AssertUnmarked(await RenderedPaths(admin, id));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/licence", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public void Nothing_in_the_server_source_draws_or_names_an_evaluation_notice()
    {
        string root = FindRepositoryRoot();
        string[] needles = ["Evaluation copy of Draughtsman", "evaluation-notice", "IExportNoticeSource", "EvaluationNotice"];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(file);
            foreach (string needle in needles)
            {
                Assert.False(text.Contains(needle, StringComparison.Ordinal), $"{Path.GetRelativePath(root, file)} names '{needle}'");
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Draughtsman.sln")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("Draughtsman.sln was not found above the test binaries");
    }

    /// <summary>An <see cref="IPdfRenderer"/> that is always available and keeps the SVG it was asked to print, so a test sees exactly what a PDF is made from.</summary>
    private sealed class CapturingPdfRenderer : IPdfRenderer
    {
        public string? LastSvg { get; set; }

        public bool IsAvailable => true;

        public Task<PdfStatus> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(PdfStatus.Ready);

        public Task<byte[]> RenderAsync(string svg, PdfOptions? options, CancellationToken cancellationToken)
        {
            LastSvg = svg;
            return Task.FromResult("%PDF-1.4 captured"u8.ToArray());
        }
    }

    private sealed class CaptureFactory(IPdfRenderer pdf) : DraughtsmanServerFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPdfRenderer>();
                services.AddSingleton(pdf);
            });
        }
    }
}
