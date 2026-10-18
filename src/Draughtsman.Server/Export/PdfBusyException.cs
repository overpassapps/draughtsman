namespace Draughtsman.Server.Export;

/// <summary>
/// Raised when every PDF render slot stayed taken for the whole wait (<see cref="PdfRenderGate"/>): the component is
/// there, it is busy. Callers answer 429 (HTTP) or an error a client may retry (MCP), never "not available".
/// </summary>
public sealed class PdfBusyException : Exception
{
    public PdfBusyException()
        : base("PDF export is busy with other renders; try again shortly.")
    {
    }
}
