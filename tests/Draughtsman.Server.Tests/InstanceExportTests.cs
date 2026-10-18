using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Backup;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>OP-01's "export everything": what an administrator gets, and, in both directions, who is allowed to get it.</summary>
public sealed class InstanceExportTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public InstanceExportTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task An_admin_gets_every_document_as_json_and_dsl_and_every_asset_in_one_zip_and_the_download_is_recorded()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        string orders = await Create(admin, "Order service / v2");
        string billing = await Create(admin, "Billing");
        byte[] png = BackupTestSupport.Png(10, 10);
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        using var form = new MultipartFormDataContent { { file, "file", "a.png" } };
        string assetId = (await (await admin.PostAsync("/api/assets", form)).Json())["id"]!.GetValue<string>();

        HttpResponseMessage response = await admin.GetAsync("/api/admin/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("draughtsman-export-", response.Content.Headers.ContentDisposition?.FileName, StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        List<string> names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains($"documents/{orders}.json", names);
        Assert.Contains($"documents/{orders}.dsl", names);
        Assert.Contains($"documents/{billing}.json", names);
        Assert.Contains($"assets/{assetId}.png", names);
        Assert.Contains("manifest.json", names);
        JsonNode json = JsonNode.Parse(Read(zip, $"documents/{orders}.json"))!;
        Assert.Equal("Order service / v2", json["title"]!.GetValue<string>());
        Assert.Contains("Order service / v2", Read(zip, $"documents/{orders}.dsl"), StringComparison.Ordinal);
        Assert.Equal(png, ReadBytes(zip, $"assets/{assetId}.png"));
        // The same bytes on every host: indented JSON writes the platform newline unless told not to.
        foreach (string text in new[] { Read(zip, $"documents/{orders}.json"), Read(zip, $"documents/{orders}.dsl"), Read(zip, "manifest.json"), Read(zip, "README.txt") })
        {
            Assert.DoesNotContain('\r', text);
        }

        JsonNode manifest = JsonNode.Parse(Read(zip, "manifest.json"))!;
        Assert.Equal(InstanceExport.FormatId, manifest["format"]!.GetValue<string>());
        Assert.Single(manifest["assets"]!.AsArray());

        using IServiceScope scope = _factory.Services.CreateScope();
        SecurityEvent recorded = (await scope.ServiceProvider.GetRequiredService<ISecurityEventStore>().List(new SecurityEventQuery(Types: [SecurityEventTypes.DataExported]))).Items.First();
        Assert.Contains("documents", recorded.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Documents_in_the_bin_are_left_out_of_the_export_unless_asked_and_then_sit_apart_under_bin()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        string live = await Create(admin, "Still here");
        string binned = await Create(admin, "In the bin");
        await admin.DeleteAsync($"/api/documents/{binned}");

        using (var defaultExport = new ZipArchive(await (await admin.GetAsync("/api/admin/export")).Content.ReadAsStreamAsync()))
        {
            List<string> names = defaultExport.Entries.Select(e => e.FullName).ToList();
            Assert.Contains($"documents/{live}.json", names);
            Assert.DoesNotContain(names, n => n.Contains(binned, StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.StartsWith("bin/", StringComparison.Ordinal));
            JsonNode manifest = JsonNode.Parse(Read(defaultExport, "manifest.json"))!;
            Assert.Equal([live], manifest["documents"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()));
            Assert.Contains("not included", Read(defaultExport, "README.txt"), StringComparison.Ordinal);
        }

        HttpResponseMessage withBin = await admin.GetAsync("/api/admin/export?includeBin=true");
        Assert.Equal(HttpStatusCode.OK, withBin.StatusCode);
        using (var zip = new ZipArchive(await withBin.Content.ReadAsStreamAsync()))
        {
            List<string> names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains($"documents/{live}.json", names);
            Assert.Contains($"bin/{binned}.json", names);
            Assert.Contains($"bin/{binned}.dsl", names);
            Assert.DoesNotContain($"documents/{binned}.json", names);
            Assert.Equal("In the bin", JsonNode.Parse(Read(zip, $"bin/{binned}.json"))!["title"]!.GetValue<string>());
            JsonArray documents = JsonNode.Parse(Read(zip, "manifest.json"))!["documents"]!.AsArray();
            Assert.Null(documents.Single(d => d!["id"]!.GetValue<string>() == live)!["deletedAt"]);
            Assert.NotNull(documents.Single(d => d!["id"]!.GetValue<string>() == binned)!["deletedAt"]);
        }

        using IServiceScope scope = factory.Services.CreateScope();
        SecurityEvent recorded = (await scope.ServiceProvider.GetRequiredService<ISecurityEventStore>().List(new SecurityEventQuery(Types: [SecurityEventTypes.DataExported]))).Items.First();
        Assert.Contains("from the bin", recorded.Detail, StringComparison.Ordinal);

        // The bin is an administrator's to export, like the rest of it: no one else can ask for it.
        await admin.CreateUser("bin-export-editor@example.com");
        HttpClient editor = await factory.SignIn("bin-export-editor@example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/admin/export?includeBin=true")).StatusCode);
    }

    [Fact]
    public async Task Nobody_but_a_cookie_signed_in_admin_can_export_it_all()
    {
        HttpClient admin = await _factory.CreateAuthenticatedClient();
        await admin.CreateUser("export-editor@example.com");
        HttpClient editor = await _factory.SignIn("export-editor@example.com");
        (_, string secret) = await admin.CreateToken("an admin's token");
        HttpClient token = _factory.Bearer(secret);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.GetAsync("/api/admin/export")).StatusCode);
        // An API token carries its owner's role, which here is Admin: it must still not be able to pull the whole instance out.
        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync("/api/admin/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/admin/export")).StatusCode);
    }

    private static async Task<string> Create(HttpClient client, string title)
    {
        HttpResponseMessage created = await client.PostAsJsonAsync("/api/documents", new { title, type = "flowchart", nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Json())["id"]!.GetValue<string>();
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader((zip.GetEntry(name) ?? throw new FileNotFoundException(name)).Open());
        return reader.ReadToEnd();
    }

    private static byte[] ReadBytes(ZipArchive zip, string name)
    {
        using Stream stream = (zip.GetEntry(name) ?? throw new FileNotFoundException(name)).Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
