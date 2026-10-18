using System.Text;
using System.Text.Json;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Diffing;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Server.Cli;

/// <summary>
/// <c>draughtsman diff</c> and <c>draughtsman check</c>: the two verbs that work on diagram files kept in a git
/// repository (docs/git-sync.md). They read the DSL text files they are given and nothing else: no data folder, no
/// configuration, no database, no network, no git, and never the web host, so they are safe to run on a CI runner that
/// has only the binary. Exit codes: 0 nothing to report, 1 a change (diff) or an error-severity finding (check), 2 a file
/// that cannot be read or parsed, or a bad command line.
/// </summary>
public static class DiagramFileCommands
{
    public const string DiffUsage =
        "Usage: draughtsman diff <before.dsl> <after.dsl> [--format markdown|json] [--ignore-layout]\n" +
        "  Compares two diagrams in the DSL and says what changed in sentences that name things by label, not id.\n" +
        "  --format        markdown (default) for a pull request comment or job summary, or json for a script\n" +
        "  --ignore-layout collapse position, size, waypoint and port changes into one line ('12 layout-only changes')\n" +
        "  The saved time, author and AI provenance lines are listed apart and never count as a change.\n" +
        "  Exit 0: no semantic change. 1: the diagram changed. 2: a file could not be read or parsed.";

    public const string CheckUsage =
        "Usage: draughtsman check <file.dsl> [--format text|json]\n" +
        "  Parses a diagram in the DSL and reports its parse warnings (unknown kind, a reference to a node that is not there)\n" +
        "  and the deterministic critique findings (the rules docs/api.md lists for critique_document).\n" +
        "  Exit 0: no error-severity finding (warnings are reported). 1: at least one error. 2: the file could not be read or parsed.";

