namespace Draughtsman.Server.Export;

/// <summary>
/// Raised when PDF export is asked for but the optional Chromium component is absent: disabled in configuration, or
/// its browser could not be launched. The message names how to enable it; callers turn it into a 501-style answer.
/// </summary>
public sealed class PdfUnavailableException : Exception
{
    public PdfUnavailableException(string message)
        : base(message)
    {
    }
}
