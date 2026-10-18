using System.Text.Json;
using Draughtsman.Core.Diffing;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Core.Tests.Diffing;

/// <summary>The sentences a pull request shows: labels not ids, one line per field, layout collapsible, volatile lines kept out.</summary>
public class SemanticDiffTests
{
    private static Document Order()
    {
        return DocumentJson.Deserialize(ContractFiles.ReadExample());
    }

    private static Document EditNode(Document document, string id, Func<Node, Node> edit)
    {
        return document with { Nodes = [.. document.Nodes.Select(n => n.Id == id ? edit(n) : n)] };
    }

    private static Document EditEdge(Document document, string id, Func<Edge, Edge> edit)
    {
        return document with { Edges = [.. document.Edges.Select(e => e.Id == id ? edit(e) : e)] };
    }

    private static JsonElement Json(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static IReadOnlyList<string> Sentences(Document from, Document to, bool ignoreLayout = false)
    {
        return SemanticDiff.Describe(from, to, ignoreLayout).Changes.Select(c => c.Text).ToList();
    }

    [Fact]
    public void A_relabelled_edge_is_reported_by_label_never_by_id()
    {
        Document before = Order();
        Document after = EditEdge(before, "e_2", e => e with { Label = "reads" });

        SemanticDiffReport report = SemanticDiff.Describe(before, after, ignoreLayout: false);

        DiffEntry entry = Assert.Single(report.Changes);
        Assert.Equal("Edge `Orders API` to `Postgres` relabelled from `read/write` to `reads`", entry.Text);
        Assert.Equal("e_2", entry.ElementId);
        Assert.True(report.HasChanges);
    }

    [Fact]
    public void A_relabelled_node_is_named_by_its_old_label_and_no_sentence_carries_an_id()
    {
        Document before = Order();
        Document after = EditEdge(EditNode(before, "n_api", n => n with { Label = "Orders service" }), "e_2", e => e with { Label = "reads" });

        SemanticDiffReport report = SemanticDiff.Describe(before, after, ignoreLayout: false);

        Assert.Contains("Node `Orders API` relabelled to `Orders service`", report.Changes.Select(c => c.Text));
        string everything = SemanticDiff.ToMarkdown(report);
        foreach (string id in before.Nodes.Select(n => n.Id).Concat(before.Edges.Select(e => e.Id)))
        {
            Assert.DoesNotContain(id, everything);
        }

        Assert.Contains("Orders service", everything);
    }

    [Fact]
    public void Added_and_removed_elements_are_named_by_label()
    {
        Document before = Order();
        Node billing = new("n_billing", "service", "Billing", "rect") { Geometry = new Geometry(600, 40, 160, 80) };
        Edge call = new("e_9", "flow", new Endpoint("n_web"), new Endpoint("n_billing")) { Label = "invoices" };
        Document after = (before with { Nodes = [.. before.Nodes, billing], Edges = [.. before.Edges.Where(e => e.Id != "e_3"), call] });

        IReadOnlyList<string> sentences = Sentences(before, after);

        Assert.Contains("Added node `Billing` (`service`)", sentences);
        Assert.Contains("Added `flow` edge `Web app` to `Billing` labelled `invoices`", sentences);
        Assert.Contains("Removed `data` edge `Orders API` to `Redis`", sentences);
    }

    [Fact]
    public void Only_layout_moved_the_default_reports_the_moves_and_ignore_layout_collapses_them()
    {
        Document before = Order();
        Document after = EditEdge(
            EditNode(EditNode(before, "n_web", n => n with { Geometry = n.Geometry! with { X = 100, Y = 90 } }), "n_db", n => n with { Geometry = n.Geometry! with { W = 200 } }),
            "e_3",
            e => e with { Routing = e.Routing! with { Waypoints = [new Point(500, 260)] } });

        SemanticDiffReport detailed = SemanticDiff.Describe(before, after, ignoreLayout: false);
        SemanticDiffReport collapsed = SemanticDiff.Describe(before, after, ignoreLayout: true);

        Assert.Equal(
            [
                "Node `Web app` moved from (40, 40) to (100, 90)",
                "Node `Postgres` resized from 160x80 to 200x80",
                "Edge `Orders API` to `Redis` rerouted (waypoints changed)",
            ],
            detailed.Changes.Select(c => c.Text));
        Assert.True(detailed.HasChanges);
        Assert.Equal(0, detailed.LayoutOnlyChanges);

        Assert.Empty(collapsed.Changes);
        Assert.False(collapsed.HasChanges);
        Assert.Equal(3, collapsed.LayoutOnlyChanges);
        Assert.Contains("No semantic changes; 3 layout-only changes.", SemanticDiff.ToMarkdown(collapsed));
    }

    [Fact]
    public void Ignore_layout_still_lists_a_semantic_change_made_alongside_the_layout()
    {
        Document before = Order();
        Document after = EditNode(before, "n_web", n => n with { Label = "Storefront", Geometry = n.Geometry! with { X = 99 } });

        SemanticDiffReport report = SemanticDiff.Describe(before, after, ignoreLayout: true);

        Assert.Equal(["Node `Web app` relabelled to `Storefront`"], report.Changes.Select(c => c.Text));
        Assert.Equal(1, report.LayoutOnlyChanges);
        string markdown = SemanticDiff.ToMarkdown(report);
        Assert.Contains("- Node `Web app` relabelled to `Storefront`", markdown);
        Assert.Contains("- 1 layout-only change\n", markdown);
    }

    [Fact]
    public void Declared_ports_and_an_edge_ends_side_port_are_layout_but_a_column_port_is_not()
    {
        Document before = Order();
        Document moved = EditEdge(
            EditNode(before, "n_api", n => n with { Ports = [new Port("in", 0.5, 0), new Port("db", 0.75, 1), new Port("cache", 1, 0.5)] }),
            "e_2",
            e => e with { From = e.From with { Port = "cache" } });

        SemanticDiffReport collapsed = SemanticDiff.Describe(before, moved, ignoreLayout: true);

        Assert.False(collapsed.HasChanges);
        // The node's ports and the edge each count once.
        Assert.Equal(2, collapsed.LayoutOnlyChanges);

        Document column = EditEdge(before, "e_2", e => e with { To = e.To with { Port = "col:order_id" } });
        SemanticDiffReport semantic = SemanticDiff.Describe(before, column, ignoreLayout: true);
        Assert.True(semantic.HasChanges);
        Assert.Equal(["Edge `Orders API` to `Postgres` target port changed from no port to `col:order_id`"], semantic.Changes.Select(c => c.Text));
    }

    [Fact]
    public void Reconnecting_an_edge_is_semantic_and_names_both_ends_by_label()
    {
        Document before = Order();
        Document after = EditEdge(before, "e_1", e => e with { To = new Endpoint("n_db") });

        Assert.Equal(
            ["Edge `Web app` to `Orders API` reconnected: now ends at `Postgres` (was `Orders API`)"],
            Sentences(before, after, ignoreLayout: true));
    }

    [Fact]
    public void Props_and_style_are_reported_one_sentence_per_key()
    {
        Document before = EditNode(Order(), "n_api", n => n with
        {
            Props = new Dictionary<string, JsonElement> { ["owner"] = Json("\"platform\""), ["tier"] = Json("1"), ["legacy"] = Json("true") },
            Style = new NodeStyle { Fill = "#ffffff", Bold = true },
        });
        Document after = EditNode(before, "n_api", n => n with
        {
            Props = new Dictionary<string, JsonElement> { ["owner"] = Json("\"data\""), ["tier"] = Json("1"), ["columns"] = Json("[\"id\"]") },
            Style = new NodeStyle { Fill = "#000000" },
        });

        IReadOnlyList<string> sentences = Sentences(before, after, ignoreLayout: true);

        Assert.Contains("Node `Orders API`: prop `owner` changed from `platform` to `data`", sentences);
        Assert.Contains("Node `Orders API`: prop `legacy` removed (was `true`)", sentences);
        Assert.Contains("Node `Orders API`: prop `columns` added (`[\"id\"]`)", sentences);
        Assert.Contains("Node `Orders API` style: `fill` changed from `#ffffff` to `#000000`", sentences);
        Assert.Contains("Node `Orders API` style: `bold` cleared (was `true`)", sentences);
        // The prop that did not change is not mentioned, and the props are not one lump.
        Assert.DoesNotContain(sentences, s => s.Contains("`tier`"));
        Assert.Equal(5, sentences.Count);
    }

    [Fact]
    public void Document_level_changes_come_first_as_sentences()
    {
        Document before = Order() with { Theme = new Dictionary<string, string> { ["canvasBackground"] = "#ffffff" } };
        Document after = before with
        {
            Title = "Order platform",
            Type = DocumentType.Freeform,
            Theme = new Dictionary<string, string> { ["canvasBackground"] = "#101010", ["palette.brand"] = "#0000ff" },
        };

        SemanticDiffReport report = SemanticDiff.Describe(before, after, ignoreLayout: true);

        Assert.Equal(
            [
                "Title changed from `Order service` to `Order platform`",
                "Diagram type changed from `architecture` to `freeform`",
                "Theme override `canvasBackground` changed from `#ffffff` to `#101010`",
                "Theme override `palette.brand` set to `#0000ff`",
            ],
            report.Changes.Select(c => c.Text));
        Assert.All(report.Changes, c => Assert.Equal("document", c.Category));
    }

    [Fact]
    public void Moving_a_node_between_containers_names_the_containers_by_label()
    {
        Document before = Order();
        Document after = EditNode(before, "n_cache", n => n with { Parent = null });

        Assert.Equal(["Node `Redis` moved out of `VPC`"], Sentences(before, after, ignoreLayout: true));
        Assert.Equal(["Node `Web app` moved into `VPC`"], Sentences(before, EditNode(before, "n_web", n => n with { Parent = "c_vpc" }), ignoreLayout: true));
    }

    [Fact]
    public void The_lines_every_save_rewrites_do_not_make_a_diff_non_empty()
    {
        Document before = Order();
        Document after = before with { Meta = before.Meta with { UpdatedAt = before.Meta.UpdatedAt.AddDays(1), AiProvenance = null } };

        SemanticDiffReport report = SemanticDiff.Describe(before, after, ignoreLayout: false);

        Assert.False(report.HasChanges);
        Assert.Empty(report.Changes);
        Assert.Equal(2, report.Housekeeping.Count);
        string markdown = SemanticDiff.ToMarkdown(report);
        Assert.Contains("No changes.", markdown);
        Assert.Contains("Last saved time changed", markdown);
        Assert.Contains("AI provenance trail changed (1 entry to 0 entries)", markdown);
    }

    [Fact]
    public void An_identical_pair_says_no_changes_and_nothing_else()
    {
        Document document = Order();

        SemanticDiffReport report = SemanticDiff.Describe(document, document, ignoreLayout: false);

        Assert.False(report.HasChanges);
        Assert.Empty(report.Housekeeping);
        Assert.Equal("### Diagram changes: `Order service`\n\nNo changes.\n", SemanticDiff.ToMarkdown(report));
    }

    [Fact]
    public void Reordering_edges_matters_in_a_sequence_diagram_and_is_layout_in_any_other()
    {
        Document sequence = DocumentJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", "checkout-sequence.json")));
        Document swapped = sequence with { Edges = [sequence.Edges[1], sequence.Edges[0], .. sequence.Edges.Skip(2)] };

        SemanticDiffReport time = SemanticDiff.Describe(sequence, swapped, ignoreLayout: true);

        Assert.True(time.HasChanges);
        Assert.Equal("order", Assert.Single(time.Changes).Category);

        Document order = Order();
        Document reordered = order with { Edges = [order.Edges[1], order.Edges[0], order.Edges[2]] };
        SemanticDiffReport drawing = SemanticDiff.Describe(order, reordered, ignoreLayout: true);
        Assert.False(drawing.HasChanges);
        Assert.Equal(1, drawing.LayoutOnlyChanges);
        Assert.True(SemanticDiff.Describe(order, reordered, ignoreLayout: false).HasChanges);
    }

    [Fact]
    public void The_fly_through_is_reported_when_it_changes()
    {
        Document before = Order();
        Document without = before with { Flythrough = null };

        Assert.Equal(["Removed fly-through (was 4 keyframes)"], Sentences(before, without, ignoreLayout: true));
        Assert.Equal(["Added fly-through (4 keyframes)"], Sentences(without, before, ignoreLayout: true));
    }

    [Fact]
    public void A_label_cannot_ping_a_user_or_break_out_of_its_code_span()
    {
        Document before = Order();
        string nasty = "@octocat `x` ``y``\nsecond line";
        Document after = EditNode(before, "n_web", n => n with { Label = nasty });

        string markdown = SemanticDiff.ToMarkdown(SemanticDiff.Describe(before, after, ignoreLayout: false));

        // On one line, inside a fence longer than any backtick run it holds (here three), so the @ is code, not a mention.
        Assert.Contains("- Node `Web app` relabelled to ```@octocat `x` ``y`` second line```\n", markdown);
        Assert.DoesNotContain("\nsecond line", markdown);
    }

    [Fact]
    public void A_long_value_is_cut_short()
    {
        Document before = Order();
        Document after = EditNode(before, "n_web", n => n with { Label = new string('a', 500) });

        string sentence = Assert.Single(Sentences(before, after));

        Assert.True(sentence.Length < 250, sentence);
        Assert.Contains("…", sentence);
    }

    [Fact]
    public void The_json_report_carries_the_verdict_the_layout_count_and_the_volatile_lines_apart()
    {
        Document before = Order();
        Document after = EditNode(before, "n_web", n => n with { Geometry = n.Geometry! with { X = 1 } }) with { Meta = before.Meta with { UpdatedAt = before.Meta.UpdatedAt.AddDays(1) } };

        using JsonDocument json = JsonDocument.Parse(SemanticDiff.ToJson(SemanticDiff.Describe(before, after, ignoreLayout: true)));
        JsonElement root = json.RootElement;

        Assert.False(root.GetProperty("hasChanges").GetBoolean());
        Assert.Equal(1, root.GetProperty("layoutOnlyChanges").GetInt32());
        Assert.Equal(0, root.GetProperty("changes").GetArrayLength());
        Assert.Equal(1, root.GetProperty("housekeeping").GetArrayLength());
        Assert.True(root.GetProperty("ignoreLayout").GetBoolean());
    }
}
