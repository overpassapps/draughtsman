using System.Text.Json;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Tests.Text;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests;

/// <summary>
/// Milestone 2 of the <c>sequence</c> type (docs/sequence-diagrams.md): combined fragments, notes, create and destroy.
/// The example is docs/examples/checkout-fragments.{json,dsl} (one document, pinned byte for byte, as checkout-sequence
/// is); the critique rules <c>empty-fragment</c>, <c>non-contiguous-fragment</c> and <c>unknown-fragment</c> are each
/// proven both ways, firing on a minimal defect and staying silent on the nearest valid document. The drawing side is
/// web/src/model/sequenceScene.ts and is tested there.
/// </summary>
public class SequenceFragmentTests
{
    private static readonly string s_jsonPath = Path.Combine(AppContext.BaseDirectory, "contract", "checkout-fragments.json");
    private static readonly string s_dslPath = Path.Combine(AppContext.BaseDirectory, "contract", "checkout-fragments.dsl");

    private static Document Example() => DocumentJson.Deserialize(File.ReadAllText(s_jsonPath));

    [Fact]
    public void The_example_json_and_dsl_are_the_same_document_and_it_is_clean()
    {
        string dsl = File.ReadAllText(s_dslPath);

        ParseResult parsed = Dsl.Parse(dsl);

        Assert.Empty(parsed.Warnings);
        DocumentAssert.Equivalent(Example(), parsed.Document, "docs/examples/checkout-fragments.dsl does not parse to docs/examples/checkout-fragments.json.");
        Assert.Equal(dsl, Dsl.Print(parsed.Document));
        Assert.True(ContractFiles.Evaluate(File.ReadAllText(s_jsonPath)).IsValid);
        Assert.Empty(CritiqueAnalyser.Analyse(Example()));
    }

    [Fact]
    public void Membership_is_written_on_the_messages_and_the_fragment_carries_only_its_operator_and_guards()
    {
        Document document = Example();

        Node fragment = Assert.Single(document.Nodes, n => n.Kind == "fragment");
        Assert.Equal(["operands", "operator"], fragment.Props!.Keys.Order());
        Assert.Null(fragment.Geometry);
        Assert.Equal(
            [("m_3", "f_1", 0), ("m_4", "f_1", 0), ("m_5", "f_1", 1)],
            document.Edges.Where(e => e.Props is not null && e.Props.ContainsKey("in")).Select(e => (e.Id, e.Props!["in"].GetString()!, e.Props["operand"].GetInt32())));
        Assert.Equal(["note", "create", "destroy"], document.Edges.Where(e => e.Kind is "note" or "create" or "destroy").Select(e => e.Kind));
    }

    // ---- critique ----

    private static Document Sequence(IEnumerable<Node> extraNodes, params Edge[] edges)
    {
        Document example = Example();
        return example with { Nodes = [.. example.Nodes.Where(n => n.Kind != "fragment"), .. extraNodes], Edges = edges };
    }

    private static Node Fragment(string id, string @operator = "alt", string? parent = null) =>
        new(id, "fragment", @operator, "rectangle")
        {
            Parent = parent,
            Props = new Dictionary<string, JsonElement>
            {
                ["operator"] = JsonSerializer.SerializeToElement(@operator),
                ["operands"] = JsonSerializer.SerializeToElement(new[] { "[x]" }),
            },
        };

    private static Edge Message(string id, string? inside = null, string from = "p_web", string to = "p_api", string kind = "sync")
    {
        var edge = new Edge(id, kind, new Endpoint(from), new Endpoint(to));
        return inside is null ? edge : edge with { Props = new Dictionary<string, JsonElement> { ["in"] = JsonSerializer.SerializeToElement(inside), ["operand"] = JsonSerializer.SerializeToElement(0) } };
    }

    private static IReadOnlyList<CritiqueFinding> Findings(Document document, string rule) => [.. CritiqueAnalyser.Analyse(document).Where(f => f.Rule == rule)];

