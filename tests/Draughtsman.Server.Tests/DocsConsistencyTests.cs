using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Draughtsman.Server.Cli;
using Draughtsman.Server.Configuration;
using YamlDotNet.RepresentationModel;

namespace Draughtsman.Server.Tests;

/// <summary>
/// The customer documentation is only useful while it is true. These tests hold the parts that can be checked by a machine:
/// every relative link and heading anchor in the docs resolves; every setting in docs/draughtsman.example.yaml is read by the
/// code (and every setting the code has is in the example, which ExampleConfigTests also pins); a setting that nothing reads is
/// flagged as such in the example; every DRAUGHTSMAN_* environment variable the docs name exists in the code, and every one the
/// server reads is documented; every command-line verb is documented, and no documented verb is invented.
/// Each rule is checked in both directions, so a stale doc and a stale code path both fail here.
/// </summary>
public partial class DocsConsistencyTests
{
    /// <summary>Settings the example lists and the code keeps only so an old file still loads. Each must be flagged "Not read" in the example and must really have no reader.</summary>
    private static readonly string[] s_unreadKeys = ["auth.mode", "theme.diagram"];

    /// <summary>Source patterns that would mean an "unread" key is read after all (so the flag in the example is wrong).</summary>
    private static readonly string[] s_readersOfUnreadKeys = [@"\.Auth\.Mode\b", @"\.Theme\.Diagram\b"];

    /// <summary>Environment variables the docs use as shell variables in examples, not as settings the server reads.</summary>
    private static readonly string[] s_exampleOnlyVariables = ["DRAUGHTSMAN_TOKEN", "DRAUGHTSMAN_TEST_POSTGRES", "DRAUGHTSMAN_FUZZ_ITERATIONS", "DRAUGHTSMAN_BUILD_DATE"];

    private static readonly string[] s_documentedVerbs = ["serve", "mcp", "reset-admin", "first-run-code", "backup", "restore", "config", "doctor", "support-bundle", "diff", "check", "service", "--version", "--help"];

