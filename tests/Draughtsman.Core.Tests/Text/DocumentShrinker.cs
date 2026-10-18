using Draughtsman.Core.Models;

namespace Draughtsman.Core.Tests.Text;

/// <summary>
/// Greedy delta debugging for a failing round trip: drops edges, nodes and then optional parts of what remains
/// while the failure persists, so the test reports the smallest DSL text that still breaks.
/// </summary>
internal static class DocumentShrinker
{
    public static Document Shrink(Document document, Func<Document, bool> stillFails)
    {
        Document current = document;
        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (Func<Document, IEnumerable<Document>> step in Steps)
            {
                foreach (Document candidate in step(current))
                {
                    if (stillFails(candidate))
                    {
                        current = candidate;
                        progress = true;
                        break;
                    }
                }

                if (progress)
                {
                    break;
                }
            }
        }

        return current;
    }

    private static readonly Func<Document, IEnumerable<Document>>[] Steps =
    [
        RemoveEdges,
        RemoveNodes,
        SimplifyFlythrough,
        SimplifyDocument,
        SimplifyNodes,
        SimplifyEdges,
    ];

    private static IEnumerable<Document> RemoveEdges(Document document)
    {
        for (int i = 0; i < document.Edges.Count; i++)
        {
            yield return document with { Edges = document.Edges.Where((_, index) => index != i).ToList() };
        }
    }

    private static IEnumerable<Document> RemoveNodes(Document document)
    {
        for (int i = 0; i < document.Nodes.Count; i++)
        {
            yield return document with { Nodes = document.Nodes.Where((_, index) => index != i).ToList() };
        }
    }

    private static IEnumerable<Document> SimplifyFlythrough(Document document)
    {
        if (document.Flythrough is not { } flythrough)
        {
            yield break;
        }

        yield return document with { Flythrough = null };
        if (flythrough.Loop is not null)
        {
            yield return document with { Flythrough = flythrough with { Loop = null } };
        }

        for (int i = 0; i < flythrough.Keyframes.Count && flythrough.Keyframes.Count > 1; i++)
        {
            yield return document with { Flythrough = flythrough with { Keyframes = flythrough.Keyframes.Where((_, index) => index != i).ToList() } };
        }

        for (int i = 0; i < flythrough.Keyframes.Count; i++)
        {
            Keyframe keyframe = flythrough.Keyframes[i];
            Keyframe[] variants = [keyframe with { Caption = null }, keyframe with { DurationMs = null }, keyframe with { HoldMs = null }, keyframe with { Easing = null }];
            foreach (Keyframe variant in variants)
            {
                if (variant != keyframe)
                {
                    yield return document with { Flythrough = flythrough with { Keyframes = flythrough.Keyframes.Select((k, index) => index == i ? variant : k).ToList() } };
                }
            }
        }
    }

    private static IEnumerable<Document> SimplifyDocument(Document document)
    {
        if (document.Theme is not null)
        {
            yield return document with { Theme = null };
        }

        if (document.Meta.AiProvenance is not null)
        {
            yield return document with { Meta = document.Meta with { AiProvenance = null } };
        }

        if (document.Title.Length > 0)
        {
            yield return document with { Title = string.Empty };
        }
    }

    private static IEnumerable<Document> SimplifyNodes(Document document)
    {
        for (int i = 0; i < document.Nodes.Count; i++)
        {
            Node node = document.Nodes[i];
            Node[] variants =
            [
                node with { Style = null },
                node with { Props = null },
                node with { Ports = null },
                node with { Geometry = null },
                node with { Parent = null },
                node with { Label = string.Empty },
            ];
            foreach (Node variant in variants)
            {
                if (variant != node)
                {
                    yield return document with { Nodes = document.Nodes.Select((n, index) => index == i ? variant : n).ToList() };
                }
            }
        }
    }

    private static IEnumerable<Document> SimplifyEdges(Document document)
    {
        for (int i = 0; i < document.Edges.Count; i++)
        {
            Edge edge = document.Edges[i];
            Edge[] variants =
            [
                edge with { Label = null },
                edge with { Routing = null },
                edge with { Style = null },
                edge with { Props = null },
                edge with { From = new Endpoint(edge.From.Node) },
                edge with { To = new Endpoint(edge.To.Node) },
            ];
            foreach (Edge variant in variants)
            {
                if (variant != edge)
                {
                    yield return document with { Edges = document.Edges.Select((e, index) => index == i ? variant : e).ToList() };
                }
            }
        }
    }
}
