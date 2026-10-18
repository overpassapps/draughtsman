using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Diagnostics;

/// <summary>
/// The effective configuration as a support bundle shows it: every key, its value when the key is on the allow-list of settings that can never
/// hold a secret, and only <c>(set)</c> or <c>(not set)</c> for every other key. An ALLOW-list, not a block-list: a setting added next
/// year that holds a password is hidden until someone decides it is safe to show, rather than shown until someone remembers to hide it.
/// The tests plant a sentinel in every property that is not listed and check none of them can reach the output.
/// </summary>
public static class EffectiveSettings
{
    /// <summary>The settings whose values are safe to send: switches, numbers, names, hosts, paths and the NAMES of environment variables (never their values).</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "server.port", "server.host", "server.knownProxies", "server.forceHttps", "server.allowedHosts", "server.publicUrl",
        "storage.provider", "storage.allowMigrationWithoutBackup",
        "auth.mode", "auth.tokens.defaultLifetimeDays", "auth.tokens.maxLifetimeDays",
        "auth.passwordReset.selfService", "auth.passwordReset.emailedLinkMinutes", "auth.passwordReset.adminLinkHours",
        "ai.provider", "ai.model", "ai.apiKeyEnvironmentVariable", "ai.apiVersion", "ai.think", "ai.timeoutSeconds", "ai.allowedEndpointHosts",
        "keyRing.protection", "keyRing.keyEnvironmentVariable", "keyRing.certificate.passwordEnvironmentVariable",
        "theme.chrome", "theme.diagram",
        "themes.fetchFromUrl.enabled", "themes.fetchFromUrl.allowedHosts",
        "export.chromium.enabled", "export.chromium.path", "export.chromium.sandbox", "export.chromium.timeoutSeconds",
        "assets.maxSizeBytes", "assets.quotaBytes",
        "retention.preMigrationCopies", "retention.checkpointsPerDocument", "retention.expiredSessionDays", "retention.binDays",
        "mcp.enabled",
        "sidecar.nodePath", "sidecar.scriptPath", "sidecar.layoutTimeoutSeconds", "sidecar.yjsTimeoutSeconds",
        "about.supportContact", "about.docsUrl",
        "limits.maxRequestBodyBytes", "limits.maxDocumentBytes", "limits.pdfConcurrency", "limits.pdfPerUserPerMinute", "limits.layoutConcurrency",
        "limits.layoutPerUserPerMinute", "limits.importPerUserPerMinute", "limits.aiConcurrency", "limits.aiPerUserPerMinute", "limits.mcpPerUserPerMinute",
        "limits.thumbnailConcurrency", "limits.thumbnailPerUserPerMinute",
        "logging.level", "logging.frameworkLevel", "logging.file.enabled", "logging.file.retainDays", "logging.file.maxFileBytes",
        "email.provider", "email.smtp.host", "email.smtp.port", "email.smtp.tls", "email.smtp.passwordEnvironmentVariable", "email.smtp.timeoutSeconds", "email.file.folder",
    };

    /// <summary>The settings that name an address people use, shown as the host alone: a path or a query on it could carry a key.</summary>
    private static readonly HashSet<string> s_hostOnly = new(StringComparer.Ordinal) { "ai.endpoint" };

    /// <summary>One line per setting, in the file's own order, as <c>path: value</c>, <c>path: (set)</c> or <c>path: (not set)</c>.</summary>
    public static IReadOnlyList<string> Lines(DraughtsmanConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var lines = new List<string>();
        Walk(config, new DraughtsmanConfig(), string.Empty, lines);
        return lines;
    }

    /// <summary>The settings listing for the bundle: a header, every line, and what was left unshown and why.</summary>
    public static string Describe(DraughtsmanConfig config, bool fileExists, long fileBytes, IReadOnlyList<ConfigWarning> warnings, IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        ArgumentNullException.ThrowIfNull(config);
        var text = new StringBuilder();
        text.Append("Effective settings: draughtsman.yaml as this version reads it, over the defaults.\n");
        text.Append("A value is shown only for settings on the allow-list of ones that cannot hold a secret. Every other setting shows (set) or (not set).\n");
        text.Append("The draughtsman.yaml file itself is not in this bundle.\n\n");
        text.Append("draughtsman.yaml: ").Append(fileExists ? "present, " + fileBytes.ToString(CultureInfo.InvariantCulture) + " bytes" : "absent (every setting is its default)").Append('\n');
        foreach (ConfigWarning warning in warnings)
        {
            text.Append("note: ").Append(Redactor.Redact(warning.Message, truncateAddresses: false)).Append('\n');
        }

        text.Append('\n');
        foreach (string line in Lines(config))
        {
            text.Append(line).Append('\n');
        }

        text.Append("\nEnvironment variables that configure Draughtsman (values shown only for the ones that cannot hold a secret):\n");
        foreach ((string name, string value) in environment)
        {
            text.Append(name).Append(": ").Append(Clean(value)).Append('\n');
        }

        return text.ToString();
    }

    private static void Walk(object current, object defaults, string prefix, List<string> lines)
    {
        foreach (PropertyInfo property in current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            string path = prefix + ToCamel(property.Name);
            object? value = property.GetValue(current);
            object? baseline = property.GetValue(defaults);
            if (IsGroup(property.PropertyType) && value is not null && baseline is not null)
            {
                Walk(value, baseline, path + ".", lines);
                continue;
            }

            lines.Add(path + ": " + Show(path, value, baseline));
        }
    }

    private static string Show(string path, object? value, object? baseline)
    {
        bool set = !Same(value, baseline) || (value is string text && text.Length > 0 && baseline is string defaultText && defaultText.Length == 0);
        if (s_hostOnly.Contains(path))
        {
            string? raw = value as string;
            return string.IsNullOrWhiteSpace(raw)
                ? "(not set)"
                : Uri.TryCreate(raw, UriKind.Absolute, out Uri? uri) ? $"(set; host {uri.Host})" : "(set)";
        }

        if (!Allowed.Contains(path))
        {
            return set ? (value is ICollection { Count: > 0 } list ? $"(set, {list.Count.ToString(CultureInfo.InvariantCulture)} entr{(list.Count == 1 ? "y" : "ies")})" : "(set)") : "(not set)";
        }

        // Each value is cleaned ON ITS OWN: the redactor's rule for a secret-looking NAME would blank "auth.tokens.defaultLifetimeDays: 90" and the name of an
        // environment variable ("email.smtp.passwordEnvironmentVariable: DRAUGHTSMAN_SMTP_PASSWORD") for containing the word token or password, and the names
        // here are fixed words, not text a caller sent. What a value could still hold (a URL's password, an email, a long token) is what the value rules take out.
        return value switch
        {
            null => "(not set)",
            bool flag => flag ? "true" : "false",
            string text2 => text2.Length == 0 ? "(empty)" : Clean(text2),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            IEnumerable<string> items => items.Any() ? "[" + string.Join(", ", items.Select(Clean)) + "]" : "[]",
            _ => "(unprintable)",
        };
    }

    private static string Clean(string value)
    {
        // Addresses stay exact: a trusted proxy's address is the reason a settings listing is read.
        return Redactor.Redact(value, truncateAddresses: false);
    }

    private static bool Same(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a is IEnumerable<string> left && b is IEnumerable<string> right ? left.SequenceEqual(right, StringComparer.Ordinal) : a.Equals(b);
    }

    private static bool IsGroup(Type type)
    {
        Type real = Nullable.GetUnderlyingType(type) ?? type;
        return real.IsClass && real != typeof(string) && !typeof(IEnumerable).IsAssignableFrom(real);
    }

    private static string ToCamel(string name)
    {
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }
}
