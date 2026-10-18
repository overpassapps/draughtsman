using System.Globalization;
using System.Reflection;
using YamlDotNet.RepresentationModel;

namespace Draughtsman.Server.Configuration;

public enum ConfigWarningKind
{
    /// <summary>A key this build does not read (a typo, or a setting from a newer build): ignored.</summary>
    Unknown,

    /// <summary>A key an earlier build used under another name: carried over to the new name.</summary>
    Renamed,

    /// <summary>A key an earlier build read that no build reads any more: ignored.</summary>
    Removed,
}

/// <summary>One thing wrong or out of date in draughtsman.yaml. Never fatal: the server starts and the line goes in the log.</summary>
public sealed record ConfigWarning(string Key, ConfigWarningKind Kind, string Message);

/// <summary>A setting an earlier build read as <paramref name="Old"/> and this one reads as <paramref name="New"/> (dotted paths, camelCase).</summary>
public sealed record ConfigRename(string Old, string New, string Since);

/// <summary>A setting that no build reads any more, and what to do about it.</summary>
public sealed record ConfigRemoval(string Key, string Since, string Advice);

/// <summary>
/// What an old draughtsman.yaml needs to keep working on this build (docs/upgrade-backup.md, "Settings"). An upgrade never
/// rewrites the file and never refuses it for its keys: a key this build renamed is read under its new name, a key it no longer
/// reads is ignored, an unknown key is ignored, and every one of those is reported as a warning naming what to do
/// (<c>draughtsman config check</c> before an upgrade, the server log after). A key is renamed or removed only by adding it to
/// <see cref="Renames"/> or <see cref="Removals"/> in the same change, which the compatibility tests pin.
/// </summary>
public static class ConfigCompatibility
{
    /// <summary>Settings this build reads under a new name. Empty: no key has been renamed since the first pushed build.</summary>
    public static IReadOnlyList<ConfigRename> Renames { get; } = [];

    /// <summary>Settings no build reads any more. Only <c>licence.path</c>: builds before the open-source release read a licence file from it.</summary>
    public static IReadOnlyList<ConfigRemoval> Removals { get; } =
    [
        new ConfigRemoval("licence.path", "the open-source release", "Delete it from draughtsman.yaml; nothing reads that file now, and it is safe to leave in the data folder."),
    ];

