using System.Text.Json;

namespace Draughtsman.Sidecar;

/// <summary>Base of every failure the sidecar client reports. Callers that only need to know "it failed" catch this.</summary>
public class SidecarException : Exception
{
    public SidecarException()
    {
    }

    public SidecarException(string message)
        : base(message)
    {
    }

    public SidecarException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The sidecar process is not running and could not be started, or died while the request was in flight. Retryable.</summary>
public sealed class SidecarUnavailableException : SidecarException
{
    public SidecarUnavailableException()
    {
    }

    public SidecarUnavailableException(string message)
        : base(message)
    {
    }

    public SidecarUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The sidecar did not answer within the configured timeout. The process is killed and restarted on the next request.</summary>
public sealed class SidecarTimeoutException : SidecarException
{
    public SidecarTimeoutException()
    {
    }

    public SidecarTimeoutException(string message)
        : base(message)
    {
    }

    public SidecarTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The sidecar answered with a JSON-RPC error: the request was understood and rejected (bad document, unknown method, layout failure).</summary>
public sealed class SidecarRpcException : SidecarException
{
    /// <summary>The payload is not a Draughtsman document the method can work with.</summary>
    public const int InvalidDocumentCode = -32000;

    /// <summary>ELK rejected the graph or failed part way through.</summary>
    public const int LayoutFailedCode = -32001;

    public const int InvalidParamsCode = -32602;

    public SidecarRpcException()
    {
    }

    public SidecarRpcException(string message)
        : base(message)
    {
    }

    public SidecarRpcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SidecarRpcException(int code, string message, JsonElement? data)
        : base(message)
    {
        Code = code;
        Data = data;
    }

    public int Code { get; }

    public new JsonElement? Data { get; }

    /// <summary>True when the fault is in the request (the caller's document or options), not the sidecar.</summary>
    public bool IsClientFault => Code is InvalidDocumentCode or LayoutFailedCode or InvalidParamsCode;
}
