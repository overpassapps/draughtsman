using System.Text.Json;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Tests.Text;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests;

/// <summary>
/// The <c>sequence</c> document type (docs/sequence-diagrams.md): the vocabulary, the JSON and DSL forms of the checkout
/// example (docs/examples/checkout-sequence.{json,dsl}, the same document, as order-service is), and the critique rule
/// for activation bars. The derived geometry is web/src/model/sequenceScene.ts and is tested there.
/// </summary>
public class SequenceDocumentTests
{
    private static readonly string s_examplePath = Path.Combine(AppContext.BaseDirectory, "contract", "checkout-sequence.json");
    private static readonly string s_exampleDslPath = Path.Combine(AppContext.BaseDirectory, "contract", "checkout-sequence.dsl");

    private static Document Example() => DocumentJson.Deserialize(File.ReadAllText(s_examplePath));

    [Fact]
    public void The_vocabulary_names_the_participant_and_fragment_kinds_and_the_message_kinds_with_their_default_look()
    {
        KindVocabulary vocabulary = KindVocabulary.For(DocumentType.Sequence);

        Assert.Equal("sequence", vocabulary.Type);
        Assert.Equal(["participant", "actor", "fragment"], vocabulary.NodeKinds.Select(k => k.Name));
        Assert.Equal(["sync", "async", "return", "note", "interaction-use", "create", "destroy"], vocabulary.EdgeKinds.Select(k => k.Name));
        Assert.All(vocabulary.NodeKinds, k => Assert.False(string.IsNullOrWhiteSpace(k.DefaultShape)));

        EdgeStyle sync = Assert.Single(vocabulary.EdgeKinds, k => k.Name == "sync").DefaultStyle!;
        EdgeStyle async = Assert.Single(vocabulary.EdgeKinds, k => k.Name == "async").DefaultStyle!;
        EdgeStyle reply = Assert.Single(vocabulary.EdgeKinds, k => k.Name == "return").DefaultStyle!;
        Assert.Equal((Dash.Solid, ArrowHead.Arrow), (sync.Dash, sync.ArrowEnd));
        Assert.Equal((Dash.Solid, ArrowHead.Open), (async.Dash, async.ArrowEnd));
        Assert.Equal((Dash.Dashed, ArrowHead.Open), (reply.Dash, reply.ArrowEnd));

        // Milestone 2: a note has no head, a create is a dashed open-headed line, a destroy a solid filled one.
        Assert.Equal((Dash.Solid, ArrowHead.None), (KindStyle(vocabulary, "note").Dash, KindStyle(vocabulary, "note").ArrowEnd));
        Assert.Equal((Dash.Dashed, ArrowHead.Open), (KindStyle(vocabulary, "create").Dash, KindStyle(vocabulary, "create").ArrowEnd));
        Assert.Equal((Dash.Solid, ArrowHead.Arrow), (KindStyle(vocabulary, "destroy").Dash, KindStyle(vocabulary, "destroy").ArrowEnd));
    }

    private static EdgeStyle KindStyle(KindVocabulary vocabulary, string kind) => Assert.Single(vocabulary.EdgeKinds, k => k.Name == kind).DefaultStyle!;

    [Fact]
    public void The_vocabulary_teaches_that_edge_order_is_time_and_the_activation_props()
    {
        string json = KindVocabulary.ReadRawJson(DocumentType.Sequence);

        Assert.Contains("ORDER OF THE EDGES ARRAY IS TIME ORDER", json, StringComparison.Ordinal);
        Assert.Contains("addEdge takes before/after", json, StringComparison.Ordinal);
        foreach (KindDefinition kind in KindVocabulary.For(DocumentType.Sequence).EdgeKinds.Where(k => k.Name is "sync" or "async" or "return"))
        {
            Assert.Equal(["activate", "deactivate", "in", "operand"], kind.Props!.Keys.Order());
        }

        // Fragment membership is written on the message, and the description an agent reads must say so, with the DSL shape.
        Assert.Contains("in=\\\"<fragment id>\\\"", json, StringComparison.Ordinal);
        KindDefinition fragment = Assert.Single(KindVocabulary.For(DocumentType.Sequence).NodeKinds, k => k.Name == "fragment");
        Assert.Equal(["operand", "operands", "operator"], fragment.Props!.Keys.Order());
        Assert.Contains("never here", fragment.Description, StringComparison.Ordinal);

        // Only the sequence vocabulary ships message default styles: the other kinds carry none.
        Assert.All(KindVocabulary.For(DocumentType.Architecture).EdgeKinds, k => Assert.Null(k.DefaultStyle));
    }

