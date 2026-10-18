namespace Draughtsman.Mcp.Patching;

/// <summary>
/// A <c>patch_document</c> op could not be applied: an unknown op name, a missing or duplicate target id, a field
/// the op does not own, or a malformed value. Always thrown rather than swallowed, per the tool's "fail loudly
/// rather than silently no-op" contract - <c>patch_document</c> is the only way an agent amends a diagram it did
/// not draw, so a target it cannot find must never look like a successful no-op.
/// </summary>
public sealed class PatchException : Exception
{
    public PatchException(string message)
        : base(message)
    {
    }
}
