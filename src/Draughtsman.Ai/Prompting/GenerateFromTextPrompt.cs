using System.Text.RegularExpressions;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Text;

namespace Draughtsman.Ai.Prompting;

/// <summary>
/// Builds the prompts for "Generate from text" (build-spec-v0.1.md section 9, MVP AI feature 1): a description
/// becomes Draughtsman DSL. Built from the same sources <c>get_schema</c> hands an agent - the kind vocabulary for
/// the requested document type and the DSL grammar - so a person's prompt and an agent's prompt teach the model the
/// same format, plus a short cheat-sheet of the forms models most often get wrong (unquoted multi-word text is the
/// single biggest cause of a failed parse in practice).
/// </summary>
public static partial class GenerateFromTextPrompt
{
    /// <summary>
    /// Concrete, correct examples of the lines a model most often gets wrong: an unquoted multi-word value (the
    /// single most common cause of a failed parse - a model writes a label or kind as a bare word even though it has
    /// a space in it), containers and parent, an edge's label, and props. Kept short and exact rather than
    /// exhaustive; the full grammar above already covers every form.
    /// </summary>
    private const string Examples = """

        Examples of forms that are easy to get wrong - copy this style exactly:
          node n_login process "Log in"                          # a single bare word never needs quotes...
          node n_router off-page-connector "App Router"           # ...but ANY text with a space or punctuation MUST be double-quoted, even a kind or a label
          container c_frontend "Frontend" { n_login n_router }    # a container's children are node ids inside { }, space-separated, no commas, no quotes
          node n_login process "Log in" parent=c_frontend          # or give an already-declared node a container with parent=<container-id>
          edge e_1 flow n_login -> n_router "starts"               # an edge's label is a quoted string straight after the two endpoints, before any key=value
          node n_service process "Payment service" tier="backend" # a prop is key="quoted value" for text, or key=123 / key=true / key=null for other JSON
        """;

    /// <summary>
    /// The cheat-sheet for a <c>sequence</c> document, which the flowchart one above would mislead (its kinds are not in the
    /// vocabulary): the two orders that carry the meaning, what a message's props do, and the quoting rule for a string value.
    /// </summary>
    private const string SequenceExamples = """

        Examples of forms that are easy to get wrong - copy this style exactly:
          node p_user actor "Customer"                                      # nodes are participants (lifelines); the ORDER OF THE NODE LINES is left to right
          node p_api participant "Orders API"                               # ANY text with a space or punctuation MUST be double-quoted, even a label
          edge m_1 sync p_user -> p_api "POST /orders" activate=true        # messages are edges; the ORDER OF THE EDGE LINES IS TIME, top to bottom, so write them as they happen
          edge m_2 sync p_api -> p_api "validate()"                         # a message from a participant to itself is a self message
          edge m_3 async p_api -> p_user "order.created"                    # async is fire-and-forget (an event); sync waits for a reply
          edge m_4 return p_api -> p_user "201 Created" deactivate=true     # a reply goes from the responder back to the caller; deactivate=true closes the bar activate=true opened
          node f_1 fragment "alt" operator="alt" operands=["card ok","declined"]   # a combined fragment (alt, opt, loop, par): a node of kind fragment, never a lifeline
          edge m_5 sync p_api -> p_user "charged" in="f_1" operand=0        # membership is written on each MESSAGE: in="<fragment id>" and operand=<index of its guard>; keep a fragment's messages next to each other
          edge n_1 note p_user -> p_api "Idempotent by key"                 # a note is an edge of kind note: a row of text across the participants from its from to its to
          edge m_6 create p_api -> p_mail "new Mailer()"                    # create makes the target's header appear at that row; destroy ends its lifeline with an X
        Never write "@ x,y", style or routing in a sequence document: positions are derived from the two orders. A string prop is key="quoted"; activate=true and deactivate=true are booleans, never quoted.
        """;

    /// <summary>The system prompt for a first attempt at a brand-new document of the given type.</summary>
    public static string BuildSystemPrompt(DocumentType type)
    {
        return $"""
            You write Draughtsman DSL: a small text format for diagrams. Reply with DSL only - no prose, no
            markdown code fences, no explanation before or after it. Every reply is a complete, standalone DSL
            document (starting with the magic line below), never a fragment.
            {CommonRules(type)}
            - Prefer a handful of well-labelled nodes and edges over an exhaustive one; this is a first draft the
              person can edit.
            """;
    }

