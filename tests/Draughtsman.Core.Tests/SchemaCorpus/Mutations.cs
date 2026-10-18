using System.Text.Json;
using System.Text.Json.Nodes;

namespace Draughtsman.Core.Tests.SchemaCorpus;

/// <summary>
/// Builds the generated half of the corpus: every frozen seed document with one (or two) things changed. The generator knows the
/// shape of a JSON tree and nothing about the schema, so it cannot share the validator's blind spots: at each node of a seed it drops
/// the member, retypes it, gives it hostile strings or numbers, adds unknown members and breaks arrays. Most of a schema's behaviour is
/// the same wherever a node sits in a document, so the full operator set runs once per node CLASS (the pointer with array indexes
/// replaced by <c>*</c>), at the first seed that has the class; every seed then also gets a seeded pseudo-random sample of single and double
/// mutations, so shapes only an old document has still meet it. Everything is deterministic (own generator, ordinal sorting), and the
/// recorded fingerprint of every text fails the run if the generator ever drifts.
/// </summary>
internal static class Mutations
{
    private const int RandomSinglesPerSeed = 50;
    private const int RandomPairsPerSeed = 25;
    private const int CrossSeedPairs = 800;

    /// <summary>Replacements every node takes, as raw JSON text.</summary>
    private static readonly (string Name, string Raw)[] s_replacements =
    [
        ("null", "null"), ("true", "true"), ("false", "false"), ("str", "\"a\""), ("empty-str", "\"\""), ("zero", "0"), ("one", "1"),
        ("fraction", "1.5"), ("negative", "-1"), ("array", "[]"), ("object", "{}"), ("array-of-str", "[\"x\"]"), ("object-of-int", "{\"a\":1}"),
    ];

    private static readonly (string Name, string Raw)[] s_strings =
    [
        ("space", "\" \""), ("a", "\"a\""), ("A", "\"A\""), ("underscore", "\"_\""), ("hyphen", "\"-\""), ("digit", "\"0\""),
        ("trailing-lf", "\"n_1\\n\""), ("trailing-crlf", "\"n_1\\r\\n\""), ("leading-lf", "\"\\nn_1\""), ("trailing-space", "\"n_1 \""),
        ("leading-space", "\" n_1\""), ("inner-space", "\"n 1\""), ("e-acute", "\"\\u00e9\""), ("fullwidth-a", "\"\\uff21\""),
        ("arabic-digit", "\"\\u0663\""), ("emoji", "\"\\ud83d\\ude00\""), ("combining", "\"e\\u0301\""), ("nul", "\"a\\u0000b\""),
        ("len64", Js(new string('a', 64))), ("len65", Js(new string('a', 65))), ("len128", Js(new string('a', 128))),
        ("len129", Js(new string('a', 129))), ("len300", Js(new string('a', 300))), ("len10000", Js(new string('a', 10000))),
        ("combining-x40", Js(string.Concat(Enumerable.Repeat("e\u0301", 40)))), ("combining-x70", Js(string.Concat(Enumerable.Repeat("e\u0301", 70)))),
        ("token", "\"token:x\""), ("hex", "\"#fff\""), ("rgb", "\"rgb(0,0,0)\""), ("custom-shape", "\"custom:a/b\""), ("column-port", "\"col:x\""),
        ("date-time", "\"2026-01-01T00:00:00Z\""), ("date-time-bad", "\"2026-13-01T00:00:00Z\""), ("rectangle", "\"rectangle\""), ("solid", "\"solid\""),
        ("flowchart", "\"flowchart\""),
    ];

