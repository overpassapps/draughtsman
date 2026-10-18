using Draughtsman.Core.Critique;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests;

/// <summary>The generic icon library is data shared by both renderers and taught to agents through get_schema; it must
/// stay well formed, round-trip, and line up with the architecture kinds that name an icon as their default shape.</summary>
public class GenericIconLibraryTests
{
    [Fact]
    public void The_library_covers_the_batch_s_icon_set_in_order()
    {
        string[] expected =
        [
            "server", "cloud", "user", "users", "mobile", "browser", "desktop", "load-balancer", "api-gateway",
            "cache", "cdn", "firewall", "object-storage", "function", "pod", "cluster", "message-stream",
            "search-index", "monitoring", "key", "email", "scheduler",
            "router", "switch", "access-point", "workstation", "modem", "hub",
            "vpc", "subnet", "availability-zone", "auto-scaling-group", "container-image", "ci-cd", "vpn", "dns",
            "deployment", "ingress", "configmap", "persistent-volume", "namespace",
        ];

        Assert.Equal(expected, GenericIconLibrary.All.Select(icon => icon.Name).ToArray());
    }

    [Fact]
    public void Every_icon_has_a_unique_well_formed_name_and_at_least_one_primitive()
    {
        Assert.NotEmpty(GenericIconLibrary.All);
        var names = new List<string>();
        foreach (GenericIcon icon in GenericIconLibrary.All)
        {
            Assert.Matches("^[A-Za-z0-9_.-]+$", icon.Name);
            Assert.False(string.IsNullOrWhiteSpace(icon.Label));
            Assert.NotEmpty(icon.Elements);
            foreach (System.Text.Json.JsonElement element in icon.Elements)
            {
                Assert.False(string.IsNullOrWhiteSpace(element.GetProperty("tag").GetString()));
            }

            names.Add(icon.Name);
        }

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("custom:generic/server", true)]
    [InlineData("custom:generic/load-balancer", true)]
    [InlineData("custom:generic/not-a-real-icon", false)]
    [InlineData("custom:other/server", false)]
    [InlineData("rectangle", false)]
    public void Only_a_name_in_the_library_is_a_generic_icon(string shape, bool expected)
    {
        Assert.Equal(expected, GenericIconLibrary.TryGet(shape, out _));
    }

    [Fact]
    public void Every_icon_round_trips_through_json_and_dsl_in_both_the_shape_and_its_palette_kind()
    {
        foreach (GenericIcon icon in GenericIconLibrary.All)
        {
            string shape = GenericIconLibrary.Prefix + icon.Name;
            Document original = DocumentJsonTests.MinimalDocument() with
            {
                Type = DocumentType.Architecture,
                Nodes = [new Node("n_icon", icon.PaletteKind, icon.Label, shape)],
            };
            string json = DocumentJson.Serialize(original);
            Assert.True(ContractFiles.Evaluate(json).IsValid, $"{shape} is not admitted by the schema");

            Document restored = DocumentJson.Deserialize(json);
            Assert.Equal(shape, Assert.Single(restored.Nodes).Shape);
            Assert.Equal(icon.PaletteKind, Assert.Single(restored.Nodes).Kind);

            // No warnings: the palette kind is a real architecture kind, so the unknown-kind lint never fires.
            ParseResult parsed = Dsl.Parse(Dsl.Print(restored));
            Assert.Empty(parsed.Warnings);
            Assert.Equal(shape, Assert.Single(parsed.Document.Nodes).Shape);
            Assert.Equal(icon.PaletteKind, Assert.Single(parsed.Document.Nodes).Kind);
            Assert.Equal(json, DocumentJson.Serialize(parsed.Document));
        }
    }

