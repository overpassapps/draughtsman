using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Draughtsman.Server.Tests.Upgrade;

/// <summary>
/// What an upgrade must leave readable through the HTTP API, checked against expected.json (what the build that wrote the fixture
/// served before it was stopped): the same checks scripts/upgrade-fixture/verify.mjs makes against a running binary, here made
/// in process against the CURRENT code. Every check is recorded, none stops the others, so one run lists everything an upgrade broke.
/// </summary>
internal sealed class UpgradeAssertions(UpgradeFixture fixture, JsonElement expected, Func<string, JsonElement?> storedSnapshotBefore)
{
    private readonly List<string> _problems = [];
    private int _checks;

    public IReadOnlyList<string> Problems => _problems;

    public int Checks => _checks;

    public void Check(string name, bool ok, string detail = "")
    {
        _checks++;
        if (!ok)
        {
            _problems.Add(detail.Length == 0 ? name : $"{name}: {detail}");
        }
    }

    private IEnumerable<JsonElement> Items(string name)
    {
        return expected.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : [];
    }

    private static string Str(JsonElement e, string name)
    {
        return e.GetProperty(name).GetString()!;
    }

    private static string? OptStr(JsonElement e, string name)
    {
        return e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    // ------------------------------------------------------------------------------------------------------------ sign-in

    /// <summary>Signs a client in with a user's known password; returns it with the antiforgery header set, or null (and a recorded problem) when it fails.</summary>
    public async Task<HttpClient?> SignIn(WebApplicationFactory<Program> factory, JsonElement user)
    {
        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", new { email = Str(user, "email"), password = Str(user, "password") });
        Check($"{Str(user, "email")} signs in with the same password", response.IsSuccessStatusCode, $"status {(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string raw in cookies)
            {
                if (raw.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal))
                {
                    client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
                    client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(raw.Split(';')[0]["XSRF-TOKEN=".Length..]));
                }
            }
        }

