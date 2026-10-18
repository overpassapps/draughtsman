namespace Draughtsman.Server.Thumbnails;

/// <summary>
/// What a thumbnail is allowed to cost (docs/api.md <c>GET /api/documents/{id}/thumbnail</c>, "Large diagrams"). A document's size is
/// counted as nodes plus edges, the two things the renderer does work for; the cut-offs are those of the limits table in the
/// docs, and the cache bounds are what keeps a documents page of fifty diagrams from rendering all of them again on each visit.
/// A record so a test can shrink them to exercise each tier without building a huge document.
/// </summary>
/// <param name="FullMaxElements">Up to this many nodes and edges a thumbnail is the whole picture (labels, routed connectors, shape detail).</param>
/// <param name="SimplifiedMaxElements">Above <paramref name="FullMaxElements"/> and up to this many, boxes and lines only; above it, a neutral placeholder.</param>
/// <param name="FullMaxSvgBytes">A full drawing larger than this (UTF-8 bytes) is replaced by the simplified one: a thumbnail must stay small enough to send fifty of at once.</param>
/// <param name="CacheEntries">Most finished thumbnails the in-memory cache holds.</param>
/// <param name="CacheBytes">Most bytes of finished thumbnails the in-memory cache holds, whichever of the two bounds is reached first.</param>
public sealed record ThumbnailLimits(
    int FullMaxElements = 400,
    int SimplifiedMaxElements = 6000,
    int FullMaxSvgBytes = 262_144,
    int CacheEntries = 200,
    long CacheBytes = 20L * 1024 * 1024);
