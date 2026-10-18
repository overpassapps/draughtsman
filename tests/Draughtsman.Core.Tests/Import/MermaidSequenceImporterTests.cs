using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Core.Tests.Import;

/// <summary>
/// The Mermaid <c>sequenceDiagram</c> importer (docs/sequence-diagrams.md, docs/api.md <c>POST /api/import/mermaid</c>).
/// Fixtures are this file's own; each assertion names a literal, so a mapping that drifts fails here rather than agreeing with itself.
/// </summary>
public class MermaidSequenceImporterTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 29, 1, 2, 3, TimeSpan.Zero);

    private static ImportResult Import(string source, string? title = null)
    {
        return MermaidImporter.Import(source, new MermaidImportOptions { Type = DocumentType.Sequence, Now = s_now, Title = title });
    }

    private static IEnumerable<ImportWarning> Unsupported(ImportResult result)
    {
        return result.Warnings.Where(w => w.Code == ImportWarningCodes.UnsupportedSyntax);
    }

    [Theory]
    [InlineData("->>", "sync", null, null, null)]
    [InlineData("-->>", "return", null, null, null)]
    [InlineData("-)", "async", null, null, null)]
    [InlineData("--)", "async", Dash.Dashed, null, null)]
    [InlineData("->", "sync", null, null, ArrowHead.None)]
    [InlineData("-->", "sync", Dash.Dashed, null, ArrowHead.None)]
    [InlineData("<<->>", "sync", null, ArrowHead.Arrow, null)]
    [InlineData("<<-->>", "return", null, ArrowHead.Open, null)]
    public void Each_mapped_arrow_becomes_the_expected_kind_and_only_the_style_that_differs_from_that_kinds_default(string arrow, string kind, Dash? dash, ArrowHead? start, ArrowHead? end)
    {
        ImportResult result = Import($"sequenceDiagram\n  A{arrow}B: hello");

        Edge message = Assert.Single(result.Document.Edges);
        Assert.Equal("m_1", message.Id);
        Assert.Equal(kind, message.Kind);
        Assert.Equal("hello", message.Label);
        Assert.Equal(("p_A", "p_B"), (message.From.Node, message.To.Node));
        Assert.Equal((dash, start, end), (message.Style?.Dash, message.Style?.ArrowStart, message.Style?.ArrowEnd));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_guard_is_stored_bare_whether_or_not_the_source_bracketed_it()
    {
        ImportResult result = Import("""
            sequenceDiagram
                participant W as Web
                participant P as Payments
                alt [card ok]
                    W->>P: charge
                else declined
                    P-->>W: declined
                end
            """);

        Node fragment = Assert.Single(result.Document.Nodes, n => n.Kind == "fragment");
        Assert.Equal(["card ok", "declined"], fragment.Props!["operands"].EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void A_plain_sync_message_carries_no_style_at_all()
    {
        Edge message = Assert.Single(Import("sequenceDiagram\n A->>B: x").Document.Edges);

        Assert.Null(message.Style);
        Assert.Null(message.Props);
        Assert.Null(message.Routing);
    }

    [Fact]
    public void The_lost_message_cross_imports_as_a_sync_message_with_one_warning_naming_it()
    {
        ImportResult result = Import("sequenceDiagram\n A-xB: lost\n B--xA: also lost\n A->>B: after");

        Assert.Equal(["sync", "sync", "sync"], result.Document.Edges.Select(e => e.Kind));
        Assert.Equal(["lost", "also lost", "after"], result.Document.Edges.Select(e => e.Label));
        Assert.Equal(Dash.Dashed, result.Document.Edges[1].Style?.Dash);
        Assert.Equal([2, 3], Unsupported(result).Select(w => w.Line));
        Assert.All(Unsupported(result), w => Assert.Contains("cross", w.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Participants_and_actors_keep_their_display_name_and_get_valid_ids()
    {
        ImportResult result = Import("""
            sequenceDiagram
                participant W as Web app
                actor U as Customer
                participant API
                U->>W: opens
                W->>API: POST /orders
            """);

        Assert.Equal(
            [("p_W", "participant", "Web app"), ("p_U", "actor", "Customer"), ("p_API", "participant", "API")],
            result.Document.Nodes.Select(n => (n.Id, n.Kind, n.Label)));
        Assert.Equal(["rectangle", "rounded", "rectangle"], result.Document.Nodes.Select(n => n.Shape));
        Assert.Equal([("p_U", "p_W"), ("p_W", "p_API")], result.Document.Edges.Select(e => (e.From.Node, e.To.Node)));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void An_id_that_is_not_a_valid_document_id_is_slugged_and_reported_and_two_that_collide_stay_distinct()
    {
        ImportResult result = Import("sequenceDiagram\n participant Web App\n participant Web_App\n participant Café as Le Café\n Web App->>Web_App: ping");

        Assert.Equal(["p_Web_App", "p_Web_App_2", "p_Caf_"], result.Document.Nodes.Select(n => n.Id));
        Assert.Equal(["Web App", "Web_App", "Le Café"], result.Document.Nodes.Select(n => n.Label));
        Assert.Equal(("p_Web_App", "p_Web_App_2"), (result.Document.Edges[0].From.Node, result.Document.Edges[0].To.Node));
        Assert.Equal(["p_Web_App", "p_Web_App_2", "p_Caf_"], result.Warnings.Where(w => w.Code == ImportWarningCodes.IdRenamed).Select(w => w.ElementId));
        Assert.True(ContractFiles.Evaluate(DocumentJson.Serialize(result.Document)).IsValid);
    }

    [Fact]
    public void A_participant_only_mentioned_in_a_message_is_created_where_it_first_appears()
    {
        ImportResult result = Import("""
            sequenceDiagram
                Ghost->>A: boo
                participant A as Alice
                A->>Late: later
                participant Early
            """);

        // Ghost appears first (in a message), then A (that message's target, its alias applying afterwards), then Late, then Early.
        Assert.Equal(["p_Ghost", "p_A", "p_Late", "p_Early"], result.Document.Nodes.Select(n => n.Id));
        Assert.Equal(["Ghost", "Alice", "Late", "Early"], result.Document.Nodes.Select(n => n.Label));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_hyphenated_id_and_an_id_with_a_space_read_as_participants()
    {
        ImportResult result = Import("sequenceDiagram\n Web-App->>Orders API: go\n Orders API-->>Web-App: ok");

        Assert.Equal(["p_Web-App", "p_Orders_API"], result.Document.Nodes.Select(n => n.Id));
        Assert.Equal([("p_Web-App", "p_Orders_API"), ("p_Orders_API", "p_Web-App")], result.Document.Edges.Select(e => (e.From.Node, e.To.Node)));
    }

    [Fact]
    public void A_self_message_has_the_same_participant_at_both_ends()
    {
        ImportResult result = Import("sequenceDiagram\n A->>A: think");

        Edge message = Assert.Single(result.Document.Edges);
        Assert.Equal(("p_A", "p_A"), (message.From.Node, message.To.Node));
        Assert.Single(result.Document.Nodes);
    }

    [Fact]
    public void Plus_and_minus_on_the_arrow_are_activate_on_the_receiver_and_deactivate_on_the_sender()
    {
        ImportResult result = Import("""
            sequenceDiagram
                A->>+B: start
                B->>+C: nested
                C-->>-B: done
                B-->>-A: reply
                A->>B: plain
            """);

        Assert.Equal([(true, false), (true, false), (false, true), (false, true), (false, false)], result.Document.Edges.Select(e => (SequenceMessages.IsSet(e, "activate"), SequenceMessages.IsSet(e, "deactivate"))));
        Assert.Equal(["start", "nested", "done", "reply", "plain"], result.Document.Edges.Select(e => e.Label));
        Assert.Null(result.Document.Edges[4].Props);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_standalone_activate_or_deactivate_attaches_only_where_the_model_can_say_it_and_warns_otherwise()
    {
        ImportResult result = Import("""
            sequenceDiagram
                A->>B: req
                activate B
                B-->>A: reply
                deactivate B
                A->>C: other
                activate A
                deactivate C
            """);

        Assert.Equal([(true, false), (false, true), (false, false)], result.Document.Edges.Select(e => (SequenceMessages.IsSet(e, "activate"), SequenceMessages.IsSet(e, "deactivate"))));
        // 'activate A' follows a message that goes to C, 'deactivate C' one that comes from A: neither is attachable.
        Assert.Equal([7, 8], Unsupported(result).Select(w => w.Line));
    }

    [Fact]
    public void Unsupported_constructs_each_give_one_warning_and_the_messages_around_and_inside_them_still_import_in_order()
    {
        ImportResult result = Import("""
            sequenceDiagram
                autonumber
                participant A
                participant B
                A->>B: first
                rect rgb(200, 200, 255)
                    A->>B: shaded
                end
                box Aqua Group
                    participant E
                end
                link A: Docs @ https://example.com
                A-xB: lost
                B-->>A: last
            """);

        Assert.Equal(["first", "shaded", "lost", "last"], result.Document.Edges.Select(e => e.Label));
        Assert.Equal(["m_1", "m_2", "m_3", "m_4"], result.Document.Edges.Select(e => e.Id));
        Assert.Equal(["p_A", "p_B", "p_E"], result.Document.Nodes.Select(n => n.Id));

        // autonumber, rect, box, link, the lost-message cross: one warning each, with its line. rect and box draw no frame,
        // so nothing of them is in the document.
        Assert.Equal([2, 6, 9, 12, 13], Unsupported(result).Select(w => w.Line));
        Assert.All(result.Warnings, w => Assert.Equal(ImportWarningCodes.UnsupportedSyntax, w.Code));
        Assert.DoesNotContain(result.Document.Nodes, n => n.Kind == "fragment");
    }

    [Fact]
    public void An_alt_else_block_becomes_a_fragment_whose_messages_carry_in_and_the_operand_they_are_under()
    {
        ImportResult result = Import("""
            sequenceDiagram
                participant W as Web
                participant P as Payments
                W->>P: charge
                alt card ok
                    P-->>W: receipt
                    W->>P: capture
                else declined
                    P-->>W: declined
                end
                W->>P: done
            """);

        Assert.Empty(result.Warnings);
        Node fragment = Assert.Single(result.Document.Nodes, n => n.Kind == "fragment");
        Assert.Equal(("f_1", "alt", "rectangle"), (fragment.Id, fragment.Label, fragment.Shape));
        Assert.Equal("alt", fragment.Props!["operator"].GetString());
        Assert.Equal(["card ok", "declined"], fragment.Props["operands"].EnumerateArray().Select(e => e.GetString()));
        Assert.Null(fragment.Parent);
        // Only the messages inside the block are members; the operand is written only when it is not 0.
        Assert.Equal(
            [(null, null), ("f_1", null), ("f_1", null), ("f_1", (int?)1), (null, null)],
            result.Document.Edges.Select(e => (SequenceMessages.FragmentOf(e), e.Props is not null && e.Props.TryGetValue("operand", out System.Text.Json.JsonElement operand) ? (int?)operand.GetInt32() : null)));
    }

    [Fact]
    public void The_other_block_words_map_to_their_operators_with_par_and_critical_operands()
    {
        ImportResult result = Import("""
            sequenceDiagram
                loop every minute
                    A->>B: tick
                end
                opt cache miss
                    A->>B: fetch
                end
                par to the queue
                    A->>B: enqueue
                and to the log
                    A->>B: write
                end
                critical connect
                    A->>B: open
                option timeout
                    A->>B: retry
                end
                break stop
                    A->>B: abort
                end
            """);

        Assert.Empty(result.Warnings);
        Assert.Equal(
            [("f_1", "loop", "every minute"), ("f_2", "opt", "cache miss"), ("f_3", "par", "to the queue,to the log"), ("f_4", "critical", "connect,timeout"), ("f_5", "break", "stop")],
            result.Document.Nodes.Where(n => n.Kind == "fragment").Select(n => (n.Id, n.Props!["operator"].GetString()!, string.Join(',', n.Props["operands"].EnumerateArray().Select(e => e.GetString())))));
        Assert.Equal(["f_1", "f_2", "f_3", "f_3", "f_4", "f_4", "f_5"], result.Document.Edges.Select(e => SequenceMessages.FragmentOf(e)));
    }

    [Fact]
    public void A_block_with_no_guard_text_has_an_empty_guard_and_one_already_in_brackets_is_stored_bare()
    {
        ImportResult result = Import("sequenceDiagram\n loop\n A->>B: x\n end\n opt [ready]\n A->>B: y\n end");

        Assert.Empty(result.Warnings);
        Assert.Equal(
            [[""], ["ready"]],
            result.Document.Nodes.Where(n => n.Kind == "fragment").Select(n => n.Props!["operands"].EnumerateArray().Select(e => e.GetString()!).ToArray()));
    }

    [Fact]
    public void A_block_inside_a_block_is_a_fragment_that_names_its_parent_and_the_operand_of_it_it_sits_under()
    {
        ImportResult result = Import("""
            sequenceDiagram
                alt a
                    A->>B: one
                else b
                    loop n times
                        A->>B: two
                    end
                    A->>B: three
                end
            """);

        Assert.Empty(result.Warnings);
        Node outer = Assert.Single(result.Document.Nodes, n => n.Id == "f_1");
        Node inner = Assert.Single(result.Document.Nodes, n => n.Id == "f_2");
        Assert.Equal(("alt", null), (outer.Props!["operator"].GetString(), outer.Parent));
        Assert.Equal(("loop", "f_1", 1), (inner.Props!["operator"].GetString(), inner.Parent, inner.Props["operand"].GetInt32()));
        Assert.Equal([("f_1", 0), ("f_2", 0), ("f_1", 1)], result.Document.Edges.Select(e => (SequenceMessages.FragmentOf(e)!, e.Props!.TryGetValue("operand", out System.Text.Json.JsonElement operand) ? operand.GetInt32() : 0)));
    }

    [Fact]
    public void A_block_holding_no_message_is_left_out_with_a_warning_and_an_unclosed_block_ends_with_the_diagram()
    {
        ImportResult empty = Import("sequenceDiagram\n A->>B: x\n opt nothing here\n end\n A->>B: y");

        Assert.DoesNotContain(empty.Document.Nodes, n => n.Kind == "fragment");
        Assert.Equal(3, Assert.Single(Unsupported(empty)).Line);

        ImportResult unclosed = Import("sequenceDiagram\n alt never closed\n A->>B: x");

        Assert.Equal("f_1", SequenceMessages.FragmentOf(Assert.Single(unclosed.Document.Edges)));
        ImportWarning warning = Assert.Single(unclosed.Warnings);
        Assert.Equal((ImportWarningCodes.ParseError, 2), (warning.Code, warning.Line));
    }

    [Fact]
    public void Notes_become_note_rows_across_the_lifelines_they_name_and_take_their_place_in_the_order()
    {
        ImportResult result = Import("""
            sequenceDiagram
                participant A
                participant B
                participant C
                A->>B: first
                Note over A,C: spans<br/>all three
                Note over B: alone
                Note left of A: to the left
                Note right of C: to the right
                B->>C: last
            """);

        Assert.Equal(["m_1", "n_1", "n_2", "n_3", "n_4", "m_2"], result.Document.Edges.Select(e => e.Id));
        Assert.Equal(["note", "note", "note", "note"], result.Document.Edges.Where(e => e.Id.StartsWith('n')).Select(e => e.Kind));
        Assert.Equal(
            [("p_A", "p_C", "spans all three"), ("p_B", "p_B", "alone"), ("p_A", "p_A", "to the left"), ("p_C", "p_C", "to the right")],
            result.Document.Edges.Where(e => e.Kind == "note").Select(e => (e.From.Node, e.To.Node, e.Label!)));
        Assert.All(result.Document.Edges.Where(e => e.Kind == "note"), e => Assert.Null(e.Style));
        // over is exact; left of and right of are drawn over the lifeline, and say so.
        Assert.Equal([8, 9], Unsupported(result).Select(w => w.Line));
    }

    [Fact]
    public void A_note_inside_a_block_is_a_member_of_its_fragment()
    {
        ImportResult result = Import("sequenceDiagram\n opt x\n A->>B: y\n Note over A: hm\n end");

        Assert.Empty(result.Warnings);
        Assert.Equal(["f_1", "f_1"], result.Document.Edges.Select(e => SequenceMessages.FragmentOf(e)));
    }

    [Fact]
    public void A_create_statement_makes_the_next_message_to_that_participant_a_create_message()
    {
        ImportResult result = Import("""
            sequenceDiagram
                participant A
                A->>A: warm up
                create participant B as Bob
                A->>B: hi Bob
                B-->>A: hello
            """);

        Assert.Empty(result.Warnings);
        Assert.Equal(["p_A", "p_B"], result.Document.Nodes.Select(n => n.Id));
        Assert.Equal("Bob", result.Document.Nodes[1].Label);
        Assert.Equal(["sync", "create", "return"], result.Document.Edges.Select(e => e.Kind));
        // ->> is solid with a filled head; a create message draws dashed with an open head by default, so the difference is written.
        Assert.Equal((Dash.Solid, ArrowHead.Arrow), (result.Document.Edges[1].Style?.Dash, result.Document.Edges[1].Style?.ArrowEnd));
    }

    [Fact]
    public void A_create_message_whose_arrow_is_the_create_look_writes_no_style()
    {
        ImportResult result = Import("sequenceDiagram\n create participant B\n A--)B: spawn");

        Assert.Equal("create", Assert.Single(result.Document.Edges).Kind);
        // --) is dashed and async-open: exactly what a create message draws by default.
        Assert.Null(result.Document.Edges[0].Style);
    }

    [Fact]
    public void A_destroy_statement_makes_the_next_message_to_that_participant_a_destroy_message()
    {
        ImportResult result = Import("""
            sequenceDiagram
                participant A
                participant C
                A->>C: hello
                destroy C
                A-xC: we are too many
                A->>C: after
            """);

        Assert.Empty(Unsupported(result));
        Assert.Equal(["sync", "destroy", "sync"], result.Document.Edges.Select(e => e.Kind));
        // The cross is how Mermaid writes the message that destroys; it is not also warned about as a lost message.
        Assert.Null(result.Document.Edges[1].Style);
    }

    [Fact]
    public void A_create_or_destroy_the_next_message_does_not_fit_is_warned_and_the_message_stays_plain()
    {
        ImportResult result = Import("""
            sequenceDiagram
                participant A
                participant B
                create participant D
                A->>B: unrelated
                destroy B
                B->>A: from the destroyed one
            """);

        Assert.Equal(["sync", "sync"], result.Document.Edges.Select(e => e.Kind));
        Assert.Equal([4, 6], Unsupported(result).Select(w => w.Line));
        Assert.Contains(result.Document.Nodes, n => n.Id == "p_D");
    }

    [Fact]
    public void A_block_with_notes_a_create_and_a_destroy_round_trips_through_the_dsl()
    {
        ImportResult result = Import("""
            sequenceDiagram
                actor U as Customer
                participant W as Web
                participant P as Payments
                U->>W: pay
                alt card ok
                    W->>P: charge
                    Note over W,P: PCI scope
                    P-->>W: receipt
                else declined
                    P-->>W: declined
                end
                create participant M as Mailer
                W->>M: notify
                destroy M
                W-xM: done
            """);

        string printed = Dsl.Print(result.Document);
        Document reparsed = Dsl.Parse(printed).Document;

        Assert.Contains("node f_1 fragment \"alt\"", printed, StringComparison.Ordinal);
        Assert.Contains("in=\"f_1\"", printed, StringComparison.Ordinal);
        Assert.Contains("operand=1", printed, StringComparison.Ordinal);
        Assert.Contains("edge n_1 note p_W -> p_P \"PCI scope\"", printed, StringComparison.Ordinal);
        Assert.Equal(result.Document.Nodes.Select(n => (n.Id, n.Kind, n.Label, n.Parent)), reparsed.Nodes.Select(n => (n.Id, n.Kind, n.Label, n.Parent)));
        Assert.Equal(result.Document.Edges.Select(e => (e.Id, e.Kind, e.Label, e.From.Node, e.To.Node, SequenceMessages.FragmentOf(e))), reparsed.Edges.Select(e => (e.Id, e.Kind, e.Label, e.From.Node, e.To.Node, SequenceMessages.FragmentOf(e))));
        Assert.Equal(["card ok", "declined"], reparsed.Nodes.Single(n => n.Kind == "fragment").Props!["operands"].EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(printed, Dsl.Print(reparsed));
        SchemaCheck schema = ContractFiles.Evaluate(DocumentJson.Serialize(result.Document));
        Assert.True(schema.IsValid, ContractFiles.DescribeErrors(schema));
    }

    [Fact]
    public void An_end_with_no_block_and_a_line_that_is_no_statement_are_parse_errors_not_exceptions()
    {
        ImportResult result = Import("sequenceDiagram\n A->>B: one\n end\n this is not mermaid at all\n B->>A: two");

        Assert.Equal(["one", "two"], result.Document.Edges.Select(e => e.Label));
        Assert.Equal([3, 4], result.Warnings.Where(w => w.Code == ImportWarningCodes.ParseError).Select(w => w.Line));
    }

    [Fact]
    public void Comments_are_skipped_and_the_title_comes_from_a_title_statement_front_matter_or_the_option_in_that_order()
    {
        const string body = "sequenceDiagram\n %% a comment\n A->>B: x\n%%{init: {'theme': 'dark'}}%%\n";
        Assert.Equal("Imported diagram", Import(body).Document.Title);
        Assert.Equal("Checkout flow", Import("sequenceDiagram\n title Checkout flow\n A->>B: x").Document.Title);
        Assert.Equal("Checkout flow", Import("sequenceDiagram\n title: Checkout flow\n A->>B: x").Document.Title);
        Assert.Equal("From front matter", Import("---\ntitle: From front matter\n---\nsequenceDiagram\n A->>B: x").Document.Title);
        Assert.Equal("Option wins", Import("sequenceDiagram\n title Ignored\n A->>B: x", "Option wins").Document.Title);
        ImportResult commented = Import(body);
        Assert.Empty(commented.Warnings);
        Assert.Single(commented.Document.Edges);
    }

    [Fact]
    public void A_fenced_mermaid_block_with_crlf_line_endings_imports_with_line_numbers_that_match_the_pasted_text()
    {
        string source = "```mermaid\r\nsequenceDiagram\r\n    participant A as Alice\r\n    A->>B: hello\r\n    Note over A: n\r\n    B-->>A: hi\r\n```\r\n";

        ImportResult result = Import(source);

        Assert.True(MermaidImporter.IsSequenceDiagram(source));
        Assert.Equal(["p_A", "p_B"], result.Document.Nodes.Select(n => n.Id));
        Assert.Equal(["hello", "n", "hi"], result.Document.Edges.Select(e => e.Label));
        Assert.Equal(["sync", "note", "return"], result.Document.Edges.Select(e => e.Kind));
        // The note imports, so nothing in the pasted text warns any more.
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_semicolon_ends_a_statement_but_an_entity_keeps_its_own_and_a_br_becomes_a_space()
    {
        ImportResult result = Import("sequenceDiagram\n A->>B: one; B->>A: two #59; still two\n A->>B: line<br/>break and #quot;quoted#quot;");

        Assert.Equal(["one", "two ; still two", "line break and \"quoted\""], result.Document.Edges.Select(e => e.Label));
    }

    [Fact]
    public void A_message_with_no_text_is_still_a_message_with_no_label_and_stray_semicolons_are_nothing()
    {
        ImportResult result = Import("sequenceDiagram\n A->>B:\n B->>A: back;;;;;\n ;\n ;;");

        Assert.Equal([null, "back"], result.Document.Edges.Select(e => e.Label));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void The_produced_document_is_schema_valid_has_no_geometry_and_reads_in_time_order()
    {
        ImportResult result = Import("sequenceDiagram\n actor U as User\n participant S as Service\n U->>+S: go\n S-->>-U: done\n U->>U: reflect");
        Document document = result.Document;

        SchemaCheck schema = ContractFiles.Evaluate(DocumentJson.Serialize(document));
        Assert.True(schema.IsValid, ContractFiles.DescribeErrors(schema));
        Assert.Equal(DocumentType.Sequence, document.Type);
        Assert.All(document.Nodes, n => Assert.Null(n.Geometry));
        Assert.All(document.Edges, e => Assert.Null(e.Routing));
        Assert.Equal(["m_1", "m_2", "m_3"], document.Edges.Select(e => e.Id));
        Assert.Equal(s_now, document.Meta.CreatedAt);
        Assert.Null(result.Direction);
        KindVocabulary vocabulary = KindVocabulary.For(DocumentType.Sequence);
        Assert.All(document.Nodes, n => Assert.True(vocabulary.IsKnownNodeKind(n.Kind)));
        Assert.All(document.Edges, e => Assert.True(vocabulary.IsKnownEdgeKind(e.Kind)));
    }

    [Fact]
    public void Import_then_print_then_parse_is_stable()
    {
        ImportResult result = Import("""
            sequenceDiagram
                title Checkout
                actor U as Customer
                participant W as Web app
                participant API
                U->>W: Place order
                W->>+API: POST /orders
                API->>API: validate()
                API--)W: order.created
                API-->>-W: 201 Created
                W->W: no head
                W-->U: dashed, no head
                W-->>U: Thanks
            """);

        string printed = Dsl.Print(result.Document);
        Document reparsed = Dsl.Parse(printed).Document;

        Assert.Contains("type sequence", printed, StringComparison.Ordinal);
        Assert.Equal(result.Document.Nodes.Select(n => (n.Id, n.Kind, n.Label)), reparsed.Nodes.Select(n => (n.Id, n.Kind, n.Label)));
        Assert.Equal(result.Document.Edges.Select(e => (e.Id, e.Kind, e.Label, e.From.Node, e.To.Node)), reparsed.Edges.Select(e => (e.Id, e.Kind, e.Label, e.From.Node, e.To.Node)));
        Assert.Equal(
            result.Document.Edges.Select(e => (e.Style?.Dash, e.Style?.ArrowStart, e.Style?.ArrowEnd, SequenceMessages.IsSet(e, "activate"), SequenceMessages.IsSet(e, "deactivate"))),
            reparsed.Edges.Select(e => (e.Style?.Dash, e.Style?.ArrowStart, e.Style?.ArrowEnd, SequenceMessages.IsSet(e, "activate"), SequenceMessages.IsSet(e, "deactivate"))));
        Assert.Equal(printed, Dsl.Print(reparsed));
        // The two "no head" messages really did survive as a style, not just an equal-looking default.
        Assert.Equal(ArrowHead.None, reparsed.Edges[5].Style?.ArrowEnd);
        Assert.Equal((Dash.Dashed, ArrowHead.None), (reparsed.Edges[6].Style?.Dash, reparsed.Edges[6].Style?.ArrowEnd));
    }

    [Theory]
    [InlineData("sequenceDiagram", true)]
    [InlineData("  \n%% c\nsequenceDiagram\n A->>B: x", true)]
    [InlineData("```mermaid\nsequenceDiagram\n A->>B: x\n```", true)]
    [InlineData("---\ntitle: T\n---\nsequenceDiagram\n A->>B: x", true)]
    [InlineData("sequenceDiagram;", true)]
    [InlineData("flowchart TD\n A --> B", false)]
    [InlineData("flowchart TD\n A[\"sequenceDiagram\"] --> B", false)]
    [InlineData("classDiagram\n A <|-- B", false)]
    [InlineData("", false)]
    public void The_header_alone_decides_whether_a_source_is_a_sequence_diagram(string source, bool expected)
    {
        Assert.Equal(expected, MermaidImporter.IsSequenceDiagram(source));
    }

    [Theory]
    [InlineData(DocumentType.Flowchart)]
    [InlineData(DocumentType.Architecture)]
    [InlineData(DocumentType.Freeform)]
    public void A_sequence_source_asked_for_as_another_type_is_refused_not_relabelled(DocumentType type)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => MermaidImporter.Import("sequenceDiagram\n A->>B: x", new MermaidImportOptions { Type = type }));

        Assert.Contains("sequenceDiagram", error.Message, StringComparison.Ordinal);
        Assert.Contains("'sequence'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flowchart_source_asked_for_as_a_sequence_is_refused_and_says_how_to_fix_it()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => MermaidImporter.Import("flowchart TD\n A --> B", new MermaidImportOptions { Type = DocumentType.Sequence }));

        Assert.Contains("not one", error.Message, StringComparison.Ordinal);
        Assert.Contains("flowchart", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flowchart_still_imports_as_a_flowchart_alongside_this()
    {
        ImportResult result = MermaidImporter.Import("flowchart TD\n A --> B", new MermaidImportOptions { Now = s_now });

        Assert.Equal(DocumentType.Flowchart, result.Document.Type);
        Assert.Equal(2, result.Document.Nodes.Count);
    }

    [Fact]
    public void A_source_with_a_header_and_nothing_else_is_an_empty_diagram_warning()
    {
        ImportResult result = Import("sequenceDiagram\n");

        Assert.Empty(result.Document.Nodes);
        Assert.Equal(ImportWarningCodes.EmptyDiagram, Assert.Single(result.Warnings).Code);
    }

    [Theory]
    [InlineData("sequenceDiagram\n A->>")]
    [InlineData("sequenceDiagram\n ->>B: x")]
    [InlineData("sequenceDiagram\n participant\n actor\n activate\n deactivate")]
    [InlineData("sequenceDiagram\n end\n end\n else\n and")]
    [InlineData("sequenceDiagram\n alt never closed\n A->>B: x")]
    [InlineData("sequenceDiagram\n :::\n >>>\n ---\n +++ \n ((")]
    public void Malformed_input_yields_a_valid_document_and_warnings_not_exceptions(string source)
    {
        ImportResult result = Import(source);

        Assert.NotEmpty(result.Warnings);
        SchemaCheck schema = ContractFiles.Evaluate(DocumentJson.Serialize(result.Document));
        Assert.True(schema.IsValid, ContractFiles.DescribeErrors(schema));
        var ids = result.Document.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.All(result.Document.Edges, e => Assert.Contains(e.From.Node, ids));
        Assert.All(result.Document.Edges, e => Assert.Contains(e.To.Node, ids));
    }
}
