using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// The Mermaid exporter (docs/api.md <c>POST /api/export/mermaid</c>). Expected text is written out literally, never derived
/// from the exporter, and each round trip compares what a reader sees (labels, order, kinds) rather than ids, which the
/// importer renames. Every equivalence helper is itself shown to fail on a real difference.
/// </summary>
public class MermaidExporterTests
{
    private static readonly string s_checkoutPath = Path.Combine(AppContext.BaseDirectory, "contract", "checkout-sequence.json");
    private static readonly string s_orderServicePath = Path.Combine(AppContext.BaseDirectory, "contract", "order-service.json");

    private static Document Checkout() => DocumentJson.Deserialize(File.ReadAllText(s_checkoutPath));

    private static Document FromDsl(string body, string type)
    {
        return Dsl.Parse($"draughtsman 1\nid doc_t\ntype {type}\ntitle \"T\"\nmeta created=2026-09-29T00:00:00.000Z updated=2026-09-29T00:00:00.000Z by=eric\n\n{body}").Document;
    }

    /// <summary>The DSL's default shape for a flowchart <c>process</c> is rounded; fixtures that mean a plain box say so.</summary>
    private static Document Boxes(Document document)
    {
        return document with { Nodes = document.Nodes.Select(n => n.Shape == "rounded" ? n with { Shape = "rectangle" } : n).ToList() };
    }

    private static string Export(Document document) => MermaidExporter.Export(document).Text;

    private static Document ReImport(string mermaid, DocumentType type)
    {
        ImportResult result = MermaidImporter.Import(mermaid, new MermaidImportOptions { Type = type });
        Assert.DoesNotContain(result.Warnings, w => w.Code is ImportWarningCodes.ParseError or ImportWarningCodes.UnsupportedSyntax);
        return result.Document;
    }

    /// <summary>A sequence diagram as a reader sees it: lifelines in order, then messages in order, ends by label, with the bar flags.</summary>
    private static string SequenceShape(Document document)
    {
        string Label(string id) => document.Nodes.Single(n => n.Id == id).Label;
        var lines = document.Nodes.Select(n => $"{n.Kind} {n.Label}").ToList();
        lines.AddRange(document.Edges.Select(e =>
            $"{e.Kind} {Label(e.From.Node)} -> {Label(e.To.Node)} [{e.Label}]"
            + $"{(Schemas.SequenceMessages.IsSet(e, "activate") ? " +" : string.Empty)}{(Schemas.SequenceMessages.IsSet(e, "deactivate") ? " -" : string.Empty)}"
            + $" {e.Style?.Dash}/{e.Style?.ArrowStart}/{e.Style?.ArrowEnd}"));
        return string.Join("\n", lines);
    }

    /// <summary>A flowchart as a reader sees it: boxes with their shape and enclosing subgraph, then links in order with their label.</summary>
    private static string FlowShape(Document document)
    {
        string Label(string id) => document.Nodes.Single(n => n.Id == id).Label;
        var lines = document.Nodes
            .Select(n => $"{n.Label} <{n.Shape}> in {(n.Parent is null ? "-" : Label(n.Parent))}").Order().ToList(); // the importer lists subgraphs first, so node order is not compared
        lines.AddRange(document.Edges.Select(e => $"{Label(e.From.Node)} -> {Label(e.To.Node)} [{e.Label}] {e.Style?.Dash}/{e.Style?.StrokeWidth}/{e.Style?.ArrowStart}/{e.Style?.ArrowEnd}"));
        return string.Join("\n", lines);
    }

