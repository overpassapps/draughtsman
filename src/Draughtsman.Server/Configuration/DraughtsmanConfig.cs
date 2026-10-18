namespace Draughtsman.Server.Configuration;

/// <summary>
/// The instance configuration, read from draughtsman.yaml in the data folder. Every key has a default so an empty
/// or absent file is a valid install. Keys mirror spec section 11. Mutable POCOs because YamlDotNet populates them.
/// Secrets (AI keys) are never in this file; they come from environment variables.
/// </summary>
public sealed class DraughtsmanConfig
{
    public const string FileName = "draughtsman.yaml";

    public const string DataFolderEnvironmentVariable = "DRAUGHTSMAN_DATA";

    public const string DefaultDataFolder = "./data";

    public ServerConfig Server { get; set; } = new();

    public StorageConfig Storage { get; set; } = new();

    public AuthConfig Auth { get; set; } = new();

    public AiConfig Ai { get; set; } = new();

    public KeyRingConfig KeyRing { get; set; } = new();

    public ThemeConfig Theme { get; set; } = new();

    public ThemesConfig Themes { get; set; } = new();

    public ExportConfig Export { get; set; } = new();

    public AssetConfig Assets { get; set; } = new();

    public RetentionConfig Retention { get; set; } = new();

    public McpConfig Mcp { get; set; } = new();

    public SidecarConfig Sidecar { get; set; } = new();

    public AboutConfig About { get; set; } = new();

    public LimitsConfig Limits { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    public EmailConfig Email { get; set; } = new();
}

public sealed class ServerConfig
{
    /// <summary>Port for HTTP, WebSocket and MCP. ASPNETCORE_URLS, when set, wins.</summary>
    public int Port { get; set; } = 5180;

    /// <summary>Bind address. Loopback by default until authentication lands; set it wider deliberately.</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>
    /// Reverse proxies to trust X-Forwarded-For/-Proto from (security review F4): IP addresses or CIDR ranges
    /// (<c>172.18.0.0/16</c>); anything else is logged and ignored (AU-02). Empty means none are trusted except a proxy on
    /// the same machine (a loopback peer) and the raw socket address is used as-is - the safe default, since trusting forwarded headers from an untrusted
    /// caller lets it spoof its own IP and forge the scheme the throttle and cookie logic key on.
    /// </summary>
    public List<string> KnownProxies { get; set; } = [];

    /// <summary>
    /// Set once the operator knows this instance is reached only over HTTPS - directly, or via a proxy listed in
    /// <see cref="KnownProxies"/> - to force the Secure cookie flag and HSTS regardless of what this process sees on
    /// the raw connection (security review F5). Off by default: forcing it on plain HTTP would make every cookie
    /// unusable, since a browser drops a Secure cookie set over an insecure connection.
    /// </summary>
    public bool ForceHttps { get; set; }

    /// <summary>
    /// Host names the server answers to (the request's Host header; a leading <c>*.</c> matches subdomains, <c>*</c>
    /// anything). Empty picks a default from how the server is bound (<see cref="HostPolicy"/>): localhost names only
    /// while it listens on loopback with no proxy in front, anything otherwise, so a proxy or a container works
    /// without configuration and a desktop install cannot be reached under another name (DNS rebinding).
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// The address people reach this instance at, for the links in emails (for example <c>https://draughtsman.example.com</c>).
    /// Never taken from a request's Host header, which a stranger controls: a reset email carrying a link to an address a
    /// stranger chose would hand them the reset. Empty means emailed reset links are not offered (an administrator's reset
    /// link still works, built from the address the administrator is using).
    /// </summary>
    public string PublicUrl { get; set; } = string.Empty;
}

public sealed class StorageConfig
{
    /// <summary>sqlite | postgres. SQL Server arrives in phase 2 (ADR 0003).</summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>Empty means the provider's default: data.db inside the data folder for SQLite.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// SQLite only. An upgrade first copies <c>data.db</c> and checks the copy reads back whole; if the copy cannot be made
    /// (disk full, no permission) the upgrade is refused. Set this to true to upgrade anyway, with no automatic way back.
    /// Take a backup by hand first. Default false.
    /// </summary>
    public bool AllowMigrationWithoutBackup { get; set; }
}

public sealed class AuthConfig
{
    /// <summary>local | oidc. OIDC is phase 2.</summary>
    public string Mode { get; set; } = "local";

