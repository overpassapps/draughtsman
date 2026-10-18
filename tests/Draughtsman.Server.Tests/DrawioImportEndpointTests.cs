using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Draughtsman.Core.Import;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Server.Tests;

/// <summary>
/// <c>POST /api/import/drawio</c> through the real server: a raw body and a multipart file read the same, the document comes back placed (no
/// layout), a page of a multi-page file is chosen by number, and the source is refused, in words, when it is untrusted in ways draw.io files never are.
/// </summary>
public class DrawioImportEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public DrawioImportEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    private static StringContent Xml(string text, string mediaType = "text/plain")
    {
        return new StringContent(text, Encoding.UTF8, mediaType);
    }

    private static async Task<ImportResult> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(await response.Content.ReadAsStringAsync(CancellationToken.None), DocumentJson.Options);
        return body ?? throw new InvalidOperationException("The response was not an import result.");
    }

    [Fact]
    public async Task A_raw_body_imports_with_its_positions_and_is_not_laid_out()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/drawio", Xml(DrawioFixtures.Read("shapes.drawio")), CancellationToken.None);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        ImportResult result = await Read(response);
        Assert.Equal("Shapes", result.Document.Title);
        Assert.Equal(19, result.Document.Nodes.Count);
        Node box = result.Document.Nodes.Single(n => n.Id == "n_s1");
        Assert.Equal(new Geometry(30, 30, 120, 60), box.Geometry);
        Assert.Equal("drawio-import", result.Document.Meta.CreatedBy);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.KindGuessed && w.ElementId == "n_s3");
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.UnsupportedSyntax && w.ElementId == "n_s11");
    }

    [Fact]
    public async Task The_wire_shape_is_camel_case_and_omits_what_is_empty()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/drawio", Xml(DrawioFixtures.Read("multipage.drawio")), CancellationToken.None);

        using JsonDocument raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        JsonElement pages = raw.RootElement.GetProperty("pages");
        Assert.Equal(3, pages.GetArrayLength());
        Assert.Equal("Overview", pages[0].GetProperty("name").GetString());
        Assert.Equal(1, pages[0].GetProperty("index").GetInt32());
        Assert.True(pages[0].GetProperty("selected").GetBoolean());
        Assert.False(pages[1].GetProperty("selected").GetBoolean());
        Assert.False(raw.RootElement.TryGetProperty("direction", out _), "a drawing has no layout direction");
        Assert.Equal("page-selected", raw.RootElement.GetProperty("warnings")[0].GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("text/xml")]
    [InlineData("application/vnd.jgraph.mxfile")]
    [InlineData("application/octet-stream")]
    public async Task Whatever_the_file_was_labelled_as_it_is_read(string mediaType)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/drawio", Xml(DrawioFixtures.Read("bare-model.xml"), mediaType), CancellationToken.None);

        ImportResult result = await Read(response);
        Assert.Equal(["n_x1", "n_x2"], result.Document.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task A_compressed_file_is_read()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        ImportResult result = await Read(await client.PostAsync("/api/import/drawio", Xml(DrawioFixtures.Read("compressed.drawio")), CancellationToken.None));

        Assert.Equal("Order flow", result.Document.Title);
        Assert.Equal(5, result.Document.Nodes.Count);
        Assert.Equal(5, result.Document.Edges.Count);
    }

    [Fact]
    public async Task A_multipart_file_is_read_the_same_as_the_raw_body()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(DrawioFixtures.Read("containers.drawio"))), "anyname", "containers.drawio" } };

        ImportResult upload = await Read(await client.PostAsync("/api/import/drawio", form, CancellationToken.None));
        ImportResult raw = await Read(await client.PostAsync("/api/import/drawio", Xml(DrawioFixtures.Read("containers.drawio")), CancellationToken.None));

        Assert.Equal(raw.Document.Title, upload.Document.Title);
        Assert.Equal(DocumentJson.Serialize(raw.Document with { Id = "x", Meta = upload.Document.Meta }), DocumentJson.Serialize(upload.Document with { Id = "x" }));
        Assert.Equal(raw.Warnings, upload.Warnings);
        Assert.Contains(upload.Document.Nodes, n => n.Id == "c_c1" && n.Kind == "container");
    }

    [Fact]
    public async Task A_multipart_body_with_a_byte_order_mark_is_read()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(DrawioFixtures.Read("bare-model.xml"))];
        using var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", "bom.xml" } };

        ImportResult result = await Read(await client.PostAsync("/api/import/drawio", form, CancellationToken.None));

        Assert.Equal(2, result.Document.Nodes.Count);
    }

    [Fact]
    public async Task A_multipart_body_with_no_file_is_400_and_an_empty_file_is_400()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        using var noFile = new MultipartFormDataContent { { new StringContent("just a field"), "note" } };
        using var emptyFile = new MultipartFormDataContent { { new ByteArrayContent([]), "file", "empty.drawio" } };

        HttpResponseMessage none = await client.PostAsync("/api/import/drawio", noFile, CancellationToken.None);
        HttpResponseMessage empty = await client.PostAsync("/api/import/drawio", emptyFile, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("no file", await none.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task The_type_chooses_the_vocabulary_the_shapes_are_mapped_into()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string source = DrawioFixtures.Read("shapes.drawio");

        ImportResult flowchart = await Read(await client.PostAsync("/api/import/drawio", Xml(source), CancellationToken.None));
        ImportResult architecture = await Read(await client.PostAsync("/api/import/drawio?type=Architecture", Xml(source), CancellationToken.None));
        ImportResult freeform = await Read(await client.PostAsync("/api/import/drawio?type=freeform", Xml(source), CancellationToken.None));

        Assert.Equal(("flowchart", "stored-data"), (flowchart.Document.Type.ToString().ToLowerInvariant(), flowchart.Document.Nodes.Single(n => n.Id == "n_s5").Kind));
        Assert.Equal(("architecture", "database"), (architecture.Document.Type.ToString().ToLowerInvariant(), architecture.Document.Nodes.Single(n => n.Id == "n_s5").Kind));
        Assert.Equal(("freeform", "shape"), (freeform.Document.Type.ToString().ToLowerInvariant(), freeform.Document.Nodes.Single(n => n.Id == "n_s5").Kind));
        Assert.DoesNotContain(freeform.Warnings, w => w.Code == ImportWarningCodes.KindGuessed);
    }

    [Theory]
    [InlineData("?type=sequence", "sequenceDiagram")]
    [InlineData("?type=erd", "'erd'")]
    [InlineData("?type=99", "'99'")]
    public async Task A_type_a_drawing_cannot_be_made_into_is_400_and_says_what_to_use(string query, string expected)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/drawio" + query, Xml(DrawioFixtures.Read("bare-model.xml")), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_is_chosen_by_number_and_the_warning_says_which_was_imported()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string source = DrawioFixtures.Read("multipage.drawio");

        ImportResult second = await Read(await client.PostAsync("/api/import/drawio?page=2&title=Mine", Xml(source), CancellationToken.None));

        Assert.Equal("Mine", second.Document.Title);
        Assert.Equal(["n_d1", "n_d2", "n_d3"], second.Document.Nodes.Select(n => n.Id));
        Assert.Equal([false, true, false], second.Pages!.Select(p => p.Selected));
        Assert.Contains("Page 2", Assert.Single(second.Warnings, w => w.Code == ImportWarningCodes.PageSelected).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?page=4", "3 pages")]
    [InlineData("?page=0", "from 1")]
    [InlineData("?page=-2", "from 1")]
    [InlineData("?page=two", "from 1")]
    [InlineData("?page=99999999999", "from 1")]
    public async Task A_page_that_is_not_there_is_400_and_says_so(string query, string expected)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/drawio" + query, Xml(DrawioFixtures.Read("multipage.drawio")), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_it_returns_can_be_saved_as_a_document_and_read_back_in_place()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        ImportResult result = await Read(await client.PostAsync("/api/import/drawio", Xml(DrawioFixtures.Read("edges.drawio")), CancellationToken.None));
        Document document = result.Document with { Id = $"doc_drawio_{Guid.NewGuid():N}" };

        HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", document, DocumentJson.Options, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        HttpResponseMessage fetched = await client.GetAsync($"/api/documents/{document.Id}", CancellationToken.None);
        Document stored = DocumentJson.Deserialize(await fetched.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(document.Nodes.Select(n => (n.Id, n.Geometry)), stored.Nodes.Select(n => (n.Id, n.Geometry)));
        Assert.Equal(document.Edges.Count, stored.Edges.Count);
    }

    [Fact]
    public async Task An_empty_body_is_400_and_a_source_over_the_limit_is_413()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage empty = await client.PostAsync("/api/import/drawio", Xml("  \n"), CancellationToken.None);
        HttpResponseMessage huge = await client.PostAsync("/api/import/drawio", Xml("<mxfile>" + new string(' ', 1_000_001)), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
    }

    [Theory]
    [InlineData("graph TD\n  A --> B")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>")]
    [InlineData("<mxfile><diagram>")]
    [InlineData("not a diagram at all")]
    public async Task A_source_that_is_not_draw_io_is_a_422_that_says_so(string source)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/drawio", Xml(source), CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Contains("draw.io", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_billion_laughs_file_is_refused_at_once_and_an_external_entity_is_never_fetched()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string bomb = "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol1 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\"><!ENTITY lol2 \"&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;\"><!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">]><mxfile><diagram name=\"&lol3;\"/></mxfile>";
        string xxe = $"<!DOCTYPE x [<!ENTITY e SYSTEM \"http://127.0.0.1:{port}/steal\">]><mxfile><diagram name=\"&e;\"/></mxfile>";

        HttpResponseMessage bombed = await client.PostAsync("/api/import/drawio", Xml(bomb), CancellationToken.None);
        HttpResponseMessage external = await client.PostAsync("/api/import/drawio", Xml(xxe), CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, bombed.StatusCode);
        Assert.Contains("DOCTYPE", await bombed.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, external.StatusCode);
        Assert.False(listener.Pending(), "the server connected to an address named in the uploaded file");
    }

    [Fact]
    public async Task A_compressed_page_that_inflates_past_the_limit_is_refused_though_the_upload_is_small()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(Encoding.ASCII.GetBytes("%3CmxGraphModel%3E%3Croot%3E%3C%21--"));
            byte[] chunk = Encoding.ASCII.GetBytes(new string('a', 1024 * 1024));
            for (int i = 0; i < 64; i++)
            {
                deflate.Write(chunk);
            }

            deflate.Write(Encoding.ASCII.GetBytes("--%3E%3C/root%3E%3C/mxGraphModel%3E"));
        }

        string source = $"<mxfile><diagram name=\"bomb\">{Convert.ToBase64String(compressed.ToArray())}</diagram></mxfile>";
        HttpResponseMessage response = await client.PostAsync("/api/import/drawio", Xml(source), CancellationToken.None);

        Assert.True(source.Length < 200_000, $"the upload is {source.Length:N0} characters");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("expands", await response.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nesting_and_element_floods_are_422_not_a_crash()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string deep = "<mxfile><diagram name=\"d\">" + string.Concat(Enumerable.Repeat("<a>", 20_000)) + string.Concat(Enumerable.Repeat("</a>", 20_000)) + "</diagram></mxfile>";
        string wide = "<mxfile><diagram name=\"w\"><mxGraphModel><root>" + string.Concat(Enumerable.Repeat("<a/>", 160_000)) + "</root></mxGraphModel></diagram></mxfile>";

        HttpResponseMessage deepResponse = await client.PostAsync("/api/import/drawio", Xml(deep), CancellationToken.None);
        HttpResponseMessage wideResponse = await client.PostAsync("/api/import/drawio", Xml(wide), CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, deepResponse.StatusCode);
        Assert.Contains("nested", await deepResponse.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wideResponse.StatusCode);
        Assert.Contains("elements", await wideResponse.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        using HttpClient anonymous = _factory.CreateClient();

        HttpResponseMessage response = await anonymous.PostAsync("/api/import/drawio", Xml(DrawioFixtures.Read("bare-model.xml")), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
