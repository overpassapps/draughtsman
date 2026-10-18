using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Tests.Shared;

namespace Draughtsman.Core.Tests.Import;

public class MermaidImporterTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 21, 1, 2, 3, TimeSpan.Zero);

    // The linear-time guards below only have to separate linear parsing from the quadratic scans they replaced:
    // a quadratic parser needs minutes on these inputs. The budget is therefore sized for a loaded shared CI
    // runner (which measured ~1.9s for the slowest of them) rather than for a developer machine.
    private static readonly TimeSpan s_linearTimeBudget = TimeSpan.FromSeconds(10);

    [Theory]
    [MemberData(nameof(MermaidCorpus.Cases), MemberType = typeof(MermaidCorpus))]
    public void Corpus_snippet_produces_expected_graph(MermaidCase testCase)
    {
        ImportResult result = MermaidImporter.Import(testCase.Source, new MermaidImportOptions { Type = testCase.Type, Now = s_now });

        Assert.Equal(testCase.Nodes, result.Document.Nodes.Count);
        Assert.Equal(testCase.Edges, result.Document.Edges.Count);
        Assert.Equal(testCase.Direction, result.Direction);
        foreach ((string id, string kind) in testCase.Kinds)
        {
            Node? node = result.Document.Nodes.SingleOrDefault(n => n.Id == id);
            Assert.True(node is not null, $"expected node '{id}'; got {string.Join(", ", result.Document.Nodes.Select(n => n.Id))}");
            Assert.Equal(kind, node.Kind);
        }

        Assert.DoesNotContain(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
        Assert.DoesNotContain(result.Warnings, w => w.Code == ImportWarningCodes.UnsupportedDiagram);
    }

    [Theory]
    [MemberData(nameof(MermaidCorpus.Cases), MemberType = typeof(MermaidCorpus))]
    public void Corpus_output_is_schema_valid_and_internally_consistent(MermaidCase testCase)
    {
        ImportResult result = MermaidImporter.Import(testCase.Source, new MermaidImportOptions { Type = testCase.Type, Now = s_now });
        Document document = result.Document;

        SchemaCheck schema = ContractFiles.Evaluate(DocumentJson.Serialize(document));
        Assert.True(schema.IsValid, ContractFiles.DescribeErrors(schema));

        KindVocabulary vocabulary = KindVocabulary.For(testCase.Type);
        var nodeIds = document.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(document.Nodes.Count, nodeIds.Count);
        Assert.All(document.Nodes, n => Assert.True(vocabulary.IsKnownNodeKind(n.Kind), $"{n.Id} has unknown kind {n.Kind}"));
        Assert.All(document.Nodes, n => Assert.Null(n.Geometry));
        Assert.All(document.Nodes.Where(n => n.Parent is not null), n => Assert.Equal("container", document.Nodes.Single(p => p.Id == n.Parent).Kind));
        Assert.All(document.Edges, e => Assert.True(vocabulary.IsKnownEdgeKind(e.Kind), $"{e.Id} has unknown kind {e.Kind}"));
        Assert.All(document.Edges, e => Assert.Contains(e.From.Node, nodeIds));
        Assert.All(document.Edges, e => Assert.Contains(e.To.Node, nodeIds));
        Assert.Equal(Enumerable.Range(1, document.Edges.Count).Select(i => $"e_{i}"), document.Edges.Select(e => e.Id));

        Assert.All(result.Warnings.Where(w => w.Code == ImportWarningCodes.KindGuessed), w => Assert.Contains(w.ElementId!, nodeIds));
        Assert.Equal(s_now, document.Meta.CreatedAt);
        Assert.Equal(DocumentType.Flowchart == testCase.Type ? "flowchart" : "architecture", document.Type.ToString().ToLowerInvariant());
    }

    [Fact]
    public void Guessed_kinds_are_flagged_only_when_the_shape_is_genuinely_ambiguous()
    {
        // A plain box, a rounded box, a diamond and a stadium all have an obvious kind. A circle and a hexagon could be several things.
        ImportResult result = MermaidImporter.Import("flowchart TD\n  a[Box] --> b{Choice} --> c([End])\n  b --> d(Round)\n  d --> e((Hub))\n  e --> f{{Prep}}", new MermaidImportOptions { Now = s_now });

        string[] flagged = result.Warnings.Where(w => w.Code == ImportWarningCodes.KindGuessed).Select(w => w.ElementId!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "n_e", "n_f" }, flagged);
    }

    [Fact]
    public void A_bare_id_and_a_plain_box_are_the_ordinary_kind_not_a_guess_in_both_vocabularies()
    {
        foreach (MermaidImportOptions options in new[] { new MermaidImportOptions { Now = s_now }, new MermaidImportOptions { Now = s_now, Type = DocumentType.Architecture } })
        {
            ImportResult result = MermaidImporter.Import("flowchart TD\n  A --> B[Box] --> C(Rounded)", options);
            Assert.DoesNotContain(result.Warnings, w => w.Code == ImportWarningCodes.KindGuessed);
        }
    }

    [Fact]
    public void A_guess_warning_names_the_label_and_the_shape_the_author_drew_not_the_id()
    {
        ImportResult result = MermaidImporter.Import("flowchart TD\n  X1((Customer signs in)) --> Y2", new MermaidImportOptions { Now = s_now });

        ImportWarning warning = Assert.Single(result.Warnings, w => w.Code == ImportWarningCodes.KindGuessed);
        Assert.Contains("\"Customer signs in\"", warning.Message, StringComparison.Ordinal);
        Assert.Contains("a circle", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("X1", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_shapes_and_edges_are_carried_faithfully()
    {
        const string source = """
            flowchart LR
                A["Say #quot;hi#quot;<br/>twice"] -- greets --> B
                B -.->|maybe| C
                C ==> D
                D --- E
                E <--> F
            """;

        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });
        Document document = result.Document;

        Assert.Equal("Say \"hi\"\ntwice", document.Nodes.Single(n => n.Id == "n_A").Label);
        Assert.Equal("B", document.Nodes.Single(n => n.Id == "n_B").Label);
        Assert.Equal("rectangle", document.Nodes.Single(n => n.Id == "n_A").Shape);

        Edge greets = document.Edges[0];
        Assert.Equal("greets", greets.Label);
        Assert.Equal("flow", greets.Kind);
        Assert.Null(greets.Style);

        Edge maybe = document.Edges[1];
        Assert.Equal("maybe", maybe.Label);
        Assert.Equal(Dash.Dashed, maybe.Style?.Dash);

        Assert.Equal(3, document.Edges[2].Style?.StrokeWidth);
        Assert.Equal("association", document.Edges[3].Kind);
        Assert.Null(document.Edges[3].Style);
        Assert.Equal(ArrowHead.Arrow, document.Edges[4].Style?.ArrowStart);
    }

    [Fact]
    public void Architecture_edges_touching_a_database_are_data_and_dotted_are_dependency()
    {
        ImportResult result = MermaidImporter.Import("graph LR\n api --> db[(pg)]\n db --> api\n db --> replica[(pg2)]\n api -.-> lib\n api --> svc", new MermaidImportOptions { Type = DocumentType.Architecture, Now = s_now });

        Assert.Equal(new[] { "data", "data", "data", "dependency", "flow" }, result.Document.Edges.Select(e => e.Kind));
    }

    [Fact]
    public void Self_referencing_edge_imports_as_a_self_loop_without_waypoints()
    {
        ImportResult result = MermaidImporter.Import("flowchart TD\n A --> A", new MermaidImportOptions { Now = s_now });
        Document document = result.Document;

        Assert.Equal(new[] { "n_A" }, document.Nodes.Select(n => n.Id));
        Edge edge = Assert.Single(document.Edges);
        Assert.Equal("flow", edge.Kind);
        Assert.Equal("n_A", edge.From.Node);
        Assert.Equal("n_A", edge.To.Node);
        Assert.Null(edge.Routing);
        Assert.DoesNotContain(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Header_on_a_one_liner_sets_direction_without_spurious_warnings()
    {
        ImportResult result = MermaidImporter.Import("graph LR; A-->B; B-->C", new MermaidImportOptions { Now = s_now });

        Assert.Equal("LR", result.Direction);
        Assert.Equal(3, result.Document.Nodes.Count);
        Assert.Equal(2, result.Document.Edges.Count);
        Assert.DoesNotContain(result.Warnings, w => w.Code is ImportWarningCodes.MissingHeader or ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Trailing_comment_after_the_header_is_not_a_node()
    {
        ImportResult result = MermaidImporter.Import("flowchart TD %% layout note\n A --> B", new MermaidImportOptions { Now = s_now });

        Assert.Equal(new[] { "n_A", "n_B" }, result.Document.Nodes.Select(n => n.Id));
        Assert.DoesNotContain(result.Warnings, w => w.Code is ImportWarningCodes.MissingHeader or ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Surrogate_numeric_entity_is_left_verbatim_and_parsing_continues()
    {
        ImportResult result = MermaidImporter.Import("flowchart TD\n A[\"x #55296; y\"] --> B\n B --> C", new MermaidImportOptions { Now = s_now });

        Assert.Equal("x #55296; y", result.Document.Nodes.Single(n => n.Id == "n_A").Label);
        Assert.Equal(3, result.Document.Nodes.Count);
        Assert.DoesNotContain(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Long_ids_are_truncated_to_the_schema_limit_and_stay_unique()
    {
        string title = new('t', 200);
        string source = $"flowchart TD\n subgraph \"{title}\"\n  a\n end\n subgraph \"{title}x\"\n  b\n end\n {title} --> {title}x";

        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });
        Document document = result.Document;

        Assert.All(document.Nodes, n => Assert.True(n.Id.Length <= 128, $"{n.Id.Length} chars"));
        Assert.Equal(document.Nodes.Count, document.Nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, document.Nodes.Count(n => n.Kind == "container"));
        Assert.True(ContractFiles.Evaluate(DocumentJson.Serialize(document)).IsValid, ContractFiles.DescribeErrors(ContractFiles.Evaluate(DocumentJson.Serialize(document))));
    }

    [Fact]
    public void Warning_line_numbers_count_fence_and_front_matter_lines()
    {
        const string source = "```mermaid\n---\ntitle: Numbered\n---\nflowchart TD\n A --> B\n C[unclosed\n```";

        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });

        Assert.Equal("Numbered", result.Document.Title);
        ImportWarning error = Assert.Single(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
        Assert.Equal(7, error.Line);
        Assert.Equal(3, result.Document.Nodes.Count);
    }

    [Fact]
    public void Pathological_text_link_line_parses_in_linear_time()
    {
        string source = "flowchart TD\nA -- " + new string(' ', 500_000) + new string('y', 500_000);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < LoadScale.Scaled(s_linearTimeBudget), $"took {stopwatch.Elapsed}");
        Assert.Equal(new[] { "n_A" }, result.Document.Nodes.Select(n => n.Id));
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Header_followed_by_a_long_run_of_spaces_parses_in_linear_time()
    {
        string padding = new(' ', 500_000);
        string source = "flowchart" + padding + "x\ngraph" + padding + "y";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < LoadScale.Scaled(s_linearTimeBudget), $"took {stopwatch.Elapsed}");
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.MissingHeader);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Many_unclosed_subgraphs_parse_in_linear_time()
    {
        var builder = new System.Text.StringBuilder("flowchart TD\n");
        for (int i = 0; i < 60_000; i++)
        {
            builder.Append("subgraph a").Append(i).Append('\n');
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ImportResult result = MermaidImporter.Import(builder.ToString(), new MermaidImportOptions { Now = s_now });
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < LoadScale.Scaled(s_linearTimeBudget), $"took {stopwatch.Elapsed}");
        Assert.Equal(60_000, result.Document.Nodes.Count(n => n.Kind == "container"));
        ImportWarning unclosed = Assert.Single(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
        Assert.Contains("60000 subgraph(s) were not closed", unclosed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_ids_that_sanitise_to_the_same_base_allocate_in_linear_time()
    {
        const string digits = "一二三四";
        var builder = new System.Text.StringBuilder("flowchart TD\n");
        for (int i = 0; i < 50_000; i++)
        {
            builder.Append(' ');
            for (int shift = 0; shift < 8; shift++)
            {
                builder.Append(digits[(i >> (2 * shift)) & 3]);
            }

            builder.Append('\n');
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ImportResult result = MermaidImporter.Import(builder.ToString(), new MermaidImportOptions { Now = s_now });
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < LoadScale.Scaled(s_linearTimeBudget), $"took {stopwatch.Elapsed}");
        Assert.Equal(50_000, result.Document.Nodes.Count);
        Assert.Equal(50_000, result.Document.Nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(result.Document.Nodes, n => Assert.True(n.Id.Length <= 128));
    }

    [Fact]
    public void Long_line_of_two_closer_shapes_parses_in_linear_time()
    {
        string source = "flowchart LR\n" + string.Concat(Enumerable.Repeat("a[/x\\] --> ", 90_000)) + "z";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < LoadScale.Scaled(s_linearTimeBudget), $"took {stopwatch.Elapsed}");
        Assert.Equal(90_000, result.Document.Edges.Count);
        Assert.Equal("io", result.Document.Nodes.Single(n => n.Id == "n_a").Kind);
    }

    [Fact]
    public void Flowchart_cylinder_is_a_data_store_not_an_input_output_and_needs_no_review()
    {
        // QA-08: [(Orders)] is Mermaid's database symbol; it used to arrive as kind io and be flagged as a guess.
        ImportResult result = MermaidImporter.Import("flowchart TD\n a[Start] --> db[(Orders)]", new MermaidImportOptions { Now = s_now });

        Node store = result.Document.Nodes.Single(n => n.Label == "Orders");
        Assert.Equal("stored-data", store.Kind);
        Assert.Equal("cylinder", store.Shape);
        Assert.DoesNotContain(result.Warnings, w => w.Code == ImportWarningCodes.KindGuessed && w.ElementId == store.Id);
        Assert.Contains(KindVocabulary.For(DocumentType.Flowchart).NodeKinds, k => k.Name == "stored-data");
    }

    [Fact]
    public void Text_links_carry_heads_and_styles()
    {
        const string source = """
            flowchart LR
                A -- one --> B
                B == two ==> C
                C -. three .-> D
                D <-- four --> E
                E -- five --- F
                F -- six --x G
                G -- seven -- H
            """;

        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });
        Document document = result.Document;

        Assert.Equal(new[] { "one", "two", "three", "four", "five", "six" }, document.Edges.Select(e => e.Label));
        Assert.Equal(3, document.Edges[1].Style?.StrokeWidth);
        Assert.Equal(Dash.Dashed, document.Edges[2].Style?.Dash);
        Assert.Equal(ArrowHead.Arrow, document.Edges[3].Style?.ArrowStart);
        Assert.Equal("association", document.Edges[4].Kind);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.StyleApproximated && w.ElementId == "e_6");
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.ParseError && w.Line == 8);
    }

    [Fact]
    public void Text_links_do_not_need_whitespace_around_the_text()
    {
        const string source = """
            flowchart LR
                A--Yes-->B
                B -- No-->C
                C==Maybe==>D
                D-.Later.->E
                E --Soon--- F
            """;

        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });
        Document document = result.Document;

        Assert.Equal(new[] { "n_A", "n_B", "n_C", "n_D", "n_E", "n_F" }, document.Nodes.Select(n => n.Id));
        Assert.Equal(new[] { "Yes", "No", "Maybe", "Later", "Soon" }, document.Edges.Select(e => e.Label));
        Assert.Equal(3, document.Edges[2].Style?.StrokeWidth);
        Assert.Equal(Dash.Dashed, document.Edges[3].Style?.Dash);
        Assert.Equal("association", document.Edges[4].Kind);
        Assert.DoesNotContain(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Long_rails_stay_unlabelled_links()
    {
        string source = "flowchart LR\n A -------- B\n B --------> C\n C ======== D\n D " + new string('-', 400_000) + "> E";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < LoadScale.Scaled(s_linearTimeBudget), $"took {stopwatch.Elapsed}");
        Assert.Equal(5, result.Document.Nodes.Count);
        Assert.All(result.Document.Edges, e => Assert.Null(e.Label));
        Assert.Equal(new[] { "association", "flow", "association", "flow" }, result.Document.Edges.Select(e => e.Kind));
        Assert.DoesNotContain(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Flowchart_elk_header_is_read_as_a_flowchart()
    {
        ImportResult result = MermaidImporter.Import("flowchart-elk LR\n A --> B", new MermaidImportOptions { Now = s_now });

        Assert.Equal("LR", result.Direction);
        Assert.Equal(new[] { "n_A", "n_B" }, result.Document.Nodes.Select(n => n.Id));
        Assert.DoesNotContain(result.Warnings, w => w.Code is ImportWarningCodes.MissingHeader or ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Node_stays_in_the_first_subgraph_that_mentions_it()
    {
        const string source = """
            graph LR
                subgraph frontend
                    web --> api
                end
                subgraph backend
                    api --> db
                end
            """;

        Document document = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now }).Document;

        Assert.Equal("c_frontend", document.Nodes.Single(n => n.Id == "n_web").Parent);
        Assert.Equal("c_frontend", document.Nodes.Single(n => n.Id == "n_api").Parent);
        Assert.Equal("c_backend", document.Nodes.Single(n => n.Id == "n_db").Parent);
    }

    [Fact]
    public void Node_mentioned_in_a_block_and_its_nested_block_belongs_to_the_nested_one()
    {
        const string source = """
            graph TD
                subgraph cloud
                    lb --> api
                    subgraph k8s
                        api --> svc
                    end
                    api --> log
                end
            """;

        Document document = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now }).Document;

        Assert.Equal("c_cloud", document.Nodes.Single(n => n.Id == "n_lb").Parent);
        Assert.Equal("c_k8s", document.Nodes.Single(n => n.Id == "n_api").Parent);
        Assert.Equal("c_k8s", document.Nodes.Single(n => n.Id == "n_svc").Parent);
        Assert.Equal("c_cloud", document.Nodes.Single(n => n.Id == "n_log").Parent);
        Assert.Equal("c_cloud", document.Nodes.Single(n => n.Id == "c_k8s").Parent);
    }

    [Fact]
    public void Unclosed_nested_blocks_still_claim_their_nodes_innermost_first()
    {
        ImportResult result = MermaidImporter.Import("graph TD\n subgraph outer\n a --> b\n subgraph inner\n b --> c", new MermaidImportOptions { Now = s_now });
        Document document = result.Document;

        Assert.Equal("c_outer", document.Nodes.Single(n => n.Id == "n_a").Parent);
        Assert.Equal("c_inner", document.Nodes.Single(n => n.Id == "n_b").Parent);
        Assert.Equal("c_inner", document.Nodes.Single(n => n.Id == "n_c").Parent);
        Assert.Contains(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Keywords_are_case_sensitive_and_need_a_following_space()
    {
        const string source = """
            flowchart TD
                accTitle: Accessible title
                subgraph s
                    Start --> End
                    click[Click handler] --> dispatch
                    class[Class] --> object
                end
                style[Style guide] --> tokens
            """;

        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });
        Document document = result.Document;

        Assert.All(new[] { "n_Start", "n_End", "n_click", "n_dispatch", "n_class", "n_object" }, id => Assert.Equal("c_s", document.Nodes.Single(n => n.Id == id).Parent));
        Assert.Equal("Style guide", document.Nodes.Single(n => n.Id == "n_style").Label);
        Assert.DoesNotContain(document.Nodes, n => n.Id == "n_accTitle");
        Assert.Single(result.Warnings, w => w.Code == ImportWarningCodes.UnsupportedSyntax);
        Assert.DoesNotContain(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Direction_is_only_a_keyword_when_a_direction_token_follows()
    {
        const string source = """
            flowchart TD
                Direction[Pick a direction] --> go
                direction --> next
                subgraph s
                    direction LR
                    a --> b
                end
            """;

        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });
        Document document = result.Document;

        Assert.Equal("Pick a direction", document.Nodes.Single(n => n.Id == "n_Direction").Label);
        Assert.Equal(new[] { "n_Direction", "n_go", "n_direction", "n_next", "c_s", "n_a", "n_b" }.Order(), document.Nodes.Select(n => n.Id).Order());
        Assert.Equal("c_s", document.Nodes.Single(n => n.Id == "n_a").Parent);
        Assert.DoesNotContain(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Odd_quote_in_a_comment_does_not_swallow_the_following_lines()
    {
        const string source = """
            flowchart TD
                A --> B %% see "note
                C --> D
                E["multi
                line"] --> F
            """;

        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });

        Assert.Equal(6, result.Document.Nodes.Count);
        Assert.Equal(3, result.Document.Edges.Count);
        Assert.Equal("multi\nline", result.Document.Nodes.Single(n => n.Id == "n_E").Label);
        Assert.DoesNotContain(result.Warnings, w => w.Code == ImportWarningCodes.ParseError);
    }

    [Fact]
    public void Unsupported_diagram_warning_names_the_header_line()
    {
        const string source = "```mermaid\n---\ntitle: Orders\n---\nclassDiagram\n  Animal <|-- Duck\n```";

        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });

        ImportWarning warning = Assert.Single(result.Warnings);
        Assert.Equal(ImportWarningCodes.UnsupportedDiagram, warning.Code);
        Assert.Equal(5, warning.Line);
    }

    [Fact]
    public void Large_paste_with_an_open_quote_imports_in_linear_time()
    {
        var builder = new System.Text.StringBuilder("flowchart TD\n A[\"open\n");
        for (int i = 0; i < 50_000; i++)
        {
            builder.Append("n").Append(i).Append(" --> n").Append(i + 1).Append('\n');
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ImportResult result = MermaidImporter.Import(builder.ToString(), new MermaidImportOptions { Now = s_now });
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < LoadScale.Scaled(s_linearTimeBudget), $"took {stopwatch.Elapsed}");
        Assert.Equal(50_002, result.Document.Nodes.Count);
    }

    [Fact]
    public void Subgraph_membership_and_links_to_subgraphs_resolve_to_containers()
    {
        const string source = """
            flowchart TB
                subgraph api [API tier]
                    a --> b
                    subgraph inner
                        c
                    end
                end
                api --> x
                x --> inner
            """;

        Document document = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now }).Document;

        Assert.Equal("c_api", document.Nodes.Single(n => n.Id == "n_a").Parent);
        Assert.Equal("c_inner", document.Nodes.Single(n => n.Id == "n_c").Parent);
        Assert.Equal("c_api", document.Nodes.Single(n => n.Id == "c_inner").Parent);
        Assert.Equal("API tier", document.Nodes.Single(n => n.Id == "c_api").Label);
        Assert.Null(document.Nodes.Single(n => n.Id == "n_x").Parent);
        Assert.Equal(("c_api", "n_x"), (document.Edges[1].From.Node, document.Edges[1].To.Node));
        Assert.Equal(("n_x", "c_inner"), (document.Edges[2].From.Node, document.Edges[2].To.Node));
        Assert.DoesNotContain(document.Nodes, n => n.Id is "n_api" or "n_inner");
    }

    [Fact]
    public void Later_declaration_refines_an_earlier_bare_reference()
    {
        Document document = MermaidImporter.Import("flowchart TD\n A --> B\n B{Really?}", new MermaidImportOptions { Now = s_now }).Document;

        Node b = document.Nodes.Single(n => n.Id == "n_B");
        Assert.Equal("decision", b.Kind);
        Assert.Equal("Really?", b.Label);
        Assert.Equal(2, document.Nodes.Count);
    }

    [Fact]
    public void Ids_are_sanitised_and_deduplicated_with_a_warning()
    {
        ImportResult result = MermaidImporter.Import("flowchart TD\n xéy --> x_y", new MermaidImportOptions { Now = s_now });
        Document document = result.Document;

        Assert.Equal(new[] { "n_x_y", "n_x_y_2" }, document.Nodes.Select(n => n.Id));
        Assert.Equal("xéy", document.Nodes[0].Label);
        Assert.Equal(2, result.Warnings.Count(w => w.Code == ImportWarningCodes.IdRenamed));
        Assert.True(ContractFiles.Evaluate(DocumentJson.Serialize(document)).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t\n")]
    [InlineData("flowchart TD")]
    [InlineData("flowchart XX\n A --> B")]
    [InlineData("flowchart TD\n A[unclosed --> B")]
    [InlineData("flowchart TD\n A --> \n --> B")]
    [InlineData("flowchart TD\n A -->|label B")]
    [InlineData("flowchart TD\n subgraph\n A\n")]
    [InlineData("flowchart TD\n end\n end\n A")]
    [InlineData("flowchart TD\n A --> B\n subgraph s\n B --> C")]
    [InlineData("flowchart TD\n ((((((\n ]]]]]]\n |||\n &&& --> ---")]
    [InlineData("classDiagram\n Animal <|-- Duck")]
    [InlineData("just some prose that is not a diagram at all.")]
    [InlineData("flowchart TD\n A --> B\n graph LR\n C --> D")]
    [InlineData("---\ntitle: never closed\nflowchart TD\n A --> B")]
    [InlineData("flowchart TD\n A --> B --> --> C")]
    public void Malformed_input_yields_warnings_not_exceptions(string source)
    {
        ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });

        Assert.NotNull(result.Document);
        Assert.NotEmpty(result.Warnings);
        AssertValidDocument(result);
    }

    // These read as a plain diagram (an unclosed fence, a stray quote, an empty class suffix): oddly written, not lossy, so
    // there is nothing to warn about - they only have to come back as a valid document.
    [Theory]
    [InlineData("```\nflowchart TD\n A --> B")]
    [InlineData("flowchart TD\n A[\"unterminated quote] --> B")]
    [InlineData("flowchart TD\n A:::\n B:::cls --> C")]
    public void Oddly_written_input_still_yields_a_valid_document(string source)
    {
        AssertValidDocument(MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now }));
    }

    private static void AssertValidDocument(ImportResult result)
    {
        Assert.True(ContractFiles.Evaluate(DocumentJson.Serialize(result.Document)).IsValid, ContractFiles.DescribeErrors(ContractFiles.Evaluate(DocumentJson.Serialize(result.Document))));
        Assert.All(result.Document.Edges, e => Assert.Contains(result.Document.Nodes, n => n.Id == e.From.Node));
        Assert.All(result.Document.Edges, e => Assert.Contains(result.Document.Nodes, n => n.Id == e.To.Node));
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var random = new Random(20260921);
        const string alphabet = "AB[]{}()<>|-.=&;\"\n %#:/\\x o`é";
        for (int i = 0; i < 500; i++)
        {
            char[] chars = new char[random.Next(0, 120)];
            for (int j = 0; j < chars.Length; j++)
            {
                chars[j] = alphabet[random.Next(alphabet.Length)];
            }

            string source = "flowchart TD\n" + new string(chars);
            ImportResult result = MermaidImporter.Import(source, new MermaidImportOptions { Now = s_now });
            Assert.NotNull(result.Document);
        }
    }

    [Fact]
    public void Other_diagram_types_are_reported_and_produce_an_empty_document()
    {
        ImportResult result = MermaidImporter.Import("classDiagram\n  Animal <|-- Duck", new MermaidImportOptions { Now = s_now });

        Assert.True(result.UnsupportedDiagram);
        Assert.Empty(result.Document.Nodes);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void Title_comes_from_options_then_front_matter_then_default()
    {
        const string withFrontMatter = "---\ntitle: From source\n---\nflowchart TD\n A";

        Assert.Equal("Chosen", MermaidImporter.Import(withFrontMatter, new MermaidImportOptions { Title = "Chosen", Now = s_now }).Document.Title);
        Assert.Equal("From source", MermaidImporter.Import(withFrontMatter, new MermaidImportOptions { Now = s_now }).Document.Title);
        Assert.Equal("Imported diagram", MermaidImporter.Import("flowchart TD\n A", new MermaidImportOptions { Now = s_now }).Document.Title);
    }

    [Fact]
    public void Freeform_type_is_rejected_up_front()
    {
        Assert.Throws<ArgumentException>(() => MermaidImporter.Import("flowchart TD\n A", new MermaidImportOptions { Type = DocumentType.Freeform }));
    }
}
