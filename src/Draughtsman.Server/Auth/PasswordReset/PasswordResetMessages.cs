using System.Globalization;
using Draughtsman.Server.Email;

namespace Draughtsman.Server.Auth.PasswordReset;

/// <summary>
/// The words of the emails this feature sends: plain text, British English, short. The reset email carries the link (the one
/// credential it exists to deliver), when it stops working, and what to do if the reader did not ask. It never carries a
/// password or any other secret.
/// </summary>
public static class PasswordResetMessages
{
    public static EmailMessage Reset(string to, string productName, string link, DateTimeOffset expiresAt, TimeSpan lifetime)
    {
        string body =
            $"Someone asked to reset the password for your {productName} account.\n\n" +
            "To choose a new password, open this link:\n\n" +
            $"{link}\n\n" +
            $"The link works once and expires in {DescribeLifetime(lifetime)}, at {FormatMoment(expiresAt)}. " +
            "Choosing a new password signs you out everywhere else.\n\n" +
            "If you did not ask for this, ignore this email: nothing changes unless the link is used. " +
            "If you think someone else is trying to get into your account, tell your administrator.\n";
        return new EmailMessage(to, $"Reset your {productName} password", body);
    }

    public static EmailMessage Test(string to, string productName, DateTimeOffset sentAt)
    {
        string body =
            $"This is a test message from {productName}, sent at {FormatMoment(sentAt)} by an administrator.\n\n" +
            "If you can read it, outgoing email works and password-reset emails can reach people. Nothing needs doing.\n";
        return new EmailMessage(to, $"{productName}: test email", body);
    }

    /// <summary>"60 minutes", "1 hour", "24 hours": the way a person says it.</summary>
    public static string DescribeLifetime(TimeSpan lifetime)
    {
        if (lifetime.TotalMinutes < 120)
        {
            int minutes = (int)Math.Round(lifetime.TotalMinutes);
            return minutes == 60 ? "1 hour" : string.Create(CultureInfo.InvariantCulture, $"{minutes} minutes");
        }

        int hours = (int)Math.Round(lifetime.TotalHours);
        return string.Create(CultureInfo.InvariantCulture, $"{hours} hours");
    }

    private static string FormatMoment(DateTimeOffset moment)
    {
        return moment.UtcDateTime.ToString("HH:mm 'UTC on' d MMMM yyyy", CultureInfo.InvariantCulture);
    }
}