    public TokenPolicyConfig Tokens { get; set; } = new();

    public PasswordResetConfig PasswordReset { get; set; } = new();
}

/// <summary>
/// Password recovery (docs/admin-guide.md, "Forgotten passwords"). An administrator can always create a reset link for a
/// person; the self-service "Forgot password?" flow additionally needs outgoing email (<see cref="EmailConfig"/>) and
/// <see cref="ServerConfig.PublicUrl"/>. Lifetimes outside the clamp in <c>PasswordResetPolicy</c> are pulled back to it, so a
/// typo can neither make a link that is already dead nor one that lives for years.
/// </summary>
public sealed class PasswordResetConfig
{
    public const int DefaultEmailedLinkMinutes = 60;

    public const int DefaultAdminLinkHours = 24;

    /// <summary>Offer "Forgot password?" on the sign-in page when email is configured. Turn off to keep recovery to administrator-issued links only.</summary>
    public bool SelfService { get; set; } = true;

    /// <summary>How long a link emailed to a person works, in minutes (5 to 1440).</summary>
    public int EmailedLinkMinutes { get; set; } = DefaultEmailedLinkMinutes;

    /// <summary>How long a link an administrator creates works, in hours (1 to 168). Longer than the emailed one because it travels by a person.</summary>
    public int AdminLinkHours { get; set; } = DefaultAdminLinkHours;
}

/// <summary>
/// Outgoing email (used for password-reset emails and the admin's test message). Empty <see cref="Provider"/> means email is
/// not configured and every feature that needs it is hidden, not broken. Nothing secret is ever in this file: the SMTP
/// password comes from the environment or a file. An administrator can also set all of this at Admin, Email (stored under the
/// data folder, applied with no restart); a key set here wins and is shown read-only on that page. A key left at its default
/// (empty text, port 587, <c>starttls</c>) counts as not set, so it does not lock anything.
/// </summary>
public sealed class EmailConfig
{
    public const string ProviderSmtp = "smtp";

    public const string ProviderFile = "file";

    /// <summary><c>smtp</c> sends through a mail server; <c>file</c> writes each message to a folder and sends nothing (development, demos). Empty means no email.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The From address, for example <c>Draughtsman &lt;draughtsman@example.com&gt;</c>. Required for either provider.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Optional Reply-To address: where a person's reply goes when it should not go to <see cref="From"/>.</summary>
    public string ReplyTo { get; set; } = string.Empty;

    public SmtpConfig Smtp { get; set; } = new();

    public EmailFileConfig File { get; set; } = new();
}

public sealed class SmtpConfig
{
    public const string TlsStartTls = "starttls";

    public const string TlsNone = "none";

    public const int DefaultPort = 587;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = DefaultPort;

    /// <summary>
    /// <c>starttls</c> (default) upgrades the connection with STARTTLS before any password is sent and refuses to carry on if the server
    /// will not. <c>none</c> sends in the clear and is accepted only without a user name, or to a server on this machine. Implicit TLS
    /// (port 465) is not supported by the framework's mail client; use 587, or a local relay.
    /// </summary>
    public string Tls { get; set; } = TlsStartTls;

    /// <summary>The account to sign in to the mail server with. Empty means no sign-in (an open relay on a private network).</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Name of the environment variable holding the SMTP password. The password itself is never in this file.</summary>
    public string PasswordEnvironmentVariable { get; set; } = "DRAUGHTSMAN_SMTP_PASSWORD";

