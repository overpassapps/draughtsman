using Draughtsman.Server.Cli;

namespace Draughtsman.Server.Docs;

/// <summary>
/// The documentation folder a release archive ships (<c>docs/</c> beside the executable), if this install has one. Absent in a
/// build tree and in the Docker image, where the Help menu keeps pointing at the vendor's site.
/// </summary>
public sealed class DocsFolder(string? root)
{
    /// <summary>The route the folder is served at.</summary>
    public const string Route = "/docs/";

    /// <summary>The absolute folder, or null when this install ships no documentation.</summary>
    public string? Root { get; } = root;

    public bool Available => Root is not null;

    /// <summary>The archive's documentation folder: only a packaged install (<see cref="AppLayout.IsPackaged"/>) with a <c>docs/index.md</c> counts.</summary>
    public static DocsFolder Find(string baseDirectory)
    {
        string docs = Path.GetFullPath(Path.Combine(baseDirectory, "docs"));
        return AppLayout.IsPackaged(baseDirectory) && File.Exists(Path.Combine(docs, "index.md")) ? new DocsFolder(docs) : new DocsFolder(null);
    }
}

/// <summary>
/// GET /docs/{path}: the archive's customer documentation, read-only, for a site whose browsers cannot reach the vendor's
/// website (docs/api.md "Documentation"). Behind sign-in like every route (the global fallback policy; there is no
/// <c>AllowAnonymous</c> here, and <c>ProtectedRoutesTests</c> checks it). It serves a short allow-list of text types as plain
/// text, never as HTML, and refuses anything that resolves outside the folder: a dot-dot segment, a backslash or drive
/// letter, an absolute path, or a symbolic link pointing elsewhere.
/// </summary>
public static class DocsEndpoints
{
    /// <summary>The only file types served. Markdown is sent as <c>text/plain</c> so a browser shows it rather than downloading it.</summary>
    private static readonly HashSet<string> s_extensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".json", ".dsl", ".grammar", ".yaml", ".txt" };

    public static IEndpointRouteBuilder MapDocsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/docs/{**path}", Serve).WithName("GetDocsFile");
        return app;
    }

    private static IResult Serve(string? path, DocsFolder docs, HttpContext context)
    {
        if (docs.Root is null)
        {
            return Results.NotFound();
        }

        // "/docs" and "/docs/" are one route; the guides link to each other relatively, which only resolves under the slash.
        if (!context.Request.Path.Value!.EndsWith('/') && string.IsNullOrEmpty(path))
        {
            return Results.Redirect(DocsFolder.Route);
        }

        string relative = string.IsNullOrEmpty(path) ? "index.md" : path;
        string? file = Resolve(docs.Root, relative);
        if (file is null)
        {
            return Results.NotFound();
        }

        // The same hardening the SVG asset route uses: whatever the browser thinks the bytes are, they are text.
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "private, no-cache";
        return Results.File(file, contentType: "text/plain; charset=utf-8");
    }

    /// <summary>The absolute file for a request path under <paramref name="root"/>, or null when it is not a servable file inside it.</summary>
    public static string? Resolve(string root, string relative)
    {
        if (relative.Contains('\\', StringComparison.Ordinal) || relative.Contains('\0', StringComparison.Ordinal) || relative.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        // Every segment must be a plain name: no "..", no ".", no empty one (a leading or doubled slash would read as an absolute path).
        if (relative.Split('/').Any(segment => segment is ".." or "." or ""))
        {
            return null;
        }

        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal) || !s_extensions.Contains(Path.GetExtension(full)) || !File.Exists(full))
        {
            return null;
        }

        // A link inside the folder that points outside it is a way out of it.
        try
        {
            FileSystemInfo? target = File.ResolveLinkTarget(full, returnFinalTarget: true);
            if (target is not null && !target.FullName.StartsWith(prefix, StringComparison.Ordinal))
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }

        return full;
    }
}
