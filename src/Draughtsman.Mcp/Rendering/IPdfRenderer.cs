namespace Draughtsman.Mcp.Rendering;

/// <summary>
/// Renders a document's SVG to a single-page PDF through headless Chromium (build-spec-v0.1.md section 7). The
/// component is optional and separately installed: a host that has not wired it up, or whose browser is missing,
/// reports unavailable, and every caller gates PDF on that signal rather than catching a missing binary. The SVG is the
/// one <see cref="SvgRenderer"/> produces and is already laid out and theme-resolved by the caller; this interface only
/// turns SVG text into PDF bytes.
/// </summary>
public interface IPdfRenderer
{
    /// <summary>Whether the component is enabled in configuration and any configured executable exists: a cheap check that does not launch a browser, so true does not promise a launch works (use <see cref="ProbeAsync"/> to report availability).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Whether a browser launch has actually succeeded, probed lazily and cached for a short time. This is what health
    /// and the editor's menu report, so they never offer a PDF export that would answer 501.
    /// </summary>
    Task<PdfStatus> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>Renders the SVG as a single-page PDF with vector text. Throws when the browser cannot be reached.</summary>
    Task<byte[]> RenderAsync(string svg, PdfOptions? options, CancellationToken cancellationToken);

    /// <summary>Renders with the default options (the page cut to the drawing).</summary>
    Task<byte[]> RenderAsync(string svg, CancellationToken cancellationToken) => RenderAsync(svg, null, cancellationToken);
}
