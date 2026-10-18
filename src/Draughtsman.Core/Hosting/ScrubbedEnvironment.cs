using System.Collections;

namespace Draughtsman.Core.Hosting;

/// <summary>
/// The environment a child process of the server is given (AU-10): a short allow-list of what a program needs to find its
/// own files and locale, never the server's whole environment. A child inherits everything by default, and this server's
/// environment holds the AI provider key (<c>DRAUGHTSMAN_AI_KEY</c>) and the key-ring encryption key
/// (<c>DRAUGHTSMAN_KEY_ENCRYPTION_KEY</c>, or whatever names they were configured under): secrets that neither the Node
/// sidecar nor a headless Chromium rendering a document has any use for, and that a flaw in either would hand to whoever
/// supplied the document. Allow-listed, not block-listed, so a secret with a name nobody thought of stays out too.
/// </summary>
public static class ScrubbedEnvironment
{
    /// <summary>Names passed through exactly (compared without regard to case, since Windows does not distinguish it).</summary>
    private static readonly HashSet<string> s_names = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "HOME", "USER", "LOGNAME", "LANG", "LANGUAGE", "TZ", "TMPDIR", "TEMP", "TMP",
        "XDG_RUNTIME_DIR", "XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "DISPLAY",
        "SystemRoot", "SystemDrive", "WINDIR", "COMSPEC", "PATHEXT", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE",
    };

    /// <summary>Prefixes passed through: locale settings, and Node's own (<c>NODE_OPTIONS</c> is how an operator caps the sidecar's memory).</summary>
    private static readonly string[] s_prefixes = ["LC_", "NODE_"];

    /// <summary>The allow-listed entries of <paramref name="source"/>, and <paramref name="additions"/> on top.</summary>
    public static Dictionary<string, string> From(IDictionary source, IEnumerable<KeyValuePair<string, string>>? additions = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scrubbed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key is string name && entry.Value is string value && IsAllowed(name))
            {
                scrubbed[name] = value;
            }
        }

        if (additions is not null)
        {
            foreach (KeyValuePair<string, string> addition in additions)
            {
                scrubbed[addition.Key] = addition.Value;
            }
        }

        return scrubbed;
    }

    /// <summary>The current process's environment, scrubbed.</summary>
    public static Dictionary<string, string> FromCurrentProcess(IEnumerable<KeyValuePair<string, string>>? additions = null)
    {
        return From(Environment.GetEnvironmentVariables(), additions);
    }

    public static bool IsAllowed(string name)
    {
        return s_names.Contains(name) || s_prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