    /// <summary>Path to a file holding the SMTP password (Docker/Kubernetes secrets, a root-only file). When set it is used instead of the environment variable.</summary>
    public string PasswordFile { get; set; } = string.Empty;

    /// <summary>How long to wait for the mail server before giving up and saying so, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 20;
}

public sealed class EmailFileConfig
{
    /// <summary>Where the <c>file</c> provider writes messages, relative to the data folder. Each message holds its link in the clear: development only.</summary>
    public string Folder { get; set; } = "outbox";
}

/// <summary>API token lifetimes (AU-05). Every new token expires; there is no "never" for a token created now.</summary>
public sealed class TokenPolicyConfig
{
    public const int DefaultDefaultLifetimeDays = 90;

    public const int DefaultMaxLifetimeDays = 365;

    /// <summary>The lifetime a token gets when its creator does not choose one.</summary>
    public int DefaultLifetimeDays { get; set; } = DefaultDefaultLifetimeDays;

    /// <summary>The longest lifetime any token may be given; a request for more is refused. The operator's ceiling.</summary>
    public int MaxLifetimeDays { get; set; } = DefaultMaxLifetimeDays;
}

public sealed class AiConfig
{
    /// <summary>anthropic | openai | azure-openai | ollama. Empty means AI features are hidden, not broken.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Model name, except for Azure OpenAI where it is the deployment name.</summary>
    public string Model { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Name of the environment variable holding the API key. The key itself is never in the file.</summary>
    public string ApiKeyEnvironmentVariable { get; set; } = "DRAUGHTSMAN_AI_KEY";

    /// <summary>Azure OpenAI REST API version for the request query string. Empty uses the provider's default.</summary>
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>Whether the model should use a hidden reasoning trace. Off by default for speed.</summary>
    public bool? Think { get; set; } = false;

    /// <summary>Request timeout in seconds. Empty means the provider's default (300s for Ollama).</summary>
    public double? TimeoutSeconds { get; set; }

    /// <summary>
    /// Hosts, beyond each provider's published one, that the AI endpoint may name (docs/ai-endpoint-policy.md): a host name, an IP
    /// address, or <c>*.example.com</c> for every subdomain. A keyed provider may use only its published host (api.openai.com,
    /// api.anthropic.com, *.openai.azure.com) unless it is listed here. Only this file can change the list, never the admin page,
    /// so a stolen admin session cannot widen it.
    /// </summary>
    public List<string> AllowedEndpointHosts { get; set; } = [];
}

/// <summary>
/// How the Data Protection key ring in <c>&lt;data&gt;/keys/</c> is protected at rest. The ring is what encrypts the
/// stored AI API key (<c>ai/settings.json</c>), so a ring left as plain text beside that file makes the encryption
/// decorative: anyone who can read the data folder can read the key. Nothing secret is ever in this file - it names a
/// file, a certificate or the environment variable that holds the key-encryption key, never the key itself.
/// </summary>
public sealed class KeyRingConfig
{
    public const string ProtectionAuto = "auto";

    public const string ProtectionDpapi = "dpapi";

    public const string ProtectionKeychain = "keychain";

    public const string ProtectionKey = "key";

    public const string ProtectionCertificate = "certificate";

    public const string ProtectionNone = "none";

    /// <summary>
    /// auto | dpapi | keychain | key | certificate | none. <c>auto</c> picks, in order: the certificate if one is named,
    /// a key-encryption key if <see cref="KeyEnvironmentVariable"/> or <see cref="KeyFile"/> supplies one, then the
    /// operating system store (Windows DPAPI, macOS Keychain); on Linux with none of those the ring stays unprotected
    /// and the server says so at startup and on the admin AI page. <c>none</c> opts out deliberately.
    /// </summary>
    public string Protection { get; set; } = ProtectionAuto;

