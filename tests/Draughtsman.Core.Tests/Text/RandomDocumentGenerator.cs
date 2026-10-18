using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Core.Tests.Text;

/// <summary>
/// Seeded generator of awkward but valid documents for the round-trip fuzz test: dotted ids, labels with quotes and
/// unicode, optional geometry, ports, sparse style, reserved-looking prop keys, nested JSON props, containers,
/// dangling references. The same seed always yields the same document.
/// </summary>
internal sealed class RandomDocumentGenerator
{
    private static readonly string[] s_textPool =
    [
        "Web app", "Orders API", "Postgres", "a", "", "  padded  ", "He said \"hi\"", "C:\\temp\\path", "C# service",
        "Zażółć gęślą jaźń", "订单服务", "🚀 Launch", "line1\nline2", "tab\there", "a -> b", "{braces}", "[brackets]",
        "key=value", "# not a comment", "42", "true", "null", "1e3", "n_api", "\u0001control", "trailing\\",
    ];

    private static readonly string[] s_shapes =
    [
        "rectangle", "rounded", "diamond", "cylinder", "pill", "hexagon", "parallelogram", "document", "predefined-process", "manual-input", "off-page-connector",
        "delay", "manual-operation", "display", "stored-data", "multi-document", "merge", "extract", "on-page-connector", "summing-junction", "or", "collate", "sort", "loop-limit", "internal-storage", "card",
        "text", "image", "custom:aws/lambda", "custom:my-lib/thing.v2", "custom:generic/server", "custom:generic/load-balancer",
    ];

    private static readonly string[] s_colours = ["#FFF", "#ff00aa", "token:accent", "rgb(1, 2, 3)", "red", "transparent"];

    private static readonly string[] s_fonts = ["Inter", "Inter, 14px", "\"Quoted Font\"", "monospace"];

    private static readonly string[] s_propKeys =
    [
        "owner", "url", "ticket", "runtime", "shape", "parent", "style.fill", "routing", "waypoints", "ports", "id", "label",
        "owner name", "właściciel", "a=b", "with#hash", "", "x.y.z", "UPPER", "_under",
    ];

    private static readonly string[] s_themeKeys = ["node.fill", "edge.stroke", "accent", "surface", "weird key", "font.body"];

    private readonly Random _random;

    public RandomDocumentGenerator(int seed)
    {
        _random = new Random(seed);
    }

    public Document Next()
    {
        DocumentType type = Pick(Enum.GetValues<DocumentType>());
        KindVocabulary.TryGet(type, out KindVocabulary? vocabulary);

        List<Node> nodes = Nodes(type, vocabulary);
        List<Edge> edges = Edges(nodes, vocabulary);

        Dictionary<string, string>? theme = null;
        if (Chance(0.4))
        {
            theme = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string key in Distinct(s_themeKeys, _random.Next(1, 4)))
            {
                theme[key] = Text();
            }
        }

        List<AiProvenance>? provenance = null;
        if (Chance(0.4))
        {
            provenance = [];
            string[] allIds = [.. nodes.Select(n => n.Id), .. edges.Select(e => e.Id)];
            for (int i = _random.Next(1, 3); i > 0; i--)
            {
                List<string> touched = allIds.Where(_ => Chance(0.3)).ToList();
                provenance.Add(new AiProvenance(Chance(0.7) ? Pick(["claude-code", "gemini-cli", "codex"]) : Text(), Timestamp(), Pick(Enum.GetValues<AiAction>()), touched)
                {
                    Model = Chance(0.5) ? Pick(["claude-fable-5-1", "gpt 5", "local/llama:8b"]) : null,
                });
            }
        }

