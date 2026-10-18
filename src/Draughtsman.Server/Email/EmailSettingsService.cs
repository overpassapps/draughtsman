using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Email;

/// <summary>What the browser sends to save, or to test without saving. Every member is optional; a blank text field clears its saved value. Locked fields (set by draughtsman.yaml) may be sent back unchanged and are then ignored.</summary>
public sealed record EmailSettingsRequest(
    string? From = null,
    string? ReplyTo = null,
    string? Host = null,
    int? Port = null,
    string? Tls = null,
    string? User = null,
    string? PublicUrl = null,
    string? PasswordAction = null,
    string? Password = null);

/// <summary>One thing wrong with a request: which field, and a sentence for an administrator. Never carries the rejected value.</summary>
public sealed record EmailFieldError(string Field, string Message);

/// <summary>What <see cref="EmailSettingsService.Plan"/> worked out: the errors that stop it, or the settings that would result and which fields changed.</summary>
public sealed class EmailPlan
{
    internal EmailPlan(IReadOnlyList<EmailFieldError> errors, EmailOverrides overrides, string? password, EmailSettings? candidate, IReadOnlyList<string> changed)
    {
        Errors = errors;
        Overrides = overrides;
        Password = password;
        Candidate = candidate;
        Changed = changed;
    }

    public IReadOnlyList<EmailFieldError> Errors { get; }

    public bool Valid => Errors.Count == 0;

    internal EmailOverrides Overrides { get; }

    internal string? Password { get; }

    /// <summary>The settings as they would be if the request were saved: what Test these values uses.</summary>
    public EmailSettings? Candidate { get; }

    /// <summary>Names of what changed ("host", "password replaced"): fields, never values.</summary>
    public IReadOnlyList<string> Changed { get; }
}

/// <summary>
/// The live outgoing-email configuration (docs/api.md "## Email"): draughtsman.yaml layered with what an administrator saved, applied the
/// moment it is saved. Every consumer asks <see cref="Current"/> at the moment it needs a setting rather than holding one from startup, which is
/// what lets a change land with no restart. All validation of what an administrator types is in <see cref="Plan"/>, shared by Save and by Test
/// these values, so the two refuse the same things.
/// <para>
/// Precedence is <see cref="EmailSettings"/>'s: a value draughtsman.yaml sets locks its field, and the page cannot change it. A saved value for a
/// field the file later takes over stays on disk underneath (it applies again if the file's key is removed). The password is write-only: it is
/// held in memory only to send mail, and nothing here returns, logs or throws it.
/// </para>
/// </summary>
public sealed class EmailSettingsService
{
    private readonly DraughtsmanConfig _file;
    private readonly string _dataFolder;
    private readonly EmailSettingsStore? _store;
    private readonly Func<string, string?>? _environment;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private volatile EmailSettings _current;

    internal EmailSettingsService(DraughtsmanConfig file, string dataFolder, EmailSettingsStore? store, ILogger logger, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(logger);
        _file = file;
        _dataFolder = dataFolder;
        _store = store;
        _logger = logger;
        _environment = environment;
        _current = Build(store?.Load() ?? StoredEmailSettings.Empty);
        if (!_current.Stored.Overrides.IsEmpty)
        {
            logger.LogInformation("Email settings saved by an administrator were loaded from the data folder; draughtsman.yaml still wins where it sets a value.");
        }
    }

    /// <summary>The settings in effect right now. A fresh snapshot after every save, so read it when you need it, not once.</summary>
    public EmailSettings Current => _current;

    /// <summary>Something is saved at Admin, Email (so there is something to remove).</summary>
    public bool HasSaved => !_current.Stored.Overrides.IsEmpty || _current.Stored.Password is not null || _current.Stored.PasswordUnreadable;

    /// <summary>
    /// Validates <paramref name="request"/> against the current state and works out what saving it would do, without saving anything. A request that
    /// would leave email half set up is refused whole, so what is stored is always either complete or off.
    /// </summary>
    public EmailPlan Plan(EmailSettingsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PlanAgainst(_current, request);
    }