    /// <summary>Name of the environment variable holding the key-encryption key (base64 of at least 32 random bytes; <c>openssl rand -base64 32</c>). The key itself is never in this file.</summary>
    public string KeyEnvironmentVariable { get; set; } = "DRAUGHTSMAN_KEY_ENCRYPTION_KEY";

    /// <summary>Path to a file holding the key-encryption key in the same form, for Docker/Kubernetes secrets or a root-only file. Must be outside the data folder.</summary>
    public string KeyFile { get; set; } = string.Empty;

    public KeyRingCertificateConfig Certificate { get; set; } = new();

    /// <summary>
    /// Old install folders to try when a stored secret (the AI API key, the saved SMTP password) was written by a build that bound it to the
    /// folder the program ran from. Only needed when the program was moved at the same time as it was upgraded, so nothing else knows where it
    /// used to be; once a secret has been read it is protected independently of any path and this setting does nothing more.
    /// </summary>
    public List<string> LegacyInstallPaths { get; set; } = [];
}

public sealed class KeyRingCertificateConfig
{
    /// <summary>Path to a PKCS#12 (.pfx) file with an RSA certificate and its private key. Empty means no certificate.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Name of the environment variable holding the file's password. Leave the variable unset for an unprotected .pfx.</summary>
    public string PasswordEnvironmentVariable { get; set; } = "DRAUGHTSMAN_KEY_CERTIFICATE_PASSWORD";
}

public sealed class ThemeConfig
{
    /// <summary>Chrome theme file, relative to the data folder's themes/ directory or to the shipped themes.</summary>
    public string Chrome { get; set; } = "chrome.yaml";

    /// <summary>Default diagram theme name: overpass | neutral | mono, or a client file.</summary>
    public string Diagram { get; set; } = "overpass";
}

/// <summary>Organisation themes (admin Themes page). Separate from <see cref="ThemeConfig"/>, which names the chrome file the instance starts from.</summary>
public sealed class ThemesConfig
{
    /// <summary>"From a website" on the Themes page: building a theme from a page's CSS by address.</summary>
    public ThemeUrlFetchConfig FetchFromUrl { get; set; } = new();
}

/// <summary>
/// Whether the server may fetch a web page for the Themes page's "From a website" (off by default: nothing leaves the network
/// unless an operator turns this on, and then only when an administrator presses the button). Pasting CSS never needs it.
/// </summary>
public sealed class ThemeUrlFetchConfig
{
    /// <summary>False: the server never fetches a page for a theme, and the Themes page offers only the paste-CSS box.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Host names or IP addresses the fetch may reach even though they resolve to a loopback, private, link-local or otherwise
    /// internal address (an intranet site to take the colours of). Exact host names only: no wildcards, no schemes, no ports.
    /// Every other host is checked address by address and refused when any address it resolves to is internal.
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];
}

public sealed class ExportConfig
{
    public ChromiumConfig Chromium { get; set; } = new();
}

public sealed class AssetConfig
{
    /// <summary>Largest upload accepted, in bytes. 10 MiB by default; an oversized body is refused before it is stored.</summary>
    public long MaxSizeBytes { get; set; } = 10 * 1024 * 1024;

    public const long DefaultQuotaBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Most bytes all uploaded images may add up to (AU-15). A new upload that would pass it is refused with 507; identical
    /// bytes already stored never count twice. 5 GiB by default; zero or less means no limit.
    /// </summary>
    public long QuotaBytes { get; set; } = DefaultQuotaBytes;
}

/// <summary>
/// What the server keeps and for how long (OP-03, AU-15, OP-02). Apart from <see cref="BinDays"/> (a deleted document's
/// time in the bin) nothing here touches a document a person saved or a version they named: only the automatic copies
/// and records that would otherwise grow without bound. A value of zero or less means "keep everything" for that item.
/// </summary>
public sealed class RetentionConfig
{
    public const int DefaultPreMigrationCopies = 3;

