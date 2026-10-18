using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Server.Endpoints;
using Draughtsman.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Draughtsman.Server.Tests;

public class HealthEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public HealthEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task An_administrator_gets_version_storage_and_sidecar_detail()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await admin.GetAsync("/api/health", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        HealthResponse? body = await response.Content.ReadFromJsonAsync<HealthResponse>(CancellationToken.None);
        Assert.NotNull(body);
        Assert.Equal("ok", body.Status);
        Assert.Equal(HealthEndpoints.ProductVersion, body.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+", body.Version);
        Assert.NotNull(body.Sidecar);
        Assert.Equal("running", body.Sidecar.Status);
        Assert.Matches(@"^\d+\.\d+\.\d+", body.Sidecar.Version);
        Assert.StartsWith("v", body.Sidecar.Node, StringComparison.Ordinal);
        Assert.Equal("sqlite", body.Storage);
        Assert.NotNull(body.Export);
        Assert.NotNull(body.Ai);
        Assert.True(File.Exists(Path.Combine(_factory.DataFolder, StorageOptions.SqliteFileName)));
    }

    [Fact]
    public async Task An_anonymous_caller_gets_the_status_and_nothing_else()
    {
        await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/api/health", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonObject body = (await response.Json()).AsObject();
        Assert.Equal(["status"], body.Select(p => p.Key).ToArray());
        Assert.Equal("ok", body["status"]!.GetValue<string>());
        string raw = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.DoesNotContain(HealthEndpoints.ProductVersion, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("sqlite", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signed_in_editor_gets_what_the_menus_gate_on_but_not_the_server_detail()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        await admin.CreateUser("editor-health@example.com");
        HttpClient editor = await _factory.SignIn("editor-health@example.com");

        HttpResponseMessage response = await editor.GetAsync("/api/health", CancellationToken.None);

        JsonObject body = (await response.Json()).AsObject();
        Assert.Equal(["ai", "export", "status"], body.Select(p => p.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.NotNull(body["export"]!["pdf"]);
        Assert.NotNull(body["ai"]!["enabled"]);
        Assert.Null(body["ai"]!["provider"]);
    }

    [Fact]
    public async Task Unknown_api_route_is_404_even_when_spa_is_served()
    {
        string contentRoot = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        string dist = Path.Combine(contentRoot, "web", "dist");
        Directory.CreateDirectory(dist);
        await File.WriteAllTextAsync(Path.Combine(dist, "index.html"), "<!doctype html><title>Draughtsman SPA</title>", CancellationToken.None);
        try
        {
            using WebApplicationFactory<Program> spaFactory = _factory.WithWebHostBuilder(builder => builder.UseContentRoot(contentRoot));
            HttpClient client = spaFactory.CreateClient();

            HttpResponseMessage spaRoute = await client.GetAsync("/documents/doc_1", CancellationToken.None);
            HttpResponseMessage unknownApi = await client.GetAsync("/api/nope", CancellationToken.None);
            HttpResponseMessage health = await client.GetAsync("/api/health", CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, spaRoute.StatusCode);
            Assert.Contains("Draughtsman SPA", await spaRoute.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }
}
