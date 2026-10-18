using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Draughtsman.Core.Auth;
using Draughtsman.Core.Critique;
using Draughtsman.Core.Diffing;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Drawio;
using Draughtsman.Core.Import.Sql;
using Draughtsman.Core.Import.Visio;
using Draughtsman.Core.Models;
using Draughtsman.Core.Schemas;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Storage;
using Draughtsman.Core.Text;
using Draughtsman.Mcp.Patching;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Sidecar;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Draughtsman.Mcp.Tools;

/// <summary>
/// The MCP tools over the document store and the sidecar: read/create/patch/render/diff/get_schema, matching the
/// names build-spec-v0.1.md section 9 gives them. One instance per call (constructor DI), so <see cref="IStore"/>'s
/// scoped lifetime is respected the same way the REST endpoints get it. Every write is checkpointed or audited the
/// way DocumentEndpoints does it. The optional <c>actor</c> parameter (the MCP equivalent of the X-Draughtsman-Actor
/// header, since MCP calls carry no such header of their own) is resolved by <see cref="ResolveActor"/>: fully
/// self-reported over stdio, where there is no identity to check it against, but only an agent-name annotation on
/// top of the authenticated caller over HTTP - see <see cref="IActorContext"/> (security review F2).
/// </summary>
[McpServerToolType]
public sealed class DraughtsmanTools
{
    private static readonly Actor DefaultActor = new(ActorKind.Agent, "mcp-client", "unknown");
    private const int MaxActorNameLength = 256;
    private const int MaxActorModelLength = 256;
    private const int MaxActorWireLength = 512;

    private readonly IStore _store;
    private readonly ISidecarClient _sidecar;
    private readonly IPdfRenderer _pdf;
    private readonly IAssetReader _assets;
    private readonly IActorContext _actorContext;
    private readonly IDiagramThemeProvider? _themes;

    public DraughtsmanTools(IStore store, ISidecarClient sidecar, IPdfRenderer pdf, IAssetReader assets, IActorContext actorContext, IDiagramThemeProvider? themes = null)
    {
        _themes = themes;
        _store = store;
        _sidecar = sidecar;
        _pdf = pdf;
        _assets = assets;
        _actorContext = actorContext;
    }

    [McpServerTool(Name = "list_documents"), Description("Lists documents, newest updated first. Paged.")]
    public async Task<ListDocumentsResult> ListDocuments(
        [Description("Case-insensitive substring match on the title. Omit to list every document.")] string? query = null,
        [Description("Workspace to list. Only 'default' exists until workspaces land.")] string? workspace = null,
        [Description("Rows to skip.")] int offset = 0,
        [Description("Rows to return, 1 to 200.")] int limit = DocumentQuery.DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0)
        {
            throw new McpException($"offset must be non-negative; got {offset}.");
        }

        if (limit < 1 || limit > DocumentQuery.MaxLimit)
        {
            throw new McpException($"limit must be between 1 and {DocumentQuery.MaxLimit}; got {limit}.");
        }

        if (!string.IsNullOrWhiteSpace(workspace) && workspace != DocumentQuery.DefaultWorkspace)
        {
            throw new McpException($"workspace must be '{DocumentQuery.DefaultWorkspace}'; got '{workspace}'. Every document lives in '{DocumentQuery.DefaultWorkspace}' until workspaces land.");
        }

