using System.Net;

namespace Draughtsman.Server.Tests;

public class ResponseCompressionTests : IClassFixture<DraughtsmanServerFactory>
{
    private readonly DraughtsmanServerFactory _factory;

    public ResponseCompressionTests(DraughtsmanServerFactory factory)
    {
        _factory = factory;
    }

    private async Task<HttpResponseMessage> Get(string path, string? acceptEncoding)
    {
        using HttpClient client = _factory.CreateDefaultClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptEncoding is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        }

        return await client.SendAsync(request);
    }

    [Theory]
    [InlineData("gzip", "gzip")]
    [InlineData("br", "br")]
    [InlineData("br, gzip", "br")]
    public async Task A_json_response_is_compressed_for_a_client_that_accepts_it(string accept, string expected)
    {
        HttpResponseMessage response = await Get("/api/health", accept);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, Assert.Single(response.Content.Headers.ContentEncoding));
    }

    [Fact]
    public async Task A_response_is_not_compressed_for_a_client_that_did_not_ask()
    {
        HttpResponseMessage response = await Get("/api/health", acceptEncoding: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
    }
}
