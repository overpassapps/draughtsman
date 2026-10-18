using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Sql;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// AU-08: the Mermaid sequence importer's message, note and declaration expressions backtracked in time quadratic in a run
/// of spaces (measured before the fix: 32,000 spaces after <c>A-&gt;&gt;B</c> took 11.5 s in .NET, so the 1 MB the import
/// endpoint accepts would have pinned a thread for hours). These tests hold the fix from three sides: the non-backtracking
/// engine reads exactly what the backtracking one did, no importer is superlinear on whitespace placed anywhere in a
/// real source, and no regular expression in the assembly can run unbounded.
/// </summary>
public class ImporterComplexityTests
{
    private const int Amplification = 30_000;

    /// <summary>
    /// The slowest acceptable import of a source with one run of <see cref="Amplification"/> spaces in it; the linear ones take milliseconds.
    /// Stretched by <see cref="LoadScale"/> where it is used, so a host busy with other builds does not fail a linear import.
    /// </summary>
    private static readonly TimeSpan s_budget = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Tries per variant. A quadratic expression is slow every time (11.5 s measured), so it still fails all of them; a stall of the
    /// machine (the first Windows CI run saw 3 s and a 500 ms regex timeout on imports that take about 1 ms on a Mac, once each, in a
    /// process running other test classes in parallel) does not repeat, and is not what this test is about.
    /// </summary>
    private const int Attempts = 3;

    private const string SequenceSource = """
        sequenceDiagram
            title: Checkout
            participant A as Alice
            actor B
            autonumber
            A->>B: hello there
            A-->>B: dashed reply
            A-)B: async call
            A->>+B: activate
            B-->>-A: deactivate
            A-xB: lost
            Note over A,B: a note over both
            Note left of A: a note to the left
            loop Every minute
                A->>B: ping
            else Sometimes
                B->>A: pong
            end
            create participant C
            A->>C: new
            destroy C
            C-->>A: bye
        """;

    private const string ErSource = """
        erDiagram
            direction LR
            CUSTOMER ||--o{ ORDER : places
            ORDER ||--|{ LINE-ITEM : contains
            CUSTOMER["A customer"] {
                int id PK
                string name "full name"
                varchar(40) email UK
            }
            ORDER {
                int id PK, FK
            }
        """;

    private const string FlowSource = """
        flowchart LR
            A[Start] -->|go| B{Choice}
            B --> C((Circle)) & D[Other]
            subgraph one [First group]
                C --> E
            end
            style A fill:#f9f,stroke:#333
            classDef hot fill:#f00
            click A callback "tip"
        """;

    public static TheoryData<string> Sources()
    {
        var all = new TheoryData<string> { SequenceSource, ErSource, FlowSource, SqlFixtures.TSql, SqlFixtures.Postgres, SqlFixtures.MySql, SqlFixtures.Sqlite };
        foreach (MermaidCase corpus in MermaidCorpus.All)
        {
            all.Add(corpus.Source);
        }

        return all;
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task A_long_run_of_spaces_anywhere_in_a_real_source_never_makes_an_import_slow(string source)
    {
        // Once, unamplified, so static initialisation and JIT are paid before anything is timed.
        await Task.Run(() => Import(source));

        var slow = new List<string>();
        TimeSpan budget = LoadScale.Scaled(s_budget);
        foreach (string variant in Amplified(source))
        {
            (string? failure, TimeSpan elapsed) = await Fastest(variant, budget);
            if (failure is not null || elapsed > budget)
            {
                slow.Add($"{failure ?? "slow"} after {elapsed.TotalMilliseconds:F0} ms (best of {Attempts}): {Describe(source, variant)}");
                if (slow.Count == 3)
                {
                    break; // a regression shows on every variant, and each costs seconds
                }
            }
        }

        Assert.True(slow.Count == 0, string.Join(Environment.NewLine, slow.Take(5)));
    }

    [Fact]
    public async Task The_pathological_inputs_the_audit_named_finish_at_once()
    {
        string[] sources =
        [
            "sequenceDiagram\nA->>B" + new string(' ', 1_000_000) + "x",
            "sequenceDiagram\nA" + new string(' ', 1_000_000) + "B",
            "sequenceDiagram\nNote over A" + new string(' ', 1_000_000) + "x",
            "sequenceDiagram\nparticipant A" + new string(' ', 1_000_000) + "x",
        ];

        foreach (string source in sources)
        {
            var timer = Stopwatch.StartNew();
            await Task.Run(() => MermaidImporter.Import(source, new MermaidImportOptions { Type = DocumentType.Sequence })).WaitAsync(LoadScale.Scaled(TimeSpan.FromSeconds(20)), CancellationToken.None);
            Assert.True(timer.Elapsed < LoadScale.Scaled(TimeSpan.FromSeconds(3)), $"took {timer.ElapsedMilliseconds} ms");
        }
    }

    [Theory]
    [InlineData("MessageLinePattern", RegexOptions.None)]
    [InlineData("NoteLinePattern", RegexOptions.IgnoreCase)]
    [InlineData("DeclarationPattern", RegexOptions.IgnoreCase)]
    public void The_linear_expression_matches_and_captures_exactly_what_the_backtracking_one_did(string patternField, RegexOptions extra)
    {
        string pattern = (string)typeof(MermaidImporter).Assembly.GetType("Draughtsman.Core.Import.MermaidSequenceImporter")!
            .GetField(patternField, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        var backtracking = new Regex(pattern, RegexOptions.CultureInvariant | extra);
        Regex linear = RegexLimits.Linear(pattern, extra);
        string[] atoms = ["A", "B", " ", "  ", "-", ">", "->", "->>", "-->", "--x", "x", ":", "+", "-)", "<<-->>", "over ", "left of ", "right  of ", "Web-App", "\t", "a b", ")", " as ", "As"];
        var random = new Random(20260930);
        int matched = 0;
        for (int i = 0; i < 100_000; i++)
        {
            string line = string.Concat(Enumerable.Range(0, random.Next(1, 9)).Select(_ => atoms[random.Next(atoms.Length)])).Trim();
            Match expected = backtracking.Match(line);
            Match actual = linear.Match(line);
            Assert.True(expected.Success == actual.Success, $"[{line}] backtracking {expected.Success}, linear {actual.Success}");
            if (!expected.Success)
            {
                continue;
            }

            matched++;
            foreach (string group in backtracking.GetGroupNames())
            {
                Assert.True(expected.Groups[group].Success == actual.Groups[group].Success && expected.Groups[group].Value == actual.Groups[group].Value,
                    $"[{line}] group {group}: backtracking '{expected.Groups[group].Value}', linear '{actual.Groups[group].Value}'");
            }
        }

        Assert.True(matched > 500, $"the corpus should match often enough to mean something; only {matched} did");
    }

    [Fact]
    public void Every_generated_regex_in_the_core_assembly_has_a_match_timeout()
    {
        var unbounded = new List<string>();
        foreach (Type type in typeof(MermaidImporter).Assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                GeneratedRegexAttribute? attribute = method.GetCustomAttribute<GeneratedRegexAttribute>();
                if (attribute is not null && attribute.MatchTimeoutMilliseconds <= 0)
                {
                    unbounded.Add($"{type.FullName}.{method.Name}");
                }
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == typeof(Regex) && ((Regex)field.GetValue(null)!).MatchTimeout == Regex.InfiniteMatchTimeout)
                {
                    unbounded.Add($"{type.FullName}.{field.Name}");
                }
            }
        }

        Assert.True(unbounded.Count == 0, "Regular expressions with no match timeout: " + string.Join(", ", unbounded));
    }

