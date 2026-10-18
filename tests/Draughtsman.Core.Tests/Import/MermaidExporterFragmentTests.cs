using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// The Mermaid exporter's milestone 2 half (docs/sequence-diagrams.md): a fragment is a block, a note a <c>Note over</c>, a
/// create or destroy message a <c>create</c>/<c>destroy</c> statement. Expected text is written out literally, never derived
/// from the exporter, and the round trip compares what a reader sees, with a check that the comparison can fail.
/// </summary>
public class MermaidExporterFragmentTests
{
    private static readonly string s_examplePath = Path.Combine(AppContext.BaseDirectory, "contract", "checkout-fragments.json");

    private static Document Example() => DocumentJson.Deserialize(File.ReadAllText(s_examplePath));

    private static Node Participant(string id, string label) => new(id, "participant", label, "rectangle");

    private static Node Fragment(string id, string @operator, string[] operands, string? parent = null) =>
        new(id, "fragment", @operator, "rectangle")
        {
            Parent = parent,
            Props = new Dictionary<string, JsonElement>
            {
                ["operator"] = JsonSerializer.SerializeToElement(@operator),
                ["operands"] = JsonSerializer.SerializeToElement(operands),
            },
        };

    private static Edge Message(string id, string from, string to, string? inside = null, int operand = 0, string kind = "sync", string? label = null)
    {
        var props = new Dictionary<string, JsonElement>();
        if (inside is not null)
        {
            props["in"] = JsonSerializer.SerializeToElement(inside);
            props["operand"] = JsonSerializer.SerializeToElement(operand);
        }

        return new Edge(id, kind, new Endpoint(from), new Endpoint(to)) { Label = label ?? id, Props = props.Count == 0 ? null : props };
    }

    private static Document Doc(Node[] nodes, Edge[] edges) => Example() with { Nodes = nodes, Edges = edges };

    [Fact]
    public void The_fragments_example_exports_to_this_exact_text()
    {
        MermaidExportResult result = MermaidExporter.Export(Example());

        Assert.Empty(result.Warnings);
        Assert.Equal(
            """
            sequenceDiagram
                title Checkout with a fragment, a note, and a created participant
                actor user as Customer
                participant web as Web app
                participant api as Orders API
                participant pay as Payments
                user->>web: Place order
                web->>+api: POST /orders
                Note over web,api: Idempotent by key
                alt card ok
                    api->>+pay: charge(total)
                    pay-->>-api: receipt
                else declined
                    api-)web: declined
                end
                api-->>-web: 201 Created
                create participant mail as Mailer
                web-->>mail: new Mailer()
                web->>mail: send(receipt)
                destroy mail
                web-xmail: close()

            """.Replace("\r\n", "\n", StringComparison.Ordinal),
            result.Text);
    }