    [Fact]
    public void The_checkout_sequence_from_the_docs_exports_to_this_exact_text()
    {
        const string expected = """
            sequenceDiagram
                title Checkout
                actor user as Customer
                participant web as Web app
                participant api as Orders API
                participant pay as Payments
                user->>web: Place order
                web->>+api: POST /orders
                api->>+pay: charge(total)
                pay-->>-api: receipt
                api-)web: declined
                api-->>-web: 201 Created
                api->>api: audit()

            """;

        MermaidExportResult result = MermaidExporter.Export(Checkout());

        Assert.Equal(expected.ReplaceLineEndings("\n"), result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_sequence_diagram_round_trips_through_the_importer_with_the_same_participants_order_kinds_and_bars()
    {
        Document original = Checkout();

        Document back = ReImport(Export(original), DocumentType.Sequence);

        Assert.Equal(DocumentType.Sequence, back.Type);
        Assert.Equal("Checkout", back.Title);
        Assert.Equal(SequenceShape(original), SequenceShape(back));
        // The comparison has teeth: it names the order, so two swapped messages, or a lost bar, are a different shape.
        Document swapped = original with { Edges = original.Edges.Take(4).Reverse().Concat(original.Edges.Skip(4)).ToList() };
        Assert.NotEqual(SequenceShape(original), SequenceShape(swapped));
        Document noBars = original with { Edges = original.Edges.Select(e => e with { Props = null }).ToList() };
        Assert.NotEqual(SequenceShape(original), SequenceShape(noBars));
    }

    [Theory]
    [InlineData("sync", null, null, null, "->>")]
    [InlineData("sync", "dashed", null, null, "-->>")]
    [InlineData("sync", null, "none", null, "->")]
    [InlineData("sync", "dashed", "none", null, "-->")]
    [InlineData("sync", null, null, "arrow", "<<->>")]
    [InlineData("async", null, null, null, "-)")]
    [InlineData("async", "dashed", null, null, "--)")]
    [InlineData("return", null, null, null, "-->>")]
    [InlineData("return", "solid", null, null, "->>")]
    [InlineData("return", null, "none", null, "-->")]
    [InlineData("return", null, null, "open", "<<-->>")]
    public void Each_message_kind_and_style_picks_the_matching_mermaid_arrow(string kind, string? dash, string? end, string? start, string arrow)
    {
        var style = new List<string>();
        if (dash is not null)
        {
            style.Add($"style.dash={dash}");
        }

        if (end is not null)
        {
            style.Add($"style.arrowEnd={end}");
        }

        if (start is not null)
        {
            style.Add($"style.arrowStart={start}");
        }

        Document document = FromDsl($"node p_a participant \"A\"\nnode p_b participant \"B\"\nedge m_1 {kind} p_a -> p_b \"x\" {string.Join(' ', style)}\n", "sequence");

        string text = Export(document);

        Assert.Contains($"\n    a{arrow}b: x\n", text);
    }

    [Fact]
    public void A_message_that_activates_and_deactivates_keeps_both_and_reads_back_the_same()
    {
        Document document = FromDsl("node p_a participant \"A\"\nnode p_b participant \"B\"\nedge m_1 sync p_a -> p_b \"go\" activate=true\nedge m_2 sync p_b -> p_b \"both\" activate=true deactivate=true\nedge m_3 return p_b -> p_a \"done\" deactivate=true\n", "sequence");

        string text = Export(document);

        Assert.Contains("    b->>+b: both\n    deactivate b\n", text);
        Assert.Equal(SequenceShape(document), SequenceShape(ReImport(text, DocumentType.Sequence)));
    }

    [Fact]
    public void Text_mermaid_reads_as_syntax_is_escaped_and_decoded_by_the_importer()
    {
        Document document = FromDsl("node p_a participant \"A; B #1\"\nnode p_b actor \"Two\\nlines\"\nedge m_1 sync p_a -> p_b \"a; b # c\"\n", "sequence");

        string text = Export(document);

        Assert.Contains("participant a as A#59; B #35;1", text);
        Assert.Contains("actor b as Two<br/>lines", text);
        Assert.Contains("a->>b: a#59; b #35; c", text);
        Assert.DoesNotContain(";", text.Replace("#59;", string.Empty, StringComparison.Ordinal).Replace("#35;", string.Empty, StringComparison.Ordinal));
        Document back = ReImport(text, DocumentType.Sequence);
        Assert.Equal("A; B #1", back.Nodes[0].Label);
        Assert.Equal("Two lines", back.Nodes[1].Label); // the importer keeps a participant's label on one line
        Assert.Equal("a; b # c", back.Edges[0].Label);
    }

    [Fact]
    public void Ids_become_usable_mermaid_ids_that_are_unique_and_never_a_keyword()
    {
        Document document = FromDsl("node end participant \"End\"\nnode web-app participant \"Web\"\nnode web_app participant \"Web two\"\nnode p_loop participant \"Loop\"\nedge m_1 sync end -> web-app \"1\"\nedge m_2 sync web_app -> p_loop \"2\"\n", "sequence");

        string text = Export(document);

        Assert.Contains("participant x_end as End\n", text);
        Assert.Contains("participant web_app as Web\n", text);
        Assert.Contains("participant web_app_2 as Web two\n", text);
        Assert.Contains("participant x_loop as Loop\n", text);
        Assert.Contains("x_end->>web_app: 1\n", text);
        Assert.Contains("web_app_2->>x_loop: 2\n", text);
        Assert.Equal(SequenceShape(document), SequenceShape(ReImport(text, DocumentType.Sequence)));
    }

    [Fact]
    public void A_message_to_an_undeclared_participant_still_gets_a_lifeline()
    {
        Document document = FromDsl("node p_a participant \"A\"\nnode p_b participant \"B\"\nedge m_1 sync p_a -> p_b \"x\"\n", "sequence");
        document = document with { Edges = [document.Edges[0] with { To = new Endpoint("p_ghost") }] };

        string text = Export(document);

        Assert.Contains("participant ghost as p_ghost\n", text);
        Assert.Contains("a->>ghost: x\n", text);
    }

    [Fact]
    public void A_flowchart_exports_to_this_exact_text()
    {
        Document document = FromDsl(
            "node n_start terminal \"Start\"\n" +
            "node n_ok decision \"In stock?\"\n" +
            "node n_ship process \"Ship it\"\n" +
            "node n_db database \"Orders \\\"DB\\\"\"\n" +
            "edge e_1 flow n_start -> n_ok\n" +
            "edge e_2 flow n_ok -> n_ship \"yes\"\n" +
            "edge e_3 flow n_ship -> n_db \"save\" style.dash=dashed\n",
            "flowchart");
        document = document with
        {
            Nodes =
            [
                document.Nodes[0] with { Shape = "pill" },
                document.Nodes[1] with { Shape = "diamond" },
                document.Nodes[2] with { Shape = "rectangle" },
                document.Nodes[3] with { Shape = "cylinder" },
            ],
        };
        const string expected = """
            ---
            title: T
            ---
            flowchart TD
                start(["Start"])
                ok{"In stock?"}
                ship["Ship it"]
                db[("Orders #quot;DB#quot;")]
                start --> ok
                ok -->|"yes"| ship
                ship -.->|"save"| db

            """;

        MermaidExportResult result = MermaidExporter.Export(document);

        Assert.Equal(expected.ReplaceLineEndings("\n"), result.Text);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_flowchart_round_trips_with_the_same_boxes_shapes_links_and_subgraphs()
    {
        const string source = """
            flowchart LR
                A[Web app] -->|HTTPS| B(Orders API)
                subgraph vpc [VPC]
                    B --> C{Cached?}
                    C -- no --> D[(Postgres)]
                    subgraph inner [Workers]
                        E{{Queue}} -.-> F([Done])
                    end
                end
                D ==> G[/Report/]
                C --- A
            """;
        Document original = MermaidImporter.Import(source, new MermaidImportOptions { Type = DocumentType.Flowchart }).Document;

        string text = Export(original);
        Document back = ReImport(text, DocumentType.Flowchart);

        Assert.Equal(FlowShape(original), FlowShape(back));
        // Spot-check the literals the comparison stands on, so both sides cannot be empty or equally wrong.
        Assert.Contains("subgraph vpc[\"VPC\"]", text);
        Assert.Contains("        subgraph inner[\"Workers\"]", text);
        Assert.Equal(2, back.Nodes.Count(n => n.Kind == "container"));
        Assert.Equal("Workers", back.Nodes.Single(n => n.Label == "Queue").Parent is { } p ? back.Nodes.Single(n => n.Id == p).Label : null);
        Assert.Equal(6, back.Edges.Count);
        // And the comparison fails when a link is dropped.
        Assert.NotEqual(FlowShape(original), FlowShape(original with { Edges = original.Edges.Skip(1).ToList() }));
    }

    [Fact]
    public void The_architecture_example_round_trips_its_labels_containers_and_links()
    {
        Document original = DocumentJson.Deserialize(File.ReadAllText(s_orderServicePath));

        string text = Export(original);
        Document back = ReImport(text, DocumentType.Architecture);

        Assert.Contains("subgraph vpc[\"VPC\"]", text);
        Assert.Equal(original.Nodes.Select(n => n.Label).Order(), back.Nodes.Select(n => n.Label).Order());
        string ParentLabel(Document d, Node n) => n.Parent is null ? "-" : d.Nodes.Single(x => x.Id == n.Parent).Label;
        Assert.Equal(
            original.Nodes.Select(n => $"{n.Label} in {ParentLabel(original, n)}").Order(),
            back.Nodes.Select(n => $"{n.Label} in {ParentLabel(back, n)}").Order());
        string Label(Document d, string id) => d.Nodes.Single(n => n.Id == id).Label;
        Assert.Equal(
            original.Edges.Select(e => $"{Label(original, e.From.Node)} -> {Label(original, e.To.Node)} [{e.Label}]"),
            back.Edges.Select(e => $"{Label(back, e.From.Node)} -> {Label(back, e.To.Node)} [{e.Label}]"));
        // The database keeps its shape through Mermaid's cylinder, so it re-imports as a database.
        Assert.Equal("database", back.Nodes.Single(n => n.Label == "Postgres").Kind);
    }

    [Fact]
    public void A_shape_mermaid_has_no_spelling_for_degrades_to_a_rectangle_with_a_comment_and_does_not_fail()
    {
        Document document = Boxes(FromDsl("node n_a process \"Plain\"\nnode n_b process \"Paper\"\nnode n_c process \"Task\"\nedge e_1 flow n_a -> n_b\nedge e_2 flow n_b -> n_c\n", "flowchart"));
        document = document with
        {
            Nodes = [document.Nodes[0], document.Nodes[1] with { Shape = "document" }, document.Nodes[2] with { Shape = "custom:bpmn/task" }],
        };

        MermaidExportResult result = MermaidExporter.Export(document);

        string[] lines = result.Text.Split('\n');
        int paper = Array.FindIndex(lines, l => l.Contains("b[\"Paper\"]", StringComparison.Ordinal));
        Assert.True(paper > 0, result.Text);
        Assert.Equal("    %% n_b: shape 'document' has no Mermaid equivalent; drawn as a rectangle", lines[paper - 1]);
        int task = Array.FindIndex(lines, l => l.Contains("c[\"Task\"]", StringComparison.Ordinal));
        Assert.Equal("    %% n_c: shape 'custom:bpmn/task' has no Mermaid equivalent; drawn as a rectangle", lines[task - 1]);
        // A shape that does map gets no comment, and the warnings list names exactly the two that degraded.
        int plain = Array.FindIndex(lines, l => l.Contains("a[\"Plain\"]", StringComparison.Ordinal));
        Assert.DoesNotContain("%%", lines[plain - 1], StringComparison.Ordinal);
        Assert.Equal(
            ["n_b: shape 'document' has no Mermaid equivalent; drawn as a rectangle", "n_c: shape 'custom:bpmn/task' has no Mermaid equivalent; drawn as a rectangle"],
            result.Warnings);
        // The comments are comments: what comes out still imports as the same three boxes and two links.
        Document back = ReImport(result.Text, DocumentType.Flowchart);
        Assert.Equal(["Plain", "Paper", "Task"], back.Nodes.Select(n => n.Label));
        Assert.Equal(2, back.Edges.Count);
    }

    [Theory]
    [InlineData(null, null, null, null, "-->")]
    [InlineData("association", null, null, null, "---")]
    [InlineData("dependency", null, null, null, "-.->")]
    [InlineData("dotted-report", null, null, null, "-.-")]
    [InlineData("flow", "dashed", null, null, "-.->")]
    [InlineData("flow", "dotted", null, null, "-.->")]
    [InlineData("flow", "solid", "none", null, "---")]
    [InlineData("flow", "solid", "circle", null, "--o")]
    [InlineData("flow", "solid", "arrow", "arrow", "<-->")]
    [InlineData("flow", "solid", "circle", "circle", "o--o")]
    [InlineData("data", "solid", null, null, "-->")]
    public void An_edges_kind_and_style_pick_the_link_operator(string? kind, string? dash, string? end, string? start, string link)
    {
        var style = new List<string>();
        if (dash is not null)
        {
            style.Add($"style.dash={dash}");
        }

        if (end is not null)
        {
            style.Add($"style.arrowEnd={end}");
        }

        if (start is not null)
        {
            style.Add($"style.arrowStart={start}");
        }

        Document document = FromDsl($"node n_a service \"A\"\nnode n_b service \"B\"\nedge e_1 {kind ?? "flow"} n_a -> n_b {string.Join(' ', style)}\n", "architecture");

        Assert.Contains($"\n    a {link} b\n", Export(document));
    }

    [Fact]
    public void A_thick_line_is_a_thick_link()
    {
        Document document = FromDsl("node n_a process \"A\"\nnode n_b process \"B\"\nedge e_1 flow n_a -> n_b style.strokeWidth=3\n", "flowchart");

        Assert.Contains("\n    a ==> b\n", Export(document));
    }

    [Fact]
    public void An_arrowhead_mermaid_has_no_spelling_for_is_a_plain_arrow_with_a_comment()
    {
        Document document = FromDsl("node n_a process \"A\"\nnode n_b process \"B\"\nedge e_1 flow n_a -> n_b style.arrowEnd=many\nedge e_2 flow n_b -> n_a\n", "flowchart");

        MermaidExportResult result = MermaidExporter.Export(document);

        Assert.Contains("    %% e_1: arrowhead 'many' has no Mermaid equivalent; drawn as a plain arrow\n    a --> b\n", result.Text);
        Assert.Equal(["e_1: arrowhead 'many' has no Mermaid equivalent; drawn as a plain arrow"], result.Warnings);
        Assert.DoesNotContain("e_2", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quote_or_hash_in_a_flowchart_label_is_an_entity_and_a_newline_a_break()
    {
        Document document = Boxes(FromDsl("node n_a process \"say \\\"hi\\\" #1\\nnow\"\nnode n_b process \"B\"\nedge e_1 flow n_a -> n_b \"a|b\"\n", "flowchart"));

        string text = Export(document);

        Assert.Contains("a[\"say #quot;hi#quot; #35;1<br/>now\"]", text);
        Document back = ReImport(text, DocumentType.Flowchart);
        Assert.Equal("say \"hi\" #1\nnow", back.Nodes[0].Label);
    }

    [Fact]
    public void A_link_to_an_undeclared_node_declares_it_and_a_parent_cycle_loses_no_node()
    {
        Document document = Boxes(FromDsl("node n_a process \"A\"\nnode n_b process \"B\"\nedge e_1 flow n_a -> n_b\n", "flowchart"));
        document = document with { Edges = [document.Edges[0] with { To = new Endpoint("n_ghost") }] };
        Assert.Contains("ghost[\"n_ghost\"]", Export(document));

        Document cyclic = document with { Nodes = [document.Nodes[0] with { Parent = "n_b" }, document.Nodes[1] with { Parent = "n_a" }], Edges = [] };
        string text = Export(cyclic);
        Assert.Contains("[\"A\"]", text);
        Assert.Contains("[\"B\"]", text);
    }

    [Fact]
    public void No_layout_is_exported_and_the_title_is_quoted_only_when_yaml_needs_it()
    {
        Document document = Boxes(FromDsl("node n_a process \"A\" @ 40,80 320x80\n", "flowchart")) with { Title = "Orders: v2" };

        string text = Export(document);

        Assert.StartsWith("---\ntitle: \"Orders: v2\"\n---\nflowchart TD\n", text);
        Assert.DoesNotContain("40", text, StringComparison.Ordinal);
        Assert.DoesNotContain("320", text, StringComparison.Ordinal);
        Assert.Equal("Orders: v2", ReImport(text, DocumentType.Flowchart).Title);
    }

    [Fact]
    public void The_text_uses_unix_newlines_whatever_the_platform()
    {
        Assert.DoesNotContain('\r', Export(Checkout()));
    }
}
