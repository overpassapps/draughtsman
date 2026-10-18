using System.Security.Cryptography;
using Draughtsman.Core.Auth;

namespace Draughtsman.Server.Auth.Recovery;

public enum AdminRecoveryOutcome
{
    /// <summary>The password was set and every session and token of the account revoked.</summary>
    Reset,

    /// <summary>The instance has no users at all: nothing to recover, first-run is open instead.</summary>
    NoUsers,

    NoSuchAccount,

    /// <summary>The account exists but is not an Admin and the caller did not ask to promote it.</summary>
    NotAnAdmin,

    PasswordTooShort,
}

public sealed record AdminRecoveryResult(AdminRecoveryOutcome Outcome, User? User = null, bool Promoted = false, bool Reactivated = false);

/// <summary>
/// The lost-admin recovery (AU-03): sets a new password for an administrator account, bringing it back if it was
/// deactivated, and revokes every session and API token it had. It has no HTTP surface, by design.
/// <para>
/// <b>Trust model.</b> The only caller is the host-shell <c>reset-admin</c> verb, so the credential is being able to
/// run a process on the host as a user that can read and write the data folder (and, for Postgres, the connection
/// string in <c>draughtsman.yaml</c>). Anyone who can do that can already read every password hash, session and
/// document and could edit the database directly, so this adds no capability a host-shell user did not have; it makes
/// the recovery supported, safe (Argon2id, revocation) and recorded (an <see cref="SecurityEventTypes.AdminRecovered"/>
/// event naming the host user) instead of a hand-edited <c>users</c> table. It is the same boundary as
/// <c>draughtsman mcp --stdio</c>. It takes effect on the running server immediately, since sessions and tokens are
/// checked against the database on every request.
/// </para>
/// </summary>
public sealed class AdminRecoveryService(IUserStore users, IPasswordResetStore resets, SecurityEventLog events, TimeProvider clock)
{
    private const string PasswordAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789"; // no look-alikes
    private const int GeneratedPasswordLength = 24;

    /// <param name="email">Case-insensitive, as at sign-in.</param>
    /// <param name="promote">Also make the account an Admin if it is an Editor; without it an Editor is refused, so a mistyped address cannot hand out admin rights.</param>
    /// <param name="hostUser">The operating-system user running the verb, recorded in the event.</param>
    public async Task<AdminRecoveryResult> Reset(string email, string newPassword, bool promote, string? hostUser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(newPassword);

        if (newPassword.Length < PasswordHasher.MinimumLength)
        {
            return new AdminRecoveryResult(AdminRecoveryOutcome.PasswordTooShort);
        }

        if (!await users.HasAnyUsers(cancellationToken))
        {
            return new AdminRecoveryResult(AdminRecoveryOutcome.NoUsers);
        }

        User? user = string.IsNullOrWhiteSpace(email) ? null : await users.GetUserByEmail(email, cancellationToken);
        if (user is null)
        {
            return new AdminRecoveryResult(AdminRecoveryOutcome.NoSuchAccount);
        }

        if (user.Role != UserRole.Admin && !promote)
        {
            return new AdminRecoveryResult(AdminRecoveryOutcome.NotAnAdmin, user);
        }

        bool promoted = user.Role != UserRole.Admin;
        bool reactivated = !user.Active;
        await users.UpdateUserPassword(user.Id, PasswordHasher.Hash(newPassword), cancellationToken);
        if (promoted)
        {
            await users.UpdateUserRole(user.Id, UserRole.Admin, cancellationToken);
        }

        if (reactivated)
        {
            await users.SetUserActive(user.Id, true, cancellationToken);
        }

        int sessions = await users.RevokeSessionsForUser(user.Id, cancellationToken);
        int tokens = await users.RevokeApiTokensForUser(user.Id, cancellationToken);
        // A reset link made before the operator chose this password must not be able to replace it.
        await resets.InvalidateLive(user.Id, clock.GetUtcNow(), cancellationToken);

        string detail = $"password reset from the host shell; {sessions} session(s) and {tokens} token(s) revoked" + (promoted ? "; promoted to Admin" : string.Empty) + (reactivated ? "; reactivated" : string.Empty);
        await events.Record(new SecurityEvent(
            SecurityEventTypes.AdminRecovered,
            clock.GetUtcNow(),
            ActorLabel: string.IsNullOrWhiteSpace(hostUser) ? "host shell" : "host user " + hostUser,
            TargetUserId: user.Id,
            TargetLabel: user.Email,
            Method: SecurityEventMethods.Host,
            Detail: detail));
        return new AdminRecoveryResult(AdminRecoveryOutcome.Reset, user, promoted, reactivated);
    }

    /// <summary>A random password for the operator to be handed once (<c>--generate</c>): 24 characters from an alphabet with no look-alikes, drawn without modulo bias.</summary>
    public static string GeneratePassword()
    {
        return string.Create(GeneratedPasswordLength, 0, static (span, _) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = PasswordAlphabet[RandomNumberGenerator.GetInt32(PasswordAlphabet.Length)];
            }
        });
    }
}