    /// <summary>
    /// The system prompt for updating an already-open document (build-spec-v0.1.md section 9): <paramref
    /// name="existingDsl"/> is the target's exact current DSL (<see cref="Dsl.Print"/>) - the whole document,
    /// geometry and style included, not the privacy-trimmed outline the old add-only prompt sent, because
    /// restructuring a diagram (grouping into containers, relabelling, reconnecting) needs to see and keep what is
    /// already there. The model replies with the complete updated document; the caller diffs that reply against the
    /// document <paramref name="existingDsl"/> was printed from and applies the result as addressable ops, so an id
    /// kept unchanged really does keep its stored geometry, style and props untouched.
    /// </summary>
    public static string BuildUpdateSystemPrompt(DocumentType type, string existingDsl)
    {
        ArgumentNullException.ThrowIfNull(existingDsl);
        return $$"""
            You write Draughtsman DSL: a small text format for diagrams. You are updating an existing diagram, not
            starting one. Reply with DSL only - no prose, no markdown code fences, no explanation before or after
            it. Your reply is the COMPLETE updated document (starting with the magic line below), not a fragment of
            just the changes.
            {{CommonRules(type)}}
            - Copy every line for a node, container or edge that should stay exactly as it is now character for
              character, including its "@ x,y" position if it has one - do not renumber, reformat, reposition or
              drop anything the request below does not ask you to change.
            - Reuse the existing id for anything that stays, even if you relabel, restyle or reparent it. Give a
              brand-new id, not used below, only to a genuinely new node or edge.
            - To group existing nodes under a new container, add a new "container" line listing their ids in its
              { } braces (or add parent=<container-id> to each grouped node's own line instead) - either way, do
              not write "@ x,y" for the new container itself; its box is fitted to its children automatically.
              Do not write "@ x,y" for any other brand-new node either; leave its position for layout to decide.
            - Only remove a node or edge the request below actually asks you to remove; when in doubt, keep it.

            This diagram's current content:
            {{existingDsl}}
            """;
    }

    /// <summary>Grammar, kind vocabulary and the cheat-sheet examples: identical for both prompts above.</summary>
    private static string CommonRules(DocumentType type)
    {
        string kindVocabulary = KindVocabulary.TryGet(type, out _) ? KindVocabulary.ReadRawJson(type) : "{}";
        return $"""

            The grammar (EBNF):
            {DslGrammar.RawText}
            {(type == DocumentType.Sequence ? SequenceExamples : Examples)}

            The document type is "{type.ToString().ToLowerInvariant()}". Its allowed node and edge kinds, and the
            conventions for their props, are this JSON vocabulary:
            {kindVocabulary}

            Rules:
            - The first line must be "draughtsman 1".
            - Write a "type {type.ToString().ToLowerInvariant()}" header line.
            - Use only the node and edge kinds in the vocabulary above; invent a label, not a kind.
            - {(type == DocumentType.Sequence
                ? "Name participants p_<name> (short, lower-case, from the label) and messages m_1, m_2, ... in time order, and write every edge line in the order the messages happen: the first edge is the first message. To insert a message in the middle of an existing diagram, write its edge line between its neighbours and keep every other edge line in its existing order."
                : "Give every node and edge a short, stable, lower-kebab-case id derived from its label.")}
            """;
    }

    /// <summary>
    /// The retry prompt after a first attempt failed to parse: the parser's own error, the exact offending line with
    /// a caret at the column, a concrete fix for that line where one can be derived from the error text, and the
    /// full failed attempt for context.
    /// </summary>
    public static string BuildRetryUserMessage(string firstAttempt, DslParseException error)
    {
        ArgumentNullException.ThrowIfNull(firstAttempt);
        ArgumentNullException.ThrowIfNull(error);
        string offendingLine = OffendingLine(firstAttempt, error.Line);
        string caret = new string(' ', Math.Max(0, error.Column - 1)) + "^";
        return $"""
            That did not parse. Line {error.Line}, column {error.Column}: {error.Detail}

            The offending line was:
            {offendingLine}
            {caret}

            {ConcreteFix(error.Detail)}

            Your full attempt was:
            {firstAttempt}

            Reply again with the complete corrected DSL only, following the same grammar, rules and examples as
            before.
            """;
    }

    private static string OffendingLine(string text, int line)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return line >= 1 && line <= lines.Length ? lines[line - 1] : "(that line number is not in your attempt)";
    }

    /// <summary>
    /// Most parse failures are one bare, unquoted, multi-word value (a label, kind or prop value written without
    /// double quotes) - and the parser's own error message names that value in single quotes, e.g. "expected '='
    /// after 'App Router'". When the last quoted token in the error contains a space, name it and show the exact
    /// quoted fix; otherwise fall back to pointing at the grammar and examples above.
    /// </summary>
    private static string ConcreteFix(string detail)
    {
        MatchCollection matches = QuotedToken().Matches(detail);
        string? candidate = matches.Count > 0 ? matches[^1].Groups[1].Value : null;
        if (candidate is { Length: > 0 } text && text.Contains(' ', StringComparison.Ordinal))
        {
            return $"""Fix: '{text}' contains a space, so it must be written as a quoted string: "{text}". Wrap any label, kind, prop value or style value that has a space or punctuation in double quotes, like the examples above.""";
        }

        return "Fix: compare the line above against the grammar and the examples above - the most common cause is a multi-word value written without double quotes.";
    }

    [GeneratedRegex("'([^']*)'", RegexOptions.None, Draughtsman.Core.RegexLimits.TimeoutMilliseconds)]
    private static partial Regex QuotedToken();
}
