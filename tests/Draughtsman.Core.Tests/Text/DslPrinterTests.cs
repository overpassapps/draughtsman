using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests.Text;

public class DslPrinterTests
{
    private static readonly DateTimeOffset s_at = new(2026, 9, 20, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Prints_header_nodes_then_edges_one_line_each()
    {
        Document document = Minimal() with
        {
            Nodes =
            [
                new Node("n_a", "process", "A", "rounded") { Geometry = new Geometry(1.5, -2, 100, 50) { Rotation = 45 } },
                new Node("c_1", "container", "Lane", "rectangle"),
                new Node("n_b", "decision", "B?", "hexagon") { Parent = "c_1", Style = new NodeStyle { Fill = "#FFF", Font = "Inter, 14px" } },
            ],
            Edges = [new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_b") { Port = "in" }) { Label = "Yes" }],
        };

        string printed = Dsl.Print(document);

        const string expected = "draughtsman 1\nid doc_min\ntype flowchart\ntitle \"Minimal\"\nmeta created=2026-09-20T22:00:00.000Z updated=2026-09-20T22:00:00.000Z by=test\n\nnode n_a process \"A\" @ 1.5,-2 100x50 rotation=45\ncontainer c_1 \"Lane\" { n_b }\nnode n_b decision \"B?\" shape=hexagon style.fill=#FFF style.font=\"Inter, 14px\"\n\nedge e_1 flow n_a -> n_b.in \"Yes\"\n";
        Assert.Equal(expected, printed);
    }

    [Fact]
    public void Empty_collections_print_as_absent()
    {
        Document document = Minimal() with
        {
            Theme = new Dictionary<string, string>(),
            Nodes = [new Node("n_a", "process", "A", "rounded") { Style = new NodeStyle(), Props = new Dictionary<string, JsonElement>(), Ports = [] }],
            Edges = [new Edge("e_1", "flow", new Endpoint("n_a"), new Endpoint("n_a")) { Routing = new Routing(RoutingMode.Straight) { Waypoints = [] } }],
            Meta = new Meta(s_at, s_at, "test") { AiProvenance = [] },
        };

        string printed = Dsl.Print(document);
        Document reread = Dsl.Parse(printed).Document;

        Assert.Contains("\nnode n_a process \"A\"\n", printed, StringComparison.Ordinal);
        Assert.Contains("\nedge e_1 flow n_a -> n_a routing=straight\n", printed, StringComparison.Ordinal);
        Assert.Null(reread.Theme);
        Assert.Null(reread.Nodes[0].Style);
        Assert.Null(reread.Nodes[0].Props);
        Assert.Null(reread.Nodes[0].Ports);
        Assert.Null(reread.Edges[0].Routing!.Waypoints);
        Assert.Null(reread.Meta.AiProvenance);
    }

    [Fact]
    public void Reserved_and_awkward_prop_keys_are_quoted_and_strings_always_are()
    {
        var props = new Dictionary<string, JsonElement>
        {
            ["shape"] = JsonSerializer.SerializeToElement("x"),
            ["style.fill"] = JsonSerializer.SerializeToElement("y"),
            ["owner name"] = JsonSerializer.SerializeToElement("z"),
            ["owner"] = JsonSerializer.SerializeToElement("platform"),
            ["n"] = JsonSerializer.SerializeToElement("42"),
            ["list"] = JsonSerializer.SerializeToElement(new object[] { 1, "two", null!, new { a = true } }),
        };
        Document document = Minimal() with { Nodes = [new Node("n_a", "process", "A", "rounded") { Props = props }] };

        string printed = Dsl.Print(document);

        Assert.Contains("node n_a process \"A\" \"shape\"=\"x\" \"style.fill\"=\"y\" \"owner name\"=\"z\" owner=\"platform\" n=\"42\" list=[1,\"two\",null,{\"a\":true}]\n", printed, StringComparison.Ordinal);
        Node reread = Dsl.Parse(printed).Document.Nodes[0];
        Assert.Equal("rounded", reread.Shape);
        Assert.Equal("x", reread.Props!["shape"].GetString());
        Assert.Equal("42", reread.Props["n"].GetString());
    }

    [Fact]
    public void Parent_that_is_not_a_container_is_written_as_a_key()
    {
        Document document = Minimal() with
        {
            Nodes =
            [
                new Node("n_a", "process", "A", "rounded"),
                new Node("n_b", "process", "B", "rounded") { Parent = "n_a" },
                new Node("n_c", "process", "C", "rounded") { Parent = "c_gone" },
            ],
        };

        string printed = Dsl.Print(document);

        Assert.Contains("node n_b process \"B\" parent=n_a\n", printed, StringComparison.Ordinal);
        Assert.Contains("node n_c process \"C\" parent=c_gone\n", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_ids_and_non_finite_numbers_are_rejected()
    {
        Document badId = Minimal() with { Nodes = [new Node("bad id", "process", "A", "rounded")] };
        Document badNumber = Minimal() with { Nodes = [new Node("n_a", "process", "A", "rounded") { Geometry = new Geometry(double.NaN, 0, 1, 1) }] };

        Assert.Throws<ArgumentException>(() => Dsl.Print(badId));
        Assert.Throws<ArgumentException>(() => Dsl.Print(badNumber));
    }

    [Fact]
    public void Trailing_newline_in_an_id_is_rejected_and_in_a_key_is_quoted()
    {
        Document badId = Minimal() with { Nodes = [new Node("n_a\n", "process", "A", "rounded")] };
        var props = new Dictionary<string, JsonElement> { ["owner\n"] = JsonSerializer.SerializeToElement("x") };
        Document newlineKey = Minimal() with
        {
            Theme = new Dictionary<string, string> { ["node.fill\n"] = "#FFF" },
            Nodes = [new Node("n_a", "process", "A", "rounded") { Props = props }],
        };

        Assert.Throws<ArgumentException>(() => Dsl.Print(badId));
        string printed = Dsl.Print(newlineKey);
        Document reread = Dsl.Parse(printed).Document;

        Assert.Contains("theme \"node.fill\\n\"=\"#FFF\"\n", printed, StringComparison.Ordinal);
        Assert.Contains("node n_a process \"A\" \"owner\\n\"=\"x\"\n", printed, StringComparison.Ordinal);
        Assert.Equal("#FFF", reread.Theme!["node.fill\n"]);
        Assert.Equal("x", reread.Nodes[0].Props!["owner\n"].GetString());
    }

    [Fact]
    public void Prints_and_reads_back_pool_and_lane_nesting()
    {
        const string source =
            "draughtsman 1\ntype flowchart\ntitle \"Swim\"\n\n"
            + "node c_pool pool \"Pool\" @ 0,0 400x300 orientation=\"vertical\"\n"
            + "node c_lane1 lane \"Lane 1\" @ 0,48 400x126 parent=c_pool\n"
            + "node c_lane2 lane \"Lane 2\" @ 200,48 400x126 parent=c_pool\n"
            + "node n_task process \"Task\" @ 60,60 parent=c_lane1\n";

        Document document = Dsl.Parse(source).Document;
        string printed = Dsl.Print(document);

        // The pool and lane shapes are their kinds' defaults, so shape= is elided; a lane's parent is not a
        // container-kind node, so it is written parent=<id>.
        Assert.Contains("node c_pool pool \"Pool\" @ 0,0 400x300 orientation=\"vertical\"", printed, StringComparison.Ordinal);
        Assert.Contains("node c_lane1 lane \"Lane 1\" @ 0,48 400x126 parent=c_pool", printed, StringComparison.Ordinal);
        Assert.Contains("node c_lane2 lane \"Lane 2\" @ 200,48 400x126 parent=c_pool", printed, StringComparison.Ordinal);
        Assert.Contains("node n_task process \"Task\" @ 60,60 160x80 parent=c_lane1", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("shape=custom:swimlane", printed, StringComparison.Ordinal);

        Assert.Equal(printed, Dsl.Print(Dsl.Parse(printed).Document));
    }

    private static Document Minimal()
    {
        return new Document("doc_min", Document.CurrentSchemaVersion, "Minimal", DocumentType.Flowchart, [], [], new Meta(s_at, s_at, "test"));
    }
}