    [Fact]
    public void A_regex_timeout_is_reported_as_a_refused_source_not_a_crash_or_a_partial_document()
    {
        // A pattern that does backtrack catastrophically, on an input that makes it, with a 1 ms budget.
        var runaway = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
        string input = new string('a', 60) + "!";

        SourceRejectedException ex = Assert.Throws<SourceRejectedException>(() => SourceRejectedException.Guard("Mermaid", () => runaway.IsMatch(input)));

        Assert.Contains("Mermaid", ex.Message, StringComparison.Ordinal);
        Assert.IsType<RegexMatchTimeoutException>(ex.InnerException);
        // And it does not hide other failures.
        Assert.Throws<InvalidOperationException>(() => SourceRejectedException.Guard<int>("Mermaid", () => throw new InvalidOperationException("other")));
    }

    /// <summary>The first clean, in-budget run of <paramref name="variant"/> or, if none of <see cref="Attempts"/> was, the quickest. Timed on the worker thread, so waiting for a free one is not counted.</summary>
    private static async Task<(string? Failure, TimeSpan Elapsed)> Fastest(string variant, TimeSpan budget)
    {
        (string? Failure, TimeSpan Elapsed) best = (null, TimeSpan.MaxValue);
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            (string? failure, TimeSpan elapsed) = await Task.Run(() =>
            {
                var timer = Stopwatch.StartNew();
                string? outcome = Import(variant);
                return (outcome, timer.Elapsed);
            });
            if (failure is null && elapsed <= budget)
            {
                return (null, elapsed);
            }

            if (elapsed < best.Elapsed)
            {
                best = (failure, elapsed);
            }
        }

        return best;
    }

    private static string? Import(string source)
    {
        try
        {
            if (source.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) || source.Contains("create table", StringComparison.OrdinalIgnoreCase) || source.Contains("SET ", StringComparison.Ordinal))
            {
                SqlImporter.Import(source);
            }
            else
            {
                DocumentType type = source.TrimStart().StartsWith("sequenceDiagram", StringComparison.Ordinal) ? DocumentType.Sequence
                    : source.TrimStart().StartsWith("erDiagram", StringComparison.Ordinal) ? DocumentType.Architecture
                    : DocumentType.Flowchart;
                MermaidImporter.Import(source, new MermaidImportOptions { Type = type });
            }

            return null;
        }
        catch (SourceRejectedException)
        {
            return "rejected";
        }
    }

    /// <summary>The source with one run of spaces standing in for a single space (sampled along the source), and with the run appended to sampled lines.</summary>
    private static IEnumerable<string> Amplified(string source)
    {
        var spaces = new List<int>();
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == ' ')
            {
                spaces.Add(i);
            }
        }

        string run = new(' ', Amplification);
        int step = Math.Max(1, spaces.Count / 30);
        for (int i = 0; i < spaces.Count; i += step)
        {
            yield return source[..spaces[i]] + run + source[(spaces[i] + 1)..];
        }

        string[] lines = source.Split('\n');
        int lineStep = Math.Max(1, lines.Length / 20);
        for (int i = 1; i < lines.Length; i += lineStep)
        {
            // Line 0 is skipped: padding the header line would make it no header, which is an honest wrong-type refusal.
            var copy = (string[])lines.Clone();
            copy[i] = copy[i].TrimEnd('\r') + run + "x";
            yield return string.Join('\n', copy);
        }
    }

    private static string Describe(string original, string variant)
    {
        int at = 0;
        while (at < original.Length && at < variant.Length && original[at] == variant[at])
        {
            at++;
        }

        int lineStart = original.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        int lineEnd = original.IndexOf('\n', at);
        return "line '" + original[lineStart..(lineEnd < 0 ? original.Length : lineEnd)].Trim() + "'";
    }
}
