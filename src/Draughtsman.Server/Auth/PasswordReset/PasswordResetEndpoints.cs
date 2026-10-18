using System.Security.Cryptography;
using System.Text;
using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth.Endpoints;

namespace Draughtsman.Server.Auth.PasswordReset;

/// <summary>
/// Forgotten-password recovery (docs/api.md "Password reset"; design/atlas/password-recovery.md). Two ways in, one mechanism:
/// an administrator creates a single-use link for a person and passes it on (<see cref="CreateAdminLink"/>, cookie-admin
/// only, no email needed), or the person asks on the sign-in page and the link is emailed (<c>/request</c>, only when email is
/// configured). Either link is spent on the same page through <c>/complete</c>, where the person chooses the password.
/// <para>
/// The sharp edges, all pinned by tests in both directions: the secret is 32 CSPRNG bytes and only its SHA-256 is stored; a link
/// is single use and dies on expiry, on a newer administrator link, on a completed reset, on a password change and on
/// deactivation; every refusal is one identical answer; <c>/request</c> answers every address identically (in content and in time) and never
/// reveals, changes or locks anything of the account it names; a completed reset signs the person out everywhere (their API
/// tokens are left alone, as for a self-service password change - a forgotten password says nothing about a leaked token, and
/// silently killing every agent configuration would teach people to avoid recovering); an API token can neither ask for nor
/// spend a link; the actor header decides nothing, because nothing here reads it.
/// </para>
/// </summary>
public static class PasswordResetEndpoints
{
    private const int MaxEmailLength = 254;

    /// <summary>The same words for every address and every outcome, so the response cannot be used to find out who has an account.</summary>
    public const string RequestedMessage = "If an account with that address can be reset, we have emailed it a link. The email can take a few minutes to arrive.";

    public const string RefusedDetail = "This reset link is not valid. It may have expired or already been used. Ask for a new one.";

