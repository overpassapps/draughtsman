using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Import;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Draughtsman.Server.Tests;

public class SqlEndpointTests(DraughtsmanServerFactory factory) : IClassFixture<DraughtsmanServerFactory>
{
    private const string Schema = """
        SET ANSI_NULLS ON
        GO
        create table dbo.Customers (
            Id int identity(1,1) not null constraint PK_Customers primary key,
            Name nvarchar(100) not null
        )
        go
        create table [shop].[Orders] (
            Id int not null constraint PK_Orders primary key,
            CustomerId int not null constraint FK_Orders_Customers references dbo.Customers,
            SalesRepId int null constraint FK_Orders_Reps references dbo.Reps
        )
        go
        """;

    private static StringContent Text(string text)
    {
        return new StringContent(text, Encoding.UTF8, "text/plain");
    }

    [Fact]
    public async Task Imports_ddl_into_entities_and_relationships_with_warnings_and_saves_nothing()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        int before = await CountDocuments(client);

        using HttpResponseMessage response = await client.PostAsync("/api/import/sql?title=Shop", Text(Schema), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        string json = await response.Content.ReadAsStringAsync(CancellationToken.None);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(json, DocumentJson.Options);
        Assert.NotNull(body);
        Assert.Equal("Shop", body.Document.Title);
        Assert.Equal(new[] { "Customers", "shop.Orders" }, body.Document.Nodes.Select(n => n.Label));
        Assert.Equal("architecture", JsonNode.Parse(json)!["document"]!["type"]!.GetValue<string>());
        Edge edge = Assert.Single(body.Document.Edges);
        Assert.Equal("CustomerId", edge.Label);
        Assert.Contains(body.Warnings, w => w.Code == ImportWarningCodes.UnsupportedSyntax && w.Line == 1);
        Assert.Contains(body.Warnings, w => w.Code == ImportWarningCodes.MissingReference);
        Assert.False(JsonNode.Parse(json)!["document"]!["nodes"]![0]!.AsObject().ContainsKey("geometry"));
        Assert.Equal(before, await CountDocuments(client));
    }

    [Theory]
    [InlineData("?dialect=tsql")]
    [InlineData("?dialect=mssql")]
    [InlineData("?dialect=postgres")]
    [InlineData("?dialect=auto")]
    [InlineData("?type=freeform")]
    public async Task Dialect_and_type_hints_are_accepted(string query)
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync("/api/import/sql" + query, Text(Schema), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(await response.Content.ReadAsStringAsync(CancellationToken.None), DocumentJson.Options);
        Assert.Equal(2, body!.Document.Nodes.Count);
    }

    private const string TaggedSchema = """
        -- [NEW] Fresh.
        create table dbo.Customers (Id int not null primary key)
        go
        -- [LEGACY] Old.
        create table dbo.Reps (Id int not null primary key)
        go
        """;

    [Fact]
    public async Task Tags_colour_the_tables_and_add_a_legend_unless_switched_off()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();

        using HttpResponseMessage on = await client.PostAsync("/api/import/sql", Text(TaggedSchema), CancellationToken.None);
        using HttpResponseMessage off = await client.PostAsync("/api/import/sql?tags=off", Text(TaggedSchema), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        JsonArray tagged = JsonNode.Parse(await on.Content.ReadAsStringAsync(CancellationToken.None))!["document"]!["nodes"]!.AsArray();
        JsonArray plain = JsonNode.Parse(await off.Content.ReadAsStringAsync(CancellationToken.None))!["document"]!["nodes"]!.AsArray();
        Assert.Equal(2, plain.Count);
        Assert.All(plain, n => Assert.Null(n!["style"]));
        Assert.Equal(5, tagged.Count); // two tables, the legend and its two swatches
        Assert.Equal("NEW", tagged[0]!["props"]!["tag"]!.GetValue<string>());
        Assert.NotEqual(tagged[0]!["style"]!["fill"]!.GetValue<string>(), tagged[1]!["style"]!["fill"]!.GetValue<string>());
    }

    private const string ImpliedSchema = """
        CREATE TABLE dbo.UserTypes (Id int NOT NULL PRIMARY KEY);
        GO
        CREATE TABLE dbo.Users
        (
            Id     int NOT NULL PRIMARY KEY,
            TypeId int NOT NULL  -- -> UserTypes.Id (no FK)
        );
        GO
        """;

    [Fact]
    public async Task Implied_links_draw_a_dashed_edge_unless_switched_off()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();

        using HttpResponseMessage on = await client.PostAsync("/api/import/sql", Text(ImpliedSchema), CancellationToken.None);
        using HttpResponseMessage off = await client.PostAsync("/api/import/sql?implied=off", Text(ImpliedSchema), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        JsonNode edge = Assert.Single(JsonNode.Parse(await on.Content.ReadAsStringAsync(CancellationToken.None))!["document"]!["edges"]!.AsArray())!;
        Assert.Equal("TypeId (no FK)", edge["label"]!.GetValue<string>());
        Assert.Equal("dashed", edge["style"]!["dash"]!.GetValue<string>());
        Assert.Equal("col:TypeId", edge["from"]!["port"]!.GetValue<string>());
        Assert.Empty(JsonNode.Parse(await off.Content.ReadAsStringAsync(CancellationToken.None))!["document"]!["edges"]!.AsArray());
    }

