using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Auth.PasswordReset;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.KeyRing;
using Draughtsman.Server.Theming;

namespace Draughtsman.Server.Email;

/// <summary>
/// The admin Email page (design/atlas/password-recovery.md, amendment 2026-10-01; docs/api.md "## Email"): what outgoing email is set to, the
/// editable form behind it, and the buttons that send a test message. Modelled on <c>AiSettingsEndpoints</c>: every route is
/// <see cref="AuthPolicies.CookieAdminOnly"/> (a cookie-authenticated administrator, never an API token whatever its owner's role: a token must not
/// be able to point the instance's mail at another server), writes carry the CSRF header, and a change is recorded as a security event that names
/// what changed and never a value.
/// <para>
/// The SMTP password is write-only: it arrives in <c>PUT</c> (or in a test), is stored encrypted, and no response, log line, event or error
/// carries it. Said plainly, an administrator can make this server connect to any host and port: that is what an outgoing-mail setting is, which
/// is why only an administrator on a browser session can set it, the test goes only to that administrator's own address, and the mail server's
/// reply text is never shown back (only a short class of failure).
/// </para>
/// </summary>
public static class EmailEndpoints
{
    /// <summary>Test messages one administrator can send per throttle window; the message goes only to their own address, so this is about not hammering a mail server.</summary>
    private const int TestsPerWindow = 5;