    [Fact]
    public void A_fragment_with_no_member_message_is_flagged_empty_and_one_with_a_member_is_not()
    {
        Document empty = Sequence([Fragment("f_1")], Message("m_1"), Message("m_2"));

        CritiqueFinding finding = Assert.Single(Findings(empty, CritiqueAnalyser.Rules.EmptyFragment));
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(["f_1"], finding.NodeIds);
        Assert.Empty(finding.EdgeIds);

        Document filled = Sequence([Fragment("f_1")], Message("m_1"), Message("m_2", "f_1"));
        Assert.Empty(Findings(filled, CritiqueAnalyser.Rules.EmptyFragment));
    }

    [Fact]
    public void Deleting_every_member_of_the_example_fragment_leaves_an_empty_fragment_warning_and_deleting_one_does_not()
    {
        Document example = Example();
        Assert.Empty(Findings(example, CritiqueAnalyser.Rules.EmptyFragment));

        // Deleting one member from the middle keeps the fragment (and every other rule stays quiet).
        Document oneGone = example with { Edges = [.. example.Edges.Where(e => e.Id != "m_4")] };
        Assert.DoesNotContain(CritiqueAnalyser.Analyse(oneGone), f => f.Rule is CritiqueAnalyser.Rules.EmptyFragment or CritiqueAnalyser.Rules.NonContiguousFragment);

        // Deleting all of them leaves the node behind, and the warning names it.
        Document allGone = example with { Edges = [.. example.Edges.Where(e => e.Id is not ("m_3" or "m_4" or "m_5"))] };
        Assert.Equal(["f_1"], Assert.Single(Findings(allGone, CritiqueAnalyser.Rules.EmptyFragment)).NodeIds);
    }

