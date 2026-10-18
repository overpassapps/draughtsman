using System.Text.Json;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests;

/// <summary>The org-chart box library is data shared by both renderers; it must stay well formed, round-trip (labels on
/// two lines included), and line up with the architecture kinds that name a box as their default shape.</summary>
public class OrgShapeLibraryTests
{
    [Fact]
    public void The_library_covers_the_batch_s_box_set_in_order()
    {
        string[] expected = ["role", "executive", "assistant", "vacancy"];

        Assert.Equal(expected, OrgShapeLibrary.All.Select(shape => shape.Name).ToArray());
    }

    [Fact]
    public void Every_box_has_a_unique_well_formed_name_labels_and_a_known_outline()
    {
        Assert.NotEmpty(OrgShapeLibrary.All);
        string[] outlines = ["solid", "double", "dashed"];
        var names = new List<string>();
        foreach (OrgShape shape in OrgShapeLibrary.All)
        {
            Assert.Matches("^[A-Za-z0-9_.-]+$", shape.Name);
            Assert.False(string.IsNullOrWhiteSpace(shape.Label));
            Assert.False(string.IsNullOrWhiteSpace(shape.Kind));
            // A role box shows name and title on two lines; every box's placed label carries that newline.
            Assert.Contains('\n', shape.DefaultLabel);
            Assert.Contains(shape.Outline, outlines);
            foreach (JsonElement element in shape.Elements)
            {
                Assert.Equal("line", element.GetProperty("tag").GetString());
            }

            names.Add(shape.Name);
        }

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("custom:org/role", true)]
    [InlineData("custom:org/vacancy", true)]
    [InlineData("custom:org/not-a-real-box", false)]
    [InlineData("custom:other/role", false)]
    [InlineData("custom:generic/role", false)]
    [InlineData("rectangle", false)]
    public void Only_a_name_in_the_library_is_an_org_box(string shape, bool expected)
    {
        Assert.Equal(expected, OrgShapeLibrary.TryGet(shape, out _));
    }

    [Fact]
    public void Every_box_round_trips_through_json_and_dsl_in_both_the_shape_and_its_kind()
    {
        foreach (OrgShape box in OrgShapeLibrary.All)
        {
            string shape = OrgShapeLibrary.Prefix + box.Name;
            Document original = DocumentJsonTests.MinimalDocument() with
            {
                Type = DocumentType.Architecture,
                Nodes = [new Node("n_org", box.Kind, box.DefaultLabel, shape)],
            };
            string json = DocumentJson.Serialize(original);
            Assert.True(ContractFiles.Evaluate(json).IsValid, $"{shape} is not admitted by the schema");

            Document restored = DocumentJson.Deserialize(json);
            Assert.Equal(shape, Assert.Single(restored.Nodes).Shape);
            Assert.Equal(box.Kind, Assert.Single(restored.Nodes).Kind);
            Assert.Equal(box.DefaultLabel, Assert.Single(restored.Nodes).Label);

            // No warnings: the box kind is a real architecture kind whose own default shape is the box.
            ParseResult parsed = Dsl.Parse(Dsl.Print(restored));
            Assert.Empty(parsed.Warnings);
            Assert.Equal(shape, Assert.Single(parsed.Document.Nodes).Shape);
            Assert.Equal(box.Kind, Assert.Single(parsed.Document.Nodes).Kind);
            // The two-line label survives the DSL's quoted-string round trip.
            Assert.Equal(box.DefaultLabel, Assert.Single(parsed.Document.Nodes).Label);
            Assert.Equal(json, DocumentJson.Serialize(parsed.Document));
        }
    }

    [Fact]
    public void A_role_box_kind_default_shape_is_elided_by_the_printer_and_dsl_restores_it()
    {
        // Two-sided: because every org kind's own default shape is its box, the printer never writes shape=; a
        // presentational custom:org shape on a non-org kind would be written out instead.
        Document document = DocumentJsonTests.MinimalDocument() with
        {
            Type = DocumentType.Architecture,
            Nodes = [new Node("n_org", "role", "Name\nTitle", "custom:org/role")],
        };
        string text = Dsl.Print(document);
        Assert.DoesNotContain("shape=", text, StringComparison.Ordinal);
        Assert.Equal("custom:org/role", Assert.Single(Dsl.Parse(text).Document.Nodes).Shape);

        Document presentation = document with { Nodes = [new Node("n_org", "service", "Name\nTitle", "custom:org/role")] };
        Assert.Contains("shape=custom:org/role", Dsl.Print(presentation), StringComparison.Ordinal);
    }

    [Fact]
    public void An_org_kind_default_shape_names_its_box_and_no_other_kind_claims_an_org_shape()
    {
        KindVocabulary architecture = KindVocabulary.For(DocumentType.Architecture);
        HashSet<string> declared = OrgShapeLibrary.All.Select(shape => shape.Kind).ToHashSet(StringComparer.Ordinal);
        HashSet<string> actual = architecture.NodeKinds
            .Where(kind => kind.DefaultShape?.StartsWith(OrgShapeLibrary.Prefix, StringComparison.Ordinal) == true)
            .Select(kind => kind.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Equal sets, both ways: a kind added without a matching box fails, and so does a box kind with no vocabulary.
        Assert.Equal(declared, actual);

        foreach (OrgShape box in OrgShapeLibrary.All)
        {
            KindDefinition? kind = architecture.NodeKinds.FirstOrDefault(kind => kind.Name == box.Kind);
            Assert.NotNull(kind);
            Assert.Equal(OrgShapeLibrary.Prefix + box.Name, kind!.DefaultShape);
            Assert.False(string.IsNullOrWhiteSpace(kind.Description));
        }
    }

    [Fact]
    public void Placing_every_box_and_connecting_it_draws_no_critique_finding_about_the_box()
    {
        foreach (OrgShape box in OrgShapeLibrary.All)
        {
            var orgNode = new Node("n_org", box.Kind, box.DefaultLabel, OrgShapeLibrary.Prefix + box.Name)
            {
                Geometry = new Geometry(0, 0, 160, 80),
            };
            var partner = new Node("n_boss", "role", "Boss\nName", "custom:org/role") { Geometry = new Geometry(0, 200, 160, 80) };
            var edge = new Edge("e_1", "dotted-report", new Endpoint("n_org"), new Endpoint("n_boss"));
            Document document = DocumentJsonTests.MinimalDocument() with
            {
                Type = DocumentType.Architecture,
                Nodes = [orgNode, partner],
                Edges = [edge],
            };

            // A connected org box is not isolated, and no other rule keys on its kind, so the review is silent.
            IReadOnlyList<CritiqueFinding> findings = CritiqueAnalyser.Analyse(document);
            Assert.Empty(findings);
        }
    }

    [Fact]
    public void The_raw_library_json_is_available_verbatim()
    {
        string json = OrgShapeLibrary.RawJson;

        Assert.Contains("\"org\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"{OrgShapeLibrary.All[0].Name}\"", json, StringComparison.Ordinal);
    }
}
