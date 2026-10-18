using System.Text;

namespace Draughtsman.Server.Auth;

/// <summary>
/// The 401 every protected route answers with. Without a <c>WWW-Authenticate</c> header an MCP client such as Claude
/// Code assumes the server speaks OAuth and fails in dynamic client registration with a message that never mentions a
/// token; <c>Bearer</c> tells it (and a person reading the response) that an API token is what is missing. OAuth itself
/// is not implemented.
/// </summary>
internal static class BearerChallenge
{
    public const string Scheme = "Bearer";

    public const string Hint =
        "Authentication required. Send an API token as 'Authorization: Bearer <token>' (create one in the web app under API tokens, or POST /api/tokens). This server does not support OAuth.";

    public static async Task WriteAsync(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.WWWAuthenticate = Scheme;
        response.ContentType = "text/plain; charset=utf-8";
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(Hint));
    }
}
