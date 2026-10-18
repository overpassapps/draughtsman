using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace Draughtsman.Server.Tests;

/// <summary>
/// AU-07: Kestrel's 30 MB default reached every route, anonymous ones included. Each route class now has its own
/// limit, enforced before a handler reads the body, for a declared Content-Length and for a chunked body alike.
/// Every assertion has its other direction: a body just inside the limit still gets through to the handler.
/// </summary>
public class RequestSizeLimitTests
{
    private const string SmallLimits = "limits:\n  maxRequestBodyBytes: 4096\n  maxDocumentBytes: 65536\nassets:\n  maxSizeBytes: 1024\n";

    [Fact]
    public async Task Anonymous_sign_in_refuses_a_body_over_the_auth_limit_and_reads_one_under_it()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage oversized = await client.PostAsync("/api/auth/login", Json(new string('a', 20 * 1024)));
        HttpResponseMessage ordinary = await client.PostAsJsonAsync("/api/auth/login", new { email = "nobody@example.com", password = "x" });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal("application/problem+json", oversized.Content.Headers.ContentType?.MediaType);
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, ordinary.StatusCode);
    }

    [Fact]
    public async Task A_chunked_body_with_no_declared_length_is_cut_off_at_the_limit_too()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage oversized = await client.PostAsync("/api/auth/login", Chunked(new string('a', 20 * 1024)));
        HttpResponseMessage ordinary = await client.PostAsync("/api/auth/login", Chunked("{\"email\":\"nobody@example.com\",\"password\":\"x\"}"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, ordinary.StatusCode);
    }

    [Fact]
    public async Task A_document_route_uses_the_document_limit_and_other_routes_the_default()
    {
        using var factory = new ConfiguredServerFactory(SmallLimits);
        HttpClient client = await factory.CreateAuthenticatedClient();

        // 10 KiB: over the 4 KiB default, under the 64 KiB document limit.
        string tenKiB = System.Text.Json.JsonSerializer.Serialize(new { title = new string('t', 10 * 1024), type = "architecture" });
        HttpResponseMessage document = await client.PostAsync("/api/documents", new StringContent(tenKiB, Encoding.UTF8, "application/json"));
        HttpResponseMessage elsewhere = await client.PostAsync("/api/tokens", new StringContent(tenKiB, Encoding.UTF8, "application/json"));
        HttpResponseMessage hugeDocument = await client.PostAsync("/api/documents", new StringContent(new string('x', 70 * 1024), Encoding.UTF8, "application/json"));

        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, document.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, hugeDocument.StatusCode);
    }

    [Fact]
    public async Task A_handler_that_reads_the_body_itself_is_stopped_on_a_chunked_overrun()
    {
        using var factory = new ConfiguredServerFactory(SmallLimits);
        HttpClient client = await factory.CreateAuthenticatedClient();

        // /api/export/dsl parses Request.Body in the handler rather than binding it.
        HttpResponseMessage oversized = await client.PostAsync("/api/export/dsl", Chunked("{\"pad\":\"" + new string('p', 70 * 1024) + "\"}"));
        HttpResponseMessage small = await client.PostAsync("/api/export/dsl", Chunked("{}"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, small.StatusCode);
    }

    [Fact]
    public async Task An_upload_is_limited_to_the_asset_limit_plus_multipart_framing()
    {
        using var factory = new ConfiguredServerFactory(SmallLimits);
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage huge = await client.PostAsync("/api/assets", Upload(new byte[200 * 1024]));
        HttpResponseMessage small = await client.PostAsync("/api/assets", Upload(new byte[100]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);
        // The route's own limit answered, before the form was read; AssetStore's "File too large" is the later check.
        Assert.Contains("Request body too large", await huge.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // 100 bytes of zeros is not an image: it got as far as the content check, which is 415, not 413.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, small.StatusCode);
    }

    /// <summary>QA P16: the refusal says what a person can do about it, with the limit the Branding page promises, not a byte count of framing.</summary>
    [Fact]
    public async Task An_oversized_branding_logo_is_refused_in_words_that_name_the_real_limit()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[700 * 1024]), "file", "logo.png");
        HttpResponseMessage oversized = await client.PostAsync("/api/admin/branding/logo", form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        string body = await oversized.Content.ReadAsStringAsync();
        Assert.Contains("512 KiB", body, StringComparison.Ordinal);
        Assert.DoesNotContain("589,824", body, StringComparison.Ordinal);
        Assert.DoesNotContain("body for this route", body, StringComparison.Ordinal);

        // The other direction: the same words are not used for a route they do not describe.
        HttpResponseMessage login = await client.PostAsync("/api/auth/login", Json(new string('a', 20 * 1024)));
        string loginBody = await login.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, login.StatusCode);
        Assert.DoesNotContain("branding image", loginBody, StringComparison.Ordinal);
        Assert.Contains("16 KiB", loginBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_routes_have_a_byte_limit_above_the_character_cap_the_handler_enforces()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        // One character past the importers' own cap is the handler's 413; 4 MB is over the route's byte limit, refused before it.
        HttpResponseMessage justOverTheCharacterCap = await client.PostAsync("/api/import/dsl", new StringContent(new string('a', 1_000_001), Encoding.UTF8, "text/plain"));
        HttpResponseMessage overTheByteLimit = await client.PostAsync("/api/import/dsl", new StringContent(new string('a', 4_000_000), Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, justOverTheCharacterCap.StatusCode);
        HttpResponseMessage atTheCap = await client.PostAsync("/api/import/dsl", new StringContent(new string('a', 1_000_000), Encoding.UTF8, "text/plain"));
        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, atTheCap.StatusCode);
        Assert.Contains("Source too large", await justOverTheCharacterCap.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, overTheByteLimit.StatusCode);
        Assert.Contains("Request body too large", await overTheByteLimit.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_draw_io_import_has_the_same_limits_for_a_raw_body_and_for_a_multipart_file()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage rawJustOver = await client.PostAsync("/api/import/drawio", new StringContent(new string('a', 1_000_001), Encoding.UTF8, "text/plain"));
        HttpResponseMessage rawOverTheByteLimit = await client.PostAsync("/api/import/drawio", new StringContent(new string('a', 4_000_000), Encoding.UTF8, "text/plain"));
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(new string('a', 1_000_001))), "file", "big.drawio" } };
        HttpResponseMessage fileJustOver = await client.PostAsync("/api/import/drawio", form);
        using var bigForm = new MultipartFormDataContent { { new ByteArrayContent(new byte[4_000_000]), "file", "huge.drawio" } };
        HttpResponseMessage fileOverTheByteLimit = await client.PostAsync("/api/import/drawio", bigForm);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rawJustOver.StatusCode);
        Assert.Contains("Source too large", await rawJustOver.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rawOverTheByteLimit.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, fileJustOver.StatusCode);
        Assert.Contains("Source too large", await fileJustOver.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, fileOverTheByteLimit.StatusCode);
    }

    [Fact]
    public async Task The_visio_import_has_the_same_limit_for_a_raw_body_and_for_a_multipart_file()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        using var rawJustOver = new ByteArrayContent(new byte[3_000_001]);
        rawJustOver.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var rawWayOver = new ByteArrayContent(new byte[4_000_000]);
        rawWayOver.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[3_000_001]), "file", "big.vsdx" } };
        using var bigForm = new MultipartFormDataContent { { new ByteArrayContent(new byte[4_000_000]), "file", "huge.vsdx" } };

        HttpResponseMessage rawResponse = await client.PostAsync("/api/import/visio", rawJustOver);
        HttpResponseMessage rawFar = await client.PostAsync("/api/import/visio", rawWayOver);
        HttpResponseMessage fileResponse = await client.PostAsync("/api/import/visio", form);
        HttpResponseMessage fileFar = await client.PostAsync("/api/import/visio", bigForm);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rawResponse.StatusCode);
        Assert.Contains("Source too large", await rawResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rawFar.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, fileResponse.StatusCode);
        Assert.Contains("Source too large", await fileResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, fileFar.StatusCode);
    }

    private static StringContent Json(string padding)
    {
        return new StringContent("{\"pad\":\"" + padding + "\"}", Encoding.UTF8, "application/json");
    }

    private static StringContent Json(object value)
    {
        return new StringContent(System.Text.Json.JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    /// <summary>A body with no Content-Length: HttpClient sends a stream of unknown length chunked.</summary>
    private static StreamContent Chunked(string text)
    {
        var content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(text)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static MultipartFormDataContent Upload(byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "picture.bin");
        return form;
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
