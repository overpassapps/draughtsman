using System.Text.Json.Nodes;
using Draughtsman.Mcp.Patching;

namespace Draughtsman.Mcp.Tests;

public class PatchApplierTests
{
    [Fact]
    public void AddEdge_warns_but_does_not_fail_when_an_endpoint_is_missing()
    {
        JsonObject document = Document();
        JsonArray ops = Ops("""[{"op":"addEdge","edge":{"id":"e_1","kind":"flow","from":{"node":"n_a"},"to":{"node":"n_ghost"}}}]""");

        List<string> warnings = PatchApplier.Apply(document, ops);

        Assert.Single(warnings);
        Assert.Contains("n_ghost", warnings[0], StringComparison.Ordinal);
        Assert.Single(document["edges"]!.AsArray());
    }

    [Fact]
    public void RemoveNode_warns_when_an_edge_still_references_it()
    {
        JsonObject document = Document();
        document["nodes"]!.AsArray().Add(JsonNode.Parse("""{"id":"n_b","kind":"process","label":"B","shape":"rectangle"}"""));
        document["edges"]!.AsArray().Add(JsonNode.Parse("""{"id":"e_1","kind":"flow","from":{"node":"n_a"},"to":{"node":"n_b"}}"""));

        List<string> warnings = PatchApplier.Apply(document, Ops("""[{"op":"removeNode","id":"n_a"}]"""));

        Assert.Single(warnings);
        Assert.Contains("e_1", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AddNode_with_a_duplicate_id_fails_loudly()
    {
        JsonObject document = Document();
        PatchException ex = Assert.Throws<PatchException>(() =>
            PatchApplier.Apply(document, Ops("""[{"op":"addNode","node":{"id":"n_a","kind":"process","label":"Dup","shape":"rectangle"}}]""")));
        Assert.Contains("n_a", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateNode_rejects_style_and_props_pointing_at_the_dedicated_ops()
    {
        JsonObject document = Document();
        PatchException ex = Assert.Throws<PatchException>(() =>
            PatchApplier.Apply(document, Ops("""[{"op":"updateNode","id":"n_a","set":{"style":{"fill":"#fff"}}}]""")));
        Assert.Contains("restyle", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetProp_then_removeProp_clears_the_props_object_entirely_when_empty()
    {
        JsonObject document = Document();
        PatchApplier.Apply(document, Ops("""[{"op":"setProp","target":"node","id":"n_a","key":"owner","value":"team-x"}]"""));
        Assert.Equal("team-x", document["nodes"]![0]!["props"]!["owner"]!.GetValue<string>());

        PatchApplier.Apply(document, Ops("""[{"op":"removeProp","target":"node","id":"n_a","key":"owner"}]"""));
        Assert.Null(document["nodes"]![0]!["props"]);
    }

    [Fact]
    public void SetProp_without_value_fails_loudly()
    {
        JsonObject document = Document();
        PatchException ex = Assert.Throws<PatchException>(() =>
            PatchApplier.Apply(document, Ops("""[{"op":"setProp","target":"node","id":"n_a","key":"owner"}]""")));
        Assert.Contains("value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SetProp_with_null_value_fails_loudly()
    {
        JsonObject document = Document();
        PatchException ex = Assert.Throws<PatchException>(() =>
            PatchApplier.Apply(document, Ops("""[{"op":"setProp","target":"node","id":"n_a","key":"owner","value":null}]""")));
        Assert.Contains("value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoveProp_on_a_prop_that_is_not_set_fails_loudly()
    {
        JsonObject document = Document();
        Assert.Throws<PatchException>(() =>
            PatchApplier.Apply(document, Ops("""[{"op":"removeProp","target":"node","id":"n_a","key":"missing"}]""")));
    }

    private static JsonArray Ops(string json)
    {
        return (JsonArray)JsonNode.Parse(json)!;
    }

    private static JsonObject Document()
    {
        return (JsonObject)JsonNode.Parse("""
            {
              "id": "doc_1", "schemaVersion": 1, "title": "T", "type": "flowchart",
              "nodes": [{"id":"n_a","kind":"process","label":"A","shape":"rectangle"}],
              "edges": [],
              "meta": {"createdAt":"2026-01-01T00:00:00.000Z","updatedAt":"2026-01-01T00:00:00.000Z","createdBy":"user:local"}
            }
            """)!;
    }
}
