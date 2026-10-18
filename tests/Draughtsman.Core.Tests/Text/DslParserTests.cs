using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests.Text;

public class DslParserTests
{
    private const string Header = "draughtsman 1\ntype flowchart\ntitle \"T\"\n";

    // docs/schema/document.schema.json is the contract every producer of a Document owes; a value it does not admit
    // is lint with a fallback, never a syntax error, so one bad word in a pasted diagram still yields a diagram.
    [Theory]
    [InlineData("node n_a process \"A\" shape=circle\n", "unknown shape 'circle'")]
    [InlineData("node n_a process \"A\" @ 0,0 -100x60\n", "a width cannot be negative")]
    [InlineData("node n_a process \"A\" @ 0,0 100x-60\n", "a height cannot be negative")]
    [InlineData("node n_a process \"A\" style.strokeWidth=-2\n", "cannot be negative")]
    [InlineData("node n_a process \"A\" style.fontSize=-2\n", "font size")]
    [InlineData("node n_a process \"A\" ports=[p1@2,0]\n", "port 'p1' x must be between 0 and 1")]
    [InlineData("node n_a process \"A\"\nnode n_b process \"B\"\nedge e_1 flow n_a -> n_b style.strokeWidth=-1\n", "cannot be negative")]
    public void Value_outside_the_schema_is_a_warning_with_a_fallback_not_a_parse_error(string body, string messagePart)
    {
        ParseResult result = Dsl.Parse(Header + body);

        DslWarning warning = Assert.Single(result.Warnings);
        Assert.Contains(messagePart, warning.Message, StringComparison.Ordinal);
        Assert.True(warning.Line > 0 && warning.Column > 0);

        SchemaCheck schema = ContractFiles.Evaluate(DocumentJson.Serialize(result.Document));
        Assert.True(schema.IsValid, ContractFiles.DescribeErrors(schema));
    }

