using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Draughtsman.Server.Tests;

/// <summary>
/// One sweeping test over the registered endpoint list (build-spec-v0.1.md section 7): every route must require a
/// signed-in session or a valid API token except the documented exceptions, so a new route added without thinking
/// about auth fails this test rather than shipping unprotected. It is deliberately behavioural (a real anonymous
/// request per endpoint, asserting 401) rather than a metadata check, since the global fallback policy that protects
/// most routes here attaches no metadata of its own to inspect - only AllowAnonymous is visible statically.
/// </summary>
public class ProtectedRoutesTests : IClassFixture<DraughtsmanServerFactory>
{
    /// <summary>
    /// (HTTP method, normalised route pattern) pairs that are deliberately reachable without signing in. Patterns are
    /// normalised the same way <see cref="Normalize"/> normalises every discovered endpoint's own pattern, so a
    /// grouped endpoint's trailing slash (<c>MapGroup("/api/documents")</c> + <c>MapGet("", ...)</c> stringifies as
    /// <c>/api/documents/</c>) does not make this list fragile against how ASP.NET Core happens to render it.
    /// </summary>
    private static readonly HashSet<(string Method, string Pattern)> AllowedAnonymous = new()
    {
        ("GET", "/api/health"),
        ("GET", "/api/theme"),
        ("GET", "/api/theme/chrome.css"),
        ("GET", "/api/theme/logo"),
        ("GET", "/api/theme/logo-light"),
        ("GET", "/api/theme/logo-compact"),
        ("GET", "/api/theme/logo-compact-light"),
        ("GET", "/api/theme/favicon"),
        ("GET", "/api/auth/status"),
        ("POST", "/api/auth/first-run"),
        ("POST", "/api/auth/login"),
        ("POST", "/api/auth/password-reset/request"), // "Forgot password?": the caller has no session by definition (PasswordResetEmailTests)
        ("POST", "/api/auth/password-reset/complete"), // spends a reset link; the link is the credential (AdminResetLinkTests)
        ("GET", "/api/{**rest}"), // the unknown-API-route catch-all: stays a 404, not a 401 (HealthEndpointTests)
        ("GET", "/.well-known/{**rest}"), // OAuth discovery probes stay a 404, not the SPA shell or a 401 (BearerChallengeTests)
        ("GET", "/{*path:nonfile}"), // the SPA's own client-side-routing fallback (StaticWeb.MapSpaFallback)
        ("HEAD", "/{*path:nonfile}"), // MapFallbackToFile answers HEAD too
    };

    private static readonly HashSet<string> MethodsWithBody = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH" };

    /// <summary>The few endpoints whose declared <c>Accepts</c> content type is not JSON (ImportEndpoints.cs).</summary>
    private static readonly Dictionary<string, string> ContentTypeOverrides = new()
    {
        ["/api/import/mermaid"] = "text/plain",
        ["/api/import/dsl"] = "text/plain",
        ["/api/import/sql"] = "text/plain",
        ["/api/import/drawio"] = "text/plain",
        ["/api/import/visio"] = "application/octet-stream",
    };

    private readonly DraughtsmanServerFactory _factory;

    public ProtectedRoutesTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Every_registered_route_requires_authentication_except_the_documented_exceptions()
    {
        List<RouteEndpoint> endpoints = _factory.Services.GetRequiredService<IEnumerable<EndpointDataSource>>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is not null)
            .ToList();
        Assert.NotEmpty(endpoints);

        // A sanity check on the sweep itself: if these known-protected routes vanished from the endpoint list, the
        // rest of this test would trivially pass having examined nothing that matters.
        Assert.Contains(endpoints, e => Normalize(e.RoutePattern.RawText!) == "/api/documents");
        Assert.Contains(endpoints, e => Normalize(e.RoutePattern.RawText!) == "/mcp");

        using HttpClient anonymous = _factory.CreateClient();
        var failures = new List<string>();

        foreach (RouteEndpoint endpoint in endpoints)
        {
            string pattern = Normalize(endpoint.RoutePattern.RawText!);
            bool declaredAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            IReadOnlyList<string> methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"];

            foreach (string method in methods)
            {
                bool expectedAnonymous = AllowedAnonymous.Contains((method, pattern));

                // Every declared-anonymous endpoint must be one we actually expect, so an accidental AllowAnonymous
                // on a sensitive route is caught here rather than only by the (missing) exceptions-list entry below.
                if (declaredAnonymous)
                {
                    if (!expectedAnonymous)
                    {
                        failures.Add($"{method} {pattern} is AllowAnonymous but is not in the expected exceptions list.");
                    }

                    continue;
                }

                if (expectedAnonymous)
                {
                    failures.Add($"{method} {pattern} is expected to be anonymous but has no AllowAnonymous metadata.");
                    continue;
                }

                // A POST/PUT/PATCH endpoint with a JSON body parameter carries an implicit "Accepts application/json"
                // requirement that ASP.NET Core's routing itself matches on (ConsumesMatcherPolicy): a request with
                // no compatible Content-Type is not merely a bad request to that endpoint, it fails to *match* it at
                // all and falls through to the next candidate - here, the /api/{**rest} catch-all's own 404. A body
                // and Content-Type are enough to route correctly to the real endpoint and reach the 401 this test is after.
                var request = new HttpRequestMessage(new HttpMethod(method), SubstitutePlaceholders(pattern));
                if (MethodsWithBody.Contains(method))
                {
                    string contentType = ContentTypeOverrides.GetValueOrDefault(pattern, "application/json");
                    string body = contentType == "application/json" ? "{}" : "x";
                    request.Content = new StringContent(body, System.Text.Encoding.UTF8, contentType);
                }

                HttpResponseMessage response = await anonymous.SendAsync(request);
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    failures.Add($"{method} {pattern} answered {(int)response.StatusCode} {response.StatusCode} to an anonymous request, not 401.");
                }
            }
        }

        Assert.True(failures.Count == 0, "Unprotected or misclassified routes:\n" + string.Join('\n', failures));
    }

    /// <summary>A leading slash and no trailing one, so a grouped endpoint's stringified pattern compares like a real request path.</summary>
    private static string Normalize(string rawText)
    {
        string withLeadingSlash = rawText.StartsWith('/') ? rawText : "/" + rawText;
        return withLeadingSlash.Length > 1 && withLeadingSlash.EndsWith('/') ? withLeadingSlash[..^1] : withLeadingSlash;
    }

    /// <summary>
    /// Replaces every route parameter with a harmless placeholder so the router matches this endpoint specifically.
    /// The value never needs to be a real id: authorization runs before the handler, so any string that only fails
    /// to *exist* still reaches - and is refused by - the auth check this test is exercising.
    /// </summary>
    private static string SubstitutePlaceholders(string pattern)
    {
        IEnumerable<string> segments = pattern.Split('/').Select(segment =>
            segment.StartsWith('{') && segment.EndsWith('}') ? "placeholder" : segment);
        return string.Join('/', segments);
    }
}