    public static async Task<int> DiffAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var files = new List<string>();
        string format = "markdown";
        bool ignoreLayout = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    await stdout.WriteLineAsync(DiffUsage);
                    return 0;
                case "--ignore-layout":
                    ignoreLayout = true;
                    break;
                case "--format" when i + 1 < args.Length:
                    format = args[++i];
                    break;
                case var option when option.StartsWith("--format=", StringComparison.Ordinal):
                    format = option["--format=".Length..];
                    break;
                case var option when option.StartsWith("--", StringComparison.Ordinal):
                    await stderr.WriteLineAsync($"Unexpected option '{arg}'.\n{DiffUsage}");
                    return 2;
                default:
                    files.Add(arg);
                    break;
            }
        }

        if (files.Count != 2 || format is not ("markdown" or "json"))
        {
            await stderr.WriteLineAsync(files.Count != 2 ? $"diff needs exactly two files, a before and an after.\n{DiffUsage}" : $"Unknown --format '{format}': use markdown or json.\n{DiffUsage}");
            return 2;
        }

        Document? before = await ReadAsync(files[0], stderr, cancellationToken);
        Document? after = before is null ? null : await ReadAsync(files[1], stderr, cancellationToken);
        if (before is null || after is null)
        {
            return 2;
        }

        // The parser refuses a repeated id, so both documents can be matched by id.
        SemanticDiffReport report = SemanticDiff.Describe(before, after, ignoreLayout);
        await stdout.WriteAsync(format == "json" ? SemanticDiff.ToJson(report) : SemanticDiff.ToMarkdown(report));
        return report.HasChanges ? 1 : 0;
    }

    public static async Task<int> CheckAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var files = new List<string>();
        string format = "text";
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-h" or "--help":
                    await stdout.WriteLineAsync(CheckUsage);
                    return 0;
                case "--format" when i + 1 < args.Length:
                    format = args[++i];
                    break;
                case var option when option.StartsWith("--format=", StringComparison.Ordinal):
                    format = option["--format=".Length..];
                    break;
                case var option when option.StartsWith("--", StringComparison.Ordinal):
                    await stderr.WriteLineAsync($"Unexpected option '{arg}'.\n{CheckUsage}");
                    return 2;
                default:
                    files.Add(arg);
                    break;
            }
        }

        if (files.Count != 1 || format is not ("text" or "json"))
        {
            await stderr.WriteLineAsync(files.Count != 1 ? $"check needs exactly one file.\n{CheckUsage}" : $"Unknown --format '{format}': use text or json.\n{CheckUsage}");
            return 2;
        }

        string path = files[0];
        ParseResult? parsed = await ParseAsync(path, stderr, cancellationToken);
        if (parsed is null)
        {
            return 2;
        }

        IReadOnlyList<CritiqueFinding> findings = CritiqueAnalyser.Analyse(parsed.Document);
        int errors = findings.Count(f => f.Severity == FindingSeverity.Error);
        int warnings = findings.Count - errors + parsed.Warnings.Count;

        await stdout.WriteAsync(format == "json" ? CheckJson(path, parsed, findings, errors, warnings) : CheckText(path, parsed, findings, errors, warnings));
        return errors > 0 ? 1 : 0;
    }

    private static string CheckText(string path, ParseResult parsed, IReadOnlyList<CritiqueFinding> findings, int errors, int warnings)
    {
        var text = new StringBuilder();
        foreach (DslWarning warning in parsed.Warnings)
        {
            text.Append($"{path}:{warning.Line}:{warning.Column}: warning: {warning.Message}\n");
        }

        foreach (CritiqueFinding finding in findings)
        {
            string evidence = Evidence(finding);
            text.Append($"{path}: {(finding.Severity == FindingSeverity.Error ? "error" : "warning")} [{finding.Rule}]: {finding.Message}{(evidence.Length > 0 ? $" ({evidence})" : string.Empty)}\n");
        }

        text.Append(errors == 0 && warnings == 0
            ? $"{path}: no problems found\n"
            : $"{path}: {Plural(errors, "error")}, {Plural(warnings, "warning")}\n");
        return text.ToString();
    }

    private static string Evidence(CritiqueFinding finding)
    {
        var parts = new List<string>();
        if (finding.NodeIds.Count > 0)
        {
            parts.Add($"nodes {string.Join(", ", finding.NodeIds)}");
        }

        if (finding.EdgeIds.Count > 0)
        {
            parts.Add($"edges {string.Join(", ", finding.EdgeIds)}");
        }

        return string.Join("; ", parts);
    }

    private static string Plural(int count, string noun)
    {
        return count == 1 ? $"1 {noun}" : $"{count} {noun}s";
    }

    private static string CheckJson(string path, ParseResult parsed, IReadOnlyList<CritiqueFinding> findings, int errors, int warnings)
    {
        var report = new CheckReport(
            path,
            errors,
            warnings,
            [.. parsed.Warnings.Select(w => new CheckParseWarning(w.Line, w.Column, w.Message))],
            findings);
        return JsonSerializer.Serialize(report, DocumentJson.Options) + "\n";
    }

    private sealed record CheckParseWarning(int Line, int Column, string Message);

    private sealed record CheckReport(string File, int Errors, int Warnings, IReadOnlyList<CheckParseWarning> ParseWarnings, IReadOnlyList<CritiqueFinding> Findings);

    private static async Task<Document?> ReadAsync(string path, TextWriter stderr, CancellationToken cancellationToken)
    {
        return (await ParseAsync(path, stderr, cancellationToken))?.Document;
    }

    private static async Task<ParseResult?> ParseAsync(string path, TextWriter stderr, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync($"draughtsman: cannot read '{path}': {ex.Message}");
            return null;
        }

        try
        {
            return Dsl.Parse(text);
        }
        catch (DslParseException ex)
        {
            await stderr.WriteLineAsync($"{path}:{ex.Line}:{ex.Column}: error: {ex.Detail}");
            return null;
        }
    }
}
