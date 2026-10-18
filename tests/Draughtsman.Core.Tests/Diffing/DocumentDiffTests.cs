using System.Text.Json;
using Draughtsman.Core.Diffing;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests.Diffing;

/// <summary>
/// DocumentDiff from the Core assembly (it used to live in the MCP project): the document-level and per-key props
/// additions, and the property git sync rests on - a diagram diffed against its own re-exported text has no changes.
/// </summary>
public class DocumentDiffTests
{
    private static Document Order()
    {
        return DocumentJson.Deserialize(ContractFiles.ReadExample());
    }

    private static Document Document(string file)
    {
        return DocumentJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contract", file)));
    }

    private static JsonElement Json(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>Everything a diff can report, including the volatile header lines.</summary>
    private static int Everything(DocumentDiffResult diff)
    {
        return diff.AddedNodes.Count + diff.RemovedNodes.Count + diff.ChangedNodes.Count
            + diff.AddedEdges.Count + diff.RemovedEdges.Count + diff.ChangedEdges.Count
            + diff.MovedNodes.Count + diff.MovedEdges.Count
            + (diff.FlythroughChange is null ? 0 : 1) + diff.DocumentChanges.Count + diff.VolatileChanges.Count;
    }

    [Theory]
    [InlineData("order-service.json")]
    [InlineData("checkout-sequence.json")]
    [InlineData("checkout-fragments.json")]
    [InlineData("editor-session.json")]
    public void A_document_diffed_against_its_own_re_exported_dsl_has_no_changes_at_all(string file)
    {
        Document original = Document(file);

        Document reExported = Dsl.Parse(Dsl.Print(original)).Document;

        Assert.Equal(0, Everything(DocumentDiff.Compare(original, reExported)));

        // The same comparison must be able to see a change, or the zero above proves nothing.
        Node first = reExported.Nodes[0];
        Document edited = reExported with { Nodes = [first with { Label = first.Label + " (edited)" }, .. reExported.Nodes.Skip(1)] };
        Assert.Single(DocumentDiff.Compare(original, edited).ChangedNodes);
    }

    [Fact]
    public void A_retitle_is_a_document_change()
    {
        Document before = Order();

        DocumentDiffResult diff = DocumentDiff.Compare(before, before with { Title = "Order platform" });

        FieldChange change = Assert.Single(diff.DocumentChanges);
        Assert.Equal("title", change.Field);
        Assert.Equal("Order service", change.From!.Value.GetString());
        Assert.Equal("Order platform", change.To!.Value.GetString());
        Assert.Equal(1, Everything(diff));
    }

    [Fact]
    public void A_type_change_is_a_document_change()
    {
        Document before = Order();

        FieldChange change = Assert.Single(DocumentDiff.Compare(before, before with { Type = DocumentType.Freeform }).DocumentChanges);

        Assert.Equal("type", change.Field);
        Assert.Equal("architecture", change.From!.Value.GetString());
        Assert.Equal("freeform", change.To!.Value.GetString());
    }

    [Fact]
    public void A_changed_document_id_and_schema_version_are_document_changes()
    {
        Document before = Order();

        DocumentDiffResult diff = DocumentDiff.Compare(before, before with { Id = "doc_copy", SchemaVersion = 2 });

        Assert.Equal(["id", "schemaVersion"], diff.DocumentChanges.Select(c => c.Field));
    }

    [Fact]
    public void Each_theme_override_is_reported_by_token_added_changed_and_removed()
    {
        Document before = Order() with { Theme = new Dictionary<string, string> { ["canvasBackground"] = "#ffffff", ["grid.visible"] = "true", ["palette.accent"] = "#ff0000" } };
        Document after = before with { Theme = new Dictionary<string, string> { ["canvasBackground"] = "#000000", ["grid.visible"] = "true", ["palette.brand"] = "#0000ff" } };

        IReadOnlyList<FieldChange> changes = DocumentDiff.Compare(before, after).DocumentChanges;

        Assert.Equal(["theme.canvasBackground", "theme.palette.accent", "theme.palette.brand"], changes.Select(c => c.Field));
        Assert.Equal(("#ffffff", "#000000"), (changes[0].From!.Value.GetString(), changes[0].To!.Value.GetString()));
        Assert.Equal("#ff0000", changes[1].From!.Value.GetString());
        Assert.Null(changes[1].To);
        Assert.Null(changes[2].From);
        Assert.Equal("#0000ff", changes[2].To!.Value.GetString());
        Assert.DoesNotContain(changes, c => c.Field == "theme.grid.visible");
    }

    [Fact]
    public void No_theme_and_an_empty_theme_are_the_same()
    {
        Document before = Order();

        Assert.Empty(DocumentDiff.Compare(before with { Theme = null }, before with { Theme = new Dictionary<string, string>() }).DocumentChanges);
    }

    [Fact]
    public void The_lines_every_save_rewrites_are_volatile_and_never_an_element_or_document_change()
    {
        Document before = Order();
        DateTimeOffset later = before.Meta.UpdatedAt.AddHours(3);
        Document after = before with
        {
            Meta = before.Meta with
            {
                UpdatedAt = later,
                CreatedBy = "someone-else",
                AiProvenance = [new AiProvenance("claude-code", later, AiAction.Updated, ["n_api"]) { Model = "m" }],
            },
        };

        DocumentDiffResult diff = DocumentDiff.Compare(before, after);

        Assert.Equal(["meta.aiProvenance", "meta.createdBy", "meta.updatedAt"], diff.VolatileChanges.Select(c => c.Field).Order(StringComparer.Ordinal));
        Assert.Empty(diff.DocumentChanges);
        Assert.Empty(diff.ChangedNodes);
        Assert.Empty(diff.ChangedEdges);
        Assert.Empty(diff.AddedNodes);
        Assert.Empty(diff.MovedNodes);
        Assert.Null(diff.FlythroughChange);
    }

    [Fact]
    public void No_provenance_trail_and_an_empty_one_are_the_same()
    {
        Document before = Order();
        Document none = before with { Meta = before.Meta with { AiProvenance = null } };
        Document empty = before with { Meta = before.Meta with { AiProvenance = [] } };

        Assert.Empty(DocumentDiff.Compare(none, empty).VolatileChanges);
    }

    private static Document WithProps(Document document, string nodeId, Dictionary<string, JsonElement>? props)
    {
        return document with { Nodes = [.. document.Nodes.Select(n => n.Id == nodeId ? n with { Props = props } : n)] };
    }

    [Fact]
    public void Props_are_reported_per_key_with_the_whole_field_still_present()
    {
        Document before = WithProps(Order(), "n_api", new Dictionary<string, JsonElement>
        {
            ["owner"] = Json("\"platform\""),
            ["tier"] = Json("1"),
            ["columns"] = Json("[\"id\",\"total\"]"),
            ["gone"] = Json("true"),
        });
        Document after = WithProps(before, "n_api", new Dictionary<string, JsonElement>
        {
            ["owner"] = Json("\"platform\""),
            ["tier"] = Json("2"),
            ["columns"] = Json("[\"id\",\"total\"]"),
            ["added"] = Json("{\"a\":1}"),
        });

        ElementChange change = Assert.Single(DocumentDiff.Compare(before, after).ChangedNodes);

        Assert.Equal(["added", "gone", "tier"], change.Props.Select(p => p.Field));
        FieldChange tier = change.Props.Single(p => p.Field == "tier");
        Assert.Equal((1, 2), (tier.From!.Value.GetInt32(), tier.To!.Value.GetInt32()));
        Assert.Null(change.Props.Single(p => p.Field == "added").From);
        Assert.Null(change.Props.Single(p => p.Field == "gone").To);
        // The unchanged keys, array included, are not reported; the field as a whole still is, for an applier of the diff.
        Assert.DoesNotContain(change.Props, p => p.Field is "owner" or "columns");
        Assert.Contains(change.Fields, f => f.Field == "props");
    }

    [Fact]
    public void Props_added_to_an_element_that_had_none_are_reported_per_key()
    {
        Document before = Order();
        Document after = WithProps(before, "n_web", new Dictionary<string, JsonElement> { ["owner"] = Json("\"web\""), ["tier"] = Json("3") });

        ElementChange change = Assert.Single(DocumentDiff.Compare(before, after).ChangedNodes);

        Assert.Equal("n_web", change.Id);
        Assert.Equal(["owner", "tier"], change.Props.Select(p => p.Field));
        Assert.All(change.Props, p => Assert.Null(p.From));
    }

    [Fact]
    public void A_change_to_something_other_than_props_reports_no_prop_keys()
    {
        Document before = Order();
        Document after = before with { Nodes = [.. before.Nodes.Select(n => n.Id == "n_api" ? n with { Label = "Orders service" } : n)] };

        ElementChange change = Assert.Single(DocumentDiff.Compare(before, after).ChangedNodes);

        Assert.Empty(change.Props);
        Assert.DoesNotContain(change.Fields, f => f.Field == "props");
    }
}
