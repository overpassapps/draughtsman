using System.Text.Json.Nodes;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests;

/// <summary>
/// The flythrough is an additive, optional member of the document (docs/flythrough.md): schema, JSON, DSL and the
/// lint fallbacks, each pinned in both directions so a check that can never fail cannot stay.
/// </summary>
public class FlythroughTests
{
    private const string DslHeader = "draughtsman 1\ntype architecture\ntitle \"T\"\n";

    private static Flythrough Tour() => new(
    [
        new Keyframe("k_overview", new ViewRect(0, 0, 1280, 800)) { Caption = "The whole system" },
        new Keyframe("k_api", new ViewRect(40.5, 200, 640, 400)) { Caption = "Orders API, \"hot path\"", DurationMs = 2000, HoldMs = 0, Easing = KeyframeEasing.EaseOut },
        new Keyframe("k_db", new ViewRect(-120, 360, 320, 200)) { Easing = KeyframeEasing.Cut },
    ])
    {
        Loop = true,
    };

    private static Document WithTour() => DocumentJsonTests.MinimalDocument() with { Flythrough = Tour() };

    [Fact]
    public void Json_writes_the_tour_last_in_canonical_member_order()
    {
        JsonNode root = JsonNode.Parse(DocumentJson.Serialize(WithTour()))!;

        Assert.Equal(["id", "schemaVersion", "title", "type", "nodes", "edges", "meta", "flythrough"], root.AsObject().Select(p => p.Key));
        JsonObject flythrough = root["flythrough"]!.AsObject();
        Assert.Equal(["keyframes", "loop"], flythrough.Select(p => p.Key));
        JsonObject second = flythrough["keyframes"]![1]!.AsObject();
        Assert.Equal(["id", "view", "caption", "durationMs", "holdMs", "easing"], second.Select(p => p.Key));
        Assert.Equal(["x", "y", "w", "h"], second["view"]!.AsObject().Select(p => p.Key));
        Assert.Equal("easeOut", second["easing"]!.GetValue<string>());
        Assert.Equal("cut", flythrough["keyframes"]![2]!["easing"]!.GetValue<string>());
        Assert.Equal(0, second["holdMs"]!.GetValue<double>());
    }

    [Fact]
    public void Json_omits_the_member_when_there_is_no_tour_and_sparse_keyframes_stay_sparse()
    {
        Assert.Null(JsonNode.Parse(DocumentJson.Serialize(DocumentJsonTests.MinimalDocument()))!["flythrough"]);

        Document bare = DocumentJsonTests.MinimalDocument() with { Flythrough = new Flythrough([new Keyframe("k_1", new ViewRect(0, 0, 10, 10))]) };
        JsonObject keyframe = JsonNode.Parse(DocumentJson.Serialize(bare))!["flythrough"]!["keyframes"]![0]!.AsObject();

        Assert.Equal(["id", "view"], keyframe.Select(p => p.Key));
        Assert.Null(JsonNode.Parse(DocumentJson.Serialize(bare))!["flythrough"]!["loop"]);
    }

    [Fact]
    public void Json_round_trips_the_tour_without_loss_and_keeps_keyframe_order()
    {
        Document original = WithTour();

        Document reread = DocumentJson.Deserialize(DocumentJson.Serialize(original));

        Assert.Equal(["k_overview", "k_api", "k_db"], reread.Flythrough!.Keyframes.Select(k => k.Id));
        Assert.Equal(DocumentJson.Serialize(original), DocumentJson.Serialize(reread));
        Assert.True(reread.Flythrough.Loop);
        Assert.Equal(KeyframeEasing.EaseOut, reread.Flythrough.Keyframes[1].Easing);
        Assert.Null(reread.Flythrough.Keyframes[0].DurationMs);
    }

    [Fact]
    public void A_document_with_a_tour_is_valid_against_the_schema_and_one_without_is_unchanged()
    {
        SchemaCheck with = ContractFiles.Evaluate(DocumentJson.Serialize(WithTour()));
        Assert.True(with.IsValid, ContractFiles.DescribeErrors(with));

        SchemaCheck without = ContractFiles.Evaluate(DocumentJson.Serialize(DocumentJsonTests.MinimalDocument()));
        Assert.True(without.IsValid, ContractFiles.DescribeErrors(without));
        Assert.Equal(Document.CurrentSchemaVersion, WithTour().SchemaVersion);
    }

