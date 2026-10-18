using Draughtsman.Core.Auth;
using Draughtsman.Server.Auth;
using Draughtsman.Server.Configuration;
using Draughtsman.Server.Theming;
using Draughtsman.Server.Theming.Extraction;

namespace Draughtsman.Server.Endpoints;

/// <summary>
/// Organisation themes (admin Themes page; docs/api.md "## Themes"): an admin creates, edits, duplicates, deletes and
/// exports themes, imports one from a file, and chooses the default that every user receives. The chrome half of the
/// default is what <c>GET /api/theme/chrome.css</c> serves; the diagram half is read by the SPA from
/// <c>GET /api/themes</c> and applied over a compiled diagram theme.
/// <para>
/// Every write route requires <see cref="AuthPolicies.CookieAdminOnly"/>: a cookie-authenticated admin, never an API
/// token regardless of its owner's role, matching Users, Tokens and Branding. The read at <c>/api/themes</c> is open
/// to any signed-in caller (the editor needs the default to paint a diagram), and carries no more than the diagram
/// half of each theme. Content is validated by <see cref="OrgThemeValidator"/>, never trusted and escaped: a colour is
/// hex and a font a list of family names or the request is refused with 422 and every problem listed. The
/// <c>X-Draughtsman-Actor</c> header plays no part in any of this; the acting user id in the log line is the
/// authenticated principal's.
/// </para>
/// </summary>
public static class OrgThemeEndpoints
{
    public static IEndpointRouteBuilder MapOrgThemeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/themes", ListForUsers).WithName("ListThemes");

