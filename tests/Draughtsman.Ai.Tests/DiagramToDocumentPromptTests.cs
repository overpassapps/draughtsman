using Draughtsman.Ai.Prompting;

namespace Draughtsman.Ai.Tests;

public class DiagramToDocumentPromptTests
{
    private const string Outline = "Checkout (flowchart)\nnodes:\n  n_cart [process] \"Cart\"\n  n_pay [service] \"Payment\"\nedges:\n  e_1: n_cart -> n_pay [flow]\n";

    [Fact]
    public void SystemPrompt_embeds_the_outline_verbatim()
    {
        string prompt = DiagramToDocumentPrompt.BuildSystemPrompt(DiagramOutputKind.DesignDoc, Outline);

        Assert.Contains(Outline, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPrompt_tells_the_model_to_cite_element_ids_in_brackets()
    {
        string prompt = DiagramToDocumentPrompt.BuildSystemPrompt(DiagramOutputKind.DesignDoc, Outline);

        // The web panel turns a bracketed id back into a click, so the exact form the prompt asks for and the form
        // the renderer looks for must be the same one.
        Assert.Contains(DiagramToDocumentPrompt.CitationRule, prompt, StringComparison.Ordinal);
        Assert.Contains("[n_checkout]", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemPrompt_differs_per_output_kind()
    {
        // Two-sided: three builders over the same outline must not collapse to one prompt, or the output choice
        // would be decorative.
        string design = DiagramToDocumentPrompt.BuildSystemPrompt(DiagramOutputKind.DesignDoc, Outline);
        string runbook = DiagramToDocumentPrompt.BuildSystemPrompt(DiagramOutputKind.Runbook, Outline);
        string explanation = DiagramToDocumentPrompt.BuildSystemPrompt(DiagramOutputKind.Explanation, Outline);

        Assert.NotEqual(design, runbook);
        Assert.NotEqual(runbook, explanation);
        Assert.NotEqual(design, explanation);
        Assert.Contains("design document", design, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runbook", runbook, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plain-language explanation", explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_kind_is_rejected_rather_than_defaulted()
    {
        Assert.False(DiagramToDocumentPrompt.TryParseKind("poem", out DiagramOutputKind kind));
        Assert.Equal(DiagramOutputKind.Unknown, kind);

        // Two-sided: a supported value in a different case and with surrounding space must still parse, so the
        // rejection above is about the value, not a brittle exact-match.
        Assert.True(DiagramToDocumentPrompt.TryParseKind("  Design-Doc ", out DiagramOutputKind parsed));
        Assert.Equal(DiagramOutputKind.DesignDoc, parsed);
        Assert.True(DiagramToDocumentPrompt.TryParseKind("Runbook", out _));
        Assert.True(DiagramToDocumentPrompt.TryParseKind("explanation", out _));
    }
}
