using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The features that do call out, each shown both ways: off in a default install (and silent: the guard hears nothing and the
/// product's own status says so), and, when an administrator switches it on, heard by the same guard going where it was told to.
/// docs/air-gapped.md and docs/marketing-claims.md name these tests as the proof of "what leaves your network and when".
/// <list type="bullet">
/// <item>AI endpoint (<c>ai.provider</c>): an HTTP request to the host an administrator names.</item>
/// <item>Outgoing email (<c>email.provider: smtp</c>): an SMTP connection to the host an administrator names.</item>
/// <item>The Themes page's "From a website" (<c>themes.fetchFromUrl.enabled</c>): an HTTPS request to the page an administrator names, when they press the button.</item>
/// <item>PDF (<c>export.chromium.enabled</c>): a local headless browser, installed by the administrator; the server never downloads one.</item>
/// <item>Postgres (<c>storage.provider: postgres</c>): a connection to a database the administrator runs.</item>
/// </list>
/// A "switched on" test never sends a byte off this machine: a remote endpoint is given to a client whose connect step refuses (so the
/// request is announced to the guard and goes nowhere), and the others point at a closed loopback port.
/// </summary>
[Collection(OutboundNetworkCollection.Name)]
public class OptInNetworkFeaturesTests
{
    [Fact]
    public async Task Ai_is_off_by_default_hidden_and_silent()
    {
        using var observer = new OutboundNetworkObserver();
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode health = await (await admin.GetAsync("/api/health", CancellationToken.None)).Json();
        JsonNode settings = await (await admin.GetAsync("/api/admin/ai", CancellationToken.None)).Json();
        HttpResponseMessage generate = await admin.PostAsync("/api/ai/generate-from-text", Json("""{"description":"a login flow","diagramType":"flowchart"}"""), CancellationToken.None);
        HttpResponseMessage tested = await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/admin/ai/test", null, admin));

        Assert.False(health["ai"]!["enabled"]!.GetValue<bool>());
        Assert.False(settings["enabled"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, generate.StatusCode);
        Assert.False((await tested.Json())["ok"]!.GetValue<bool>());
        Assert.DoesNotContain(observer.Attempts, a => a.Channel is "dns lookup" or "tcp connect" or "tls handshake");
        Assert.All(observer.Attempts, a => Assert.Equal("localhost:80", a.Destination)); // the test's own in-memory calls to the server, and nothing the server started
        Assert.Empty(observer.OffMachine);
    }

    [Fact]
    public async Task Ai_switched_on_and_pointed_at_a_remote_host_is_heard_by_the_guard_and_goes_nowhere_here()
    {
        using var observer = new OutboundNetworkObserver();
        using var factory = new RefusingFactory("ai:\n  provider: ollama\n  model: llama3.1\n  endpoint: \"http://ai.draughtsman-airgap.test:11434\"\n  timeoutSeconds: 5\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage tested = await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/admin/ai/test", null, admin));

        Assert.Equal(HttpStatusCode.OK, tested.StatusCode);
        Assert.False((await tested.Json())["ok"]!.GetValue<bool>());
        NetworkAttempt attempt = Assert.Single(observer.OffMachine, a => a.Channel == "http request");
        Assert.Equal("ai.draughtsman-airgap.test:11434", attempt.Destination);
        Assert.Contains("ai.draughtsman-airgap.test:11434", factory.Refused);
    }

    [Fact]
    public async Task Ai_switched_on_and_pointed_at_this_machine_stays_on_this_machine()
    {
        int closedPort = ClosedLoopbackPort();
        using var observer = new OutboundNetworkObserver();
        using var factory = new ConfiguredServerFactory($"ai:\n  provider: ollama\n  model: llama3.1\n  endpoint: \"http://127.0.0.1:{closedPort}\"\n  timeoutSeconds: 5\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();

        HttpResponseMessage tested = await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/admin/ai/test", null, admin));

        Assert.False((await tested.Json())["ok"]!.GetValue<bool>());
        Assert.Contains(observer.Attempts, a => a.Channel == "http request" && a.Destination == $"127.0.0.1:{closedPort}");
        Assert.Empty(observer.OffMachine);
    }

    [Fact]
    public async Task Email_is_off_by_default_and_a_reset_request_sends_nothing()
    {
        using var observer = new OutboundNetworkObserver();
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode email = await (await admin.GetAsync("/api/admin/email", CancellationToken.None)).Json();
        using HttpResponseMessage reset = await factory.CreateClient().RequestReset(AuthTestHelpers.DefaultEmail);
        JsonNode tested = await (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/admin/email/test", null, admin))).Json();

        Assert.False(email["configured"]!.GetValue<bool>());
        Assert.Null(email["provider"]);
        Assert.Equal(HttpStatusCode.NotFound, reset.StatusCode);
        Assert.False(tested["ok"]!.GetValue<bool>());
        Assert.DoesNotContain(observer.Attempts, a => a.Channel is "tcp connect" or "tls handshake");
        Assert.Empty(observer.OffMachine);
    }

    [Fact]
    public async Task Email_switched_on_reaches_for_exactly_the_mail_server_it_was_given()
    {
        int closedPort = ClosedLoopbackPort();
        using var observer = new OutboundNetworkObserver();
        using var factory = new ConfiguredServerFactory($"server:\n  publicUrl: https://draughtsman.test\nemail:\n  provider: smtp\n  from: Draughtsman <noreply@draughtsman.test>\n  smtp:\n    host: 127.0.0.1\n    port: {closedPort}\n    tls: starttls\n    timeoutSeconds: 5\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode tested = await (await admin.SendAsync(AuthBarHelpers.Request(HttpMethod.Post, "/api/admin/email/test", null, admin))).Json();

        Assert.False(tested["ok"]!.GetValue<bool>());
        Assert.Contains($"127.0.0.1:{closedPort}", tested["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(observer.Attempts, a => a.Channel == "tcp connect" && !a.OffMachine && a.Destination.StartsWith("InterNetwork", StringComparison.Ordinal));
        Assert.Empty(observer.OffMachine);
    }

    [Fact]
    public async Task Pdf_is_off_by_default_and_the_server_never_starts_a_browser_or_downloads_one()
    {
        using var observer = new OutboundNetworkObserver();
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();
        IPdfRenderer renderer = factory.Services.GetRequiredService<IPdfRenderer>();

        PdfStatus probe = await renderer.ProbeAsync(CancellationToken.None);
        JsonNode health = await (await admin.GetAsync("/api/health", CancellationToken.None)).Json();

        Assert.False(new DraughtsmanConfig().Export.Chromium.Enabled);
        Assert.False(renderer.IsAvailable);
        Assert.False(probe.Available);
        Assert.Equal("disabled", probe.Problem);
        Assert.Equal("disabled", health["export"]!["pdfProblem"]!.GetValue<string>());
        Assert.Empty(observer.OffMachine);

        // The only place a browser could be fetched is Playwright's installer, and nothing in the server calls it: the "install chromium"
        // command is text an administrator is told to run themselves.
        string[] callers = [.. SourceFiles("src").Where(f => Regex.IsMatch(File.ReadAllText(f), @"Playwright\.Program\b|\bProgram\.Main\(\s*new\b|InstallBrowsers", RegexOptions.None, TimeSpan.FromSeconds(5)))];
        Assert.True(callers.Length == 0, "These files call Playwright's installer:\n" + string.Join('\n', callers));
    }

    [Fact]
    public async Task Theme_fetch_is_off_by_default_the_page_says_so_and_a_request_for_one_makes_no_connection()
    {
        using var observer = new OutboundNetworkObserver();
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode themes = await (await admin.GetAsync("/api/admin/themes/extract/options", CancellationToken.None)).Json();
        HttpResponseMessage fetch = await admin.PostAsync("/api/admin/themes/extract", Json("""{"url":"https://example.com/"}"""), CancellationToken.None);
        HttpResponseMessage pasted = await admin.PostAsync("/api/admin/themes/extract", Json("""{"css":"body{background:#112233;color:#eeeeee}"}"""), CancellationToken.None);

        Assert.False(new DraughtsmanConfig().Themes.FetchFromUrl.Enabled);
        Assert.False(themes["url"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Forbidden, fetch.StatusCode);
        Assert.Equal(HttpStatusCode.OK, pasted.StatusCode); // the paste-CSS route is not a network feature and works regardless
        Assert.DoesNotContain(observer.Attempts, a => a.Channel is "dns lookup" or "tcp connect" or "tls handshake");
        Assert.All(observer.Attempts, a => Assert.Equal("localhost:80", a.Destination));
        Assert.Empty(observer.OffMachine);
    }

    [Fact]
    public async Task Theme_fetch_switched_on_reaches_for_exactly_the_address_the_administrator_typed_and_only_when_they_press_the_button()
    {
        int closedPort = ClosedLoopbackPort();
        using var observer = new OutboundNetworkObserver();
        using var factory = new ConfiguredServerFactory("themes:\n  fetchFromUrl:\n    enabled: true\n    allowedHosts: [\"127.0.0.1\"]\n");
        HttpClient admin = await factory.CreateAuthenticatedClient();

        // Switching it on, and reading the Themes page, make no request: the fetch happens only on the administrator's own POST.
        JsonNode themes = await (await admin.GetAsync("/api/admin/themes/extract/options", CancellationToken.None)).Json();
        Assert.True(themes["url"]!.GetValue<bool>());
        Assert.DoesNotContain(observer.Attempts, a => a.Channel is "dns lookup" or "tcp connect" or "tls handshake");

        HttpResponseMessage fetch = await admin.PostAsync("/api/admin/themes/extract", Json($$"""{"url":"http://127.0.0.1:{{closedPort}}/"}"""), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadGateway, fetch.StatusCode); // nothing listens there
        Assert.Contains(observer.Attempts, a => a.Channel == "http request" && a.Destination == $"127.0.0.1:{closedPort}");
        Assert.Empty(observer.OffMachine);
    }

    [Fact]
    public async Task Postgres_is_off_by_default_and_sqlite_is_what_a_default_instance_runs()
    {
        using var factory = new DraughtsmanServerFactory();
        HttpClient admin = await factory.CreateAuthenticatedClient();

        JsonNode health = await (await admin.GetAsync("/api/health", CancellationToken.None)).Json();

        Assert.Equal("sqlite", new DraughtsmanConfig().Storage.Provider);
        Assert.Equal("sqlite", health["storage"]!.GetValue<string>());
    }

    [Fact]
    public void Nothing_that_talks_to_stripe_or_reports_telemetry_is_part_of_the_server()
    {
        // draughtsman.deps.json is the server's own dependency manifest: every project and package that is in its closure.
        string manifest = Path.Combine(AppContext.BaseDirectory, "draughtsman.deps.json");
        JsonNode deps = JsonNode.Parse(File.ReadAllText(manifest))!;
        string[] libraries = [.. deps["libraries"]!.AsObject().Select(p => p.Key.Split('/')[0])];

        Assert.Contains("Draughtsman.Core", libraries);
        Assert.Contains("Draughtsman.Ai", libraries);
        Assert.DoesNotContain(libraries, name => name.Contains("Stripe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(libraries, name => name.Contains("Sentry", StringComparison.OrdinalIgnoreCase) || name.Contains("ApplicationInsights", StringComparison.OrdinalIgnoreCase) || name.Contains("OpenTelemetry", StringComparison.OrdinalIgnoreCase) || name.Contains("Analytics", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The set of files that may contain network-capable code is closed: a new one is a decision, made here and in data-flow.md.</summary>
    [Fact]
    public void Network_capable_code_lives_only_in_the_files_that_data_flow_md_accounts_for()
    {
        string[] patterns = [@"\bHttpClient\b", @"\bIHttpClientFactory\b", @"\bAddHttpClient\b", @"\bSmtpClient\b", @"\bTcpClient\b", @"\bTcpListener\b", @"\bUdpClient\b", @"new Socket\(", @"\bDns\.", @"\bClientWebSocket\b", @"\bHttpWebRequest\b", @"\bWebClient\b", @"\bUseNpgsql\b", @"Playwright\.CreateAsync"];
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in SourceFiles("src"))
        {
            string text = File.ReadAllText(file);
            if (patterns.Any(p => Regex.IsMatch(text, p, RegexOptions.None, TimeSpan.FromSeconds(5))))
            {
                found.Add(RelativeToRepository(file));
            }
        }

        string[] allowed =
        [
            // The AI endpoint (off until an administrator sets ai.provider): four providers and their wiring.
            "src/Draughtsman.Ai/Anthropic/AnthropicModelProvider.cs",
            "src/Draughtsman.Ai/AzureOpenAi/AzureOpenAiModelProvider.cs",
            "src/Draughtsman.Ai/Ollama/OllamaModelProvider.cs",
            "src/Draughtsman.Ai/OpenAi/OpenAiModelProvider.cs",
            "src/Draughtsman.Server/Ai/AiConnectGuard.cs", // the AU-11 endpoint policy at connect time: resolves the endpoint's name and opens the socket.
            "src/Draughtsman.Server/Ai/AiServiceRegistration.cs",
            "src/Draughtsman.Server/Ai/AiSettingsService.cs",
            // `draughtsman doctor` and `support-bundle`: one anonymous GET /api/health to 127.0.0.1 / ::1, written over a socket by hand. Loopback only, never a name, never another host.
            "src/Draughtsman.Server/Diagnostics/LoopbackProbe.cs",
            // Outgoing email (off until email.provider is smtp).
            "src/Draughtsman.Server/Email/SmtpEmailSender.cs",
            // The PDF browser (off until export.chromium.enabled): Playwright drives a local Chromium.
            "src/Draughtsman.Server/Export/ChromiumPdfRenderer.cs",
            // "From a website" on the Themes page (off until themes.fetchFromUrl.enabled): fetches the page an administrator names.
            "src/Draughtsman.Server/Theming/Extraction/ThemePageFetcher.cs",
            // Postgres (off until storage.provider is postgres).
            "src/Draughtsman.Storage/StorageSetup.cs",
        ];

        string[] unexpected = [.. found.Except(allowed, StringComparer.Ordinal)];
        string[] stale = [.. allowed.Except(found, StringComparer.Ordinal)];
        Assert.True(unexpected.Length == 0, "New network-capable code, not in the air-gap inventory (add it here, to docs/data-flow.md and to docs/air-gapped.md):\n" + string.Join('\n', unexpected));
        Assert.True(stale.Length == 0, "These files no longer contain network code; remove them from the inventory:\n" + string.Join('\n', stale));
    }

    /// <summary>Every child process the server can start is named, so a new one that could reach out is a decision too.</summary>
    [Fact]
    public void Child_processes_are_started_only_by_the_files_that_data_flow_md_accounts_for()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in SourceFiles("src"))
        {
            if (Regex.IsMatch(File.ReadAllText(file), @"(?<![A-Za-z])Process\.Start\(|new ProcessStartInfo\(", RegexOptions.None, TimeSpan.FromSeconds(5)))
            {
                found.Add(RelativeToRepository(file));
            }
        }

        string[] allowed =
        [
            "src/Draughtsman.Server/Cli/ServiceExecutor.cs", // `draughtsman service install|uninstall|status`: the operator's own command (systemctl, launchctl, sc).
            "src/Draughtsman.Server/Export/ProcessTable.cs", // `ps`, to find the PDF browser's own children to stop.
            "src/Draughtsman.Server/KeyRing/KeyEncryptionKeySources.cs", // macOS `security`: the local Keychain.
            "src/Draughtsman.Sidecar/SidecarProcess.cs", // The Node sidecar, over stdio.
        ];

        string[] unexpected = [.. found.Except(allowed, StringComparer.Ordinal)];
        string[] stale = [.. allowed.Except(found, StringComparer.Ordinal)];
        Assert.True(unexpected.Length == 0, "New child-process code, not in the air-gap inventory:\n" + string.Join('\n', unexpected));
        Assert.True(stale.Length == 0, "These files no longer start a process; remove them from the inventory:\n" + string.Join('\n', stale));
    }

    private static StringContent Json(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    /// <summary>A loopback port nothing listens on: a connect to it is refused at once, without leaving the machine.</summary>
    private static int ClosedLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static IEnumerable<string> SourceFiles(string folder)
    {
        string root = RepositoryRoot();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
        {
            string relative = RelativeToRepository(file);
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/Migrations/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string RelativeToRepository(string file)
    {
        return Path.GetRelativePath(RepositoryRoot(), file).Replace('\\', '/');
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Draughtsman.sln")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new FileNotFoundException("Draughtsman.sln not found above the test binaries.");
    }

    /// <summary>
    /// A server with <paramref name="yaml"/> as its draughtsman.yaml whose every <see cref="IHttpClientFactory"/> client refuses to connect:
    /// the request is made (and announced to the guard) but no DNS lookup and no socket follows, so a "remote" endpoint costs no traffic.
    /// </summary>
    private sealed class RefusingFactory(string yaml) : DraughtsmanServerFactory
    {
        public List<string> Refused { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(Path.Combine(DataFolder, DraughtsmanConfig.FileName), yaml);
            builder.ConfigureServices(services => services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = new SocketsHttpHandler
                {
                    ConnectCallback = (context, _) =>
                    {
                        lock (Refused)
                        {
                            Refused.Add($"{context.DnsEndPoint.Host}:{context.DnsEndPoint.Port}");
                        }

                        throw new IOException("The test refuses every outgoing connection.");
                    },
                })));
        }
    }
}