    private static string Root()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Draughtsman.sln")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new FileNotFoundException("Draughtsman.sln not found above the test binaries.");
    }

    /// <summary>The customer-facing pages: docs/ (the spec and the ADRs are frozen records and are not checked), the README and the root notes.</summary>
    private static IEnumerable<string> DocumentationFiles()
    {
        string root = Root();
        foreach (string name in new[] { "README.md", "SKILL.md", "CHANGELOG.md", "SECURITY.md" })
        {
            yield return Path.Combine(root, name);
        }

        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("docs/spec/", StringComparison.Ordinal) || relative.StartsWith("docs/adr/", StringComparison.Ordinal) || relative.StartsWith("docs/examples/", StringComparison.Ordinal))
            {
                continue;
            }

            yield return file;
        }
    }

    // ---- links ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_relative_link_and_anchor_in_the_documentation_resolves()
    {
        string root = Root();
        var broken = new List<string>();
        foreach (string file in DocumentationFiles())
        {
            string text = StripCode(File.ReadAllText(file));
            foreach (Link link in Links(file, text))
            {
                string? problem = Check(link);
                if (problem is not null)
                {
                    broken.Add($"{Path.GetRelativePath(root, file)}: ({link.Target}) {problem}");
                }
            }
        }

        Assert.True(broken.Count == 0, "Broken links:\n" + string.Join('\n', broken));
    }

    [Fact]
    public void The_link_checker_can_fail()
    {
        string root = Root();
        var file = Path.Combine(root, "docs", "index.md");

        Assert.NotNull(Check(new Link(file, "no-such-page.md")));
        Assert.NotNull(Check(new Link(file, "deployment.md#no-such-heading")));
        Assert.NotNull(Check(new Link(file, "#no-such-heading-on-this-page")));
        Assert.Null(Check(new Link(file, "deployment.md#the-short-version")));
        Assert.Null(Check(new Link(file, "https://example.com/not-followed")));
    }

    private sealed record Link(string File, string Target);

    private static IEnumerable<Link> Links(string file, string text)
    {
        foreach (Match match in MarkdownLink().Matches(text))
        {
            yield return new Link(file, match.Groups[1].Value);
        }
    }

    private static string? Check(Link link)
    {
        string target = link.Target;
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int hash = target.IndexOf('#', StringComparison.Ordinal);
        string pathPart = hash >= 0 ? target[..hash] : target;
        string anchor = hash >= 0 ? target[(hash + 1)..] : string.Empty;
        string resolved = pathPart.Length == 0 ? link.File : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(link.File)!, Uri.UnescapeDataString(pathPart)));
        if (!File.Exists(resolved) && !Directory.Exists(resolved))
        {
            return "does not exist";
        }

        if (anchor.Length > 0 && resolved.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !Anchors(resolved).Contains(anchor))
        {
            return "no such heading in " + Path.GetFileName(resolved);
        }

        return null;
    }

    private static HashSet<string> Anchors(string markdownFile)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        bool inFence = false;
        foreach (string line in File.ReadAllLines(markdownFile))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            Match heading = inFence ? Match.Empty : Heading().Match(line);
            if (!heading.Success)
            {
                continue;
            }

            string slug = Slug(heading.Groups[1].Value);
            if (seen.TryGetValue(slug, out int count))
            {
                seen[slug] = count + 1;
                slug = $"{slug}-{count + 1}";
            }
            else
            {
                seen[slug] = 0;
            }

            anchors.Add(slug);
        }

        return anchors;
    }

    /// <summary>GitHub's heading anchor: lower case, markup dropped, punctuation other than hyphen and underscore removed, spaces to hyphens.</summary>
    private static string Slug(string heading)
    {
        string text = Regex.Replace(heading, @"\[([^\]]*)\]\([^)]*\)", "$1");
        text = text.Replace("`", string.Empty, StringComparison.Ordinal).Replace("*", string.Empty, StringComparison.Ordinal);
        var slug = new StringBuilder();
        foreach (char c in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
            {
                slug.Append(c);
            }
            else if (c == ' ')
            {
                slug.Append('-');
            }
        }

        return slug.ToString();
    }

    private static string StripCode(string markdown)
    {
        var kept = new StringBuilder();
        bool inFence = false;
        foreach (string line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence)
            {
                kept.Append(InlineCode().Replace(line, string.Empty)).Append('\n');
            }
        }

        return kept.ToString();
    }

    // ---- settings -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_setting_in_the_example_is_read_by_the_code_or_flagged_as_unread()
    {
        string example = File.ReadAllText(Path.Combine(Root(), "docs", "draughtsman.example.yaml"));
        string sources = ServerSources();
        var problems = new List<string>();
        foreach (string key in ExampleKeys(example))
        {
            string property = char.ToUpperInvariant(key.Split('.')[^1][0]) + key.Split('.')[^1][1..];
            bool read = Regex.IsMatch(sources, @"\." + Regex.Escape(property) + @"\b");
            if (s_unreadKeys.Contains(key, StringComparer.Ordinal))
            {
                if (!CommentAbove(example, key).Contains("Not read", StringComparison.Ordinal))
                {
                    problems.Add($"{key} is unread but the example does not say so");
                }

                foreach (string pattern in s_readersOfUnreadKeys)
                {
                    if (Regex.IsMatch(sources, pattern))
                    {
                        problems.Add($"{key} is flagged as unread but the code now matches {pattern}: read it and drop the flag");
                    }
                }
            }
            else if (!read)
            {
                problems.Add($"{key} is in the example but no code reads .{property}");
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void Every_setting_the_code_has_is_in_the_example()
    {
        IReadOnlySet<string> inExample = ExampleKeys(File.ReadAllText(Path.Combine(Root(), "docs", "draughtsman.example.yaml"))).ToHashSet(StringComparer.Ordinal);
        IEnumerable<string> missing = ConfigKeys(typeof(DraughtsmanConfig), string.Empty).Where(k => !inExample.Contains(k));

        Assert.Empty(missing);
    }

    [Fact]
    public void The_settings_checker_can_fail()
    {
        // A key nothing reads, with no flag: must be reported. A key with a reader: must not.
        Assert.DoesNotMatch(@"\.NoSuchSettingAnywhere\b", "config.Server.Port");
        Assert.Matches(@"\.KnownProxies\b", ServerSources());
        Assert.Contains("Not read", CommentAbove(File.ReadAllText(Path.Combine(Root(), "docs", "draughtsman.example.yaml")), "auth.mode"), StringComparison.Ordinal);
        Assert.DoesNotContain("Not read", CommentAbove(File.ReadAllText(Path.Combine(Root(), "docs", "draughtsman.example.yaml")), "server.port"), StringComparison.Ordinal);
    }

    private static string ServerSources(bool includeConfig = false)
    {
        string root = Root();
        var all = new StringBuilder();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal) || (!includeConfig && relative.EndsWith("Configuration/DraughtsmanConfig.cs", StringComparison.Ordinal)))
            {
                continue;
            }

            all.AppendLine(File.ReadAllText(file));
        }

        return all.ToString();
    }

    private static IEnumerable<string> ExampleKeys(string yamlText)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(yamlText));
        return Flatten((YamlMappingNode)yaml.Documents[0].RootNode, string.Empty).ToList();
    }

    private static IEnumerable<string> Flatten(YamlMappingNode node, string prefix)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> entry in node.Children)
        {
            string path = prefix + ((YamlScalarNode)entry.Key).Value;
            if (entry.Value is YamlMappingNode child)
            {
                foreach (string key in Flatten(child, path + "."))
                {
                    yield return key;
                }
            }
            else
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> ConfigKeys(Type type, string prefix)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string path = prefix + char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            if (property.PropertyType.IsClass && property.PropertyType != typeof(string) && !property.PropertyType.IsGenericType)
            {
                foreach (string key in ConfigKeys(property.PropertyType, path + "."))
                {
                    yield return key;
                }
            }
            else
            {
                yield return path;
            }
        }
    }

    /// <summary>The comment lines immediately above a key in the example (found by walking the nesting to the key's own line).</summary>
    private static string CommentAbove(string yamlText, string dottedKey)
    {
        string[] lines = yamlText.Split('\n');
        string[] parts = dottedKey.Split('.');
        int depth = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith('#') || string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int indent = line.Length - line.TrimStart().Length;
            if (indent / 2 == depth && line.TrimStart().StartsWith(parts[depth] + ":", StringComparison.Ordinal))
            {
                if (depth == parts.Length - 1)
                {
                    var comment = new StringBuilder();
                    for (int j = i - 1; j >= 0 && lines[j].TrimStart().StartsWith('#'); j--)
                    {
                        comment.Insert(0, lines[j] + "\n");
                    }

                    return comment.ToString();
                }

                depth++;
            }
        }

        return string.Empty;
    }

    // ---- environment variables ---------------------------------------------------------------------------------------

    [Fact]
    public void Every_environment_variable_the_docs_name_exists_and_every_one_the_server_reads_is_documented()
    {
        string root = Root();
        string sources = ServerSources(includeConfig: true) + string.Join('\n', new[] { "scripts", "docker" }.SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*", SearchOption.AllDirectories)).Where(f => !f.Contains("node_modules", StringComparison.Ordinal)).Select(File.ReadAllText))
            + File.ReadAllText(Path.Combine(root, "Dockerfile"));
        string docs = string.Join('\n', DocumentationFiles().Select(File.ReadAllText)) + File.ReadAllText(Path.Combine(root, "docs", "draughtsman.example.yaml"));

        HashSet<string> named = DraughtsmanVariable().Matches(docs).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        string[] invented = [.. named.Where(v => !s_exampleOnlyVariables.Contains(v, StringComparer.Ordinal) && !sources.Contains(v, StringComparison.Ordinal))];
        HashSet<string> read = DraughtsmanVariable().Matches(ServerSources(includeConfig: true)).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        string[] undocumented = [.. read.Where(v => !named.Contains(v))];

        Assert.True(invented.Length == 0, "Documented but nothing reads them: " + string.Join(", ", invented));
        Assert.True(undocumented.Length == 0, "Read by the server but not documented: " + string.Join(", ", undocumented));
    }

    // ---- commands -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_command_is_documented_and_the_help_text_lists_exactly_the_documented_verbs()
    {
        string docs = string.Join('\n', DocumentationFiles().Where(f => !f.EndsWith("CHANGELOG.md", StringComparison.Ordinal)).Select(File.ReadAllText));
        foreach (string verb in s_documentedVerbs)
        {
            Assert.Contains(verb, docs, StringComparison.Ordinal);
            Assert.Contains(verb, ServerCli.HelpText, StringComparison.Ordinal);
        }

        // Verbs in the help text (the first word after "draughtsman " at the start of a usage line) must all be known to this test, so a new verb forces a doc check.
        string[] helpVerbs = [.. Regex.Matches(ServerCli.HelpText, @"^\s{2}draughtsman (\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct()];
        foreach (string verb in helpVerbs)
        {
            string name = verb.TrimStart('[').TrimEnd(']');
            Assert.Contains(name, s_documentedVerbs.Append("--version").Append("--help"), StringComparer.Ordinal);
        }
    }

    [Fact]
    public void No_documented_command_is_invented()
    {
        string[] known = [.. s_documentedVerbs.Where(v => !v.StartsWith('-'))];
        var invented = new List<string>();
        foreach (string file in DocumentationFiles().Where(f => !f.EndsWith("CHANGELOG.md", StringComparison.Ordinal)))
        {
            foreach (string line in File.ReadAllLines(file))
            {
                if (!line.Contains("draughtsman", StringComparison.Ordinal) || line.Contains("docker", StringComparison.Ordinal) || line.Contains("claude", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in CommandUse().Matches(line))
                {
                    string verb = match.Groups[1].Value;
                    if (!known.Contains(verb, StringComparer.Ordinal))
                    {
                        invented.Add($"{Path.GetFileName(file)}: draughtsman {verb}");
                    }
                }
            }
        }

        Assert.True(invented.Count == 0, "Commands in the docs that do not exist:\n" + string.Join('\n', invented.Distinct()));
    }

    [GeneratedRegex(@"\]\(([^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"^#{1,6}\s+(.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"DRAUGHTSMAN_[A-Z0-9_]*[A-Z0-9]")]
    private static partial Regex DraughtsmanVariable();

    /// <summary>A command as the docs write it: the program name as a word on a shell-looking line, then a lower-case word. Prose ("Draughtsman is ...") is capitalised and so is not matched.</summary>
    [GeneratedRegex(@"(?:^|[\s/'""$])draughtsman(?:\.exe)?\s+(?!draughtsman\b|is\b|the\b|to\b|and\b|or\b|in\b|on\b|uses\b|does\b|will\b|has\b|can\b|for\b|with\b)([a-z][a-z-]+)(?=\s|$|`)")]
    private static partial Regex CommandUse();
}
