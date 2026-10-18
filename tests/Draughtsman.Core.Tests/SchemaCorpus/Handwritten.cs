using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Draughtsman.Core.Tests.SchemaCorpus;

/// <summary>
/// The corpus's directed probes. The mutation generator is blind to the schema on purpose; these are the opposite: each group aims at
/// something a replacement validator could plausibly get wrong, written from knowing how validators differ: regular-expression anchors
/// and character classes, text length (UTF-16 units, code points or grapheme clusters), integer-ness and equality of number spellings,
/// RFC 3339 corners, duplicate and escaped member names, JSON pointer escaping in locations, huge and deeply nested input, and text that
/// is not JSON at all. A probe that lands on an invalid document is as useful as one that lands on a valid one: the recorded outcome is
/// whatever the validator the corpus was recorded against did.
/// </summary>
internal static class Handwritten
{
    /// <summary>A valid document that has every kind of node the schema describes, so a probe can replace one value and isolate it.</summary>
    internal const string BaseDocument =
        "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"T\",\"type\":\"freeform\",\"theme\":{\"palette.accent\":\"#fff\"},"
        + "\"nodes\":[{\"id\":\"n1\",\"kind\":\"service\",\"label\":\"A\",\"shape\":\"rectangle\",\"geometry\":{\"x\":0,\"y\":0,\"w\":100,\"h\":50,\"rotation\":0},"
        + "\"style\":{\"fill\":\"#fff\",\"stroke\":\"red\",\"strokeWidth\":1,\"textColor\":\"token:text\",\"font\":\"Inter\",\"dash\":\"solid\",\"fontSize\":12,\"bold\":true},"
        + "\"props\":{\"a\":1},\"parent\":\"c1\",\"ports\":[{\"id\":\"p1\",\"x\":0.5,\"y\":1}]},{\"id\":\"c1\",\"kind\":\"container\",\"label\":\"C\",\"shape\":\"rounded\"}],"
        + "\"edges\":[{\"id\":\"e1\",\"kind\":\"flow\",\"label\":\"go\",\"from\":{\"node\":\"n1\",\"port\":\"p1\"},\"to\":{\"node\":\"c1\"},"
        + "\"routing\":{\"mode\":\"orthogonal\",\"waypoints\":[{\"x\":1,\"y\":2}]},"
        + "\"style\":{\"stroke\":\"#000\",\"strokeWidth\":2,\"dash\":\"dashed\",\"arrowStart\":\"none\",\"arrowEnd\":\"arrow\"},\"props\":{\"b\":[1,2]}}],"
        + "\"meta\":{\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-01T00:00:00.123+01:00\",\"createdBy\":\"u\","
        + "\"aiProvenance\":[{\"agent\":\"a\",\"model\":\"m\",\"at\":\"2026-01-01T00:00:00Z\",\"action\":\"created\",\"elementIds\":[\"n1\"]}]},"
        + "\"flythrough\":{\"keyframes\":[{\"id\":\"k1\",\"view\":{\"x\":0,\"y\":0,\"w\":10,\"h\":10},\"caption\":\"c\",\"durationMs\":1,\"holdMs\":1,\"easing\":\"cut\"}],\"loop\":false}}";

    private static readonly string[] s_ids =
    [
        "n_1", "n.1", "n-1", "_", "a", "Z", "a1", "_1", "a.b-c_d", "1a", "-a", ".a", "a b", "a\n", "\na", "a\r\n", "a\r", "a\u2028", "a\u0000", "\u00e9",
        "a\u00e9", "\uff21", "\u0663", "a\u0301", "\ud83d\ude00", "a\ud83d\ude00", "", " ", "  ", "a ", " a", "a\t", "#", "a/b", "a:b", "a@b", "a$", "$a", "~", "col:x",
        "n_1\n\n", "n_1\u000b", "n_1\u0085", new string('a', 1), new string('a', 127), new string('a', 128), new string('a', 129), new string('a', 200),
        "_" + new string('a', 127), "_" + new string('a', 128), string.Concat(Enumerable.Repeat("e\u0301", 60)), "a" + string.Concat(Enumerable.Repeat("\u0301", 130)),
        string.Concat(Enumerable.Repeat("\ud83d\udc68\u200d\ud83d\udc69\u200d\ud83d\udc67", 129)), new string('a', 128) + "\n", new string('a', 129) + "\n",
    ];

    private static readonly string[] s_portRefs =
    [
        "p1", "col:x", "col:", "col:a%20b", "col:%", "col:%4", "col:%zz", "col:\u00e9", "col:a b", "col:a.b", "col:-", "col:_", "col:x\n", "col:" + new string('a', 124),
        "col:" + new string('a', 125), "Col:x", "col", "col::x", "col:x:y", "x:y", "col:%41%42", "col:a-b_c", "col:a%2Fb", "col:é\n",
    ];

