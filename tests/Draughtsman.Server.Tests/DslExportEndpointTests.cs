using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Server.Tests;

public class DslExportEndpointTests(DraughtsmanServerFactory factory) : IClassFixture<DraughtsmanServerFactory>
{
    [Theory]
    [InlineData("order-service.json")]
    [InlineData("editor-session.json")]
    public async Task Unsaved_model_prints_and_parses_losslessly(string fixture)
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        string json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "contract", fixture), CancellationToken.None);
        using var response = await client.PostAsync("/api/export/dsl", new StringContent(json, Encoding.UTF8, "application/json"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        string text = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Equal(Dsl.Print(DocumentJson.Deserialize(json)), text);
        using var parsed = await client.PostAsync("/api/import/dsl", new StringContent(text, Encoding.UTF8, "text/plain"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, parsed.StatusCode);
        var result = JsonNode.Parse(await parsed.Content.ReadAsStringAsync(CancellationToken.None))!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), result["document"]));
        Assert.Empty(result["warnings"]!.AsArray());
    }

    [Theory]
    [InlineData("{}", HttpStatusCode.UnprocessableEntity)]
    [InlineData("{", HttpStatusCode.BadRequest)]
    public async Task Invalid_documents_do_not_print(string json, HttpStatusCode expected)
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        using var response = await client.PostAsync("/api/export/dsl", new StringContent(json, Encoding.UTF8, "application/json"), CancellationToken.None);
        Assert.Equal(expected, response.StatusCode);
    }
}
