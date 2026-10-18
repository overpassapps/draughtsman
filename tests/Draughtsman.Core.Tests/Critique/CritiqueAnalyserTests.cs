using Draughtsman.Core.Critique;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Tests;

/// <summary>
/// Each rule is proven in both directions: it fires on a minimal defective document, and it stays silent on the
/// nearest valid one. Every finding carries node or edge evidence, so the assertions check the ids too, not just the
/// rule name.
/// </summary>
public class CritiqueAnalyserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rules_key_only_on_kinds_the_shipped_vocabularies_spell()
    {
        KindVocabulary flowchart = KindVocabulary.For(DocumentType.Flowchart);
        KindVocabulary architecture = KindVocabulary.For(DocumentType.Architecture);

        foreach (string kind in new[] { CritiqueAnalyser.Terminal, CritiqueAnalyser.Process, CritiqueAnalyser.Decision, CritiqueAnalyser.Io, CritiqueAnalyser.Subprocess, CritiqueAnalyser.Container, CritiqueAnalyser.Pool, CritiqueAnalyser.Lane, CritiqueAnalyser.Note, CritiqueAnalyser.Text, CritiqueAnalyser.Image, CritiqueAnalyser.Sticky })
        {
            Assert.True(flowchart.IsKnownNodeKind(kind), $"flowchart vocabulary lacks '{kind}'");
        }

        Assert.True(flowchart.IsKnownEdgeKind(CritiqueAnalyser.FlowEdge), "flowchart vocabulary lacks flow edge");
        Assert.True(architecture.IsKnownNodeKind(CritiqueAnalyser.Database), "architecture vocabulary lacks database");
        Assert.True(architecture.IsKnownEdgeKind(CritiqueAnalyser.DataEdge), "architecture vocabulary lacks data edge");
        Assert.True(architecture.IsKnownEdgeKind(CritiqueAnalyser.FlowEdge), "architecture vocabulary lacks flow edge");
    }

    [Fact]
    public void Dangling_edge_fires_when_an_edge_points_at_a_missing_node()
    {
        var document = D(DocumentType.Flowchart, [N("n_a", "process")], [E("e_1", "n_a", "n_ghost")]);

        var findings = CritiqueAnalyser.Analyse(document);

        CritiqueFinding finding = Assert.Single(findings, f => f.Rule == CritiqueAnalyser.Rules.DanglingEdge);
        Assert.Equal(FindingSeverity.Error, finding.Severity);
        Assert.Equal(["e_1"], finding.EdgeIds);
        Assert.Contains("n_ghost", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dangling_edge_stays_silent_when_both_endpoints_exist()
    {
        var document = D(DocumentType.Flowchart, [N("n_a", "process"), N("n_b", "process")], [E("e_1", "n_a", "n_b")]);

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DanglingEdge);
    }

    [Fact]
    public void Dangling_parent_fires_when_a_node_names_a_missing_parent()
    {
        var document = D(DocumentType.Flowchart, [N("n_a", "process") with { Parent = "c_gone" }], []);

        CritiqueFinding finding = Assert.Single(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DanglingParent);
        Assert.Equal(["n_a"], finding.NodeIds);
    }

    [Fact]
    public void Dangling_parent_stays_silent_when_the_parent_exists()
    {
        var document = D(DocumentType.Flowchart,
            [N("c_1", "container"), N("n_a", "process") with { Parent = "c_1" }], []);

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DanglingParent);
    }

    [Fact]
    public void Non_container_parent_fires_when_a_node_names_a_non_container_parent()
    {
        var document = D(DocumentType.Flowchart, [N("n_x", "process"), N("n_a", "process") with { Parent = "n_x" }], []);

        CritiqueFinding finding = Assert.Single(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.NonContainerParent);
        Assert.Equal(["n_a", "n_x"], finding.NodeIds);
    }

    [Fact]
    public void Non_container_parent_stays_silent_for_a_container_parent()
    {
        var document = D(DocumentType.Flowchart,
            [N("c_1", "container"), N("n_a", "process") with { Parent = "c_1" }], []);

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.NonContainerParent);
    }

    [Fact]
    public void Decision_with_one_branch_fires()
    {
        var document = D(DocumentType.Flowchart,
            [N("d", "decision"), N("p", "process")],
            [E("e_yes", "d", "p", label: "Yes")]);

        CritiqueFinding finding = Assert.Single(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DecisionSingleBranch);
        Assert.Equal(["d"], finding.NodeIds);
        Assert.Equal(["e_yes"], finding.EdgeIds);
    }

    [Fact]
    public void Decision_with_two_branches_stays_silent()
    {
        var document = D(DocumentType.Flowchart,
            [N("d", "decision"), N("p1", "process"), N("p2", "process")],
            [E("e_yes", "d", "p1", label: "Yes"), E("e_no", "d", "p2", label: "No")]);

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DecisionSingleBranch);
    }

    [Fact]
    public void Unlabelled_decision_branch_fires()
    {
        var document = D(DocumentType.Flowchart,
            [N("d", "decision"), N("p1", "process"), N("p2", "process")],
            [E("e_yes", "d", "p1", label: "Yes"), E("e_2", "d", "p2")]);

        CritiqueFinding finding = Assert.Single(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DecisionBranchUnlabelled);
        Assert.Equal(["d"], finding.NodeIds);
        Assert.Equal(["e_2"], finding.EdgeIds);
    }

    [Fact]
    public void Labelled_decision_branches_stay_silent()
    {
        var document = D(DocumentType.Flowchart,
            [N("d", "decision"), N("p1", "process"), N("p2", "process")],
            [E("e_yes", "d", "p1", label: "Yes"), E("e_no", "d", "p2", label: "No")]);

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DecisionBranchUnlabelled);
    }

    [Fact]
    public void Flow_that_stops_at_a_process_fires_dead_end()
    {
        var document = D(DocumentType.Flowchart,
            [N("start", "terminal"), N("a", "process")],
            [E("e_1", "start", "a")]);

        Assert.Contains(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DeadEnd && f.NodeIds.SequenceEqual(["a"]));
    }

    [Fact]
    public void Flow_reaching_a_terminal_stays_silent_for_dead_end()
    {
        var document = D(DocumentType.Flowchart,
            [N("start", "terminal"), N("a", "process"), N("end", "terminal")],
            [E("e_1", "start", "a"), E("e_2", "a", "end")]);

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DeadEnd);
    }

    [Fact]
    public void Flow_that_stops_at_a_subprocess_fires_dead_end()
    {
        var document = D(DocumentType.Flowchart,
            [N("start", "terminal"), N("a", "process"), N("s", "subprocess")],
            [E("e_1", "start", "a"), E("e_2", "a", "s")]);

        Assert.Contains(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DeadEnd && f.NodeIds.SequenceEqual(["s"]));
    }

    [Fact]
    public void Subprocess_followed_by_a_terminal_stays_silent_for_dead_end()
    {
        var document = D(DocumentType.Flowchart,
            [N("start", "terminal"), N("s", "subprocess"), N("end", "terminal")],
            [E("e_1", "start", "s"), E("e_2", "s", "end")]);

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DeadEnd);
    }

    [Fact]
    public void A_node_unreachable_from_any_terminal_fires()
    {
        var document = D(DocumentType.Flowchart,
            [N("start", "terminal"), N("a", "process"), N("b", "process"), N("c", "process")],
            [E("e_1", "start", "a"), E("e_2", "b", "c")]);

        var findings = CritiqueAnalyser.Analyse(document);
        Assert.Contains(findings, f => f.Rule == CritiqueAnalyser.Rules.UnreachableNode && f.NodeIds.SequenceEqual(["b"]));
        Assert.Contains(findings, f => f.Rule == CritiqueAnalyser.Rules.UnreachableNode && f.NodeIds.SequenceEqual(["c"]));
    }

    [Fact]
    public void A_node_reachable_from_a_terminal_stays_silent()
    {
        var document = D(DocumentType.Flowchart,
            [N("start", "terminal"), N("a", "process"), N("b", "process"), N("end", "terminal")],
            [E("e_1", "start", "a"), E("e_2", "a", "b"), E("e_3", "b", "end")]);

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.UnreachableNode);
    }

    [Fact]
    public void A_node_with_no_edges_fires_isolated()
    {
        var document = D(DocumentType.Architecture, [N("svc", "service")], []);

        Assert.Contains(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.IsolatedNode && f.NodeIds.SequenceEqual(["svc"]));
    }

    [Fact]
    public void A_connected_node_stays_silent_for_isolated()
    {
        var document = D(DocumentType.Architecture, [N("svc", "service"), N("db", "database")], [E("e_1", "svc", "db", kind: "data")]);

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.IsolatedNode);
    }

    [Fact]
    public void An_empty_container_fires()
    {
        var document = D(DocumentType.Flowchart, [N("c_1", "container")], []);

        Assert.Contains(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.EmptyContainer && f.NodeIds.SequenceEqual(["c_1"]));
    }

    [Fact]
    public void A_container_with_children_stays_silent()
    {
        var document = D(DocumentType.Flowchart,
            [N("c_1", "container"), N("n_a", "process") with { Parent = "c_1" }], []);

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.EmptyContainer);
    }

    [Fact]
    public void A_database_nothing_reads_or_writes_fires()
    {
        var document = D(DocumentType.Architecture, [N("db", "database")], []);

        Assert.Contains(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DatabaseUnused && f.NodeIds.SequenceEqual(["db"]));
    }

    [Fact]
    public void A_database_with_an_incoming_data_edge_stays_silent()
    {
        var document = D(DocumentType.Architecture,
            [N("svc", "service"), N("db", "database")],
            [E("e_1", "svc", "db", kind: "data")]);

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DatabaseUnused);
    }

    [Fact]
    public void A_database_only_read_from_stays_silent()
    {
        var document = D(DocumentType.Architecture,
            [N("db", "database"), N("report", "service")],
            [E("e_r", "db", "report", kind: "data", label: "read-only")]);

        Assert.DoesNotContain(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.DatabaseUnused);
    }

    [Fact]
    public void A_duplicate_node_id_fails_loudly_rather_than_mismatching()
    {
        var node = N("n_a", "process");
        var document = D(DocumentType.Flowchart, [node, node with { Label = "dupe" }], []);

        Assert.Throws<InvalidOperationException>(() => CritiqueAnalyser.Analyse(document));
    }

    [Fact]
    public void The_example_documents_produce_no_findings()
    {
        Document jsonExample = DocumentJson.Deserialize(ContractFiles.ReadExample());
        Document dslExample = Draughtsman.Core.Text.Dsl.Parse(ContractFiles.ReadExampleDsl()).Document;

        Assert.Empty(CritiqueAnalyser.Analyse(jsonExample));
        Assert.Empty(CritiqueAnalyser.Analyse(dslExample));
    }

    [Fact]
    public void A_swimlane_pool_with_a_lane_and_contents_is_silent_for_parent_empty_and_isolated()
    {
        var document = D(DocumentType.Flowchart,
            [
                N("c_pool", "pool"),
                N("c_lane", "lane") with { Parent = "c_pool" },
                N("n_task", "process") with { Parent = "c_lane" },
            ],
            [E("e_loop", "n_task", "n_task")]);

        var findings = CritiqueAnalyser.Analyse(document);

        Assert.DoesNotContain(findings, f => f.Rule == CritiqueAnalyser.Rules.NonContainerParent);
        Assert.DoesNotContain(findings, f => f.Rule == CritiqueAnalyser.Rules.EmptyContainer);
        Assert.DoesNotContain(findings, f => f.Rule == CritiqueAnalyser.Rules.IsolatedNode);
    }

    [Fact]
    public void An_empty_swimlane_fires_empty_container()
    {
        var document = D(DocumentType.Flowchart, [N("c_pool", "pool"), N("c_lane", "lane") with { Parent = "c_pool" }], []);

        Assert.Contains(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.EmptyContainer && f.NodeIds.SequenceEqual(["c_lane"]));
    }

    [Fact]
    public void A_lane_parented_to_a_process_fires_non_container_parent()
    {
        var document = D(DocumentType.Flowchart,
            [N("n_task", "process"), N("c_lane", "lane") with { Parent = "n_task" }], []);

        Assert.Contains(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.NonContainerParent && f.NodeIds.SequenceEqual(["c_lane", "n_task"]));
    }

    private static Document D(DocumentType type, IReadOnlyList<Node> nodes, IReadOnlyList<Edge> edges)
    {
        return new Document("doc_test", Document.CurrentSchemaVersion, "T", type, nodes, edges, new Meta(Now, Now, "test"));
    }

    private static Node N(string id, string kind)
    {
        return new Node(id, kind, id, "rectangle");
    }

    private static Edge E(string id, string from, string to, string kind = "flow", string? label = null)
    {
        return new Edge(id, kind, new Endpoint(from), new Endpoint(to)) { Label = label };
    }
}