    private static readonly string[] s_shapes =
    [
        "rectangle", "rounded", "diamond", "cylinder", "pill", "hexagon", "parallelogram", "document", "predefined-process", "manual-input", "off-page-connector",
        "delay", "manual-operation", "display", "stored-data", "multi-document", "merge", "extract", "on-page-connector", "summing-junction", "or", "collate", "sort",
        "loop-limit", "internal-storage", "card", "text", "image", "custom:lib/name", "custom:a-b_c/d.e", "custom:/x", "custom:a/", "custom:a", "custom:a/b/c",
        "custom:a b/c", "custom:\u00e9/x", "Rectangle", "RECTANGLE", "rectangle ", " rectangle", "rectangle\n", "rect", "circle", "rectangle|rounded", "custom:generic/server",
        "custom:bpmn/task", "custom:state/initial", "custom:swimlane/pool", "custom:er/entity", "custom:uml/class", "custom:org/role", "", "custom:", "custom:x/y\n",
        "custom:x/y.", "custom:x/.", "custom:x_y/z-w", "custom:X/Y", "preparation", "ellipse", "triangle", "custom:a/b c", "custom:a\n/b",
    ];

    private static readonly string[] s_colours =
    [
        "#fff", "#FFF", "#ffff", "#ffffff", "#ffffffff", "#ffffffg", "#ff", "#fffff", "#fffffff", "#fffffffff", "#", "#xyz", "rgb(0,0,0)", "rgb(0 0 0)", "rgb(0 0 0 / 50%)",
        "rgba(0,0,0,.5)", "hsl(120, 100%, 50%)", "hsla(120,100%,50%,0.5)", "hwb(120 0% 0%)", "lab(50% 40 59.5)", "lch(52% 72 56)", "oklab(0.5 0.1 0.1)", "oklch(0.5 0.2 30)",
        "color(display-p3 1 0 0)", "RGB(0,0,0)", "rgb()", "rgb(", "rgb(0,0,0", "rgb(0,0,0))", "rgb(0,0,0)x", "rgb(0,0,0) ", "rgb (0,0,0)", "rgb(0,0,0)\n", "url(x)", "var(--x)",
        "calc(1px)", "rgb(url(x))", "rgb(0;0;0)", "rgb(\u00e9)", "rgb(a_b)", "token:text", "token:a.b-c_d", "token:", "token: x", "token:\u00e9", "token:x\n", "Token:x", "red",
        "Red", "RED", "red1", "re d", " red", "red ", "red\n", "transparent", "currentColor", "inherit", "none", "", new string('a', 63), new string('a', 64), new string('a', 65),
        "token:" + new string('a', 58), "token:" + new string('a', 59), "#" + new string('f', 63), "rgb(" + new string('0', 59) + ")", "rgb(" + new string('0', 60) + ")",
        new string('\u00e9', 64), new string('\u00e9', 65), string.Concat(Enumerable.Repeat("e\u0301", 40)), string.Concat(Enumerable.Repeat("e\u0301", 70)),
        "a" + string.Concat(Enumerable.Repeat("\u0301", 70)), "red\r\n", "red\r", "\nred", "rgb(0,0,0)\n\n", "token:x\r\n",
        string.Concat(Enumerable.Repeat("\ud83d\udc68\u200d\ud83d\udc69\u200d\ud83d\udc67", 30)), "rgb(1.5e3,+2,-3)", "rgb(100%,0%,0%)", "hsl(1turn 50% 50%)", "color(srgb 1 0 0 / .5)",
        "COLOR(srgb 1 0 0)", "Rgb(0,0,0)", "oklch(.5 .2 30deg)", "#ABCDEF", "#abcdef12", "#12345g", "#0000",
    ];

    private static readonly string[] s_enumValues =
    [
        "flowchart", "architecture", "freeform", "sequence", "solid", "dashed", "dotted", "none", "arrow", "open", "diamond", "circle", "one", "many", "zeroOrOne",
        "oneOrMany", "zeroOrMany", "oneAndOnlyOne", "inheritance", "aggregation", "composition", "orthogonal", "straight", "curved", "cut", "linear", "easeIn", "easeOut",
        "easeInOut", "created", "updated", "removed", "", "Solid", "SOLID", " solid", "solid ", "solid\n", "dashed2", "NONE", "none ", "ease-in", "easein", "bounce", "create",
        "delete", "sequnce", "Flowchart", "flowchart\n", "zeroormany", "ZeroOrMany", "easeinout", "\u00e9", "dash", "\u0000", "arrow\r\n",
    ];

    private static readonly string[] s_rawNonStrings = ["null", "1", "0", "-1", "1.5", "true", "false", "[]", "[\"solid\"]", "{}", "{\"a\":1}", "[[]]", "[null]"];