        DateTimeOffset created = Timestamp();
        // Last, so every document an earlier seed produced keeps its nodes and edges: the flythrough only adds to it.
        Flythrough? flythrough = Chance(0.35) ? Flythrough() : null;
        return new Document(
            "doc_" + Ident(allowDots: false),
            Document.CurrentSchemaVersion,
            Text(),
            type,
            nodes,
            edges,
            new Meta(created, Chance(0.5) ? created : Timestamp(), Chance(0.6) ? Ident(allowDots: false) : Text()) { AiProvenance = provenance })
        {
            Theme = theme,
            Flythrough = flythrough,
        };
    }

    private Flythrough Flythrough()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keyframes = new List<Keyframe>();
        for (int i = _random.Next(1, 7); i > 0; i--)
        {
            keyframes.Add(new Keyframe(UniqueId(ids, "k_", allowDots: true), new ViewRect(Number(), Number(), Math.Abs(Number()) + 1, Math.Abs(Number()) + 1))
            {
                Caption = Chance(0.6) ? Text() : null,
                DurationMs = Chance(0.4) ? Milliseconds() : null,
                HoldMs = Chance(0.4) ? Milliseconds() : null,
                Easing = Chance(0.5) ? Pick(Enum.GetValues<KeyframeEasing>()) : null,
            });
        }

        return new Flythrough(keyframes) { Loop = Chance(0.4) ? Chance(0.5) : null };
    }

    /// <summary>A keyframe time inside the schema's 0 to 600000, at the edges as well as in between.</summary>
    private double Milliseconds()
    {
        return _random.Next(0, 4) switch
        {
            0 => Pick([0.0, 600000.0]),
            1 => _random.Next(0, 10001),
            2 => Math.Round(_random.NextDouble() * 600000, 1),
            _ => _random.NextDouble() * 600000,
        };
    }

    private List<Node> Nodes(DocumentType type, KindVocabulary? vocabulary)
    {
        int count = _random.Next(0, 13);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<Node>(count);
        for (int i = 0; i < count; i++)
        {
            string kind = Chance(0.15) ? "container" : Kind(vocabulary?.NodeKinds);
            string shape = Chance(0.7) ? DefaultShape(type, kind) : Pick(s_shapes);
            List<Port>? ports = null;
            if (Chance(0.4))
            {
                ports = Distinct(["in", "out", "db", "cache", "p.1", "p-2"], _random.Next(1, 4)).Select(id => new Port(id, Fraction(), Fraction())).ToList();
            }

            nodes.Add(new Node(UniqueId(ids, "n_", allowDots: true), kind, Text(), shape)
            {
                Geometry = Chance(0.75) ? new Geometry(Number(), Number(), Math.Abs(Number()), Math.Abs(Number())) { Rotation = Chance(0.3) ? Number() : null } : null,
                Style = Chance(0.5) ? NodeStyle() : null,
                Props = Chance(0.6) ? Props() : null,
                Ports = ports,
            });
        }

        // "a.b" cannot be both node a's port b and node a.b; the grammar resolves that in favour of the node, so
        // keep the generator away from it. Real ids with dots are rare; ports named after other nodes rarer still.
        for (int i = 0; i < nodes.Count; i++)
        {
            while (nodes.Any(other => other.Ports is not null && other.Ports.Any(p => nodes[i].Id == other.Id + "." + p.Id)))
            {
                nodes[i] = nodes[i] with { Id = UniqueId(ids, "n_", allowDots: false) };
            }
        }

        List<int> containers = nodes.Select((n, i) => (n, i)).Where(t => t.n.Kind == "container").Select(t => t.i).ToList();
        for (int i = 0; i < nodes.Count; i++)
        {
            List<int> earlierContainers = containers.Where(c => c < i).ToList();
            if (earlierContainers.Count > 0 && Chance(0.5))
            {
                nodes[i] = nodes[i] with { Parent = nodes[Pick(earlierContainers)].Id };
            }
            else if (i > 0 && Chance(0.05))
            {
                nodes[i] = nodes[i] with { Parent = nodes[_random.Next(0, i)].Id };
            }
            else if (Chance(0.03))
            {
                nodes[i] = nodes[i] with { Parent = "ghost_" + Ident(allowDots: false) };
            }
        }

        return nodes;
    }

    private List<Edge> Edges(List<Node> nodes, KindVocabulary? vocabulary)
    {
        int count = nodes.Count == 0 ? _random.Next(0, 3) : _random.Next(0, 16);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var edges = new List<Edge>(count);
        for (int i = 0; i < count; i++)
        {
            List<Point>? waypoints = Chance(0.5) ? Enumerable.Range(0, _random.Next(1, 4)).Select(_ => new Point(Number(), Number())).ToList() : null;
            edges.Add(new Edge(UniqueId(ids, "e_", allowDots: true), Kind(vocabulary?.EdgeKinds), Endpoint(nodes), Endpoint(nodes))
            {
                Label = Chance(0.6) ? Text() : null,
                Routing = Chance(0.5) ? new Routing(Pick(Enum.GetValues<RoutingMode>())) { Waypoints = waypoints } : null,
                Style = Chance(0.4) ? EdgeStyle() : null,
                Props = Chance(0.4) ? Props() : null,
            });
        }

        return edges;
    }

    private Endpoint Endpoint(List<Node> nodes)
    {
        if (nodes.Count == 0 || Chance(0.05))
        {
            return new Endpoint("ghost_" + Ident(allowDots: false)) { Port = Chance(0.5) ? Ident(allowDots: false) : null };
        }

        Node node = Pick(nodes);
        string? port = null;
        if (node.Ports is { Count: > 0 } && Chance(0.5))
        {
            port = Pick(node.Ports).Id;
        }
        else if (Chance(0.03))
        {
            port = "missing";
        }

        return new Endpoint(node.Id) { Port = port };
    }

    private string Kind(IReadOnlyList<KindDefinition>? known)
    {
        if (known is not null && Chance(0.6))
        {
            return Pick(known).Name;
        }

        return Chance(0.7) ? Ident(allowDots: false) : Pick(["my kind", "kind\"quoted", "rodzaj", "#hashy"]);
    }

    private static string DefaultShape(DocumentType type, string kind)
    {
        if (KindVocabulary.TryGet(type, out KindVocabulary? vocabulary))
        {
            return vocabulary!.NodeKinds.FirstOrDefault(k => k.Name == kind)?.DefaultShape ?? "rectangle";
        }

        return "rectangle";
    }

    private NodeStyle NodeStyle()
    {
        NodeStyle style;
        do
        {
            style = new NodeStyle
            {
                Fill = Chance(0.4) ? Pick(s_colours) : null,
                Stroke = Chance(0.4) ? Pick(s_colours) : null,
                StrokeWidth = Chance(0.4) ? Math.Abs(Number()) : null,
                TextColor = Chance(0.3) ? Pick(s_colours) : null,
                Font = Chance(0.3) ? Pick(s_fonts) : null,
                Dash = Chance(0.4) ? Pick(Enum.GetValues<Dash>()) : null,
                FontSize = Chance(0.3) ? Math.Abs(Number()) : null,
                Bold = Chance(0.3) ? Chance(0.5) : null,
            };
        }
        while (style == new NodeStyle());

        return style;
    }

    private EdgeStyle EdgeStyle()
    {
        EdgeStyle style;
        do
        {
            style = new EdgeStyle
            {
                Stroke = Chance(0.4) ? Pick(s_colours) : null,
                StrokeWidth = Chance(0.4) ? Math.Abs(Number()) : null,
                Dash = Chance(0.4) ? Pick(Enum.GetValues<Dash>()) : null,
                ArrowStart = Chance(0.4) ? Pick(Enum.GetValues<ArrowHead>()) : null,
                ArrowEnd = Chance(0.4) ? Pick(Enum.GetValues<ArrowHead>()) : null,
            };
        }
        while (style == new EdgeStyle());

        return style;
    }

    private Dictionary<string, JsonElement> Props()
    {
        var props = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (string key in Distinct(s_propKeys, _random.Next(1, 5)))
        {
            props[key] = PropValue(depth: 0);
        }

        return props;
    }

    private JsonElement PropValue(int depth)
    {
        int choice = _random.Next(0, depth >= 2 ? 6 : 8);
        return choice switch
        {
            0 or 1 => JsonSerializer.SerializeToElement(Text()),
            2 => JsonSerializer.SerializeToElement(_random.Next(-1000, 1000)),
            3 => JsonSerializer.SerializeToElement(Number()),
            4 => JsonSerializer.SerializeToElement(Chance(0.5)),
            5 => JsonSerializer.SerializeToElement<object?>(null),
            6 => JsonSerializer.SerializeToElement(Enumerable.Range(0, _random.Next(0, 4)).Select(_ => PropValue(depth + 1)).ToArray()),
            _ => JsonSerializer.SerializeToElement(Distinct(s_propKeys, _random.Next(0, 3)).ToDictionary(k => k, _ => PropValue(depth + 1), StringComparer.Ordinal)),
        };
    }

    private string Text()
    {
        int pieces = _random.Next(1, 4);
        return string.Join(Chance(0.5) ? " " : string.Empty, Enumerable.Range(0, pieces).Select(_ => Pick(s_textPool)));
    }

    private string UniqueId(HashSet<string> taken, string prefix, bool allowDots)
    {
        while (true)
        {
            string id = Chance(0.8) ? prefix + Ident(allowDots) : Ident(allowDots);
            if (taken.Add(id))
            {
                return id;
            }
        }
    }

    private string Ident(bool allowDots)
    {
        const string first = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_";
        string rest = allowDots ? first + "0123456789-." : first + "0123456789-";
        int length = _random.Next(1, 10);
        var chars = new char[length];
        chars[0] = first[_random.Next(first.Length)];
        for (int i = 1; i < length; i++)
        {
            chars[i] = rest[_random.Next(rest.Length)];
        }

        return new string(chars);
    }

    private double Number()
    {
        return _random.Next(0, 6) switch
        {
            0 => _random.Next(-500, 2000),
            1 => Math.Round(_random.NextDouble() * 1000, 2),
            2 => Math.Round(_random.NextDouble() * 100 - 50, 1),
            3 => Pick([0.1 + 0.2, 1e-7, 123456789.123, -0.5, 1e21, 0.30000000000000004, 2.5e-3]),
            4 => _random.NextDouble() * 400,
            _ => _random.Next(0, 3),
        };
    }

    private double Fraction()
    {
        return _random.Next(0, 3) switch
        {
            0 => Pick([0.0, 0.5, 1.0]),
            1 => Math.Round(_random.NextDouble(), 2),
            _ => _random.NextDouble(),
        };
    }

    private DateTimeOffset Timestamp()
    {
        long milliseconds = _random.NextInt64(0, 4_000_000_000_000);
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToOffset(TimeSpan.FromHours(_random.Next(-12, 13)));
    }

    private bool Chance(double probability)
    {
        return _random.NextDouble() < probability;
    }

    private T Pick<T>(IReadOnlyList<T> items)
    {
        return items[_random.Next(items.Count)];
    }

    private IEnumerable<T> Distinct<T>(IReadOnlyList<T> pool, int count)
    {
        return pool.OrderBy(_ => _random.Next()).Take(count).ToList();
    }
}
