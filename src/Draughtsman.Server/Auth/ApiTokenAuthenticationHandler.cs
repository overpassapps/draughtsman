using System.Text.Encodings.Web;
using Draughtsman.Core.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Draughtsman.Server.Auth;

/// <summary>
/// Authenticates <c>Authorization: Bearer &lt;token&gt;</c> (build-spec-v0.1.md section 7). Used by MCP over HTTP and
/// by scripting; <c>draughtsman mcp --stdio</c> never goes through HTTP at all, so it is unaffected (README's Sign-in
/// section). A revoked or expired token or a deactivated owner is refused on the very next request, same as a session.
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IUserStore userStore)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail("The Authorization header must be a Bearer token.");
        }

        string secret = header[BearerPrefix.Length..].Trim();
        if (secret.Length == 0)
        {
            return AuthenticateResult.Fail("The bearer token is empty.");
        }

        string hash = TokenGenerator.Hash(secret);
        ApiToken? token = await userStore.GetApiTokenByHash(hash, Context.RequestAborted);
        // One message for unknown, revoked and expired alike: the caller learns nothing about which it was. Expiry is
        // checked here, on every request, against the stored row (a null ExpiresAt is a grandfathered, pre-expiry token).
        if (token is null || token.Revoked || (token.ExpiresAt is DateTimeOffset expiresAt && expiresAt <= DateTimeOffset.UtcNow))
        {
            return AuthenticateResult.Fail("The token is invalid, revoked or expired.");
        }

        User? user = await userStore.GetUserById(token.UserId, Context.RequestAborted);
        if (user is null || !user.Active)
        {
            return AuthenticateResult.Fail("The token's user no longer has access.");
        }

        await userStore.RecordApiTokenUse(token.Id, DateTimeOffset.UtcNow, Context.RequestAborted);

        var ticket = new AuthenticationTicket(PrincipalFactory.ForUser(user, AuthSchemes.Token, tokenId: token.Id, tokenScope: token.Scope), AuthSchemes.Token);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => BearerChallenge.WriteAsync(Response);
}
