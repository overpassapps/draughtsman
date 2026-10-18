using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.Tests;

public class MermaidExportEndpointTests(DraughtsmanServerFactory factory) : IClassFixture<DraughtsmanServerFactory>
{
    private static string Example(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", name));

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_sequence_document_exports_as_a_sequence_diagram_and_saves_nothing()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync("/api/export/mermaid", Json(Example("checkout-sequence.json")), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        string text = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.StartsWith("sequenceDiagram\n    title Checkout\n", text, StringComparison.Ordinal);
        Assert.Contains("\n    api-->>-web: 201 Created\n", text, StringComparison.Ordinal);

        // What comes out imports back as the same sequence through the existing importer endpoint.
        using HttpResponseMessage back = await client.PostAsync("/api/import/mermaid", new StringContent(text, Encoding.UTF8, "text/plain"), CancellationToken.None);
        JsonNode document = JsonNode.Parse(await back.Content.ReadAsStringAsync(CancellationToken.None))!["document"]!;
        Assert.Equal("sequence", document["type"]!.GetValue<string>());
        Assert.Equal(4, document["nodes"]!.AsArray().Count);
        Assert.Equal(7, document["edges"]!.AsArray().Count);
    }

    [Fact]
    public async Task An_architecture_document_exports_as_a_flowchart_with_its_container_as_a_subgraph()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync("/api/export/mermaid", Json(Example("order-service.json")), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string text = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("flowchart TD\n", text, StringComparison.Ordinal);
        Assert.Contains("    subgraph vpc[\"VPC\"]\n", text, StringComparison.Ordinal);
        Assert.Contains("db[(\"Postgres\")]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entity_document_exports_as_an_er_diagram_and_a_lossy_one_reports_its_warnings()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        const string entity = """{"id":"t_a","kind":"entity","label":"Customer","shape":"custom:er/entity","geometry":{"x":0,"y":0,"w":200,"h":90},"props":{"columns":["PK Id int not null"]}}""";
        const string plain = """{"id":"n_b","kind":"process","label":"Job","shape":"document","geometry":{"x":300,"y":0,"w":100,"h":60}}""";
        static string Doc(string nodes) => """{"schemaVersion":1,"id":"doc_er","type":"architecture","title":"Shop","nodes":[""" + nodes + """],"edges":[],"meta":{"createdAt":"2026-09-29T00:00:00.000Z","updatedAt":"2026-09-29T00:00:00.000Z","createdBy":"test"}}""";

        using HttpResponseMessage clean = await client.PostAsync("/api/export/mermaid", Json(Doc(entity)), CancellationToken.None);
        using HttpResponseMessage mixed = await client.PostAsync("/api/export/mermaid", Json(Doc(entity + "," + plain)), CancellationToken.None);

        Assert.Contains("erDiagram\n    Customer {\n        int Id PK\n    }\n", await clean.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
        Assert.Equal("0", Assert.Single(clean.Headers.GetValues("X-Draughtsman-Export-Warnings")));
        Assert.Contains("flowchart TD", await mixed.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
        Assert.NotEqual("0", Assert.Single(mixed.Headers.GetValues("X-Draughtsman-Export-Warnings")));
        // What was simplified is listed, not only counted (P8): a percent-encoded JSON array of the messages, absent when faithful.
        Assert.False(clean.Headers.Contains("X-Draughtsman-Export-Notes"));
        string[] notes = System.Text.Json.JsonSerializer.Deserialize<string[]>(Uri.UnescapeDataString(Assert.Single(mixed.Headers.GetValues("X-Draughtsman-Export-Notes")))) ?? [];
        Assert.Contains(notes, note => note.Contains("document", StringComparison.Ordinal));
        Assert.Equal(int.Parse(Assert.Single(mixed.Headers.GetValues("X-Draughtsman-Export-Warnings")), System.Globalization.CultureInfo.InvariantCulture), notes.Length);
    }

    [Theory]
    [InlineData("{}", HttpStatusCode.UnprocessableEntity)]
    [InlineData("{", HttpStatusCode.BadRequest)]
    public async Task An_invalid_document_is_refused(string json, HttpStatusCode expected)
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync("/api/export/mermaid", Json(json), CancellationToken.None);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/api/export/mermaid", Json(Example("checkout-sequence.json")), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
