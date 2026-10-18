using Draughtsman.Mcp.Rendering;

namespace Draughtsman.Mcp.Tests;

/// <summary>
/// A fake <see cref="IPdfRenderer"/>: reports <see cref="IsAvailable"/> as the test sets it and, when rendering, returns
/// a deterministic "PDF" byte array rather than launching a browser, so pdf-format tests never need Chromium.
/// </summary>
public sealed class FakePdfRenderer : IPdfRenderer
{
    public bool IsAvailable { get; set; } = true;

    /// <summary>The options of the last render, so a test can see what reached the renderer.</summary>
    public PdfOptions? LastOptions { get; private set; }

    public Task<PdfStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(IsAvailable ? PdfStatus.Ready : new PdfStatus(false, "fake"));
    }

    public Task<byte[]> RenderAsync(string svg, PdfOptions? options, CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        return Task.FromResult("%PDF-1.4 fake"u8.ToArray());
    }
}