    [Fact]
    public void Edge_endpoint_id_over_the_schema_bound_is_a_parse_error_not_a_document_the_schema_rejects()
    {
        string tooLong = new('a', 129);

        DslParseException error = Assert.Throws<DslParseException>(
            () => Dsl.Parse(Header + $"node n_a process \"A\"\nedge e_1 flow n_a -> {tooLong}\n"));

        Assert.Equal(5, error.Line);
        Assert.True(error.Column > 0);
        Assert.Contains("1 to 128 characters", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_shape_falls_back_to_the_kinds_default_shape()
    {
        ParseResult result = Dsl.Parse(Header + "node n_a decision \"A\" shape=circle\n");

        Assert.Equal(Dsl.Parse(Header + "node n_a decision \"A\"\n").Document.Nodes[0].Shape, result.Document.Nodes[0].Shape);
    }

    [Fact]
    public void Port_offsets_outside_the_unit_range_are_clamped_to_it()
    {
        ParseResult result = Dsl.Parse(Header + "node n_a process \"A\" ports=[p1@2,-1]\n");

        Assert.Equal(new Port("p1", 1, 0), Assert.Single(result.Document.Nodes[0].Ports!));
    }

    [Fact]
    public void Missing_geometry_is_null_and_position_only_gets_the_default_size()
    {
        ParseResult result = Dsl.Parse(Header + "node n_a process \"A\"\nnode n_b process \"B\" @ 10,20\n");

        Assert.Null(result.Document.Nodes[0].Geometry);
        Assert.Equal(new Geometry(10, 20, 160, 80), result.Document.Nodes[1].Geometry);
    }

    [Fact]
    public void Shape_defaults_from_the_kind_vocabulary_and_rectangle_for_unknown_kinds()
    {
        ParseResult result = Dsl.Parse(Header + "node n_a decision \"A\"\nnode n_b mystery \"B\"\nnode n_c process \"C\" shape=hexagon\n");

        Assert.Equal("diamond", result.Document.Nodes[0].Shape);
        Assert.Equal("rectangle", result.Document.Nodes[1].Shape);
        Assert.Equal("hexagon", result.Document.Nodes[2].Shape);
        DslWarning warning = Assert.Single(result.Warnings);
        Assert.Equal(5, warning.Line);
        Assert.Equal(10, warning.Column);
        Assert.Contains("unknown node kind 'mystery'", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Freeform_documents_have_no_kind_warnings()
    {
        ParseResult result = Dsl.Parse("draughtsman 1\ntype freeform\nnode n_a anything \"A\"\nedge e_1 whatever n_a -> n_a\n");

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Id_defaults_to_a_slug_of_the_title_and_meta_defaults_to_now()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

        Document document = Dsl.Parse("draughtsman 1\ntype flowchart\ntitle \"Order service: v2!\"\n").Document;

        Assert.Equal("doc_order_service_v2", document.Id);
        Assert.InRange(document.Meta.CreatedAt, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(document.Meta.CreatedAt, document.Meta.UpdatedAt);
        Assert.Equal("dsl", document.Meta.CreatedBy);
        Assert.Equal(0, document.Meta.CreatedAt.Ticks % TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored_but_hashes_inside_values_are_kept()
    {
        const string text = Header + "# a comment\n\nnode n_a process \"C# service\" style.fill=#FF0000 # trailing\n";

        Node node = Assert.Single(Dsl.Parse(text).Document.Nodes);

        Assert.Equal("C# service", node.Label);
        Assert.Equal("#FF0000", node.Style!.Fill);
    }

    [Fact]
    public void Font_size_and_bold_round_trip_through_the_style_fields()
    {
        const string text = Header + "node n_a process \"A\" style.fontSize=18.5 style.bold=true\nnode n_b process \"B\" style.fontSize=9 style.bold=false\n";

        ParseResult result = Dsl.Parse(text);

        Node first = result.Document.Nodes[0];
        Node second = result.Document.Nodes[1];
        Assert.Empty(result.Warnings);
        Assert.Equal(18.5, first.Style!.FontSize);
        Assert.True(first.Style.Bold);
        Assert.Equal(9, second.Style!.FontSize);
        Assert.False(second.Style.Bold);

        // Both directions of each two-sided field survive print -> parse byte-identically.
        Assert.Equal(Dsl.Print(result.Document), Dsl.Print(Dsl.Parse(Dsl.Print(result.Document)).Document));

        SchemaCheck schema = ContractFiles.Evaluate(DocumentJson.Serialize(result.Document));
        Assert.True(schema.IsValid, ContractFiles.DescribeErrors(schema));
    }

    [Fact]
    public void Font_size_and_bold_shape_the_printed_style_fields()
    {
        Document document = Dsl.Parse(Header).Document with
        {
            Nodes =
            [
                new Node("n_a", "process", "A", "rectangle") { Style = new NodeStyle { FontSize = 12, Bold = true } },
            ],
        };

        string printed = Dsl.Print(document);

        Assert.Contains("style.fontSize=12 style.bold=true", printed, StringComparison.Ordinal);
        Assert.Equal(document.Nodes[0].Style, Dsl.Parse(printed).Document.Nodes[0].Style);
    }

    [Fact]
    public void Prop_values_are_json_shaped()
    {
        const string text = Header + "node n_a process \"A\" owner=\"platform\" count=3 ratio=0.5 on=true off=false nothing=null tags=[\"a\", 1] meta={\"k\": {\"n\": [true]}} quoted=\"42\"\n";

        IReadOnlyDictionary<string, JsonElement> props = Assert.Single(Dsl.Parse(text).Document.Nodes).Props!;

        Assert.Equal("platform", props["owner"].GetString());
        Assert.Equal(3, props["count"].GetInt32());
        Assert.Equal(0.5, props["ratio"].GetDouble());
        Assert.True(props["on"].GetBoolean());
        Assert.False(props["off"].GetBoolean());
        Assert.Equal(JsonValueKind.Null, props["nothing"].ValueKind);
        Assert.Equal(JsonValueKind.Array, props["tags"].ValueKind);
        Assert.True(props["meta"].GetProperty("k").GetProperty("n")[0].GetBoolean());
        Assert.Equal("42", props["quoted"].GetString());
    }

    [Fact]
    public void Quoted_keys_are_props_even_when_they_look_reserved()
    {
        const string text = Header + "node n_a process \"A\" \"shape\"=\"x\" \"style.fill\"=\"y\" shape=pill\n";

        Node node = Assert.Single(Dsl.Parse(text).Document.Nodes);

        Assert.Equal("pill", node.Shape);
        Assert.Null(node.Style);
        Assert.Equal("x", node.Props!["shape"].GetString());
        Assert.Equal("y", node.Props["style.fill"].GetString());
    }

    [Fact]
    public void Edge_label_is_distinguished_from_a_quoted_prop_key()
    {
        const string text = Header + "node n_a process \"A\"\nedge e_1 flow n_a -> n_a \"owner name\"=\"x\"\nedge e_2 flow n_a -> n_a \"Yes\" \"k\"=1\n";

        IReadOnlyList<Edge> edges = Dsl.Parse(text).Document.Edges;

        Assert.Null(edges[0].Label);
        Assert.Equal("x", edges[0].Props!["owner name"].GetString());
        Assert.Equal("Yes", edges[1].Label);
        Assert.Equal(1, edges[1].Props!["k"].GetInt32());
    }

    [Fact]
    public void Container_braces_set_parent_and_children_may_be_declared_later()
    {
        const string text = Header + "container c_1 \"Lane\" { n_a n_b }\nnode n_a process \"A\"\nnode n_b process \"B\" parent=c_1\nnode n_c process \"C\" parent=n_a\n";

        Document document = Dsl.Parse(text).Document;

        Assert.Equal("container", document.Nodes[0].Kind);
        Assert.Equal("c_1", document.Nodes[1].Parent);
        Assert.Equal("c_1", document.Nodes[2].Parent);
        Assert.Equal("n_a", document.Nodes[3].Parent);
    }

    [Fact]
    public void Endpoint_ports_split_at_the_last_dot_unless_the_whole_token_is_a_node()
    {
        const string text = Header + "node n.api process \"A\" ports=[in@0.5,0 p.1@1,1]\nnode n.api.in process \"B\"\nnode n_x process \"C\"\nedge e_1 flow n.api.in -> n.api.p.1\nedge e_2 flow n_x.out -> ghost.p\nedge e_3 flow ghost.p.1 -> n_x\n";

        ParseResult result = Dsl.Parse(text);
        IReadOnlyList<Edge> edges = result.Document.Edges;

        Assert.Equal(new Endpoint("n.api.in"), edges[0].From);
        Assert.Equal(new Endpoint("n.api") { Port = "p.1" }, edges[0].To);
        Assert.Equal(new Endpoint("n_x") { Port = "out" }, edges[1].From);
        Assert.Equal(new Endpoint("ghost") { Port = "p" }, edges[1].To);
        Assert.Equal(new Endpoint("ghost.p.1"), edges[2].From);
        Assert.Equal(3, result.Warnings.Count);
        Assert.Contains("port 'out'", result.Warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("node 'ghost'", result.Warnings[1].Message, StringComparison.Ordinal);
        Assert.Contains("node 'ghost.p.1'", result.Warnings[2].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_lines_populate_theme_meta_and_provenance()
    {
        const string text = "draughtsman 1\nid doc_x\ntype architecture\ntitle \"X\"\ntheme node.fill=\"#FFF\" \"odd key\"=\"v\"\nmeta created=2026-09-20T22:00:00.000Z updated=2026-09-20T22:30:00.000Z by=\"eric w\"\nprovenance claude-code created 2026-09-20T22:00:00.000Z model=claude-fable-5-1 { n_a }\nprovenance \"the agent\" removed 2026-09-20T22:01:00.000Z { }\nnode n_a service \"A\"\n";

        Document document = Dsl.Parse(text).Document;

        Assert.Equal("doc_x", document.Id);
        Assert.Equal("#FFF", document.Theme!["node.fill"]);
        Assert.Equal("v", document.Theme["odd key"]);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 22, 0, 0, TimeSpan.Zero), document.Meta.CreatedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 22, 30, 0, TimeSpan.Zero), document.Meta.UpdatedAt);
        Assert.Equal("eric w", document.Meta.CreatedBy);
        Assert.Equal(2, document.Meta.AiProvenance!.Count);
        Assert.Equal("claude-fable-5-1", document.Meta.AiProvenance[0].Model);
        Assert.Equal(["n_a"], document.Meta.AiProvenance[0].ElementIds);
        Assert.Equal("the agent", document.Meta.AiProvenance[1].Agent);
        Assert.Equal(AiAction.Removed, document.Meta.AiProvenance[1].Action);
        Assert.Empty(document.Meta.AiProvenance[1].ElementIds);
    }

    [Fact]
    public void Edge_routing_style_and_waypoints_parse()
    {
        const string text = Header + "node n_a process \"A\"\nedge e_1 flow n_a -> n_a routing=curved waypoints=[1,2 -3.5,4e2] style.stroke=\"rgb(1, 2, 3)\" style.strokeWidth=2 style.dash=dotted style.arrowStart=none style.arrowEnd=diamond\n";

        Edge edge = Assert.Single(Dsl.Parse(text).Document.Edges);

        Assert.Equal(RoutingMode.Curved, edge.Routing!.Mode);
        Assert.Equal([new Point(1, 2), new Point(-3.5, 400)], edge.Routing.Waypoints);
        Assert.Equal("rgb(1, 2, 3)", edge.Style!.Stroke);
        Assert.Equal(2, edge.Style.StrokeWidth);
        Assert.Equal(Dash.Dotted, edge.Style.Dash);
        Assert.Equal(ArrowHead.None, edge.Style.ArrowStart);
        Assert.Equal(ArrowHead.Diamond, edge.Style.ArrowEnd);
    }

    [Theory]
    [InlineData(ArrowHead.One, "one")]
    [InlineData(ArrowHead.Many, "many")]
    [InlineData(ArrowHead.ZeroOrOne, "zeroOrOne")]
    [InlineData(ArrowHead.OneOrMany, "oneOrMany")]
    [InlineData(ArrowHead.ZeroOrMany, "zeroOrMany")]
    [InlineData(ArrowHead.OneAndOnlyOne, "oneAndOnlyOne")]
    [InlineData(ArrowHead.Inheritance, "inheritance")]
    [InlineData(ArrowHead.Aggregation, "aggregation")]
    [InlineData(ArrowHead.Composition, "composition")]
    public void Notation_arrowheads_round_trip_losslessly(ArrowHead head, string spelling)
    {
        // Each new head parses from its camelCase spelling and the printed form re-parses to the same document.
        string text = Header + $"node n_a process \"A\"\nnode n_b process \"B\"\nedge e_1 flow n_a -> n_b style.arrowStart={spelling} style.arrowEnd={spelling}\n";
        Document once = Dsl.Parse(text).Document;
        Edge edge = Assert.Single(once.Edges);
        Assert.Equal(head, edge.Style!.ArrowStart);
        Assert.Equal(head, edge.Style.ArrowEnd);

        Document twice = Dsl.Parse(Dsl.Print(once)).Document;
        Edge reprinted = Assert.Single(twice.Edges);
        Assert.Equal(head, reprinted.Style!.ArrowStart);
        Assert.Equal(head, reprinted.Style.ArrowEnd);
        Assert.Equal(Dsl.Print(once), Dsl.Print(twice));
    }

    [Fact]
    public void Quoted_strings_decode_json_escapes()
    {
        const string text = Header + "node n_a process \"say \\\"hi\\\"\\n\\t\\\\ \\u00e9 \\ud83d\\ude80\"\n";

        Node node = Assert.Single(Dsl.Parse(text).Document.Nodes);

        Assert.Equal("say \"hi\"\n\t\\ é \U0001F680", node.Label);
    }

    [Theory]
    [InlineData("", 1, 1, "the first line must be 'draughtsman 1'")]
    [InlineData("type flowchart\n", 1, 1, "the first line must be 'draughtsman 1'")]
    [InlineData("draughtsman 2\n", 1, 13, "unsupported DSL version '2'")]
    [InlineData("draughtsman 1\ntitle \"x\"\n", 1, 1, "missing 'type' line")]
    [InlineData("draughtsman 1\ntype mindmap\n", 2, 6, "unknown document type 'mindmap'")]
    [InlineData("draughtsman 1\ntype flowchart\ntitle \"a\"\ntitle \"b\"\n", 4, 1, "'title' was already given on line 3")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\"\nnode n_a process \"B\"\n", 4, 6, "node id 'n_a' was already used on line 3")]
    [InlineData("draughtsman 1\ntype flowchart\nnode 1bad process \"A\"\n", 3, 6, "'1bad' is not a valid id")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process A\n", 3, 18, "expected the label in double quotes")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\n", 3, 18, "unterminated string")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" @ 10\n", 3, 24, "expected a position x,y")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" @ 10,x\n", 3, 24, "two numbers")]
    // The grammar admits any exponent, but a value double.Parse answers Infinity for cannot be carried or printed.
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" @ 1e400,0\n", 3, 24, "two numbers in range")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" @ 0,0 1e400x60\n", 3, 28, "two numbers in range")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" @ 0,0 100x60 rotation=1e400\n", 3, 44, "'rotation' must be a number in range")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" ports=[p1@1e400,0]\n", 3, 32, "two numbers in range")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" style.strokeWidth=1e400\n", 3, 40, "'style.strokeWidth' must be a number in range")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" rotation=45\n", 3, 22, "rotation needs a position")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" shape\n", 3, 27, "expected '=' after 'shape'")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" shape=\n", 3, 28, "expected a value after 'shape='")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" routing=curved\n", 3, 22, "'routing' applies to edges, not nodes")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\"\nedge e_1 flow n_a -> n_a shape=pill\n", 4, 26, "'shape' applies to nodes, not edges")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" label=\"x\"\n", 3, 22, "'label' is positional: write the label as the quoted string after the kind")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\"\nedge e_1 flow n_a -> n_a label=\"x\"\n", 4, 26, "'label' is positional: write the label as a quoted string after the endpoints")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" id=n_b\n", 3, 22, "'id' is positional: the id is the first word after 'node'")]
    [InlineData("draughtsman 1\ntype flowchart\ncontainer c_1 \"L\" label=\"x\"\n", 3, 19, "'label' is positional: write the label as the quoted string after the container id")]
    [InlineData("draughtsman 1\ntype flowchart\ncontainer c_1 \"L\" id=c_2\n", 3, 19, "'id' is positional: the id is the first word after 'container'")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\"\nedge e_1 flow n_a -> n_a kind=data\n", 4, 26, "'kind' is positional: the kind is the word after the id")]
    [InlineData("draughtsman 1\ntype flowchart\ncontainer c_1 \"L\" kind=lane\n", 3, 19, "a container's kind is always 'container'")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" style=red\n", 3, 22, "'style' takes a field: write style.fill=...")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\"\nedge e_1 flow n_a -> n_a style=red\n", 4, 26, "'style' takes a field: write style.stroke=...")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" owner=platform\n", 3, 28, "owner=platform: string values must be quoted, write owner=\"platform\"")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" \"owner name\"=x\n", 3, 35, "\"owner name\"=x: string values must be quoted, write \"owner name\"=\"x\"")]
    [InlineData("draughtsman 1\ntype flowchart\ntitle Orders\n", 3, 7, "expected the title in double quotes")]
    [InlineData("draughtsman 1\ntype flowchart\ntheme node.fill=#FFF\n", 3, 17, "node.fill=#FFF: theme values must be quoted, write node.fill=\"#FFF\"")]
    [InlineData("draughtsman 1\ntype flowchart\nmeta created=\"2026-09-20T22:00:00.000Z\"\n", 3, 14, "'created' must be a UTC timestamp written yyyy-MM-ddTHH:mm:ss.fffZ, unquoted, for example created=2026-09-20T22:00:00.000Z")]
    [InlineData("draughtsman 1\ntype flowchart\nmeta updated=2026-09-21T00:30:00+02:00\n", 3, 14, "'updated' must be a UTC timestamp written yyyy-MM-ddTHH:mm:ss.fffZ")]
    [InlineData("draughtsman 1\ntype flowchart\nmeta created=2026-09-20T22:00:00Z\n", 3, 14, "'created' must be a UTC timestamp written yyyy-MM-ddTHH:mm:ss.fffZ")]
    [InlineData("draughtsman 1\ntype flowchart\nprovenance bot updated 2026-09-20T22:00:00+01:00 { }\n", 3, 24, "is not a timestamp; write it as UTC yyyy-MM-ddTHH:mm:ss.fffZ")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" style.colour=red\n", 3, 22, "unknown node style field 'colour'")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" style.dash=wavy\n", 3, 33, "'style.dash' must be one of solid, dashed, dotted")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" style.bold=maybe\n", 3, 33, "'style.bold' must be true or false")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\"\nedge e_1 flow n_a -> n_a style.arrowEnd=crowsfoot\n", 4, 41, "'style.arrowEnd' must be one of none, arrow, open, diamond, circle, one, many, zeroOrOne, oneOrMany, zeroOrMany, oneAndOnlyOne, inheritance, aggregation, composition")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" tags=[1,\n", 3, 27, "missing ']'")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" tags=[1,]\n", 3, 27, "not valid JSON")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" { n_b }\n", 3, 22, "only containers list children")]
    [InlineData("draughtsman 1\ntype flowchart\ncontainer c_1 \"L\" { n_b }\n", 3, 21, "lists 'n_b', which is not declared")]
    [InlineData("draughtsman 1\ntype flowchart\ncontainer c_1 \"L\" { n_b }\ncontainer c_2 \"M\" { n_b }\nnode n_b process \"B\"\n", 4, 21, "already belongs to 'c_1'")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\"\nedge e_1 flow n_a n_a\n", 4, 19, "expected '->'")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\"\nedge e_1 flow n_a -> n_a waypoints=[1,2]\n", 4, 26, "waypoints need a routing mode")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" x=1 x=2\n", 3, 26, "'x' is given twice")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" ports=[in@0.5]\n", 3, 32, "a port position x,y")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" \"bad\\q\"\n", 3, 26, "unknown escape")]
    [InlineData("draughtsman 1\ntype flowchart\nwidget n_a\n", 3, 1, "unknown statement 'widget'")]
    [InlineData("draughtsman 1\ntype flowchart\nmeta created=yesterday\n", 3, 14, "'created' must be a UTC timestamp written yyyy-MM-ddTHH:mm:ss.fffZ")]
    [InlineData("draughtsman 1\ntype flowchart\nprovenance bot updated 2026-09-20T22:00:00.000Z\n", 3, 48, "provenance needs the touched element ids")]
    [InlineData("draughtsman 1\ntype flowchart\nnode n_a process \"A\" extra\n", 3, 27, "expected '=' after 'extra'")]
    public void Syntax_errors_report_line_and_column(string text, int line, int column, string messagePart)
    {
        DslParseException error = Assert.Throws<DslParseException>(() => Dsl.Parse(text));

        Assert.Equal(line, error.Line);
        Assert.Equal(column, error.Column);
        Assert.Contains(messagePart, error.Detail, StringComparison.Ordinal);
        Assert.StartsWith($"line {line}, column {column}: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_line_endings_are_accepted()
    {
        ParseResult result = Dsl.Parse("draughtsman 1\r\ntype flowchart\r\nnode n_a process \"A\"\r\n");

        Assert.Single(result.Document.Nodes);
    }

    [Fact]
    public void Unknown_edge_kind_and_parent_warn_with_positions()
    {
        ParseResult result = Dsl.Parse(Header + "node n_a process \"A\" parent=c_missing\nedge e_1 teleport n_a -> n_a\n");

        Assert.Equal(2, result.Warnings.Count);
        Assert.Equal((4, 29), (result.Warnings[0].Line, result.Warnings[0].Column));
        Assert.Contains("parent 'c_missing'", result.Warnings[0].Message, StringComparison.Ordinal);
        Assert.Equal((5, 10), (result.Warnings[1].Line, result.Warnings[1].Column));
        Assert.Contains("unknown edge kind 'teleport'", result.Warnings[1].Message, StringComparison.Ordinal);
    }
}
