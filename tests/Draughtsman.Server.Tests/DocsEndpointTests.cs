using System.Net;
using System.Text.Json.Nodes;
using Draughtsman.Server.Docs;
using Draughtsman.Server.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Draughtsman.Server.Tests;

/// <summary>
/// P21: the archive ships docs/ and the server now serves it, read-only, at /docs behind sign-in, and Help > Documentation
/// points there while about.docsUrl is unset. Both directions of every rule: what is served, and what must never be.
/// </summary>
public sealed class DocsEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "draughtsman-docs-" + Guid.NewGuid().ToString("N"));
    private readonly string _docs;

    public DocsEndpointTests()
    {
        _docs = Path.Combine(_root, "docs");
        Directory.CreateDirectory(Path.Combine(_docs, "spec"));
        File.WriteAllText(Path.Combine(_docs, "index.md"), "# Draughtsman documentation\n");
        File.WriteAllText(Path.Combine(_docs, "install.md"), "# Install\n<script>alert(1)</script>\n");
        File.WriteAllText(Path.Combine(_docs, "spec", "dsl.grammar"), "grammar\n");
        File.WriteAllText(Path.Combine(_docs, "page.html"), "<h1>not served</h1>");
        File.WriteAllText(Path.Combine(_docs, "tool.sh"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(_root, "secret.md"), "outside the docs folder\n");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private WebApplicationFactory<Program> WithDocs(DraughtsmanServerFactory factory, string? docsFolder, string? aboutYaml = null)
    {
        if (aboutYaml is not null)
        {
            Directory.CreateDirectory(factory.DataFolder);
            File.WriteAllText(Path.Combine(factory.DataFolder, "draughtsman.yaml"), aboutYaml);
        }

        return factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<DocsFolder>();
            services.AddSingleton(new DocsFolder(docsFolder));
        }));
    }

    [Fact]
    public async Task Anonymous_requests_for_the_docs_are_refused_not_served()
    {
        using var factory = new DraughtsmanServerFactory();
        using WebApplicationFactory<Program> host = WithDocs(factory, _docs);
        using HttpClient anonymous = host.CreateClient();

        foreach (string path in new[] { "/docs/", "/docs/index.md", "/docs/install.md", "/docs/spec/dsl.grammar" })
        {
            HttpResponseMessage response = await anonymous.GetAsync(path, CancellationToken.None);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_signed_in_user_reads_the_pages_as_plain_text_never_as_html()
    {
        using var factory = new DraughtsmanServerFactory();
        using WebApplicationFactory<Program> host = WithDocs(factory, _docs);
        HttpClient client = await host.CreateAuthenticatedClient();

        HttpResponseMessage index = await client.GetAsync("/docs/", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", index.Content.Headers.ContentType!.ToString());
        Assert.Contains("# Draughtsman documentation", await index.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
        Assert.Equal("nosniff", index.Headers.GetValues("X-Content-Type-Options").Single());

        // A page that carries script is still text: nothing in the folder can run in the app's origin.
        HttpResponseMessage install = await client.GetAsync("/docs/install.md", CancellationToken.None);
        Assert.StartsWith("text/plain", install.Content.Headers.ContentType!.ToString(), StringComparison.Ordinal);
        Assert.Contains("<script>", await install.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/docs/spec/dsl.grammar", CancellationToken.None)).StatusCode);

        // "/docs" (no slash) is sent on to "/docs/" (the test client follows the redirect), so a relative link from the index resolves under /docs/.
        HttpResponseMessage bare = await client.GetAsync("/docs", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, bare.StatusCode);
        Assert.Equal("/docs/", bare.RequestMessage!.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("/docs/../secret.md")]
    [InlineData("/docs/%2e%2e/secret.md")]
    [InlineData("/docs/spec/../../secret.md")]
    [InlineData("/docs/..%2fsecret.md")]
    [InlineData("/docs/%2e%2e%5csecret.md")]
    [InlineData("/docs//etc/passwd")]
    [InlineData("/docs/C:/Windows/win.ini")]
    [InlineData("/docs/page.html")]
    [InlineData("/docs/tool.sh")]
    [InlineData("/docs/no-such-page.md")]
    [InlineData("/docs/spec")]
    public async Task Nothing_outside_the_folder_or_outside_the_text_types_is_ever_served(string path)
    {
        using var factory = new DraughtsmanServerFactory();
        using WebApplicationFactory<Program> host = WithDocs(factory, _docs);
        HttpClient client = await host.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync(path, CancellationToken.None);

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{path} answered {(int)response.StatusCode}");
        Assert.DoesNotContain("outside the docs folder", await response.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../secret.md")]
    [InlineData("spec/../../secret.md")]
    [InlineData("./index.md")]
    [InlineData("spec/./dsl.grammar")]
    [InlineData("..\\secret.md")]
    [InlineData("spec\\dsl.grammar")]
    [InlineData("/etc/passwd")]
    [InlineData("/index.md")]
    [InlineData("spec//dsl.grammar")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("index.md\0.html")]
    [InlineData("page.html")]
    [InlineData("tool.sh")]
    [InlineData("spec")]
    [InlineData("missing.md")]
    public void Resolve_refuses_every_way_out_of_the_folder_and_every_other_file_type(string relative)
    {
        // Straight at the resolver, because an HTTP client or Kestrel collapses dot segments before they ever reach it.
        Assert.Null(DocsEndpoints.Resolve(_docs, relative));
    }

    [Theory]
    [InlineData("index.md")]
    [InlineData("install.md")]
    [InlineData("spec/dsl.grammar")]
    public void Resolve_accepts_the_files_that_are_there(string relative)
    {
        Assert.Equal(Path.GetFullPath(Path.Combine(_docs, relative)), DocsEndpoints.Resolve(_docs, relative));
    }

    [Fact]
    public void Resolve_refuses_a_symbolic_link_that_leaves_the_folder_and_accepts_one_that_stays()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.CreateSymbolicLink(Path.Combine(_docs, "escape.md"), Path.Combine(_root, "secret.md"));
        File.CreateSymbolicLink(Path.Combine(_docs, "alias.md"), Path.Combine(_docs, "index.md"));

        Assert.Null(DocsEndpoints.Resolve(_docs, "escape.md"));
        Assert.Equal(Path.Combine(_docs, "alias.md"), DocsEndpoints.Resolve(_docs, "alias.md"));
    }

    [Fact]
    public async Task An_install_without_docs_answers_404_for_every_path()
    {
        using var factory = new DraughtsmanServerFactory();
        using WebApplicationFactory<Program> host = WithDocs(factory, docsFolder: null);
        HttpClient client = await host.CreateAuthenticatedClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/docs/", CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/docs/index.md", CancellationToken.None)).StatusCode);
    }

    [Fact]
    public void Only_a_packaged_install_with_an_index_counts_as_having_docs()
    {
        Assert.False(DocsFolder.Find(_root).Available, "an unpackaged folder (no wwwroot) with docs/ is not an install");

        Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
        File.WriteAllText(Path.Combine(_root, "wwwroot", "index.html"), "<html/>");
        Assert.True(DocsFolder.Find(_root).Available);

        File.Delete(Path.Combine(_docs, "index.md"));
        Assert.False(DocsFolder.Find(_root).Available, "docs/ without its index is not served");
    }

    [Fact]
    public async Task Help_documentation_points_at_the_local_copy_only_while_unset_and_present()
    {
        using var factory = new DraughtsmanServerFactory();

        // Unset, docs present: this server's own copy.
        using (WebApplicationFactory<Program> local = WithDocs(factory, _docs))
        {
            Assert.Equal("/docs/", await DocsUrlOf(local));
        }

        // Unset, no docs shipped: the vendor's site, as before.
        using (WebApplicationFactory<Program> none = WithDocs(factory, docsFolder: null))
        {
            Assert.Equal(AboutEndpoints.VendorDocsUrl, await DocsUrlOf(none));
        }

        // Set: the operator's address wins even though a local copy exists.
        using (WebApplicationFactory<Program> set = WithDocs(factory, _docs, "about:\n  docsUrl: https://wiki.example.com/draughtsman\n"))
        {
            Assert.Equal("https://wiki.example.com/draughtsman", await DocsUrlOf(set));
        }

        // Explicitly empty: the entry is hidden, local copy or not.
        using (WebApplicationFactory<Program> hidden = WithDocs(factory, _docs, "about:\n  docsUrl: \"\"\n"))
        {
            Assert.Null(await DocsUrlOf(hidden));
        }

        // A value that is not an http(s) address is ignored (and so hides the entry), never passed on as an href.
        using (WebApplicationFactory<Program> unsafeValue = WithDocs(factory, _docs, "about:\n  docsUrl: \"javascript:alert(1)\"\n"))
        {
            Assert.Null(await DocsUrlOf(unsafeValue));
        }
    }

    private static async Task<string?> DocsUrlOf(WebApplicationFactory<Program> host)
    {
        HttpClient client = await host.CreateAuthenticatedClient();
        JsonNode about = JsonNode.Parse(await client.GetStringAsync("/api/about", CancellationToken.None))!;
        return about["docsUrl"]?.GetValue<string>();
    }
}