    [Fact]
    public void The_example_validates_against_the_schema_and_a_misspelt_type_does_not()
    {
        string json = File.ReadAllText(s_examplePath);

        Assert.True(ContractFiles.Evaluate(json).IsValid);
        Assert.False(ContractFiles.Evaluate(json.Replace("\"type\": \"sequence\"", "\"type\": \"sequnce\"", StringComparison.Ordinal)).IsValid);
    }

    [Fact]
    public void The_example_json_and_dsl_are_the_same_document_and_the_dsl_prints_back_byte_identical()
    {
        Document expected = Example();
        string dsl = File.ReadAllText(s_exampleDslPath);

        ParseResult parsed = Dsl.Parse(dsl);

        Assert.Empty(parsed.Warnings);
        DocumentAssert.Equivalent(expected, parsed.Document, "docs/examples/checkout-sequence.dsl does not parse to docs/examples/checkout-sequence.json.");
        Assert.Equal(dsl, Dsl.Print(parsed.Document));
        Assert.Equal(dsl, Dsl.Print(expected));
    }

    [Fact]
    public void Time_is_the_order_of_the_edges_and_it_survives_a_round_trip_through_json_and_the_dsl()
    {
        Document document = Example();
        Assert.Equal(["m_1", "m_2", "m_3", "m_4", "m_5", "m_6", "m_7"], document.Edges.Select(e => e.Id));
        Assert.All(document.Nodes, n => Assert.Null(n.Geometry));

        Document reordered = document with { Edges = [document.Edges[2], document.Edges[0], .. document.Edges.Skip(3), document.Edges[1]] };
        Document viaDsl = Dsl.Parse(Dsl.Print(reordered)).Document;
        Document viaJson = DocumentJson.Deserialize(DocumentJson.Serialize(reordered));

        string[] expected = ["m_3", "m_1", "m_4", "m_5", "m_6", "m_7", "m_2"];
        Assert.Equal(expected, viaDsl.Edges.Select(e => e.Id));
        Assert.Equal(expected, viaJson.Edges.Select(e => e.Id));
        Assert.NotEqual(Dsl.Print(document), Dsl.Print(reordered));
    }

    [Fact]
    public void The_dsl_elides_the_default_shape_of_a_participant_and_an_actor_and_keeps_activate_a_boolean()
    {
        string dsl = File.ReadAllText(s_exampleDslPath);

        Assert.DoesNotContain("shape=", dsl, StringComparison.Ordinal);
        Edge activating = Dsl.Parse(dsl).Document.Edges.Single(e => e.Id == "m_2");
        Assert.Equal(JsonValueKind.True, activating.Props!["activate"].ValueKind);
        Assert.Equal("rounded", Dsl.Parse(dsl).Document.Nodes.Single(n => n.Id == "p_user").Shape);
        Assert.Equal("rectangle", Dsl.Parse(dsl).Document.Nodes.Single(n => n.Id == "p_web").Shape);
    }

    // ---- critique: unbalanced-activation, both directions ----

    private static Document Sequence(params Edge[] messages) => Example() with { Edges = messages };

