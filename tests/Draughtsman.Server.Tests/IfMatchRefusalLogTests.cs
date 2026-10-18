using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Draughtsman.Server.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Draughtsman.Server.Tests;

/// <summary>A clock a test moves by hand.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
    }
}

/// <summary>
/// Failure logging that says why (issue 63's lesson: saves failed with 400 and the app log said nothing). A write refused over its
/// <c>If-Match</c> tag writes ONE Warning with the route, the status, the reason and whether the tag was weak, never the tag or the document id,
/// and not more than once per route per minute; a weakened tag the server accepts is an Information line once an hour.
/// </summary>
public class IfMatchRefusalLogTests
{
    // ------------------------------------------------------------------------------------------------------ classifying

    [Theory]
    [InlineData(428, null, IfMatchReason.Missing)]
    [InlineData(428, "", IfMatchReason.Missing)]
    [InlineData(412, "\"dv1:abc\"", IfMatchReason.Stale)]
    [InlineData(412, "W/\"dv1:abc\"", IfMatchReason.Stale)]
    [InlineData(400, "W/\"dv1:abc\"", IfMatchReason.Weak)]
    [InlineData(400, "w/\"dv1:abc\"", IfMatchReason.Weak)]
    [InlineData(400, "*", IfMatchReason.Wildcard)]
    [InlineData(400, "\"a\", \"b\"", IfMatchReason.List)]
    [InlineData(400, "W/\"a\", \"b\"", IfMatchReason.List)]
    [InlineData(400, "not-a-tag", IfMatchReason.Malformed)]
    [InlineData(400, "dv1:abc", IfMatchReason.Malformed)]
    public void A_refusal_over_the_tag_is_classified(int status, string? ifMatch, IfMatchReason expected)
    {
        Assert.Equal(expected, IfMatchClassifier.Refusal(status, ifMatch));
    }

    [Theory]
    [InlineData(400, null)]
    [InlineData(400, "")]
    [InlineData(400, "\"dv1:abc\"")]
    [InlineData(200, "W/\"dv1:abc\"")]
    [InlineData(404, "\"dv1:abc\"")]
    [InlineData(500, "W/\"x\"")]
    [InlineData(201, null)]
    public void Anything_else_is_not_a_tag_refusal(int status, string? ifMatch)
    {
        // A 400 for an invalid body, with a good strong tag or none, is not a version-tag problem and must not be logged as one.
        Assert.Null(IfMatchClassifier.Refusal(status, ifMatch));
    }

    // ------------------------------------------------------------------------------------------------------ rate limiting and wording