    public static IEndpointRouteBuilder MapPasswordResetEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/auth/password-reset").AllowAnonymous().AddEndpointFilter(RefuseApiTokens);
        group.MapPost("/request", RequestReset).WithName("PasswordResetRequest");
        group.MapPost("/complete", CompleteReset).WithName("PasswordResetComplete");
        return app;
    }

    /// <summary>A reset link is for a person at a browser. A token, whatever its owner's role, can neither ask for one nor spend one.</summary>
    private static async ValueTask<object?> RefuseApiTokens(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (string.Equals(context.HttpContext.User.Identity?.AuthenticationType, AuthSchemes.Token, StringComparison.Ordinal))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not available to API tokens", detail: "Password reset links are used from a browser. An API token can neither request one nor use one.");
        }

        return await next(context);
    }

    /// <summary>
    /// "Forgot password?". Throttled per address (all accounts) and per address-and-email pair, charged on every request since a
    /// request that "worked" cannot be told from one that did not. It records one security event, enqueues, and answers: nothing
    /// that depends on whether an account exists happens before the answer, so the answer and its timing are the same either way.
    /// </summary>
    private static async Task<IResult> RequestReset(PasswordResetRequestBody? body, HttpContext context, PasswordResetPolicy policy, FailedSignInThrottle throttle, PasswordResetMailQueue queue, SecurityEventLog events)
    {
        if (!policy.SelfServiceAvailable)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not available", detail: "Emailed reset links are not set up on this instance. Ask your administrator for a reset link.");
        }

        if (body is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The body must be a JSON object.");
        }

        string email = (body.Email ?? string.Empty).Trim();
        if (email.Length == 0 || email.Length > MaxEmailLength || !email.Contains('@', StringComparison.Ordinal))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: "Enter the email address you sign in with.");
        }

        string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string ipKey = "forgot:ip:" + ip;
        string pairKey = "forgot:pair:" + email.ToLowerInvariant() + "|" + ip;
        bool pairBlocked = throttle.IsBlocked(pairKey, PasswordResetPolicy.RequestsPerAddressAndEmail);
        if (pairBlocked || throttle.IsBlocked(ipKey, PasswordResetPolicy.RequestsPerAddress))
        {
            if (throttle.TryReportTrip(pairBlocked ? pairKey : ipKey))
            {
                await events.Record(context, SecurityEventTypes.SignInThrottled, detail: pairBlocked ? "too many password reset requests for one address from this address" : "too many password reset requests from this address", targetLabel: AttemptedEmail(email));
            }

            return TooManyAttempts();
        }

        throttle.RecordFailure(ipKey);
        throttle.RecordFailure(pairKey);
        await events.Record(context, SecurityEventTypes.PasswordResetRequested, targetLabel: AttemptedEmail(email));
        queue.TryEnqueue(new ResetMailRequest(email, ip));
        return Results.Json(new PasswordResetRequestedResponse(RequestedMessage), statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Spends a link and sets the password. The password is checked first, before the link is looked at, so a short one cannot be
    /// used to tell a live link from a dead one (and a typo does not cost the person their link). Every refusal after that is the same
    /// answer; the security log gets the reason class. Per-address and per-(account, address) throttles charge failures only.
    /// </summary>
    private static async Task<IResult> CompleteReset(
        PasswordResetCompleteBody? body,
        HttpContext context,
        IUserStore users,
        IPasswordResetStore resets,
        FailedSignInThrottle throttle,
        SecurityEventLog events,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The body must be a JSON object.");
        }

        if (string.IsNullOrEmpty(body.NewPassword) || body.NewPassword.Length < PasswordHasher.MinimumLength)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid request", detail: $"Password must be at least {PasswordHasher.MinimumLength} characters.");
        }

        string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        string ipKey = "resetuse:ip:" + ip;
        if (throttle.IsBlocked(ipKey, FailedSignInThrottle.IpMaxAttempts))
        {
            if (throttle.TryReportTrip(ipKey))
            {
                await events.Record(context, SecurityEventTypes.SignInThrottled, detail: "too many failed password reset links from this address");
            }

            return TooManyAttempts();
        }

        // Charged before anything is looked up and handed back on success, the same shape as Login: only failures stay counted.
        long ticket = throttle.RecordFailure(ipKey);

        string? secret = body.Token?.Trim();
        PasswordResetToken? token = null;
        string hash = string.Empty;
        if (PasswordResetSecrets.IsWellFormed(secret))
        {
            hash = PasswordResetSecrets.Hash(secret!);
            token = await resets.GetByHash(hash, cancellationToken);
            if (token is not null && !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token.TokenHash), Encoding.ASCII.GetBytes(hash)))
            {
                token = null;
            }
        }

        if (token is null)
        {
            await events.Record(context, SecurityEventTypes.PasswordResetLinkRefused, detail: "link not recognised");
            return Refused();
        }

        User? user = await users.GetUserById(token.UserId, cancellationToken);
        string pairKey = "resetuse:pair:" + token.UserId + "|" + ip;
        DateTimeOffset now = clock.GetUtcNow();
        string? reason = RefusalReason(token, user, now);
        if (reason is not null)
        {
            // Someone holding a dead link of this account and trying it again: charged to the (account, address) pair, and past the
            // budget refused quietly. The budget never applies to a live link, so repeating an old one can never keep the owner out.
            bool pairBlocked = throttle.IsBlocked(pairKey);
            throttle.RecordFailure(pairKey);
            if (!pairBlocked)
            {
                await events.Record(context, SecurityEventTypes.PasswordResetLinkRefused, target: user, detail: reason);
            }
            else if (throttle.TryReportTrip(pairKey))
            {
                await events.Record(context, SecurityEventTypes.SignInThrottled, detail: "too many failed uses of reset links for one account from this address", target: user);
            }

            return Refused();
        }

        // Hashed only now that the link is live: a garbage or dead link costs one lookup, not an Argon2 derivation.
        PasswordResetRedemption redemption = await resets.Redeem(hash, PasswordHasher.Hash(body.NewPassword), now, cancellationToken);
        if (redemption.Status != PasswordResetRedeemStatus.Redeemed)
        {
            throttle.RecordFailure(pairKey);
            await events.Record(context, SecurityEventTypes.PasswordResetLinkRefused, target: redemption.User ?? user, detail: ReasonOf(redemption.Status));
            return Refused();
        }

        throttle.Refund(ipKey, ticket);
        User owner = redemption.User!;
        string kind = redemption.Token!.Purpose == PasswordResetPurpose.Admin ? "administrator" : "emailed";
        await events.Record(context, SecurityEventTypes.PasswordResetLinkUsed, target: owner, actor: owner, method: SecurityEventMethods.ResetLink, detail: $"{kind} link used");
        await events.Record(context, SecurityEventTypes.PasswordResetCompleted, target: owner, actor: owner, method: SecurityEventMethods.ResetLink, detail: $"password changed; {redemption.SessionsRevoked} session(s) signed out; {redemption.LinksInvalidated} other link(s) ended; API tokens left as they were");
        return Results.NoContent();
    }

    /// <summary>
    /// An administrator creates a link for another person (cookie-admin only: an API token is refused by the group's policy). Ends
    /// every earlier unused link of that person in the same transaction, so there is only ever one administrator link live. The
    /// administrator never sees or chooses the person's new password. The event names who, for whom and when it expires - never the link.
    /// </summary>
    internal static async Task<IResult> CreateAdminLink(string id, HttpContext context, IUserStore users, IPasswordResetStore resets, PasswordResetPolicy policy, SecurityEventLog events, TimeProvider clock, CancellationToken cancellationToken)
    {
        User? target = await users.GetUserById(id, cancellationToken);
        if (target is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "User not found", detail: $"No user with id '{id}'.");
        }

        if (!target.Active)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Account deactivated", detail: $"{target.Email} is deactivated. Reactivate the account first: a deactivated account cannot sign in or reset its password.");
        }

        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset expires = now + policy.AdminLifetime;
        string secret = PasswordResetSecrets.NewSecret();
        PasswordResetCreation created = await resets.Create(target.Id, PasswordResetPurpose.Admin, PasswordResetSecrets.Hash(secret), now, expires, context.User.UserId(), invalidateOthers: true, cancellationToken);
        await events.Record(context, SecurityEventTypes.PasswordResetLinkIssued, target: target, detail: $"administrator link, expires {expires:u}; {created.LinksInvalidated} earlier link(s) ended");
        string? link = policy.PublicUrl is null ? null : PasswordResetSecrets.BuildLink(policy.PublicUrl, secret);
        return Results.Created((string?)null, new PasswordResetLinkResponse(secret, link, expires, (int)policy.AdminLifetime.TotalHours, target.Email));
    }

    private static string? RefusalReason(PasswordResetToken token, User? user, DateTimeOffset now)
    {
        if (token.UsedAt is not null)
        {
            return ReasonOf(PasswordResetRedeemStatus.Used);
        }

        if (token.InvalidatedAt is not null)
        {
            return ReasonOf(PasswordResetRedeemStatus.Invalidated);
        }

        if (token.ExpiresAt <= now)
        {
            return ReasonOf(PasswordResetRedeemStatus.Expired);
        }

        return user is null || !user.Active ? ReasonOf(PasswordResetRedeemStatus.AccountInactive) : null;
    }

    private static string ReasonOf(PasswordResetRedeemStatus status)
    {
        return status switch
        {
            PasswordResetRedeemStatus.NotFound => "link not recognised",
            PasswordResetRedeemStatus.Used => "link already used",
            PasswordResetRedeemStatus.Invalidated => "link ended by a newer link, a password change or a deactivation",
            PasswordResetRedeemStatus.Expired => "link expired",
            PasswordResetRedeemStatus.AccountInactive => "account deactivated",
            _ => "link refused",
        };
    }

    private static string? AttemptedEmail(string email)
    {
        return email.Contains('@', StringComparison.Ordinal) && !email.Any(char.IsWhiteSpace) ? email : null;
    }

    private static IResult Refused()
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Link not valid", detail: RefusedDetail);
    }

    private static IResult TooManyAttempts()
    {
        return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many attempts", detail: $"Too many attempts from this network address; try again in {FailedSignInThrottle.Window.TotalMinutes:0} minutes.");
    }
}