    /// <summary>The schema's bounds, each in both directions: the value just inside is valid, the one just outside is not.</summary>
    [Theory]
    [InlineData("/flythrough/keyframes/0/view/w", 0.001, 0, "width of zero")]
    [InlineData("/flythrough/keyframes/0/view/h", 0.001, -5, "negative height")]
    [InlineData("/flythrough/keyframes/0/durationMs", 0, -1, "negative duration")]
    [InlineData("/flythrough/keyframes/0/durationMs", 600000, 600001, "duration over ten minutes")]
    [InlineData("/flythrough/keyframes/0/holdMs", 0, -0.5, "negative hold")]
    [InlineData("/flythrough/keyframes/0/holdMs", 600000, 1e9, "hold over ten minutes")]
    public void Schema_bounds_admit_the_inside_value_and_refuse_the_outside_one(string pointer, double inside, double outside, string what)
    {
        Assert.True(IsValidWith(pointer, inside), $"{what}: the boundary value must be valid");
        Assert.False(IsValidWith(pointer, outside), $"{what}: must be refused");
    }

    [Fact]
    public void Schema_refuses_an_empty_tour_an_unknown_easing_and_members_it_does_not_define()
    {
        Assert.False(IsValidAfter(root => root["flythrough"]!["keyframes"] = new JsonArray()), "an empty keyframe list");
        Assert.False(IsValidAfter(root => root["flythrough"]!["keyframes"]![1]!["easing"] = "bounce"), "an unknown easing");
        Assert.False(IsValidAfter(root => root["flythrough"]!["autoplay"] = true), "an unknown flythrough member");
        Assert.False(IsValidAfter(root => root["flythrough"]!["keyframes"]![0]!["zoom"] = 2), "an unknown keyframe member");
        Assert.False(IsValidAfter(root => root["flythrough"]!["keyframes"]![0]!.AsObject().Remove("view")), "a keyframe with no view");
        Assert.False(IsValidAfter(root => root["flythrough"]!["keyframes"]![0]!["id"] = "1 bad id"), "a keyframe id that is not an id");
        Assert.False(IsValidAfter(root => root["flythrough"] = new JsonObject()), "a tour with no keyframes member");
        Assert.False(IsValidAfter(root => root["flythrough"]!["loop"] = "yes"), "a loop that is not a boolean");
        Assert.True(IsValidAfter(root => root["flythrough"]!["keyframes"]![1]!["easing"] = "linear"), "control: a known easing is valid");
        Assert.True(IsValidAfter(root => root["flythrough"]!.AsObject().Remove("loop")), "control: loop is optional");
    }

