using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Draughtsman.Core.Import;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Draughtsman.Server.Tests;

public class ImportEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public ImportEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Imports_a_flowchart_and_returns_document_json_with_warnings()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        const string mermaid = "flowchart LR\n  A([Start]) --> B{OK?}\n  B -->|yes| C((Ship))\n";

        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid?title=Release",
            new StringContent(mermaid, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        string json = await response.Content.ReadAsStringAsync(CancellationToken.None);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(json, DocumentJson.Options);
        Assert.NotNull(body);
        Assert.Equal("Release", body.Document.Title);
        Assert.Equal("LR", body.Direction);
        Assert.Equal(3, body.Document.Nodes.Count);
        Assert.Equal(2, body.Document.Edges.Count);
        Assert.Equal("decision", body.Document.Nodes.Single(n => n.Id == "n_B").Kind);
        Assert.Contains(body.Warnings, w => w.Code == ImportWarningCodes.KindGuessed && w.ElementId == "n_C");

        using JsonDocument raw = JsonDocument.Parse(json);
        Assert.Equal("flowchart", raw.RootElement.GetProperty("document").GetProperty("type").GetString());
        Assert.False(raw.RootElement.GetProperty("document").GetProperty("nodes")[0].TryGetProperty("geometry", out _), "imported nodes carry no geometry until layout");
    }

    [Fact]
    public async Task Type_query_selects_the_architecture_vocabulary()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        const string mermaid = "graph TD\n  api[Orders API] --> db[(Postgres)]\n";

        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid?type=Architecture",
            new StringContent(mermaid, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(await response.Content.ReadAsStringAsync(CancellationToken.None), DocumentJson.Options);
        Assert.NotNull(body);
        Assert.Equal("database", body.Document.Nodes.Single(n => n.Id == "n_db").Kind);
        Assert.Equal("data", body.Document.Edges.Single().Kind);
    }

    [Theory]
    [InlineData("?type=freeform")]
    [InlineData("?type=erd")]
    public async Task Unusable_type_is_400(string query)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid" + query,
            new StringContent("flowchart TD\n A --> B", Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken.None);
        Assert.NotNull(problem);
        Assert.NotNull(problem.Detail);
    }

    [Fact]
    public async Task Empty_body_is_400()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid",
            new StringContent("   \n", Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_body_is_413()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string mermaid = "flowchart TD\n A[" + new string('x', 1_000_001) + "]";

        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid",
            new StringContent(mermaid, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Other_diagram_types_are_422_with_a_warning()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid",
            new StringContent("classDiagram\n  Animal <|-- Duck\n", Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(await response.Content.ReadAsStringAsync(CancellationToken.None), DocumentJson.Options);
        Assert.NotNull(body);
        Assert.Empty(body.Document.Nodes);
        Assert.Contains(body.Warnings, w => w.Code == ImportWarningCodes.UnsupportedDiagram);
    }

    private const string ErSource = "erDiagram\n  CUSTOMER {\n    int id PK\n  }\n  ORDER {\n    int id PK\n    int customer_id FK\n  }\n  ORDER }o--|| CUSTOMER : places\n  this is nonsense\n";

    [Theory]
    [InlineData("", "architecture")]
    [InlineData("?type=architecture", "architecture")]
    [InlineData("?type=freeform", "freeform")]
    public async Task An_erDiagram_imports_as_entities_and_crows_feet_edges_and_a_bad_line_only_warns(string query, string expectedType)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid" + query,
            new StringContent(ErSource, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        JsonElement document = raw.RootElement.GetProperty("document");
        Assert.Equal(expectedType, document.GetProperty("type").GetString());
        Assert.Equal(["t_CUSTOMER", "t_ORDER"], document.GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()));
        Assert.Equal("custom:er/entity", document.GetProperty("nodes")[0].GetProperty("shape").GetString());
        JsonElement style = document.GetProperty("edges")[0].GetProperty("style");
        Assert.Equal("zeroOrMany", style.GetProperty("arrowStart").GetString());
        Assert.Equal("oneAndOnlyOne", style.GetProperty("arrowEnd").GetString());
        Assert.Equal(ImportWarningCodes.ParseError, Assert.Single(raw.RootElement.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("flowchart")]
    [InlineData("sequence")]
    public async Task An_erDiagram_asked_for_as_a_type_that_cannot_hold_entities_is_400_naming_the_mismatch(string type)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync($"/api/import/mermaid?type={type}",
            new StringContent(ErSource, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken.None);
        Assert.Contains("erDiagram", problem?.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flowchart_asked_for_as_freeform_is_still_400()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid?type=freeform",
            new StringContent("flowchart TD\n  A --> B\n", Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private const string SequenceSource = "sequenceDiagram\n  participant W as Web app\n  actor U as Customer\n  U->>+W: Place order\n  autonumber\n  W-->>-U: 201 Created\n";

    [Fact]
    public async Task A_sequence_diagram_header_imports_as_a_sequence_document_when_no_type_is_given()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid?title=Checkout",
            new StringContent(SequenceSource, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync(CancellationToken.None);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(json, DocumentJson.Options);
        Assert.NotNull(body);
        Assert.Equal("Checkout", body.Document.Title);
        Assert.Equal(["p_W", "p_U"], body.Document.Nodes.Select(n => n.Id));
        Assert.Equal(["Place order", "201 Created"], body.Document.Edges.Select(e => e.Label));
        Assert.Equal(["sync", "return"], body.Document.Edges.Select(e => e.Kind));
        Assert.Equal(5, Assert.Single(body.Warnings, w => w.Code == ImportWarningCodes.UnsupportedSyntax).Line);

        using JsonDocument raw = JsonDocument.Parse(json);
        JsonElement document = raw.RootElement.GetProperty("document");
        Assert.Equal("sequence", document.GetProperty("type").GetString());
        Assert.False(document.GetProperty("nodes")[0].TryGetProperty("geometry", out _));
        Assert.True(document.GetProperty("edges")[0].GetProperty("props").GetProperty("activate").GetBoolean());
        Assert.True(document.GetProperty("edges")[1].GetProperty("props").GetProperty("deactivate").GetBoolean());
    }

    [Fact]
    public async Task A_fenced_crlf_sequence_diagram_imports_and_the_result_is_a_document_the_store_accepts()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string fenced = "```mermaid\r\n" + SequenceSource.Replace("\n", "\r\n", StringComparison.Ordinal) + "```\r\n";

        HttpResponseMessage imported = await client.PostAsync("/api/import/mermaid?type=sequence",
            new StringContent(fenced, Encoding.UTF8, "text/plain"), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await imported.Content.ReadAsStringAsync(CancellationToken.None));

        HttpResponseMessage saved = await client.PostAsync("/api/documents",
            new StringContent(body.RootElement.GetProperty("document").GetRawText(), Encoding.UTF8, "application/json"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        using JsonDocument stored = JsonDocument.Parse(await saved.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal("sequence", stored.RootElement.GetProperty("type").GetString());
        Assert.Equal(2, stored.RootElement.GetProperty("edges").GetArrayLength());
    }

    [Theory]
    [InlineData("flowchart")]
    [InlineData("architecture")]
    [InlineData("freeform")]
    public async Task A_sequence_diagram_asked_for_as_another_type_is_400_naming_the_mismatch(string type)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync($"/api/import/mermaid?type={type}",
            new StringContent(SequenceSource, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken.None);
        Assert.Contains("sequence", problem?.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_flowchart_asked_for_as_a_sequence_is_400_and_says_it_is_not_a_sequenceDiagram()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid?type=sequence",
            new StringContent("flowchart TD\n  A --> B\n", Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken.None);
        Assert.Contains("sequenceDiagram", problem?.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_input_is_200_with_warnings_not_500()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        const string mermaid = "flowchart TD\n  A[unclosed --> B\n  --> \n  C ==> D\n  style A fill:#f9f\n";

        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid",
            new StringContent(mermaid, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(await response.Content.ReadAsStringAsync(CancellationToken.None), DocumentJson.Options);
        Assert.NotNull(body);
        Assert.Contains(body.Warnings, w => w.Code == ImportWarningCodes.ParseError);
        Assert.Contains(body.Warnings, w => w.Code == ImportWarningCodes.UnsupportedSyntax);
        Assert.Contains(body.Document.Edges, e => e.From.Node == "n_C" && e.To.Node == "n_D");
    }

    [Fact]
    public async Task Imports_dsl_and_returns_document_json()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        const string dsl = "draughtsman 1\nid doc_x\ntype architecture\ntitle \"X\"\nmeta created=2026-09-20T22:00:00.000Z updated=2026-09-20T22:00:00.000Z by=eric\n\nnode n_a service \"A\" @ 0,0 100x60\nnode n_b service \"B\" @ 200,0 100x60\n\nedge e_1 flow n_a -> n_b\n";

        HttpResponseMessage response = await client.PostAsync("/api/import/dsl",
            new StringContent(dsl, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(await response.Content.ReadAsStringAsync(CancellationToken.None), DocumentJson.Options);
        Assert.NotNull(body);
        Assert.Equal("X", body.Document.Title);
        Assert.Equal(2, body.Document.Nodes.Count);
        Assert.Single(body.Document.Edges);
        Assert.NotNull(body.Document.Nodes[0].Geometry);
    }

    [Fact]
    public async Task Importing_dsl_with_newId_gives_each_import_a_fresh_id_so_the_same_title_can_be_created_twice()
    {
        // QA-09: without an id line the id is the slug of the title, so a second document with that title answers 409.
        HttpClient client = await _factory.CreateAuthenticatedClient();
        const string dsl = "draughtsman 1\ntype architecture\ntitle \"Same title\"\nmeta created=2026-09-20T22:00:00.000Z updated=2026-09-20T22:00:00.000Z by=eric\n\nnode n_a service \"A\" @ 0,0 100x60\n";

        async Task<Document> Import(string query)
        {
            HttpResponseMessage response = await client.PostAsync("/api/import/dsl" + query, new StringContent(dsl, Encoding.UTF8, "text/plain"), CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonSerializer.Deserialize<ImportResult>(await response.Content.ReadAsStringAsync(CancellationToken.None), DocumentJson.Options)!.Document;
        }

        Document plain = await Import("");
        Assert.Equal("doc_same_title", plain.Id); // the default stays lossless: export then import gives the same id back

        Document first = await Import("?newId=true");
        Document second = await Import("?newId=true");
        Assert.Matches("^doc_[0-9a-f]{32}$", first.Id);
        Assert.NotEqual(first.Id, second.Id);

        foreach (Document document in new[] { first, second })
        {
            HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", document, DocumentJson.Options, CancellationToken.None);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
    }

    [Fact]
    public async Task Creating_the_same_dsl_import_twice_is_a_409_that_says_how_to_avoid_it()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        const string dsl = "draughtsman 1\ntype architecture\ntitle \"Twice over\"\nmeta created=2026-09-20T22:00:00.000Z updated=2026-09-20T22:00:00.000Z by=eric\n\nnode n_a service \"A\" @ 0,0 100x60\n";
        HttpResponseMessage imported = await client.PostAsync("/api/import/dsl", new StringContent(dsl, Encoding.UTF8, "text/plain"), CancellationToken.None);
        Document document = JsonSerializer.Deserialize<ImportResult>(await imported.Content.ReadAsStringAsync(CancellationToken.None), DocumentJson.Options)!.Document;

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/documents", document, DocumentJson.Options, CancellationToken.None)).StatusCode);
        HttpResponseMessage again = await client.PostAsJsonAsync("/api/documents", document, DocumentJson.Options, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("newId=true", await again.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Dsl_syntax_error_is_422_with_line_and_column()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        const string dsl = "draughtsman 1\nid doc_x\ntype architecture\ntitle \"X\"\nmeta created=2026-09-20T22:00:00.000Z updated=2026-09-20T22:00:00.000Z by=eric\n\nnode n_a bogus kind here\n";

        HttpResponseMessage response = await client.PostAsync("/api/import/dsl",
            new StringContent(dsl, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.True(problem.RootElement.TryGetProperty("line", out _));
        Assert.True(problem.RootElement.TryGetProperty("column", out _));
        Assert.True(problem.RootElement.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task Dsl_lint_warning_message_does_not_repeat_its_position()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        const string dsl = "draughtsman 1\nid doc_x\ntype architecture\ntitle \"X\"\nmeta created=2026-09-20T22:00:00.000Z updated=2026-09-20T22:00:00.000Z by=eric\n\nnode n_a widget \"A\"\n";

        HttpResponseMessage response = await client.PostAsync("/api/import/dsl",
            new StringContent(dsl, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(await response.Content.ReadAsStringAsync(CancellationToken.None), DocumentJson.Options);
        Assert.NotNull(body);
        ImportWarning warning = Assert.Single(body.Warnings);
        Assert.Equal(ImportWarningCodes.DslLint, warning.Code);
        Assert.Equal(7, warning.Line);
        Assert.Contains("widget", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("line 7", warning.Message, StringComparison.Ordinal);
    }

    // 1e400 satisfies the grammar's number pattern but is out of range for a double, and JSON cannot write the
    // infinity it would become. The parser rejects it where it reads it, so it reaches the caller as an ordinary
    // syntax error with a position rather than crashing the response writer mid-body.
    [Theory]
    [InlineData("node n_a service \"A\" @ 1e400,0")]
    [InlineData("node n_a service \"A\" @ 0,0 100x60 rotation=1e400")]
    [InlineData("node n_a service \"A\" ports=[p1@1e400,0]")]
    [InlineData("node n_a service \"A\" style.strokeWidth=1e400")]
    [InlineData("edge e_1 flow n_a -> n_b routing=orthogonal waypoints=[1e400,0]")]
    [InlineData("edge e_1 flow n_a -> n_b style.strokeWidth=1e400")]
    public async Task Dsl_number_out_of_range_is_422_rather_than_a_serialisation_failure(string line)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string dsl = "draughtsman 1\nid doc_x\ntype architecture\ntitle \"X\"\nmeta created=2026-09-20T22:00:00.000Z updated=2026-09-20T22:00:00.000Z by=eric\n\n"
            + (line.StartsWith("edge", StringComparison.Ordinal)
                ? "node n_a service \"A\"\nnode n_b service \"B\"\n\n" + line + "\n"
                : line + "\nnode n_b service \"B\"\n");

        HttpResponseMessage response = await client.PostAsync("/api/import/dsl",
            new StringContent(dsl, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Contains("in range", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.True(problem.RootElement.TryGetProperty("line", out _));
        Assert.True(problem.RootElement.TryGetProperty("column", out _));
    }

    // The companion of the theory above: the guard must reject the infinity, not the syntax that carries it.
    [Theory]
    [InlineData("node n_a service \"A\" @ 10,0")]
    [InlineData("node n_a service \"A\" @ 0,0 100x60 rotation=90")]
    [InlineData("node n_a service \"A\" ports=[p1@0.5,0]")]
    [InlineData("node n_a service \"A\" style.strokeWidth=2")]
    [InlineData("edge e_1 flow n_a -> n_b routing=orthogonal waypoints=[10,0]")]
    [InlineData("edge e_1 flow n_a -> n_b style.strokeWidth=2")]
    public async Task Dsl_same_lines_with_finite_numbers_still_import(string line)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string dsl = "draughtsman 1\nid doc_x\ntype architecture\ntitle \"X\"\nmeta created=2026-09-20T22:00:00.000Z updated=2026-09-20T22:00:00.000Z by=eric\n\n"
            + (line.StartsWith("edge", StringComparison.Ordinal)
                ? "node n_a service \"A\"\nnode n_b service \"B\"\n\n" + line + "\n"
                : line + "\nnode n_b service \"B\"\n");

        HttpResponseMessage response = await client.PostAsync("/api/import/dsl",
            new StringContent(dsl, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // The reported failure: a value the schema rejects came back as 200, and POST /api/documents then refused it.
    [Fact]
    public async Task Dsl_import_of_an_unknown_shape_warns_and_still_yields_a_storable_document()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        const string dsl = "draughtsman 1\ntype flowchart\ntitle \"Order flow\"\nnode n_a process \"A\" shape=circle\n";

        HttpResponseMessage response = await client.PostAsync("/api/import/dsl",
            new StringContent(dsl, Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync(CancellationToken.None);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(json, DocumentJson.Options);
        Assert.NotNull(body);
        ImportWarning warning = Assert.Single(body.Warnings);
        Assert.Equal(ImportWarningCodes.DslLint, warning.Code);
        Assert.Contains("circle", warning.Message, StringComparison.Ordinal);
        Assert.Equal(4, warning.Line);
        Assert.NotEqual("circle", body.Document.Nodes[0].Shape);

        using JsonDocument envelope = JsonDocument.Parse(json);
        string documentJson = envelope.RootElement.GetProperty("document").GetRawText();
        HttpResponseMessage saved = await client.PostAsync("/api/documents",
            new StringContent(documentJson, Encoding.UTF8, "application/json"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
    }

    [Fact]
    public async Task Dsl_empty_body_is_400()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/dsl",
            new StringContent("   \n", Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
