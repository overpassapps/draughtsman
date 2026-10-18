namespace Draughtsman.Server.Export;

/// <summary>
/// Raised when a PDF render (or the probe that health reports from) did not finish within <c>export.chromium.timeoutSeconds</c>:
/// the browser hung. The browser and its driver were stopped by id before this was thrown, so nothing is left running. It derives from
/// <see cref="InvalidOperationException"/> so the MCP tool, which turns that into a tool error carrying the message, reports it without
/// a special case; the HTTP route answers 504 with the same text.
/// </summary>
public sealed class PdfTimeoutException : InvalidOperationException
{
    public PdfTimeoutException(string message)
        : base(message)
    {
    }
}
