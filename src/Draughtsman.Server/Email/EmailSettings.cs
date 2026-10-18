using System.Net;
using System.Net.Mail;
using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Email;

/// <summary>
/// What outgoing email adds up to right now: draughtsman.yaml's <c>email:</c> and <c>server.publicUrl</c> layered with what an
/// administrator saved at Admin, Email. An immutable snapshot (<see cref="EmailSettingsService"/> swaps in a new one on every save, so a
/// change needs no restart): whether a message can be sent at all, whether a link in a message would work, which fields are locked and why,
/// and in words what is wrong when it cannot.
/// <para>
/// The precedence rule, in one place: a value <b>draughtsman.yaml</b> sets wins and locks that field on the page; a key left at its default
/// (empty text, port 587, <c>starttls</c>) counts as not set, and the administrator's saved value (else the default) applies. The SMTP
/// password follows the same rule: the environment variable or password file named in the yaml wins when it holds a value, else the
/// saved, encrypted one. Nothing here is a secret except <see cref="ReadPassword"/>, which is read at the moment of sending and held nowhere.
/// </para>
/// </summary>
public sealed class EmailSettings
{
    public const string ReasonInFile = "Set in draughtsman.yaml. Edit that file to change it.";

    public const string FieldProvider = "provider";
    public const string FieldFrom = "from";
    public const string FieldReplyTo = "replyTo";
    public const string FieldHost = "host";
    public const string FieldPort = "port";
    public const string FieldTls = "tls";
    public const string FieldUser = "user";
    public const string FieldPublicUrl = "publicUrl";
    public const string FieldPassword = "password";

    private readonly Func<string, string?> _environment;
    private readonly string _dataFolder;

    public EmailSettings(EmailConfig email, ServerConfig server, string dataFolder, Func<string, string?>? environment = null, StoredEmailSettings? stored = null)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(server);
        _dataFolder = dataFolder;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        Config = email;
        Stored = stored ?? StoredEmailSettings.Empty;
        EmailOverrides saved = Stored.Overrides;

        var locks = new Dictionary<string, string>(StringComparer.Ordinal);
        string Pick(string field, string fromFile, string fallback, string? savedValue, bool differs)
        {
            if (differs)
            {
                locks[field] = ReasonInFile;
                return fromFile;
            }

            return savedValue ?? fallback;
        }

        SmtpConfig smtp = email.Smtp;
        string rawProvider = Pick(FieldProvider, email.Provider.Trim(), string.Empty, saved.Provider, email.Provider.Trim().Length > 0);
        Provider = rawProvider.Trim().ToLowerInvariant();
        string from = Pick(FieldFrom, email.From.Trim(), string.Empty, saved.From, email.From.Trim().Length > 0);
        string replyTo = Pick(FieldReplyTo, email.ReplyTo.Trim(), string.Empty, saved.ReplyTo, email.ReplyTo.Trim().Length > 0);
        string host = Pick(FieldHost, smtp.Host.Trim(), string.Empty, saved.Host, smtp.Host.Trim().Length > 0);
        int port = smtp.Port != SmtpConfig.DefaultPort ? smtp.Port : saved.Port ?? SmtpConfig.DefaultPort;
        if (smtp.Port != SmtpConfig.DefaultPort)
        {
            locks[FieldPort] = ReasonInFile;
        }

        string tls = Pick(FieldTls, smtp.Tls.Trim(), SmtpConfig.TlsStartTls, saved.Tls, !string.Equals(smtp.Tls.Trim(), SmtpConfig.TlsStartTls, StringComparison.OrdinalIgnoreCase));
        string user = Pick(FieldUser, smtp.User.Trim(), string.Empty, saved.User, smtp.User.Trim().Length > 0);
        string publicUrl = Pick(FieldPublicUrl, server.PublicUrl.Trim(), string.Empty, saved.PublicUrl, server.PublicUrl.Trim().Length > 0);

        FilePassword = ReadFilePassword();
        if (FilePassword is not null)
        {
            locks[FieldPassword] = !string.IsNullOrWhiteSpace(smtp.PasswordFile)
                ? "Read from the password file named in draughtsman.yaml. Change that file to change it."
                : $"Read from the environment variable {smtp.PasswordEnvironmentVariable}. Change it there.";
        }

        if (Provider == EmailConfig.ProviderFile)
        {
            // A mail server is not used at all, so its fields would be dead controls: say why, once, rather than let them be saved.
            const string NoServer = "Email is set to write files in draughtsman.yaml (email.provider: file), so no mail server is used.";
            foreach (string field in new[] { FieldHost, FieldPort, FieldTls, FieldUser, FieldPassword })
            {
                locks.TryAdd(field, NoServer);
            }
        }

