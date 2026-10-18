using System.Text.Json;
using Draughtsman.Core.Serialization;
using Xunit.Abstractions;

namespace Draughtsman.Core.Tests;

/// <summary>
/// The official JSON-Schema-Test-Suite cases for every keyword <see cref="SchemaValidator"/> implements (JsonSchemaTestSuite/README.md), run as data: for
/// each group the suite's schema is compiled, and a group whose schema needs something the validator deliberately lacks is skipped by name. Every other
/// case must give the suite's verdict, except the few listed in <see cref="KeptAsTheLibraryHadIt"/>.
/// </summary>
public sealed class JsonSchemaTestSuiteTests(ITestOutputHelper output)
{
    /// <summary>Cases where SchemaValidator keeps the behaviour of the schema library it replaced (docs/adr/0004-document-schema-validator.md), not the suite's verdict.</summary>
    private static readonly Dictionary<string, string> KeptAsTheLibraryHadIt = BuildKept();

    [Fact]
    public void Every_group_the_validator_can_compile_gives_the_suites_verdicts()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "json-schema-test-suite", "draft2020-12");
        var failures = new List<string>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int groups = 0;
        int cases = 0;

        foreach (string file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string name = Path.GetRelativePath(root, file).Replace('\\', '/');
            using JsonDocument suite = JsonDocument.Parse(File.ReadAllText(file));
            foreach (JsonElement group in suite.RootElement.EnumerateArray())
            {
                string groupName = group.GetProperty("description").GetString()!;
                SchemaValidator validator;
                try
                {
                    validator = SchemaValidator.Compile(group.GetProperty("schema").GetRawText());
                }
                catch (Exception error) when (error is NotSupportedException or InvalidOperationException or ArgumentException)
                {
                    skipped.Add($"{name}: {groupName} ({error.GetType().Name}: {Brief(error.Message)})");
                    continue;
                }

                groups++;
                foreach (JsonElement test in group.GetProperty("tests").EnumerateArray())
                {
                    cases++;
                    string testName = test.GetProperty("description").GetString()!;
                    string key = $"{name}|{groupName}|{testName}";
                    bool expected = test.GetProperty("valid").GetBoolean();
                    bool actual = validator.Validate(test.GetProperty("data").GetRawText()).Count == 0;
                    if (actual == expected)
                    {
                        continue;
                    }

                    if (KeptAsTheLibraryHadIt.ContainsKey(key))
                    {
                        seen.Add(key);
                    }
                    else
                    {
                        failures.Add($"{key}: the suite says {(expected ? "valid" : "invalid")}");
                    }
                }
            }
        }

        output.WriteLine($"{cases} cases in {groups} groups run; {skipped.Count} groups skipped:");
        foreach (string line in skipped)
        {
            output.WriteLine("  skipped " + line);
        }

        failures.AddRange(KeptAsTheLibraryHadIt.Keys.Where(k => !seen.Contains(k)).Select(k => $"{k}: listed as kept, but it now agrees with the suite"));
        Assert.True(failures.Count == 0, $"{failures.Count} cases differ from the suite:\n" + string.Join('\n', failures.Take(25)));
        Assert.True(cases >= 380, $"only {cases} suite cases ran: the groups that compile have shrunk");
    }

    private static Dictionary<string, string> BuildKept()
    {
        const string Whitespace = "`\\s` and `\\S` are the .NET engine's, which reads them as ASCII whitespace under RegexOptions.ECMAScript, exactly as the library did; no pattern in the schema uses them";
        const string Surrogates = "patterns are not read in Unicode mode, so a quantifier applies to the last UTF-16 unit of a surrogate pair, exactly as with the library; no pattern in the schema has one";
        var kept = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["optional/format/date-time.json|validation of date-time strings|a trailing newline is invalid"] =
                "the library accepted one final line feed (a regular expression's $), so `2026-01-01T00:00:00Z\\n` is a valid timestamp today; kept (corpus: hand:datetime)",
            ["optional/non-bmp-regex.json|Proper UTF-16 surrogate pair handling: pattern|matches empty"] = Surrogates,
            ["optional/non-bmp-regex.json|Proper UTF-16 surrogate pair handling: pattern|matches two"] = Surrogates,
        };
        foreach (string test in new[] { "latin-1 non-breaking-space matches", "zero-width whitespace matches", "paragraph separator matches (line terminator)", "EM SPACE matches (Space_Separator)" })
        {
            kept["optional/ecmascript-regex.json|ECMA 262 \\s matches whitespace|" + test] = Whitespace;
        }

        foreach (string test in new[] { "latin-1 non-breaking-space does not match", "zero-width whitespace does not match", "paragraph separator does not match (line terminator)", "EM SPACE does not match (Space_Separator)" })
        {
            kept["optional/ecmascript-regex.json|ECMA 262 \\S matches everything but whitespace|" + test] = Whitespace;
        }

        return kept;
    }

    private static string Brief(string message)
    {
        return message.Length <= 110 ? message : message[..110] + "...";
    }
}
