using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Mcp.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// QA-02 over HTTP: /api/health says PDF is available only when a launch works, and the PDF endpoint takes page options.
/// The first group drives the real launch path with an executable that exists but is not a browser (no Chromium needed).
/// </summary>
[Collection(RealChromiumCollection.Name)]
public class PdfHealthTests
{
    private sealed class StubPdfRenderer : IPdfRenderer
    {
        public bool IsAvailable { get; init; } = true;

        public bool LaunchWorks { get; init; } = true;

        public string? Problem { get; init; }

        public PdfOptions? LastOptions { get; private set; }

        public string? LastSvg { get; private set; }

        public Task<PdfStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(LaunchWorks ? PdfStatus.Ready : new PdfStatus(false, "install it", Problem));

        public Task<byte[]> RenderAsync(string svg, PdfOptions? options, CancellationToken cancellationToken)
        {
            LastOptions = options;
            LastSvg = svg;
            return Task.FromResult("%PDF-1.4 stub"u8.ToArray());
        }
    }

    private static WebApplicationFactory<Program> WithRenderer(DraughtsmanServerFactory factory, IPdfRenderer renderer) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPdfRenderer>();
            services.AddSingleton(renderer);
        }));

    private static async Task<JsonNode> ReadJson(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None))!;

    [Fact]
    public async Task Health_reports_pdf_only_when_the_probe_says_a_launch_works()
    {
        using var factory = new DraughtsmanServerFactory();

        // Configured on, launch failing: the QA-02 state. Health must say false, and carry the install hint.
        using (WebApplicationFactory<Program> failing = WithRenderer(factory, new StubPdfRenderer { IsAvailable = true, LaunchWorks = false }))
        {
            JsonNode health = await ReadJson(await (await failing.CreateAuthenticatedClient()).GetAsync("/api/health", CancellationToken.None));
            Assert.False(health["export"]!["pdf"]!.GetValue<bool>());
            Assert.Equal("install it", health["export"]!["pdfHint"]!.GetValue<string>());
        }

        // The other direction: a working launch says true, with no hint.
        using (WebApplicationFactory<Program> working = WithRenderer(factory, new StubPdfRenderer()))
        {
            JsonNode health = await ReadJson(await (await working.CreateAuthenticatedClient()).GetAsync("/api/health", CancellationToken.None));
            Assert.True(health["export"]!["pdf"]!.GetValue<bool>());
            Assert.Null(health["export"]!["pdfHint"]);
        }
    }

    [Fact]
    public async Task Health_and_admin_status_say_which_part_is_the_problem_to_an_administrator_and_a_working_launch_says_none()
    {
        using var factory = new DraughtsmanServerFactory();
        Directory.CreateDirectory(factory.DataFolder);
        await File.WriteAllTextAsync(Path.Combine(factory.DataFolder, "draughtsman.yaml"), "export:\n  chromium:\n    enabled: true\n", CancellationToken.None);

        // QA B1: a missing driver is reported as the driver, so the editor's dialog does not tell the admin to install a browser.
        using (WebApplicationFactory<Program> failing = WithRenderer(factory, new StubPdfRenderer { LaunchWorks = false, Problem = PdfProblems.Driver }))
        {
            HttpClient client = await failing.CreateAuthenticatedClient();
            JsonNode health = await ReadJson(await client.GetAsync("/api/health", CancellationToken.None));
            JsonNode status = await ReadJson(await client.GetAsync("/api/admin/status", CancellationToken.None));
            Assert.Equal("driver", health["export"]!["pdfProblem"]!.GetValue<string>());
            Assert.Equal("driver", status["pdf"]!["problem"]!.GetValue<string>());
        }

        using (WebApplicationFactory<Program> working = WithRenderer(factory, new StubPdfRenderer()))
        {
            HttpClient client = await working.CreateAuthenticatedClient();
            JsonNode health = await ReadJson(await client.GetAsync("/api/health", CancellationToken.None));
            JsonNode status = await ReadJson(await client.GetAsync("/api/admin/status", CancellationToken.None));
            Assert.Null(health["export"]!["pdfProblem"]);
            Assert.Null(status["pdf"]!["problem"]);
        }
    }

    [Fact]
    public async Task The_admin_status_page_reports_pdf_from_the_same_probe_health_uses_in_both_directions()
    {
        using var factory = new DraughtsmanServerFactory();
        Directory.CreateDirectory(factory.DataFolder);
        await File.WriteAllTextAsync(Path.Combine(factory.DataFolder, "draughtsman.yaml"), "export:\n  chromium:\n    enabled: true\n", CancellationToken.None);

        // Enabled in configuration (the renderer's own cheap check even says yes), but the launch fails: not available, with the hint.
        using (WebApplicationFactory<Program> failing = WithRenderer(factory, new StubPdfRenderer { IsAvailable = true, LaunchWorks = false }))
        {
            HttpClient client = await failing.CreateAuthenticatedClient();
            JsonNode health = await ReadJson(await client.GetAsync("/api/health", CancellationToken.None));
            JsonNode status = await ReadJson(await client.GetAsync("/api/admin/status", CancellationToken.None));
            Assert.False(health["export"]!["pdf"]!.GetValue<bool>());
            Assert.True(status["pdf"]!["enabled"]!.GetValue<bool>());
            Assert.False(status["pdf"]!["available"]!.GetValue<bool>());
            Assert.Equal("install it", status["pdf"]!["hint"]!.GetValue<string>());
        }

        // The launch works: available, and no hint.
        using (WebApplicationFactory<Program> working = WithRenderer(factory, new StubPdfRenderer()))
        {
            HttpClient client = await working.CreateAuthenticatedClient();
            JsonNode health = await ReadJson(await client.GetAsync("/api/health", CancellationToken.None));
            JsonNode status = await ReadJson(await client.GetAsync("/api/admin/status", CancellationToken.None));
            Assert.True(health["export"]!["pdf"]!.GetValue<bool>());
            Assert.True(status["pdf"]!["available"]!.GetValue<bool>());
            Assert.Null(status["pdf"]!["hint"]);
        }
    }

    [Fact]
    public async Task An_enabled_component_whose_executable_cannot_launch_is_reported_absent_end_to_end()
    {
        using var factory = new DraughtsmanServerFactory();
        Directory.CreateDirectory(factory.DataFolder);
        // A file that exists but is not a browser: the old check (configuration plus File.Exists) called this available.
        string notABrowser = typeof(PdfHealthTests).Assembly.Location;
        await File.WriteAllTextAsync(
            Path.Combine(factory.DataFolder, "draughtsman.yaml"),
            $"export:\n  chromium:\n    enabled: true\n    path: \"{notABrowser.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n",
            CancellationToken.None);
        HttpClient client = await factory.CreateAuthenticatedClient();

        JsonNode health = await ReadJson(await client.GetAsync("/api/health", CancellationToken.None));
        Assert.False(health["export"]!["pdf"]!.GetValue<bool>());
        Assert.Contains("npx playwright@", health["export"]!["pdfHint"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(PdfProblems.Browser, health["export"]!["pdfProblem"]!.GetValue<string>());

        // The admin status page tells the same truth: it used to read configuration alone and show "OK" here.
        JsonNode status = await ReadJson(await client.GetAsync("/api/admin/status", CancellationToken.None));
        Assert.True(status["pdf"]!["enabled"]!.GetValue<bool>());
        Assert.False(status["pdf"]!["available"]!.GetValue<bool>());
        Assert.Contains("npx playwright@", status["pdf"]!["hint"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(PdfProblems.Browser, status["pdf"]!["problem"]!.GetValue<string>());

        // And the endpoint agrees with health: 501 with the hint, not a 500.
        string id = await CreateDocument(client);
        HttpResponseMessage pdf = await client.GetAsync($"/api/documents/{id}/pdf", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotImplemented, pdf.StatusCode);
        string detail = (await ReadJson(pdf))["detail"]!.GetValue<string>();
        Assert.True(detail.Contains("install chromium", StringComparison.Ordinal), detail);
    }

    [Fact]
    public async Task The_pdf_endpoint_passes_page_size_orientation_and_margin_to_the_renderer()
    {
        using var factory = new DraughtsmanServerFactory();
        var renderer = new StubPdfRenderer();
        using WebApplicationFactory<Program> host = WithRenderer(factory, renderer);
        HttpClient client = await host.CreateAuthenticatedClient();
        string id = await CreateDocument(client);

        HttpResponseMessage response = await client.GetAsync($"/api/documents/{id}/pdf?pageSize=a3&orientation=portrait&margin=12.5", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new PdfOptions(PdfPageSize.A3, PdfOrientation.Portrait, 12.5), renderer.LastOptions);

        // No query: the page is cut to the drawing, so an export with no options is still one page.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/documents/{id}/pdf", CancellationToken.None)).StatusCode);
        Assert.Equal(new PdfOptions(), renderer.LastOptions);
    }

    [Theory]
    [InlineData("pageSize=a5")]
    [InlineData("pageSize=2")]
    [InlineData("orientation=sideways")]
    [InlineData("margin=-1")]
    [InlineData("margin=51")]
    [InlineData("margin=abc")]
    public async Task The_pdf_endpoint_refuses_options_it_cannot_honour_with_400(string query)
    {
        using var factory = new DraughtsmanServerFactory();
        var renderer = new StubPdfRenderer();
        using WebApplicationFactory<Program> host = WithRenderer(factory, renderer);
        HttpClient client = await host.CreateAuthenticatedClient();
        string id = await CreateDocument(client);

        HttpResponseMessage response = await client.GetAsync($"/api/documents/{id}/pdf?{query}", CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(renderer.LastOptions);
    }

    [Fact]
    public async Task The_pdf_carries_the_organisation_default_theme_when_one_is_set_and_the_overpass_default_when_none_is()
    {
        using var factory = new DraughtsmanServerFactory();
        var renderer = new StubPdfRenderer();
        using WebApplicationFactory<Program> host = WithRenderer(factory, renderer);
        HttpClient admin = await host.CreateAuthenticatedClient();
        string id = await CreateDocument(admin);
        const string OverpassRed = "#e04832";

        // No organisation theme: the shipped Overpass colours (a flowchart process node is outlined in brand red, on white paper).
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/documents/{id}/pdf", CancellationToken.None)).StatusCode);
        Assert.Contains(OverpassRed, renderer.LastSvg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#0000ff", renderer.LastSvg, StringComparison.OrdinalIgnoreCase);

        // An organisation theme that swaps the brand red and sets its own paper, made the default.
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/admin/themes", new { name = "Acme" }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonNode theme = await ReadJson(created);
        theme["diagram"]!["palette"] = new JsonObject { ["red"] = "#0000ff" };
        theme["diagram"]!["exportBackground"] = "#eeeeee";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/themes/acme", theme, CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/themes/default", new { id = "acme" }, CancellationToken.None)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/documents/{id}/pdf", CancellationToken.None)).StatusCode);
        Assert.Contains("#0000ff", renderer.LastSvg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OverpassRed, renderer.LastSvg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fill=\"#eeeeee\"", renderer.LastSvg, StringComparison.OrdinalIgnoreCase);

        // Cleared again: back to Overpass, with no restart.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/themes/default", new { id = (string?)null }, CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/documents/{id}/pdf", CancellationToken.None)).StatusCode);
        Assert.Contains(OverpassRed, renderer.LastSvg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#0000ff", renderer.LastSvg, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> CreateDocument(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/documents", new
        {
            title = "Top",
            type = "flowchart",
            nodes = new[] { new { id = "n_a", kind = "process", label = "A", shape = "rectangle", geometry = new { x = 0, y = 0, w = 120, h = 60 } } },
            edges = Array.Empty<object>(),
        }, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJson(response))["id"]!.GetValue<string>();
    }
}
