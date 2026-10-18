using System.Text;
using System.Text.RegularExpressions;

namespace Draughtsman.Core.Tests.SchemaCorpus;

/// <summary>
/// The differential test: <see cref="Draughtsman.Core.Serialization.DocumentSchema.Validate"/> must give, for every text of the corpus, exactly the
/// outcome recorded from the validator it replaced (schema-corpus/expectations.jsonl): the same verdict, the same violation lines in the same
/// order, the same exception type for text that is not JSON. See SchemaCorpus/README.md.
/// </summary>
public sealed class SchemaCorpusTests
{
    [Fact]
    public void Every_case_has_the_outcome_that_was_recorded_for_it()
    {
        IReadOnlyList<CorpusCase> cases = SchemaCorpus.Build();
        string? record = Environment.GetEnvironmentVariable(SchemaCorpus.RecordVariable);
        if (record == "1")
        {
            Record(cases);
            return;
        }

        if (record == "differences")
        {
            RecordDifferences(cases);
            return;
        }

        Dictionary<string, (string Sha, CorpusOutcome Outcome)> expected = SchemaCorpus.LoadExpectations();
        Dictionary<string, (string Sha, CorpusOutcome Outcome, string Why)> intended = SchemaCorpus.LoadDifferences();
        var problems = new List<string>();
        var built = new HashSet<string>(StringComparer.Ordinal);
        var differing = new HashSet<string>(StringComparer.Ordinal);
        foreach (CorpusCase item in cases)
        {
            built.Add(item.Id);
            if (!expected.TryGetValue(item.Id, out (string Sha, CorpusOutcome Outcome) recorded))
            {
                problems.Add($"generated but never recorded: {item.Id}");
                continue;
            }

            if (recorded.Sha != item.Fingerprint)
            {
                problems.Add($"the text built for {item.Id} is not the one recorded (fingerprint {item.Fingerprint}, recorded {recorded.Sha}): the generator or a seed changed");
                continue;
            }

            CorpusOutcome now = CorpusOutcome.Observe(item.Json);
            if (now.SameAs(recorded.Outcome))
            {
                continue;
            }

            differing.Add(item.Id);
            string? why = IntentionalDifferences.Classify(recorded.Outcome, now);
            if (why is not null && intended.TryGetValue(item.Id, out (string Sha, CorpusOutcome Outcome, string Why) listed) && listed.Why == why && listed.Sha == item.Fingerprint && now.SameAs(listed.Outcome))
            {
                continue;
            }

            problems.Add($"{item.Id}\n    recorded: {Clip(recorded.Outcome.Describe())}\n    now:      {Clip(now.Describe())}\n    reason:   {why ?? "none of the intended differences"}{(why is not null ? ", but it is not listed as such" : string.Empty)}");
        }

        problems.AddRange(expected.Keys.Where(id => !built.Contains(id)).Select(id => $"recorded but no longer generated: {id}"));
        problems.AddRange(intended.Keys.Where(id => !differing.Contains(id)).Select(id => $"listed in intentional-differences.jsonl but it no longer differs: {id}"));
        Assert.True(problems.Count == 0, $"{problems.Count} of {cases.Count} corpus cases differ:\n" + string.Join('\n', problems.Take(int.TryParse(Environment.GetEnvironmentVariable(SchemaCorpus.ShowVariable), out int show) ? show : 12)));
    }

    [Fact]
    public void The_corpus_is_large_and_has_every_kind_of_outcome()
    {
        Dictionary<string, (string Sha, CorpusOutcome Outcome)> expected = SchemaCorpus.LoadExpectations();
        int valid = expected.Values.Count(e => e.Outcome.Kind == CorpusOutcome.Valid);
        int invalid = expected.Values.Count(e => e.Outcome.Kind == CorpusOutcome.Invalid);
        int throws = expected.Values.Count(e => e.Outcome.Kind == CorpusOutcome.Throws);

        Assert.True(expected.Count >= 10000, $"only {expected.Count} cases");
        Assert.True(valid >= 3500, $"only {valid} valid cases: a corpus of rejections cannot show a validator that rejects too much");
        Assert.True(invalid >= 6000, $"only {invalid} invalid cases");
        Assert.True(throws >= 100, $"only {throws} cases for text the validator cannot read");
    }

