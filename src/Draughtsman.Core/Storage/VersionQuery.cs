namespace Draughtsman.Core.Storage;

/// <summary>
/// Paged version listing, newest first. Mirrors <see cref="DocumentQuery"/> so both listings share one paging
/// vocabulary; versions have no search filter, so only offset and limit apply.
/// </summary>
public sealed record VersionQuery(int Offset = 0, int Limit = VersionQuery.DefaultLimit)
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;
}