    private static readonly (string Name, string Raw)[] s_numbers =
    [
        ("0", "0"), ("-0", "-0"), ("0.0", "0.0"), ("-0.0", "-0.0"), ("0e0", "0e0"), ("1", "1"), ("1.0", "1.0"), ("1e0", "1e0"), ("1E0", "1E0"),
        ("10e-1", "10e-1"), ("100e-2", "100e-2"), ("0.1e1", "0.1e1"), ("1.0e-0", "1.0e-0"), ("1.00e-3", "1.00e-3"), ("0.5", "0.5"), ("-0.5", "-0.5"),
        ("-1", "-1"), ("2", "2"), ("1.5", "1.5"), ("0.1", "0.1"), ("0.10", "0.10"), ("20-nines", "0.99999999999999999999"),
        ("1+20-zeros-1", "1.00000000000000000001"), ("1+36-zeros-1", "1.0000000000000000000000000000000000001"),
        ("36-nines", "0.9999999999999999999999999999999999999"), ("600000", "600000"), ("600001", "600001"), ("599999.999999", "599999.999999"),
        ("600000+tiny", "600000.0000000000000001"), ("1e308", "1e308"), ("1e309", "1e309"), ("1e400", "1e400"), ("-1e400", "-1e400"),
        ("1e-400", "1e-400"), ("-1e-400", "-1e-400"), ("5e-324", "5e-324"), ("30-digits", "123456789012345678901234567890"),
        ("decimal-max", "79228162514264337593543950335"), ("decimal-max+1", "79228162514264337593543950336"), ("2^53+1", "9007199254740993"),
        ("1E+2", "1E+2"), ("1e+0", "1e+0"), ("2e-1", "2e-1"), ("tiny", "0.00000000000000000000000000001"),
    ];

    /// <summary>The number spellings every numeric site of the schema is probed with, by name and raw JSON text.</summary>
    internal static (string Name, string Raw)[] NumberPool => s_numbers;

    private static readonly string[] s_unknownKeys = ["zz", "", "a/b", "a~b", "~1", "é", "a b", "%41", "__proto__", "$ref", "properties"];

    internal sealed record Site(string Pointer, string[] Segments, string Class, JsonValueKind Kind, bool InObject, bool InArray);

    internal sealed record Op(string Name, string? RootText, Func<JsonNode, bool> Apply);

    private sealed record SeedSites(string Name, string Text, List<Site> Sites);

    public static IEnumerable<CorpusCase> Generate(IReadOnlyList<(string Name, string Text)> seeds)
    {
        var all = seeds.Select(s => new SeedSites(s.Name, s.Text, Sites(s.Text))).ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cases = new List<CorpusCase>();
        var seenClasses = new HashSet<string>(StringComparer.Ordinal);
        var firstSites = new List<(SeedSites Seed, Site Site)>();

        foreach (SeedSites seed in all)
        {
            foreach (Site site in seed.Sites)
            {
                if (!seenClasses.Add(site.Class))
                {
                    continue;
                }

                firstSites.Add((seed, site));
                foreach (Op op in OpsFor(site))
                {
                    Emit(cases, ids, seed, [site], [op]);
                }
            }
        }

        foreach (SeedSites seed in all)
        {
            var rng = new Rng(Fnv(seed.Name));
            List<Site> nonRoot = [.. seed.Sites.Where(s => s.Segments.Length > 0)];
            if (nonRoot.Count == 0)
            {
                continue;
            }

            for (int i = 0; i < RandomSinglesPerSeed; i++)
            {
                Site site = nonRoot[rng.Next(nonRoot.Count)];
                List<Op> ops = [.. OpsFor(site)];
                Emit(cases, ids, seed, [site], [ops[rng.Next(ops.Count)]]);
            }

            for (int i = 0; i < RandomPairsPerSeed; i++)
            {
                EmitPair(cases, ids, seed, nonRoot[rng.Next(nonRoot.Count)], nonRoot[rng.Next(nonRoot.Count)], rng);
            }
        }

        var cross = new Rng(Fnv("cross-seed"));
        for (int i = 0; i < CrossSeedPairs; i++)
        {
            (SeedSites seed, Site first) = firstSites[cross.Next(firstSites.Count)];
            List<Site> nonRoot = [.. seed.Sites.Where(s => s.Segments.Length > 0)];
            if (nonRoot.Count == 0)
            {
                continue;
            }

            EmitPair(cases, ids, seed, first.Segments.Length == 0 ? nonRoot[0] : first, nonRoot[cross.Next(nonRoot.Count)], cross);
        }

        return cases;
    }

    /// <summary>The document with the node at <paramref name="pointer"/> replaced by the raw JSON text; null when the path does not exist.</summary>
    internal static string? WithValue(string text, string pointer, string raw)
    {
        string[] segments = pointer.Length == 0
            ? []
            : [.. pointer[1..].Split('/').Select(s => s.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))];
        Op op = SetOp("set", segments, raw);
        if (op.RootText is { } rootText)
        {
            return rootText;
        }

