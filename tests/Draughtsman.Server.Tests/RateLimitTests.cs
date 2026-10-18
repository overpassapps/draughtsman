using System.Net;
using System.Net.Http.Json;
using System.Text;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Export;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// AU-09: per-user rate limits and instance-wide concurrency caps on PDF, layout, import, AI and MCP. Each test checks
/// both directions: calls inside the limit are not refused, the call over it is (429 with Retry-After), another user is
/// unaffected, and a limit of 0 is off.
/// </summary>
public class RateLimitTests
{
    private const string EmptyDocument = "{\"schemaVersion\":1,\"id\":\"doc_rl\",\"type\":\"flowchart\",\"title\":\"t\",\"nodes\":[],\"edges\":[]}";

    [Fact]
    public async Task Layout_is_limited_per_user_and_the_limit_is_each_users_own()
    {
        using var factory = new ConfiguredServerFactory("limits:\n  layoutPerUserPerMinute: 3\n");
        HttpClient alice = await factory.CreateAuthenticatedClient();
        await alice.CreateUser("bob@example.com");
        HttpClient bob = await factory.SignIn("bob@example.com");

        var aliceStatuses = new List<HttpStatusCode>();
        for (int i = 0; i < 4; i++)
        {
            aliceStatuses.Add((await alice.PostAsync("/api/layout", Json(EmptyDocument))).StatusCode);
        }

        HttpResponseMessage bobsFirst = await bob.PostAsync("/api/layout", Json(EmptyDocument));
        HttpResponseMessage aliceRefused = await alice.PostAsync("/api/layout", Json(EmptyDocument));

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, aliceStatuses.Take(3));
        Assert.Equal(HttpStatusCode.TooManyRequests, aliceStatuses[3]);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, bobsFirst.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, aliceRefused.StatusCode);
        Assert.True(int.Parse(Assert.Single(aliceRefused.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture) >= 1);
        Assert.Equal("application/problem+json", aliceRefused.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/api/import/dsl", "importPerUserPerMinute")]
    [InlineData("/api/import/mermaid", "importPerUserPerMinute")]
    [InlineData("/api/import/sql", "importPerUserPerMinute")]
    [InlineData("/api/import/drawio", "importPerUserPerMinute")]
    [InlineData("/api/import/visio", "importPerUserPerMinute")]
    [InlineData("/api/ai/generate-from-text", "aiPerUserPerMinute")]
    [InlineData("/api/ai/diagram-to-document", "aiPerUserPerMinute")]
    [InlineData("/mcp", "mcpPerUserPerMinute")]
    public async Task Each_limited_route_class_refuses_the_call_over_its_limit(string path, string setting)
    {
        using var factory = new ConfiguredServerFactory($"limits:\n  {setting}: 2\n");
        HttpClient client = await factory.CreateAuthenticatedClient();

        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 3; i++)
        {
            statuses.Add((await client.PostAsync(path, new StringContent("x", Encoding.UTF8, "text/plain"))).StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(2));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[2]);
    }

    [Fact]
    public async Task A_limit_of_zero_is_off()
    {
        using var factory = new ConfiguredServerFactory("limits:\n  importPerUserPerMinute: 0\n");
        HttpClient client = await factory.CreateAuthenticatedClient();

        for (int i = 0; i < 12; i++)
        {
            HttpResponseMessage response = await client.PostAsync("/api/import/dsl", new StringContent("x", Encoding.UTF8, "text/plain"));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }
    }

    [Fact]
    public async Task Routes_outside_the_limited_classes_are_not_counted()
    {
        using var factory = new ConfiguredServerFactory("limits:\n  layoutPerUserPerMinute: 1\n  importPerUserPerMinute: 1\n  aiPerUserPerMinute: 1\n  mcpPerUserPerMinute: 1\n");
        HttpClient client = await factory.CreateAuthenticatedClient();

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/documents")).StatusCode);
        }
    }

    [Fact]
    public async Task At_most_the_configured_number_of_pdf_renders_run_at_once_and_a_finished_one_frees_its_place()
    {
        var renderer = new GatedPdfRenderer();
        using var factory = new ConfiguredServerFactory("limits:\n  pdfConcurrency: 1\n  pdfPerUserPerMinute: 0\n")
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IPdfRenderer>(renderer)));
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = await CreateDocument(client);

        Task<HttpResponseMessage> first = client.GetAsync($"/api/documents/{id}/pdf");
        await renderer.Started.Task.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None); // a hang guard, not a speed assertion
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        HttpResponseMessage second = await client.GetAsync($"/api/documents/{id}/pdf", giveUp.Token);

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.False(first.IsCompleted);

        renderer.Release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        HttpResponseMessage third = await client.GetAsync($"/api/documents/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    [Fact]
    public async Task Pdf_renders_are_also_limited_per_user()
    {
        var renderer = new GatedPdfRenderer();
        renderer.Release.SetResult();
        using var factory = new ConfiguredServerFactory("limits:\n  pdfConcurrency: 0\n  pdfPerUserPerMinute: 2\n")
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IPdfRenderer>(renderer)));
        HttpClient client = await factory.CreateAuthenticatedClient();
        string id = await CreateDocument(client);

        HttpStatusCode[] statuses =
        [
            (await client.GetAsync($"/api/documents/{id}/pdf")).StatusCode,
            (await client.GetAsync($"/api/documents/{id}/pdf")).StatusCode,
            (await client.GetAsync($"/api/documents/{id}/pdf")).StatusCode,
        ];

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task The_render_gate_holds_its_cap_for_every_caller_not_only_http_ones()
    {
        var gate = new PdfRenderGate(permits: 2, wait: TimeSpan.FromMilliseconds(50));

        using IDisposable one = await gate.EnterAsync(CancellationToken.None);
        IDisposable two = await gate.EnterAsync(CancellationToken.None);
        await Assert.ThrowsAsync<PdfBusyException>(() => gate.EnterAsync(CancellationToken.None));

        two.Dispose();
        two.Dispose(); // a double dispose must not hand out a third slot
        using IDisposable again = await gate.EnterAsync(CancellationToken.None);
        await Assert.ThrowsAsync<PdfBusyException>(() => gate.EnterAsync(CancellationToken.None));

        var unlimited = new PdfRenderGate(permits: 0, wait: TimeSpan.FromMilliseconds(50));
        for (int i = 0; i < 10; i++)
        {
            _ = await unlimited.EnterAsync(CancellationToken.None);
        }
    }

    private static StringContent Json(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static async Task<string> CreateDocument(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsync("/api/documents", Json("{\"title\":\"pdf\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]}"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Json())["id"]!.GetValue<string>();
    }

    private sealed class GatedPdfRenderer : IPdfRenderer
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => true;

        public Task<PdfStatus> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(PdfStatus.Ready);

        public async Task<byte[]> RenderAsync(string svg, PdfOptions? options, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return [0x25, 0x50, 0x44, 0x46];
        }
    }
}
