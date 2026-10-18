namespace Draughtsman.Ai;

/// <summary>
/// Thrown by an <see cref="IModelProvider"/> when the model could not be reached or its response could not be
/// turned into a completion. <see cref="Reachable"/> distinguishes a connectivity failure (model unreachable: the
/// caller should say so plainly, per build-spec-v0.1.md section 9) from the model answering with something this
/// provider could not parse.
/// </summary>
public sealed class ModelProviderException : Exception
{
    public ModelProviderException(string message, bool reachable, Exception? innerException = null, int? statusCode = null)
        : base(message, innerException)
    {
        Reachable = reachable;
        StatusCode = statusCode;
    }

    /// <summary>True when the model process/endpoint responded at all; false when it could not be reached.</summary>
    public bool Reachable { get; }

    /// <summary>The HTTP status the provider answered with, or null when it never answered or the failure was not a status.</summary>
    public int? StatusCode { get; }
}