        Form = new EmailFormValues(Provider, from, replyTo, host, port, tls, user, publicUrl);
        Locks = locks;

        var problems = new List<string>();
        var linkProblems = new List<string>();
        if (Provider.Length == 0)
        {
            Configured = false;
        }
        else if (Provider is not (EmailConfig.ProviderSmtp or EmailConfig.ProviderFile))
        {
            problems.Add($"email.provider must be \"{EmailConfig.ProviderSmtp}\" or \"{EmailConfig.ProviderFile}\" (it is \"{email.Provider}\").");
        }
        else
        {
            ResolveSender(from, "email.from", problems, out MailAddress? fromAddress, required: true);
            From = fromAddress;
            ResolveSender(replyTo, "email.replyTo", problems, out MailAddress? replyToAddress, required: false);
            ReplyTo = replyToAddress;
            if (Provider == EmailConfig.ProviderSmtp)
            {
                ResolveSmtp(host, port, tls, user, smtp, problems);
            }
            else
            {
                OutboxFolder = Path.GetFullPath(Path.Combine(dataFolder, string.IsNullOrWhiteSpace(email.File.Folder) ? "outbox" : email.File.Folder));
            }

            Configured = problems.Count == 0;
        }

        PublicUrl = ResolvePublicUrl(publicUrl, linkProblems);
        Problems = [.. problems];
        LinkProblems = [.. linkProblems];
    }

    /// <summary>What draughtsman.yaml says (the password sources live here).</summary>
    public EmailConfig Config { get; }

    /// <summary>What the administrator saved, with the password decrypted. Never sent anywhere: the page is told only whether a password is set.</summary>
    internal StoredEmailSettings Stored { get; }

    /// <summary>The fields as the form shows them: the yaml value where the file sets one, else the saved value, else the default. The raw text, not the parsed address.</summary>
    public EmailFormValues Form { get; }

    /// <summary>Field name to the reason it cannot be edited at Admin, Email. Absent means editable.</summary>
    public IReadOnlyDictionary<string, string> Locks { get; }

    /// <summary>smtp | file | empty (not configured, or unrecognised: see <see cref="Problems"/>).</summary>
    public string Provider { get; }

    /// <summary>A message can be sent: the provider is known and everything it needs is set.</summary>
    public bool Configured { get; }

    /// <summary>What is wrong with the email settings, in words an administrator can act on. Empty when it is fine or simply not set up.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>What is wrong with the public address (empty when it is fine or unset).</summary>
    public IReadOnlyList<string> LinkProblems { get; }

    public MailAddress? From { get; private set; }

    public MailAddress? ReplyTo { get; private set; }

    public string? Host { get; private set; }

    public int Port { get; private set; }

    public bool StartTls { get; private set; }

    public string? User { get; private set; }

    public string? OutboxFolder { get; private set; }

    public TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(SmtpConfigDefaults.TimeoutSeconds);

    /// <summary>The public address without a trailing slash, or null when unset or unusable. The only base a link in an email is built on.</summary>
    public string? PublicUrl { get; }

    /// <summary>An email can carry a working link: it can be sent and the instance's address is known.</summary>
    public bool CanEmailLinks => Configured && PublicUrl is not null;

    /// <summary>The password the environment variable or file named in draughtsman.yaml holds, read when the settings were resolved; null when neither holds one. Locks the password field.</summary>
    private string? FilePassword { get; }

    public bool PasswordLocked => FilePassword is not null;

    /// <summary>A password is available from any source.</summary>
    public bool PasswordSet => FilePassword is not null || Stored.Password is not null;

    /// <summary>environment | file | saved | none: which source is in effect. Names it, never the value.</summary>
    public string PasswordKind
    {
        get
        {
            if (FilePassword is not null)
            {
                return string.IsNullOrWhiteSpace(Config.Smtp.PasswordFile) ? "environment" : "file";
            }

            return Stored.Password is not null ? "saved" : "none";
        }
    }

    /// <summary>
    /// The SMTP password, read now: draughtsman.yaml's file if one is named, else its environment variable, else the one saved here.
    /// Null when none holds one. The file and the variable are read again on every call, so rotating either takes effect at once.
    /// Never cached by the caller, never logged.
    /// </summary>
    public string? ReadPassword()
    {
        return ReadFilePassword() ?? Stored.Password;
    }

    /// <summary>Where the SMTP password is read from, for the admin page: names the source, never the value.</summary>
    public string PasswordSource()
    {
        SmtpConfig smtp = Config.Smtp;
        string? file = ReadFilePassword();
        if (!string.IsNullOrWhiteSpace(smtp.PasswordFile))
        {
            if (file is not null)
            {
                return $"file {smtp.PasswordFile}";
            }

            return Stored.Password is not null ? "saved on this server (encrypted)" : $"file {smtp.PasswordFile} (not readable or empty)";
        }

        if (file is not null)
        {
            return $"environment variable {smtp.PasswordEnvironmentVariable}";
        }

        if (Stored.Password is not null)
        {
            return "saved on this server (encrypted)";
        }

        return string.IsNullOrWhiteSpace(smtp.PasswordEnvironmentVariable) ? "none" : $"environment variable {smtp.PasswordEnvironmentVariable} (not set)";
    }

    private string? ReadFilePassword()
    {
        SmtpConfig smtp = Config.Smtp;
        if (!string.IsNullOrWhiteSpace(smtp.PasswordFile))
        {
            try
            {
                string text = File.ReadAllText(ResolvePath(smtp.PasswordFile)).TrimEnd('\r', '\n');
                return text.Length == 0 ? null : text;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        string? fromEnvironment = string.IsNullOrWhiteSpace(smtp.PasswordEnvironmentVariable) ? null : _environment(smtp.PasswordEnvironmentVariable);
        return string.IsNullOrEmpty(fromEnvironment) ? null : fromEnvironment;
    }

    private string ResolvePath(string path)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_dataFolder, path));
    }

    private static void ResolveSender(string value, string name, List<string> problems, out MailAddress? address, bool required)
    {
        address = null;
        if (value.Length == 0)
        {
            if (required)
            {
                problems.Add($"{name} is not set: the address messages come from.");
            }

            return;
        }

        if (EmailFieldRules.SenderProblem(value, name, out address) is not null)
        {
            address = null;
            problems.Add($"{name} \"{value}\" is not an email address (use name@example.com or Name <name@example.com>), or has a line break in it.");
        }
    }

    private void ResolveSmtp(string host, int port, string tls, string user, SmtpConfig smtp, List<string> problems)
    {
        Host = host;
        Port = port;
        User = user.Length == 0 ? null : user;
        Timeout = TimeSpan.FromSeconds(smtp.TimeoutSeconds is > 0 and <= 300 ? smtp.TimeoutSeconds : SmtpConfigDefaults.TimeoutSeconds);

        if (host.Length == 0)
        {
            problems.Add("email.smtp.host is not set: the mail server to send through.");
        }
        else if (EmailFieldRules.HostProblem(host) is not null)
        {
            problems.Add($"email.smtp.host \"{(host.Any(char.IsControl) ? "…" : host)}\" is not a host name or address.");
        }

        if (port is < 1 or > 65535)
        {
            problems.Add($"email.smtp.port {port} is not a port number (1 to 65535).");
        }

        if (User is not null && EmailFieldRules.UserProblem(User) is not null)
        {
            problems.Add("email.smtp.user is not a plain single-line user name.");
        }

        switch (tls.ToLowerInvariant())
        {
            case SmtpConfig.TlsStartTls:
                StartTls = true;
                break;
            case SmtpConfig.TlsNone:
                StartTls = false;
                if (User is not null && !IsThisMachine(host))
                {
                    problems.Add("email.smtp.tls is \"none\" but email.smtp.user is set: Draughtsman will not send a password to another machine unencrypted. Use \"starttls\" (port 587), or drop the user name for a relay that needs none.");
                }

                break;
            default:
                problems.Add($"email.smtp.tls must be \"{SmtpConfig.TlsStartTls}\" or \"{SmtpConfig.TlsNone}\" (it is \"{tls}\"). Implicit TLS on port 465 is not supported; use 587.");
                break;
        }

        if (User is not null && ReadPassword() is null)
        {
            problems.Add($"email.smtp.user is set but no password was found ({PasswordSource()}).");
        }
    }

    internal static bool IsThisMachine(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }

    private static string? ResolvePublicUrl(string configured, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        string text = configured.Trim();
        if (EmailFieldRules.PublicUrlProblem(text) is not null)
        {
            problems.Add($"server.publicUrl \"{(text.Any(char.IsControl) ? "…" : configured)}\" must be an http or https address with no user name, query or fragment, for example https://draughtsman.example.com.");
            return null;
        }

        return text.TrimEnd('/');
    }

    /// <summary>The default timeout, named once for the settings and the example file.</summary>
    internal static class SmtpConfigDefaults
    {
        public const int TimeoutSeconds = 20;
    }
}

/// <summary>The email fields as text, the way the admin form holds them. <c>Provider</c> is smtp, file or empty; <c>Tls</c> is starttls or none (or whatever draughtsman.yaml says, so a bad value is shown rather than hidden).</summary>
public sealed record EmailFormValues(string Provider, string From, string ReplyTo, string Host, int Port, string Tls, string User, string PublicUrl);
