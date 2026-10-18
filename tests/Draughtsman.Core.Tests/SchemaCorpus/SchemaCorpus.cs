using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Draughtsman.Core.Tests.SchemaCorpus;

/// <summary>
/// The differential corpus for <see cref="Draughtsman.Core.Serialization.DocumentSchema.Validate"/>: a deterministic set of JSON texts, built from
/// frozen seed documents, the documents this repo's own tests validate, directed probes and generated mutations, and the outcome the validator
/// that was in place before the schema library was removed gave for each (expectations.jsonl). README.md in this folder says how it was made
/// and what a change to it needs.
/// </summary>
internal static class SchemaCorpus
{
    public const string RecordVariable = "DRAUGHTSMAN_RECORD_SCHEMA_CORPUS";

    /// <summary>How many differing cases a failing run lists (default 12).</summary>
    public const string ShowVariable = "DRAUGHTSMAN_SCHEMA_CORPUS_SHOW";

    /// <summary>The data files copied next to the test binary (Draughtsman.Core.Tests.csproj).</summary>
    public static string ContentRoot { get; } = Path.Combine(AppContext.BaseDirectory, "schema-corpus");

    /// <summary>The same folder in the source tree, for re-recording.</summary>
    public static string SourceRoot { get; } = SourceDirectory();

    public static IReadOnlyList<(string Name, string Text)> Seeds()
    {
        string[] files = Directory.GetFiles(Path.Combine(ContentRoot, "seeds"), "*.json");
        Array.Sort(files, StringComparer.Ordinal);
        return [.. files.Select(f => (Path.GetFileNameWithoutExtension(f), File.ReadAllText(f).Replace("\r\n", "\n", StringComparison.Ordinal)))];
    }

    /// <summary>Every document the repo's own tests handed the validator (one JSON string literal per line, deduplicated by shape).</summary>
    public static IEnumerable<CorpusCase> Harvested()
    {
        string[] lines = File.ReadAllLines(Path.Combine(ContentRoot, "harvested.jsonl"));
        for (int i = 0; i < lines.Length; i++)
        {
            yield return new CorpusCase($"harvest:{i:D3}", JsonSerializer.Deserialize<string>(lines[i])!);
        }
    }

    public static IReadOnlyList<CorpusCase> Build()
    {
        IReadOnlyList<(string Name, string Text)> seeds = Seeds();
        var cases = new List<CorpusCase>();
        foreach ((string name, string text) in seeds)
        {
            cases.Add(new CorpusCase($"seed:{name}", text));
            cases.Add(new CorpusCase($"seed-compact:{name}", JsonNode.Parse(text)!.ToJsonString()));
        }

        cases.AddRange(Harvested());
        cases.AddRange(Handwritten.All());
        cases.AddRange(Mutations.Generate(seeds));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (CorpusCase item in cases)
        {
            if (!ids.Add(item.Id))
            {
                throw new InvalidOperationException($"Duplicate corpus id '{item.Id}'.");
            }
        }

        return cases;
    }

    public static string ExpectationsPath => Path.Combine(ContentRoot, "expectations.jsonl");

    public static Dictionary<string, (string Sha, CorpusOutcome Outcome)> LoadExpectations()
    {
        var result = new Dictionary<string, (string, CorpusOutcome)>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(ExpectationsPath))
        {
            (string id, string sha, CorpusOutcome outcome, _) = CorpusOutcome.FromLine(line);
            result[id] = (sha, outcome);
        }

        return result;
    }

    public static string DifferencesPath => Path.Combine(ContentRoot, "intentional-differences.jsonl");

    /// <summary>The cases the replacement deliberately answers differently, with what it answers and why (empty when there is no file).</summary>
    public static Dictionary<string, (string Sha, CorpusOutcome Outcome, string Why)> LoadDifferences()
    {
        var result = new Dictionary<string, (string, CorpusOutcome, string)>(StringComparer.Ordinal);
        if (!File.Exists(DifferencesPath))
        {
            return result;
        }

        foreach (string line in File.ReadLines(DifferencesPath))
        {
            (string id, string sha, CorpusOutcome outcome, string? why) = CorpusOutcome.FromLine(line);
            result[id] = (sha, outcome, why ?? throw new InvalidOperationException($"{id} has no reason."));
        }

        return result;
    }

    private static string SourceDirectory([CallerFilePath] string path = "")
    {
        return Path.GetDirectoryName(path)!;
    }
}