    private static readonly string[] s_dateTimes =
    [
        "2026-01-01T00:00:00Z", "2026-01-01t00:00:00z", "2026-01-01T00:00:00.1Z", "2026-01-01T00:00:00.123456789Z", "2026-01-01T00:00:00.000000000000000000001Z",
        "2026-01-01T00:00:00+00:00", "2026-01-01T00:00:00-00:00", "2026-01-01T00:00:00+23:59", "2026-01-01T00:00:00-23:59", "2026-01-01T00:00:00+24:00",
        "2026-01-01T00:00:00+00:60", "2026-01-01T00:00:00+0000", "2026-01-01T00:00:00+00", "2026-01-01T00:00:00Z0", "2026-01-01 00:00:00Z", "2026-01-01_00:00:00Z",
        "2026-01-01T24:00:00Z", "2026-01-01T23:59:60Z", "2026-01-01T23:59:60+00:00", "2026-01-01T00:59:60+01:00", "2026-01-01T12:00:60Z", "2026-01-01T23:59:61Z",
        "2026-01-01T23:59:60-01:00", "2026-01-01T22:59:60-01:00", "2026-01-01T23:00:60+00:01", "2026-02-29T00:00:00Z", "2024-02-29T00:00:00Z", "2000-02-29T00:00:00Z",
        "1900-02-29T00:00:00Z", "2026-04-31T00:00:00Z", "2026-00-10T00:00:00Z", "2026-13-01T00:00:00Z", "2026-01-00T00:00:00Z", "2026-01-32T00:00:00Z",
        "0000-01-01T00:00:00Z", "9999-12-31T23:59:59Z", "0001-01-01T00:00:00Z", "2026-1-1T00:00:00Z", "26-01-01T00:00:00Z", "20260101T000000Z", "2026-01-01T00:00Z",
        "2026-01-01T00:00:00", "2026-01-01", "00:00:00Z", "", " 2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z ", "2026-01-01T00:00:00Z\n", "\n2026-01-01T00:00:00Z",
        "2026-01-01T00:00:00Z\r\n", "2026-01-01T00:00:00\nZ", "\u0662\u0660\u0662\u0666-\u0660\u0661-\u0660\u0661T\u0660\u0660:\u0660\u0660:\u0660\u0660Z",
        "\uff12\uff10\uff12\uff16-\uff10\uff11-\uff10\uff11T\uff10\uff10:\uff10\uff10:\uff10\uff10Z", "2026-01-01T00:00:00.Z", "2026-01-01T00:00:00,123Z",
        "2026-01-01T00:00:00.1.2Z", "2026-01-01T00:00:00ZZ", "2026-01-01T00:00:00+01:00Z", "2026-01-01T00:00:00 Z", "2026-01-01T00:00:00 +01:00", "2026-01-01TT00:00:00Z",
        "2026-01-01T00:00:00Z+01:00", "yesterday", "now", "1", "null", "true", "2026-01-01T0:00:00Z", "2026-01-01T00:0:00Z", "2026-01-01T00:00:0Z", "2026-01-01T000:00:00Z",
        "2026-01-01T00:00:00+1:00", "2026-01-01T00:00:00+01:0", "2026-01-01T00:00:00+001:00", "2026-01-01T00:00:00 +01:00", "+002026-01-01T00:00:00Z", "-2026-01-01T00:00:00Z",
        "2026-01-01T00:00:00.123456789012345678901234567890Z", "2026-01-01T00:00:00z\n", "2026-01-01T00:00:00+01:00\n", "2026-02-28T23:59:60Z", "2026-12-31T23:59:60Z",
        "2026-06-30T23:59:60Z", "2026-01-01T00:00:00\u212a", "2026-01-01\u212a00:00:00Z", "2026-01-01T00:00:00Z\u2028", "\u00a02026-01-01T00:00:00Z",
    ];

    private static readonly string[] s_anyStrings =
    [
        "", " ", "x", "Hello", "caf\u00e9", "\u65e5\u672c\u8a9e", "\ud83d\ude00", "line1\nline2", "tab\there", "quote\"inside", "back\\slash", "nul\u0000nul", "\u2028\u2029",
        "e\u0301", new string('x', 1000), new string('x', 100000), "\ud83d\udc68\u200d\ud83d\udc69\u200d\ud83d\udc67", "\ufeffbom", "\u0085", "<script>alert(1)</script>", "&amp;", "'",
    ];