    [Fact]
    public void A_fragment_is_never_a_participant_and_a_created_one_is_declared_by_its_message()
    {
        string text = MermaidExporter.Export(Example()).Text;

        Assert.DoesNotContain("participant f_1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("participant f1", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split('\n').Count(line => line.Contains("participant mail", StringComparison.Ordinal)));
        Assert.True(text.IndexOf("create participant mail", StringComparison.Ordinal) > text.IndexOf("201 Created", StringComparison.Ordinal));
    }

    /// <summary>What a reader sees: lifelines, then every row with its kind, ends, label and the fragment path it is in with the operand.</summary>
    private static string Shape(Document document)
    {
        string Label(string id) => document.Nodes.Single(n => n.Id == id).Label;
        IReadOnlyDictionary<string, IReadOnlyList<SequenceMessages.FragmentLevel>> memberships = SequenceMessages.Memberships(document);
        // A guard's brackets are drawn, not stored: the example keeps them (an older document), the import does not.
        string Path(Edge edge) => memberships.TryGetValue(edge.Id, out IReadOnlyList<SequenceMessages.FragmentLevel>? levels)
            ? string.Join('/', levels.Select(l => $"{document.Nodes.Single(n => n.Id == l.Fragment).Label}:{SequenceMessages.OperandsOf(document.Nodes.Single(n => n.Id == l.Fragment)).ElementAtOrDefault(l.Operand)?.Trim('[', ']', ' ')}"))
            : "-";
        var lines = document.Nodes.Where(n => n.Kind != "fragment").Select(n => $"{n.Kind} {n.Label}").ToList();
        lines.AddRange(document.Edges.Select(e => $"{e.Kind} {Label(e.From.Node)} -> {Label(e.To.Node)} [{e.Label}] in {Path(e)}"));
        return string.Join("\n", lines);
    }

    [Fact]
    public void The_fragments_example_round_trips_through_the_importer_with_the_same_frames_notes_and_lifecycles()
    {
        Document original = Example();

        ImportResult back = MermaidImporter.Import(MermaidExporter.Export(original).Text, new MermaidImportOptions { Type = DocumentType.Sequence });

        Assert.Empty(back.Warnings);
        Assert.Equal(Shape(original), Shape(back.Document));
        // The comparison has teeth: it names the fragment path and the kinds, so a message moved out of its frame or a
        // create turned plain is a different shape.
        Document outOfFrame = original with { Edges = original.Edges.Select(e => e.Id == "m_4" ? e with { Props = new Dictionary<string, JsonElement> { ["deactivate"] = JsonSerializer.SerializeToElement(true) } } : e).ToList() };
        Assert.NotEqual(Shape(original), Shape(outOfFrame));
        Document plain = original with { Edges = original.Edges.Select(e => e.Kind == "create" ? e with { Kind = "sync" } : e).ToList() };
        Assert.NotEqual(Shape(original), Shape(plain));
    }

    [Fact]
    public void Nested_fragments_and_par_critical_blocks_are_indented_and_use_their_own_continuation_words()
    {
        Document document = Doc(
            [Participant("p_a", "A"), Participant("p_b", "B"), Fragment("f_1", "par", ["[left]", "[right]"]), Fragment("f_2", "loop", ["[n times]"], "f_1"), Fragment("f_3", "critical", ["[try]", "[on error]"])],
            [Message("m_1", "p_a", "p_b", "f_1", 0), Message("m_2", "p_a", "p_b", "f_2"), Message("m_3", "p_b", "p_a", "f_1", 1), Message("m_4", "p_a", "p_b", "f_3", 0), Message("m_5", "p_b", "p_a", "f_3", 1)]);

        string text = MermaidExporter.Export(document).Text;

        Assert.Contains(
            "    par left\n        a->>b: m_1\n        loop n times\n            a->>b: m_2\n        end\n    and right\n        b->>a: m_3\n    end\n    critical try\n        a->>b: m_4\n    option on error\n        b->>a: m_5\n    end\n",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_with_no_guard_is_written_bare_and_a_bracketless_guard_is_kept()
    {
        Document document = Doc([Participant("p_a", "A"), Participant("p_b", "B"), Fragment("f_1", "opt", [""]), Fragment("f_2", "break", ["done"])], [Message("m_1", "p_a", "p_b", "f_1"), Message("m_2", "p_a", "p_b", "f_2")]);

        string text = MermaidExporter.Export(document).Text;

        Assert.Contains("    opt\n        a->>b: m_1\n    end\n    break done\n        a->>b: m_2\n    end\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operator_mermaid_has_no_block_for_is_flattened_with_a_warning_and_no_stray_end()
    {
        Document document = Doc([Participant("p_a", "A"), Participant("p_b", "B"), Fragment("f_1", "neg", ["[x]"])], [Message("m_1", "p_a", "p_b", "f_1"), Message("m_2", "p_a", "p_b")]);

        MermaidExportResult result = MermaidExporter.Export(document);

        Assert.Contains("    %% neg x\n    a->>b: m_1\n    a->>b: m_2\n", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("end", result.Text.Replace("sequenceDiagram", string.Empty, StringComparison.Ordinal).Replace("Idempotent", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("'neg'", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_operand_of_an_operator_with_no_continuation_word_is_warned_and_commented()
    {
        Document document = Doc([Participant("p_a", "A"), Participant("p_b", "B"), Fragment("f_1", "opt", ["[x]", "[y]"])], [Message("m_1", "p_a", "p_b", "f_1", 0), Message("m_2", "p_a", "p_b", "f_1", 1)]);

        MermaidExportResult result = MermaidExporter.Export(document);

        Assert.Contains("    opt x\n        a->>b: m_1\n        %% operand 1 of the fragment above\n        a->>b: m_2\n    end\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("second operand", Assert.Single(result.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_over_one_lifeline_and_a_second_create_or_destroy_of_the_same_participant_are_written_plainly()
    {
        Document document = Doc(
            [Participant("p_a", "A"), Participant("p_b", "B")],
            [Message("n_1", "p_a", "p_a", kind: "note", label: "just A"), Message("m_1", "p_a", "p_b", kind: "create"), Message("m_2", "p_a", "p_b", kind: "create"), Message("m_3", "p_a", "p_b", kind: "destroy"), Message("m_4", "p_a", "p_b", kind: "destroy")]);

        string text = MermaidExporter.Export(document).Text;

        Assert.Contains("    participant a as A\n    Note over a: just A\n    create participant b as B\n    a-->>b: m_1\n    a-->>b: m_2\n    destroy b\n    a-xb: m_3\n    a->>b: m_4\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_interaction_use_is_written_as_a_note_reading_ref_and_says_so()
    {
        Document document = Doc(
            [Participant("a", "A"), Participant("b", "B")],
            [Message("m_1", "a", "b"), Message("u_1", "a", "b", kind: "interaction-use", label: "Authenticate"), Message("u_2", "b", "b", kind: "interaction-use", label: null) with { Label = null }]);

        MermaidExportResult result = MermaidExporter.Export(document);

        Assert.Contains("    Note over a,b: ref: Authenticate\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("    Note over b: ref:\n", result.Text, StringComparison.Ordinal);
        Assert.Equal(2, result.Warnings.Count(w => w.Contains("no Mermaid form", StringComparison.Ordinal)));
        Assert.Contains(result.Warnings, w => w.Contains("'u_1'", StringComparison.Ordinal));
    }
}
