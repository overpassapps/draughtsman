using System.Diagnostics;
using System.Net;
using System.Text;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Server.Tests;

/// <summary>
/// AU-08 through the real endpoint: the audit's "one 1 MB request can pin a thread for minutes" input (a sequence message
/// followed by a long run of spaces) is answered in well under a second, and an ordinary sequence diagram still imports.
/// </summary>
public class ImportComplexityEndpointTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public ImportComplexityEndpointTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("sequenceDiagram\nA->>B{0}x")]
    [InlineData("sequenceDiagram\nA{0}B")]
    [InlineData("sequenceDiagram\nNote over A{0}x")]
    [InlineData("sequenceDiagram\nparticipant A{0}x")]
    public async Task A_megabyte_of_spaces_in_a_sequence_source_is_answered_at_once(string template)
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();
        string source = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, new string(' ', 990_000));

        var timer = Stopwatch.StartNew();
        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid", new StringContent(source, Encoding.UTF8, "text/plain"), CancellationToken.None);
        timer.Stop();

        // Before the fix the first of these took hours; the importer now reads it in linear time, so it answers 200 with
        // whatever it could read (a warning, at worst) and never the 422 a timeout would give.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(timer.Elapsed < LoadScale.Scaled(TimeSpan.FromSeconds(3)), $"took {timer.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task An_ordinary_sequence_diagram_still_imports_with_its_messages()
    {
        HttpClient client = await _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync("/api/import/mermaid",
            new StringContent("sequenceDiagram\nparticipant A as Alice\nA->>B: hello\nNote over A,B: noted\n", Encoding.UTF8, "text/plain"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("hello", body, StringComparison.Ordinal);
        Assert.Contains("noted", body, StringComparison.Ordinal);
    }
}
