using Draughtsman.Core.Critique;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests;

/// <summary>The basic shape library is data shared by the palette, the DSL and both renderers; it must stay well formed, resolve to
/// the same numbers as its web twin (web/src/model/basicShapes.ts), round-trip, and place on kinds the vocabularies know.</summary>
public class BasicShapeLibraryTests
{
    [Fact]
    public void The_library_covers_the_set_in_order()
    {
        string[] expected =
        [
            "ellipse", "triangle", "trapezoid", "chevron", "cross", "arrow", "double-arrow", "note", "callout", "brace", "cube", "folder",
            "component",
        ];

        Assert.Equal(expected, BasicShapeLibrary.All.Select(shape => shape.Name).ToArray());
    }

    [Fact]
    public void Every_shape_has_a_unique_well_formed_name_a_label_and_a_size()
    {
        var names = new List<string>();
        foreach (BasicShape shape in BasicShapeLibrary.All)
        {
            Assert.Matches("^[A-Za-z0-9_.-]+$", shape.Name);
            Assert.False(string.IsNullOrWhiteSpace(shape.Label));
            Assert.True(shape.DefaultWidth > 0 && shape.DefaultHeight > 0, shape.Name);
            names.Add(shape.Name);
        }

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("custom:basic/ellipse", true)]
    [InlineData("custom:basic/component", true)]
    [InlineData("custom:basic/not-a-real-shape", false)]
    [InlineData("custom:state/initial", false)]
    [InlineData("custom:basic", false)]
    [InlineData("ellipse", false)]
    public void Only_a_name_in_the_library_is_a_basic_shape(string shape, bool expected)
    {
        Assert.Equal(expected, BasicShapeLibrary.IsBasicShape(shape));
    }

    [Fact]
    public void Every_shape_describes_itself_well_enough_to_draw_it()
    {
        foreach (BasicShape shape in BasicShapeLibrary.All)
        {
            Assert.NotEmpty(shape.Parts);
            foreach (BasicShapePart part in shape.Parts)
            {
                // Throws for a command the library does not understand or a wrong number of coordinates.
                Assert.StartsWith("M", BasicShapeGeometry.ResolvePath(part.D, 0, 0, shape.DefaultWidth, shape.DefaultHeight), StringComparison.Ordinal);
            }

            (double left, double top, double right, double bottom) = BasicShapeGeometry.ResolveBox(shape.LabelBox, shape.DefaultWidth, shape.DefaultHeight);
            Assert.True(right > left && bottom > top, $"{shape.Name} label box is empty");
            Assert.True(left >= 0 && top >= 0 && right <= shape.DefaultWidth && bottom <= shape.DefaultHeight, $"{shape.Name} label box leaves the box");

            Assert.NotEmpty(shape.Ports);
            foreach (BasicShapePort port in shape.Ports)
            {
                Assert.Contains(port.Id, new[] { "top", "right", "bottom", "left" });
                Assert.InRange(port.X, 0, 1);
                Assert.InRange(port.Y, 0, 1);
            }
        }

        // Only the brace is a line: every other outline is filled.
        Assert.All(BasicShapeLibrary.All, shape => Assert.Equal(shape.Name != "brace", shape.Parts[0].Fill));
    }

    [Theory]
    [InlineData("0.5", 200, 100)]
    [InlineData("1", 200, 200)]
    [InlineData("0", 200, 0)]
    [InlineData("1-16", 200, 184)]
    [InlineData("0+8", 200, 8)]
    [InlineData("0.75-4", 80, 56)]
    [InlineData("-0.25", 200, -50)]
    [InlineData("1-16", 20, 10)]
    [InlineData("0+24", 30, 15)]
    public void A_coordinate_is_a_fraction_of_the_extent_plus_or_minus_a_fixed_length_never_over_half(string token, double extent, double expected)
    {
        Assert.Equal(expected, BasicShapeGeometry.ResolveCoordinate(token, extent));
    }

