namespace Draughtsman.Server.Auth.PasswordReset;

/// <summary>
/// The secret behind a reset link: 32 bytes from the system's CSPRNG (<see cref="TokenGenerator.NewSecret"/>) under a recognisable
/// prefix, so a copy that leaks into a ticket is greppable. Only <see cref="Hash"/> of it is ever stored. The link carries it in
/// the URL <em>fragment</em> (<c>/reset-password#token=…</c>), which a browser never sends to the server, so it is in no access
/// log, proxy log or Referer header, and a mail scanner that fetches the link cannot spend it: the page reads it and POSTs it in
/// a body.
/// </summary>
public static class PasswordResetSecrets
{
    public const string Prefix = "dpr_";

    /// <summary>The longest secret anything here will look at; the real one is 47 characters.</summary>
    public const int MaxLength = 128;

    public const string LinkPath = "/reset-password";

    public const string FragmentKey = "token";

    public static string NewSecret()
    {
        return Prefix + TokenGenerator.NewSecret();
    }

    public static string Hash(string secret)
    {
        return TokenGenerator.Hash(secret);
    }

    /// <summary>The characters a real secret can hold, within the length cap. Anything else is a token nobody here issued.</summary>
    public static bool IsWellFormed(string? secret)
    {
        return !string.IsNullOrEmpty(secret)
            && secret.Length <= MaxLength
            && secret.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');
    }

    public static string BuildLink(string publicUrl, string secret)
    {
        return $"{publicUrl.TrimEnd('/')}{LinkPath}#{FragmentKey}={secret}";
    }
}
