using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Draughtsman.Core.Media;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The asset API (docs/api.md): upload by content, dedup by hash, serve with the right type and caching, and the
/// restrictive headers an SVG is served with. The small-limit factory proves the configured cap is enforced, not
/// only the default 10 MiB (which would make the oversize test read like a fixture against itself).
/// </summary>
public class AssetEndpointTests
{
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x11, 0x22];
    private static readonly byte[] GifBytes = [.. "GIF89a"u8, 0x01, 0x00, 0x01, 0x00];
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];
    private static readonly byte[] WebpBytes = [.. "RIFF"u8, 0x1A, 0x00, 0x00, 0x00, .. "WEBP"u8, 0x56, 0x50, 0x38, 0x20];
    private static readonly byte[] CleanSvg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\" fill=\"#c0392b\"/></svg>");

    public static TheoryData<byte[], string> AcceptedTypes => new()
    {
        { PngBytes, AssetTypes.Png },
        { GifBytes, AssetTypes.Gif },
        { JpegBytes, AssetTypes.Jpeg },
        { WebpBytes, AssetTypes.Webp },
        { CleanSvg, AssetTypes.Svg },
    };

    [Theory]
    [MemberData(nameof(AcceptedTypes))]
    public async Task Upload_accepts_each_allowed_type_and_serves_it_back_byte_for_byte(byte[] bytes, string expectedType)
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        UploadedAsset uploaded = await Upload(client, bytes, "picture.bin", "application/octet-stream");

        Assert.Equal(expectedType, uploaded.ContentType);
        Assert.Equal(bytes.Length, uploaded.Size);
        Assert.StartsWith("asset_", uploaded.Id, StringComparison.Ordinal);

        HttpResponseMessage served = await client.GetAsync($"/api/assets/{uploaded.Id}");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(expectedType, served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await served.Content.ReadAsByteArrayAsync());
        // Serialised by ASP.NET's CacheControl parser, which orders the directives max-age first; private keeps a
        // shared proxy from holding a signed-in user's image. The public/private half is asserted, not just max-age.
        Assert.Equal("max-age=31536000, private, immutable", served.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", Assert.Single(served.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Upload_rejects_a_text_file_renamed_as_an_image()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await RawUpload(client, Encoding.UTF8.GetBytes("%PDF-1.7 not an image at all"), "logo.png", "image/png");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("image", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upload_rejects_a_file_over_the_configured_limit()
    {
        using var factory = new SmallLimitFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await RawUpload(client, [.. PngBytes, .. new byte[128]], "big.png", "image/png");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("64", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uploading_the_same_bytes_twice_returns_the_same_asset()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        UploadedAsset first = await Upload(client, PngBytes, "one.png", "image/png");
        UploadedAsset second = await Upload(client, PngBytes, "two-different-name.png", "image/png");

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual("", first.Id);
    }

    [Fact]
    public async Task An_svg_is_served_with_a_restrictive_policy()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        UploadedAsset uploaded = await Upload(client, CleanSvg, "drawing.svg", "image/svg+xml");
        HttpResponseMessage served = await client.GetAsync($"/api/assets/{uploaded.Id}");

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/svg+xml", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(served.Headers.GetValues("X-Content-Type-Options")));
        // Scripts and external loads are off; the file is an image, never a document this origin runs.
        Assert.Equal(AssetEndpoints.SvgContentSecurityPolicy, Assert.Single(served.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal(CleanSvg, await served.Content.ReadAsByteArrayAsync());
    }

    // AU-17: an asset SVG passes the same allowlist a branding SVG does, CSS escapes decoded.
    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script><rect width=\"10\" height=\"10\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"><rect width=\"10\" height=\"10\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><foreignObject><body xmlns=\"http://www.w3.org/1999/xhtml\">x</body></foreignObject></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"https://evil.example/x.png\"/></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><style>rect{fill:u\\72l(https://evil.example/y)}</style><rect width=\"10\" height=\"10\"/></svg>")]
    public async Task A_hostile_svg_is_refused_at_upload_and_nothing_is_stored(string svg)
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await RawUpload(client, Encoding.UTF8.GetBytes(svg), "evil.svg", "image/svg+xml");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        ProblemDetails? problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("The SVG was refused", problem!.Detail, StringComparison.Ordinal);
        string assets = Path.Combine(factory.DataFolder, "assets");
        Assert.False(Directory.Exists(assets) && Directory.GetFiles(assets, "*", SearchOption.AllDirectories).Length > 0, "a refused SVG must not reach the disk");
    }

    [Fact]
    public async Task A_png_is_served_without_the_svg_policy()
    {
        // The policy is for the type that can carry script; a raster must not be handed the SVG-only header.
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        UploadedAsset uploaded = await Upload(client, PngBytes, "one.png", "image/png");
        HttpResponseMessage served = await client.GetAsync($"/api/assets/{uploaded.Id}");

        // The SPA-wide policy (SecurityHeadersMiddleware) is on it, but never the SVG-only sandbox.
        Assert.NotEqual(AssetEndpoints.SvgContentSecurityPolicy, Assert.Single(served.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task Unknown_asset_is_404()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync("/api/assets/asset_missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_non_multipart_upload_is_400()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient client = await factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/assets", new ByteArrayContent(PngBytes));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<UploadedAsset> Upload(HttpClient client, byte[] bytes, string fileName, string contentType)
    {
        HttpResponseMessage response = await RawUpload(client, bytes, fileName, contentType);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        return new UploadedAsset(
            root.GetProperty("id").GetString()!,
            root.GetProperty("contentType").GetString()!,
            root.GetProperty("size").GetInt64());
    }

    private static async Task<HttpResponseMessage> RawUpload(HttpClient client, byte[] bytes, string fileName, string contentType)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var form = new MultipartFormDataContent();
        form.Add(file, "file", fileName);
        return await client.PostAsync("/api/assets", form);
    }

    private sealed record UploadedAsset(string Id, string ContentType, long Size);

    /// <summary>A factory whose draughtsman.yaml caps uploads at 64 bytes, so the limit can be exercised cheaply.</summary>
    private sealed class SmallLimitFactory : DraughtsmanServerFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(Path.Combine(DataFolder, DraughtsmanConfig.FileName), "assets:\n  maxSizeBytes: 64\n");
        }
    }
}