    [Fact]
    public void Schema_version_stays_one_with_a_tour()
    {
        Assert.Equal(1, JsonNode.Parse(DocumentJson.Serialize(WithTour()))!["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Dsl_writes_the_tour_after_the_edges_in_tour_order()
    {
        Document document = WithTour() with { Nodes = [new Node("n_a", "service", "A", "rounded") { Geometry = new Geometry(0, 0, 160, 80) }] };

        string printed = Dsl.Print(document);

        string[] lines = printed.TrimEnd('\n').Split('\n');
        int first = Array.FindIndex(lines, l => l.StartsWith("flythrough", StringComparison.Ordinal));
        Assert.True(first > Array.FindIndex(lines, l => l.StartsWith("node ", StringComparison.Ordinal)));
        Assert.Equal(string.Empty, lines[first - 1]);
        Assert.Equal(
            [
                "flythrough loop=true",
                "keyframe k_overview @ 0,0 1280x800 caption=\"The whole system\"",
                "keyframe k_api @ 40.5,200 640x400 caption=\"Orders API, \\\"hot path\\\"\" duration=2000 hold=0 easing=easeOut",
                "keyframe k_db @ -120,360 320x200 easing=cut",
            ],
            lines[first..]);
    }

    [Fact]
    public void Dsl_round_trips_the_tour_byte_for_byte()
    {
        Document original = WithTour();

        string printed = Dsl.Print(original);
        ParseResult parsed = Dsl.Parse(printed);

        Assert.Empty(parsed.Warnings);
        Assert.Equal(DocumentJson.Serialize(original), DocumentJson.Serialize(parsed.Document));
        Assert.Equal(printed, Dsl.Print(parsed.Document));
    }

    [Fact]
    public void Dsl_keeps_an_explicit_loop_false_distinct_from_no_loop_setting()
    {
        Document explicitFalse = DocumentJsonTests.MinimalDocument() with { Flythrough = new Flythrough([new Keyframe("k_1", new ViewRect(0, 0, 10, 10))]) { Loop = false } };
        Document unset = DocumentJsonTests.MinimalDocument() with { Flythrough = new Flythrough([new Keyframe("k_1", new ViewRect(0, 0, 10, 10))]) };

        Assert.Contains("flythrough loop=false", Dsl.Print(explicitFalse), StringComparison.Ordinal);
        Assert.DoesNotContain("flythrough", Dsl.Print(unset), StringComparison.Ordinal);
        Assert.False(Dsl.Parse(Dsl.Print(explicitFalse)).Document.Flythrough!.Loop);
        Assert.Null(Dsl.Parse(Dsl.Print(unset)).Document.Flythrough!.Loop);
    }

    [Fact]
    public void Dsl_prints_nothing_for_a_tour_with_no_keyframes_and_a_document_without_one_is_unchanged()
    {
        Document hollow = DocumentJsonTests.MinimalDocument() with { Flythrough = new Flythrough([]) { Loop = true } };

        Assert.Equal(Dsl.Print(DocumentJsonTests.MinimalDocument()), Dsl.Print(hollow));
        Assert.DoesNotContain("keyframe", Dsl.Print(DocumentJsonTests.MinimalDocument()), StringComparison.Ordinal);
        Assert.Null(Dsl.Parse(Dsl.Print(hollow)).Document.Flythrough);
    }

    [Fact]
    public void Dsl_reads_keyframes_in_the_order_they_are_written_wherever_they_sit()
    {
        ParseResult result = Dsl.Parse(
            DslHeader +
            "keyframe k_b @ 10,10 100x50\n" +
            "node n_a service \"A\" @ 0,0\n" +
            "keyframe k_a @ 0,0 200x100 easing=linear\n");

        Assert.Equal(["k_b", "k_a"], result.Document.Flythrough!.Keyframes.Select(k => k.Id));
        Assert.Equal(KeyframeEasing.Linear, result.Document.Flythrough.Keyframes[1].Easing);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("keyframe k_1 @ 0,0 100x0\n", "no area")]
    [InlineData("keyframe k_1 @ 0,0 -5x100\n", "no area")]
    [InlineData("keyframe k_1 @ 0,0 100x50 duration=-1\n", "'duration' must be between 0 and 600000")]
    [InlineData("keyframe k_1 @ 0,0 100x50 duration=600001\n", "'duration' must be between 0 and 600000")]
    [InlineData("keyframe k_1 @ 0,0 100x50 hold=1e9\n", "'hold' must be between 0 and 600000")]
    [InlineData("flythrough loop=true\n", "has no keyframes")]
    public void A_value_the_schema_refuses_is_a_warning_and_the_document_stays_valid(string body, string messagePart)
    {
        ParseResult result = Dsl.Parse(DslHeader + body);

        DslWarning warning = Assert.Single(result.Warnings);
        Assert.Contains(messagePart, warning.Message, StringComparison.Ordinal);
        Assert.True(warning.Line > 0 && warning.Column > 0);
        SchemaCheck schema = ContractFiles.Evaluate(DocumentJson.Serialize(result.Document));
        Assert.True(schema.IsValid, ContractFiles.DescribeErrors(schema));
    }

    [Fact]
    public void A_skipped_keyframe_leaves_the_others_and_a_dropped_timing_restores_the_default()
    {
        ParseResult result = Dsl.Parse(DslHeader + "keyframe k_1 @ 0,0 100x0\nkeyframe k_2 @ 0,0 100x50 duration=-1 hold=3000\n");

        Keyframe only = Assert.Single(result.Document.Flythrough!.Keyframes);
        Assert.Equal("k_2", only.Id);
        Assert.Null(only.DurationMs);
        Assert.Equal(3000, only.HoldMs);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Theory]
    [InlineData("keyframe k_1\n", "needs the view")]
    [InlineData("keyframe k_1 0,0 100x50\n", "needs the view")]
    [InlineData("keyframe k_1 @ 0,0\n", "a size wxh")]
    [InlineData("keyframe 1bad @ 0,0 10x10\n", "not a valid id")]
    [InlineData("keyframe k_1 @ 0,0 10x10 easing=bounce\n", "'easing' must be one of cut, linear, easeIn, easeOut, easeInOut")]
    [InlineData("keyframe k_1 @ 0,0 10x10 zoom=2\n", "unknown keyframe field 'zoom'")]
    [InlineData("keyframe k_1 @ 0,0 10x10 caption=hello\n", "a caption must be quoted")]
    [InlineData("keyframe k_1 @ 0,0 10x10\nkeyframe k_1 @ 5,5 10x10\n", "already used on line 4")]
    [InlineData("flythrough loop=maybe\n", "'loop' must be true or false")]
    [InlineData("flythrough speed=2\n", "unknown flythrough field 'speed'")]
    [InlineData("flythrough loop=true\nflythrough loop=false\n", "already given")]
    public void A_malformed_tour_line_is_a_parse_error_naming_the_line(string body, string messagePart)
    {
        DslParseException error = Assert.Throws<DslParseException>(() => Dsl.Parse(DslHeader + body));

        Assert.Contains(messagePart, error.Detail, StringComparison.Ordinal);
        Assert.True(error.Line >= 4);
    }

    [Fact]
    public void A_document_with_no_tour_reads_exactly_as_it_did_before()
    {
        ParseResult result = Dsl.Parse(DslHeader + "node n_a service \"A\" @ 0,0\n");

        Assert.Null(result.Document.Flythrough);
        Assert.DoesNotContain("flythrough", DocumentJson.Serialize(result.Document), StringComparison.Ordinal);
    }

    /// <summary>The tour is a presentation, not part of the graph: the Mermaid export is the same with it or without, and never says its captions.</summary>
    [Fact]
    public void Mermaid_export_ignores_the_tour()
    {
        Document withTour = DocumentJson.Deserialize(ContractFiles.ReadExample());
        Document withoutTour = withTour with { Flythrough = null };
        Assert.NotNull(withTour.Flythrough);

        MermaidExportResult tour = MermaidExporter.Export(withTour);
        MermaidExportResult plain = MermaidExporter.Export(withoutTour);

        Assert.Equal(plain.Text, tour.Text);
        Assert.Equal(plain.Warnings, tour.Warnings);
        foreach (Keyframe keyframe in withTour.Flythrough!.Keyframes)
        {
            Assert.DoesNotContain(keyframe.Caption!, tour.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(keyframe.Id, tour.Text, StringComparison.Ordinal);
        }

        // The other direction: the export is not blind to the graph, so the equality above says something.
        Document renamed = withTour with { Nodes = withTour.Nodes.Select(n => n.Id == "n_web" ? n with { Label = "Storefront" } : n).ToList() };
        Assert.NotEqual(tour.Text, MermaidExporter.Export(renamed).Text);
    }

    private static bool IsValidWith(string pointer, double value)
    {
        return IsValidAfter(root => Set(root, pointer, value));
    }

    private static bool IsValidAfter(Action<JsonNode> mutate)
    {
        JsonNode root = JsonNode.Parse(DocumentJson.Serialize(WithTour()))!;
        mutate(root);
        return ContractFiles.Evaluate(root.ToJsonString()).IsValid;
    }

    private static void Set(JsonNode root, string pointer, double value)
    {
        JsonNode node = root;
        string[] path = pointer.Trim('/').Split('/');
        foreach (string step in path[..^1])
        {
            node = int.TryParse(step, out int index) ? node[index]! : node[step]!;
        }

        node[path[^1]] = value;
    }
}