    private static Edge Message(string id, string from, string to, string? prop = null, JsonElement? value = null)
    {
        var edge = new Edge(id, "sync", new Endpoint(from), new Endpoint(to));
        return prop is null ? edge : edge with { Props = new Dictionary<string, JsonElement> { [prop] = value ?? JsonSerializer.SerializeToElement(true) } };
    }

    private static IReadOnlyList<CritiqueFinding> Unbalanced(Document document) =>
        [.. CritiqueAnalyser.Analyse(document).Where(f => f.Rule == CritiqueAnalyser.Rules.UnbalancedActivation)];

    [Fact]
    public void A_balanced_activation_raises_nothing_and_the_example_is_clean()
    {
        Assert.Empty(Unbalanced(Example()));
        Assert.Empty(CritiqueAnalyser.Analyse(Example()));
    }

    [Fact]
    public void A_deactivate_with_nothing_open_is_flagged_with_the_message_and_the_participant()
    {
        Document document = Sequence(Message("m_1", "p_web", "p_api"), Message("m_2", "p_api", "p_web", "deactivate"));

        CritiqueFinding finding = Assert.Single(Unbalanced(document));

        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(["p_api"], finding.NodeIds);
        Assert.Equal(["m_2"], finding.EdgeIds);
        Assert.Contains("no open activation", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bar_nothing_closes_is_flagged_on_the_message_that_opened_it()
    {
        Document document = Sequence(Message("m_1", "p_web", "p_api", "activate"), Message("m_2", "p_api", "p_web"));

        CritiqueFinding finding = Assert.Single(Unbalanced(document));

        Assert.Equal(["p_api"], finding.NodeIds);
        Assert.Equal(["m_1"], finding.EdgeIds);
        Assert.Contains("nothing deactivates it", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_bars_pair_up_innermost_first_and_only_the_extra_one_is_flagged()
    {
        Document document = Sequence(
            Message("m_1", "p_web", "p_api", "activate"),
            Message("m_2", "p_web", "p_api", "activate"),
            Message("m_3", "p_web", "p_api", "activate"),
            Message("m_4", "p_api", "p_web", "deactivate"),
            Message("m_5", "p_api", "p_web", "deactivate"));

        CritiqueFinding finding = Assert.Single(Unbalanced(document));

        Assert.Equal(["m_1"], finding.EdgeIds);
    }

    [Fact]
    public void A_bar_is_closed_by_its_own_participant_not_by_a_deactivate_from_another()
    {
        // p_web deactivates, but the open bar is p_api's, so the deactivate is stray and the bar is left open: two findings.
        Document document = Sequence(Message("m_1", "p_web", "p_api", "activate"), Message("m_2", "p_web", "p_api", "deactivate"));

        Assert.Equal(["m_1", "m_2"], Unbalanced(document).SelectMany(f => f.EdgeIds).Order());
    }

    [Fact]
    public void The_string_true_counts_as_on_and_other_values_do_not()
    {
        JsonElement text = JsonSerializer.SerializeToElement("TRUE");
        Assert.Single(Unbalanced(Sequence(Message("m_1", "p_web", "p_api", "activate", text))));

        foreach (JsonElement off in new[] { JsonSerializer.SerializeToElement(false), JsonSerializer.SerializeToElement("yes"), JsonSerializer.SerializeToElement(1) })
        {
            Assert.Empty(Unbalanced(Sequence(Message("m_1", "p_web", "p_api", "activate", off))));
        }
    }

    [Fact]
    public void The_rule_runs_only_for_sequence_documents()
    {
        Document notSequence = Sequence(Message("m_1", "p_web", "p_api", "activate")) with { Type = DocumentType.Freeform };

        Assert.Empty(Unbalanced(notSequence));
    }

    [Fact]
    public void A_participant_with_no_messages_is_flagged_isolated_by_the_generic_rule()
    {
        Document document = Sequence(Message("m_1", "p_web", "p_api"));

        Assert.Contains(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.IsolatedNode && f.NodeIds.SequenceEqual(["p_user"]));
    }
}