    /// <summary>The same literals as the web twin's test (web/src/model/basicShapes.test.ts), so the two cannot drift apart.</summary>
    [Fact]
    public void A_path_resolves_to_the_same_literals_as_the_web_twin()
    {
        Assert.Equal("M50,0 L100,80 L0,80 Z", BasicShapeGeometry.ResolvePath("M0.5,0 L1,1 L0,1 Z", 0, 0, 100, 80));
        Assert.Equal("M0,0 L124,0 L140,16 L140,90 L0,90 Z", BasicShapeGeometry.ResolvePath("M0,0 L1-16,0 L1,0+16 L1,1 L0,1 Z", 0, 0, 140, 90));
        Assert.Equal("M0,0 H64 V18 H160 V100 H0 Z", BasicShapeGeometry.ResolvePath("M0,0 H0.4 V0+18 H1 V1 H0 Z", 0, 0, 160, 100));
        Assert.Equal("M33.33,0 L66.67,33.33", BasicShapeGeometry.ResolvePath("M0.3333,0 L0.6667,0.3333", 0, 0, 100, 100));
        Assert.Equal("M0,30 C24,6 48,54 120,30", BasicShapeGeometry.ResolvePath("M0,0.5 C0.2,0.1 0.4,0.9 1,0.5", 0, 0, 120, 60));

        // Rounded half up (JavaScript's Math.round), not to even: 12.525 and 12.375 both go up.
        Assert.Equal("M12.53,0", BasicShapeGeometry.ResolvePath("M0.125,0", 0, 0, 100.2, 10));
        Assert.Equal("M12.38,0", BasicShapeGeometry.ResolvePath("M0.375,0", 0, 0, 33, 10));
    }

    [Fact]
    public void A_path_is_placed_at_the_boxs_top_left_corner()
    {
        Assert.Equal("M82,52 L132,132 L32,132 Z", BasicShapeGeometry.ResolvePath("M0.5,0 L1,1 L0,1 Z", 32, 52, 100, 80));
    }

    [Fact]
    public void A_path_with_an_unknown_command_or_the_wrong_number_of_coordinates_is_refused()
    {
        Assert.Throws<FormatException>(() => BasicShapeGeometry.ResolvePath("M0,0 Q1,1 2,2", 0, 0, 10, 10));
        Assert.Throws<FormatException>(() => BasicShapeGeometry.ResolvePath("M0,0 L1", 0, 0, 10, 10));
        Assert.Throws<FormatException>(() => BasicShapeGeometry.ResolvePath("Z 1", 0, 0, 10, 10));
    }

    [Fact]
    public void Every_shape_round_trips_through_json_and_dsl_on_a_kind_its_vocabulary_knows()
    {
        foreach (DocumentType type in new[] { DocumentType.Flowchart, DocumentType.Architecture })
        {
            KindVocabulary vocabulary = KindVocabulary.For(type);
            foreach (BasicShape shape in BasicShapeLibrary.All)
            {
                string kind = shape.Annotation ? "note" : type == DocumentType.Flowchart ? "process" : "service";
                Assert.True(vocabulary.IsKnownNodeKind(kind), $"{type} has no kind {kind}");

                string name = BasicShapeLibrary.ShapeName(shape);
                Document original = DocumentJsonTests.MinimalDocument() with
                {
                    Type = type,
                    Nodes = [new Node("n_basic", kind, shape.Label, name)],
                };
                string json = DocumentJson.Serialize(original);
                Assert.True(ContractFiles.Evaluate(json).IsValid, $"{name} is not admitted by the schema");

                // The kind's own default shape is not this one, so the printer writes the shape out and the parser reads it back.
                string text = Dsl.Print(DocumentJson.Deserialize(json));
                Assert.Contains(name, text, StringComparison.Ordinal);
                ParseResult parsed = Dsl.Parse(text);
                Assert.Empty(parsed.Warnings);
                Assert.Equal(name, Assert.Single(parsed.Document.Nodes).Shape);
                Assert.Equal(kind, Assert.Single(parsed.Document.Nodes).Kind);
                Assert.Equal(json, DocumentJson.Serialize(parsed.Document));
            }
        }
    }

    [Fact]
    public void Placing_every_shape_and_connecting_it_draws_no_critique_finding_about_the_shape()
    {
        foreach (BasicShape shape in BasicShapeLibrary.All)
        {
            var shapeNode = new Node("n_basic", shape.Annotation ? "note" : "process", shape.Label, BasicShapeLibrary.ShapeName(shape))
            {
                Geometry = new Geometry(0, 0, shape.DefaultWidth, shape.DefaultHeight),
            };
            var partner = new Node("n_partner", "process", "Partner", "rounded") { Geometry = new Geometry(320, 0, 160, 80) };
            var edge = new Edge("e_1", "flow", new Endpoint("n_basic"), new Endpoint("n_partner"));
            Document document = DocumentJsonTests.MinimalDocument() with
            {
                Type = DocumentType.Flowchart,
                Nodes = [shapeNode, partner],
                Edges = [edge],
            };

            Assert.Empty(CritiqueAnalyser.Analyse(document));
        }
    }

    [Fact]
    public void The_raw_library_json_is_available_for_agents_verbatim()
    {
        string json = BasicShapeLibrary.RawJson;

        Assert.Contains("\"basic\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"{BasicShapeLibrary.All[0].Name}\"", json, StringComparison.Ordinal);
    }
}
