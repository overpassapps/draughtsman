using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The Draughtsman favicon and brand images the SPA ships in web/public are served by the existing static file handling
/// with the right media type and a day's cache, while the shell and the hashed bundles are not given that policy. A stand-in
/// web build holds the files, since a real one is not guaranteed to exist where the tests run; the real files and their
/// wiring are checked by the web tests and the browser look.
/// </summary>
public class BrandAssetStaticTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public BrandAssetStaticTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/favicon.svg", "image/svg+xml")]
    [InlineData("/favicon-16.png", "image/png")]
    [InlineData("/favicon-32.png", "image/png")]
    [InlineData("/apple-touch-icon.png", "image/png")]
    [InlineData("/brand/mark.svg", "image/svg+xml")]
    [InlineData("/brand/lockup-dark.svg", "image/svg+xml")]
    public async Task Brand_assets_are_served_anonymously_with_their_type_and_a_day_of_cache(string path, string mediaType)
    {
        await WithStandInWebBuild(async client =>
        {
            HttpResponseMessage response = await client.GetAsync(path, CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("public, max-age=86400", response.Headers.CacheControl?.ToString());
        });
    }

    [Fact]
    public async Task The_shell_and_the_hashed_bundles_do_not_get_the_brand_cache_policy()
    {
        await WithStandInWebBuild(async client =>
        {
            foreach (string path in new[] { "/", "/assets/app-1a2b3c.js" })
            {
                HttpResponseMessage response = await client.GetAsync(path, CancellationToken.None);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.NotEqual("public, max-age=86400", response.Headers.CacheControl?.ToString());
            }
        });
    }

    private async Task WithStandInWebBuild(Func<HttpClient, Task> body)
    {
        string contentRoot = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        string dist = Path.Combine(contentRoot, "web", "dist");
        Directory.CreateDirectory(Path.Combine(dist, "assets"));
        Directory.CreateDirectory(Path.Combine(dist, "brand"));
        await File.WriteAllTextAsync(Path.Combine(dist, "index.html"), "<!doctype html><title>SPA</title>", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(dist, "assets", "app-1a2b3c.js"), "console.log(1)", CancellationToken.None);
        foreach (string svg in new[] { "favicon.svg", "brand/mark.svg", "brand/lockup-dark.svg" })
        {
            await File.WriteAllTextAsync(Path.Combine(dist, svg), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>", CancellationToken.None);
        }

        foreach (string png in new[] { "favicon-16.png", "favicon-32.png", "apple-touch-icon.png" })
        {
            await File.WriteAllBytesAsync(Path.Combine(dist, png), [0x89, 0x50, 0x4E, 0x47], CancellationToken.None);
        }

        try
        {
            using WebApplicationFactory<Program> spa = _factory.WithWebHostBuilder(builder => builder.UseContentRoot(contentRoot));
            await body(spa.CreateClient());
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }
}