        JsonNode root = JsonNode.Parse(text)!;
        return op.Apply(root) ? root.ToJsonString() : null;
    }

    /// <summary>Every node of a document, root first, in document order.</summary>
    internal static List<Site> Sites(string text)
    {
        JsonNode root = JsonNode.Parse(text)!;
        var sites = new List<Site>();
        Walk(root, [], [], false, false, sites);
        return sites;
    }

    private static void Walk(JsonNode? node, List<string> path, List<string> classPath, bool inObject, bool inArray, List<Site> sites)
    {
        JsonValueKind kind = node?.GetValueKind() ?? JsonValueKind.Null;
        sites.Add(new Site(Pointer(path), [.. path], "/" + string.Join('/', classPath), kind, inObject, inArray));
        if (node is JsonObject obj)
        {
            // The members of a free-form map (props, theme) are one class: the schema treats every key alike, and a document's own key names
            // would otherwise make a class of each. Nothing inside a props value is walked, since props values are not described at all.
            bool freeForm = path.Count > 0 && path[^1] is "props" or "theme";
            bool insideProps = classPath.Contains("props") && path.Count > 0 && path[^1] != "props";
            foreach ((string key, JsonNode? child) in obj)
            {
                path.Add(key);
                classPath.Add(freeForm ? "*" : key);
                if (insideProps)
                {
                    sites.Add(new Site(Pointer(path), [.. path], "/" + string.Join('/', classPath), child?.GetValueKind() ?? JsonValueKind.Null, true, false));
                }
                else
                {
                    Walk(child, path, classPath, true, false, sites);
                }

                classPath.RemoveAt(classPath.Count - 1);
                path.RemoveAt(path.Count - 1);
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
            {
                path.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                classPath.Add("*");
                Walk(array[i], path, classPath, false, true, sites);
                classPath.RemoveAt(classPath.Count - 1);
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    private static string Pointer(IEnumerable<string> segments)
    {
        return string.Concat(segments.Select(s => "/" + s.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));
    }

    internal static IEnumerable<Op> OpsFor(Site site)
    {
        string[] seg = site.Segments;
        foreach ((string name, string raw) in s_replacements)
        {
            yield return SetOp("set:" + name, seg, raw);
        }

        if (seg.Length > 0 && site.InObject)
        {
            yield return new Op("drop", null, root => Edit(root, seg, (container, key, _) => ((JsonObject)container).Remove(key)));
        }

        if (seg.Length > 0 && site.InArray)
        {
            yield return new Op("del", null, root => Edit(root, seg, (container, _, index) =>
            {
                ((JsonArray)container).RemoveAt(index);
                return true;
            }));
        }

        switch (site.Kind)
        {
            case JsonValueKind.String:
                foreach ((string name, string raw) in s_strings)
                {
                    yield return SetOp("str:" + name, seg, raw);
                }

                break;
            case JsonValueKind.Number:
                foreach ((string name, string raw) in s_numbers)
                {
                    yield return SetOp("num:" + name, seg, raw);
                }

                break;
            case JsonValueKind.Object:
                foreach (string key in s_unknownKeys)
                {
                    yield return new Op("add:" + key, null, root => Mutate(root, seg, node =>
                    {
                        if (node is not JsonObject obj || obj.ContainsKey(key))
                        {
                            return false;
                        }

                        obj[key] = 1;
                        return true;
                    }));
                }

                break;
            case JsonValueKind.Array:
                yield return new Op("arr:dup-first", null, root => Mutate(root, seg, node => node is JsonArray { Count: > 0 } a && Add(a, 0, a[0]?.DeepClone())));
                yield return new Op("arr:rep2", null, root => Mutate(root, seg, node => node is JsonArray { Count: > 0 } a && Repeat(a, 2)));
                yield return new Op("arr:rep500", null, root => Mutate(root, seg, node => node is JsonArray { Count: > 0 } a && Repeat(a, 500)));
                yield return new Op("arr:rep501", null, root => Mutate(root, seg, node => node is JsonArray { Count: > 0 } a && Repeat(a, 501)));
                foreach ((string name, string raw) in new[] { ("null", "null"), ("object", "{}"), ("str", "\"s\""), ("num", "1"), ("array", "[]") })
                {
                    yield return new Op("arr:append-" + name, null, root => Mutate(root, seg, node => node is JsonArray a && Add(a, a.Count, JsonNode.Parse(raw))));
                    yield return new Op("arr:prepend-" + name, null, root => Mutate(root, seg, node => node is JsonArray a && Add(a, 0, JsonNode.Parse(raw))));
                }

                yield return new Op("arr:clear", null, root => Mutate(root, seg, node =>
                {
                    if (node is not JsonArray a)
                    {
                        return false;
                    }

                    a.Clear();
                    return true;
                }));
                break;
            default:
                break;
        }
    }

    private static bool Add(JsonArray array, int index, JsonNode? node)
    {
        array.Insert(index, node);
        return true;
    }

    private static bool Repeat(JsonArray array, int count)
    {
        JsonNode? first = array[0];
        if ((first?.ToJsonString().Length ?? 0) > 160)
        {
            return false; // a few hundred copies of a whole node would be megabytes of recorded violations; the keyframe, waypoint and id arrays are the ones with limits
        }

        while (array.Count < count)
        {
            array.Add(first?.DeepClone());
        }

        return true;
    }

    private static Op SetOp(string name, string[] seg, string raw)
    {
        return seg.Length == 0
            ? new Op(name, raw, _ => true)
            : new Op(name, null, root => Edit(root, seg, (container, key, index) =>
            {
                if (container is JsonObject obj)
                {
                    obj[key] = JsonNode.Parse(raw);
                }
                else
                {
                    ((JsonArray)container)[index] = JsonNode.Parse(raw);
                }

                return true;
            }));
    }

    /// <summary>Runs <paramref name="edit"/> on the container of the node at <paramref name="seg"/>; false when the path no longer exists.</summary>
    private static bool Edit(JsonNode root, string[] seg, Func<JsonNode, string, int, bool> edit)
    {
        JsonNode? container = Navigate(root, seg[..^1]);
        string last = seg[^1];
        switch (container)
        {
            case JsonObject obj when obj.ContainsKey(last):
                return edit(obj, last, -1);
            case JsonArray array when int.TryParse(last, out int index) && index >= 0 && index < array.Count:
                return edit(array, last, index);
            default:
                return false;
        }
    }

    private static bool Mutate(JsonNode root, string[] seg, Func<JsonNode, bool> mutate)
    {
        JsonNode? node = Navigate(root, seg);
        return node is not null && mutate(node);
    }

    private static JsonNode? Navigate(JsonNode root, string[] seg)
    {
        JsonNode? current = root;
        foreach (string s in seg)
        {
            current = current switch
            {
                JsonObject obj when obj.TryGetPropertyValue(s, out JsonNode? child) => child,
                JsonArray array when int.TryParse(s, out int i) && i >= 0 && i < array.Count => array[i],
                _ => null,
            };
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    private static void EmitPair(List<CorpusCase> cases, HashSet<string> ids, SeedSites seed, Site a, Site b, Rng rng)
    {
        if (a.Pointer == b.Pointer || a.Segments.Length == 0 || b.Segments.Length == 0)
        {
            return;
        }

        List<Op> opsA = [.. OpsFor(a).Where(o => o.RootText is null)];
        List<Op> opsB = [.. OpsFor(b).Where(o => o.RootText is null)];
        Emit(cases, ids, seed, [a, b], [opsA[rng.Next(opsA.Count)], opsB[rng.Next(opsB.Count)]]);
    }

    private static void Emit(List<CorpusCase> cases, HashSet<string> ids, SeedSites seed, Site[] sites, Op[] ops)
    {
        string id = "mut:" + seed.Name + string.Concat(sites.Zip(ops, (s, o) => "|" + s.Pointer + "|" + o.Name));
        if (ids.Contains(id))
        {
            return;
        }

        string? text;
        if (ops[0].RootText is { } rootText)
        {
            text = rootText;
        }
        else
        {
            JsonNode root = JsonNode.Parse(seed.Text)!;
            text = ops.All(op => op.Apply(root)) ? root.ToJsonString() : null;
        }

        if (text is null)
        {
            return;
        }

        ids.Add(id);
        cases.Add(new CorpusCase(id, text));
    }

    private static string Js(string value)
    {
        return JsonSerializer.Serialize(value);
    }

    private static ulong Fnv(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (char c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    /// <summary>A small linear congruential generator: its sequence is fixed by this code, not by the runtime's version of <see cref="Random"/>.</summary>
    private sealed class Rng(ulong seed)
    {
        private ulong _state = seed;

        public int Next(int bound)
        {
            _state = unchecked((_state * 6364136223846793005UL) + 1442695040888963407UL);
            return (int)((_state >> 33) % (ulong)bound);
        }
    }
}