        RouteGroupBuilder group = app.MapGroup("/api/admin/themes").RequireAuthorization(AuthPolicies.CookieAdminOnly);
        group.MapGet("", ListForAdmin).WithName("ListAdminThemes");
        group.MapPost("", Create).WithName("CreateTheme");
        group.MapPost("/import", Import).WithName("ImportTheme");
        group.MapPost("/extract", Extract).WithName("ExtractTheme");
        group.MapGet("/extract/options", ExtractOptions).WithName("ThemeExtractOptions");
        group.MapPut("/default", SetDefault).WithName("SetDefaultTheme");
        group.MapGet("/{id}", GetOne).WithName("GetTheme");
        group.MapPut("/{id}", Update).WithName("UpdateTheme");
        group.MapDelete("/{id}", Delete).WithName("DeleteTheme");
        group.MapPost("/{id}/duplicate", Duplicate).WithName("DuplicateTheme");
        group.MapGet("/{id}/export", Export).WithName("ExportTheme");
        return app;
    }

    private static IResult ListForUsers(OrgThemeService themes, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new PublicThemesResponse(
            themes.DefaultId,
            [.. themes.List.Select(t => new PublicTheme(t.Id, t.Name, ToDiagramDto(t.Diagram)))]));
    }

    private static IResult ListForAdmin(OrgThemeService themes)
    {
        return Results.Ok(Describe(themes));
    }

    private static IResult GetOne(string id, OrgThemeService themes)
    {
        return Run(() => Results.Ok(ToDto(themes.Get(id), themes.DefaultId)));
    }

    private static IResult Create(CreateThemeRequest? body, HttpContext context, OrgThemeService themes, ILoggerFactory loggerFactory)
    {
        if (body is null)
        {
            return MalformedBody();
        }

        return Run(() =>
        {
            OrgTheme created = themes.Create(body.Name ?? string.Empty, body.BaseId);
            Log(loggerFactory, context, "created", created.Id);
            return Results.Created($"/api/admin/themes/{created.Id}", ToDto(created, themes.DefaultId));
        });
    }

    private static IResult Import(ImportThemeRequest? body, HttpContext context, OrgThemeService themes, ILoggerFactory loggerFactory)
    {
        if (body?.Yaml is null)
        {
            return MalformedBody();
        }

        return Run(() =>
        {
            OrgTheme imported = themes.Import(body.Yaml);
            Log(loggerFactory, context, "imported", imported.Id);
            return Results.Created($"/api/admin/themes/{imported.Id}", ToDto(imported, themes.DefaultId));
        });
    }

    /// <summary>
    /// A draft theme from a site's colours (docs/api.md "Extract a draft from CSS or a web address"). Nothing is saved and nothing is
    /// stored: the draft and the list of how it was chosen are the whole response, and the draft only becomes a theme through the
    /// create and update routes. <c>css</c> is pure computation over the text sent and works with no network. <c>url</c> makes the
    /// server fetch a page: only when the operator enabled <c>themes.fetchFromUrl</c>, only here, only for a cookie-authenticated
    /// administrator, and recorded in the security log (host and outcome, never the path, the query or the page).
    /// </summary>
    private static async Task<IResult> Extract(ExtractThemeRequest? body, HttpContext context, ThemePageFetcher fetcher, SecurityEventLog events, CancellationToken cancellationToken)
    {
        bool hasCss = !string.IsNullOrWhiteSpace(body?.Css);
        bool hasUrl = !string.IsNullOrWhiteSpace(body?.Url);
        if (body is null || hasCss == hasUrl)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Nothing to read",
                detail: "Send the CSS (or the HTML of a page with its styles inline) as {\"css\": \"...\"}, or a web address as {\"url\": \"https://...\"}, not both.");
        }

        if (hasCss)
        {
            return Draft(() => ThemeExtractor.Extract(body.Css!, "Theme from pasted CSS"), "css", null);
        }

        string url = body.Url!;
        string host = ThemePageFetcher.HostForLog(url) ?? "unreadable";
        try
        {
            FetchedPage page = await fetcher.FetchAsync(url, cancellationToken);
            string name = $"Theme from {page.Host}";
            IResult result = Draft(() => ThemeExtractor.Extract(page.Source, name.Length > OrgThemeValidator.MaxNameLength ? name[..OrgThemeValidator.MaxNameLength] : name), "url", page);
            await Record(events, context, host, result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK } ? "ok" : "no-colours");
            return result;
        }
        catch (ThemeFetchException refused)
        {
            await Record(events, context, host, refused.Outcome);
            int status = refused.Reason switch
            {
                FetchRefusal.Disabled => StatusCodes.Status403Forbidden,
                FetchRefusal.TimedOut => StatusCodes.Status504GatewayTimeout,
                FetchRefusal.Unreachable => StatusCodes.Status502BadGateway,
                _ => StatusCodes.Status422UnprocessableEntity,
            };
            return Results.Problem(statusCode: status, title: "Could not fetch that page", detail: refused.Message, extensions: new Dictionary<string, object?> { ["reason"] = refused.Outcome });
        }
    }

    /// <summary>What the From a website section may offer: whether the operator enabled fetching a page by address. A separate route so the theme list's own response is unchanged.</summary>
    private static IResult ExtractOptions(DraughtsmanConfig config)
    {
        return Results.Ok(new ExtractOptionsResponse(config.Themes.FetchFromUrl.Enabled));
    }

    private static IResult Draft(Func<ThemeExtraction> extract, string source, FetchedPage? page)
    {
        try
        {
            ThemeExtraction extracted = extract();
            List<string> reasons = [.. extracted.Reasons];
            if (page is not null)
            {
                reasons.Insert(0, $"Read the page and {page.StylesheetsRead} of its stylesheets" + (page.StylesheetsSkipped > 0 ? $"; {page.StylesheetsSkipped} could not be read or were over the limit." : "."));
            }

            return Results.Ok(new ExtractThemeResponse(ToDto(extracted.Draft, null), reasons, extracted.Adjustments, source));
        }
        catch (ThemeExtractionException error)
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "No colours found", detail: error.Message);
        }
    }

    private static Task Record(SecurityEventLog events, HttpContext context, string host, string outcome)
    {
        // Host and outcome only: not the path or query of the address (which can carry a token) and nothing of the page.
        return events.Record(context, SecurityEventTypes.ThemeUrlFetch, detail: $"host={host} outcome={outcome}");
    }

    private static IResult Update(string id, OrgThemeDto? body, HttpContext context, OrgThemeService themes, ILoggerFactory loggerFactory)
    {
        if (body is null)
        {
            return MalformedBody();
        }

        return Run(() =>
        {
            OrgTheme saved = themes.Update(id, FromDto(body));
            Log(loggerFactory, context, "updated", saved.Id);
            return Results.Ok(ToDto(saved, themes.DefaultId));
        });
    }

    private static IResult Duplicate(string id, HttpContext context, OrgThemeService themes, ILoggerFactory loggerFactory)
    {
        return Run(() =>
        {
            OrgTheme copy = themes.Duplicate(id);
            Log(loggerFactory, context, "duplicated", copy.Id);
            return Results.Created($"/api/admin/themes/{copy.Id}", ToDto(copy, themes.DefaultId));
        });
    }

    private static IResult Delete(string id, HttpContext context, OrgThemeService themes, ILoggerFactory loggerFactory)
    {
        return Run(() =>
        {
            themes.Delete(id);
            Log(loggerFactory, context, "deleted", id);
            return Results.Ok(Describe(themes));
        });
    }

    private static IResult SetDefault(SetDefaultRequest? body, HttpContext context, OrgThemeService themes, ILoggerFactory loggerFactory)
    {
        if (body is null)
        {
            return MalformedBody();
        }

        return Run(() =>
        {
            themes.SetDefault(string.IsNullOrEmpty(body.Id) ? null : body.Id);
            Log(loggerFactory, context, "default set", body.Id ?? "shipped");
            return Results.Ok(Describe(themes));
        });
    }

    private static IResult Export(string id, OrgThemeService themes, ChromeBrandingService branding, HttpContext context)
    {
        return Run(() =>
        {
            string yaml = themes.Export(id, branding.Current.ProductName);
            // The id is a validated slug, so the file name can carry no header-breaking character.
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"{id}.theme.yaml\"";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Text(yaml, "application/x-yaml; charset=utf-8");
        });
    }

    private static IResult Run(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (OrgThemeException error)
        {
            return error.Failure switch
            {
                OrgThemeFailure.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such theme", detail: error.Message),
                OrgThemeFailure.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Theme conflict", detail: error.Message),
                _ => Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Invalid theme",
                    detail: error.Message,
                    extensions: new Dictionary<string, object?> { ["problems"] = error.Problems.Select(p => new ProblemItem(p.Field, p.Message)).ToArray() }),
            };
        }
    }

    private static AdminThemesResponse Describe(OrgThemeService themes)
    {
        string? defaultId = themes.DefaultId;
        return new AdminThemesResponse(defaultId, [.. themes.List.Select(t => ToDto(t, defaultId))], [.. OrgDiagramTheme.Bases]);
    }

    private static OrgThemeDto ToDto(OrgTheme theme, string? defaultId)
    {
        return new OrgThemeDto(
            theme.Id,
            theme.Name,
            theme.Mode,
            new ChromeDto(
                new Dictionary<string, string>(theme.Dark),
                new Dictionary<string, string>(theme.Light),
                theme.Radius,
                new ChromeFontsDto(theme.FontUi, theme.FontMono)),
            ToDiagramDto(theme.Diagram),
            theme.UpdatedAt,
            string.Equals(theme.Id, defaultId, StringComparison.Ordinal));
    }

    private static DiagramDto ToDiagramDto(OrgDiagramTheme diagram)
    {
        return new DiagramDto(
            diagram.Base,
            new Dictionary<string, string>(diagram.Palette),
            diagram.CanvasBackground,
            diagram.ExportBackground,
            diagram.GridColor,
            new DiagramFontsDto(diagram.FontLabel, diagram.FontMono));
    }

    /// <summary>Maps a request body to a theme for validation; a missing part becomes an empty one the validator reports on.</summary>
    private static OrgTheme FromDto(OrgThemeDto dto)
    {
        return new OrgTheme(
            dto.Id ?? string.Empty,
            dto.Name ?? string.Empty,
            dto.Mode ?? string.Empty,
            dto.Chrome?.Dark ?? [],
            dto.Chrome?.Light ?? [],
            dto.Chrome?.Radius ?? double.NaN,
            dto.Chrome?.Fonts?.Ui ?? string.Empty,
            dto.Chrome?.Fonts?.Mono ?? string.Empty,
            dto.Diagram is null
                ? OrgDiagramTheme.Default with { Base = string.Empty }
                : new OrgDiagramTheme(
                    dto.Diagram.Base ?? string.Empty,
                    dto.Diagram.Palette ?? [],
                    dto.Diagram.CanvasBackground,
                    dto.Diagram.ExportBackground,
                    dto.Diagram.GridColor,
                    dto.Diagram.Fonts?.Label,
                    dto.Diagram.Fonts?.Mono),
            DateTimeOffset.UtcNow);
    }

    private static void Log(ILoggerFactory loggerFactory, HttpContext context, string what, string themeId)
    {
        // Never the colours or fonts, only what happened to which theme and who did it.
        loggerFactory.CreateLogger("Draughtsman.Server.Themes")
            .LogInformation("Theme {ThemeId} {What} by user {ActorUserId}.", themeId, what, context.User.UserId() ?? "unknown");
    }

    private static IResult MalformedBody()
    {
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Malformed body", detail: "The body must be a JSON object of the shape docs/api.md describes for this route.");
    }

    public sealed record CreateThemeRequest(string? Name = null, string? BaseId = null);

    public sealed record ImportThemeRequest(string? Yaml = null);

    public sealed record ExtractThemeRequest(string? Css = null, string? Url = null);

    public sealed record ExtractThemeResponse(OrgThemeDto Draft, IReadOnlyList<string> Reasons, IReadOnlyList<string> Adjustments, string Source);

    public sealed record SetDefaultRequest(string? Id = null);

    public sealed record OrgThemeDto(
        string? Id = null,
        string? Name = null,
        string? Mode = null,
        ChromeDto? Chrome = null,
        DiagramDto? Diagram = null,
        DateTimeOffset? UpdatedAt = null,
        bool? IsDefault = null);

    public sealed record ChromeDto(
        Dictionary<string, string>? Dark = null,
        Dictionary<string, string>? Light = null,
        double? Radius = null,
        ChromeFontsDto? Fonts = null);

    public sealed record ChromeFontsDto(string? Ui = null, string? Mono = null);

    public sealed record DiagramDto(
        string? Base = null,
        Dictionary<string, string>? Palette = null,
        string? CanvasBackground = null,
        string? ExportBackground = null,
        string? GridColor = null,
        DiagramFontsDto? Fonts = null);

    public sealed record DiagramFontsDto(string? Label = null, string? Mono = null);

    public sealed record AdminThemesResponse(string? DefaultId, IReadOnlyList<OrgThemeDto> Themes, IReadOnlyList<string> Bases);

    public sealed record ExtractOptionsResponse(bool Url);

    public sealed record PublicTheme(string Id, string Name, DiagramDto Diagram);

    public sealed record PublicThemesResponse(string? DefaultId, IReadOnlyList<PublicTheme> Themes);

    public sealed record ProblemItem(string Field, string Message);
}
