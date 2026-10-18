using Draughtsman.Core.Critique;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests;

/// <summary>The state-machine shape library is data shared by the palette, the DSL and both renderers; it must stay
/// well formed, round-trip, and line up with the flowchart kinds that name a state shape as their default.</summary>
public class StateShapeLibraryTests
{
    [Fact]
    public void The_library_covers_the_batch_s_shape_set_in_order()
    {
        string[] expected =
        [
            "initial", "final", "choice", "fork-join", "shallow-history", "deep-history", "entry-point", "exit-point",
            "composite-state",
        ];

        Assert.Equal(expected, StateShapeLibrary.All.Select(shape => shape.Name).ToArray());
    }

    [Fact]
    public void Every_shape_has_a_unique_well_formed_name_and_a_label()
    {
        Assert.NotEmpty(StateShapeLibrary.All);
        var names = new List<string>();
        foreach (StateShape shape in StateShapeLibrary.All)
        {
            Assert.Matches("^[A-Za-z0-9_.-]+$", shape.Name);
            Assert.False(string.IsNullOrWhiteSpace(shape.Label));
            names.Add(shape.Name);
        }

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("custom:state/initial", true)]
    [InlineData("custom:state/composite-state", true)]
    [InlineData("custom:state/not-a-real-shape", false)]
    [InlineData("custom:generic/server", false)]
    [InlineData("custom:state", false)]
    [InlineData("rounded", false)]
    public void Only_a_name_in_the_library_is_a_state_shape(string shape, bool expected)
    {
        Assert.Equal(expected, StateShapeLibrary.IsStateShape(shape));
    }

    [Fact]
    public void Every_shape_round_trips_through_json_and_dsl_in_both_the_shape_and_its_kind()
    {
        foreach (StateShape shape in StateShapeLibrary.All)
        {
            string name = StateShapeLibrary.ShapeName(shape);
            Document original = DocumentJsonTests.MinimalDocument() with
            {
                Type = DocumentType.Flowchart,
                Nodes = [new Node("n_state", shape.Kind, shape.Label, name)],
            };
            string json = DocumentJson.Serialize(original);
            Assert.True(ContractFiles.Evaluate(json).IsValid, $"{name} is not admitted by the schema");

            Document restored = DocumentJson.Deserialize(json);
            Assert.Equal(name, Assert.Single(restored.Nodes).Shape);
            Assert.Equal(shape.Kind, Assert.Single(restored.Nodes).Kind);

            // No warnings: the kind is a real flowchart kind, so the unknown-kind lint never fires.
            ParseResult parsed = Dsl.Parse(Dsl.Print(restored));
            Assert.Empty(parsed.Warnings);
            Assert.Equal(name, Assert.Single(parsed.Document.Nodes).Shape);
            Assert.Equal(shape.Kind, Assert.Single(parsed.Document.Nodes).Kind);
            Assert.Equal(json, DocumentJson.Serialize(parsed.Document));
        }
    }

    [Fact]
    public void Every_shape_is_a_kind_s_own_default_shape_so_the_printer_elides_the_shape()
    {
        KindVocabulary flowchart = KindVocabulary.For(DocumentType.Flowchart);
        foreach (StateShape shape in StateShapeLibrary.All)
        {
            // Two-sided: the shape is elided (it is the kind's default) and the kind name still prints in its place.
            Assert.Equal(shape.Kind, shape.PaletteKind);
            KindDefinition? kind = flowchart.NodeKinds.FirstOrDefault(candidate => candidate.Name == shape.Kind);
            Assert.NotNull(kind);
            Assert.Equal(StateShapeLibrary.ShapeName(shape), kind!.DefaultShape);
            Assert.False(string.IsNullOrWhiteSpace(kind.Description));

            Document document = DocumentJsonTests.MinimalDocument() with
            {
                Type = DocumentType.Flowchart,
                Nodes = [new Node("n_state", shape.Kind, shape.Label, StateShapeLibrary.ShapeName(shape))],
            };
            string text = Dsl.Print(document);
            Assert.DoesNotContain("shape=", text, StringComparison.Ordinal);
            Assert.Contains(shape.Kind, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Placing_every_shape_and_connecting_it_draws_no_critique_finding_about_the_shape()
    {
        foreach (StateShape shape in StateShapeLibrary.All)
        {
            var shapeNode = new Node("n_state", shape.Kind, shape.Label, StateShapeLibrary.ShapeName(shape))
            {
                Geometry = new Geometry(0, 0, 160, 80),
            };
            var partner = new Node("n_partner", "process", "Partner", "rounded") { Geometry = new Geometry(320, 0, 160, 80) };
            var edge = new Edge("e_1", "flow", new Endpoint("n_state"), new Endpoint("n_partner"));
            Document document = DocumentJsonTests.MinimalDocument() with
            {
                Type = DocumentType.Flowchart,
                Nodes = [shapeNode, partner],
                Edges = [edge],
            };

            // A connected state shape is not isolated and no other rule keys on its kind, so the review is silent.
            Assert.Empty(CritiqueAnalyser.Analyse(document));
        }
    }

    [Fact]
    public void The_composite_state_is_a_container_so_a_child_does_not_trip_the_non_container_parent_rule()
    {
        var composite = new Node("n_cs", "composite-state", "Order", "custom:state/composite-state") { Geometry = new Geometry(0, 0, 320, 240) };
        var child = composite with { Id = "n_a", Kind = "process", Label = "Verify", Shape = "rounded", Geometry = new Geometry(40, 60, 160, 80), Parent = "n_cs" };

        IReadOnlyList<CritiqueFinding> findings = CritiqueAnalyser.Analyse(DocumentJsonTests.MinimalDocument() with
        {
            Type = DocumentType.Flowchart,
            Nodes = [composite, child],
        });

        Assert.DoesNotContain(findings, finding => finding.Rule == CritiqueAnalyser.Rules.NonContainerParent);

        // The other direction: a plain process parent is still refused, so the rule is not simply disabled.
        var process = composite with { Id = "n_p", Kind = "process", Label = "Step", Shape = "rounded", Parent = null };
        var orphan = child with { Id = "n_b", Parent = "n_p" };
        IReadOnlyList<CritiqueFinding> refused = CritiqueAnalyser.Analyse(DocumentJsonTests.MinimalDocument() with
        {
            Type = DocumentType.Flowchart,
            Nodes = [process, orphan],
        });

        CritiqueFinding finding = Assert.Single(refused, candidate => candidate.Rule == CritiqueAnalyser.Rules.NonContainerParent);
        Assert.Equal(["n_b", "n_p"], finding.NodeIds);
    }

    [Fact]
    public void The_composite_state_kind_is_a_real_flowchart_kind()
    {
        KindVocabulary flowchart = KindVocabulary.For(DocumentType.Flowchart);

        Assert.True(flowchart.IsKnownNodeKind(CritiqueAnalyser.CompositeState), "flowchart vocabulary lacks composite-state");
    }

    [Fact]
    public void The_raw_library_json_is_available_for_agents_verbatim()
    {
        string json = StateShapeLibrary.RawJson;

        Assert.Contains("\"state\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"{StateShapeLibrary.All[0].Name}\"", json, StringComparison.Ordinal);
    }
}
