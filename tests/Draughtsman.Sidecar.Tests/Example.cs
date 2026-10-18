using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;

namespace Draughtsman.Sidecar.Tests;

/// <summary>The shared example document next to the test binary, in the forms the tests need.</summary>
internal static class Example
{
    public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "contract", "order-service.json");

    public static string ReadJson()
    {
        return File.ReadAllText(Path);
    }

    public static Document ReadDocument()
    {
        return DocumentJson.Deserialize(ReadJson());
    }

    /// <summary>The example as a DSL or Mermaid importer would produce it: no geometry on any node.</summary>
    public static Document WithoutGeometry()
    {
        JsonNode root = JsonNode.Parse(ReadJson())!;
        foreach (JsonNode? node in root["nodes"]!.AsArray())
        {
            node!.AsObject().Remove("geometry");
        }

        return ToDocument(root);
    }

    /// <summary>A chain of many nodes, enough work for ELK that a tiny timeout reliably fires first.</summary>
    public static Document LargeChain(int count)
    {
        var nodes = new JsonArray();
        var edges = new JsonArray();
        for (int i = 0; i < count; i++)
        {
            nodes.Add(new JsonObject { ["id"] = $"n_{i}", ["kind"] = "process", ["label"] = $"Step {i}", ["shape"] = "rounded" });
            if (i > 0)
            {
                edges.Add(new JsonObject { ["id"] = $"e_{i}", ["kind"] = "flow", ["from"] = new JsonObject { ["node"] = $"n_{i - 1}" }, ["to"] = new JsonObject { ["node"] = $"n_{i}" } });
                if (i > 1)
                {
                    edges.Add(new JsonObject { ["id"] = $"e_skip_{i}", ["kind"] = "flow", ["from"] = new JsonObject { ["node"] = $"n_{i - 2}" }, ["to"] = new JsonObject { ["node"] = $"n_{i}" } });
                }
            }
        }

        var document = new JsonObject
        {
            ["id"] = "doc_large",
            ["schemaVersion"] = 1,
            ["title"] = "Large",
            ["type"] = "flowchart",
            ["nodes"] = nodes,
            ["edges"] = edges,
            ["meta"] = new JsonObject { ["createdAt"] = "2026-09-21T00:00:00.000Z", ["updatedAt"] = "2026-09-21T00:00:00.000Z", ["createdBy"] = "test" },
        };
        return ToDocument(document);
    }

    private static Document ToDocument(JsonNode root)
    {
        return JsonSerializer.Deserialize<Document>(root, DocumentJson.Options)!;
    }

    public static void AssertWellFormedLayout(Document document)
    {
        var byId = document.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (Node node in document.Nodes)
        {
            Geometry geometry = LaidOut(node);
            Assert.True(geometry.W > 0 && geometry.H > 0, $"{node.Id} has no size");
            if (node.Parent is not null)
            {
                Geometry outer = LaidOut(byId[node.Parent]);
                Geometry inner = geometry;
                Assert.True(
                    inner.X >= outer.X && inner.Y >= outer.Y && inner.X + inner.W <= outer.X + outer.W && inner.Y + inner.H <= outer.Y + outer.H,
                    $"{node.Id} lies outside its container {node.Parent}");
            }
        }

        foreach (Node a in document.Nodes)
        {
            foreach (Node b in document.Nodes)
            {
                if (string.CompareOrdinal(a.Id, b.Id) < 0 && a.Parent == b.Parent)
                {
                    Assert.False(Overlaps(LaidOut(a), LaidOut(b)), $"{a.Id} and {b.Id} overlap");
                }
            }
        }
    }

    /// <summary>Geometry of a node the layout has placed; every node leaves layout with one.</summary>
    public static Geometry LaidOut(Node node)
    {
        Assert.True(node.Geometry is not null, $"{node.Id} came back without geometry");
        return node.Geometry!;
    }

    private static bool Overlaps(Geometry a, Geometry b)
    {
        return a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
    }
}
