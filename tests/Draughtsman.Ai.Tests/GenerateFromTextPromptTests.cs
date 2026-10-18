using Draughtsman.Ai.Prompting;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Text;

namespace Draughtsman.Ai.Tests;

public class GenerateFromTextPromptTests
{
    [Fact]
    public void SystemPrompt_names_the_requested_document_type()
    {
        string prompt = GenerateFromTextPrompt.BuildSystemPrompt(DocumentType.Flowchart);

        Assert.Contains("document type is \"flowchart\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPrompt_differs_per_document_type_because_the_kind_vocabulary_differs()
    {
        // Two-sided: same builder, different input, must not collapse to the same prompt (a fixture built from one
        // constant would pass even if the type argument were ignored entirely).
        string flowchart = GenerateFromTextPrompt.BuildSystemPrompt(DocumentType.Flowchart);
        string architecture = GenerateFromTextPrompt.BuildSystemPrompt(DocumentType.Architecture);

        Assert.NotEqual(flowchart, architecture);
    }

    [Fact]
    public void SystemPrompt_embeds_the_real_dsl_grammar_text()
    {
        string prompt = GenerateFromTextPrompt.BuildSystemPrompt(DocumentType.Flowchart);

        Assert.Contains(DslGrammar.RawText, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPrompt_carries_a_quoted_multiword_example_because_that_is_the_most_common_parse_failure()
    {
        string prompt = GenerateFromTextPrompt.BuildSystemPrompt(DocumentType.Flowchart);

        Assert.Contains("\"App Router\"", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateSystemPrompt_embeds_the_existing_document_verbatim()
    {
        const string existingDsl = "draughtsman 1\ntype flowchart\nnode n_existing process \"Existing node\"\n";

        string prompt = GenerateFromTextPrompt.BuildUpdateSystemPrompt(DocumentType.Flowchart, existingDsl);

        // Two-sided with the fresh-document prompt: this one must actually contain the document's real content
        // (including a field the outline form used to drop, so the two paths cannot be confused for each other).
        Assert.Contains(existingDsl, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(existingDsl, GenerateFromTextPrompt.BuildSystemPrompt(DocumentType.Flowchart), StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateSystemPrompt_tells_the_model_to_reply_with_the_complete_document_and_keep_unchanged_ids()
    {
        string prompt = GenerateFromTextPrompt.BuildUpdateSystemPrompt(DocumentType.Flowchart, "draughtsman 1\ntype flowchart\n");

        Assert.Contains("COMPLETE updated document", prompt, StringComparison.Ordinal);
        Assert.Contains("Reuse the existing id", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateSystemPrompt_explains_how_to_group_nodes_into_a_new_container()
    {
        string prompt = GenerateFromTextPrompt.BuildUpdateSystemPrompt(DocumentType.Flowchart, "draughtsman 1\ntype flowchart\n");

        Assert.Contains("container", prompt, StringComparison.Ordinal);
        Assert.Contains("parent=<container-id>", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryMessage_carries_the_parser_line_and_column_and_the_failed_attempt()
    {
        DslParseException error = MakeParseException("not a magic line\nunexpected token here");

        string retry = GenerateFromTextPrompt.BuildRetryUserMessage("not a magic line\nunexpected token here", error);

        Assert.Contains(error.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), retry, StringComparison.Ordinal);
        Assert.Contains(error.Column.ToString(System.Globalization.CultureInfo.InvariantCulture), retry, StringComparison.Ordinal);
        Assert.Contains(error.Detail, retry, StringComparison.Ordinal);
        Assert.Contains("unexpected token here", retry, StringComparison.Ordinal);
    }

    // Reproduces the maintainer's exact reported failure ("expected '=' after 'App Router'"): a model that writes a
    // second quoted string after a node's label, with nothing after it, hits DslParser's dangling-quoted-key path.
    private const string UnquotedMultiwordAttempt = "draughtsman 1\ntype flowchart\nnode n_a process \"A\"\nnode n_b process \"B\"\nnode n_bad process \"Label\" \"App Router\"\n";

    [Fact]
    public void RetryMessage_quotes_the_exact_offending_line_not_just_the_whole_attempt()
    {
        DslParseException error = MakeParseException(UnquotedMultiwordAttempt);

        string retry = GenerateFromTextPrompt.BuildRetryUserMessage(UnquotedMultiwordAttempt, error);

        // Two-sided: the offending line, and only it, must be singled out - the assertion would also pass for a
        // message that merely echoed the whole attempt back if we did not check line 5 is called out on its own.
        Assert.Equal(5, error.Line);
        Assert.Contains("expected '=' after 'App Router'", error.Message, StringComparison.Ordinal);
        Assert.Contains("The offending line was:\nnode n_bad process \"Label\" \"App Router\"", retry, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryMessage_names_the_unquoted_multiword_value_and_shows_the_quoted_fix()
    {
        DslParseException error = MakeParseException(UnquotedMultiwordAttempt);

        string retry = GenerateFromTextPrompt.BuildRetryUserMessage(UnquotedMultiwordAttempt, error);

        Assert.Contains("must be written as a quoted string: \"App Router\"", retry, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryMessage_falls_back_to_a_generic_fix_when_the_error_names_no_multiword_value()
    {
        // "unknown document type 'bogus'; expected one of ..." quotes a single word, not a multi-word phrase.
        const string attempt = "draughtsman 1\ntype bogus\n";
        DslParseException error = MakeParseException(attempt);

        string retry = GenerateFromTextPrompt.BuildRetryUserMessage(attempt, error);

        // Two-sided with the targeted-fix test above: an error with nothing multi-word to blame must not fabricate a
        // fix instruction naming a value that was never in the error.
        Assert.DoesNotContain("must be written as a quoted string", retry, StringComparison.Ordinal);
        Assert.Contains("Fix:", retry, StringComparison.Ordinal);
    }

    private static DslParseException MakeParseException(string text)
    {
        try
        {
            // A genuine DslParseException from the real parser, rather than a hand-built one whose Line/Column/Detail
            // could drift from what the parser actually reports.
            Dsl.Parse(text);
            throw new InvalidOperationException("Expected Dsl.Parse to throw for malformed input.");
        }
        catch (DslParseException e)
        {
            return e;
        }
    }
}
