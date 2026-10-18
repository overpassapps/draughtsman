namespace Draughtsman.Server.Diagnostics;

/// <summary>How a check came out. <see cref="Info"/> is a fact worth printing that is neither good nor bad (the doctor only; the self-check page has pass, warn and fail).</summary>
public enum DiagnosticStatus
{
    Pass,
    Info,
    Warn,
    Fail,
}

/// <summary>
/// What to change to fix a finding, in the places an operator changes it (a proxy's configuration, <c>draughtsman.yaml</c>, or a command to run). Each is plain text ready to paste, or null when
/// that place is not where the fix lives. Never contains a secret: the only values ever filled in are an address the request
/// came from and the setting names.
/// </summary>
public sealed record DiagnosticFix(string? Nginx = null, string? Caddy = null, string? Cloudflare = null, string? Yaml = null, string? Shell = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Nginx is null && Caddy is null && Cloudflare is null && Yaml is null && Shell is null;
}

/// <summary>
/// One line of a diagnostic: the connection self-check page (<c>GET /api/admin/selfcheck</c>) and <c>draughtsman doctor</c> both
/// print these. <see cref="Facts"/> are the raw observations the verdict rests on (header values, addresses), so an operator can
/// see what the server saw and not only what it concluded; they never carry a secret, a cookie value or a document.
/// </summary>
public sealed record DiagnosticItem(
    string Id,
    string Title,
    DiagnosticStatus Status,
    string Summary,
    IReadOnlyList<string>? Detail = null,
    IReadOnlyDictionary<string, string>? Facts = null,
    DiagnosticFix? Fix = null)
{
    public static DiagnosticItem Pass(string id, string title, string summary, IReadOnlyDictionary<string, string>? facts = null, IReadOnlyList<string>? detail = null)
    {
        return new DiagnosticItem(id, title, DiagnosticStatus.Pass, summary, detail, facts);
    }

    public static DiagnosticItem Info(string id, string title, string summary, IReadOnlyDictionary<string, string>? facts = null, IReadOnlyList<string>? detail = null)
    {
        return new DiagnosticItem(id, title, DiagnosticStatus.Info, summary, detail, facts);
    }

    public static DiagnosticItem Warn(string id, string title, string summary, IReadOnlyDictionary<string, string>? facts = null, IReadOnlyList<string>? detail = null, DiagnosticFix? fix = null)
    {
        return new DiagnosticItem(id, title, DiagnosticStatus.Warn, summary, detail, facts, fix);
    }

    public static DiagnosticItem Fail(string id, string title, string summary, IReadOnlyDictionary<string, string>? facts = null, IReadOnlyList<string>? detail = null, DiagnosticFix? fix = null)
    {
        return new DiagnosticItem(id, title, DiagnosticStatus.Fail, summary, detail, facts, fix);
    }
}
