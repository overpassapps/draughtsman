namespace Draughtsman.Core.Critique;

/// <summary>How serious a finding is, in the order a reviewer triages them.</summary>
public enum FindingSeverity
{
    Error,
    Warning,
}

/// <summary>
/// One structural defect the critique found: a stable rule id, a severity, a one-sentence message and the node and
/// edge ids that are its evidence. Every evidence id is an element that actually exists in the document, so an agent
/// can follow it straight back to the graph the same way <c>patch_document</c> addresses elements by id. A referenced
/// element that no longer exists is named in the message, not listed as evidence, so evidence is never a dangling id.
/// </summary>
public sealed record CritiqueFinding(
    string Rule,
    FindingSeverity Severity,
    string Message,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<string> EdgeIds);