        DocumentPage page = await _store.ListDocuments(new DocumentQuery(query, offset, limit), cancellationToken);
        IReadOnlyList<DocumentListing> items = page.Items
            .Select(i => new DocumentListing(i.Id, i.Title, i.Type.ToString().ToLowerInvariant(), UtcTimestampConverter.Format(i.UpdatedAt)))
            .ToList();
        return new ListDocumentsResult(items, page.Total, page.Offset, page.Limit);
    }

    [McpServerTool(Name = "read_document"), Description("Reads a document's current snapshot as dsl, json, outline (the compact form for prompts) or mermaid (text to paste into a README).")]
    public async Task<string> ReadDocument(
        [Description("Document id.")] string id,
        [Description("dsl | json | outline | mermaid. Defaults to json. mermaid is text to paste into a README: a sequenceDiagram for a sequence document, an erDiagram for a document made only of ER entities, a flowchart for anything else, with no layout and a %% comment wherever a shape or arrowhead had no Mermaid equivalent.")] string format = "json",
        CancellationToken cancellationToken = default)
    {
        Document document = await RequireDocument(id, cancellationToken);
        return format switch
        {
            "json" => DocumentJson.Serialize(document, indented: true),
            "dsl" => Dsl.Print(document),
            "outline" => Outline.Render(document),
            "mermaid" => MermaidExporter.Export(document).Text,
            _ => throw new McpException($"format must be 'dsl', 'json', 'outline' or 'mermaid'; got '{format}'."),
        };
    }

    [McpServerTool(Name = "create_document"), Description("Creates a document from dsl, mermaid, sql, drawio, visio or json source. Missing geometry is laid out with ELK when layout is true (a sequence document has none: its layout is derived). mermaid is a flowchart (type flowchart or architecture), a sequenceDiagram (type must be sequence) or an erDiagram (type architecture or freeform, drawn as one entity per block and one crow's-foot edge per relationship); a mismatch is refused, never relabelled. sql is CREATE TABLE / ALTER TABLE DDL (T-SQL, PostgreSQL, MySQL or SQLite) drawn as one entity per table with a crow's-foot edge per foreign key; it carries no positions, so it is always laid out, and its type must be architecture or freeform. " +
        "drawio is a draw.io (diagrams.net) file, plain XML or compressed, read as one page (the page argument, counted from 1, the first by default; the result's warnings say when the file has others): shapes become nodes with their label, size and position, containers hold what is inside them, connectors become edges, and a kind is guessed from each shape and flagged in the warnings for review (type flowchart, architecture or freeform, which guesses none). Its positions arrive with the file, so it is laid out only when layout is true. What has no equivalent (stencils, pictures, loose connector ends, hidden layers) is a warning, never an error; a file that is not draw.io, has a DOCTYPE or is past a size limit is refused. " +
        "visio is a Visio (.vsdx) drawing. A .vsdx is a binary ZIP package and a tool argument is text, so content is the file BASE64-ENCODED (up to 3,000,000 bytes before encoding; whitespace and a data: URI prefix are tolerated). It is read like drawio: one page (the page argument, counted from 1, the first by default), shapes with their text, size and position (Visio's bottom-left origin turned to top-left), groups as containers, connectors as edges between the shapes they are glued to, kinds guessed from the master's name and the outline and flagged for review, positions kept, so it is laid out only when layout is true. Stencil masters, layers, shape data, themes and pictures are warnings, never errors; an older binary .vsd, a ZIP that is not Visio, a hostile package or one past a limit is refused (save a .vsd as .vsdx in Visio first). " +
        "title and type are the caller's, and where the dsl or json content has its own they are compared, never silently overridden: a type that differs from the content's own `type` is refused (the content was read against that type's kinds, so changing it would relabel the diagram); a non-empty content title that differs from the title argument is replaced by the title argument, and a warning says so. A new document id is always assigned: an id line or member in the content is ignored (POST /api/documents would keep a body id). " +
        "json takes the same document as POST /api/documents: id, schemaVersion, meta (and its createdAt, updatedAt, createdBy) may be left out and are filled in the same way, and meta.aiProvenance is dropped (the server records who wrote it); a node needs id, kind, label and shape, an edge id, kind, from and to. It differs in two ways only: title and type may also be left out of the body, since they are arguments here, and the id is never kept.")]
    public async Task<CreateDocumentResult> CreateDocument(
        [Description("Document title.")] string title,
        [Description("flowchart | architecture | freeform | sequence.")] string type,
        [Description("Source text: DSL, a Mermaid flowchart, sequenceDiagram or erDiagram, SQL DDL, a draw.io file's XML, a Visio .vsdx file base64-encoded, or a document as JSON.")] string content,
        [Description("dsl | mermaid | sql | drawio | visio | json.")] string format,
        [Description("Lay the document out with ELK before saving.")] bool layout = false,
        [Description("Over stdio: who is writing, as user:<name> or agent:<name>/<model>; defaults to agent:mcp-client/unknown. Over HTTP: the caller is already known from the session or token, so this may only be agent:<name>/<model> to annotate which agent is acting - a user:<name> value is rejected. The X-Draughtsman-Actor header is not read on /mcp at all: this argument is the only way to label the writer, and it never changes whose token is acting.")] string? actor = null,
        CancellationToken cancellationToken = default,
        [Description("drawio and visio only: which page of a multi-page file to read, counted from 1.")] int page = 1)
    {
        DocumentType documentType = ParseType(type);
        (Actor writer, string? actorUserId) = ResolveActor(actor);
        var warnings = new List<string>();

        Document document = format switch
        {
            "dsl" => ParseDsl(content, warnings),
            "mermaid" => ParseMermaid(content, documentType, warnings),
            "sql" => ParseSql(content, documentType, warnings),
            "drawio" => ParseDrawio(content, documentType, page, warnings),
            "visio" => ParseVisio(content, documentType, page, warnings),
            "json" => ParseJson(content, title, documentType),
            _ => throw new McpException($"format must be 'dsl', 'mermaid', 'sql', 'drawio', 'visio' or 'json'; got '{format}'."),
        };

        if (format is "dsl" or "json")
        {
            CompareWithArguments(document, title, documentType, format, warnings);
        }

        document = document with { Title = title, Type = documentType, Id = StoreIds.NewDocumentId() };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        document = document with { Meta = document.Meta with { CreatedAt = now, UpdatedAt = now, CreatedBy = writer.ToString() } };

        document = document with { Meta = AddProvenance(document.Meta, writer, now, AiAction.Created, document.Nodes.Select(n => n.Id).Concat(document.Edges.Select(e => e.Id))) };

        if (layout || format == "sql" || (format == "mermaid" && MermaidImporter.IsErDiagram(content)))
        {
            document = await TryLayout(document, warnings, cancellationToken);
        }

        ValidateAgainstSchema(document);
        await _store.CreateDocument(document, cancellationToken);
        await _store.AppendAudit(new AuditEntry(document.Id, writer, AuditActions.Created, now, actorUserId), cancellationToken);
        return new CreateDocumentResult(document.Id, warnings);
    }

    [McpServerTool(Name = "patch_document"), Description(
        "Amends a document with addressable ops: {op:'addNode'|'removeNode'|'updateNode'|'addEdge'|'removeEdge'|'updateEdge'|'moveEdge', ...} " +
        "(addEdge takes an optional before or after edge id to insert mid-stream instead of at the end, and moveEdge {id, before|after} reorders an existing edge: edge order is time order in a sequence diagram) " +
        "or {op:'setProp'|'removeProp'|'restyle', target:'node'|'edge', id, ...} " +
        "or {op:'setFlythrough', flythrough:{keyframes:[...], loop?}|null} (replaces the document's camera path whole, null removes it; it names no node or edge). Always checkpoints the document first. " +
        "An unknown op kind, a missing target or a duplicate id fails the whole call with a message naming the valid op kinds, rather than silently doing nothing.")]
    public async Task<PatchDocumentResult> PatchDocument(
        [Description("Document id.")] string id,
        [Description("The patch ops: a JSON array, or a string holding that JSON array.")] JsonElement ops,
        [Description("none | affected | all. 'affected' lays out when any node in the patched document has no geometry; 'all' always relays out the whole document.")] string layout = "none",
        [Description("Over stdio: who is writing, as user:<name> or agent:<name>/<model>; defaults to agent:mcp-client/unknown. Over HTTP: the caller is already known from the session or token, so this may only be agent:<name>/<model> to annotate which agent is acting - a user:<name> value is rejected. The X-Draughtsman-Actor header is not read on /mcp at all: this argument is the only way to label the writer, and it never changes whose token is acting.")] string? actor = null,
        CancellationToken cancellationToken = default)
    {
        if (layout is not ("none" or "affected" or "all"))
        {
            throw new McpException($"layout must be 'none', 'affected' or 'all'; got '{layout}'.");
        }

        // The document and the version tag it was stored under, in one read: the base version the write below is checked against.
        StoredDocument storedExisting = await _store.GetStoredDocument(id, cancellationToken) ?? throw new McpException($"No document with id '{id}'.");
        Document existing = storedExisting.Document;
        (Actor writer, string? actorUserId) = ResolveActor(actor);

        // Clients send ops as a real array or, because the tool used to demand it, as a string of JSON: take both.
        JsonArray opsArray;
        try
        {
            JsonNode? parsed = ops.ValueKind == JsonValueKind.String ? JsonNode.Parse(ops.GetString()!) : JsonNode.Parse(ops.GetRawText());
            opsArray = parsed as JsonArray ?? throw new McpException("ops must be a JSON array.");
        }
        catch (JsonException e)
        {
            throw new McpException($"ops is not valid JSON: {e.Message}");
        }

        JsonObject working = JsonNode.Parse(DocumentJson.Serialize(existing))!.AsObject();
        List<string> warnings;
        try
        {
            warnings = PatchApplier.Apply(working, opsArray);
        }
        catch (PatchException e)
        {
            throw new McpException(e.Message);
        }

        ValidateAgainstSchema(working.ToJsonString());

        Document patched;
        try
        {
            patched = DocumentJson.Deserialize(working.ToJsonString());
        }
        catch (JsonException e)
        {
            throw new McpException($"The patch produced a document that could not be read back: {e.Message}");
        }

        bool missingGeometry = patched.Nodes.Any(n => n.Geometry is null);
        if (layout == "all" || (layout == "affected" && missingGeometry))
        {
            patched = await TryLayout(patched, warnings, cancellationToken);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        patched = patched with { Meta = patched.Meta with { UpdatedAt = now } };

        // One provenance entry per write, listing every element the call touched, the way docs/examples/order-service.json
        // shapes it - not one per op, which would grow the header linearly with the document's edit history.
        patched = patched with { Meta = AddProvenance(patched.Meta, writer, now, AiAction.Updated, opsArray.Select(TouchedElementId).OfType<string>()) };

        ValidateAgainstSchema(patched);

        // Always checkpoints the snapshot being replaced first (build-spec-v0.1.md section 9), regardless of actor kind.
        DocumentVersion checkpoint;
        try
        {
            checkpoint = await _store.CreateVersion(id, "checkpoint", writer, $"Before patch by {writer}", cancellationToken);

            // The base version is the snapshot this patch was composed from, so a write that landed while the patch
            // was being built is not overwritten: the store refuses and the ops are reported back unapplied.
            ReplaceOutcome outcome = await _store.ReplaceDocument(patched, storedExisting.Version, cancellationToken);
            if (outcome == ReplaceOutcome.NotFound)
            {
                throw new McpException($"Document '{id}' was deleted while it was being patched.");
            }

            if (outcome == ReplaceOutcome.VersionMismatch)
            {
                throw new McpException($"Document '{id}' was changed by another writer while it was being patched; nothing was written. Read it again and re-send the ops.");
            }
        }
        catch (DocumentNotFoundException)
        {
            throw new McpException($"Document '{id}' was deleted while it was being patched.");
        }

        await _store.AppendAudit(new AuditEntry(id, writer, AuditActions.Replaced, now, actorUserId), cancellationToken);
        Document stored = await RequireDocument(id, cancellationToken);
        return new PatchDocumentResult(stored, checkpoint.Id, warnings);
    }

    /// <summary>In-process callers that already hold the ops as JSON text; not a tool, so the MCP surface is the
    /// <see cref="JsonElement"/> overload above.</summary>
    public Task<PatchDocumentResult> PatchDocument(string id, string ops, string layout = "none", string? actor = null, CancellationToken cancellationToken = default)
        => PatchDocument(id, JsonSerializer.SerializeToElement(ops), layout, actor, cancellationToken);

    [McpServerTool(Name = "render_document"), Description("Renders a document to SVG or PDF through the document's own geometry and style, laying it out first if any node has none. svg returns the markup; pdf returns base64 bytes and needs the optional Chromium export component. Returns warnings naming anything the picture could not show.")]
    public async Task<RenderDocumentResult> RenderDocument(
        [Description("Document id.")] string id,
        [Description("svg | pdf. Defaults to svg. pdf needs the optional Chromium export component (build-spec-v0.1.md section 7).")] string format = "svg",
        [Description("Scales the output width/height; the viewBox stays in canvas units. svg only.")] double? scale = null,
        CancellationToken cancellationToken = default)
    {
        if (format is not ("svg" or "pdf"))
        {
            throw new McpException($"render_document format must be 'svg' or 'pdf'; got '{format}'. 'png' needs the optional Chromium export component and is not wired here.");
        }

        if (format == "svg" && scale is { } suppliedScale && (!double.IsFinite(suppliedScale) || suppliedScale <= 0))
        {
            throw new McpException("scale must be positive and finite.");
        }

        Document document = await RequireDocument(id, cancellationToken);
        var warnings = new List<string>();
        if (document.Nodes.Any(n => n.Geometry is null))
        {
            document = await TryLayout(document, warnings, cancellationToken, throwIfUnavailable: true);
        }

        try
        {
            Dictionary<string, AssetImage> images = await ResolveAssets(document, cancellationToken);
            IReadOnlyDictionary<string, IReadOnlyList<Point>>? routes = null;
            SequenceScene? scene = null;
            if (document.Type == DocumentType.Sequence)
            {
                // A sequence diagram stores no positions: its picture is the sidecar's derived scene, and drawing it
                // without one would mean inventing a layout, so unlike a routing failure this refuses.
                try
                {
                    scene = await _sidecar.SequenceSceneAsync(document, cancellationToken);
                }
                catch (SidecarException e)
                {
                    throw new McpException($"Cannot render: a sequence diagram's layout is derived by the sidecar, and {DescribeSceneFailure(e)}.");
                }
            }

            try
            {
                routes = await ConnectorRouting.RouteAsync(_sidecar, document, cancellationToken);
            }
            catch (SidecarException e)
            {
                // Degrade honestly (build-spec-v0.1.md section 9): the picture is still returned, but the warning says
                // its orthogonal connectors are drawn straight rather than routed as the editor routes them.
                warnings.Add($"orthogonal connectors are drawn as straight lines because {DescribeRoutingFailure(e)}");
            }

            SvgRenderResult rendered = SvgRenderer.Render(document, format == "pdf" ? 1 : scale ?? 1, id => images.GetValueOrDefault(id), routes, scene, _themes?.Default);
            warnings.AddRange(rendered.Warnings);
            if (format == "svg")
            {
                return new RenderDocumentResult(rendered.Svg, Pdf: null!, warnings);
            }

            if (!_pdf.IsAvailable)
            {
                throw new McpException($"render_document format 'pdf' needs the optional Chromium export component, which is not available. Enable it with export.chromium.enabled and export.chromium.path in draughtsman.yaml and install Chromium (build-spec-v0.1.md section 7).");
            }

            byte[] pdf = await _pdf.RenderAsync(rendered.Svg, cancellationToken);
            return new RenderDocumentResult(Svg: null!, Convert.ToBase64String(pdf), warnings);
        }
        catch (InvalidOperationException e)
        {
            throw new McpException(e.Message);
        }
    }

    [McpServerTool(Name = "diff_documents"), Description("Structured diff between two revisions of a document: added/removed/changed nodes and edges, field by field.")]
    public async Task<DiffDocumentsResult> DiffDocuments(
        [Description("Document id.")] string id,
        [Description("A version id, or 'current' for the live snapshot.")] string fromVersion,
        [Description("A version id, or 'current' for the live snapshot. Defaults to 'current'.")] string toVersion = "current",
        CancellationToken cancellationToken = default)
    {
        Document from = await ResolveRevision(id, fromVersion, cancellationToken);
        Document to = await ResolveRevision(id, toVersion, cancellationToken);
        try
        {
            DocumentDiffResult diff = DocumentDiff.Compare(from, to);
            return new DiffDocumentsResult(fromVersion, toVersion, diff);
        }
        catch (InvalidOperationException e)
        {
            throw new McpException(e.Message);
        }
    }

    [McpServerTool(Name = "critique_document"), Description("Analyses a document for structural defects and returns findings with a rule id, severity, message and node/edge ids as evidence.")]
    public async Task<CritiqueDocumentResult> CritiqueDocument(
        [Description("Document id.")] string id,
        CancellationToken cancellationToken = default)
    {
        Document document = await RequireDocument(id, cancellationToken);
        try
        {
            return new CritiqueDocumentResult(CritiqueAnalyser.Analyse(document));
        }
        catch (InvalidOperationException e)
        {
            throw new McpException(e.Message);
        }
    }

    [McpServerTool(Name = "get_schema"), Description("Returns the committed document JSON Schema and, for flowchart or architecture, that type's kind vocabulary, plus the generic-technology icon library, the UML state-machine shape library, the BPMN shape library, the UML class shape library, the ER entity (database table) shape library and the general-purpose basic shape library (ellipse, triangle, note, callout, brace, cube, folder, component and more) - all read from the shipped files, never restated by hand.")]
    public GetSchemaResult GetSchema(
        [Description("flowchart | architecture | freeform | sequence. Omit for just the document schema.")] string? type = null)
    {
        JsonElement? kindVocabulary = null;
        JsonElement? genericIcons = null;
        JsonElement? stateShapes = null;
        JsonElement? bpmnShapes = null;
        JsonElement? umlShapes = null;
        JsonElement? erShapes = null;
        JsonElement? basicShapes = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            DocumentType parsed = ParseType(type);
            if (KindVocabulary.TryGet(parsed, out _))
            {
                kindVocabulary = JsonSerializer.Deserialize<JsonElement>(KindVocabulary.ReadRawJson(parsed));
            }

            // The generic icon library is not tied to a document type: every type can place its custom:generic/<name>
            // shapes, and it is listed whenever a type is named so an agent can discover the whole set.
            genericIcons = JsonSerializer.Deserialize<JsonElement>(GenericIconLibrary.RawJson);

            // The state-machine shapes are usable in flowchart and freeform documents; the library is listed for any
            // named type for the same reason, so an agent sees every custom:state/<name> shape and the kind it names.
            stateShapes = JsonSerializer.Deserialize<JsonElement>(StateShapeLibrary.RawJson);

            // The BPMN shapes (custom:bpmn/<name>, each with its own bpmn-<name> flowchart kind) likewise; pools and lanes
            // are the swimlane kinds already in the flowchart vocabulary.
            bpmnShapes = JsonSerializer.Deserialize<JsonElement>(BpmnShapeLibrary.RawJson);

            // The UML class shapes (class, interface, lollipop, socket) place in architecture and freeform documents;
            // the library carries the props protocol (attributes/operations line lists), so it is listed for any named type.
            umlShapes = JsonSerializer.Deserialize<JsonElement>(UmlShapeLibrary.RawJson);

            // The ER entity shapes (entity, weak-entity) place in architecture and freeform documents; the library carries
            // the column line format and the crow's-foot guidance, so it is listed for any named type.
            erShapes = JsonSerializer.Deserialize<JsonElement>(ErShapeLibrary.RawJson);

            // The basic shapes (custom:basic/<name>) place on any kind in any diagram type, so the library is listed for any named type.
            basicShapes = JsonSerializer.Deserialize<JsonElement>(BasicShapeLibrary.RawJson);
        }

        return new GetSchemaResult(JsonSerializer.Deserialize<JsonElement>(DocumentSchema.RawJson), kindVocabulary, genericIcons, stateShapes, bpmnShapes, umlShapes, erShapes, basicShapes);
    }

    private async Task<Document> RequireDocument(string id, CancellationToken cancellationToken)
    {
        Document? document = await _store.GetDocument(id, cancellationToken);
        return document ?? throw new McpException($"No document with id '{id}'.");
    }

    /// <summary>
    /// Every image node's bytes, keyed by asset id, so one read serves the render's inlined data URIs. An image node
    /// with no assetId, or one naming an unknown asset, is left with the renderer's placeholder frame rather than
    /// failing the whole render.
    /// </summary>
    private async Task<Dictionary<string, AssetImage>> ResolveAssets(Document document, CancellationToken cancellationToken)
    {
        var images = new Dictionary<string, AssetImage>(StringComparer.Ordinal);
        foreach (Node node in document.Nodes)
        {
            if (node.Shape != "image" || AssetIdOf(node) is not { } assetId || images.ContainsKey(assetId))
            {
                continue;
            }

            AssetContent? content = await _assets.ReadAsset(assetId, cancellationToken);
            if (content is not null)
            {
                images[assetId] = new AssetImage(content.ContentType, content.Bytes);
            }
        }

        return images;
    }

    /// <summary>An image node's asset id, from its <c>props.assetId</c>, or null when it names none.</summary>
    private static string? AssetIdOf(Node node)
    {
        if (node.Props is null || !node.Props.TryGetValue("assetId", out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return string.IsNullOrEmpty(value.GetString()) ? null : value.GetString();
    }

    private async Task<Document> ResolveRevision(string id, string revision, CancellationToken cancellationToken)
    {
        if (revision == "current")
        {
            return await RequireDocument(id, cancellationToken);
        }

        DocumentVersion? version = await _store.GetVersion(id, revision, cancellationToken);
        return version?.Document ?? throw new McpException($"No version '{revision}' of document '{id}'.");
    }

    private async Task<Document> TryLayout(Document document, List<string> warnings, CancellationToken cancellationToken, bool throwIfUnavailable = false)
    {
        if (document.Type == DocumentType.Sequence)
        {
            // Derived geometry: there is nothing to lay out or store (see NodeSidecarClient.LayoutAsync).
            return document;
        }

        try
        {
            LayoutResult result = await _sidecar.LayoutAsync(document, null, cancellationToken);
            warnings.AddRange(result.Warnings);
            return result.Document;
        }
        catch (SidecarException e) when (!throwIfUnavailable)
        {
            // Degrade honestly (build-spec-v0.1.md section 9): the document is still returned, just not laid out.
            warnings.Add($"layout was requested but {DescribeLayoutFailure(e)}");
            return document;
        }
        catch (SidecarException e)
        {
            throw new McpException($"Cannot render: the document has nodes with no geometry and layout failed - {DescribeLayoutFailure(e)}.");
        }
    }

    /// <summary>
    /// Names the actual failure, because the three <see cref="SidecarException"/> subtypes point at different fixes:
    /// a timeout means raising <c>sidecar.layoutTimeoutSeconds</c>, a rejection means the document, and only
    /// unavailability means the Node process.
    /// </summary>
    private static string DescribeLayoutFailure(SidecarException e)
    {
        return e switch
        {
            SidecarTimeoutException => $"the sidecar timed out laying it out: {e.Message}",
            SidecarRpcException => $"the sidecar rejected the document: {e.Message}",
            _ => $"the sidecar is unavailable: {e.Message}",
        };
    }

    private static string DescribeSceneFailure(SidecarException e)
    {
        return e switch
        {
            SidecarTimeoutException => $"the sidecar timed out deriving it: {e.Message}",
            SidecarRpcException => $"the sidecar rejected the document: {e.Message}",
            _ => $"the sidecar is unavailable: {e.Message}",
        };
    }

    private static string DescribeRoutingFailure(SidecarException e)
    {
        return e switch
        {
            SidecarTimeoutException => $"the sidecar timed out routing them: {e.Message}",
            SidecarRpcException => $"the sidecar rejected the document: {e.Message}",
            _ => $"the sidecar is unavailable: {e.Message}",
        };
    }

    private static Document ParseDsl(string content, List<string> warnings)
    {
        try
        {
            ParseResult result = Dsl.Parse(content);
            warnings.AddRange(result.Warnings.Select(w => w.ToString()));
            return result.Document;
        }
        catch (DslParseException e)
        {
            throw new McpException($"DSL parse error at line {e.Line}, column {e.Column}: {e.Message}");
        }
    }

    private static Document ParseMermaid(string content, DocumentType type, List<string> warnings)
    {
        try
        {
            ImportResult result = MermaidImporter.Import(content, new MermaidImportOptions { Type = type });
            warnings.AddRange(result.Warnings.Select(w => w.Line is { } line ? $"{w.Code} (line {line}): {w.Message}" : $"{w.Code}: {w.Message}"));
            return result.Document;
        }
        catch (Exception e) when (e is ArgumentException or SourceRejectedException)
        {
            throw new McpException(e.Message);
        }
    }

    private static Document ParseDrawio(string content, DocumentType type, int page, List<string> warnings)
    {
        try
        {
            ImportResult result = DrawioImporter.Import(content, new DrawioImportOptions { Type = type, Page = page });
            warnings.AddRange(result.Warnings.Select(w => $"{w.Code}: {w.Message}"));
            return result.Document;
        }
        catch (Exception e) when (e is ArgumentException or SourceRejectedException)
        {
            throw new McpException(e.Message);
        }
    }

    /// <summary>
    /// A Visio drawing carried as text: <paramref name="content"/> is the .vsdx file base64-encoded (a data: URI prefix and any whitespace are allowed, since a
    /// model or a shell often adds them). The size is checked before decoding, so a megabyte of text cannot cost more than its own decoded size.
    /// </summary>
    private static Document ParseVisio(string content, DocumentType type, int page, List<string> warnings)
    {
        string encoded = content.TrimStart();
        if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && encoded.IndexOf("base64,", StringComparison.OrdinalIgnoreCase) is var marker and >= 0)
        {
            encoded = encoded[(marker + "base64,".Length)..];
        }

        // Base64 is four characters for three bytes; whitespace (line breaks every 76 characters, or more) is allowed on top of that.
        if (encoded.Length > (VisioImporter.MaxFileBytes / 3 * 4) + (VisioImporter.MaxFileBytes / 16) + 16)
        {
            throw new McpException($"The Visio file is larger than {VisioImporter.MaxFileBytes:N0} bytes, which is more than Draughtsman reads. Remove embedded pictures, or save one page per file.");
        }

        byte[] file;
        try
        {
            file = Convert.FromBase64String(string.Concat(encoded.Where(c => !char.IsWhiteSpace(c))));
        }
        catch (FormatException)
        {
            throw new McpException("For format 'visio', content must be the .vsdx file encoded as base64 text (for example: base64 -i diagram.vsdx). That content is not valid base64.");
        }

        try
        {
            ImportResult result = VisioImporter.Import(file, new VisioImportOptions { Type = type, Page = page });
            warnings.AddRange(result.Warnings.Select(w => $"{w.Code}: {w.Message}"));
            return result.Document;
        }
        catch (Exception e) when (e is ArgumentException or SourceRejectedException)
        {
            throw new McpException(e.Message);
        }
    }

    private static Document ParseSql(string content, DocumentType type, List<string> warnings)
    {
        try
        {
            ImportResult result = SqlImporter.Import(content, new SqlImportOptions { Type = type });
            warnings.AddRange(result.Warnings.Select(w => w.Line is { } line ? $"{w.Code} (line {line}): {w.Message}" : $"{w.Code}: {w.Message}"));
            return result.Document;
        }
        catch (Exception e) when (e is ArgumentException or SourceRejectedException)
        {
            throw new McpException(e.Message);
        }
    }

    /// <summary>
    /// A type the content states for itself must be the type argument (the content was read against that type's kinds, as a
    /// Mermaid source already is); a title it states and the argument replaces is reported rather than silently dropped.
    /// </summary>
    private static void CompareWithArguments(Document parsed, string title, DocumentType type, string format, List<string> warnings)
    {
        if (parsed.Type != type)
        {
            throw new McpException($"The {format} content is type {parsed.Type.ToString().ToLowerInvariant()} but the type argument is {type.ToString().ToLowerInvariant()}. Make them agree: the content was read against {parsed.Type.ToString().ToLowerInvariant()}'s kinds, so it is refused rather than relabelled.");
        }

        if (!string.IsNullOrEmpty(parsed.Title) && parsed.Title != title)
        {
            warnings.Add($"The {format} content's own title \"{parsed.Title}\" was replaced by the title argument \"{title}\".");
        }
    }

    /// <summary>
    /// The same document POST /api/documents takes: the server fills schemaVersion and the meta header, drops any claimed
    /// aiProvenance, and (here, since they are arguments) takes title and type from the call when the body has none.
    /// </summary>
    private static Document ParseJson(string content, string title, DocumentType type)
    {
        try
        {
            if (JsonNode.Parse(content) is not JsonObject body)
            {
                throw new McpException("content is not a valid document: a document is a JSON object.");
            }

            string now = UtcTimestampConverter.Format(DateTimeOffset.UtcNow);
            body["title"] ??= title;
            body["type"] ??= type.ToString().ToLowerInvariant();
            body["id"] ??= StoreIds.NewDocumentId();
            body["schemaVersion"] ??= Document.CurrentSchemaVersion;
            if (body["meta"] is not JsonObject meta)
            {
                meta = [];
                body["meta"] = meta;
            }

            meta["createdAt"] ??= now;
            meta["updatedAt"] ??= now;
            meta["createdBy"] ??= "mcp";
            meta.Remove("aiProvenance");

            string completed = body.ToJsonString();
            ValidateAgainstSchema(completed);
            return DocumentJson.Deserialize(completed);
        }
        catch (JsonException e)
        {
            throw new McpException($"content is not a valid document: {e.Message}");
        }
    }

    private static void ValidateAgainstSchema(Document document)
    {
        ValidateAgainstSchema(DocumentJson.Serialize(document));
    }

    /// <summary>The node or edge an op touches, or null for one that touches none: <c>setFlythrough</c> replaces the camera path, which is no element.</summary>
    private static string? TouchedElementId(JsonNode? op)
    {
        string kind = op!["op"]!.GetValue<string>();
        if (kind == "setFlythrough")
        {
            return null;
        }

        return (kind switch
        {
            "addNode" => op["node"]!["id"],
            "addEdge" => op["edge"]!["id"],
            _ => op["id"],
        })!.GetValue<string>();
    }

    // Node ids and edge ids are separate namespaces, so the same id can appear in both; the DSL's provenance line
    // lists each element once, so the entry has to be deduplicated or the printed document will not parse back.
    private static Meta AddProvenance(Meta meta, Actor writer, DateTimeOffset at, AiAction action, IEnumerable<string> elementIds)
    {
        return writer.Kind == ActorKind.Agent
            ? meta with { AiProvenance = [.. meta.AiProvenance ?? [], new AiProvenance(writer.Name, at, action, elementIds.Distinct(StringComparer.Ordinal).ToList()) { Model = writer.Model }] }
            : meta;
    }

    private static void ValidateAgainstSchema(string json)
    {
        IReadOnlyList<string> errors = DocumentSchema.Validate(json);
        if (errors.Count > 0)
        {
            throw new McpException("The document is not valid: " + string.Join("; ", errors));
        }

        using JsonDocument document = JsonDocument.Parse(json);
        foreach (string collection in new[] { "nodes", "edges" })
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement element in document.RootElement.GetProperty(collection).EnumerateArray())
            {
                string id = element.GetProperty("id").GetString()!;
                if (!ids.Add(id))
                {
                    throw new McpException($"The document contains duplicate {collection} id '{id}'.");
                }
            }
        }
    }

    private static DocumentType ParseType(string type)
    {
        return type.Trim().ToLowerInvariant() switch
        {
            "flowchart" => DocumentType.Flowchart,
            "architecture" => DocumentType.Architecture,
            "freeform" => DocumentType.Freeform,
            "sequence" => DocumentType.Sequence,
            _ => throw new McpException($"type must be 'flowchart', 'architecture', 'freeform' or 'sequence'; got '{type}'."),
        };
    }

    /// <summary>
    /// Resolves who is writing. Over stdio (no <see cref="IActorContext.Current"/>) the <paramref name="actor"/>
    /// argument is fully self-reported, exactly as before - stdio has no identity to check it against
    /// (build-spec-v0.1.md section 9). Over HTTP the caller is already authenticated (the same fallback
    /// authorization policy that protects every other route), so the argument may only annotate which agent is
    /// acting on the owner's behalf (<c>agent:&lt;name&gt;/&lt;model&gt;</c>) - it can never claim a different user,
    /// and the owner's own id is always returned alongside it, since a display name alone is neither unique nor a
    /// reliable audit trail (security review F2, F9).
    /// </summary>
    private (Actor Actor, string? ActorUserId) ResolveActor(string? actor)
    {
        AuthenticatedIdentity? identity = _actorContext.Current;
        if (identity is null)
        {
            return (ParseActor(actor), null);
        }

        // ResolveActor is only ever called by the tools that write, so this is where a read-scoped API token (AU-05)
        // is refused: it may list, read, render, diff, critique and get the schema, never create or patch.
        if (identity.ReadOnly)
        {
            throw new McpException("This API token is read-only; it cannot create or change documents. Use a full-scope token.");
        }

        if (string.IsNullOrWhiteSpace(actor))
        {
            return (new Actor(ActorKind.User, identity.DisplayName), identity.UserId);
        }

        if (!Actor.TryParse(actor, out Actor? parsed) || parsed.Kind != ActorKind.Agent)
        {
            throw new McpException($"actor must be 'agent:<name>/<model>' when calling over HTTP; the caller's identity is already known from the session or token. Got '{actor}'.");
        }

        return (ValidateActorLength(parsed), identity.UserId);
    }

    private static Actor ParseActor(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            return DefaultActor;
        }

        return Actor.TryParse(actor, out Actor? parsed)
            ? ValidateActorLength(parsed)
            : throw new McpException($"actor must be 'user:<name>' or 'agent:<name>/<model>'; got '{actor}'.");
    }

    private static Actor ValidateActorLength(Actor parsed)
    {
        string wire = parsed.ToString();
        if (wire.Length > MaxActorWireLength || parsed.Name.Length > MaxActorNameLength || parsed.Model?.Length > MaxActorModelLength)
        {
            throw new McpException($"actor name and model must each be at most {MaxActorNameLength} characters and the whole form at most {MaxActorWireLength} characters.");
        }

        return parsed;
    }
}
