using System.Text.Json;
using System.Text.RegularExpressions;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;

namespace Draughtsman.Core.Import;

/// <summary>
/// Imports a Mermaid <c>sequenceDiagram</c> into a <c>sequence</c> document (docs/sequence-diagrams.md): participants become
/// <c>p_&lt;id&gt;</c> nodes in first-appearance order, messages become <c>m_1</c>, <c>m_2</c>… edges in source order (the order
/// of the edges is time), and no geometry or routing is produced, since the layout of a sequence is derived.
/// Combined fragments (<c>alt</c>/<c>else</c>, <c>opt</c>, <c>loop</c>, <c>par</c>/<c>and</c>, <c>critical</c>/<c>option</c>,
/// <c>break</c>) become <c>fragment</c> nodes whose messages carry <c>in</c> and <c>operand</c>; <c>Note over/left of/right of</c>
/// becomes a <c>note</c> row; <c>create</c> and <c>destroy</c> make the message that follows a <c>create</c> or <c>destroy</c> message.
/// A tolerant reader in the manner of the SQL importer: what it cannot carry (<c>autonumber</c>, <c>box</c>, <c>rect</c>, a
/// lost-message cross, a sender that is destroyed) is one <c>unsupported-syntax</c> warning per construct and is flattened, so
/// the messages inside a block still import in order; a line it cannot read is a <c>parse-error</c> warning. It never throws on input.
/// </summary>
internal static partial class MermaidSequenceImporter
{
    private const string DefaultTitle = "Imported diagram";
    private const string CreatedBy = "mermaid-import";

    /// <summary>Blocks that become a combined fragment, each with the operator it draws as.</summary>
    private static readonly string[] s_fragmentOpeners = { "loop", "alt", "opt", "par", "critical", "break" };

    /// <summary>Blocks that close with <c>end</c> but have no equivalent: reported once at the opener, and the block's contents import flat.</summary>
    private static readonly string[] s_flatBlockOpeners = { "rect", "box" };

    /// <summary>Lines that start the next operand of the enclosing fragment (<c>else</c>, <c>and</c>, <c>option</c>).</summary>
    private static readonly string[] s_blockContinuations = { "else", "and", "option" };

    /// <summary>What each message kind draws with by default (Schemas/sequence.json), to write only the style that deviates from it.</summary>
    private static readonly Dictionary<string, (Dash Dash, ArrowHead End)> s_kindLooks = new(StringComparer.Ordinal)
    {
        ["sync"] = (Dash.Solid, ArrowHead.Arrow),
        ["async"] = (Dash.Solid, ArrowHead.Open),
        ["return"] = (Dash.Dashed, ArrowHead.Open),
        ["create"] = (Dash.Dashed, ArrowHead.Open),
        ["destroy"] = (Dash.Solid, ArrowHead.Arrow),
    };

    /// <summary>Statements Mermaid accepts that this importer does not carry: reported once each, then skipped.</summary>
    private static readonly string[] s_unsupportedStatements = { "autonumber", "link", "links", "properties", "details" };

    /// <summary>Longest alternatives first, so <c>--&gt;&gt;</c> is not read as <c>--&gt;</c> then a stray <c>&gt;</c>.</summary>
    private static readonly (string Text, MermaidArrow Arrow)[] s_arrows =
    {
        ("<<-->>", new MermaidArrow("return", null, ArrowHead.Open, null, false)),
        ("<<->>", new MermaidArrow("sync", null, ArrowHead.Arrow, null, false)),
        ("-->>", new MermaidArrow("return", null, null, null, false)),
        ("->>", new MermaidArrow("sync", null, null, null, false)),
        ("--x", new MermaidArrow("sync", Dash.Dashed, null, null, true)),
        ("-x", new MermaidArrow("sync", null, null, null, true)),
        ("--)", new MermaidArrow("async", Dash.Dashed, null, null, false)),
        ("-)", new MermaidArrow("async", null, null, null, false)),
        ("-->", new MermaidArrow("sync", Dash.Dashed, null, ArrowHead.None, false)),
        ("->", new MermaidArrow("sync", null, null, ArrowHead.None, false)),
    };