    public const int DefaultCheckpointsPerDocument = 50;

    public const int DefaultExpiredSessionDays = 7;

    public const int DefaultBinDays = 30;

    /// <summary>How many <c>data.db.pre-migration-*</c> copies of a SQLite database are kept after an upgrade takes a new one.</summary>
    public int PreMigrationCopies { get; set; } = DefaultPreMigrationCopies;

    /// <summary>
    /// How many automatic checkpoints (the snapshot an agent edit takes first) are kept per document, newest first. Older ones
    /// are deleted by the daily maintenance pass. Named versions are never touched.
    /// </summary>
    public int CheckpointsPerDocument { get; set; } = DefaultCheckpointsPerDocument;

    /// <summary>A sign-in session that expired or was revoked more than this many days ago is deleted by the daily maintenance pass.</summary>
    public int ExpiredSessionDays { get; set; } = DefaultExpiredSessionDays;

    /// <summary>
    /// How many days a deleted document stays in the bin before the daily maintenance pass removes it for good (OP-02). Until
    /// then it can be restored with all of its versions. Unlike the automatic copies above, this is a person's document:
    /// zero or less keeps binned documents until someone deletes them permanently, it never deletes anything sooner.
    /// </summary>
    public int BinDays { get; set; } = DefaultBinDays;
}

public sealed class ChromiumConfig
{
    /// <summary>Server-side PDF via Chromium is an optional component; disabled unless installed and enabled here.</summary>
    public bool Enabled { get; set; }

    /// <summary>Path to a Chromium executable. Empty means the bundled optional component's default location.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Chromium's own process sandbox (AU-10): <c>auto</c> tries it and, where the platform refuses (a container without the
    /// privileges it needs), falls back to no sandbox with a warning; <c>on</c> never runs without it; <c>off</c> never
    /// tries. The page being printed has no JavaScript and no network either way.
    /// </summary>
    public string Sandbox { get; set; } = SandboxAuto;

    /// <summary>
    /// The longest one PDF render (and the probe behind health, capped at 30 s) may take, in seconds. A render that overruns has its browser
    /// stopped and fails with a 504 naming what to do, instead of hanging the request for ever. Zero or less means <see cref="DefaultTimeoutSeconds"/>.
    /// </summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    public const int DefaultTimeoutSeconds = 60;

    public const string SandboxAuto = "auto";

    public const string SandboxOn = "on";

    public const string SandboxOff = "off";
}

public sealed class McpConfig
{
    public bool Enabled { get; set; } = true;
}

public sealed class SidecarConfig
{
    public const double DefaultLayoutTimeoutSeconds = 30;

    public const double DefaultYjsTimeoutSeconds = 10;

    /// <summary>Node executable for the sidecar: full path or a name on PATH. Empty means the bundled runtime, then PATH.</summary>
    public string NodePath { get; set; } = string.Empty;

    /// <summary>Path to sidecar.js. Empty means the copy under sidecar/ next to the server binary.</summary>
    public string ScriptPath { get; set; } = string.Empty;

    /// <summary>How long a layout may take before the sidecar is stopped. Must be greater than zero.</summary>
    public double LayoutTimeoutSeconds { get; set; } = DefaultLayoutTimeoutSeconds;

    /// <summary>How long a yjs.* call may take before the sidecar is stopped. Must be greater than zero.</summary>
    public double YjsTimeoutSeconds { get; set; } = DefaultYjsTimeoutSeconds;