    [Fact]
    public void A_retry_loop_writes_one_line_per_route_per_minute_and_the_next_says_how_many_it_swallowed()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));
        var logs = new CapturingLoggerProvider();
        using ILoggerFactory factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var log = new IfMatchRefusalLog(factory, time);

        for (int i = 0; i < 50; i++)
        {
            log.Refused("PUT", "/api/documents/{id}", 400, IfMatchReason.Weak, weak: true);
            time.Advance(TimeSpan.FromMilliseconds(500));
        }

        // A different route, and a different method on the same route, each get their own line.
        log.Refused("PATCH", "/api/documents/{id}", 400, IfMatchReason.Weak, weak: true);
        log.Refused("POST", "/api/documents/{id}/versions/{versionId}/restore", 428, IfMatchReason.Missing, weak: false);
        Assert.Equal(3, logs.Lines.Count);

        time.Advance(TimeSpan.FromMinutes(1));
        log.Refused("PUT", "/api/documents/{id}", 400, IfMatchReason.Weak, weak: true);

        CapturingLoggerProvider.LogLine[] lines = [.. logs.Lines];
        Assert.Equal(4, lines.Length);
        Assert.All(lines, l => Assert.Equal(LogLevel.Warning, l.Level));
        Assert.DoesNotContain("not logged", lines[0].Message, StringComparison.Ordinal);
        Assert.Contains("49 more like it in the last minute were not logged", lines[3].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wording_names_the_route_status_reason_and_weakness_and_points_at_the_connection_check()
    {
        var logs = new CapturingLoggerProvider();
        using ILoggerFactory factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var log = new IfMatchRefusalLog(factory, TimeProvider.System);

        log.Refused("PUT", "/api/documents/{id}", 400, IfMatchReason.Weak, weak: true);
        log.Refused("PATCH", "/api/documents/{id}", 428, IfMatchReason.Missing, weak: false);
        log.Refused("POST", "/api/documents/{id}/versions/{versionId}/restore", 412, IfMatchReason.Stale, weak: false);

        string[] messages = [.. logs.Lines.Select(l => l.Message)];
        Assert.Contains("PUT /api/documents/{id} answered 400 (weak tag); the If-Match tag was weak (W/).", messages[0], StringComparison.Ordinal);
        Assert.Contains("Admin > Connection check", messages[0], StringComparison.Ordinal);
        Assert.Contains("PATCH /api/documents/{id} answered 428 (missing If-Match); the If-Match tag was absent.", messages[1], StringComparison.Ordinal);
        Assert.Contains("answered 412 (stale version); the If-Match tag was not weak.", messages[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_weakened_tag_the_server_accepts_is_one_information_line_an_hour_per_peer()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));
        var logs = new CapturingLoggerProvider();
        using ILoggerFactory factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var log = new IfMatchRefusalLog(factory, time);

        for (int i = 0; i < 20; i++)
        {
            log.AcceptedWeak("198.51.100.7");
        }

        log.AcceptedWeak("198.51.100.8");
        time.Advance(TimeSpan.FromMinutes(59));
        log.AcceptedWeak("198.51.100.7");
        Assert.Equal(2, logs.Lines.Count);

        time.Advance(TimeSpan.FromMinutes(2));
        log.AcceptedWeak("198.51.100.7");

        CapturingLoggerProvider.LogLine[] lines = [.. logs.Lines];
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.Equal(LogLevel.Information, l.Level));
        Assert.Equal("accepted a weakened version tag from 198.51.100.7; a proxy or CDN is rewriting ETag (logged once an hour per address).", lines[0].Message);
    }

    // ------------------------------------------------------------------------------------------------------ a real server

    [Fact]
    public async Task A_weakened_tag_is_accepted_and_noted_once_an_hour_without_the_tag_and_a_stale_one_is_refused_with_the_weakness_noted()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new FakeIpServerFactory(logs: logs);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        HttpResponseMessage created = await admin.PostAsync("/api/documents", Json(Body("Tagged")));
        JsonNode createdBody = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        string id = createdBody["id"]!.GetValue<string>();
        string tag = created.Headers.ETag!.ToString();
        Assert.StartsWith("\"dv1:", tag, StringComparison.Ordinal);

        async Task<HttpStatusCode> Put(string? ifMatch)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{id}") { Content = Json(Body("Tagged", id)) };
            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            return (await admin.SendAsync(request)).StatusCode;
        }

        // A save that carries a perfectly good tag logs nothing at all.
        Assert.Equal(HttpStatusCode.OK, await Put(tag));
        Assert.DoesNotContain(logs.Lines, l => l.Category == "Draughtsman.Server.VersionTag");
        string current = (await admin.GetAsync($"/api/documents/{id}")).Headers.ETag!.ToString();

        // A gateway's weakened form of the current tag is accepted (the version is the same), and the server says that a proxy is rewriting ETag, once.
        Assert.Equal(HttpStatusCode.OK, await Put("W/" + current));
        string afterWeak = (await admin.GetAsync($"/api/documents/{id}")).Headers.ETag!.ToString();
        Assert.Equal(HttpStatusCode.OK, await Put("W/" + afterWeak));
        CapturingLoggerProvider.LogLine note = Assert.Single(logs.Lines, l => l.Category == "Draughtsman.Server.VersionTag");
        Assert.Equal(LogLevel.Information, note.Level);
        Assert.StartsWith("accepted a weakened version tag from ", note.Message, StringComparison.Ordinal);
        Assert.Contains("a proxy or CDN is rewriting ETag", note.Message, StringComparison.Ordinal);
        // Never the tag's value and never the document.
        Assert.DoesNotContain(id, note.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(current.Trim('"'), note.Message, StringComparison.Ordinal);

        // A weak tag that names a version that has moved on is a 412 like any other: logged as a Warning, with the weakness noted and the tag left out.
        Assert.Equal(HttpStatusCode.PreconditionFailed, await Put("W/" + tag));
        string stale = Assert.Single(WarningsAbout(logs)).Message;
        Assert.Contains("PUT /api/documents/{id} answered 412 (stale version); the If-Match tag was weak (W/).", stale, StringComparison.Ordinal);
        Assert.DoesNotContain(id, stale, StringComparison.Ordinal);
        Assert.DoesNotContain(tag.Trim('"'), stale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_wildcard_and_list_refusals_are_each_reported_and_a_bad_body_is_not()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new FakeIpServerFactory(logs: logs);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        HttpResponseMessage created = await admin.PostAsync("/api/documents", Json(Body("Refusals")));
        string id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        string tag = created.Headers.ETag!.ToString();

        async Task<HttpStatusCode> Send(HttpMethod method, string path, string? ifMatch, string? body = null)
        {
            using var request = new HttpRequestMessage(method, path) { Content = Json(body ?? Body("Refusals", id)) };
            if (ifMatch is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            }

            return (await admin.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.PreconditionRequired, await Send(HttpMethod.Put, $"/api/documents/{id}", null));
        Assert.Equal(HttpStatusCode.BadRequest, await Send(HttpMethod.Patch, $"/api/documents/{id}", "*", "{\"title\":\"x\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, await Send(HttpMethod.Post, $"/api/documents/{id}/versions/v_none/restore", $"{tag}, {tag}", "{}"));

        string[] messages = [.. WarningsAbout(logs).Select(l => l.Message)];
        Assert.Equal(3, messages.Length);
        Assert.Contains(messages, m => m.Contains("PUT /api/documents/{id} answered 428 (missing If-Match)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("PATCH /api/documents/{id} answered 400 (wildcard)", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("answered 400 (list of tags)", StringComparison.Ordinal) && m.Contains("restore", StringComparison.Ordinal));
        Assert.All(messages, m => Assert.DoesNotContain(id, m, StringComparison.Ordinal));

        // A 400 that is only an invalid body, sent with a good strong tag, is not a version-tag problem: nothing more is logged.
        int before = WarningsAbout(logs).Count;
        Assert.Equal(HttpStatusCode.BadRequest, await Send(HttpMethod.Put, $"/api/documents/{id}", tag, "{ not json"));
        Assert.Equal(before, WarningsAbout(logs).Count);
    }

    [Fact]
    public async Task A_stale_save_is_reported_as_a_stale_version_and_the_second_one_within_the_minute_is_not()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new FakeIpServerFactory(logs: logs);
        HttpClient admin = await factory.CreateAuthenticatedClient();
        HttpResponseMessage created = await admin.PostAsync("/api/documents", Json(Body("Stale")));
        string id = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
        string stale = created.Headers.ETag!.ToString();

        async Task<HttpStatusCode> Put(string ifMatch)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{id}") { Content = Json(Body("Stale again", id)) };
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            return (await admin.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await Put(stale));
        Assert.Equal(HttpStatusCode.PreconditionFailed, await Put(stale));
        Assert.Equal(HttpStatusCode.PreconditionFailed, await Put(stale));

        string message = Assert.Single(WarningsAbout(logs)).Message;
        Assert.Contains("PUT /api/documents/{id} answered 412 (stale version); the If-Match tag was not weak.", message, StringComparison.Ordinal);
        Assert.DoesNotContain(id, message, StringComparison.Ordinal);
        Assert.DoesNotContain(stale.Trim('"'), message, StringComparison.Ordinal);
    }

    private static List<CapturingLoggerProvider.LogLine> WarningsAbout(CapturingLoggerProvider logs)
    {
        return [.. logs.Lines.Where(l => l.Category == "Draughtsman.Server.VersionTag" && l.Level == LogLevel.Warning)];
    }

    private static StringContent Json(string json)
    {
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    private static string Body(string title, string? id = null)
    {
        var body = new JsonObject { ["title"] = title, ["type"] = "flowchart", ["nodes"] = new JsonArray(), ["edges"] = new JsonArray() };
        if (id is not null)
        {
            body["id"] = id;
        }

        return body.ToJsonString();
    }
}
