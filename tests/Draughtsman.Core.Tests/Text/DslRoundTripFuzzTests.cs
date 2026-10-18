using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests.Text;

/// <summary>
/// The pitch rests on parse(print(doc)) == doc. Generates seeded random documents and checks the round trip; on the
/// first failure it shrinks the document and fails with the smallest offending DSL text.
/// Set DRAUGHTSMAN_FUZZ_ITERATIONS to run more than the default 500.
/// </summary>
public class DslRoundTripFuzzTests
{
    private const int BaseSeed = 20260921;

    private const int DefaultIterations = 500;

    [Fact]
    public void Random_documents_survive_parse_print_round_trip()
    {
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("DRAUGHTSMAN_FUZZ_ITERATIONS"), out int requested) && requested > 0
            ? requested
            : DefaultIterations;

        for (int i = 0; i < iterations; i++)
        {
            int seed = BaseSeed + i;
            Document document = new RandomDocumentGenerator(seed).Next();
            if (RoundTripFailure(document) is null)
            {
                continue;
            }

            Document minimal = DocumentShrinker.Shrink(document, d => RoundTripFailure(d) is not null);
            string failure = RoundTripFailure(minimal) ?? "(failure disappeared while shrinking)";
            Assert.Fail(
                $"Round trip failed for seed {seed} (iteration {i} of {iterations}).{Environment.NewLine}" +
                $"Minimal offending DSL:{Environment.NewLine}{TryPrint(minimal)}{Environment.NewLine}{failure}");
        }
    }

    [Fact]
    public void Generator_is_deterministic_for_a_seed()
    {
        Document first = new RandomDocumentGenerator(BaseSeed).Next();
        Document second = new RandomDocumentGenerator(BaseSeed).Next();

        DocumentAssert.Equivalent(first, second, "The same seed produced different documents.");
    }

    [Fact]
    public void Generator_covers_the_interesting_cases()
    {
        List<Document> documents = Enumerable.Range(0, 200).Select(i => new RandomDocumentGenerator(BaseSeed + i).Next()).ToList();
        List<Node> nodes = documents.SelectMany(d => d.Nodes).ToList();
        List<Edge> edges = documents.SelectMany(d => d.Edges).ToList();

        Assert.Contains(nodes, n => n.Geometry is null);
        Assert.Contains(nodes, n => n.Geometry?.Rotation is not null);
        Assert.Contains(nodes, n => n.Kind == "container" && nodes.Any(c => c.Parent == n.Id));
        Assert.Contains(nodes, n => n.Ports is { Count: > 0 });
        Assert.Contains(nodes, n => n.Props is not null && n.Props.ContainsKey("style.fill"));
        Assert.Contains(nodes, n => n.Id.Contains('.', StringComparison.Ordinal));
        Assert.Contains(nodes, n => n.Label.Contains('"', StringComparison.Ordinal));
        Assert.Contains(edges, e => e.From.Port is not null);
        Assert.Contains(edges, e => e.Routing?.Waypoints is { Count: > 0 });
        Assert.Contains(documents, d => d.Theme is not null);
        Assert.Contains(documents, d => d.Meta.AiProvenance is not null);
        Assert.Contains(documents, d => d.Flythrough is null);
        Assert.Contains(documents, d => d.Flythrough is { Loop: true });
        Assert.Contains(documents, d => d.Flythrough is { Loop: false });
        Assert.Contains(documents, d => d.Flythrough is { Keyframes.Count: > 1 });
        List<Keyframe> keyframes = documents.SelectMany(d => d.Flythrough?.Keyframes ?? []).ToList();
        Assert.Contains(keyframes, k => k.Caption is not null);
        Assert.Contains(keyframes, k => k.DurationMs is not null && k.HoldMs is not null);
        Assert.Contains(keyframes, k => k.Easing is null);
        foreach (KeyframeEasing easing in Enum.GetValues<KeyframeEasing>())
        {
            Assert.Contains(keyframes, k => k.Easing == easing);
        }
    }

    private static string? RoundTripFailure(Document document)
    {
        string printed;
        try
        {
            printed = Dsl.Print(document);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"Print threw: {ex.Message}";
        }

        ParseResult result;
        try
        {
            result = Dsl.Parse(printed);
        }
        catch (DslParseException ex)
        {
            return $"Parse threw: {ex.Message}";
        }

        if (!DocumentAssert.AreEquivalent(document, result.Document, out string difference))
        {
            return $"Parsed document differs from the original.{Environment.NewLine}{difference}";
        }

        string reprinted = Dsl.Print(result.Document);
        if (!string.Equals(printed, reprinted, StringComparison.Ordinal))
        {
            return $"Printing the parsed document gave different text.{Environment.NewLine}--- second print ---{Environment.NewLine}{reprinted}";
        }

        return null;
    }

    private static string TryPrint(Document document)
    {
        try
        {
            return Dsl.Print(document);
        }
        catch (ArgumentException ex)
        {
            return $"(could not print: {ex.Message})";
        }
    }
}
