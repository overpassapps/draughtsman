using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Draughtsman.Server.Tests;

/// <summary>Shared plumbing for the authentication-bar test classes (security events, token scope and expiry, password change).</summary>
internal static class AuthBarHelpers
{
    public const string UserPassword = "correcthorsebattery";

    public static async Task<JsonNode> Json(this HttpResponseMessage response)
    {
        string text = await response.Content.ReadAsStringAsync();
        return JsonNode.Parse(text) ?? throw new InvalidOperationException("Empty body.");
    }

    public static string? Cookie(this HttpResponseMessage response, string cookieName)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values))
        {
            return null;
        }

        foreach (string raw in values)
        {
            string namePart = raw.Split(';')[0];
            int eq = namePart.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && string.Equals(namePart[..eq], cookieName, StringComparison.Ordinal))
            {
                return namePart[(eq + 1)..];
            }
        }

        return null;
    }

    /// <summary>Creates the user through <paramref name="admin"/> and returns the created user's id.</summary>
    public static async Task<string> CreateUser(this HttpClient admin, string email, string role = "Editor", string password = UserPassword)
    {
        HttpResponseMessage created = await admin.PostAsJsonAsync("/api/users", new { email, displayName = email.Split('@')[0], role, password });
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        return (await created.Json())["id"]!.GetValue<string>();
    }

    /// <summary>A fresh client signed in as the user by password, carrying that session's CSRF header.</summary>
    public static async Task<HttpClient> SignIn(this DraughtsmanServerFactory factory, string email, string password = UserPassword)
    {
        HttpClient client = factory.CreateClient();
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(System.Net.HttpStatusCode.OK, login.StatusCode);
        client.DefaultRequestHeaders.Add(AuthTestHelpers.XsrfHeaderName, login.Cookie("XSRF-TOKEN")!);
        return client;
    }

    public static HttpClient Bearer(this DraughtsmanServerFactory factory, string secret)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return client;
    }

    /// <summary>Mints a token for the signed-in client's own user and returns (id, secret).</summary>
    public static async Task<(string Id, string Secret)> CreateToken(this HttpClient client, string name, object? extra = null)
    {
        var body = new JsonObject { ["name"] = name };
        if (extra is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in JsonSerializer.SerializeToNode(extra)!.AsObject())
            {
                body[pair.Key] = pair.Value?.DeepClone();
            }
        }

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/tokens", body);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        JsonNode json = await response.Json();
        return (json["id"]!.GetValue<string>(), json["token"]!.GetValue<string>());
    }

    /// <summary>Reads the security events through the admin API with a raw query string (for example <c>?type=token.created</c>).</summary>
    public static async Task<JsonArray> SecurityEvents(this HttpClient admin, string query = "")
    {
        HttpResponseMessage response = await admin.GetAsync("/api/admin/security-events" + query);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return (await response.Json())["items"]!.AsArray();
    }

    public static HttpRequestMessage Request(HttpMethod method, string url, object? body = null, HttpClient? withCsrfFrom = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (withCsrfFrom is not null)
        {
            request.Headers.Add(AuthTestHelpers.XsrfHeaderName, withCsrfFrom.DefaultRequestHeaders.GetValues(AuthTestHelpers.XsrfHeaderName).Single());
        }

        return request;
    }
}
