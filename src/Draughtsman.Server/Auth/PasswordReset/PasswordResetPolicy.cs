using Draughtsman.Server.Configuration;
using Draughtsman.Server.Email;

namespace Draughtsman.Server.Auth.PasswordReset;

/// <summary>
/// The lifetimes and limits for password-reset links, resolved from <c>auth.passwordReset</c> in draughtsman.yaml; whether email works is read live from <see cref="EmailSettingsService"/>, so saving the email settings turns "Forgot password?" on or off at once. The same
/// idea as <see cref="TokenPolicy"/>: a nonsense configured value is clamped rather than honoured, so a typo can neither make a
/// link that is already dead nor one that lives for years.
/// </summary>
public sealed class PasswordResetPolicy(PasswordResetConfig config, EmailSettingsService email)
{
    public const int MinEmailedMinutes = 5;

    public const int MaxEmailedMinutes = 1440;

    public const int MinAdminHours = 1;

    public const int MaxAdminHours = 168;

    /// <summary>
    /// Emails one address can be sent in <see cref="EmailWindow"/>, however many strangers ask. A request over it sends nothing and
    /// changes nothing; the links already in the inbox keep working, so a flood can annoy a mailbox for a few minutes but cannot
    /// use up, replace or lock anything the real owner holds.
    /// </summary>
    public const int MaxEmailsPerWindow = 3;

    public static readonly TimeSpan EmailWindow = TimeSpan.FromMinutes(15);

    /// <summary>Requests from one address (all accounts) in the throttle window.</summary>
    public const int RequestsPerAddress = 10;

    /// <summary>Requests from one address for one email address in the throttle window.</summary>
    public const int RequestsPerAddressAndEmail = 3;

    public TimeSpan EmailedLifetime { get; } = TimeSpan.FromMinutes(Math.Clamp(config.EmailedLinkMinutes, MinEmailedMinutes, MaxEmailedMinutes));

    public TimeSpan AdminLifetime { get; } = TimeSpan.FromHours(Math.Clamp(config.AdminLinkHours, MinAdminHours, MaxAdminHours));

    /// <summary>"Forgot password?" is offered: self-service is on, mail can be sent, and the instance knows its own address for the link.</summary>
    public bool SelfServiceAvailable => config.SelfService && email.Current.CanEmailLinks;

    /// <summary>The base an administrator's link is built on when the server knows it; otherwise the page builds it from the address in the administrator's own browser.</summary>
    public string? PublicUrl => email.Current.PublicUrl;
}