    /// <summary>
    /// The configured seconds as a timeout, falling back to <paramref name="defaultSeconds"/> when the value is
    /// not a positive number: zero or less would stop the sidecar before any answer could arrive.
    /// </summary>
    public static TimeSpan ResolveTimeout(double seconds, double defaultSeconds)
    {
        return TimeSpan.FromSeconds(seconds > 0 && double.IsFinite(seconds) ? seconds : defaultSeconds);
    }
}

/// <summary>What the About screen and Help menu point at. A reseller or an air-gapped site can repoint both.</summary>
public sealed class AboutConfig
{
    /// <summary>Who to write to for help: an email address (shown as a mailto link) or any text. Shown to every signed-in user.</summary>
    public string SupportContact { get; set; } = "draughtsman@overpass.co.uk";

    /// <summary>
    /// The documentation the Help menu opens in a new tab; an absolute http(s) URL, anything else is ignored. Unset (the default):
    /// this server's own copy at <c>/docs/</c> when the install ships one (a release archive does), otherwise the vendor's site
    /// (<see cref="Endpoints.AboutEndpoints.VendorDocsUrl"/>). An empty string, written <c>""</c>, hides the entry.
    /// </summary>
    public string? DocsUrl { get; set; }
}

/// <summary>
/// Request size and rate limits (AU-07, AU-09). Every byte count is per request body; every "per minute" figure is per
/// signed-in user (per address when there is none), and 0 turns that one limit off. The defaults are well above what
/// the editor does in normal use: the point is to stop one caller or one leaked token taking the instance down.
/// </summary>
public sealed class LimitsConfig
{
    /// <summary>Largest request body for any route not named below. 1 MiB.</summary>
    public long MaxRequestBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Largest body for a document, a layout, an export or an MCP call. 10 MiB.</summary>
    public long MaxDocumentBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>PDF renders running at once across the instance; each is a Chromium of 150 to 300 MB.</summary>
    public int PdfConcurrency { get; set; } = 2;

    public int PdfPerUserPerMinute { get; set; } = 10;

    /// <summary>Layouts running at once; one Node sidecar serves them all.</summary>
    public int LayoutConcurrency { get; set; } = 4;

    public int LayoutPerUserPerMinute { get; set; } = 30;

    public int ImportPerUserPerMinute { get; set; } = 60;

    /// <summary>AI calls in flight at once; each holds a connection to the provider open.</summary>
    public int AiConcurrency { get; set; } = 4;

    public int AiPerUserPerMinute { get; set; } = 20;

    /// <summary>Calls to /mcp per minute: an agent makes many small ones, so this is the loosest limit.</summary>
    public int McpPerUserPerMinute { get; set; } = 300;

    /// <summary>
    /// Document thumbnails drawn at once. A request over it waits its turn rather than being refused: a documents page asks for
    /// every card's thumbnail together, and a cold one must still load.
    /// </summary>
    public int ThumbnailConcurrency { get; set; } = 2;

    /// <summary>Thumbnail requests per minute: loose, since a page of cards asks for fifty and a revalidation is nearly free.</summary>
    public int ThumbnailPerUserPerMinute { get; set; } = 600;
}

/// <summary>Log levels and the optional rolling log file (AU-14).</summary>
public sealed class LoggingConfig
{
    /// <summary>Level for the server's own messages: Trace, Debug, Information, Warning, Error or None.</summary>
    public string Level { get; set; } = "Information";

    /// <summary>
    /// Level for ASP.NET Core (a line for every request), Entity Framework (a line for every SQL command) and the HTTP
    /// client. Warning by default: one editor session otherwise writes thousands of lines a minute.
    /// </summary>
    public string FrameworkLevel { get; set; } = "Warning";

    public LogFileConfig File { get; set; } = new();
}

public sealed class LogFileConfig
{
    /// <summary>Also write the log to daily files in <c>&lt;data&gt;/logs/</c>. The console log is unaffected.</summary>
    public bool Enabled { get; set; }

    /// <summary>How many days of files to keep; older ones are deleted when a new day starts.</summary>
    public int RetainDays { get; set; } = 14;

    /// <summary>A day's file stops growing at this size (10 MiB); the rest of the day goes to a numbered successor.</summary>
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;
}