    [Fact]
    public void An_icon_with_its_own_kind_prints_by_kind_and_a_presentation_only_icon_prints_its_shape()
    {
        foreach (GenericIcon icon in GenericIconLibrary.All)
        {
            string shape = GenericIconLibrary.Prefix + icon.Name;
            Document document = DocumentJsonTests.MinimalDocument() with
            {
                Type = DocumentType.Architecture,
                Nodes = [new Node("n_icon", icon.PaletteKind, icon.Label, shape)],
            };
            string text = Dsl.Print(document);

            // Two-sided: a kind's own default shape is elided by the printer, a presentational shape is written out.
            if (icon.Kind is null)
            {
                Assert.Contains("shape=", text, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("shape=", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Every_palette_kind_is_a_real_architecture_kind_so_no_icon_trips_the_unknown_kind_lint()
    {
        KindVocabulary architecture = KindVocabulary.For(DocumentType.Architecture);

        foreach (GenericIcon icon in GenericIconLibrary.All)
        {
            Assert.True(architecture.IsKnownNodeKind(icon.PaletteKind), $"{icon.Name} places unknown kind '{icon.PaletteKind}'");
            Assert.NotEqual("shape", icon.PaletteKind);
            if (icon.Kind is not null)
            {
                // An icon that is a kind's own default shape is placed with that same kind.
                Assert.Equal(icon.Kind, icon.PaletteKind);
            }
        }
    }

    [Fact]
    public void Placing_every_icon_and_connecting_it_draws_no_critique_finding_about_the_icon()
    {
        foreach (GenericIcon icon in GenericIconLibrary.All)
        {
            var iconNode = new Node("n_icon", icon.PaletteKind, icon.Label, GenericIconLibrary.Prefix + icon.Name)
            {
                Geometry = new Geometry(0, 0, 160, 80),
            };
            var partner = new Node("n_partner", "service", "Partner", "rounded") { Geometry = new Geometry(320, 0, 160, 80) };
            var edge = new Edge("e_1", "flow", new Endpoint("n_icon"), new Endpoint("n_partner"));
            var nodes = new List<Node> { iconNode, partner };
            // A boundary icon is placed with the container kind, so give it a decorative child: an empty container is a
            // real finding this test is not about, and a note is skipped by the isolated-node rule.
            if (icon.PaletteKind == "container")
            {
                nodes.Add(new Node("n_child", "note", "Inside", "rectangle") { Geometry = new Geometry(20, 20, 80, 40), Parent = "n_icon" });
            }

            Document document = DocumentJsonTests.MinimalDocument() with
            {
                Type = DocumentType.Architecture,
                Nodes = nodes,
                Edges = [edge],
            };

            // A connected icon is not isolated, and no other rule keys on its palette kind, so the review is silent.
            IReadOnlyList<CritiqueFinding> findings = CritiqueAnalyser.Analyse(document);
            Assert.Empty(findings);
        }
    }

    [Fact]
    public void The_three_cloud_network_boundaries_are_the_only_icons_that_draw_as_boundaries()
    {
        HashSet<string> boundaries = GenericIconLibrary.All
            .Where(icon => icon.Boundary)
            .Select(icon => icon.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new[] { "vpc", "subnet", "availability-zone" }.ToHashSet(StringComparer.Ordinal), boundaries);

        // Two-sided: every boundary is placed with the container kind (so it is a valid parent and labels top-left),
        // and no other icon is.
        foreach (GenericIcon icon in GenericIconLibrary.All)
        {
            Assert.Equal(icon.Boundary, icon.PaletteKind == "container");
        }
    }

    [Fact]
    public void An_icon_kind_default_shape_names_its_icon_and_no_other_kind_claims_an_icon_shape()
    {
        KindVocabulary architecture = KindVocabulary.For(DocumentType.Architecture);
        HashSet<string> declared = GenericIconLibrary.All
            .Where(icon => icon.Kind is not null)
            .Select(icon => icon.Kind!)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> actual = architecture.NodeKinds
            .Where(kind => kind.DefaultShape?.StartsWith(GenericIconLibrary.Prefix, StringComparison.Ordinal) == true)
            .Select(kind => kind.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Equal sets, both ways: a kind added without a matching icon entry fails, and so does an icon kind with no
        // vocabulary entry.
        Assert.Equal(declared, actual);

        foreach (GenericIcon icon in GenericIconLibrary.All.Where(icon => icon.Kind is not null))
        {
            KindDefinition? kind = architecture.NodeKinds.FirstOrDefault(kind => kind.Name == icon.Kind);
            Assert.NotNull(kind);
            Assert.Equal(GenericIconLibrary.Prefix + icon.Name, kind!.DefaultShape);
            Assert.False(string.IsNullOrWhiteSpace(kind.Description));
        }
    }

    [Fact]
    public void The_raw_library_json_is_available_for_agents_verbatim()
    {
        string json = GenericIconLibrary.RawJson;

        Assert.Contains("\"generic\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"{GenericIconLibrary.All[0].Name}\"", json, StringComparison.Ordinal);
    }
}