    /// <summary>
    /// Applies the renames to <paramref name="yaml"/> (so the deserialiser sees the new names) and reports renames, removals and
    /// unknown keys. The text is returned untouched when nothing had to move, so no formatting is ever rewritten.
    /// </summary>
    /// <exception cref="YamlDotNet.Core.YamlException">The text is not valid YAML: the one thing that is not softened.</exception>
    public static (string Yaml, IReadOnlyList<ConfigWarning> Warnings) Apply(string yaml, IReadOnlyList<ConfigRename>? renames = null, IReadOnlyList<ConfigRemoval>? removals = null, Type? root = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        renames ??= Renames;
        removals ??= Removals;
        root ??= typeof(DraughtsmanConfig);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return (yaml, []);
        }

        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode top)
        {
            return (yaml, []);
        }

        var warnings = new List<ConfigWarning>();
        bool changed = false;
        foreach (ConfigRename rename in renames)
        {
            if (!TryTake(top, rename.Old.Split('.'), out YamlNode? value))
            {
                continue;
            }

            changed = true;
            if (Exists(top, rename.New.Split('.')))
            {
                warnings.Add(new ConfigWarning(rename.Old, ConfigWarningKind.Renamed, $"'{rename.Old}' was renamed '{rename.New}' in {rename.Since}; both are set, so '{rename.New}' is used and '{rename.Old}' is ignored. Delete '{rename.Old}'."));
            }
            else
            {
                Put(top, rename.New.Split('.'), value!);
                warnings.Add(new ConfigWarning(rename.Old, ConfigWarningKind.Renamed, $"'{rename.Old}' was renamed '{rename.New}' in {rename.Since}; the old name is still read this time. Rename it in draughtsman.yaml."));
            }
        }

        foreach (ConfigRemoval removal in removals)
        {
            if (TryTake(top, removal.Key.Split('.'), out _))
            {
                changed = true;
                warnings.Add(new ConfigWarning(removal.Key, ConfigWarningKind.Removed, $"'{removal.Key}' was removed in {removal.Since} and is ignored. {removal.Advice}"));
            }
        }

        ReportUnknown(top, root, string.Empty, warnings);

        if (!changed)
        {
            return (yaml, warnings);
        }

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        return (writer.ToString(), warnings);
    }

    private static void ReportUnknown(YamlMappingNode node, Type type, string path, List<ConfigWarning> warnings)
    {
        Dictionary<string, PropertyInfo> known = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .ToDictionary(p => ToCamel(p.Name), p => p, StringComparer.Ordinal);
        foreach ((YamlNode keyNode, YamlNode value) in node.Children)
        {
            string key = (keyNode as YamlScalarNode)?.Value ?? string.Empty;
            string dotted = path.Length == 0 ? key : path + "." + key;
            if (!known.TryGetValue(key, out PropertyInfo? property))
            {
                string? suggestion = Suggest(key, known.Keys);
                warnings.Add(new ConfigWarning(
                    dotted,
                    ConfigWarningKind.Unknown,
                    $"'{dotted}' is not a setting this version reads, so it is ignored" + (suggestion is null ? "." : $" (did you mean '{(path.Length == 0 ? suggestion : path + "." + suggestion)}'?).")));
                continue;
            }

            if (value is YamlMappingNode child && IsSection(property.PropertyType))
            {
                ReportUnknown(child, property.PropertyType, dotted, warnings);
            }
        }
    }

    private static bool IsSection(Type type)
    {
        return type.IsClass && type != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    internal static string ToCamel(string name)
    {
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>A known key within two edits of <paramref name="key"/> (or equal ignoring case), so a typo is named, not just rejected.</summary>
    private static string? Suggest(string key, IEnumerable<string> known)
    {
        string? best = null;
        int bestDistance = 3;
        foreach (string candidate in known)
        {
            int distance = string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase) ? 0 : Distance(key.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private static int Distance(string a, string b)
    {
        int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int[] current = new int[b.Length + 1];
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[b.Length];
    }

    private static YamlNode? Find(YamlMappingNode node, string key)
    {
        foreach ((YamlNode k, YamlNode v) in node.Children)
        {
            if (k is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return v;
            }
        }

        return null;
    }

    private static bool Exists(YamlMappingNode top, string[] path)
    {
        YamlNode? current = top;
        foreach (string segment in path)
        {
            if (current is not YamlMappingNode map || (current = Find(map, segment)) is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Removes the node at <paramref name="path"/> and returns it, and removes any section that is left empty, so a moved setting does not leave an empty (and so "unknown") section behind.</summary>
    private static bool TryTake(YamlMappingNode top, string[] path, out YamlNode? value)
    {
        value = null;
        var chain = new List<(YamlMappingNode Parent, YamlNode Key)>();
        YamlMappingNode parent = top;
        for (int i = 0; i < path.Length; i++)
        {
            YamlNode? key = parent.Children.Keys.FirstOrDefault(k => k is YamlScalarNode scalar && string.Equals(scalar.Value, path[i], StringComparison.Ordinal));
            if (key is null)
            {
                return false;
            }

            chain.Add((parent, key));
            if (i < path.Length - 1)
            {
                if (parent.Children[key] is not YamlMappingNode next)
                {
                    return false;
                }

                parent = next;
            }
        }

        (YamlMappingNode owner, YamlNode last) = chain[^1];
        value = owner.Children[last];
        owner.Children.Remove(last);
        for (int i = chain.Count - 2; i >= 0 && chain[i + 1].Parent.Children.Count == 0; i--)
        {
            chain[i].Parent.Children.Remove(chain[i].Key);
        }

        return true;
    }

    private static void Put(YamlMappingNode top, string[] path, YamlNode value)
    {
        YamlMappingNode parent = top;
        for (int i = 0; i < path.Length - 1; i++)
        {
            if (Find(parent, path[i]) is YamlMappingNode next)
            {
                parent = next;
            }
            else
            {
                var created = new YamlMappingNode();
                parent.Add(path[i], created);
                parent = created;
            }
        }

        parent.Add(path[^1], value);
    }
}
