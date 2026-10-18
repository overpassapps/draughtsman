using Draughtsman.Core.Auth;
using Draughtsman.Server.Email;
using Draughtsman.Server.Theming;

namespace Draughtsman.Server.Auth.PasswordReset;

/// <summary>
/// Handles one queued "Forgot password?" request, away from the person who made it: finds the active account for the address,
/// makes an emailed link and sends it. For an address nobody has, or an account that is deactivated, it does nothing at all.
/// Whatever it does, it never touches what the account already holds: no earlier link is ended, no session revoked, no password
/// or lock changed. The only thing a stranger's request can add to someone else's account is one more link and one email to
/// their own inbox, and at most <see cref="PasswordResetPolicy.MaxEmailsPerWindow"/> of those per
/// <see cref="PasswordResetPolicy.EmailWindow"/>.
/// </summary>
internal sealed class PasswordResetMailer(
    IUserStore users,
    IPasswordResetStore resets,
    IEmailSender sender,
    EmailSettingsService emailSettings,
    PasswordResetPolicy policy,
    SecurityEventLog events,
    ChromeBrandingService branding,
    TimeProvider clock,
    ILogger<PasswordResetMailer> logger)
{
    public async Task HandleAsync(ResetMailRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // One snapshot for the whole request: a save halfway through must not mix two sets of settings.
        EmailSettings email = emailSettings.Current;
        if (email.PublicUrl is null)
        {
            return;
        }

        User? user = await users.GetUserByEmail(request.Email, cancellationToken);
        if (user is null || !user.Active)
        {
            // Said nowhere a requester can see, and not at all with the address in it.
            logger.LogDebug("A reset was asked for an address with no active account; nothing was sent");
            return;
        }

        if (!EmailAddresses.IsPlainAddress(user.Email))
        {
            // An account whose address is not a plain name@host (an administrator typed something odd) is never mailed, rather than mailed somewhere surprising.
            logger.LogWarning("No reset email was sent to account {UserId}: its address is not a plain email address", user.Id);
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();
        int recent = await resets.CountCreatedSince(user.Id, PasswordResetPurpose.Email, now - PasswordResetPolicy.EmailWindow, cancellationToken);
        if (recent >= PasswordResetPolicy.MaxEmailsPerWindow)
        {
            logger.LogInformation("No reset email was sent to account {UserId}: {Count} were already sent in the last {Minutes} minutes", user.Id, recent, PasswordResetPolicy.EmailWindow.TotalMinutes);
            return;
        }

        string secret = PasswordResetSecrets.NewSecret();
        DateTimeOffset expires = now + policy.EmailedLifetime;
        PasswordResetCreation created = await resets.Create(user.Id, PasswordResetPurpose.Email, PasswordResetSecrets.Hash(secret), now, expires, createdByUserId: null, invalidateOthers: false, cancellationToken);
        EmailMessage message = PasswordResetMessages.Reset(user.Email, branding.Current.ProductName, PasswordResetSecrets.BuildLink(email.PublicUrl, secret), expires, policy.EmailedLifetime);
        try
        {
            await sender.SendAsync(message, cancellationToken);
        }
        catch (EmailSendException error)
        {
            // A link that never left the server reaches nobody and is ended, rather than left live for an hour.
            await resets.Invalidate(created.Token.Id, now, cancellationToken);
            await events.Record(new SecurityEvent(SecurityEventTypes.PasswordResetEmailFailed, now, TargetUserId: user.Id, TargetLabel: user.Email, SourceIp: request.SourceIp, Method: SecurityEventMethods.Anonymous, Detail: "the reset email was not sent: " + error.Message));
            return;
        }

        await events.Record(new SecurityEvent(SecurityEventTypes.PasswordResetEmailSent, now, TargetUserId: user.Id, TargetLabel: user.Email, SourceIp: request.SourceIp, Method: SecurityEventMethods.Anonymous, Detail: $"reset link emailed ({email.Provider}); expires {expires:u}"));
    }
}