    public static IEndpointRouteBuilder MapEmailEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/admin/email").RequireAuthorization(AuthPolicies.CookieAdminOnly);
        group.MapGet("", Get).WithName("GetEmailSettings");
        group.MapPut("", Save).WithName("SaveEmailSettings");
        group.MapDelete("", Reset).WithName("ResetEmailSettings");
        group.MapPost("/test", SendTest).WithName("SendTestEmail");
        return app;
    }

    private static IResult Get(EmailSettingsService settings, PasswordResetPolicy policy, DraughtsmanConfig config, IServiceProvider services)
    {
        return Results.Ok(Describe(settings, policy, config, services.GetService<KeyRingStatus>()));
    }

    private static async Task<IResult> Save(HttpContext context, EmailSettingsService settings, PasswordResetPolicy policy, DraughtsmanConfig config, SecurityEventLog events, IServiceProvider services, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        (bool readable, EmailSettingsRequest? body) = await ReadBody<EmailSettingsRequest>(context, cancellationToken);
        if (!readable || body is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Malformed body", "The body must be a JSON object with the email settings.", []);
        }

        IReadOnlyList<string> changed;
        IReadOnlyList<EmailFieldError> errors;
        try
        {
            changed = settings.Apply(body, out errors);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Type only: the old settings stay in effect, and the message never has a value in it.
            loggers.CreateLogger("Draughtsman.Server.Email.Settings").LogError("Email settings could not be written ({Reason}).", error.GetType().Name);
            return Problem(StatusCodes.Status500InternalServerError, "Could not save", "The settings could not be written to the data folder. Nothing was changed; check the server's log.", []);
        }

        if (errors.Count > 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Cannot save these settings", string.Join(' ', errors.Select(e => e.Message)), errors);
        }

        if (changed.Count > 0)
        {
            await events.Record(context, SecurityEventTypes.EmailSettingsChanged, detail: string.Join(", ", changed));
        }

        return Results.Ok(Describe(settings, policy, config, services.GetService<KeyRingStatus>()));
    }

    private static async Task<IResult> Reset(HttpContext context, EmailSettingsService settings, PasswordResetPolicy policy, DraughtsmanConfig config, SecurityEventLog events, IServiceProvider services)
    {
        bool hadSaved = settings.HasSaved;
        settings.Reset();
        if (hadSaved)
        {
            await events.Record(context, SecurityEventTypes.EmailSettingsReset, detail: "saved email settings and password removed; back to draughtsman.yaml");
        }

        return Results.Ok(Describe(settings, policy, config, services.GetService<KeyRingStatus>()));
    }

    private static EmailSettingsResponse Describe(EmailSettingsService service, PasswordResetPolicy policy, DraughtsmanConfig config, KeyRingStatus? keyRing)
    {
        EmailSettings email = service.Current;
        EmailFormValues form = email.Form;
        var problems = email.Problems.Concat(email.LinkProblems).ToList();
        if (email.Stored.PasswordUnreadable)
        {
            // The startup check says which: an install path an earlier version tied it to, a key ring that cannot be opened, or damage, and what to do.
            problems.Add(keyRing?.Describe().StoredSecrets?.FirstOrDefault(s => s is { Name: "SMTP password", State: "unreadable" })?.Message ?? "The saved SMTP password could not be decrypted (the server's key ring changed). Enter the password again.");
        }

        if (email.Configured && email.PublicUrl is null && email.LinkProblems.Count == 0)
        {
            problems.Add("The public address is not set, so emailed reset links are off: an email needs the address people reach this instance at. Set it to, for example, https://draughtsman.example.com. Administrator-issued reset links work without it.");
        }

        return new EmailSettingsResponse(
            email.Configured,
            string.IsNullOrEmpty(email.Provider) ? null : email.Provider,
            Blank(form.From),
            Blank(form.ReplyTo),
            Blank(form.Host),
            form.Port,
            form.Tls.ToLowerInvariant(),
            Blank(form.User),
            email.PasswordSet,
            email.PasswordKind,
            email.Stored.PasswordUnreadable,
            email.PasswordSet || email.Stored.PasswordUnreadable || !string.IsNullOrWhiteSpace(form.User) ? email.PasswordSource() : null,
            email.Provider == EmailConfig.ProviderFile ? email.OutboxFolder : null,
            Blank(form.PublicUrl),
            email.Locks,
            service.HasSaved,
            config.Auth.PasswordReset.SelfService,
            policy.SelfServiceAvailable,
            (int)policy.EmailedLifetime.TotalMinutes,
            (int)policy.AdminLifetime.TotalHours,
            problems,
            keyRing?.Describe());
    }

    /// <summary>
    /// Sends one message to the signed-in administrator's own address (never to an address typed into the request: this must not be a
    /// way to send mail to anyone) and says what happened. With a <c>values</c> object in the body it tests those form values without storing
    /// anything; without one it tests what is saved. A failure is a 200 with <c>ok: false</c> and a short class of reason (never the server's own
    /// reply text), the way the AI page's connection test reports one.
    /// </summary>
    private static async Task<IResult> SendTest(HttpContext context, EmailSettingsService settings, IEmailSender sender, IUserStore users, FailedSignInThrottle throttle, SecurityEventLog events, ChromeBrandingService branding, TimeProvider clock, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        // The body is optional (the button with nothing in it tests what is saved), and a request with no body at all is not bound by the framework: read it here.
        (bool readable, EmailTestRequest? body) = await ReadBody<EmailTestRequest>(context, cancellationToken);
        if (!readable)
        {
            return Problem(StatusCodes.Status400BadRequest, "Malformed body", "The body must be a JSON object, or empty.", []);
        }

        EmailSettings email = settings.Current;
        IEmailSender send = sender;
        bool unsaved = body?.Values is not null;
        if (unsaved)
        {
            EmailPlan plan = settings.Plan(body!.Values!);
            if (!plan.Valid)
            {
                return Problem(StatusCodes.Status400BadRequest, "Cannot test these settings", string.Join(' ', plan.Errors.Select(e => e.Message)), plan.Errors);
            }

            email = plan.Candidate!;
            send = LiveEmailSender.For(email, clock, loggers);
        }

        if (!email.Configured)
        {
            string why = email.Problems.Count > 0 ? string.Join(' ', email.Problems) : "Enter the mail server and the From address first (or set email.provider in draughtsman.yaml).";
            return Results.Ok(new EmailTestResult(false, "Outgoing email is not configured. " + why));
        }

        string adminId = context.User.UserId() ?? string.Empty;
        User? admin = await users.GetUserById(adminId, cancellationToken);
        if (admin is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Not signed in", detail: "Sign in again.");
        }

        string throttleKey = "emailtest:" + adminId;
        if (throttle.IsBlocked(throttleKey, TestsPerWindow))
        {
            return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many tests", detail: $"That is {TestsPerWindow} test emails in {FailedSignInThrottle.Window.TotalMinutes:0} minutes; wait a little before sending another.");
        }

        throttle.RecordFailure(throttleKey);
        string which = unsaved ? "the values in the form, not saved" : "saved settings";
        try
        {
            await send.SendAsync(PasswordResetMessages.Test(admin.Email, branding.Current.ProductName, clock.GetUtcNow()), cancellationToken);
        }
        catch (EmailSendException error)
        {
            await events.Record(context, SecurityEventTypes.EmailTestFailed, target: admin, detail: $"{error.Message} ({which})");
            return Results.Ok(new EmailTestResult(false, error.Message));
        }

        await events.Record(context, SecurityEventTypes.EmailTestSent, target: admin, detail: $"test email sent ({email.Provider}; {which})");
        string where = email.Provider == EmailConfig.ProviderFile ? $"written to {email.OutboxFolder} (the file provider sends nothing)" : $"handed to {email.Host}:{email.Port}";
        return Results.Ok(new EmailTestResult(true, $"Test email to {admin.Email} {where}." + (unsaved ? " Nothing was saved." : string.Empty)));
    }

    /// <summary>
    /// Reads an optional JSON body ourselves rather than have the framework bind it: a request with no body at all is not bound by a body parameter
    /// (it never reaches the handler), and a body that is not JSON must be a plain 400 here, not whatever the host's exception handler makes of it.
    /// </summary>
    private static async Task<(bool Readable, T? Value)> ReadBody<T>(HttpContext context, CancellationToken cancellationToken)
        where T : class
    {
        if (!context.Request.HasJsonContentType() || context.Request.ContentLength is 0)
        {
            return (true, null);
        }

        try
        {
            return (true, await context.Request.ReadFromJsonAsync<T>(cancellationToken));
        }
        catch (System.Text.Json.JsonException)
        {
            return (false, null);
        }
    }

    private static string? Blank(string value)
    {
        return value.Length == 0 ? null : value;
    }

    private static IResult Problem(int status, string title, string detail, IReadOnlyList<EmailFieldError> errors)
    {
        return Results.Problem(statusCode: status, title: title, detail: detail, extensions: new Dictionary<string, object?> { ["errors"] = errors });
    }
}