    [Fact]
    public void A_fragment_holding_only_a_nested_fragments_messages_is_not_empty()
    {
        Document nested = Sequence([Fragment("f_out"), Fragment("f_in", "loop", "f_out")], Message("m_1", "f_in"));

        Assert.Empty(Findings(nested, CritiqueAnalyser.Rules.EmptyFragment));
        // ... a fragment is a valid parent for the structure rules, and it is never reported as an isolated node (the
        // participants the example left out of this document are, which is why the findings are filtered to the fragments).
        Assert.DoesNotContain(CritiqueAnalyser.Analyse(nested), f => f.NodeIds.Any(id => id.StartsWith("f_", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_message_that_is_not_a_member_between_two_members_is_flagged_and_the_evidence_names_it()
    {
        Document gap = Sequence([Fragment("f_1")], Message("m_1", "f_1"), Message("m_2"), Message("m_3", "f_1"));

        CritiqueFinding finding = Assert.Single(Findings(gap, CritiqueAnalyser.Rules.NonContiguousFragment));

        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(["f_1"], finding.NodeIds);
        Assert.Equal(["m_2"], finding.EdgeIds);

        // Contiguous members, and non-members only before and after, are fine.
        Document tight = Sequence([Fragment("f_1")], Message("m_0"), Message("m_1", "f_1"), Message("m_3", "f_1"), Message("m_4"));
        Assert.Empty(Findings(tight, CritiqueAnalyser.Rules.NonContiguousFragment));
    }

    [Fact]
    public void A_nested_fragments_messages_count_as_members_of_the_outer_one_so_they_are_not_a_gap()
    {
        Document nested = Sequence([Fragment("f_out"), Fragment("f_in", "loop", "f_out")], Message("m_1", "f_out"), Message("m_2", "f_in"), Message("m_3", "f_out"));

        Assert.Empty(Findings(nested, CritiqueAnalyser.Rules.NonContiguousFragment));

        // But a message of the outer fragment between two of the inner one is a gap in the inner one only.
        Document interleaved = Sequence([Fragment("f_out"), Fragment("f_in", "loop", "f_out")], Message("m_1", "f_in"), Message("m_2", "f_out"), Message("m_3", "f_in"));
        CritiqueFinding finding = Assert.Single(Findings(interleaved, CritiqueAnalyser.Rules.NonContiguousFragment));
        Assert.Equal(["f_in"], finding.NodeIds);
        Assert.Equal(["m_2"], finding.EdgeIds);
    }

    [Fact]
    public void A_message_naming_a_fragment_that_does_not_exist_is_flagged_and_still_drawn_outside_every_frame()
    {
        Document dangling = Sequence([], Message("m_1", "f_missing"), Message("m_2", "p_web"));

        List<CritiqueFinding> findings = [.. Findings(dangling, CritiqueAnalyser.Rules.UnknownFragment)];

        Assert.Equal([["m_1"], ["m_2"]], findings.Select(f => f.EdgeIds));
        Assert.All(findings, f => Assert.Equal(FindingSeverity.Warning, f.Severity));
        Assert.Empty(Findings(Sequence([Fragment("f_1")], Message("m_1", "f_1")), CritiqueAnalyser.Rules.UnknownFragment));
    }

    [Theory]
    [InlineData("alt", false)]
    [InlineData("Opt", false)]
    [InlineData("loop", false)]
    [InlineData("par", false)]
    [InlineData("break", false)]
    [InlineData("critical", false)]
    [InlineData("neg", false)]
    [InlineData("ref", false)]
    [InlineData("alr", true)]
    [InlineData("otherwise", true)]
    public void An_operator_outside_the_standard_list_is_a_warning_and_the_standard_ones_are_silent(string @operator, bool flagged)
    {
        Document document = Sequence([Fragment("f_1", @operator)], Message("m_1", "f_1"));

        IReadOnlyList<CritiqueFinding> findings = Findings(document, CritiqueAnalyser.Rules.UnknownFragment);

        Assert.Equal(flagged, findings.Count == 1);
        if (flagged)
        {
            Assert.Equal(["f_1"], findings[0].NodeIds);
            Assert.Contains(@operator, findings[0].Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_fragment_rules_run_only_for_sequence_documents()
    {
        Document gap = Sequence([Fragment("f_1")], Message("m_1", "f_1"), Message("m_2"), Message("m_3", "f_1")) with { Type = DocumentType.Freeform };

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(gap), f => f.Rule is CritiqueAnalyser.Rules.NonContiguousFragment or CritiqueAnalyser.Rules.EmptyFragment or CritiqueAnalyser.Rules.UnknownFragment);
    }

    [Fact]
    public void A_destroy_ends_every_bar_open_on_its_target_so_none_is_left_unclosed()
    {
        Edge open = Message("m_1", to: "p_pay") with { Props = new Dictionary<string, JsonElement> { ["activate"] = JsonSerializer.SerializeToElement(true) } };

        Assert.Single(Findings(Sequence([], open, Message("m_2", from: "p_web", to: "p_api")), CritiqueAnalyser.Rules.UnbalancedActivation));
        Assert.Empty(Findings(Sequence([], open, Message("m_2", from: "p_web", to: "p_pay", kind: "destroy")), CritiqueAnalyser.Rules.UnbalancedActivation));
    }

    [Fact]
    public void The_dsl_round_trips_an_operand_list_a_nested_parent_and_the_string_props()
    {
        Document document = Sequence([Fragment("f_out"), Fragment("f_in", "loop", "f_out")], Message("m_1", "f_in"), Message("m_2", "f_out"));

        Document reparsed = Dsl.Parse(Dsl.Print(document)).Document;

        Assert.Equal(["f_out", "f_in"], reparsed.Nodes.Where(n => n.Kind == "fragment").Select(n => n.Id));
        Assert.Equal("f_out", reparsed.Nodes.Single(n => n.Id == "f_in").Parent);
        Assert.Equal(["[x]"], reparsed.Nodes.Single(n => n.Id == "f_in").Props!["operands"].EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["f_in", "f_out"], reparsed.Edges.Select(e => e.Props!["in"].GetString()));
        Assert.Equal(Dsl.Print(document), Dsl.Print(reparsed));
    }
}