    /// <summary>True when the first meaningful line, after a pasted code fence, front matter, blanks and comments, is the <c>sequenceDiagram</c> header.</summary>
    public static bool IsSequenceDiagram(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        string[] physical = Prepare(source, out _, out int start);
        for (int i = start; i < physical.Length; i++)
        {
            string line = physical[i].Trim();
            if (line.Length == 0 || IsComment(line))
            {
                continue;
            }

            return HeaderWord(line);
        }

        return false;
    }

    public static ImportResult Import(string source, MermaidImportOptions options)
    {
        var warnings = new List<ImportWarning>();
        var participants = new List<Participant>();
        var messages = new List<Message>();
        var fragments = new List<FragmentDef>();
        string? title = null;
        try
        {
            string[] physical = Prepare(source ?? string.Empty, out MermaidGraph front, out int start);
            title = front.Title;
            var state = new ParseState(participants, messages, fragments, warnings);
            ParseLines(physical, start, state, ref title);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or RegexMatchTimeoutException))
        {
            // Belt and braces, as in the flowchart parser: a bug here must not take the request down.
            warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"Import stopped early: {ex.Message}"));
        }

        var ids = new MermaidImporter.IdAllocator();
        var nodeIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodes = new List<Node>();
        foreach (Participant participant in participants)
        {
            string id = ids.Allocate("p_", participant.MermaidId, participant.Line, warnings);
            nodeIds[participant.MermaidId] = id;
            nodes.Add(participant.IsActor ? new Node(id, "actor", participant.Label, "rounded") : new Node(id, "participant", participant.Label, "rectangle"));
        }

        // Fragment nodes follow the participants (their place in nodes[] is not a column), in the order they first held a message.
        foreach (FragmentDef fragment in fragments)
        {
            var fragmentProps = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                [SequenceMessages.OperatorProp] = JsonSerializer.SerializeToElement(fragment.Operator),
                [SequenceMessages.OperandsProp] = JsonSerializer.SerializeToElement(fragment.Operands),
            };
            if (fragment.Parent is not null)
            {
                fragmentProps[SequenceMessages.Operand] = JsonSerializer.SerializeToElement(fragment.ParentOperand);
            }

            nodes.Add(new Node(fragment.Id, SequenceMessages.FragmentKind, fragment.Operator, "rectangle") { Parent = fragment.Parent?.Id, Props = fragmentProps });
        }

        var edges = new List<Edge>();
        int noteCount = 0;
        int messageCount = 0;
        foreach (Message message in messages)
        {
            string id = message.Kind == SequenceMessages.NoteKind ? $"n_{++noteCount}" : $"m_{++messageCount}";
            Dictionary<string, JsonElement>? props = null;
            void Set(string key, JsonElement value) => (props ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal))[key] = value;
            if (message.Activate)
            {
                Set("activate", JsonSerializer.SerializeToElement(true));
            }

            if (message.Deactivate)
            {
                Set("deactivate", JsonSerializer.SerializeToElement(true));
            }

            if (message.Fragment is { } fragment)
            {
                Set(SequenceMessages.In, JsonSerializer.SerializeToElement(fragment.Id));
                if (message.Operand != 0)
                {
                    Set(SequenceMessages.Operand, JsonSerializer.SerializeToElement(message.Operand));
                }
            }

            edges.Add(new Edge(id, message.Kind, new Endpoint(nodeIds[message.From]), new Endpoint(nodeIds[message.To]))
            {
                Label = string.IsNullOrEmpty(message.Label) ? null : message.Label,
                Style = message.Kind == SequenceMessages.NoteKind ? null : StyleFor(message),
                Props = props,
            });
        }

        if (nodes.Count == 0)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.EmptyDiagram, "The source produced no participants."));
        }

        DateTimeOffset now = options.Now ?? DateTimeOffset.UtcNow;
        var document = new Document(
            $"doc_{Guid.NewGuid():N}",
            Document.CurrentSchemaVersion,
            options.Title ?? title ?? DefaultTitle,
            DocumentType.Sequence,
            nodes,
            edges,
            new Meta(now, now, CreatedBy));
        return new ImportResult(document, warnings);
    }

    /// <summary>The source as physical lines (CRLF folded, a code fence blanked in place so line numbers still match, front matter skipped past).</summary>
    private static string[] Prepare(string source, out MermaidGraph front, out int start)
    {
        string[] physical = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        MermaidFlowchartParser.BlankFence(physical);
        front = new MermaidGraph();
        start = MermaidFlowchartParser.ReadFrontMatter(physical, front);
        return physical;
    }

    private static void ParseLines(string[] physical, int start, ParseState state, ref string? title)
    {
        bool headerSeen = false;
        for (int i = start; i < physical.Length; i++)
        {
            int lineNumber = i + 1;
            string trimmed = physical[i].Trim();
            if (trimmed.Length == 0 || IsComment(trimmed))
            {
                continue;
            }

            foreach (string raw in StatementSeparator().Split(trimmed))
            {
                string statement = raw.Trim();
                if (statement.Length == 0 || IsComment(statement))
                {
                    continue;
                }

                if (!headerSeen)
                {
                    headerSeen = true;
                    if (HeaderWord(statement))
                    {
                        continue;
                    }

                    // Only reachable when Import is called directly; the importer's dispatch checked the header already.
                    state.Warnings.Add(new ImportWarning(ImportWarningCodes.MissingHeader, "No 'sequenceDiagram' header found; assumed one.", lineNumber));
                }

                ParseStatement(statement, lineNumber, state, ref title);
            }
        }

        // A block still open at the end is closed there, as Mermaid's own renderer would have to.
        foreach (Block block in state.Blocks)
        {
            state.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"The '{block.Keyword}' block on line {block.Line} is never closed with 'end'; it ends with the diagram.", block.Line));
        }

        state.Blocks.Clear();
    }

    private static void ParseStatement(string statement, int line, ParseState state, ref string? title)
    {
        List<Participant> participants = state.Participants;
        List<ImportWarning> warnings = state.Warnings;
        Match word = FirstWord().Match(statement);
        string keyword = word.Success ? word.Value.ToLowerInvariant() : string.Empty;
        string rest = word.Success ? statement[word.Length..].Trim() : string.Empty;

        switch (keyword)
        {
            case "participant" or "actor":
                Declare(keyword == "actor", rest, line, participants, warnings);
                return;
            case "title":
                title = MermaidFlowchartParser.CleanLabel(rest.TrimStart(':')).Replace('\n', ' ');
                return;
            case "activate" or "deactivate":
                Toggle(keyword == "activate", rest, line, state.Messages, warnings);
                return;
            case "note":
                ReadNote(rest, statement, line, state);
                return;
            case "create":
                CreateParticipant(rest, line, state);
                return;
            case "destroy":
                state.PendingDestroy = new Pending(rest.Trim(), line);
                return;
            case "end":
                if (state.Blocks.Count == 0)
                {
                    warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, "'end' closes no block; it was skipped.", line));
                }
                else
                {
                    CloseBlock(state);
                }

                return;
        }

        if (Array.IndexOf(s_fragmentOpeners, keyword) >= 0)
        {
            var definition = new FragmentDef(keyword, GuardText(rest));
            state.Blocks.Add(new Block(keyword, line, definition));
            return;
        }

        if (Array.IndexOf(s_flatBlockOpeners, keyword) >= 0)
        {
            state.Blocks.Add(new Block(keyword, line, null));
            warnings.Add(Unsupported($"'{keyword}' blocks are not imported yet ('{Shorten(statement)}'); the messages inside are kept in order, without the frame.", line));
            return;
        }

        if (Array.IndexOf(s_blockContinuations, keyword) >= 0)
        {
            if (state.Blocks.Count == 0)
            {
                warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"'{keyword}' is outside any block; it was skipped.", line));
            }
            else if (state.Blocks[^1].Definition is { } definition)
            {
                // The next operand of the block: its guard text, and from now on its messages carry the new operand index.
                definition.Operands.Add(GuardText(rest));
                state.Blocks[^1].Operand = definition.Operands.Count - 1;
            }

            return;
        }

        if (Array.IndexOf(s_unsupportedStatements, keyword) >= 0)
        {
            warnings.Add(Unsupported($"'{keyword}' is not imported yet; it was skipped.", line));
            return;
        }

        if (!TryReadMessage(statement, line, state))
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"Could not read '{Shorten(statement)}'; the line was skipped.", line));
        }
    }

    /// <summary>
    /// A guard as stored: the bare text, without the square brackets the editor draws around it (a guard the author already
    /// bracketed loses them), or nothing for a block with no text.
    /// </summary>
    private static string GuardText(string text)
    {
        string cleaned = Flatten(MermaidFlowchartParser.CleanLabel(text.Trim().TrimStart(':').Trim()));
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        return cleaned.StartsWith('[') && cleaned.EndsWith(']') ? cleaned[1..^1].Trim() : cleaned;
    }

    /// <summary>
    /// The fragments the open blocks stand for, outermost first, created (given an id, and added to the document) the
    /// first time a row lands inside them so a block with no row in it leaves nothing behind. A nested fragment records
    /// the fragment it is in and which operand of it it sits under.
    /// </summary>
    private static (FragmentDef Fragment, int Operand)? Membership(ParseState state)
    {
        FragmentDef? parent = null;
        int parentOperand = 0;
        (FragmentDef, int)? innermost = null;
        foreach (Block block in state.Blocks)
        {
            if (block.Definition is not { } definition)
            {
                continue;
            }

            if (definition.Id.Length == 0)
            {
                definition.Id = $"f_{state.Fragments.Count + 1}";
                definition.Parent = parent;
                definition.ParentOperand = parentOperand;
                state.Fragments.Add(definition);
            }

            parent = definition;
            parentOperand = block.Operand;
            innermost = (definition, block.Operand);
        }

        return innermost;
    }

    private static void CloseBlock(ParseState state)
    {
        Block block = state.Blocks[^1];
        state.Blocks.RemoveAt(state.Blocks.Count - 1);
        if (block.Definition is { Id.Length: 0 })
        {
            state.Warnings.Add(Unsupported($"The '{block.Keyword}' block on line {block.Line} holds no message, so it was left out.", block.Line));
        }
    }

    /// <summary>
    /// <c>Note over A,B: text</c>, <c>Note left of A: text</c>, <c>Note right of A: text</c>. The model has one kind of note, a row
    /// across the lifelines from its first participant to its last, so left of and right of are drawn over the lifeline (warned).
    /// </summary>
    private static void ReadNote(string rest, string statement, int line, ParseState state)
    {
        Match match = NoteLine().Match(rest);
        if (!match.Success)
        {
            state.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"Could not read '{Shorten(statement)}'; the line was skipped.", line));
            return;
        }

        string[] who = match.Groups["who"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (who.Length == 0)
        {
            state.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"Could not read '{Shorten(statement)}'; the line was skipped.", line));
            return;
        }

        string position = Whitespace().Replace(match.Groups["pos"].Value.ToLowerInvariant(), " ");
        if (position != "over")
        {
            state.Warnings.Add(Unsupported($"'Note {position}' has no equivalent; '{Shorten(statement)}' is drawn as a note over the lifeline.", line));
        }

        foreach (string id in who)
        {
            Find(id, line, state.Participants);
        }

        string text = Flatten(MermaidFlowchartParser.CleanLabel(match.Groups["text"].Value));
        (FragmentDef Fragment, int Operand)? membership = Membership(state);
        state.Messages.Add(new Message(who[0], who[^1], null, text, SequenceMessages.NoteKind) { Fragment = membership?.Fragment, Operand = membership?.Operand ?? 0 });
    }

    /// <summary><c>participant A as Alice</c>, <c>actor B</c>, and the v11 <c>participant D@{ "type": "database" }</c> form (the type config is dropped).</summary>
    private static void Declare(bool isActor, string rest, int line, List<Participant> participants, List<ImportWarning> warnings)
    {
        Match match = Declaration().Match(rest);
        if (!match.Success)
        {
            warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"Could not read '{(isActor ? "actor" : "participant")} {Shorten(rest)}'; the line was skipped.", line));
            return;
        }

        string id = match.Groups["id"].Value.Trim();
        int config = id.IndexOf("@{", StringComparison.Ordinal);
        if (config >= 0)
        {
            id = id[..config].Trim();
            warnings.Add(Unsupported($"The participant type config on '{id}' is not imported; it is drawn as a plain {(isActor ? "actor" : "participant")}.", line));
        }

        string? alias = match.Groups["alias"].Success ? Flatten(MermaidFlowchartParser.CleanLabel(match.Groups["alias"].Value)) : null;
        Participant participant = Find(id, line, participants);
        participant.IsActor = isActor;
        if (!string.IsNullOrEmpty(alias))
        {
            participant.Label = alias;
        }
    }

    /// <summary>
    /// <c>create participant D as Dave</c> declares the participant and marks the next message as the one that creates it
    /// (<see cref="TryReadMessage"/> turns a message to it into a <c>create</c> message); <c>create actor D</c> likewise.
    /// </summary>
    private static void CreateParticipant(string rest, int line, ParseState state)
    {
        Match word = FirstWord().Match(rest);
        string keyword = word.Success ? word.Value.ToLowerInvariant() : string.Empty;
        if (keyword is not ("participant" or "actor"))
        {
            state.Warnings.Add(new ImportWarning(ImportWarningCodes.ParseError, $"Could not read 'create {Shorten(rest)}'; the line was skipped.", line));
            return;
        }

        string declaration = rest[word.Length..].Trim();
        Declare(keyword == "actor", declaration, line, state.Participants, state.Warnings);
        Match name = Declaration().Match(declaration);
        string id = name.Success ? name.Groups["id"].Value.Trim() : declaration;
        int config = id.IndexOf("@{", StringComparison.Ordinal);
        state.PendingCreate = new Pending(config >= 0 ? id[..config].Trim() : id, line);
    }

    /// <summary>
    /// A standalone <c>activate X</c> / <c>deactivate X</c> attaches to the message just before it when that message
    /// starts the bar on X (it goes to X) or ends X's bar (it comes from X), the only shape the model can say; anything
    /// else is a warning.
    /// </summary>
    private static void Toggle(bool activate, string rest, int line, List<Message> messages, List<ImportWarning> warnings)
    {
        string id = rest.Trim();
        Message? previous = messages.Count > 0 && messages[^1].Kind != SequenceMessages.NoteKind ? messages[^1] : null;
        if (previous is not null && activate && previous.To == id)
        {
            previous.Activate = true;
        }
        else if (previous is not null && !activate && previous.From == id)
        {
            previous.Deactivate = true;
        }
        else
        {
            string side = activate ? "the message just before it must go to" : "the message just before it must come from";
            warnings.Add(Unsupported($"'{(activate ? "activate" : "deactivate")} {Shorten(id)}' could not be attached to a message ({side} '{Shorten(id)}'); it was skipped. Use '+' or '-' on the arrow.", line));
        }
    }

    private static bool TryReadMessage(string statement, int line, ParseState state)
    {
        Match match = MessageLine().Match(statement);
        if (!match.Success)
        {
            return false;
        }

        string arrowText = match.Groups["arrow"].Value;
        MermaidArrow arrow = Array.Find(s_arrows, a => a.Text == arrowText).Arrow;
        string from = match.Groups["from"].Value.Trim();
        string to = match.Groups["to"].Value.Trim();
        if (from.Length == 0 || to.Length == 0)
        {
            return false;
        }

        // The message a `create X` or `destroy X` statement was waiting for: one that goes to X. Whatever the next message
        // is, it settles both statements; one that did not fit is warned.
        string kind = arrow.Kind;
        Pending? creating = state.PendingCreate;
        Pending? destroying = state.PendingDestroy;
        state.PendingCreate = null;
        state.PendingDestroy = null;
        if (creating is not null)
        {
            if (creating.Id == to && from != to)
            {
                kind = SequenceMessages.CreateKind;
            }
            else
            {
                state.Warnings.Add(Unsupported($"'create {Shorten(creating.Id)}' is not followed by a message to it, so no creation message is drawn; the participant is drawn from the top.", creating.Line));
            }
        }

        if (destroying is not null)
        {
            if (destroying.Id == to)
            {
                kind = SequenceMessages.DestroyKind;
            }
            else
            {
                string why = destroying.Id == from ? "the model ends the lifeline of the receiver of a message, not its sender" : "it is not followed by a message to it";
                state.Warnings.Add(Unsupported($"'destroy {Shorten(destroying.Id)}' is not drawn: {why}; the lifeline runs to the bottom.", destroying.Line));
            }
        }

        // The cross on `Alice-xCarl` is how Mermaid writes the message that destroys Carl, which the destroy message draws.
        if (arrow.Lost && kind != SequenceMessages.DestroyKind)
        {
            state.Warnings.Add(Unsupported($"The lost-message cross ('{arrowText}') has no equivalent yet; '{Shorten(statement)}' is drawn as a plain sync message.", line));
        }

        Find(from, line, state.Participants);
        Find(to, line, state.Participants);
        string sign = match.Groups["mark"].Value;
        string label = match.Groups["text"].Success ? Flatten(MermaidFlowchartParser.CleanLabel(match.Groups["text"].Value)) : string.Empty;
        (FragmentDef Fragment, int Operand)? membership = Membership(state);
        state.Messages.Add(new Message(from, to, arrow, label, kind) { Activate = sign == "+", Deactivate = sign == "-", Fragment = membership?.Fragment, Operand = membership?.Operand ?? 0 });
        return true;
    }

    /// <summary>
    /// Only the style that differs from the message kind's default is written. A `sync`, `async` or `return` message reports
    /// the arrow's own deviation (the mapping table's); a create or destroy message compares the arrow's effective look with the
    /// look its kind draws by default, so `Alice->>Bob` that creates Bob stays solid instead of turning dashed.
    /// </summary>
    private static EdgeStyle? StyleFor(Message message)
    {
        MermaidArrow arrow = message.Arrow!;
        if (message.Kind == arrow.Kind)
        {
            return arrow.Dash is null && arrow.Start is null && arrow.End is null ? null : new EdgeStyle { Dash = arrow.Dash, ArrowStart = arrow.Start, ArrowEnd = arrow.End };
        }

        (Dash dash, ArrowHead end) = s_kindLooks[arrow.Kind];
        Dash effectiveDash = arrow.Dash ?? dash;
        ArrowHead effectiveEnd = arrow.End ?? end;
        (Dash kindDash, ArrowHead kindEnd) = s_kindLooks[message.Kind];
        Dash? styleDash = effectiveDash == kindDash ? null : effectiveDash;
        ArrowHead? styleEnd = effectiveEnd == kindEnd ? null : effectiveEnd;
        return styleDash is null && arrow.Start is null && styleEnd is null ? null : new EdgeStyle { Dash = styleDash, ArrowStart = arrow.Start, ArrowEnd = styleEnd };
    }

    private static Participant Find(string mermaidId, int line, List<Participant> participants)
    {
        Participant? existing = participants.Find(p => p.MermaidId == mermaidId);
        if (existing is not null)
        {
            return existing;
        }

        var created = new Participant(mermaidId, line);
        participants.Add(created);
        return created;
    }

    private static bool IsComment(string trimmed)
    {
        return trimmed.StartsWith("%%", StringComparison.Ordinal);
    }

    private static bool HeaderWord(string line)
    {
        return string.Equals(line.TrimEnd(';').Trim(), "sequenceDiagram", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A label is one line: the derived layout gives a message a single row, so a <c>&lt;br/&gt;</c> becomes a space.</summary>
    private static string Flatten(string label)
    {
        return string.Join(' ', label.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string Shorten(string text)
    {
        return text.Length <= 60 ? text : text[..57] + "...";
    }

    private static ImportWarning Unsupported(string message, int line)
    {
        return new ImportWarning(ImportWarningCodes.UnsupportedSyntax, message, line);
    }

    private sealed class Participant
    {
        public Participant(string mermaidId, int line)
        {
            MermaidId = mermaidId;
            Line = line;
            Label = mermaidId;
        }

        public string MermaidId { get; }

        public int Line { get; }

        public string Label { get; set; }

        public bool IsActor { get; set; }
    }

    private sealed class Message
    {
        public Message(string from, string to, MermaidArrow? arrow, string label, string kind)
        {
            From = from;
            To = to;
            Arrow = arrow;
            Label = label;
            Kind = kind;
        }

        public string From { get; }

        public string To { get; }

        /// <summary>Null for a note.</summary>
        public MermaidArrow? Arrow { get; }

        public string Label { get; }

        /// <summary>The edge kind it becomes: the arrow's, or <c>create</c>, <c>destroy</c> or <c>note</c>.</summary>
        public string Kind { get; }

        public bool Activate { get; set; }

        public bool Deactivate { get; set; }

        /// <summary>The fragment it sits in (the innermost open one) and the operand of it, or null outside every fragment.</summary>
        public FragmentDef? Fragment { get; set; }

        public int Operand { get; set; }
    }

    /// <summary>A block that has been opened and not yet closed with <c>end</c>; <see cref="Definition"/> is null for the kinds that draw no frame.</summary>
    private sealed class Block
    {
        public Block(string keyword, int line, FragmentDef? definition)
        {
            Keyword = keyword;
            Line = line;
            Definition = definition;
        }

        public string Keyword { get; }

        public int Line { get; }

        public FragmentDef? Definition { get; }

        /// <summary>The operand messages under it carry now: 0 until an <c>else</c>, <c>and</c> or <c>option</c>.</summary>
        public int Operand { get; set; }
    }

    /// <summary>A fragment in the making: the operator, the guards so far, and (once a message has landed in it) its id and what it is nested in.</summary>
    private sealed class FragmentDef
    {
        public FragmentDef(string @operator, string firstGuard)
        {
            Operator = @operator;
            Operands.Add(firstGuard);
        }

        public string Operator { get; }

        public List<string> Operands { get; } = new();

        /// <summary>Empty until a row lands inside it.</summary>
        public string Id { get; set; } = string.Empty;

        public FragmentDef? Parent { get; set; }

        public int ParentOperand { get; set; }
    }

    private sealed record Pending(string Id, int Line);

    /// <summary>Everything one parse accumulates.</summary>
    private sealed class ParseState
    {
        public ParseState(List<Participant> participants, List<Message> messages, List<FragmentDef> fragments, List<ImportWarning> warnings)
        {
            Participants = participants;
            Messages = messages;
            Fragments = fragments;
            Warnings = warnings;
        }

        public List<Participant> Participants { get; }

        public List<Message> Messages { get; }

        public List<FragmentDef> Fragments { get; }

        public List<ImportWarning> Warnings { get; }

        public List<Block> Blocks { get; } = new();

        public Pending? PendingCreate { get; set; }

        public Pending? PendingDestroy { get; set; }
    }

    /// <summary>What an arrow means in the model: the message kind plus only the style that deviates from that kind's default.</summary>
    private sealed record MermaidArrow(string Kind, Dash? Dash, ArrowHead? Start, ArrowHead? End, bool Lost);

    // A ';' ends a statement in Mermaid, except inside a "#59;" style entity.
    [GeneratedRegex(@"(?<!#[A-Za-z0-9]+);", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex StatementSeparator();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[A-Za-z]+(?=[\s:]|$)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: RegexLimits.TimeoutMilliseconds)]
    private static partial Regex FirstWord();

    internal const string DeclarationPattern = @"^(?<id>.+?)(?:\s+as\s+(?<alias>.+))?$";

    private static readonly Regex s_declaration = RegexLimits.Linear(DeclarationPattern, RegexOptions.IgnoreCase);

    private static Regex Declaration() => s_declaration;

    // "over A,B: text", "left of A: text", "right of A: text" (the keyword `Note` is already consumed).
    internal const string NoteLinePattern = @"^(?<pos>over|left\s+of|right\s+of)\s+(?<who>[^:]+?)\s*:(?<text>.*)$";

    private static readonly Regex s_noteLine = RegexLimits.Linear(NoteLinePattern, RegexOptions.IgnoreCase);

    private static Regex NoteLine() => s_noteLine;

    // from, arrow, optional +/- activation mark, to, optional ": text". The lazy "from" stops at the first arrow, so an id
    // with a hyphen ("Web-App") still reads; neither side may contain a colon, which starts the text.
    //
    // These three are built non-backtracking (AU-08): the lazy group followed by \s* made a long run of spaces cost time
    // quadratic in its length (32,000 spaces took 11 s, a 1 MB body would have pinned a thread for hours), and each has an
    // equivalent pattern the linear engine reads identically (MermaidSequenceRegexTests replays a random corpus through both).
    internal const string MessageLinePattern = @"^(?<from>[^:]+?)\s*(?<arrow><<-->>|<<->>|-->>|->>|--x|-x|--\)|-\)|-->|->)(?<mark>[+-])?\s*(?<to>[^:]+?)\s*(?::(?<text>.*))?$";

    private static readonly Regex s_messageLine = RegexLimits.Linear(MessageLinePattern);

    private static Regex MessageLine() => s_messageLine;
}
