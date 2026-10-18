using Draughtsman.Ai;
using Draughtsman.Ai.Prompting;
using Draughtsman.Core.Models;
using Draughtsman.Core.Storage;
using Draughtsman.Mcp.Tools;

namespace Draughtsman.Server.Ai;

/// <summary>
/// "Diagram to document" (build-spec-v0.1.md section 9, MVP AI feature 2; design/atlas/ai-panel.md): a diagram
/// becomes Markdown - a design doc, a runbook or a plain explanation - that cites the diagram's elements by id.
///
/// Privacy (build-spec-v0.1.md section 9): only <see cref="Outline.Render"/> - ids, kinds, labels, containers and
/// edge endpoints, exactly what <c>read_document format=outline</c> shows - goes to the model. Geometry, style,
/// props and any image are never sent. Reading is the whole job: nothing is written back to the document, so there
/// is no checkpoint and no aiProvenance to record.
/// </summary>
public sealed class DiagramToDocumentService
{
    private readonly IModelProvider? _provider;
    private readonly IStore _store;

    public DiagramToDocumentService(IStore store, IModelProvider? provider = null)
    {
        _store = store;
        _provider = provider;
    }

    public bool IsAvailable => _provider is not null;

    public async Task<DiagramToDocumentResult> GenerateAsync(DiagramToDocumentRequest request, CancellationToken cancellationToken)
    {
        if (_provider is null)
        {
            throw new InvalidOperationException("GenerateAsync was called while no AI provider is configured; callers must check IsAvailable first.");
        }

        if (string.IsNullOrWhiteSpace(request.DocumentId))
        {
            throw new DiagramToDocumentValidationException("documentId must not be empty.");
        }

        if (!DiagramToDocumentPrompt.TryParseKind(request.OutputKind, out DiagramOutputKind kind))
        {
            throw new DiagramToDocumentValidationException($"outputKind must be 'design-doc', 'runbook' or 'explanation'; got '{request.OutputKind}'.");
        }

        Document document = await _store.GetDocument(request.DocumentId, cancellationToken)
            ?? throw new DiagramToDocumentDocumentNotFoundException(request.DocumentId);

        string systemPrompt = DiagramToDocumentPrompt.BuildSystemPrompt(kind, Outline.Render(document));
        var messages = new List<ModelMessage> { new(ModelRole.User, "Write the document for this diagram.") };

        string markdown = await _provider.CompleteAsync(systemPrompt, messages, cancellationToken);
        return new DiagramToDocumentResult(markdown.Trim(), _provider.Provider, _provider.Model);
    }
}

public sealed record DiagramToDocumentRequest(string? DocumentId, string? OutputKind);

public sealed record DiagramToDocumentResult(string Markdown, string Provider, string Model);

/// <summary>The request was malformed: no documentId, or an outputKind the panel does not offer.</summary>
public sealed class DiagramToDocumentValidationException(string message) : Exception(message);

/// <summary>request.DocumentId named a document that does not exist.</summary>
public sealed class DiagramToDocumentDocumentNotFoundException(string documentId) : Exception($"No document with id '{documentId}'.")
{
    public string DocumentId { get; } = documentId;
}