    [Fact]
    public async Task An_unknown_implied_option_is_400()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync("/api/import/sql?implied=maybe", Text(ImpliedSchema), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_composite_key_with_a_foreign_key_is_a_weak_entity()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        const string sql = "CREATE TABLE P (Id int PRIMARY KEY); CREATE TABLE C (PId int NOT NULL REFERENCES P(Id), Seq int NOT NULL, PRIMARY KEY (PId, Seq));";

        using HttpResponseMessage response = await client.PostAsync("/api/import/sql", Text(sql), CancellationToken.None);

        JsonArray nodes = JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None))!["document"]!["nodes"]!.AsArray();
        Assert.Equal(new[] { "entity", "weak-entity" }, nodes.Select(n => n!["kind"]!.GetValue<string>()));
    }

    [Fact]
    public async Task An_unknown_tags_option_is_400()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync("/api/import/sql?tags=maybe", Text(TaggedSchema), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("?dialect=oracle")]
    [InlineData("?type=flowchart")]
    [InlineData("?type=erd")]
    public async Task Unusable_dialect_or_type_is_400(string query)
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync("/api/import/sql" + query, Text(Schema), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(CancellationToken.None);
        Assert.NotNull(problem?.Detail);
    }

    [Fact]
    public async Task Empty_body_is_400_and_oversized_body_is_413()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();

        using HttpResponseMessage empty = await client.PostAsync("/api/import/sql", Text("  \n"), CancellationToken.None);
        using HttpResponseMessage huge = await client.PostAsync("/api/import/sql", Text("-- " + new string('x', 1_000_001)), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
    }

    [Fact]
    public async Task Text_with_no_tables_is_200_with_an_empty_diagram_warning_and_garbage_is_never_500()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync("/api/import/sql", Text("select 1;\n)))(((;\ncreate table ("), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(await response.Content.ReadAsStringAsync(CancellationToken.None), DocumentJson.Options);
        Assert.Empty(body!.Document.Nodes);
        Assert.Contains(body.Warnings, w => w.Code == ImportWarningCodes.EmptyDiagram);
    }

    [Fact]
    public async Task Exports_an_imported_diagram_back_to_ddl_in_each_dialect_and_it_imports_again()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        using HttpResponseMessage imported = await client.PostAsync("/api/import/sql", Text(Schema), CancellationToken.None);
        string document = JsonNode.Parse(await imported.Content.ReadAsStringAsync(CancellationToken.None))!["document"]!.ToJsonString();

        foreach ((string dialect, string expected) in new[]
        {
            ("tsql", "create table [dbo].[Customers]"),
            ("postgres", "create table \"shop\".\"Orders\""),
            ("mysql", "create table `shop_Orders`"),
            ("sqlite", "create table \"Customers\""),
        })
        {
            using HttpResponseMessage response = await client.PostAsync($"/api/export/sql?dialect={dialect}", new StringContent(document, Encoding.UTF8, "application/json"), CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
            string sql = await response.Content.ReadAsStringAsync(CancellationToken.None);
            Assert.Contains(expected, sql, StringComparison.Ordinal);

            using HttpResponseMessage again = await client.PostAsync($"/api/import/sql?dialect={dialect}", Text(sql), CancellationToken.None);
            ImportResult? body = JsonSerializer.Deserialize<ImportResult>(await again.Content.ReadAsStringAsync(CancellationToken.None), DocumentJson.Options);
            Assert.Equal(2, body!.Document.Nodes.Count);
            Assert.Single(body.Document.Edges);
        }
    }

    [Fact]
    public async Task An_imported_schema_lays_out_through_the_sidecar_with_each_table_sized_from_its_columns()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        using HttpResponseMessage imported = await client.PostAsync("/api/import/sql", Text(Schema), CancellationToken.None);
        JsonNode document = JsonNode.Parse(await imported.Content.ReadAsStringAsync(CancellationToken.None))!["document"]!;

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/layout", document, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Document laidOut = DocumentJson.Deserialize((await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None)).GetProperty("document").GetRawText());
        Assert.All(laidOut.Nodes, n => Assert.True(n.Geometry is { W: > 0, H: > 0 }, n.Id));
        // Orders has three columns and Customers two, so the box heights differ; a default-sized box would not.
        Assert.True(laidOut.Nodes[1].Geometry!.H > laidOut.Nodes[0].Geometry!.H);
        Assert.All(laidOut.Edges, e => Assert.Equal(Draughtsman.Core.Enums.RoutingMode.Orthogonal, e.Routing?.Mode));
    }

    [Fact]
    public async Task Export_defaults_to_tsql()
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();
        using HttpResponseMessage imported = await client.PostAsync("/api/import/sql", Text(Schema), CancellationToken.None);
        string document = JsonNode.Parse(await imported.Content.ReadAsStringAsync(CancellationToken.None))!["document"]!.ToJsonString();

        using HttpResponseMessage response = await client.PostAsync("/api/export/sql", new StringContent(document, Encoding.UTF8, "application/json"), CancellationToken.None);

        Assert.Contains("create table [dbo].[Customers]", await response.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?dialect=oracle", "{}", HttpStatusCode.BadRequest)]
    [InlineData("?dialect=auto", "{}", HttpStatusCode.BadRequest)]
    [InlineData("", "{}", HttpStatusCode.UnprocessableEntity)]
    [InlineData("", "{", HttpStatusCode.BadRequest)]
    public async Task Export_refuses_a_bad_dialect_or_an_invalid_document(string query, string json, HttpStatusCode expected)
    {
        using HttpClient client = await factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsync("/api/export/sql" + query, new StringContent(json, Encoding.UTF8, "application/json"), CancellationToken.None);

        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<int> CountDocuments(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/documents", CancellationToken.None);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return body.RootElement.GetProperty("total").GetInt32();
    }
}