        return client;
    }

    public JsonElement User(string key)
    {
        return Items("users").Single(u => Str(u, "key") == key);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, byte[] Bytes, HttpResponseMessage Response)> Get(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        JsonElement body = default;
        if (bytes.Length > 0 && (response.Content.Headers.ContentType?.MediaType ?? string.Empty).Contains("json", StringComparison.Ordinal))
        {
            body = JsonDocument.Parse(bytes).RootElement.Clone();
        }

        return (response.StatusCode, body, bytes, response);
    }

    // ------------------------------------------------------------------------------------------------------------ users and tokens

    public async Task Users(WebApplicationFactory<Program> factory, HttpClient admin)
    {
        (HttpStatusCode status, JsonElement list, _, _) = await Get(admin, "/api/users");
        Check("GET /api/users answers", status == HttpStatusCode.OK, $"status {(int)status}");
        if (status != HttpStatusCode.OK)
        {
            return;
        }

        foreach (JsonElement user in Items("users"))
        {
            string email = Str(user, "email");
            JsonElement? row = list.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(u => u!.Value.GetProperty("email").GetString() == email);
            Check($"user {email} is still there", row is not null);
            if (row is null)
            {
                continue;
            }

            Check($"user {email} keeps id {Str(user, "id")}", Str(row.Value, "id") == Str(user, "id"));
            Check($"user {email} keeps role {Str(user, "role")}", Str(row.Value, "role") == Str(user, "role"), $"now {Str(row.Value, "role")}");
            bool active = !row.Value.TryGetProperty("active", out JsonElement a) || a.GetBoolean();
            Check($"user {email} keeps active={user.GetProperty("active").GetBoolean()}", active == user.GetProperty("active").GetBoolean());
            if (Str(user, "key") == "admin")
            {
                continue;
            }

            HttpClient other = factory.CreateClient();
            HttpResponseMessage login = await other.PostAsJsonAsync("/api/auth/login", new { email, password = Str(user, "password") });
            if (user.GetProperty("active").GetBoolean())
            {
                Check($"{email} signs in with the same password", login.IsSuccessStatusCode, $"status {(int)login.StatusCode}");
            }
            else
            {
                Check($"{email} (deactivated) is still refused", login.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"status {(int)login.StatusCode}");
            }
        }
    }

    public async Task Tokens(WebApplicationFactory<Program> factory, HttpClient admin)
    {
        (HttpStatusCode _, JsonElement mine, _, _) = await Get(admin, "/api/tokens");
        foreach (JsonElement token in Items("tokens"))
        {
            string name = Str(token, "name");
            HttpClient client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Str(token, "secret"));
            HttpResponseMessage read = await client.GetAsync("/api/documents?limit=1");
            if (token.GetProperty("revoked").GetBoolean())
            {
                Check($"revoked token \"{name}\" is still refused", read.StatusCode == HttpStatusCode.Unauthorized, $"status {(int)read.StatusCode}");
                continue;
            }

            // A fixture is made once and then ages: a token an earlier build issued with a lifetime stops authenticating on the real clock when it lapses
            // (the handler reads the stored row's expiry against the time of the request). That is the product working, not an upgrade losing the token, so
            // once the stored expiry has passed the right answer is the refusal, and the row is still checked below (listed, with its expiry unchanged).
            string? lapsesAt = OptStr(token, "expiresAt");
            if (lapsesAt is not null && DateTimeOffset.Parse(lapsesAt, System.Globalization.CultureInfo.InvariantCulture) <= DateTimeOffset.UtcNow)
            {
                Check($"token \"{name}\" (lapsed {lapsesAt}) is refused as an expired token", read.StatusCode == HttpStatusCode.Unauthorized, $"status {(int)read.StatusCode}");
            }
            else
            {
                Check($"token \"{name}\" still authenticates", read.IsSuccessStatusCode, $"status {(int)read.StatusCode}");
            }

            if (OptStr(token, "scope") == "read" && read.StatusCode != HttpStatusCode.Unauthorized)
            {
                HttpResponseMessage write = await client.PostAsJsonAsync("/api/documents", new { title = "must not be stored", type = "freeform", nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
                Check($"read-scoped token \"{name}\" is still refused a write", write.StatusCode == HttpStatusCode.Forbidden, $"status {(int)write.StatusCode}");
            }

            if (Str(token, "owner") == "admin" && mine.ValueKind == JsonValueKind.Array)
            {
                JsonElement? row = mine.EnumerateArray().Cast<JsonElement?>().FirstOrDefault(t => t!.Value.GetProperty("id").GetString() == Str(token, "id"));
                Check($"token \"{name}\" is listed for its owner", row is not null);
                if (row is not null)
                {
                    string? expiry = OptStr(token, "expiresAt");
                    if (expiry is not null)
                    {
                        Check($"token \"{name}\" keeps its expiry {expiry}", OptStr(row.Value, "expiresAt") == expiry, $"now {OptStr(row.Value, "expiresAt")}");
                    }
                    else
                    {
                        // A token an older build issued has no lifetime; the migration grandfathers it (no expiry, full scope).
                        Check($"token \"{name}\" (issued before token lifetimes) is still not expiring", OptStr(row.Value, "expiresAt") is null);
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------ documents

    public async Task Documents(HttpClient admin)
    {
        (HttpStatusCode status, JsonElement list, _, _) = await Get(admin, "/api/documents?limit=200");
        Check("the document list answers", status == HttpStatusCode.OK);
        int total = list.ValueKind == JsonValueKind.Object ? list.GetProperty("total").GetInt32() : -1;
        Check("the document count is unchanged", total == expected.GetProperty("documentListTotal").GetInt32(), $"{total} vs {expected.GetProperty("documentListTotal").GetInt32()}");
        IReadOnlyList<Normalisation> allowed = JsonDifferences.Allowed;
        foreach (JsonElement doc in Items("documents"))
        {
            string id = Str(doc, "id");
            (HttpStatusCode s, JsonElement now, _, HttpResponseMessage response) = await Get(admin, $"/api/documents/{id}");
            Check($"document {id} opens", s == HttpStatusCode.OK, $"status {(int)s}");
            if (s != HttpStatusCode.OK)
            {
                continue;
            }

            if (Canonical.Hash(now) == Str(doc, "sha256"))
            {
                Check($"document {id} content hash is unchanged", true);
            }
            else
            {
                List<string> differences = storedSnapshotBefore(id) is { } before ? JsonDifferences.Between(before, now) : ["(the stored snapshot could not be read to say where)"];
                List<string> unexplained = [.. differences.Where(d => !JsonDifferences.IsAllowed(d, allowed))];
                Check($"document {id} is unchanged apart from documented normalisations", unexplained.Count == 0, $"differs at {string.Join(", ", unexplained.Take(8))}");
            }

            Check($"document {id} has the same node and edge counts", now.GetProperty("nodes").GetArrayLength() == doc.GetProperty("nodes").GetInt32() && now.GetProperty("edges").GetArrayLength() == doc.GetProperty("edges").GetInt32());
            Check($"document {id} serves an ETag", response.Headers.ETag is not null);
            Check($"document {id} keeps its fly-through", now.TryGetProperty("flythrough", out _) == doc.GetProperty("hasFlythrough").GetBoolean());
            if (OptStr(doc, "dslSha256") is { } dslHash)
            {
                HttpResponseMessage dsl = await admin.PostAsync("/api/export/dsl", new StringContent(now.GetRawText(), System.Text.Encoding.UTF8, "application/json"));
                Check($"document {id} prints the same DSL", dsl.IsSuccessStatusCode && Canonical.Sha256(await dsl.Content.ReadAsByteArrayAsync()) == dslHash, $"status {(int)dsl.StatusCode}");
            }
        }

        foreach (JsonElement v in Items("versions"))
        {
            string documentId = Str(v, "documentId");
            string versionId = Str(v, "id");
            (HttpStatusCode vs, JsonElement version, _, _) = await Get(admin, $"/api/documents/{documentId}/versions/{versionId}");
            Check($"version {versionId} of {documentId} is still readable", vs == HttpStatusCode.OK, $"status {(int)vs}");
            if (vs != HttpStatusCode.OK)
            {
                continue;
            }

            Check($"version {versionId} has the same snapshot", Canonical.Hash(version.GetProperty("document")) == OptStr(v, "documentSha256"));
            Check($"version {versionId} keeps its name and actor", OptStr(version, "name") == OptStr(v, "name") && OptStr(version, "actor") == OptStr(v, "actor"));
        }
    }

    public async Task Bin(HttpClient admin)
    {
        JsonElement[] binned = [.. Items("binned")];
        if (binned.Length == 0)
        {
            return;
        }

        (HttpStatusCode status, JsonElement bin, _, _) = await Get(admin, "/api/bin?limit=200");
        Check("the bin answers", status == HttpStatusCode.OK, $"status {(int)status}");
        foreach (JsonElement b in binned)
        {
            string id = Str(b, "id");
            Check($"binned document {id} is still in the bin", status == HttpStatusCode.OK && bin.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("id").GetString() == id));
            (HttpStatusCode live, _, _, _) = await Get(admin, $"/api/documents/{id}");
            Check($"binned document {id} is not live", live == HttpStatusCode.NotFound, $"status {(int)live}");
        }
    }

    /// <summary>Takes a binned document out of the bin and checks it comes back exactly as it was deleted, then proves the instance still writes.</summary>
    public async Task RestoreFromBinAndWrite(HttpClient admin)
    {
        JsonElement[] binned = [.. Items("binned")];
        if (binned.Length > 0)
        {
            JsonElement b = binned[0];
            string id = Str(b, "id");
            HttpResponseMessage restore = await admin.PostAsync($"/api/bin/{id}/restore", null);
            Check($"binned document {id} restores", restore.IsSuccessStatusCode, $"status {(int)restore.StatusCode}");
            if (restore.IsSuccessStatusCode)
            {
                (HttpStatusCode s, JsonElement back, _, _) = await Get(admin, $"/api/documents/{id}");
                Check($"restored document {id} is exactly as it was deleted", s == HttpStatusCode.OK && Canonical.Hash(back) == Str(b, "sha256"));
            }
        }

        HttpResponseMessage create = await admin.PostAsJsonAsync("/api/documents", new { title = "Written after the upgrade", type = "freeform", nodes = Array.Empty<object>(), edges = Array.Empty<object>() });
        Check("a new document can be created after the upgrade", create.StatusCode == HttpStatusCode.Created, $"status {(int)create.StatusCode}");
        JsonElement? first = Items("documents").Cast<JsonElement?>().FirstOrDefault(d => d!.Value.GetProperty("nodes").GetInt32() > 0);
        if (first is { } doc)
        {
            string id = Str(doc, "id");
            (HttpStatusCode s, JsonElement now, _, HttpResponseMessage response) = await Get(admin, $"/api/documents/{id}");
            if (s == HttpStatusCode.OK && response.Headers.ETag is { } tag)
            {
                var put = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{id}") { Content = new StringContent(now.GetRawText(), System.Text.Encoding.UTF8, "application/json") };
                put.Headers.IfMatch.Add(tag);
                HttpResponseMessage saved = await admin.SendAsync(put);
                Check($"an old document ({id}) can be saved with the ETag the new build serves", saved.IsSuccessStatusCode, $"status {(int)saved.StatusCode}");
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------ instance settings

    public async Task Assets(HttpClient admin)
    {
        foreach (JsonElement asset in Items("assets"))
        {
            (HttpStatusCode s, _, byte[] bytes, _) = await Get(admin, $"/api/assets/{Str(asset, "id")}");
            Check($"uploaded image {Str(asset, "id")[..12]} is served", s == HttpStatusCode.OK, $"status {(int)s}");
            Check($"uploaded image {Str(asset, "id")[..12]} has the same bytes", s == HttpStatusCode.OK && Canonical.Sha256(bytes) == Str(asset, "sha256"));
        }
    }

    public async Task Branding(HttpClient admin)
    {
        if (!expected.TryGetProperty("branding", out JsonElement branding))
        {
            return;
        }

        (HttpStatusCode s, JsonElement now, _, _) = await Get(admin, "/api/admin/branding");
        Check("branding answers", s == HttpStatusCode.OK, $"status {(int)s}");
        if (s != HttpStatusCode.OK)
        {
            return;
        }

        Check("the product name is unchanged", OptStr(now, "productName") == OptStr(branding, "productName"), $"now {OptStr(now, "productName")}");
        Check("the overridden branding slots are unchanged", Canonical.Write(now.GetProperty("overridden")) == Canonical.Write(branding.GetProperty("state").GetProperty("overridden")));
        foreach (JsonProperty slot in branding.GetProperty("served").EnumerateObject())
        {
            if (!slot.Value.TryGetProperty("sha256", out JsonElement hash))
            {
                continue;
            }

            string path = slot.Name switch { "logo" => "/api/theme/logo", "logo-light" => "/api/theme/logo-light", _ => "/api/theme/favicon" };
            (HttpStatusCode st, _, byte[] bytes, _) = await Get(admin, path);
            Check($"branding {slot.Name} serves the same bytes", st == HttpStatusCode.OK && Canonical.Sha256(bytes) == hash.GetString(), $"status {(int)st}");
        }
    }

    public async Task Themes(HttpClient admin)
    {
        if (!expected.TryGetProperty("themes", out JsonElement themes))
        {
            return;
        }

        (HttpStatusCode s, JsonElement now, _, _) = await Get(admin, "/api/admin/themes");
        Check("organisation themes answer", s == HttpStatusCode.OK, $"status {(int)s}");
        if (s != HttpStatusCode.OK)
        {
            return;
        }

        Check("the default organisation theme is unchanged", OptStr(now, "defaultId") == OptStr(themes, "defaultId"), $"now {OptStr(now, "defaultId")}");
        // A theme's updatedAt is its file's modification time (OrgThemeStore), which a copy or restore of the data folder does not
        // keep, so it is the one member of a theme an upgrade rehearsal cannot compare (README, "Intentional normalisations").
        Check("the organisation themes are unchanged", Canonical.Hash(WithoutUpdatedAt(now)) == Canonical.Hash(WithoutUpdatedAt(themes.GetProperty("list"))));
        (HttpStatusCode cs, _, byte[] css, _) = await Get(admin, "/api/theme/chrome.css");
        Check("the default theme's colours are in the served chrome stylesheet", cs == HttpStatusCode.OK && System.Text.Encoding.UTF8.GetString(css).Contains("#c0392b", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonElement WithoutUpdatedAt(JsonElement list)
    {
        System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(list.GetRawText())!;
        foreach (System.Text.Json.Nodes.JsonNode? theme in node["themes"]?.AsArray() ?? [])
        {
            theme?.AsObject().Remove("updatedAt");
        }

        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    public async Task Ai(HttpClient admin)
    {
        if (!expected.TryGetProperty("ai", out JsonElement ai))
        {
            return;
        }

        (HttpStatusCode s, JsonElement now, _, _) = await Get(admin, "/api/admin/ai");
        Check("AI settings answer", s == HttpStatusCode.OK, $"status {(int)s}");
        if (s != HttpStatusCode.OK)
        {
            return;
        }

        foreach (string field in new[] { "provider", "model", "endpoint" })
        {
            Check($"the AI {field} is unchanged", OptStr(now.GetProperty("effective"), field) == OptStr(ai.GetProperty("state").GetProperty("effective"), field), $"now {OptStr(now.GetProperty("effective"), field)}");
        }

        JsonElement key = now.GetProperty("key");
        // The key an earlier build stored was bound to ITS install path. This host runs from another one, and the key must still read: this build
        // opens it through its legacy fallback and re-protects it under the pinned application name (docs/upgrade-backup.md, "Where to put the new version").
        Check("the stored AI key is still readable by the ring, from a different install path", key.GetProperty("storedSet").GetBoolean() && !key.GetProperty("storedUnreadable").GetBoolean(), $"storedSet {key.GetProperty("storedSet")} storedUnreadable {key.GetProperty("storedUnreadable")}");
    }

    public async Task Email(HttpClient admin)
    {
        if (!expected.TryGetProperty("email", out JsonElement email))
        {
            return;
        }

        (HttpStatusCode s, JsonElement now, _, _) = await Get(admin, "/api/admin/email");
        Check("email settings answer", s == HttpStatusCode.OK, $"status {(int)s}");
        if (s != HttpStatusCode.OK)
        {
            return;
        }

        JsonElement want = email.GetProperty("state");
        foreach (string field in new[] { "from", "host", "tls", "user", "publicUrl" })
        {
            Check($"the email {field} is unchanged", OptStr(now, field) == OptStr(want, field), $"now {OptStr(now, field)}");
        }

        Check("the email port is unchanged", now.GetProperty("port").GetInt32() == want.GetProperty("port").GetInt32());
        // The password an earlier build saved was bound to ITS install path; from this host's different one it must still read (the fallback, then re-protection).
        if (want.TryGetProperty("passwordSet", out JsonElement wasSet) && wasSet.ValueKind == JsonValueKind.True)
        {
            Check("the saved email password is still readable, from a different install path", now.GetProperty("passwordSet").GetBoolean() && !now.GetProperty("passwordUnreadable").GetBoolean(), $"passwordSet {now.GetProperty("passwordSet")} passwordUnreadable {now.GetProperty("passwordUnreadable")}");
        }
        else
        {
            Check("the saved email password is still there", now.GetProperty("passwordSet").GetBoolean() || now.GetProperty("passwordUnreadable").GetBoolean());
        }
    }

    /// <summary>
    /// A data folder an earlier build left a licence file in (and, in some, the setting that named it) is just a file this build never
    /// opens: the instance has started on it, the About page answers without any licence reading, and the file is left exactly as it was.
    /// </summary>
    public async Task OldLicenceFileIsIgnored(HttpClient admin, string dataFolder)
    {
        (HttpStatusCode s, JsonElement about, _, _) = await Get(admin, "/api/about");
        Check("the About page answers", s == HttpStatusCode.OK, $"status {(int)s}");
        if (s == HttpStatusCode.OK)
        {
            Check("the About page carries no licence reading", !about.TryGetProperty("licenceState", out _));
        }

        (HttpStatusCode gone, _, _, _) = await Get(admin, "/api/admin/licence");
        Check("there is no licence page to ask", gone == HttpStatusCode.NotFound, $"status {(int)gone}");
        if (fixture.HasLicence)
        {
            Check("the old licence file is still there, untouched", File.Exists(Path.Combine(dataFolder, "licence.key")));
        }
    }

    public async Task SecurityEvents(HttpClient admin)
    {
        if (!expected.TryGetProperty("securityEvents", out JsonElement events) || events.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        (HttpStatusCode s, JsonElement now, _, _) = await Get(admin, "/api/admin/security-events?limit=200");
        Check("the security log answers", s == HttpStatusCode.OK, $"status {(int)s}");
        if (s != HttpStatusCode.OK)
        {
            return;
        }

        JsonElement items = now.ValueKind == JsonValueKind.Object ? now.GetProperty("items") : now;
        Dictionary<long, string> have = items.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetInt64(), Canonical.Hash);
        long[] missing = [.. events.EnumerateArray().Select(e => e.GetProperty("id").GetInt64()).Where(id => !have.ContainsKey(id))];
        Check("every security event is still in the log", missing.Length == 0, $"{missing.Length} missing");
        long[] changed = [.. events.EnumerateArray().Where(e => have.TryGetValue(e.GetProperty("id").GetInt64(), out string? h) && h != Str(e, "hash")).Select(e => e.GetProperty("id").GetInt64())];
        Check("no security event has changed", changed.Length == 0, $"changed: {string.Join(", ", changed)}");
    }

    public async Task Audit(HttpClient admin)
    {
        if (!expected.TryGetProperty("audit", out JsonElement audit) || audit.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty entry in audit.EnumerateObject())
        {
            (HttpStatusCode s, JsonElement now, _, _) = await Get(admin, $"/api/admin/audit?documentId={entry.Name}&limit=200");
            if (s != HttpStatusCode.OK)
            {
                Check($"audit rows of {entry.Name} answer", false, $"status {(int)s}");
                continue;
            }

            JsonElement items = now.ValueKind == JsonValueKind.Object ? now.GetProperty("items") : now;
            Check($"audit rows of {entry.Name} are all still there", items.GetArrayLength() >= entry.Value.GetArrayLength(), $"{items.GetArrayLength()} vs {entry.Value.GetArrayLength()}");
        }
    }
}