    /// <summary>Saves a valid plan and applies it at once. Returns the names of what changed (empty when nothing did); throws if the file could not be written, leaving the old settings in effect.</summary>
    internal IReadOnlyList<string> Apply(EmailSettingsRequest request, out IReadOnlyList<EmailFieldError> errors)
    {
        lock (_gate)
        {
            EmailPlan plan = PlanAgainst(_current, request);
            errors = plan.Errors;
            if (!plan.Valid)
            {
                return [];
            }

            _store?.Save(plan.Overrides, plan.Password);
            _current = Build(new StoredEmailSettings(plan.Overrides, plan.Password, false));
            return plan.Changed;
        }
    }

    /// <summary>Back to draughtsman.yaml alone: every saved field and the saved password are forgotten.</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            _store?.Save(EmailOverrides.None, null);
            _current = Build(StoredEmailSettings.Empty);
        }
    }

    private EmailSettings Build(StoredEmailSettings stored)
    {
        return new EmailSettings(_file.Email, _file.Server, _dataFolder, _environment, stored);
    }

    private EmailPlan PlanAgainst(EmailSettings now, EmailSettingsRequest request)
    {
        var errors = new List<EmailFieldError>();
        EmailOverrides before = now.Stored.Overrides;
        IReadOnlyDictionary<string, string> locks = now.Locks;
        EmailFormValues form = now.Form;

        void Refuse(string field, string message)
        {
            errors.Add(new EmailFieldError(field, message));
        }

        // A locked field is not ours to change. A request that sends it back as the page showed it is fine and ignored; one that
        // tries a different value is refused by name, so a script cannot believe it changed something it did not.
        bool Locked(string field, string label, string? sent, string effective)
        {
            if (!locks.ContainsKey(field))
            {
                return false;
            }

            string trimmed = sent?.Trim() ?? string.Empty;
            if (trimmed.Length > 0 && !string.Equals(trimmed, effective, StringComparison.OrdinalIgnoreCase))
            {
                Refuse(field, $"{label} cannot be changed here: {locks[field]}");
            }

            return true;
        }

        string? Text(string field, string label, string? sent, string effective, string? current, Func<string, string?> problem)
        {
            if (Locked(field, label, sent, effective))
            {
                return current;
            }

            string trimmed = sent?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (problem(trimmed) is { } message)
            {
                Refuse(field, message);
                return current;
            }

            return trimmed;
        }

        string? from = Text(EmailSettings.FieldFrom, "The From address", request.From, form.From, before.From, v => EmailFieldRules.SenderProblem(v, "The From address", out _));
        string? replyTo = Text(EmailSettings.FieldReplyTo, "The Reply-to address", request.ReplyTo, form.ReplyTo, before.ReplyTo, v => EmailFieldRules.SenderProblem(v, "The Reply-to address", out _));
        string? host = Text(EmailSettings.FieldHost, "The mail server", request.Host, form.Host, before.Host, EmailFieldRules.HostProblem);
        string? user = Text(EmailSettings.FieldUser, "The user name", request.User, form.User, before.User, EmailFieldRules.UserProblem);
        string? publicUrl = Text(EmailSettings.FieldPublicUrl, "The public address", request.PublicUrl, form.PublicUrl, before.PublicUrl, EmailFieldRules.PublicUrlProblem);

        string? tls = Text(
            EmailSettings.FieldTls,
            "The encryption",
            request.Tls,
            form.Tls,
            before.Tls,
            v => v.ToLowerInvariant() is SmtpConfig.TlsStartTls or SmtpConfig.TlsNone ? null : "The encryption must be STARTTLS or None. Implicit TLS on port 465 is not supported: use the provider's port 587 with STARTTLS.");
        tls = tls?.ToLowerInvariant();
        if (tls == SmtpConfig.TlsStartTls)
        {
            tls = null;
        }

        int? port = before.Port;
        if (!Locked(EmailSettings.FieldPort, "The port", request.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture), form.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            port = request.Port;
            if (port is { } number)
            {
                if (EmailFieldRules.PortProblem(number) is { } message)
                {
                    Refuse(EmailSettings.FieldPort, message);
                    port = before.Port;
                }
                else if (number == SmtpConfig.DefaultPort)
                {
                    port = null;
                }
            }
        }

        // Password: keep (the default), replace or clear. The value is checked for shape and never repeated in a message.
        string action = (request.PasswordAction ?? "keep").Trim().ToLowerInvariant();
        string? password = now.Stored.Password;
        switch (action)
        {
            case "keep":
                break;
            case "replace":
                if (locks.ContainsKey(EmailSettings.FieldPassword))
                {
                    Refuse(EmailSettings.FieldPassword, $"The password cannot be changed here: {locks[EmailSettings.FieldPassword]}");
                }
                else if (EmailFieldRules.PasswordProblem(request.Password ?? string.Empty) is { } message)
                {
                    Refuse(EmailSettings.FieldPassword, message);
                }
                else
                {
                    password = request.Password;
                }

                break;
            case "clear":
                if (locks.ContainsKey(EmailSettings.FieldPassword))
                {
                    Refuse(EmailSettings.FieldPassword, $"The password cannot be changed here: {locks[EmailSettings.FieldPassword]}");
                }
                else
                {
                    password = null;
                }

                break;
            default:
                Refuse(EmailSettings.FieldPassword, "The password action must be keep, replace or clear.");
                break;
        }

        // Email is "on" at the browser's say-so when any mail-server field is filled in, or a password is being set; and it stays on while the
        // file supplies the server and only the rest is being edited. Leaving them all blank (a form with only the public address, say) leaves it
        // off, and leaves nothing half-saved behind.
        bool Filled(string field, string? value)
        {
            return !locks.ContainsKey(field) && !string.IsNullOrEmpty(value);
        }

        bool filled = Filled(EmailSettings.FieldFrom, from) || Filled(EmailSettings.FieldHost, host) || Filled(EmailSettings.FieldUser, user) || Filled(EmailSettings.FieldReplyTo, replyTo) || action == "replace";
        bool fileSuppliesServer = locks.ContainsKey(EmailSettings.FieldHost) || locks.ContainsKey(EmailSettings.FieldFrom);
        string? provider = locks.ContainsKey(EmailSettings.FieldProvider)
            ? before.Provider
            : filled || (before.Provider == EmailConfig.ProviderSmtp && fileSuppliesServer) ? EmailConfig.ProviderSmtp : null;

        // A saved password with no user name to use it for is a stray secret: it goes, and a new one is refused.
        string effectiveUser = locks.ContainsKey(EmailSettings.FieldUser) ? form.User : user ?? string.Empty;
        if (effectiveUser.Length == 0 && password is not null)
        {
            if (action == "replace" && errors.All(e => e.Field != EmailSettings.FieldPassword))
            {
                Refuse(EmailSettings.FieldPassword, "A password needs a user name to go with it. Enter the user name, or leave the password out for a server that needs no sign-in.");
            }

            password = null;
        }

        var overrides = new EmailOverrides(provider, from, replyTo, host, port, tls, user, publicUrl);
        var stored = new StoredEmailSettings(overrides, password, false);
        var candidate = new EmailSettings(_file.Email, _file.Server, _dataFolder, _environment, stored);

        if (errors.Count == 0)
        {
            if (candidate.Provider == EmailConfig.ProviderSmtp)
            {
                // Everything a send needs must be there: the same checks the file gets, said in the same words, so the form cannot save what the file would refuse.
                foreach (string problem in candidate.Problems)
                {
                    Refuse("form", problem);
                }
            }

            foreach (string problem in candidate.LinkProblems)
            {
                Refuse(EmailSettings.FieldPublicUrl, problem);
            }
        }

        return new EmailPlan(errors, overrides, password, candidate, errors.Count == 0 ? Diff(now.Stored, overrides, password, action) : []);
    }

    private static List<string> Diff(StoredEmailSettings was, EmailOverrides now, string? password, string action)
    {
        EmailOverrides before = was.Overrides;
        var changed = new List<string>();
        if (before.Provider != now.Provider)
        {
            changed.Add(now.Provider is null ? "email turned off" : "email turned on");
        }

        void Check(string name, object? a, object? b)
        {
            if (!Equals(a, b))
            {
                changed.Add(name);
            }
        }

        Check("mail server", before.Host, now.Host);
        Check("port", before.Port, now.Port);
        Check("encryption", before.Tls, now.Tls);
        Check("user name", before.User, now.User);
        Check("from address", before.From, now.From);
        Check("reply-to address", before.ReplyTo, now.ReplyTo);
        Check("public address", before.PublicUrl, now.PublicUrl);
        if (action == "replace" && password is not null)
        {
            changed.Add("password replaced");
        }
        else if (was.Password is not null && password is null)
        {
            changed.Add("password cleared");
        }

        return changed;
    }
}
