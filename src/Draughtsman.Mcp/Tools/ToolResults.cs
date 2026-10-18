using System.Text.Json;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Diffing;
using Draughtsman.Core.Models;

namespace Draughtsman.Mcp.Tools;

/// <summary>Typed tool results, so a caller (and a test) gets a documented shape rather than an anonymous object.</summary>
public sealed record DocumentListing(string Id, string Title, string Type, string UpdatedAt);

public sealed record ListDocumentsResult(IReadOnlyList<DocumentListing> Items, int Total, int Offset, int Limit);

public sealed record CreateDocumentResult(string Id, IReadOnlyList<string> Warnings);

public sealed record PatchDocumentResult(Document Document, string CheckpointVersionId, IReadOnlyList<string> Warnings);

public sealed record RenderDocumentResult(string Svg, string Pdf, IReadOnlyList<string> Warnings);

public sealed record DiffDocumentsResult(string FromVersion, string ToVersion, DocumentDiffResult Diff);

public sealed record CritiqueDocumentResult(IReadOnlyList<CritiqueFinding> Findings);

public sealed record GetSchemaResult(JsonElement DocumentSchema, JsonElement? KindVocabulary, JsonElement? GenericIcons, JsonElement? StateShapes, JsonElement? BpmnShapes, JsonElement? UmlShapes, JsonElement? ErShapes, JsonElement? BasicShapes);
