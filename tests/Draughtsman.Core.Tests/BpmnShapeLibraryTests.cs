using Draughtsman.Core.Critique;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests;

/// <summary>The BPMN shape library is data shared by the palette, the DSL and both renderers; it must stay well
/// formed, round-trip, line up with the flowchart kinds that name a BPMN shape as their default, and let a process
/// drawn as intended pass the critique.</summary>
public class BpmnShapeLibraryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_library_covers_the_batch_s_shape_set_in_order()
    {
        string[] expected =
        [
            "start", "start-message", "start-timer", "start-signal",
            "intermediate", "intermediate-message", "intermediate-message-throw", "intermediate-timer", "intermediate-error",
            "intermediate-signal", "intermediate-signal-throw", "intermediate-link", "intermediate-link-throw",
            "end", "end-message", "end-error", "end-signal",
            "task", "user-task", "service-task", "script-task", "manual-task", "send-task", "receive-task",
            "business-rule-task", "sub-process", "call-activity",
            "exclusive-gateway", "parallel-gateway", "inclusive-gateway", "event-gateway", "complex-gateway",
            "data-object", "data-store", "annotation",
        ];

        Assert.Equal(expected, BpmnShapeLibrary.All.Select(shape => shape.Name).ToArray());
    }

    [Fact]
    public void Every_shape_has_a_unique_well_formed_name_a_label_and_a_glyph_that_exists()
    {
        var names = new List<string>();
        foreach (BpmnShape shape in BpmnShapeLibrary.All)
        {
            Assert.Matches("^[A-Za-z0-9_.-]+$", shape.Name);
            Assert.False(string.IsNullOrWhiteSpace(shape.Label));
            Assert.Equal(BpmnShapeLibrary.KindPrefix + shape.Name, shape.Kind);
            names.Add(shape.Name);
            if (shape.Glyph is not null)
            {
                Assert.True(BpmnShapeLibrary.Glyphs.TryGetValue(shape.Glyph, out IReadOnlyList<System.Text.Json.JsonElement>? elements), $"{shape.Name} names glyph '{shape.Glyph}', which is not drawn");
                Assert.NotEmpty(elements!);
                Assert.NotNull(shape.Placement);
            }

            Assert.True(shape.Width > 0 && shape.Height > 0);
        }

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        // Every drawn glyph is used by some shape, so the file carries no dead art.
        HashSet<string> used = BpmnShapeLibrary.All.Where(shape => shape.Glyph is not null).Select(shape => shape.Glyph!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(BpmnShapeLibrary.Glyphs.Keys.OrderBy(k => k, StringComparer.Ordinal), used.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void An_event_s_border_weight_follows_its_position_and_only_the_call_activity_otherwise_differs()
    {
        foreach (BpmnShape shape in BpmnShapeLibrary.All.Where(shape => shape.Family == BpmnShapeLibrary.EventFamily))
        {
            string expected = shape.Position switch
            {
                BpmnShapeLibrary.StartPosition => "single",
                BpmnShapeLibrary.IntermediatePosition => "double",
                BpmnShapeLibrary.EndPosition => "thick",
                _ => throw new InvalidOperationException($"{shape.Name} has no position"),
            };
            Assert.Equal(expected, shape.Border);
        }

        Assert.Equal(["call-activity"], BpmnShapeLibrary.All.Where(shape => shape.Family != BpmnShapeLibrary.EventFamily && shape.Border != "single").Select(shape => shape.Name).ToArray());
    }

    [Theory]
    [InlineData("custom:bpmn/user-task", true)]
    [InlineData("custom:bpmn/event-gateway", true)]
    [InlineData("custom:bpmn/not-a-real-shape", false)]
    [InlineData("custom:swimlane/pool", false)]
    [InlineData("custom:bpmn", false)]
    [InlineData("rounded", false)]
    public void Only_a_name_in_the_library_is_a_bpmn_shape(string shape, bool expected)
    {
        Assert.Equal(expected, BpmnShapeLibrary.IsBpmnShape(shape));
    }

    [Fact]
    public void Every_shape_is_a_flowchart_kind_s_own_default_shape_and_every_bpmn_kind_has_a_shape()
    {
        KindVocabulary flowchart = KindVocabulary.For(DocumentType.Flowchart);
        foreach (BpmnShape shape in BpmnShapeLibrary.All)
        {
            KindDefinition? kind = flowchart.NodeKinds.FirstOrDefault(candidate => candidate.Name == shape.Kind);
            Assert.NotNull(kind);
            Assert.Equal(BpmnShapeLibrary.ShapeName(shape), kind!.DefaultShape);
            Assert.True(kind.Description.Length > 20, $"{shape.Kind} needs a description an agent can act on");
        }

        // The other direction: a BPMN kind in the vocabulary with no shape behind it would draw as a plain box.
        foreach (KindDefinition kind in flowchart.NodeKinds.Where(kind => kind.Name.StartsWith(BpmnShapeLibrary.KindPrefix, StringComparison.Ordinal)))
        {
            Assert.NotNull(BpmnShapeLibrary.ForKind(kind.Name));
            Assert.True(BpmnShapeLibrary.IsBpmnShape(kind.DefaultShape!));
        }
    }

    [Fact]
    public void The_flowchart_vocabulary_lists_the_bpmn_edge_kinds_with_descriptions()
    {
        KindVocabulary flowchart = KindVocabulary.For(DocumentType.Flowchart);
        foreach (string name in new[] { "flow", "message-flow", "bpmn-association", "data-association" })
        {
            KindDefinition? kind = flowchart.EdgeKinds.FirstOrDefault(candidate => candidate.Name == name);
            Assert.NotNull(kind);
            Assert.False(string.IsNullOrWhiteSpace(kind!.Description));
        }

        // The architecture vocabulary does not know them: they belong to process diagrams.
        Assert.False(KindVocabulary.For(DocumentType.Architecture).IsKnownEdgeKind("message-flow"));
    }

    [Fact]
    public void Every_shape_round_trips_through_json_and_dsl_in_both_the_shape_and_its_kind()
    {
        foreach (BpmnShape shape in BpmnShapeLibrary.All)
        {
            string name = BpmnShapeLibrary.ShapeName(shape);
            Document original = DocumentJsonTests.MinimalDocument() with
            {
                Type = DocumentType.Flowchart,
                Nodes = [new Node("n_bpmn", shape.Kind, shape.Label, name)],
            };
            string json = DocumentJson.Serialize(original);
            Assert.True(ContractFiles.Evaluate(json).IsValid, $"{name} is not admitted by the schema");

            string text = Dsl.Print(original);
            // Two-sided: the printer elides the shape (it is the kind's default) and the kind name prints in its place.
            Assert.DoesNotContain("shape=", text, StringComparison.Ordinal);
            Assert.Contains(shape.Kind, text, StringComparison.Ordinal);

            ParseResult parsed = Dsl.Parse(text);
            Assert.Empty(parsed.Warnings);
            Assert.Equal(name, Assert.Single(parsed.Document.Nodes).Shape);
            Assert.Equal(shape.Kind, Assert.Single(parsed.Document.Nodes).Kind);
            Assert.Equal(json, DocumentJson.Serialize(parsed.Document));
        }
    }

    [Fact]
    public void The_bpmn_edge_kinds_round_trip_through_the_dsl_without_a_lint_warning()
    {
        Node[] nodes =
        [
            new Node("n_a", "bpmn-user-task", "A", "custom:bpmn/user-task"),
            new Node("n_b", "bpmn-user-task", "B", "custom:bpmn/user-task"),
            new Node("n_note", "bpmn-annotation", "Note", "custom:bpmn/annotation"),
            new Node("n_data", "bpmn-data-object", "Data", "custom:bpmn/data-object"),
        ];
        Edge[] edges =
        [
            new Edge("e_1", "message-flow", new Endpoint("n_a"), new Endpoint("n_b")),
            new Edge("e_2", "bpmn-association", new Endpoint("n_note"), new Endpoint("n_a")),
            new Edge("e_3", "data-association", new Endpoint("n_data"), new Endpoint("n_a")),
        ];
        Document original = DocumentJsonTests.MinimalDocument() with { Type = DocumentType.Flowchart, Nodes = nodes, Edges = edges };

        ParseResult parsed = Dsl.Parse(Dsl.Print(original));

        Assert.Empty(parsed.Warnings);
        Assert.Equal(["message-flow", "bpmn-association", "data-association"], parsed.Document.Edges.Select(edge => edge.Kind).ToArray());
    }

    [Fact]
    public void A_process_drawn_as_intended_draws_no_critique_finding_and_a_task_in_a_lane_is_parented_correctly()
    {
        Document document = OrderProcess();

        Assert.Empty(CritiqueAnalyser.Analyse(document));
        // The task really is inside the lane, and the lane inside the pool.
        Assert.Equal("l_sales", document.Nodes.Single(node => node.Id == "n_check").Parent);
        Assert.Equal("p_shop", document.Nodes.Single(node => node.Id == "l_sales").Parent);
    }

    [Fact]
    public void A_task_parented_to_another_task_still_fires_non_container_parent()
    {
        Document document = OrderProcess() with
        {
            Nodes = [.. OrderProcess().Nodes, B("n_inner", "user-task") with { Parent = "n_check" }],
            Edges = [.. OrderProcess().Edges, E("e_inner", "n_inner", "n_end")],
        };

        Assert.Contains(CritiqueAnalyser.Analyse(document), f => f.Rule == CritiqueAnalyser.Rules.NonContainerParent && f.NodeIds.SequenceEqual(["n_inner", "n_check"]));
    }

    [Fact]
    public void A_task_the_start_event_cannot_reach_is_unreachable_and_an_activity_with_no_way_on_is_a_dead_end()
    {
        Document reachable = OrderProcess();
        Assert.DoesNotContain(CritiqueAnalyser.Analyse(reachable), f => f.Rule is CritiqueAnalyser.Rules.UnreachableNode or CritiqueAnalyser.Rules.DeadEnd);

        // n_orphan flows to the end but nothing flows to it from the start: a flow-connected task off the main path.
        Document orphaned = reachable with
        {
            Nodes = [.. reachable.Nodes, B("n_orphan", "service-task"), B("n_orphan_from", "service-task")],
            Edges = [.. reachable.Edges, E("e_o1", "n_orphan_from", "n_orphan"), E("e_o2", "n_orphan", "n_end")],
        };
        IReadOnlyList<CritiqueFinding> findings = CritiqueAnalyser.Analyse(orphaned);
        Assert.Contains(findings, f => f.Rule == CritiqueAnalyser.Rules.UnreachableNode && f.NodeIds.SequenceEqual(["n_orphan"]));
        Assert.Contains(findings, f => f.Rule == CritiqueAnalyser.Rules.UnreachableNode && f.NodeIds.SequenceEqual(["n_orphan_from"]));

        // A task reached from the start that then goes nowhere is a dead end.
        Document stopped = reachable with
        {
            Nodes = [.. reachable.Nodes, B("n_stop", "user-task")],
            Edges = [.. reachable.Edges, E("e_s", "n_check", "n_stop")],
        };
        Assert.Contains(CritiqueAnalyser.Analyse(stopped), f => f.Rule == CritiqueAnalyser.Rules.DeadEnd && f.NodeIds.SequenceEqual(["n_stop"]));
    }

    [Fact]
    public void An_error_boundary_event_starts_its_own_path_so_the_tasks_after_it_are_reachable()
    {
        Document document = OrderProcess();
        Node boundary = B("n_err", "intermediate-error");
        Node handler = B("n_handle", "manual-task");
        Node errEnd = B("n_err_end", "end");
        Document withBoundary = document with
        {
            Nodes = [.. document.Nodes, boundary, handler, errEnd],
            Edges = [.. document.Edges, E("e_b1", "n_err", "n_handle"), E("e_b2", "n_handle", "n_err_end")],
        };

        Assert.Empty(CritiqueAnalyser.Analyse(withBoundary));

        // Two-sided: the same handler behind an ordinary intermediate event that something flows into is judged by
        // reachability like any other node, and here nothing reaches it.
        Node feeder = B("n_feed", "service-task");
        Document notAnEntry = document with
        {
            Nodes = [.. document.Nodes, feeder, B("n_mid", "intermediate"), handler, errEnd],
            Edges = [.. document.Edges, E("e_c1", "n_feed", "n_mid"), E("e_c2", "n_mid", "n_handle"), E("e_c3", "n_handle", "n_err_end")],
        };
        Assert.Contains(CritiqueAnalyser.Analyse(notAnEntry), f => f.Rule == CritiqueAnalyser.Rules.UnreachableNode && f.NodeIds.SequenceEqual(["n_handle"]));
    }

    [Fact]
    public void A_loose_annotation_is_left_alone_but_a_loose_data_object_is_isolated()
    {
        Document document = OrderProcess();
        Document loose = document with { Nodes = [.. document.Nodes, B("n_loose_note", "annotation"), B("n_loose_data", "data-object")] };

        IReadOnlyList<CritiqueFinding> findings = CritiqueAnalyser.Analyse(loose);

        Assert.DoesNotContain(findings, f => f.NodeIds.Contains("n_loose_note"));
        Assert.Contains(findings, f => f.Rule == CritiqueAnalyser.Rules.IsolatedNode && f.NodeIds.SequenceEqual(["n_loose_data"]));
    }

    /// <summary>A small two-pool order process: the customer starts it with a message, the shop's sales lane checks stock
    /// and a gateway decides. Every node is placed the way the vocabulary teaches.</summary>
    private static Document OrderProcess()
    {
        Node[] nodes =
        [
            new Node("p_shop", "pool", "Shop", "custom:swimlane/pool"),
            new Node("l_sales", "lane", "Sales", "custom:swimlane/lane") { Parent = "p_shop" },
            B("n_start", "start-message") with { Parent = "l_sales" },
            B("n_check", "service-task") with { Parent = "l_sales" },
            B("n_gw", "exclusive-gateway") with { Parent = "l_sales" },
            B("n_ship", "user-task") with { Parent = "l_sales" },
            B("n_end", "end") with { Parent = "l_sales" },
            B("n_stock", "data-store"),
            B("n_note", "annotation"),
        ];
        Edge[] edges =
        [
            E("e_1", "n_start", "n_check"),
            E("e_2", "n_check", "n_gw"),
            E("e_3", "n_gw", "n_ship", label: "In stock"),
            E("e_4", "n_gw", "n_end", label: "Out of stock"),
            E("e_5", "n_ship", "n_end"),
            E("e_6", "n_stock", "n_check", "data-association"),
            E("e_7", "n_note", "n_gw", "bpmn-association"),
        ];
        return new Document("doc_bpmn", Document.CurrentSchemaVersion, "Order", DocumentType.Flowchart, nodes, edges, new Meta(Now, Now, "test"));
    }

    private static Node B(string id, string name)
    {
        BpmnShape shape = BpmnShapeLibrary.All.Single(candidate => candidate.Name == name);
        return new Node(id, shape.Kind, shape.Label, BpmnShapeLibrary.ShapeName(shape));
    }

    private static Edge E(string id, string from, string to, string kind = "flow", string? label = null)
    {
        return new Edge(id, kind, new Endpoint(from), new Endpoint(to)) { Label = label };
    }
}
