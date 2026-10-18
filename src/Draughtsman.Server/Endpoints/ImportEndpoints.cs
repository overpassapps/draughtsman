using System.Text.Json;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Import;
using Draughtsman.Core.Import.Drawio;
using Draughtsman.Core.Import.Sql;
using Draughtsman.Core.Import.Visio;
using Draughtsman.Core.Models;
using Draughtsman.Core.Serialization;
using Draughtsman.Core.Text;

namespace Draughtsman.Server.Endpoints;

/// <summary>Stateless text-to-document conversions. Nothing here touches the store; the caller decides whether to save.</summary>
public static class ImportEndpoints
{
    private const int MaxSourceLength = 1_000_000;

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/import/mermaid", ImportMermaid)
            .WithName("ImportMermaid")
            .Accepts<string>("text/plain");
        app.MapPost("/api/import/dsl", ImportDsl)
            .WithName("ImportDsl")
            .Accepts<string>("text/plain");
        app.MapPost("/api/import/sql", ImportSql)
            .WithName("ImportSql")
            .Accepts<string>("text/plain");
        // A .drawio file arrives as the request body (XML, whatever it is labelled) or as one file of a multipart form, so the route declares both in ONE
        // Accepts (a second Accepts replaces the first as far as content-type matching goes, and a request it does not match is a 404, not a 415); it
        // reads the form itself, as the asset upload does, and has no use for the framework's antiforgery check (the CSRF middleware guards it).
        app.MapPost("/api/import/drawio", ImportDrawio)
            .WithName("ImportDrawio")
            .Accepts<string>("text/plain", "application/xml", "text/xml", "application/vnd.jgraph.mxfile", "application/octet-stream", "multipart/form-data")
            .DisableAntiforgery();
        // A .vsdx is a binary ZIP package, so it arrives as the request body (any of the types a client might label it with) or as one file of a multipart
        // form, and the route reads both itself for the same reason as draw.io's. Over the limit is a 413 before the body is read where the length says so.
        app.MapPost("/api/import/visio", ImportVisio)
            .WithName("ImportVisio")
            .Accepts<byte[]>("application/octet-stream", "application/vnd.ms-visio.drawing", "application/vnd.ms-visio.drawing.macroenabled.12", "application/vnd.visio", "application/zip", "application/x-zip-compressed", "multipart/form-data")
            .DisableAntiforgery();
        app.MapPost("/api/export/dsl", ExportDsl)
            .WithName("ExportDsl")
            .Accepts<JsonElement>("application/json");
        app.MapPost("/api/export/mermaid", ExportMermaid)
            .WithName("ExportMermaid")
            .Accepts<JsonElement>("application/json");
        app.MapPost("/api/export/sql", ExportSql)
            .WithName("ExportSql")
            .Accepts<JsonElement>("application/json");
        return app;
    }

    private static async Task<IResult> ExportDsl(HttpRequest request, CancellationToken cancellationToken)
    {
        (Document? document, IResult? problem) = await ReadDocument(request, "printed", cancellationToken);
        if (document is null)
        {
            return problem!;
        }

        try
        {
            return Results.Text(Dsl.Print(document), "text/plain");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The document could not be printed.", detail: ex.Message);
        }
    }

    /// <summary>
    /// Prints an unsaved document as Mermaid: a <c>sequence</c> document as a <c>sequenceDiagram</c>, anything else as a
    /// <c>flowchart</c>. What Mermaid has no spelling for is a <c>%%</c> comment in the text beside what it degraded, so the
    /// text is complete on its own and, like the DSL and SQL exports, nothing is saved.
    /// </summary>
    /// <summary>How many shapes, arrowheads or other things <c>POST /api/export/mermaid</c> had to simplify; 0 is a faithful export.</summary>
    public const string MermaidWarningsHeader = "X-Draughtsman-Export-Warnings";

    /// <summary>
    /// What <c>POST /api/export/mermaid</c> had to simplify, as a percent-encoded JSON array of the messages (at most
    /// <see cref="MermaidNotesLimit"/>, the rest summarised in a last entry), so the editor can list them beside the count.
    /// </summary>
    public const string MermaidNotesHeader = "X-Draughtsman-Export-Notes";

    private const int MermaidNotesLimit = 25;

    private static async Task<IResult> ExportMermaid(HttpRequest request, CancellationToken cancellationToken)
    {
        (Document? document, IResult? problem) = await ReadDocument(request, "exported", cancellationToken);
        if (document is null)
        {
            return problem!;
        }

        // Text is all the body carries, so how much was simplified travels as a header (docs/api.md): the SPA's banner
        // says the copy is lossy when it is not zero.
        MermaidExportResult result = MermaidExporter.Export(document);
        request.HttpContext.Response.Headers[MermaidWarningsHeader] = result.Warnings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (result.Warnings.Count > 0)
        {
            List<string> notes = [.. result.Warnings.Take(MermaidNotesLimit)];
            if (result.Warnings.Count > MermaidNotesLimit)
            {
                notes.Add($"and {(result.Warnings.Count - MermaidNotesLimit).ToString(System.Globalization.CultureInfo.InvariantCulture)} more (each is a % comment in the text)");
            }

            request.HttpContext.Response.Headers[MermaidNotesHeader] = Uri.EscapeDataString(JsonSerializer.Serialize(notes));
        }

        return Results.Text(result.Text, "text/plain");
    }

    /// <summary>
    /// Prints an unsaved entity diagram as CREATE TABLE DDL (<c>?dialect=tsql|postgres|mysql|sqlite</c>, default
    /// <c>tsql</c>). What could not be carried (a node that is not an entity, a many-to-many edge, a key-less parent) is
    /// in the script as leading <c>-- warning:</c> comments, so the script is complete on its own.
    /// </summary>
    private static async Task<IResult> ExportSql(HttpRequest request, string? dialect, CancellationToken cancellationToken)
    {
        SqlDialect target = SqlDialect.TSql;
        if (!string.IsNullOrWhiteSpace(dialect) && (!SqlDialects.TryParse(dialect, out target) || target == SqlDialect.Auto))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown dialect",
                detail: $"'{dialect}' is not a dialect to export to; use 'tsql', 'postgres', 'mysql' or 'sqlite'.");
        }

        (Document? document, IResult? problem) = await ReadDocument(request, "exported", cancellationToken);
        if (document is null)
        {
            return problem!;
        }

        return Results.Text(SqlExporter.Export(document, target).Sql, "text/plain");
    }

    /// <summary>The request body as a schema-valid document, or the 400/422 problem that replaces it. Both export endpoints read alike.</summary>
    private static async Task<(Document? Document, IResult? Problem)> ReadDocument(HttpRequest request, string verb, CancellationToken cancellationToken)
    {
        JsonDocument body;
        try
        {
            body = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Malformed JSON", detail: ex.Message));
        }

        using var input = body;
        string json = body.RootElement.GetRawText();
        IReadOnlyList<string> errors = DocumentSchema.Validate(json);
        if (errors.Count > 0)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: $"The document could not be {verb}.", detail: string.Join("; ", errors)));
        }

        try
        {
            return (DocumentJson.Deserialize(json), null);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: $"The document could not be {verb}.", detail: ex.Message));
        }
    }

    /// <summary>
    /// Reads pasted DDL (<c>?dialect=</c> names tsql, postgres, mysql or sqlite; absent means detect it) into a document of
    /// ER entities: one table per node, one crow's-foot edge per foreign key. Like the other importers it saves nothing and
    /// places nothing; the caller posts the document to /api/layout. Whatever was skipped is a warning, so the response is
    /// 200 with a possibly empty document unless the request itself is wrong.
    /// </summary>
    private static async Task<IResult> ImportSql(HttpRequest request, string? dialect, string? type, string? title, string? tags, string? implied, CancellationToken cancellationToken)
    {
        if (!SqlDialects.TryParse(dialect, out SqlDialect parsedDialect))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown dialect",
                detail: $"'{dialect}' is not a dialect; use 'tsql', 'postgres', 'mysql' or 'sqlite', or leave it out to detect it.");
        }

        DocumentType documentType = DocumentType.Architecture;
        if (!string.IsNullOrWhiteSpace(type)
            && (!Enum.TryParse(type, ignoreCase: true, out documentType) || documentType is not (DocumentType.Architecture or DocumentType.Freeform)))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unsupported document type",
                detail: $"'{type}' cannot hold database tables; use 'architecture' or 'freeform'.");
        }

        if (!string.IsNullOrWhiteSpace(tags) && !tags.Equals("on", StringComparison.OrdinalIgnoreCase) && !tags.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown tags option",
                detail: $"'{tags}' is not a tags option; use 'on' (the default) or 'off'.");
        }

        if (!string.IsNullOrWhiteSpace(implied) && !implied.Equals("on", StringComparison.OrdinalIgnoreCase) && !implied.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown implied option",
                detail: $"'{implied}' is not an implied option; use 'on' (the default) or 'off'.");
        }

        (string? source, IResult? problem) = await ReadSource(request, "SQL", cancellationToken);
        if (source is null)
        {
            return problem!;
        }

        try
        {
            ImportResult result = SqlImporter.Import(source, new SqlImportOptions
            {
                Dialect = parsedDialect,
                Type = documentType,
                Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                Tags = !"off".Equals(tags?.Trim(), StringComparison.OrdinalIgnoreCase),
                Implied = !"off".Equals(implied?.Trim(), StringComparison.OrdinalIgnoreCase),
            });
            return Results.Json(result, DocumentJson.Options);
        }
        catch (SourceRejectedException ex)
        {
            return SourceRejected(ex);
        }
    }

    /// <summary>
    /// A Mermaid <c>sequenceDiagram</c> imports as a <c>sequence</c> document, an <c>erDiagram</c> as <c>architecture</c> (the SQL import's default; <c>freeform</c> is the other choice) and a flowchart as <c>flowchart</c> (or
    /// <c>architecture</c>). With no <c>?type=</c> the header decides; a <c>type</c> that disagrees with the source is a 400
    /// that says so, never a relabelled document.
    /// </summary>
    private static async Task<IResult> ImportMermaid(HttpRequest request, string? type, string? title, CancellationToken cancellationToken)
    {
        DocumentType? requested = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse(type, ignoreCase: true, out DocumentType parsed) || !Enum.IsDefined(parsed))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown document type",
                    detail: $"'{type}' is not a document type; use 'flowchart' or 'architecture' for a flowchart, or 'sequence' for a sequenceDiagram, or 'architecture' for an erDiagram.");
            }

            requested = parsed;
        }

        (string? source, IResult? problem) = await ReadSource(request, "Mermaid", cancellationToken);
        if (source is null)
        {
            return problem!;
        }

        ImportResult result;
        try
        {
            // Freeform holds an erDiagram's entities but has no kind vocabulary for a flowchart's shapes.
            if (requested == DocumentType.Freeform && !MermaidImporter.IsErDiagram(source))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unsupported document type",
                    detail: "Freeform documents have no kind vocabulary to map a Mermaid flowchart or sequenceDiagram into; use 'flowchart' or 'architecture' for a flowchart, or 'sequence' for a sequenceDiagram. Freeform is only for an erDiagram.");
            }

            DocumentType documentType = requested ?? (MermaidImporter.IsSequenceDiagram(source) ? DocumentType.Sequence : MermaidImporter.IsErDiagram(source) ? DocumentType.Architecture : DocumentType.Flowchart);
            result = MermaidImporter.Import(source, new MermaidImportOptions
            {
                Type = documentType,
                Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            });
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Document type does not match the source", detail: ex.Message);
        }
        catch (SourceRejectedException ex)
        {
            return SourceRejected(ex);
        }

        int status = result.UnsupportedDiagram ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status200OK;
        return Results.Json(result, DocumentJson.Options, statusCode: status);
    }

    /// <summary>
    /// Reads a draw.io (diagrams.net) file, plain or compressed, into a document of the requested type (<c>?type=flowchart</c> by default,
    /// <c>architecture</c> or <c>freeform</c>): the shapes, containers and connectors of one page (<c>?page=</c>, counted from 1, the first by default),
    /// with the geometry the file gave them, so the caller does not lay it out. The body is the file's XML as the request body, or one file of a
    /// <c>multipart/form-data</c> form. Nothing is saved. A shape or style it cannot map is a warning on a 200, not a failure; a body that is not a
    /// draw.io file, or is past a limit, or has a DOCTYPE, is a 422 (the source is refused); a page the file does not have is a 400.
    /// </summary>
    private static async Task<IResult> ImportDrawio(HttpRequest request, string? type, string? title, string? page, CancellationToken cancellationToken)
    {
        if (ReadDrawingQuery(type, page, out DocumentType documentType, out int pageNumber) is { } invalid)
        {
            return invalid;
        }

        (string? source, IResult? problem) = await ReadDrawioSource(request, cancellationToken);
        if (source is null)
        {
            return problem!;
        }

        try
        {
            ImportResult result = DrawioImporter.Import(source, new DrawioImportOptions
            {
                Type = documentType,
                Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                Page = pageNumber,
            });
            return Results.Json(result, DocumentJson.Options);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "That page cannot be imported", detail: ex.Message);
        }
        catch (SourceRejectedException ex)
        {
            return SourceRejected(ex);
        }
    }

    /// <summary>
    /// The <c>type</c> and <c>page</c> of a drawing import (draw.io and Visio read alike): a document type a drawing can be read into, and a page number from 1.
    /// Null when both are good; otherwise the 400 that says which is not.
    /// </summary>
    private static IResult? ReadDrawingQuery(string? type, string? page, out DocumentType documentType, out int pageNumber)
    {
        documentType = DocumentType.Flowchart;
        pageNumber = 1;
        if (!string.IsNullOrWhiteSpace(type)
            && (!Enum.TryParse(type, ignoreCase: true, out documentType) || documentType is not (DocumentType.Flowchart or DocumentType.Architecture or DocumentType.Freeform)))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unsupported document type",
                detail: $"'{type}' cannot be made from a drawing; use 'flowchart' (the default), 'architecture' or 'freeform'. A sequence document is made from a Mermaid sequenceDiagram.");
        }

        if (!string.IsNullOrWhiteSpace(page) && (!int.TryParse(page, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out pageNumber) || pageNumber < 1))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown page",
                detail: $"'{page}' is not a page number; pages are counted from 1.");
        }

        return null;
    }

    /// <summary>
    /// Reads a Visio (.vsdx) drawing into a document of the requested type (<c>?type=flowchart</c> by default, <c>architecture</c> or <c>freeform</c>): the shapes,
    /// groups and connectors of one page (<c>?page=</c>, counted from 1, the first by default), with the geometry the file gave them, so the caller does not lay it
    /// out. The body is the file's bytes as the request body, or one file of a <c>multipart/form-data</c> form. Nothing is saved. A shape or style it cannot map is
    /// a warning on a 200, not a failure; a body that is not a .vsdx (an older .vsd included), or is past a limit, or is a hostile ZIP or XML, is a 422 (the source is
    /// refused); a page the file does not have is a 400.
    /// </summary>
    private static async Task<IResult> ImportVisio(HttpRequest request, string? type, string? title, string? page, CancellationToken cancellationToken)
    {
        if (ReadDrawingQuery(type, page, out DocumentType documentType, out int pageNumber) is { } invalid)
        {
            return invalid;
        }

        (byte[]? file, IResult? problem) = await ReadVisioSource(request, cancellationToken);
        if (file is null)
        {
            return problem!;
        }

        try
        {
            ImportResult result = VisioImporter.Import(file, new VisioImportOptions
            {
                Type = documentType,
                Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
                Page = pageNumber,
            });
            return Results.Json(result, DocumentJson.Options);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "That page cannot be imported", detail: ex.Message);
        }
        catch (SourceRejectedException ex)
        {
            return SourceRejected(ex);
        }
    }

    /// <summary>The .vsdx bytes: the request body, or the first file of a multipart form; a 400 when there is none and a 413 when it is past <see cref="VisioImporter.MaxFileBytes"/>, however it says so.</summary>
    private static async Task<(byte[]? File, IResult? Problem)> ReadVisioSource(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            if (request.ContentLength > VisioImporter.MaxFileBytes)
            {
                return (null, VisioTooLarge());
            }

            byte[]? body = await ReadLimited(request.Body, cancellationToken);
            return body switch
            {
                null => (null, VisioTooLarge()),
                { Length: 0 } => (null, Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Empty body",
                    detail: "Send the Visio (.vsdx) file as the request body (application/octet-stream) or as one file of a multipart form.")),
                _ => (body, null),
            };
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The multipart body could not be read."));
        }

        IFormFile? upload = form.Files.Count > 0 ? form.Files[0] : null;
        if (upload is null)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No file", detail: "The multipart body carried no file. Send the Visio (.vsdx) file as one part of the form."));
        }

        if (upload.Length > VisioImporter.MaxFileBytes)
        {
            return (null, VisioTooLarge());
        }

        await using Stream stream = upload.OpenReadStream();
        byte[]? bytes = await ReadLimited(stream, cancellationToken);
        return bytes switch
        {
            null => (null, VisioTooLarge()),
            { Length: 0 } => (null, Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Empty file", detail: "The Visio file is empty.")),
            _ => (bytes, null),
        };
    }

    /// <summary>Everything in <paramref name="stream"/> up to <see cref="VisioImporter.MaxFileBytes"/>, or null when there is more (the stream is not read past one byte over).</summary>
    private static async Task<byte[]?> ReadLimited(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, VisioImporter.MaxFileBytes + 1L - buffer.Length)), cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > VisioImporter.MaxFileBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }

    private static IResult VisioTooLarge()
    {
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Source too large",
            detail: $"A Visio file is limited to {VisioImporter.MaxFileBytes:N0} bytes. Remove embedded pictures, or save one page per file.");
    }

    /// <summary>The draw.io text: the request body, or the first file of a multipart form; a 400 when there is none and a 413 when it is too large.</summary>
    private static async Task<(string? Source, IResult? Problem)> ReadDrawioSource(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return await ReadSource(request, "draw.io", cancellationToken);
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(cancellationToken);
        }
        catch (InvalidDataException)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The multipart body could not be read."));
        }

        IFormFile? file = form.Files.Count > 0 ? form.Files[0] : null;
        if (file is null)
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No file", detail: "The multipart body carried no file. Send the draw.io file as one part of the form."));
        }

        // A character is at most three bytes of UTF-8, so a file longer than that cannot be within the character limit.
        if (file.Length > MaxSourceLength * 3L)
        {
            return (null, SourceTooLarge());
        }

        string source;
        await using (Stream stream = file.OpenReadStream())
        using (var reader = new StreamReader(stream))
        {
            source = await reader.ReadToEndAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Empty file", detail: "The draw.io file is empty."));
        }

        return source.Length > MaxSourceLength ? (null, SourceTooLarge()) : (source, null);
    }

    /// <summary>
    /// The SPA's DSL entry point: the browser has no DSL parser of its own, so this is the only way to turn pasted
    /// DSL text into a document short of hand-rolling one client-side. It calls <see cref="Dsl.Parse"/> unchanged
    /// and returns the same <c>{ document, warnings }</c> envelope as the Mermaid import; a syntax error comes back
    /// as 422 with the parser's own message plus <c>line</c>/<c>column</c> extensions so the caller can point at it.
    /// </summary>
    private static async Task<IResult> ImportDsl(HttpRequest request, CancellationToken cancellationToken)
    {
        (string? source, IResult? problem) = await ReadSource(request, "DSL", cancellationToken);
        if (source is null)
        {
            return problem!;
        }

        ParseResult result;
        try
        {
            result = Dsl.Parse(source);
        }
        catch (DslParseException ex)
        {
            // ex.Message repeats the "line X, column Y:" prefix that line/column already carry as extensions;
            // ex.Detail is the message alone so a caller composing "line X, column Y: <detail>" does not double it.
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The DSL could not be parsed.",
                detail: ex.Detail, extensions: new Dictionary<string, object?> { ["line"] = ex.Line, ["column"] = ex.Column });
        }

        // w.Message alone: ImportWarning.Line already carries the position, and w.ToString() repeats it in the text.
        var warnings = result.Warnings
            .Select(w => new ImportWarning(ImportWarningCodes.DslLint, w.Message, w.Line))
            .ToList();
        // Dsl.Parse slugs the title into the id when the source has no id line (the printer leaves that line out when it
        // matches, so export then import must give the same id back: DslExportEndpointTests). A caller that means to
        // create a new document from text, and may already hold one with that title, asks for a fresh id (QA-09).
        Document document = request.Query.TryGetValue("newId", out var newId) && string.Equals(newId, "true", StringComparison.OrdinalIgnoreCase)
            ? result.Document with { Id = $"doc_{Guid.NewGuid():N}" }
            : result.Document;
        return Results.Json(new ImportResult(document, warnings), DocumentJson.Options);
    }

    /// <summary>The request body as text, or the 400/413 problem that replaces it. Both import endpoints read alike.</summary>
    private static async Task<(string? Source, IResult? Problem)> ReadSource(HttpRequest request, string format, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxSourceLength)
        {
            return (null, SourceTooLarge());
        }

        string source;
        using (var reader = new StreamReader(request.Body))
        {
            source = await reader.ReadToEndAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return (null, Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Empty body",
                detail: $"Send the {format} source as the request body (text/plain)."));
        }

        return source.Length > MaxSourceLength ? (null, SourceTooLarge()) : (source, null);
    }

    /// <summary>The importer would not read this source in reasonable time (a regex match timeout): the source is refused, not the request malformed.</summary>
    private static IResult SourceRejected(SourceRejectedException ex)
    {
        return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "The source could not be imported.", detail: ex.Message);
    }

    private static IResult SourceTooLarge()
    {
        return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Source too large",
            detail: $"Source is limited to {MaxSourceLength:N0} characters.");
    }
}
