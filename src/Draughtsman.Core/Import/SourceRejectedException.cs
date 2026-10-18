using System.Text.RegularExpressions;

namespace Draughtsman.Core.Import;

/// <summary>
/// The importer refused the source itself: one step of reading it ran past its limit (a regular expression hit its match
/// timeout, <see cref="RegexLimits"/>: input that no real diagram or schema looks like, or, rarely, a host too busy to
/// run the thread, which is why the message does not claim to know which), or the source is not the format at all, or it crosses a
/// size limit (the draw.io importer's decompression, element and depth limits) or carries a construct that is refused outright (a DOCTYPE). Distinct from an
/// <see cref="ArgumentException"/>, which means the caller asked for the wrong thing; callers report this one as a
/// refusal of the source itself (HTTP 422, an MCP error).
/// </summary>
public sealed class SourceRejectedException : Exception
{
    public SourceRejectedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    /// <summary>Runs an import, turning a regex timeout into the refusal <paramref name="format"/>'s callers report.</summary>
    internal static T Guard<T>(string format, Func<T> import)
    {
        try
        {
            return import();
        }
        catch (RegexMatchTimeoutException ex)
        {
            throw new SourceRejectedException($"The {format} source took too long to read; try again, or split the source into smaller parts.", ex);
        }
    }
}