    public static IEnumerable<CorpusCase> All()
    {
        var cases = new List<CorpusCase>();
        void Add(string group, string name, string json) => cases.Add(new CorpusCase($"hand:{group}|{name}", json));

        string Replace(string pointer, string raw) => Mutations.WithValue(BaseDocument, pointer, raw) ?? throw new InvalidOperationException(pointer);

        Add("base", "valid", BaseDocument);

        // Strings that go through a pattern, an enum, a format or a length limit, at every site that has one.
        foreach (string pointer in new[] { "/id", "/nodes/0/id", "/edges/0/id", "/nodes/0/parent", "/edges/0/from/node", "/edges/0/to/node", "/nodes/0/ports/0/id", "/flythrough/keyframes/0/id", "/meta/aiProvenance/0/elementIds/0" })
        {
            foreach (string value in s_ids.Distinct(StringComparer.Ordinal))
            {
                Add("id", $"{pointer}={Describe(value)}", Replace(pointer, Js(value)));
            }
        }

        foreach (string value in s_portRefs.Distinct(StringComparer.Ordinal))
        {
            Add("portref", Describe(value), Replace("/edges/0/from/port", Js(value)));
            Add("portref-to", Describe(value), WithPort(value));
        }

        foreach (string value in s_shapes.Distinct(StringComparer.Ordinal))
        {
            Add("shape", Describe(value), Replace("/nodes/0/shape", Js(value)));
        }

        foreach (string pointer in new[] { "/nodes/0/style/fill", "/nodes/0/style/stroke", "/nodes/0/style/textColor", "/edges/0/style/stroke" })
        {
            foreach (string value in s_colours.Distinct(StringComparer.Ordinal))
            {
                Add("colour", $"{pointer}={Describe(value)}", Replace(pointer, Js(value)));
            }
        }

        foreach (string pointer in new[] { "/type", "/nodes/0/style/dash", "/edges/0/style/dash", "/edges/0/style/arrowStart", "/edges/0/style/arrowEnd", "/edges/0/routing/mode", "/flythrough/keyframes/0/easing", "/meta/aiProvenance/0/action" })
        {
            foreach (string value in s_enumValues.Distinct(StringComparer.Ordinal))
            {
                Add("enum", $"{pointer}={Describe(value)}", Replace(pointer, Js(value)));
            }

            foreach (string raw in s_rawNonStrings)
            {
                Add("enum", $"{pointer}=<raw {raw}>", Replace(pointer, raw));
            }
        }

        foreach (string pointer in new[] { "/meta/createdAt", "/meta/updatedAt", "/meta/aiProvenance/0/at" })
        {
            foreach (string value in s_dateTimes.Distinct(StringComparer.Ordinal))
            {
                Add("datetime", $"{pointer}={Describe(value)}", Replace(pointer, Js(value)));
            }

            foreach (string raw in s_rawNonStrings)
            {
                Add("datetime", $"{pointer}=<raw {raw}>", Replace(pointer, raw));
            }
        }

        foreach (string pointer in new[]
        {
            "/schemaVersion", "/nodes/0/geometry/x", "/nodes/0/geometry/y", "/nodes/0/geometry/w", "/nodes/0/geometry/h", "/nodes/0/geometry/rotation", "/nodes/0/style/strokeWidth",
            "/nodes/0/style/fontSize", "/nodes/0/ports/0/x", "/nodes/0/ports/0/y", "/edges/0/style/strokeWidth", "/edges/0/routing/waypoints/0/x", "/edges/0/routing/waypoints/0/y",
            "/flythrough/keyframes/0/view/x", "/flythrough/keyframes/0/view/y", "/flythrough/keyframes/0/view/w", "/flythrough/keyframes/0/view/h",
            "/flythrough/keyframes/0/durationMs", "/flythrough/keyframes/0/holdMs", "/nodes/0/props/a",
        })
        {
            foreach ((string name, string raw) in Mutations.NumberPool)
            {
                Add("number", $"{pointer}={name}", Replace(pointer, raw));
            }
        }

        foreach ((string name, string raw) in new (string, string)[]
        {
            ("e99999999999", "1e99999999999"), ("e-99999999999", "1e-99999999999"), ("-e99999999999", "-1e99999999999"), ("0e99999999999", "0e99999999999"),
            ("400-digits", "1" + new string('0', 400)), ("400-fraction-digits", "0." + new string('0', 400) + "1"), ("1+400-zeros", "1." + new string('0', 400)),
            ("1+400-zeros-1", "1." + new string('0', 400) + "1"), ("1e0-with-400-zeros", "1." + new string('0', 400) + "e0"), ("600000+400-zeros", "600000." + new string('0', 400)),
            ("600000+400-zeros-1", "600000." + new string('0', 400) + "1"), ("e-0", "1e-0"), ("1e-0000000001", "1e-0000000001"), ("1e00000", "1e00000"),
        })
        {
            foreach (string pointer in new[] { "/schemaVersion", "/nodes/0/geometry/w", "/nodes/0/ports/0/x", "/flythrough/keyframes/0/view/w", "/flythrough/keyframes/0/durationMs" })
            {
                Add("number-extreme", $"{pointer}={name}", Replace(pointer, raw));
            }
        }

        foreach (string pointer in new[] { "/title", "/nodes/0/kind", "/nodes/0/label", "/edges/0/kind", "/edges/0/label", "/meta/createdBy", "/flythrough/keyframes/0/caption", "/theme/palette.accent", "/nodes/0/style/font", "/nodes/0/props/a" })
        {
            foreach (string value in s_anyStrings.Distinct(StringComparer.Ordinal))
            {
                Add("string", $"{pointer}={Describe(value)}", Replace(pointer, Js(value)));
            }
        }

        AddStructure(Add);
        AddLoneSurrogates(Add);
        AddSyntax(Add);
        return cases;

        string WithPort(string value) => Replace("/edges/0/to", "{\"node\":\"c1\",\"port\":" + Js(value) + "}");
    }

