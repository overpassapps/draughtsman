using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Cli;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Diagnostics;
using Draughtsman.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>What a test does instead of asking the machine: an offline answer for the account and the loopback, and a fixed clock.</summary>
internal static class OfflineProbes
{
    public static DoctorProbes Make(AccountFacts? account = null, LoopbackAnswer? loopback = null, DateTimeOffset? now = null)
    {
        return new DoctorProbes(
            () => account ?? new AccountFacts(FileOwners.CurrentUserId(), FileOwners.CurrentUserName(), null, false, OperatingSystem.IsWindows()),
            port => Task.FromResult(loopback ?? new LoopbackAnswer(port, false, false, null, null)),
            () => now ?? DateTimeOffset.UtcNow,
            // A volume with room, whatever this machine's disk is doing: a test must not depend on how full the host is.
            Disk: _ => (50L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024));
    }
}

/// <summary>
/// The support bundle, from the side of what must never be in it. A real server over a data folder full of planted secrets (in the settings, the log, the
/// security events, the key ring, the setup-code file, stored AI and email settings, the licence, an outbox, file names and the environment) is bundled;
/// every entry of the zip (and every zip inside it) is then searched, bytes and names, for every one of them. The same search is shown to find a leak when
/// one is put there, so a pass means something.
/// </summary>
public class SupportBundleTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------------------------------------------ the plant

    /// <summary>Everything that must be absent from the zip. Grouped by where it was put, so a failure names the route a secret took.</summary>
    private sealed class Plant
    {
        public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

        public string Add(string where, string secret)
        {
            Secrets[secret] = where;
            return secret;
        }
    }

    private static string Yaml(Plant plant, string folder)
    {
        string aiVar = "PLANTED_AI_KEY_VAR";
        string smtpVar = "PLANTED_SMTP_PW_VAR";
        string smtpFile = Path.Combine(folder, "smtp-password.txt");
        string keyFile = Path.Combine(folder, "kek.txt");
        // Plain words: nothing about it is a secret's shape, so only knowing it is in the file the settings name as the SMTP password can remove it from the log.
        File.WriteAllText(smtpFile, plant.Add("smtp password file (plain words)", "my plain words secret") + "\n");
        File.WriteAllText(keyFile, plant.Add("key file", "KekFilePlanted-0a1b2c3d4e5f") + "\n");
        return $"""
            server:
              knownProxies:
                - "172.17.0.1"
              publicUrl: "https://admin:{plant.Add("publicUrl userinfo", "UrlPassw0rdPlanted")}@draughtsman.example.com"
            ai:
              provider: ollama
              model: llama3
              endpoint: "https://ai.example.com/v1/chat?key={plant.Add("ai endpoint query", "PlantedEndpointKey123")}"
              apiKeyEnvironmentVariable: {aiVar}
            email:
              provider: smtp
              from: "{plant.Add("email.from", "noreply-planted@planted-example.com")}"
              replyTo: "{plant.Add("email.replyTo", "replyto-planted@planted-example.com")}"
              smtp:
                host: smtp.planted.example.com
                user: "{plant.Add("smtp user", "smtp-user-planted-account")}"
                passwordEnvironmentVariable: {smtpVar}
                passwordFile: "{smtpFile.Replace("\\", "\\\\", StringComparison.Ordinal)}"
            keyRing:
              keyFile: "{keyFile.Replace("\\", "\\\\", StringComparison.Ordinal)}"
              certificate:
                path: "{plant.Add("certificate path", "/secrets/planted-certificate-path.pfx")}"
            logging:
              file:
                enabled: true
            """;
    }

    private static Dictionary<string, string> Environment(Plant plant)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PLANTED_AI_KEY_VAR"] = plant.Add("ai key env var", "sk-planted-ai-key-9f8e7d6c5b4a3210"),
            ["PLANTED_SMTP_PW_VAR"] = plant.Add("smtp password env var", "SmtpEnvPwPlanted-123456"),
            ["DRAUGHTSMAN_KEY_ENCRYPTION_KEY"] = plant.Add("kek env var", "KekPlanted0123456789abcdef=="),
            ["SOME_VENDOR_API_TOKEN"] = plant.Add("secret-named env var", "vendorplantedtoken9988776655"),
            ["ASPNETCORE_URLS"] = "http://0.0.0.0:5180",
            ["DRAUGHTSMAN_DATA"] = "/data",
            ["DRAUGHTSMAN_SOMETHING_ELSE"] = plant.Add("other DRAUGHTSMAN_ env var", "OtherEnvValuePlanted-5544"),
            ["PATH"] = "/usr/bin",
            // A secret that looks like nothing: only knowing it is the value of an environment variable named for a secret can remove it.
            ["MY_APP_PASSWORD"] = plant.Add("plain-words env secret", "correct horse battery staple"),
        };
    }

    /// <summary>Puts secrets into every file a data folder has that the bundle must not read, and a hostile log it must read and clean.</summary>
    private static void PlantFiles(Plant plant, string folder)
    {
        File.WriteAllText(Path.Combine(folder, "setup-code.txt"), $"Draughtsman first-run setup code: {plant.Add("setup code file", "K7M2QXPV4N")}\nRead it once.\n");
        Directory.CreateDirectory(Path.Combine(folder, "ai"));
        File.WriteAllText(Path.Combine(folder, "ai", "settings.json"), $"{{\"provider\":\"openai\",\"apiKey\":\"{plant.Add("stored ai key", "StoredAiKeyPlanted-zz99yy88xx77")}\"}}");
        Directory.CreateDirectory(Path.Combine(folder, "email"));
        File.WriteAllText(Path.Combine(folder, "email", "settings.json"), $"{{\"smtpPassword\":\"{plant.Add("stored smtp password", "StoredSmtpPwPlanted-aa11bb22")}\"}}");
        File.WriteAllText(Path.Combine(folder, "licence.key"), $"-----BEGIN LICENCE-----\n{plant.Add("licence body", "LICENCEPAYLOADPLANTEDb64Zm9vYmFyYmF6")}\nOrganisation: {plant.Add("licensee", "Planted Holdings Limited")}\n-----END LICENCE-----\n");
        Directory.CreateDirectory(Path.Combine(folder, "outbox"));
        File.WriteAllText(Path.Combine(folder, "outbox", "20261006-reset.eml"), $"Subject: reset\n\nhttps://x/reset-password#token={plant.Add("outbox reset link", "dpr_OutboxPlantedResetTokenValue0123456789abcdef")}\n");
        Directory.CreateDirectory(Path.Combine(folder, "backups"));
        File.WriteAllText(Path.Combine(folder, "backups", $"secret-{plant.Add("file name", "PlantedFileName9f8e7d6c5b4a3210ffee")}.zip"), "not a real backup");
        Directory.CreateDirectory(Path.Combine(folder, "assets", "ab"));
        File.WriteAllText(Path.Combine(folder, "assets", "ab", plant.Add("asset hash name", "ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12cd34ef56ab12") + ".png"), "x");

        // A log from an earlier day, full of what logs hold when something went wrong, in the file logger's own line format.
        Directory.CreateDirectory(Path.Combine(folder, "logs"));
        var log = new StringBuilder();
        log.Append("2026-10-05T08:00:00.000Z INF Draughtsman.Server.Startup: Draughtsman 0.1.0; data folder /data; storage sqlite\n");
        log.Append($"2026-10-05T08:00:01.000Z WRN Draughtsman.Server.Security: Security event sign_in.failed: actor - target {plant.Add("log email", "victim.person@planted-example.com")} from 203.0.113.77 via anonymous. password={plant.Add("log password", "LogPassw0rd-Planted-xyz")}\n");
        log.Append($"2026-10-05T08:00:02.000Z INF cat: request Authorization: Bearer {plant.Add("log bearer", "eyJ" + "hbGciOiJIUzI1NiJ9.eyJ" + "zdWIiOiJwbGFudGVkIn0.PlantedSignaturePartForLogBearer0123")}\n");
        log.Append($"2026-10-05T08:00:03.000Z INF cat: Cookie: ds_session={plant.Add("log session", "LogSessionPlanted-Zk3-8vQnLw2pYx9TfR7sHd1Ma")}; XSRF-TOKEN={plant.Add("log xsrf", "LogXsrfPlanted-Qq1Ww2Ee3Rr4Tt5Yy6Uu7Ii8Oo9Pp")}\n");
        log.Append($"2026-10-05T08:00:04.000Z INF cat: First-run setup code (single use): {plant.Add("log setup code", "Q9W8E7R6T5")}\n");
        log.Append($"2026-10-05T08:00:05.000Z INF cat: API token created: {plant.Add("log api token", "dst_LogApiTokenPlanted0123456789abcdefghijklmnopq")}\n");
        log.Append($"2026-10-05T08:00:06.000Z ERR cat: connection failed Host=db;Username=u;Password={plant.Add("log pg password", "LogPgPassword-Planted-99")}\n");
        log.Append($"2026-10-05T08:00:07.000Z INF cat: smtp.password: {plant.Add("log smtp", "LogSmtpPlanted-pw-555")}\n");
        log.Append($"2026-10-05T08:00:08.000Z INF cat: the AI key is {plant.Add("log ai key", "sk-" + "LogAiKeyPlanted0123456789abcdefghijklmnop")}\n");
        log.Append($"2026-10-05T08:00:09.000Z INF cat: session secret hex {plant.Add("log hex", "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")}\n");
        log.Append($"2026-10-05T08:00:10.000Z INF cat: fetched https://svc:{plant.Add("log url password", "LogUrlPassPlanted42")}@feeds.example.com/x\n");
        log.Append("2026-10-05T08:00:11.000Z INF Microsoft.Hosting.Lifetime: Now listening on: http://0.0.0.0:5180\n");
        // Free-form lines that name no secret and look like none: a person's words. Removed by value, because this process knows them.
        log.Append("2026-10-05T08:00:12.000Z INF cat: the operator typed correct horse battery staple into the form and pressed go\n");
        log.Append("2026-10-05T08:00:13.000Z INF cat: smtp login used my plain words secret for the relay\n");
        File.WriteAllText(Path.Combine(folder, "logs", "draughtsman-20261005.log"), log.ToString());
    }

    // ------------------------------------------------------------------------------------------------------ the search

    /// <summary>Every place a secret could be in a zip: entry names, entry bytes (as UTF-8 and as UTF-16), and any zip inside a zip, to any depth.</summary>
    private static List<string> Leaks(byte[] zipBytes, IReadOnlyDictionary<string, string> secrets, string path = "bundle.zip")
    {
        var found = new List<string>();
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string here = path + "!" + entry.FullName;
            using var buffer = new MemoryStream();
            using (Stream stream = entry.Open())
            {
                stream.CopyTo(buffer);
            }

            byte[] bytes = buffer.ToArray();
            string text8 = Encoding.UTF8.GetString(bytes);
            string text16 = Encoding.Unicode.GetString(bytes);
            foreach ((string secret, string where) in secrets)
            {
                if (entry.FullName.Contains(secret, StringComparison.Ordinal))
                {
                    found.Add($"{where}: '{secret}' is in the NAME of {here}");
                }

                if (text8.Contains(secret, StringComparison.Ordinal) || text16.Contains(secret, StringComparison.Ordinal))
                {
                    found.Add($"{where}: '{secret}' is in {here}");
                }
            }

            if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K')
            {
                found.AddRange(Leaks(bytes, secrets, here));
            }
        }

        return found;
    }

    private static byte[] Zip(IEnumerable<BundleEntry> entries)
    {
        string path = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            SupportBundle.WriteZip(path, entries, s_now);
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------------------------------------------------ the tests

    [Fact]
    public async Task No_planted_secret_reaches_the_zip_from_a_real_instance_full_of_them()
    {
        var plant = new Plant();
        using var factory = new FakeIpServerFactory(logs: null);
        Directory.CreateDirectory(factory.DataFolder);
        PlantFiles(plant, factory.DataFolder);
        File.WriteAllText(Path.Combine(factory.DataFolder, DraughtsmanConfig.FileName), Yaml(plant, factory.DataFolder));
        Dictionary<string, string> environment = Environment(plant);

        // Real events, a real session, a real token, a real password hash, and the real key ring that is on disk after the host started.
        HttpClient admin = await factory.CreateAuthenticatedClient();
        (_, string apiToken) = await admin.CreateToken("planted-token-name-" + plant.Add("token name text", "TokenNameSecretWord"));
        // A document title is content: it is in the event the product writes when a document moves to the bin.
        string title = plant.Add("document title", "Quarterly Merger Plan Planted");
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/documents", new { title, type = "flowchart", nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
        string documentId = (await created.Json())["id"]!.GetValue<string>();
        await admin.DeleteAsync($"/api/documents/{documentId}");
        plant.Add("api token", apiToken);
        using HttpClient anonymous = factory.CreateClient();
        using var failed = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email = "planted.attacker@example.org", password = plant.Add("failed login password", "AttemptedPassw0rd-Planted") }) };
        failed.Headers.TryAddWithoutValidation(FakeIpServerFactory.Header, "203.0.113.77");
        await anonymous.SendAsync(failed);
        HttpResponseMessage login = await anonymous.PostAsJsonAsync("/api/auth/login", new { email = AuthTestHelpers.DefaultEmail, password = AuthTestHelpers.DefaultPassword });
        plant.Add("session cookie", login.Cookie("ds_session")!);
        plant.Add("xsrf cookie", login.Cookie("XSRF-TOKEN")!);
        plant.Add("user password", AuthTestHelpers.DefaultPassword);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            User? user = await scope.ServiceProvider.GetRequiredService<IUserStore>().GetUserByEmail(AuthTestHelpers.DefaultEmail);
            plant.Add("password hash", user!.PasswordHash);
        }

        foreach (string key in Directory.EnumerateFiles(Path.Combine(factory.DataFolder, "keys"), "*.xml"))
        {
            foreach (System.Text.RegularExpressions.Match value in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(key), @">([A-Za-z0-9+/=]{40,})<"))
            {
                plant.Add("key ring material", value.Groups[1].Value);
            }
        }

        Assert.Contains(plant.Secrets.Values, v => v == "key ring material");

        IReadOnlyList<BundleEntry> entries = await SupportBundle.BuildAsync(factory.DataFolder, new SupportBundleOptions { Probes = OfflineProbes.Make(now: s_now), Environment = environment, Now = s_now });
        byte[] zip = Zip(entries);

        // 1. Not one of them is in the zip.
        List<string> leaks = Leaks(zip, plant.Secrets);
        Assert.True(leaks.Count == 0, "Secrets reached the support bundle:\n" + string.Join('\n', leaks));

        // 2. It is the bundle it claims to be, and the search is not blind: the entries are all there, and what should survive did.
        Assert.Equal(SupportBundle.FileNames, entries.Select(e => e.Name));
        string All(string name) => entries.Single(e => e.Name == name).Content;
        Assert.Contains("server.forceHttps: false", All("settings-effective.txt"), StringComparison.Ordinal);
        Assert.Contains("server.knownProxies: [172.17.0.1]", All("settings-effective.txt"), StringComparison.Ordinal);
        Assert.Contains("storage.connectionString: (not set)", All("settings-effective.txt"), StringComparison.Ordinal);
        Assert.Contains("email.smtp.user: (set)", All("settings-effective.txt"), StringComparison.Ordinal);
        Assert.Contains("email.smtp.passwordFile: (set)", All("settings-effective.txt"), StringComparison.Ordinal);
        Assert.Contains("ai.endpoint: (set; host ai.example.com)", All("settings-effective.txt"), StringComparison.Ordinal);
        Assert.Contains("DRAUGHTSMAN_SOMETHING_ELSE: (set)", All("settings-effective.txt"), StringComparison.Ordinal);
        Assert.Contains("ASPNETCORE_URLS: http://0.0.0.0:5180", All("settings-effective.txt"), StringComparison.Ordinal);
        Assert.Contains("Draughtsman 0.1.0; data folder /data; storage sqlite", All("log-tail.txt"), StringComparison.Ordinal);
        Assert.Contains("Now listening on: http://0.0.0.0:5180", All("log-tail.txt"), StringComparison.Ordinal);
        Assert.Contains("Security event sign_in.failed", All("log-tail.txt"), StringComparison.Ordinal);
        Assert.Contains("v***@planted-example.com", All("log-tail.txt"), StringComparison.Ordinal);
        Assert.Contains("203.0.113.0/24", All("log-tail.txt"), StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.77", string.Concat(entries.Select(e => e.Content)), StringComparison.Ordinal);

        // 3. The events came from the database, with the address cut to its /24 and the email to its first letter.
        string events = All("security-events.txt");
        Assert.Contains("sign_in.failed", events, StringComparison.Ordinal);
        Assert.Contains("from 203.0.113.0/24", events, StringComparison.Ordinal);
        Assert.Contains("p***@example.org", events, StringComparison.Ordinal);
        Assert.Contains("token.created", events, StringComparison.Ordinal);
        Assert.DoesNotContain("planted.attacker", string.Concat(entries.Select(e => e.Content)), StringComparison.Ordinal);
        // What the product itself wrote to its log about the document and the token is there, with what a person typed taken out.
        Assert.Contains("Security event document.deleted: [document title and details withheld]", All("log-tail.txt"), StringComparison.Ordinal);
        Assert.Contains("[name withheld]", All("log-tail.txt"), StringComparison.Ordinal);
        Assert.Matches(@"document\.deleted via session from \S+ actor \S+ target -\n", events);

        // 4. Listing: names, never contents; counts for the folders that hold assets, keys or mail.
        string listing = All("data-folder-listing.txt");
        Assert.Contains("data.db", listing, StringComparison.Ordinal);
        Assert.Contains("entries inside, not listed", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("20261006-reset.eml", listing, StringComparison.Ordinal);

        // 5. A licence file an earlier build left in the folder is never read into the bundle.
        Assert.DoesNotContain("Planted Holdings", string.Concat(entries.Select(e => e.Content)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_search_finds_a_leak_when_one_is_put_there_so_a_clean_result_means_something()
    {
        var plant = new Plant();
        string folder = NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "draughtsman.yaml"), "server:\n  port: 5180\n");
            IReadOnlyList<BundleEntry> clean = await SupportBundle.BuildAsync(folder, new SupportBundleOptions { Probes = OfflineProbes.Make(now: s_now), Environment = new Dictionary<string, string>(), Now = s_now });
            plant.Add("meant to be found", "ThisSecretShouldBeFoundIfPresent");

            Assert.Empty(Leaks(Zip(clean), plant.Secrets));
            // The same bundle with one entry carrying the secret, in a name, in text, and inside a nested zip:
            List<BundleEntry> dirty = [.. clean];
            dirty[3] = dirty[3] with { Content = dirty[3].Content + "\nThisSecretShouldBeFoundIfPresent\n" };
            Assert.Single(Leaks(Zip(dirty), plant.Secrets));
            dirty[3] = clean[3];
            dirty.Add(new BundleEntry("ThisSecretShouldBeFoundIfPresent.txt", "x"));
            Assert.Single(Leaks(Zip(dirty), plant.Secrets));
            dirty.RemoveAt(dirty.Count - 1);
            byte[] inner = Zip([new BundleEntry("deep.txt", "..ThisSecretShouldBeFoundIfPresent..")]);
            using var holder = new MemoryStream();
            using (var outer = new ZipArchive(holder, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (BundleEntry entry in clean)
                {
                    using Stream s = outer.CreateEntry(entry.Name).Open();
                    s.Write(Encoding.UTF8.GetBytes(entry.Content));
                }

                using Stream nested = outer.CreateEntry("nested.zip").Open();
                nested.Write(inner);
            }

            Assert.Single(Leaks(holder.ToArray(), plant.Secrets));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_stopped_server_is_bundled_from_the_folder_alone()
    {
        var plant = new Plant();
        string folder = NewFolder();
        try
        {
            PlantFiles(plant, folder);
            File.WriteAllText(Path.Combine(folder, DraughtsmanConfig.FileName), Yaml(plant, folder));
            // A database with events and nobody running: made the way the server makes it, then closed.
            StorageOptions storage = StorageOptions.FromConfig("sqlite", string.Empty, folder);
            await using (DraughtsmanDbContext db = StorageSetup.CreateContext(storage))
            {
                await db.Database.MigrateAsync();
                var events = new EfSecurityEventStore(db);
                await events.Append(new SecurityEvent(SecurityEventTypes.SignInFailed, s_now.AddHours(-1), null, "casual.visitor@example.net", null, null, "198.51.100.200", SecurityEventMethods.Anonymous, "wrong password"));
                await events.Append(new SecurityEvent(SecurityEventTypes.SignInSucceeded, s_now.AddDays(-3), "u_1", "owner@example.net", null, null, "2001:db8:abcd:12:1:2:3:4", SecurityEventMethods.Password, null));
                await events.Append(new SecurityEvent(SecurityEventTypes.SignInSucceeded, s_now.AddDays(-30), "u_1", "owner@example.net", null, null, "198.51.100.9", SecurityEventMethods.Password, null));
            }

            // The context's pooled connection is what keeps a write-ahead log alive after it is disposed.
            SqlitePools.Release(folder);
            Assert.False(File.Exists(Path.Combine(folder, "data.db-wal")), "precondition: no write-ahead log before the bundle");
            IReadOnlyList<BundleEntry> entries = await SupportBundle.BuildAsync(folder, new SupportBundleOptions { Probes = OfflineProbes.Make(now: s_now), Environment = Environment(plant), Now = s_now });

            string events2 = entries.Single(e => e.Name == "security-events.txt").Content;
            Assert.Matches(@"\s1  sign_in\.failed", events2);
            // The 7 day window: the 30 day old event is in the list, not in the counts.
            Assert.Contains("sign_in.succeeded", events2, StringComparison.Ordinal);
            Assert.Contains("3 in all", events2, StringComparison.Ordinal);
            Assert.Contains("from 198.51.100.0/24", events2, StringComparison.Ordinal);
            Assert.Contains("from 2001:db8:abcd:12::/64", events2, StringComparison.Ordinal);
            Assert.Contains("c***@example.net", events2, StringComparison.Ordinal);
            Assert.DoesNotContain("198.51.100.200", events2, StringComparison.Ordinal);
            Assert.Contains("Nothing answered", entries.Single(e => e.Name == "health.txt").Content, StringComparison.Ordinal);
            Assert.Contains("[applied]", entries.Single(e => e.Name == "migrations.txt").Content, StringComparison.Ordinal);
            Assert.Empty(Leaks(Zip(entries), plant.Secrets));
            // Reading the events created nothing beside the database.
            Assert.False(File.Exists(Path.Combine(folder, "data.db-wal")), "a bundle must not leave a write-ahead log behind");
            Assert.False(File.Exists(Path.Combine(folder, "data.db-shm")), "a bundle must not leave a shared-memory file behind");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_postgres_connection_string_and_its_password_never_reach_the_zip()
    {
        var plant = new Plant();
        string folder = NewFolder();
        try
        {
            string connection = $"Host=127.0.0.1;Port=1;Database=draught;Username=draught;Password={plant.Add("postgres password", "PgPassPlanted-8d7c6b5a")};Timeout=1;Command Timeout=1";
            plant.Add("connection string", connection);
            File.WriteAllText(Path.Combine(folder, DraughtsmanConfig.FileName), $"storage:\n  provider: postgres\n  connectionString: \"{connection}\"\n");

            IReadOnlyList<BundleEntry> entries = await SupportBundle.BuildAsync(folder, new SupportBundleOptions { Probes = OfflineProbes.Make(now: s_now), Environment = new Dictionary<string, string>(), Now = s_now });

            Assert.Empty(Leaks(Zip(entries), plant.Secrets));
            Assert.Contains("storage.connectionString: (set)", entries.Single(e => e.Name == "settings-effective.txt").Content, StringComparison.Ordinal);
            Assert.Contains("storage.provider: postgres", entries.Single(e => e.Name == "settings-effective.txt").Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_zip_is_plain_text_with_unix_line_breaks_and_a_readme_that_lists_what_it_holds_and_what_it_does_not()
    {
        string folder = NewFolder();
        try
        {
            IReadOnlyList<BundleEntry> entries = await SupportBundle.BuildAsync(folder, new SupportBundleOptions { Probes = OfflineProbes.Make(now: s_now), Environment = new Dictionary<string, string>(), Now = s_now });
            byte[] zip = Zip(entries);

            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            Assert.Equal(SupportBundle.FileNames, archive.Entries.Select(e => e.FullName));
            string readme = entries[0].Content;
            foreach (string name in SupportBundle.FileNames.Skip(1))
            {
                Assert.Contains(name, readme, StringComparison.Ordinal);
            }

            Assert.Contains("WHAT IS NOT IN THIS ZIP", readme, StringComparison.Ordinal);
            foreach (string never in new[] { "the database", "password hashes", "the key ring", "the setup code", "Postgres connection string" })
            {
                Assert.Contains(never, readme, StringComparison.Ordinal);
            }

            Assert.Contains("read each file before you send it", readme, StringComparison.OrdinalIgnoreCase);
            Assert.All(entries, e => Assert.DoesNotContain('\r', e.Content));
            Assert.All(entries, e => Assert.EndsWith("\n", e.Content, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_log_tail_keeps_only_the_last_lines_and_says_where_there_is_no_log()
    {
        string folder = NewFolder();
        try
        {
            IReadOnlyList<BundleEntry> none = await SupportBundle.BuildAsync(folder, new SupportBundleOptions { Probes = OfflineProbes.Make(now: s_now), Environment = new Dictionary<string, string>(), Now = s_now });
            string noLog = none.Single(e => e.Name == "log-tail.txt").Content;
            Assert.Contains("There is no log file to include", noLog, StringComparison.Ordinal);
            Assert.Contains("docker logs <container name>", noLog, StringComparison.Ordinal);

            Directory.CreateDirectory(Path.Combine(folder, "logs"));
            File.WriteAllLines(Path.Combine(folder, "logs", "draughtsman-20261004.log"), Enumerable.Range(1, 5).Select(i => $"2026-10-04T08:00:0{i}.000Z INF cat: older line {i}"));
            File.WriteAllLines(Path.Combine(folder, "logs", "draughtsman-20261005.log"), Enumerable.Range(1, 5).Select(i => $"2026-10-05T08:00:0{i}.000Z INF cat: newer line {i}"));
            File.SetLastWriteTimeUtc(Path.Combine(folder, "logs", "draughtsman-20261004.log"), s_now.UtcDateTime.AddDays(-2));
            File.SetLastWriteTimeUtc(Path.Combine(folder, "logs", "draughtsman-20261005.log"), s_now.UtcDateTime.AddDays(-1));

            string seven = (await SupportBundle.BuildAsync(folder, new SupportBundleOptions { LogLines = 7, Probes = OfflineProbes.Make(now: s_now), Environment = new Dictionary<string, string>(), Now = s_now })).Single(e => e.Name == "log-tail.txt").Content;
            string zero = (await SupportBundle.BuildAsync(folder, new SupportBundleOptions { LogLines = 0, Probes = OfflineProbes.Make(now: s_now), Environment = new Dictionary<string, string>(), Now = s_now })).Single(e => e.Name == "log-tail.txt").Content;

            string[] logLines = [.. seven.Split('\n').Where(l => l.Contains(" INF cat: ", StringComparison.Ordinal))];
            Assert.Equal(7, logLines.Length);
            Assert.Equal("2026-10-04T08:00:04.000Z INF cat: older line 4", logLines[0]);
            Assert.Equal("2026-10-05T08:00:05.000Z INF cat: newer line 5", logLines[^1]);
            Assert.DoesNotContain("older line 3", seven, StringComparison.Ordinal);
            Assert.DoesNotContain(" INF cat: ", zero, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------------------------------ the settings allow-list

    [Fact]
    public void Every_setting_that_is_not_on_the_allow_list_shows_only_that_it_is_set()
    {
        // A sentinel in EVERY string and list the configuration has, found by reflection, so a setting added next year is covered without anyone remembering to add it here.
        var config = new DraughtsmanConfig();
        var paths = new List<(string Path, string Sentinel)>();
        Fill(config, string.Empty, paths);
        Assert.True(paths.Count >= 35, "the reflection found the settings");

        string text = string.Join('\n', EffectiveSettings.Lines(config));

        foreach ((string path, string sentinel) in paths)
        {
            bool shown = text.Contains(sentinel, StringComparison.Ordinal);
            Assert.True(EffectiveSettings.Allowed.Contains(path) == shown, EffectiveSettings.Allowed.Contains(path) ? $"{path} is allow-listed but its value is not shown" : $"{path} is NOT allow-listed but its value reached the output");
            if (!EffectiveSettings.Allowed.Contains(path))
            {
                Assert.Matches($@"(^|\n){System.Text.RegularExpressions.Regex.Escape(path)}: \(set(, \d+ entr(y|ies))?\)(\n|$)", text);
            }
        }
    }

    [Fact]
    public void Every_entry_on_the_allow_list_is_a_real_setting()
    {
        var paths = new List<(string Path, string Sentinel)>();
        var numbers = new List<string>();
        Fill(new DraughtsmanConfig(), string.Empty, paths);
        CollectLeafPaths(new DraughtsmanConfig(), string.Empty, numbers);

        // A typo in the allow-list would silently hide a setting nobody meant to hide.
        foreach (string allowed in EffectiveSettings.Allowed)
        {
            Assert.Contains(allowed, numbers);
        }
    }

    [Fact]
    public void A_setting_at_its_default_shows_not_set_and_one_that_was_changed_shows_set()
    {
        var config = new DraughtsmanConfig();
        string[] before = [.. EffectiveSettings.Lines(config)];
        config.Storage.ConnectionString = "Host=x;Password=y";
        config.Email.Smtp.User = "someone";
        config.KeyRing.LegacyInstallPaths = ["/a", "/b"];
        string[] after = [.. EffectiveSettings.Lines(config)];

        Assert.Contains("storage.connectionString: (not set)", before);
        Assert.Contains("storage.connectionString: (set)", after);
        Assert.Contains("email.smtp.user: (set)", after);
        Assert.Contains("keyRing.legacyInstallPaths: (set, 2 entries)", after);
        Assert.Contains("server.port: 5180", before);
    }

    [Fact]
    public void Known_secrets_are_collected_by_value_from_the_environment_the_settings_and_the_files()
    {
        string folder = NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "pw.txt"), "FilePasswordValue-1234\n");
            File.WriteAllText(Path.Combine(folder, "setup-code.txt"), "Draughtsman first-run setup code: K7M2QXPV4N\n");
            var config = new DraughtsmanConfig();
            config.Email.Smtp.PasswordFile = Path.Combine(folder, "pw.txt");
            config.Storage.ConnectionString = "Host=db;Password=PgPlanted-998877;Username=u";
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DRAUGHTSMAN_AI_KEY"] = "AiKeyValue-abcdef123456",
                ["MY_SERVICE_PASSWORD"] = "ServicePwValue-777888",
                ["HARMLESS"] = "harmless-value-here",
                ["SHORT_SECRET"] = "abc",
            };

            IReadOnlyList<string> known = SupportBundle.KnownSecrets(config, folder, environment);

            Assert.Contains("AiKeyValue-abcdef123456", known);
            Assert.Contains("ServicePwValue-777888", known);
            Assert.Contains("FilePasswordValue-1234", known);
            Assert.Contains("PgPlanted-998877", known);
            Assert.Contains("K7M2QXPV4N", known);
            Assert.DoesNotContain("harmless-value-here", known);
            Assert.DoesNotContain("abc", known); // too short to scrub without shredding the text
            Assert.Equal("see [redacted] and [redacted]", SupportBundle.Scrub("see ServicePwValue-777888 and AiKeyValue-abcdef123456", known));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------------------------------ the command

    [Fact]
    public async Task The_command_writes_the_zip_lists_its_files_and_never_overwrites()
    {
        string folder = NewFolder();
        string output = Path.Combine(folder, "out", "bundle.zip");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var options = new SupportBundleOptions { Probes = OfflineProbes.Make(now: s_now), Environment = new Dictionary<string, string>(), Now = s_now };
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            int first = await SupportBundleCommand.RunAsync(["--output", output, "--lines", "10"], stdout, stderr, folder, options);
            int second = await SupportBundleCommand.RunAsync([$"--output={output}"], new StringWriter(), stderr, folder, options);

            Assert.Equal(0, first);
            Assert.True(File.Exists(output));
            Assert.Contains($"Wrote {output}", stdout.ToString(), StringComparison.Ordinal);
            foreach (string name in SupportBundle.FileNames)
            {
                Assert.Contains("  " + name, stdout.ToString(), StringComparison.Ordinal);
            }

            Assert.Contains("Read README.txt inside it", stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, second);
            Assert.Contains("never overwrites", stderr.ToString(), StringComparison.Ordinal);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(output));
            }

            using var zip = ZipFile.OpenRead(output);
            Assert.Equal(SupportBundle.FileNames, zip.Entries.Select(e => e.FullName));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("--nope")]
    [InlineData("--output")]
    [InlineData("--lines")]
    public async Task A_bad_command_line_is_refused_with_the_usage_exit_code(string argument)
    {
        var stderr = new StringWriter();

        int exit = await SupportBundleCommand.RunAsync([argument], new StringWriter(), stderr, NewFolderPath());

        Assert.Equal(SupportBundleCommand.UsageExitCode, exit);
        Assert.Contains("Usage: draughtsman support-bundle", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_verb_is_wired_into_the_dispatcher_and_the_help()
    {
        var stdout = new StringWriter();
        CliOutcome help = await ServerCli.ExecuteAsync(["--help"], stdout, new StringWriter());
        CliOutcome verb = await ServerCli.ExecuteAsync(["support-bundle", "--help"], stdout, new StringWriter());

        Assert.False(help.Serve);
        Assert.False(verb.Serve);
        Assert.Equal(0, verb.ExitCode);
        Assert.Contains("draughtsman support-bundle [--output <file>]", stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("draughtsman doctor", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(CliVerb.SupportBundle, CliCommand.Parse(["support-bundle"]).Verb);
        Assert.Equal(CliVerb.Doctor, CliCommand.Parse(["doctor", "--json"]).Verb);
    }

    // ------------------------------------------------------------------------------------------------------ helpers

    private static void Fill(object current, string prefix, List<(string Path, string Sentinel)> paths)
    {
        foreach (PropertyInfo property in current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string path = prefix + char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type == typeof(string))
            {
                string sentinel = "SENTINEL<" + path + ">";
                property.SetValue(current, sentinel);
                paths.Add((path, sentinel));
            }
            else if (type == typeof(List<string>))
            {
                string sentinel = "SENTINEL<" + path + ">";
                property.SetValue(current, new List<string> { sentinel });
                paths.Add((path, sentinel));
            }
            else if (type.IsClass)
            {
                Fill(property.GetValue(current)!, path + ".", paths);
            }
        }
    }

    private static void CollectLeafPaths(object current, string prefix, List<string> paths)
    {
        foreach (PropertyInfo property in current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string path = prefix + char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type.IsClass && type != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            {
                CollectLeafPaths(property.GetValue(current)!, path + ".", paths);
            }
            else
            {
                paths.Add(path);
            }
        }
    }

    private static string NewFolder()
    {
        string folder = NewFolderPath();
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string NewFolderPath()
    {
        return Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
    }
}

/// <summary>What a person reads in the bundle must read as written: found by looking at a real one (issue: the redactor blanked "auth.tokens.defaultLifetimeDays" and the README's own words).</summary>
public class SupportBundleReadabilityTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_settings_listing_keeps_the_values_of_settings_whose_names_merely_contain_a_secret_word_and_the_readme_keeps_its_own_words_and_the_contact()
    {
        string folder = Path.Combine(Path.GetTempPath(), "draughtsman-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "draughtsman.yaml"), "about:\n  supportContact: help@acme.example\nemail:\n  smtp:\n    passwordEnvironmentVariable: ACME_SMTP_PASSWORD\nserver:\n  publicUrl: \"https://admin:HiddenUrlPass-1@draughtsman.acme.example\"\n");

            IReadOnlyList<BundleEntry> entries = await SupportBundle.BuildAsync(folder, new SupportBundleOptions { Probes = OfflineProbes.Make(now: s_now), Environment = new Dictionary<string, string>(), Now = s_now });

            string settings = entries.Single(e => e.Name == "settings-effective.txt").Content;
            Assert.Contains("auth.tokens.defaultLifetimeDays: 90\n", settings, StringComparison.Ordinal);
            Assert.Contains("auth.passwordReset.selfService: true\n", settings, StringComparison.Ordinal);
            Assert.Contains("retention.expiredSessionDays: 7\n", settings, StringComparison.Ordinal);
            Assert.Contains("ai.apiKeyEnvironmentVariable: DRAUGHTSMAN_AI_KEY\n", settings, StringComparison.Ordinal);
            Assert.Contains("email.smtp.passwordEnvironmentVariable: ACME_SMTP_PASSWORD\n", settings, StringComparison.Ordinal);
            // What a value can hold is still taken out of it: the user and password in a web address.
            Assert.Contains("server.publicUrl: https://[redacted]@draughtsman.acme.example\n", settings, StringComparison.Ordinal);
            Assert.DoesNotContain("HiddenUrlPass-1", settings, StringComparison.Ordinal);

            string readme = entries[0].Content;
            Assert.Contains("Send the whole zip to help@acme.example.", readme, StringComparison.Ordinal);
            Assert.Contains("the setup code for first sign-in", readme, StringComparison.Ordinal);
            Assert.Contains("a Bearer or Basic credential", readme, StringComparison.Ordinal);
            Assert.DoesNotContain("[redacted]", readme, StringComparison.Ordinal);
            Assert.DoesNotContain("[masked]", readme, StringComparison.Ordinal);
        }
        finally
        {
            SqlitePools.Release(folder);
            Directory.Delete(folder, recursive: true);
        }
    }
}
