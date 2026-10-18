using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Draughtsman.Core.Import;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// <c>POST /api/import/visio</c> through the real server: a raw body and a multipart file read the same, the document comes back placed (no layout), a page
/// of a multi-page file is chosen by number, and the source is refused, in words, when it is untrusted in ways a Visio file never is.
/// </summary>
public class VisioImportEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public VisioImportEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    private static ByteArrayContent Bytes(byte[] file, string mediaType = "application/octet-stream")
    {
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    private static async Task<ImportResult> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ImportResult? body = JsonSerializer.Deserialize<ImportResult>(await response.Content.ReadAsStringAsync(CancellationToken.None), DocumentJson.Options);
        return body ?? throw new InvalidOperationException("The response was not an import result.");
    }

    private static async Task<string> Detail(HttpResponseMessage response)
    {
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        return problem.RootElement.GetProperty("detail").GetString() ?? string.Empty;
    }

    [Fact]
    public async Task A_raw_body_imports_with_its_positions_and_is_not_laid_out()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/visio", Bytes(VisioFixtures.Shapes()), CancellationToken.None);

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        ImportResult result = await Read(response);
        Assert.Equal("Shapes", result.Document.Title);
        Assert.Equal(10, result.Document.Nodes.Count);
        Node box = result.Document.Nodes.Single(n => n.Id == "n_1");
        Assert.Equal(new Geometry(125, 62.5, 150, 75), box.Geometry);
        Assert.Equal("visio-import", result.Document.Meta.CreatedBy);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.KindGuessed && w.ElementId == "n_3");
    }

    [Fact]
    public async Task The_wire_shape_is_camel_case_and_lists_the_pages()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/visio", Bytes(VisioFixtures.MultiPage()), CancellationToken.None);

        using JsonDocument raw = JsonDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));
        JsonElement pages = raw.RootElement.GetProperty("pages");
        Assert.Equal(3, pages.GetArrayLength());
        Assert.Equal("Overview", pages[0].GetProperty("name").GetString());
        Assert.True(pages[0].GetProperty("selected").GetBoolean());
        Assert.False(raw.RootElement.TryGetProperty("direction", out _), "a drawing has no layout direction");
        Assert.Equal("page-selected", raw.RootElement.GetProperty("warnings")[0].GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("application/vnd.ms-visio.drawing")]
    [InlineData("application/vnd.visio")]
    [InlineData("application/zip")]
    [InlineData("application/x-zip-compressed")]
    public async Task Whatever_the_file_was_labelled_as_it_is_read(string mediaType)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/visio", Bytes(VisioFixtures.MasterLess(), mediaType), CancellationToken.None);

        ImportResult result = await Read(response);
        Assert.Equal(["n_1", "n_2"], result.Document.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task A_multipart_file_is_read_the_same_as_the_raw_body()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        using var form = new MultipartFormDataContent { { Bytes(VisioFixtures.Group()), "anyname", "group.vsdx" } };

        ImportResult upload = await Read(await client.PostAsync("/api/import/visio", form, CancellationToken.None));
        ImportResult raw = await Read(await client.PostAsync("/api/import/visio", Bytes(VisioFixtures.Group()), CancellationToken.None));

        Assert.Equal(raw.Document.Title, upload.Document.Title);
        Assert.Equal(DocumentJson.Serialize(raw.Document with { Id = "x", Meta = upload.Document.Meta }), DocumentJson.Serialize(upload.Document with { Id = "x" }));
        Assert.Equal(raw.Warnings, upload.Warnings);
        Assert.Contains(upload.Document.Nodes, n => n.Id == "c_1" && n.Kind == "container");
    }

    [Fact]
    public async Task A_multipart_body_with_no_file_is_400_and_an_empty_file_is_400()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        using var noFile = new MultipartFormDataContent { { new StringContent("just a field"), "note" } };
        using var emptyFile = new MultipartFormDataContent { { new ByteArrayContent([]), "file", "empty.vsdx" } };

        HttpResponseMessage none = await client.PostAsync("/api/import/visio", noFile, CancellationToken.None);
        HttpResponseMessage empty = await client.PostAsync("/api/import/visio", emptyFile, CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("no file", await none.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task The_type_chooses_the_vocabulary_the_shapes_are_mapped_into()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        ImportResult flowchart = await Read(await client.PostAsync("/api/import/visio", Bytes(VisioFixtures.Shapes()), CancellationToken.None));
        ImportResult architecture = await Read(await client.PostAsync("/api/import/visio?type=Architecture", Bytes(VisioFixtures.Shapes()), CancellationToken.None));
        ImportResult freeform = await Read(await client.PostAsync("/api/import/visio?type=freeform", Bytes(VisioFixtures.Shapes()), CancellationToken.None));

        Assert.Equal(("flowchart", "decision"), (flowchart.Document.Type.ToString().ToLowerInvariant(), flowchart.Document.Nodes.Single(n => n.Id == "n_4").Kind));
        Assert.Equal(("architecture", "service"), (architecture.Document.Type.ToString().ToLowerInvariant(), architecture.Document.Nodes.Single(n => n.Id == "n_4").Kind));
        Assert.Equal(("freeform", "shape"), (freeform.Document.Type.ToString().ToLowerInvariant(), freeform.Document.Nodes.Single(n => n.Id == "n_4").Kind));
        Assert.DoesNotContain(freeform.Warnings, w => w.Code == ImportWarningCodes.KindGuessed);
    }

    [Theory]
    [InlineData("?type=sequence", "sequenceDiagram")]
    [InlineData("?type=erd", "'erd'")]
    [InlineData("?type=99", "'99'")]
    public async Task A_type_a_drawing_cannot_be_made_into_is_400_and_says_what_to_use(string query, string expected)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/visio" + query, Bytes(VisioFixtures.Shapes()), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_is_chosen_by_number_and_the_warning_says_which_was_imported()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        ImportResult second = await Read(await client.PostAsync("/api/import/visio?page=2&title=Mine", Bytes(VisioFixtures.MultiPage()), CancellationToken.None));

        Assert.Equal("Mine", second.Document.Title);
        Assert.Equal("Only on page two", Assert.Single(second.Document.Nodes).Label);
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

        HttpResponseMessage response = await client.PostAsync("/api/import/visio" + query, Bytes(VisioFixtures.MultiPage()), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_it_returns_can_be_saved_as_a_document_and_read_back_in_place()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        ImportResult result = await Read(await client.PostAsync("/api/import/visio", Bytes(VisioFixtures.Connectors()), CancellationToken.None));
        Document document = result.Document with { Id = $"doc_visio_{Guid.NewGuid():N}" };

        HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", document, DocumentJson.Options, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        HttpResponseMessage fetched = await client.GetAsync($"/api/documents/{document.Id}", CancellationToken.None);
        Document stored = DocumentJson.Deserialize(await fetched.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(document.Nodes.Select(n => (n.Id, n.Geometry)), stored.Nodes.Select(n => (n.Id, n.Geometry)));
        Assert.Equal(document.Edges.Count, stored.Edges.Count);
    }

    [Fact]
    public async Task An_empty_body_is_400_and_a_file_over_the_limit_is_413()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage empty = await client.PostAsync("/api/import/visio", Bytes([]), CancellationToken.None);
        HttpResponseMessage justOver = await client.PostAsync("/api/import/visio", Bytes(new byte[3_000_001]), CancellationToken.None);
        HttpResponseMessage wayOver = await client.PostAsync("/api/import/visio", Bytes(new byte[4_000_000]), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, justOver.StatusCode);
        Assert.Contains("Source too large", await justOver.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, wayOver.StatusCode);
    }

    [Fact]
    public async Task A_body_sent_without_a_length_that_turns_out_too_large_is_413_and_is_not_read_to_its_end()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/import/visio") { Content = new ChunkedContent(3_000_500) };
        request.Headers.TransferEncodingChunked = true;

        HttpResponseMessage response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>A body with no known length, so it is sent chunked and nothing can be refused from a Content-Length.</summary>
    private sealed class ChunkedContent : HttpContent
    {
        private readonly int _length;

        public ChunkedContent(int length)
        {
            _length = length;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            byte[] chunk = new byte[64 * 1024];
            for (int sent = 0; sent < _length; sent += chunk.Length)
            {
                await stream.WriteAsync(chunk.AsMemory(0, Math.Min(chunk.Length, _length - sent)), CancellationToken.None);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task A_content_length_over_the_limit_is_refused_before_the_body_is_read()
    {
        var counter = new BodyCounter();
        using WebApplicationFactory<Program> counting = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter>(new CountBodyFilter(counter))));
        HttpClient client = await counting.CreateAuthenticatedClient();

        // Just over the endpoint's limit and under the request-size middleware's, so it is the endpoint that has to say no, from the header alone.
        HttpResponseMessage response = await client.PostAsync("/api/import/visio", Bytes(new byte[3_000_001]), CancellationToken.None);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(counter.Bytes < 1_000, $"{counter.Bytes:N0} bytes of a body the header already said was too large were read");
    }

    private sealed class BodyCounter
    {
        public long Bytes;
    }

    /// <summary>Puts a counting stream under <c>/api/import/visio</c>'s request body, before anything else sees it.</summary>
    private sealed class CountBodyFilter(BodyCounter counter) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    if (context.Request.Path.StartsWithSegments("/api/import/visio", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Request.Body = new CountingStream(context.Request.Body, counter);
                    }

                    await nextMiddleware();
                });
                next(app);
            };
        }
    }

    private sealed class CountingStream(Stream inner, BodyCounter counter) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            Interlocked.Add(ref counter.Bytes, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer, cancellationToken);
            Interlocked.Add(ref counter.Bytes, read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task An_old_binary_visio_file_is_a_422_that_says_to_save_as_vsdx()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        byte[] vsd = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];

        HttpResponseMessage response = await client.PostAsync("/api/import/visio", Bytes(vsd), CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        string detail = await Detail(response);
        Assert.Contains(".vsd", detail, StringComparison.Ordinal);
        Assert.Contains("Save As", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<mxfile><diagram name='x'/></mxfile>", "Import draw.io")]
    [InlineData("not a diagram at all", "not a ZIP package")]
    public async Task A_source_that_is_not_a_vsdx_is_a_422_that_says_so(string source, string expected)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/visio", Bytes(Encoding.UTF8.GetBytes(source)), CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(expected, await Detail(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_billion_laughs_part_is_refused_at_once_and_an_external_entity_is_never_fetched()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        List<KeyValuePair<string, byte[]>> parts = new VisioDocBuilder().Page("P", VisioDocBuilder.Shape(1, 2, 6, 1, 1)).Parts();
        string bomb = "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol1 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\"><!ENTITY lol2 \"&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;&lol1;\"><!ENTITY lol3 \"&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;\">]><PageContents a=\"&lol3;\"/>";
        string xxe = $"<!DOCTYPE x [<!ENTITY e SYSTEM \"http://127.0.0.1:{port}/steal\">]><PageContents><Shapes><Shape ID='1'><Text>&e;</Text></Shape></Shapes></PageContents>";

        HttpResponseMessage bombed = await client.PostAsync("/api/import/visio", Bytes(VisioDocBuilder.Replace(parts, "visio/pages/page1.xml", bomb)), CancellationToken.None);
        HttpResponseMessage external = await client.PostAsync("/api/import/visio", Bytes(VisioDocBuilder.Replace(parts, "visio/pages/page1.xml", xxe)), CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, bombed.StatusCode);
        Assert.Contains("DOCTYPE", await Detail(bombed), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, external.StatusCode);
        Assert.False(listener.Pending(), "the server connected to an address named in the uploaded file");
    }

    [Fact]
    public async Task A_part_that_inflates_past_the_limit_is_refused_though_the_upload_is_small()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        List<KeyValuePair<string, byte[]>> parts = new VisioDocBuilder().Page("P", VisioDocBuilder.Shape(1, 2, 6, 1, 1)).Parts();
        byte[] bomb = VisioDocBuilder.ZipPadded(parts, new Dictionary<string, (string Prefix, long PadBytes, string Suffix)> { ["visio/pages/page1.xml"] = ("<PageContents><!--", 65L * 1024 * 1024, "--></PageContents>") });

        HttpResponseMessage response = await client.PostAsync("/api/import/visio", Bytes(bomb), CancellationToken.None);

        Assert.True(bomb.Length < 200_000, $"the upload is {bomb.Length:N0} bytes");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("claims to be", await Detail(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_size_header_that_lies_and_a_name_that_climbs_out_of_the_folder_are_422_not_a_crash()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        byte[] lying = ZipTamper.ClaimUncompressedSize(VisioFixtures.Shapes(), "visio/pages/page1.xml", 5_000_000);
        List<KeyValuePair<string, byte[]>> parts = new VisioDocBuilder().Page("P", VisioDocBuilder.Shape(1, 2, 6, 1, 1)).Parts();
        parts.Add(new KeyValuePair<string, byte[]>("../../evil.xml", Encoding.UTF8.GetBytes("<x/>")));

        HttpResponseMessage lied = await client.PostAsync("/api/import/visio", Bytes(lying), CancellationToken.None);
        HttpResponseMessage slipped = await client.PostAsync("/api/import/visio", Bytes(VisioDocBuilder.Zip(parts)), CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, lied.StatusCode);
        Assert.Contains("lying", await Detail(lied), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, slipped.StatusCode);
        Assert.Contains("outside its own folder", await Detail(slipped), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_truncated_file_is_a_422_in_words()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        byte[] good = VisioFixtures.Shapes();

        HttpResponseMessage response = await client.PostAsync("/api/import/visio", Bytes(good[..(good.Length / 2)]), CancellationToken.None);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("damaged or cut short", await Detail(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        using HttpClient anonymous = _factory.CreateClient();

        HttpResponseMessage response = await anonymous.PostAsync("/api/import/visio", Bytes(VisioFixtures.Shapes()), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