    private static void AddStructure(Action<string, string, string> add)
    {
        const string meta = "\"meta\":{\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-01T00:00:00Z\",\"createdBy\":\"u\"}";
        string Doc(string head = "", string nodes = "[]", string edges = "[]", string tail = "") =>
            "{" + head + "\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":" + nodes + ",\"edges\":" + edges + "," + meta + tail + "}";
        const string node = "{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\"}";

        // Duplicate member names: JSON parsers disagree about which one counts.
        add("dup", "title-bad-then-ok", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":5,\"title\":\"ok\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("dup", "title-ok-then-bad", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"ok\",\"title\":5,\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("dup", "title-bad-bad", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":5,\"title\":6,\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("dup", "id-ok-then-bad-pattern", "{\"id\":\"d1\",\"id\":\"1x\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("dup", "id-bad-pattern-then-ok", "{\"id\":\"1x\",\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("dup", "unknown-twice", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"zz\":1,\"zz\":2,\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("dup", "unknown-and-known", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"zz\":1,\"title\":\"u\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("dup", "required-only-second-occurrence", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + ",\"id\":\"d2\"}");
        add("dup", "schemaVersion-1-then-2", "{\"id\":\"d1\",\"schemaVersion\":1,\"schemaVersion\":2,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("dup", "schemaVersion-2-then-1", "{\"id\":\"d1\",\"schemaVersion\":2,\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("dup", "node-id-twice", Doc(nodes: "[{\"id\":\"n\",\"id\":\"1x\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\"}]"));
        add("dup", "node-id-twice-reversed", Doc(nodes: "[{\"id\":\"1x\",\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\"}]"));
        add("dup", "style-fill", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"style\":{\"fill\":\"url(x)\",\"fill\":\"red\"}}]"));
        add("dup", "style-fill-reversed", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"style\":{\"fill\":\"red\",\"fill\":\"url(x)\"}}]"));
        add("dup", "theme-key", Doc(head: "\"theme\":{\"a\":1,\"a\":\"x\"},"));
        add("dup", "theme-key-reversed", Doc(head: "\"theme\":{\"a\":\"x\",\"a\":1},"));
        add("dup", "geometry-x", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"geometry\":{\"x\":\"a\",\"x\":1,\"y\":0,\"w\":1,\"h\":1}}]"));
        add("dup", "props-key", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"props\":{\"a\":1,\"a\":null}}]"));
        add("dup", "props-twice", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"props\":{},\"props\":5}]"));
        add("dup", "props-twice-reversed", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"props\":5,\"props\":{}}]"));
        add("dup", "meta-twice", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[],\"meta\":{},\"meta\":{\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-01T00:00:00Z\",\"createdBy\":\"u\"}}");
        add("dup", "nodes-twice", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[1],\"nodes\":[],\"edges\":[]," + meta + "}");

        // Escaped member names are the same member.
        add("escape", "unicode-escaped-names", "{\"\\u0069d\":\"d1\",\"schema\\u0056ersion\":1,\"\\u0074itle\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("escape", "unicode-escaped-unknown", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"\\u007a\\u007a\":1,\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("escape", "slash-escaped-value", Doc(nodes: "[{\"id\":\"n\\/1\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\"}]"));
        add("escape", "escaped-value-matches-pattern", Doc(nodes: "[{\"id\":\"\\u006e_1\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"\\u0072ectangle\"}]"));

        // Order of members and of errors.
        add("order", "geometry-w-before-y", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"geometry\":{\"w\":-1,\"x\":1,\"y\":\"2\",\"h\":1}}]"));
        add("order", "geometry-reversed", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"geometry\":{\"h\":-1,\"w\":-1,\"y\":\"2\",\"x\":\"1\"}}]"));
        add("order", "node-members-reversed", Doc(nodes: "[{\"shape\":1,\"label\":2,\"kind\":3,\"id\":4}]"));
        add("order", "root-members-reversed", "{\"meta\":1,\"edges\":2,\"nodes\":3,\"type\":4,\"title\":5,\"schemaVersion\":6,\"id\":7}");
        add("order", "unknown-b-before-a", "{\"b\":1,\"a\":2,\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("order", "unknown-many", "{\"z1\":1,\"y2\":2,\"x3\":3,\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + ",\"a4\":4}");
        add("order", "required-all-missing", "{}");
        add("order", "required-all-missing-with-unknown", "{\"zz\":1}");
        add("order", "required-some-missing", "{\"title\":\"t\",\"edges\":[]}");
        add("order", "node-required-missing-and-unknown", Doc(nodes: "[{\"zz\":1,\"label\":\"l\"}]"));
        add("order", "nested-errors-three-levels", Doc(nodes: "[{\"id\":1,\"kind\":2,\"label\":3,\"shape\":4,\"geometry\":{\"x\":\"a\",\"y\":\"b\",\"w\":-1,\"h\":-2,\"rotation\":\"c\"},\"style\":{\"fill\":1,\"bold\":\"x\"},\"ports\":[{\"id\":1,\"x\":5,\"y\":-5}]}]",
            edges: "[{\"id\":1,\"kind\":2,\"from\":{\"node\":3,\"port\":4},\"to\":5,\"routing\":{\"mode\":\"x\",\"waypoints\":[{\"x\":\"a\"},5]},\"style\":{\"arrowEnd\":\"x\"}}]"));
        add("order", "many-bad-nodes", Doc(nodes: "[1,2,3,4,5,6,7,8,9,10,11,12]"));
        add("order", "bad-nodes-and-edges-interleaved", Doc(nodes: "[1," + node + ",2," + node + ",3]", edges: "[4,5]"));

        // Member names that need escaping in a JSON pointer.
        add("pointer", "theme-keys", Doc(head: "\"theme\":{\"a/b\":1,\"c~d\":2,\"\":3,\"\\u00e9\":4,\"x y\":5,\"%41\":6,\"~0\":7,\"~1\":8,\"a.b\":9,\"[0]\":10,\"#\":11,\"\\\"\":12,\"\\\\\":13,\"\\n\":14,\"\\u0000\":15},"));
        add("pointer", "unknown-keys", "{\"a/b\":1,\"c~d\":2,\"\":3,\"\\u00e9\":4,\"x y\":5,\"%41\":6,\"~0\":7,\"~1\":8,\"\\\"\":9,\"\\n\":10,\"\\ud83d\\ude00\":11,\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        add("pointer", "unknown-in-node", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"a/b\":1,\"c~d\":2,\"\":3}]"));

        // Empty and minimal.
        add("shape", "empty-object", "{}");
        add("shape", "empty-array", "[]");
        add("shape", "all-empty-containers", Doc(nodes: "[]", edges: "[]", head: "\"theme\":{},", tail: ",\"flythrough\":{\"keyframes\":[]}"));
        add("shape", "empty-meta", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[],\"meta\":{}}");
        add("shape", "empty-node", Doc(nodes: "[{}]"));
        add("shape", "empty-edge", Doc(edges: "[{}]"));
        add("shape", "node-null", Doc(nodes: "[null]"));
        add("shape", "nodes-null-items", Doc(nodes: "[null,null]"));
        add("shape", "meta-array", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[],\"meta\":[]}");
        add("shape", "nested-arrays", Doc(nodes: "[[[]]]"));

        // Size.
        var nodes3000 = new StringBuilder("[");
        for (int i = 0; i < 3000; i++)
        {
            nodes3000.Append(i == 0 ? "" : ",").Append("{\"id\":\"n_").Append(i).Append("\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"geometry\":{\"x\":").Append(i).Append(",\"y\":0,\"w\":10,\"h\":10}}");
        }

        add("size", "3000-valid-nodes", Doc(nodes: nodes3000.Append(']').ToString()));
        add("size", "300-bad-nodes", Doc(nodes: "[" + string.Join(',', Enumerable.Repeat("{\"id\":\"1\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"x\"}", 300)) + "]"));
        add("size", "title-2-mib", Doc().Replace("\"title\":\"t\"", "\"title\":\"" + new string('x', 2 * 1024 * 1024) + "\"", StringComparison.Ordinal));
        add("size", "label-1-mib", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"" + new string('y', 1024 * 1024) + "\",\"shape\":\"rectangle\"}]"));
        add("size", "fill-1-mib", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"style\":{\"fill\":\"" + new string('a', 1024 * 1024) + "\"}}]"));
        add("size", "id-1-mib", Doc(nodes: "[{\"id\":\"" + new string('a', 1024 * 1024) + "\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\"}]"));
        add("size", "props-20000-keys", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"props\":{" + string.Join(',', Enumerable.Range(0, 20000).Select(i => "\"k" + i + "\":" + i)) + "}}]"));
        add("size", "theme-20000-values", Doc(head: "\"theme\":{" + string.Join(',', Enumerable.Range(0, 20000).Select(i => "\"k" + i + "\":\"v\"")) + "},"));
        add("size", "theme-300-bad-values", Doc(head: "\"theme\":{" + string.Join(',', Enumerable.Range(0, 300).Select(i => "\"k" + i + "\":" + i)) + "},"));
        add("size", "unknown-300-keys", "{" + string.Join(',', Enumerable.Range(0, 300).Select(i => "\"u" + i + "\":" + i)) + ",\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + meta + "}");
        string provenance = "\"meta\":{\"createdAt\":\"2026-01-01T00:00:00Z\",\"updatedAt\":\"2026-01-01T00:00:00Z\",\"createdBy\":\"u\",\"aiProvenance\":[{\"agent\":\"a\",\"at\":\"2026-01-01T00:00:00Z\",\"action\":\"updated\",\"elementIds\":["
            + string.Join(',', Enumerable.Range(0, 20000).Select(i => "\"n_" + i + "\"")) + "]}]}";
        add("size", "element-ids-20000", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[]," + provenance + "}");
        foreach (int count in new[] { 1, 499, 500, 501, 600, 2000 })
        {
            string keyframes = string.Join(',', Enumerable.Range(0, count).Select(i => "{\"id\":\"k" + i + "\",\"view\":{\"x\":0,\"y\":0,\"w\":1,\"h\":1}}"));
            add("size", $"keyframes-{count}", Doc(tail: ",\"flythrough\":{\"keyframes\":[" + keyframes + "]}"));
        }

        // Nesting: System.Text.Json refuses documents deeper than 64.
        foreach (int depth in new[] { 5, 30, 55, 58, 59, 60, 61, 62, 63, 64, 65, 100, 2000 })
        {
            string nested = string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);
            add("depth", $"props-{depth}", Doc(nodes: "[{\"id\":\"n\",\"kind\":\"k\",\"label\":\"l\",\"shape\":\"rectangle\",\"props\":" + nested + "}]"));
            string arrays = new string('[', depth) + new string(']', depth);
            add("depth", $"theme-arrays-{depth}", Doc(head: "\"theme\":{\"a\":" + arrays + "},"));
        }

        // Everything wrong at once.
        add("sink", "all-wrong", "{\"id\":\"1\",\"schemaVersion\":\"1\",\"title\":null,\"type\":\"nope\",\"theme\":[],\"nodes\":{},\"edges\":\"x\",\"meta\":5,\"flythrough\":[],\"zz\":1}");
        add("sink", "all-wrong-deep", Doc(nodes: "[{\"id\":\"1\",\"kind\":null,\"label\":[],\"shape\":\"x\",\"geometry\":{\"x\":\"a\",\"w\":-1,\"zz\":1},\"style\":{\"fill\":\"url(x)\",\"strokeWidth\":-1,\"dash\":\"x\",\"bold\":1,\"zz\":2},\"props\":5,\"parent\":\"1\",\"ports\":[{\"id\":\"1\",\"x\":2},5]}]",
            edges: "[{\"id\":\"1\",\"kind\":1,\"label\":2,\"from\":{\"port\":\"x y\"},\"to\":[],\"routing\":{\"waypoints\":[{\"x\":1},5]},\"style\":{\"arrowStart\":\"x\",\"arrowEnd\":1},\"props\":[]}]",
            head: "\"theme\":{\"a\":1},", tail: ",\"flythrough\":{\"keyframes\":[{\"id\":\"1\",\"view\":{\"x\":0,\"y\":0,\"w\":0,\"h\":0},\"durationMs\":-1,\"holdMs\":999999999,\"easing\":\"x\"}],\"loop\":\"yes\"}"));
        add("sink", "meta-all-wrong", "{\"id\":\"d1\",\"schemaVersion\":1,\"title\":\"t\",\"type\":\"flowchart\",\"nodes\":[],\"edges\":[],\"meta\":{\"createdAt\":1,\"updatedAt\":\"x\",\"createdBy\":2,\"aiProvenance\":[{\"agent\":1,\"model\":2,\"at\":\"x\",\"action\":\"x\",\"elementIds\":[1,\"1\",\"ok\"],\"zz\":1},5]}}");
    }

    /// <summary>Strings no UTF-16 text can hold: a lone surrogate escape is legal JSON but is not a string.</summary>
    private static void AddLoneSurrogates(Action<string, string, string> add)
    {
        (string Name, string Old, string New)[] sites =
        [
            ("title", "\"title\":\"T\"", "\"title\":\"{S}\""), ("node-kind", "\"kind\":\"service\"", "\"kind\":\"{S}\""), ("node-label", "\"label\":\"A\"", "\"label\":\"{S}\""),
            ("node-id", "{\"id\":\"n1\",\"kind\"", "{\"id\":\"{S}\",\"kind\""), ("shape", "\"shape\":\"rectangle\"", "\"shape\":\"{S}\""),
            ("fill", "\"fill\":\"#fff\"", "\"fill\":\"{S}\""), ("font", "\"font\":\"Inter\"", "\"font\":\"{S}\""), ("type", "\"type\":\"freeform\"", "\"type\":\"{S}\""),
            ("created-at", "\"createdAt\":\"2026-01-01T00:00:00Z\"", "\"createdAt\":\"{S}\""), ("created-by", "\"createdBy\":\"u\"", "\"createdBy\":\"{S}\""),
            ("theme-value", "\"palette.accent\":\"#fff\"", "\"palette.accent\":\"{S}\""), ("props-value", "\"props\":{\"a\":1}", "\"props\":{\"a\":\"{S}\"}"),
            ("edge-kind", "\"kind\":\"flow\"", "\"kind\":\"{S}\""), ("from-port", "\"port\":\"p1\"", "\"port\":\"{S}\""), ("edge-dash", "\"dash\":\"dashed\"", "\"dash\":\"{S}\""),
            ("easing", "\"easing\":\"cut\"", "\"easing\":\"{S}\""), ("action", "\"action\":\"created\"", "\"action\":\"{S}\""), ("caption", "\"caption\":\"c\"", "\"caption\":\"{S}\""),
            ("agent", "\"agent\":\"a\"", "\"agent\":\"{S}\""), ("schema-version", "\"schemaVersion\":1", "\"schemaVersion\":\"{S}\""),
            ("unknown-key", "{\"id\":\"d1\"", "{\"{S}\":1,\"id\":\"d1\""), ("theme-key", "\"palette.accent\"", "\"{S}\""), ("props-key", "\"props\":{\"a\":1}", "\"props\":{\"{S}\":1}"),
        ];
        foreach ((string name, string old, _) in sites)
        {
            int first = BaseDocument.IndexOf(old, StringComparison.Ordinal);
            if (first < 0 || BaseDocument.IndexOf(old, first + 1, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException($"'{old}' must appear exactly once in the base document ({name}).");
            }
        }

        foreach (string escaped in new[] { "\\ud800", "\\udc00", "x\\ud800", "\\ud800x", "\\ud800\\ud800", "\\udc00\\ud800", "\\ud83d\\ude00", "\\ud83d\\ude00\\ud83d" })
        {
            foreach ((string name, string old, string replacement) in sites)
            {
                add("surrogate", $"{name}={escaped}", BaseDocument.Replace(old, replacement.Replace("{S}", escaped, StringComparison.Ordinal), StringComparison.Ordinal));
            }
        }
    }

    /// <summary>Text that is not a document, and some that is barely JSON.</summary>
    private static void AddSyntax(Action<string, string, string> add)
    {
        string[] texts =
        [
            "", " ", "\n", "{", "}", "{}", "[]", "null", "nul", "true", "1", "\"x\"", "{\"a\":}", "{\"a\":1,}", "{'a':1}", "{a:1}", "// c\n{}", "/* c */{}", "{} x", "{}{}", "[1,]",
            "NaN", "Infinity", "-Infinity", "{\"a\":01}", "{\"a\":.5}", "{\"a\":1.}", "{\"a\":+1}", "{\"a\":0x10}", "\ufeff{}", "{\"a\":\"\\x\"}", "{\"a\":\"\\u12\"}", "{\"a\":\"\t\"}",
            "{\"a\":\"\n\"}", "{\"a\":1e}", "{\"a\":1e+}", "{\"a\":-}", "{\"a\":--1}", "{\"a\" 1}", "{\"a\":1 \"b\":2}", "{\"a\":[1 2]}", "{,}", "[,]", "\"unterminated", "{\"a\":\"b\"",
            "\u0000", "{\u0000}", "{\"a\":1}\u0000", "tru", "falsey", "nulll", "{\"a\":nul}", "-", "1e1000000000000", "[" + new string('[', 100) + "]", "\t{}\t", "\r\n{}\r\n",
        ];
        for (int i = 0; i < texts.Length; i++)
        {
            add("syntax", $"{i}", texts[i]);
        }

        // Valid JSON documents with odd surroundings.
        add("syntax", "document-leading-whitespace", "  \n\t " + BaseDocument);
        add("syntax", "document-trailing-whitespace", BaseDocument + "  \n\t ");
        // NewLine is explicit: the writer's default is Environment.NewLine, so Windows built CRLF here and the text's fingerprint stopped matching the recorded one.
        add("syntax", "document-pretty", JsonSerializer.Serialize(JsonDocument.Parse(BaseDocument).RootElement, new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }));
        add("syntax", "document-trailing-comma", BaseDocument[..^1] + ",}");
        add("syntax", "document-with-comment", "/* c */" + BaseDocument);
        add("syntax", "document-twice", BaseDocument + BaseDocument);
        add("syntax", "document-in-array", "[" + BaseDocument + "]");
        add("syntax", "document-as-string", JsonSerializer.Serialize(BaseDocument));
    }

    internal static string Js(string value)
    {
        return JsonSerializer.Serialize(value);
    }

    private static string Describe(string value)
    {
        const int Limit = 40;
        var builder = new StringBuilder();
        foreach (char c in value)
        {
            builder.Append(c is >= ' ' and < '\u007f' ? c.ToString() : $"\\u{(int)c:x4}");
            if (builder.Length > Limit)
            {
                string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..6];
                builder.Append("...(").Append(value.Length).Append(" chars #").Append(digest).Append(')');
                break;
            }
        }

        return builder.ToString();
    }
}