/// <summary>
/// Body of every email settings response. Nothing in it is, or contains, the SMTP password: <c>PasswordSet</c> says whether one is available,
/// <c>PasswordKind</c> (<c>environment</c>, <c>file</c>, <c>saved</c>, <c>none</c>) which source is in effect, and <c>PasswordSource</c> names where
/// it comes from. <c>Locked</c> maps each field draughtsman.yaml (or the environment) sets to the reason it cannot be edited here.
/// </summary>
public sealed record EmailSettingsResponse(
    bool Configured,
    string? Provider,
    string? From,
    string? ReplyTo,
    string? Host,
    int Port,
    string Tls,
    string? User,
    bool PasswordSet,
    string PasswordKind,
    bool PasswordUnreadable,
    string? PasswordSource,
    string? OutboxFolder,
    string? PublicUrl,
    IReadOnlyDictionary<string, string> Locked,
    bool HasSaved,
    bool SelfServiceEnabled,
    bool SelfServiceAvailable,
    int EmailedLinkMinutes,
    int AdminLinkHours,
    IReadOnlyList<string> Problems,
    KeyRingState? KeyRing);

/// <summary>Body of POST /api/admin/email/test: optional <c>values</c> to test without saving. Any other member (a stray <c>to</c>) is ignored: the test goes to the signed-in administrator.</summary>
public sealed record EmailTestRequest(EmailSettingsRequest? Values = null);

/// <summary>Body of POST /api/admin/email/test.</summary>
public sealed record EmailTestResult(bool Ok, string Message);