    [Fact]
    public void Validate_throws_for_text_that_is_not_json_and_for_nothing_else()
    {
        int notJson = 0;
        foreach (CorpusCase item in SchemaCorpus.Build())
        {
            CorpusOutcome outcome = CorpusOutcome.Observe(item.Json);
            if (outcome.Kind == CorpusOutcome.Throws)
            {
                notJson++;
                Assert.True(outcome.Exception == "JsonReaderException", $"{item.Id} made Validate throw {outcome.Exception}");
            }
        }

        Assert.True(notJson >= 60, $"only {notJson} corpus texts are not JSON");
    }

    [Fact]
    public void Every_keyword_the_schema_uses_is_exercised_by_violations_in_the_corpus()
    {
        string[] lines = [.. SchemaCorpus.LoadExpectations().Values.SelectMany(e => e.Outcome.Violations)];
        (string Keyword, Regex Message)[] expectations =
        [
            ("type", MessageOf(@"Value is ""\w+"" but should be ""(string|number|integer|object|array|boolean)""")),
            ("required", MessageOf(@"Required properties \[.+\] are not present")),
            ("properties", MessageOf(@"Some properties did not match the required schema: \[.+\]")),
            ("additionalProperties: false", MessageOf(@"All values fail against the false schema")),
            ("additionalProperties: false (applicator)", MessageOf(@"Some additional properties did not match the required schema: \[.+\]")),
            ("additionalProperties: schema (theme)", new Regex(@"^/theme/.*: Value is ""\w+"" but should be ""string""$", RegexOptions.None, RegexLimits.Timeout)),
            ("items", MessageOf(@"Some items do not match the required schema; failing indexes: \[[\d,]+\]")),
            ("enum", MessageOf(@"Value should match one of the values specified by the enum")),
            ("const", MessageOf(@"Expected ""1""")),
            ("pattern", MessageOf(@"The string value is not a match for the indicated regular expression")),
            ("minLength", MessageOf(@"Value should be at least \d+ characters")),
            ("maxLength", MessageOf(@"Value should be at most \d+ characters")),
            ("minItems", MessageOf(@"Value should have at least \d+ items")),
            ("maxItems", MessageOf(@"Value should have at most \d+ items")),
            ("minimum", MessageOf(@"\S+ should be at least \S+")),
            ("maximum", MessageOf(@"\S+ should be at most \S+")),
            ("exclusiveMinimum", MessageOf(@"\S+ should be greater than \S+")),
            ("format: date-time", MessageOf(@"Value does not match format ""date-time""")),
        ];

        foreach ((string keyword, Regex message) in expectations)
        {
            Assert.True(lines.Any(l => message.IsMatch(l)), $"no violation in the corpus comes from '{keyword}'");
        }
    }

    private static Regex MessageOf(string text)
    {
        return new Regex("^(\\(document\\)|/.*?): " + text + "$", RegexOptions.None, RegexLimits.Timeout);
    }

    private static void Record(IReadOnlyList<CorpusCase> cases)
    {
        string path = Path.Combine(SchemaCorpus.SourceRoot, "expectations.jsonl");
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        foreach (CorpusCase item in cases)
        {
            writer.WriteLine(CorpusOutcome.Observe(item.Json).ToLine(item));
        }
    }

    /// <summary>Writes intentional-differences.jsonl: every case whose outcome now differs from the recorded one, with the reason a rule gives; fails if a difference has none.</summary>
    private static void RecordDifferences(IReadOnlyList<CorpusCase> cases)
    {
        Dictionary<string, (string Sha, CorpusOutcome Outcome)> expected = SchemaCorpus.LoadExpectations();
        var unclassified = new List<string>();
        using (var writer = new StreamWriter(Path.Combine(SchemaCorpus.SourceRoot, "intentional-differences.jsonl"), false, new UTF8Encoding(false)) { NewLine = "\n" })
        {
            foreach (CorpusCase item in cases)
            {
                CorpusOutcome now = CorpusOutcome.Observe(item.Json);
                CorpusOutcome recorded = expected[item.Id].Outcome;
                if (now.SameAs(recorded))
                {
                    continue;
                }

                string? why = IntentionalDifferences.Classify(recorded, now);
                if (why is null)
                {
                    unclassified.Add($"{item.Id}: recorded {Clip(recorded.Describe())}; now {Clip(now.Describe())}");
                    continue;
                }

                writer.WriteLine(now.ToLine(item, why));
            }
        }

        Assert.True(unclassified.Count == 0, "differences no rule explains:\n" + string.Join('\n', unclassified.Take(12)));
    }

    private static string Clip(string text)
    {
        return text.Length <= 700 ? text : text[..700] + $"... ({text.Length} chars)";
    }
}
