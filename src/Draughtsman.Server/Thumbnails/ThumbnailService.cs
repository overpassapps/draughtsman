using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Draughtsman.Core.Enums;
using Draughtsman.Core.Models;
using Draughtsman.Core.Storage;
using Draughtsman.Mcp.Rendering;
using Draughtsman.Server.Hosting;
using Draughtsman.Sidecar;

namespace Draughtsman.Server.Thumbnails;

/// <summary>
/// Draws, caches and rations the document thumbnails (<c>GET /api/documents/{id}/thumbnail</c>). Three things keep it cheap:
/// a finished thumbnail is kept in a bounded in-memory cache under (document, version, variant), so a documents page of fifty
/// diagrams renders none of them on a repeat visit; concurrent requests for the same thumbnail share one render; and every render
/// runs under a semaphore (<c>limits.thumbnailConcurrency</c>), so a cold page of fifty queues behind two renders at a time rather
/// than starting fifty. A diagram too large to draw in full is drawn as boxes and lines, or as a placeholder
/// (<see cref="ThumbnailLimits"/>), and a failure of the layout engine is a placeholder that is not cached, never an error: a
/// documents page must still load.
/// <para>
/// The <em>variant</em> is what the picture depends on besides the document: the organisation's default diagram theme and this build
/// (the drawing can change with a release). It goes into the cache key and the ETag, which is therefore the document's version tag
/// followed by the variant, so a browser holding a thumbnail revalidates it after a theme change or an upgrade.
/// </para>
/// </summary>
public sealed class ThumbnailService
{
    /// <summary>
    /// Bumped when what the thumbnail renderer draws changes without the build changing (the build's version and date are already
    /// part of the variant, so a release invalidates every cached thumbnail by itself; this is for a development build).
    /// </summary>
    public const int Revision = 1;

    private static readonly string BuildIdentity = BuildIdentityOf(BuildInfo.FromAssembly());

    private readonly ThumbnailLimits _limits;
    private readonly ThumbnailCache _cache;
    private readonly SemaphoreSlim _slots;
    private readonly ILogger<ThumbnailService> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<Thumbnail?>> _inFlight = new(StringComparer.Ordinal);
    private long _renders;

    public ThumbnailService(ThumbnailLimits limits, int concurrency, ILogger<ThumbnailService> logger)
    {
        _limits = limits;
        _cache = new ThumbnailCache(limits.CacheEntries, limits.CacheBytes);
        // 0 turns the limit off, as for every other limit (docs/draughtsman.example.yaml).
        _slots = new SemaphoreSlim(concurrency > 0 ? concurrency : int.MaxValue);
        _logger = logger;
    }

    /// <summary>How many thumbnails have been drawn (not served from the cache): the count a test asserts instead of a time.</summary>
    public long Renders => Interlocked.Read(ref _renders);

    public ThumbnailCache Cache => _cache;

    /// <summary>What the picture depends on besides the document, as a short token for the cache key and the ETag.</summary>
    public static string VariantOf(DiagramThemeChoice? theme)
    {
        var text = new StringBuilder();
        text.Append(Revision.ToString(CultureInfo.InvariantCulture)).Append('|').Append(BuildIdentity).Append('|');
        if (theme is not null)
        {
            text.Append(theme.Base).Append('|').Append(theme.ExportBackground).Append('|');
            foreach (KeyValuePair<string, string> colour in theme.Palette.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                text.Append(colour.Key).Append('=').Append(colour.Value).Append(';');
            }
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>The cached thumbnail for exactly this version and variant, with no work done; null when there is none.</summary>
    public Thumbnail? TryGetCached(string id, string version, string variant)
    {
        return _cache.TryGet(ThumbnailCache.KeyOf(id, version, variant));
    }

    /// <summary>
    /// The thumbnail of document <paramref name="id"/>: from the cache, from a render another request already has in hand, or
    /// drawn now under the concurrency limit. <paramref name="expectedVersion"/> is the version the caller saw a moment ago; the
    /// document is read afresh, and the thumbnail carries the version actually drawn. Null when the document is gone.
    /// </summary>
    public async Task<Thumbnail?> GetAsync(string id, string expectedVersion, DiagramThemeChoice? theme, Func<CancellationToken, Task<StoredDocument?>> load, ISidecarClient sidecar, CancellationToken cancellationToken)
    {
        string variant = VariantOf(theme);
        string key = ThumbnailCache.KeyOf(id, expectedVersion, variant);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_cache.TryGet(key) is { } cached)
            {
                return cached;
            }

            var mine = new TaskCompletionSource<Thumbnail?>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<Thumbnail?>? theirs;
            lock (_gate)
            {
                if (!_inFlight.TryGetValue(key, out theirs))
                {
                    _inFlight[key] = mine;
                }
            }

            if (theirs is not null)
            {
                try
                {
                    return await theirs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The request that was drawing it went away before it finished: take the work over.
                    continue;
                }
            }

            try
            {
                Thumbnail? result = await DrawAsync(id, variant, theme, load, sidecar, cancellationToken).ConfigureAwait(false);
                if (result is { Degraded: false })
                {
                    _cache.Set(id, ThumbnailCache.KeyOf(id, result.Version, variant), result);
                }

                mine.TrySetResult(result);
                return result;
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight.Remove(key);
                }

                mine.TrySetCanceled();
            }
        }
    }

    private async Task<Thumbnail?> DrawAsync(string id, string variant, DiagramThemeChoice? theme, Func<CancellationToken, Task<StoredDocument?>> load, ISidecarClient sidecar, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StoredDocument? stored = await load(cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return null;
            }

            Interlocked.Increment(ref _renders);
            long started = Stopwatch.GetTimestamp();
            Thumbnail thumbnail = await DrawDocumentAsync(stored, variant, theme, sidecar, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Thumbnail of {DocumentId} drawn as {Kind} in {Milliseconds:F0} ms", id, thumbnail.Kind, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return thumbnail;
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Draws one document: the tier its size allows, with the layout engine used only where the picture needs it.</summary>
    internal async Task<Thumbnail> DrawDocumentAsync(StoredDocument stored, string variant, DiagramThemeChoice? theme, ISidecarClient sidecar, CancellationToken cancellationToken)
    {
        Document document = stored.Document;
        int elements = document.Nodes.Count + document.Edges.Count;
        try
        {
            if (elements == 0)
            {
                return Placeholder(stored, variant, theme, ThumbnailKind.Empty, ThumbnailPlaceholder.Empty);
            }

            if (elements > _limits.SimplifiedMaxElements)
            {
                return Placeholder(stored, variant, theme, ThumbnailKind.TooLarge, ThumbnailPlaceholder.TooLarge);
            }

            // A sequence diagram stores no positions: its picture comes from the scene the layout engine derives, and there is no
            // box-and-line form of it, so a large one is a placeholder. Any other type with nodes still lacking a position would
            // need a layout, which is not worth running over a large diagram for a thumbnail.
            bool sequence = document.Type == DocumentType.Sequence;
            bool needsLayout = !sequence && document.Nodes.Any(node => node.Geometry is null);
            if (elements > _limits.FullMaxElements)
            {
                return sequence || needsLayout
                    ? Placeholder(stored, variant, theme, ThumbnailKind.TooLarge, ThumbnailPlaceholder.TooLarge)
                    : Make(stored, variant, ThumbnailKind.Simplified, SvgRenderer.RenderThumbnail(document, ThumbnailDetail.Simplified, theme: theme).Svg, degraded: false);
            }

            if (needsLayout)
            {
                try
                {
                    document = (await sidecar.LayoutAsync(document, null, cancellationToken).ConfigureAwait(false)).Document;
                }
                catch (SidecarException ex)
                {
                    _logger.LogWarning(ex, "A thumbnail of {DocumentId} could not lay the document out", document.Id);
                    return Placeholder(stored, variant, theme, ThumbnailKind.Unavailable, ThumbnailPlaceholder.Unavailable, degraded: true);
                }
            }

            IReadOnlyDictionary<string, IReadOnlyList<Point>>? routes = null;
            SequenceScene? scene = null;
            bool degraded = false;
            try
            {
                if (sequence)
                {
                    scene = await sidecar.SequenceSceneAsync(document, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    routes = await ConnectorRouting.RouteAsync(sidecar, document, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (SidecarException ex)
            {
                _logger.LogWarning(ex, "A thumbnail of {DocumentId} could not use the layout engine", document.Id);
                if (sequence)
                {
                    return Placeholder(stored, variant, theme, ThumbnailKind.Unavailable, ThumbnailPlaceholder.Unavailable, degraded: true);
                }

                // Connectors with no route draw straight, which is a worse picture, not a missing one; it is tried again next time.
                degraded = true;
            }

            string svg = SvgRenderer.RenderThumbnail(document, ThumbnailDetail.Full, routes, scene, theme).Svg;
            if (Encoding.UTF8.GetByteCount(svg) > _limits.FullMaxSvgBytes)
            {
                return sequence
                    ? Placeholder(stored, variant, theme, ThumbnailKind.TooLarge, ThumbnailPlaceholder.TooLarge)
                    : Make(stored, variant, ThumbnailKind.Simplified, SvgRenderer.RenderThumbnail(document, ThumbnailDetail.Simplified, theme: theme).Svg, degraded);
            }

            return Make(stored, variant, ThumbnailKind.Full, svg, degraded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A thumbnail is decoration: whatever the renderer or the layout engine did (an id used twice, say), the documents page
            // still loads. It is not cached, so the next request tries again; a document that always fails costs one cheap attempt each.
            _logger.LogWarning(ex, "A thumbnail of {DocumentId} could not be drawn", document.Id);
            return Placeholder(stored, variant, theme, ThumbnailKind.Unavailable, ThumbnailPlaceholder.Unavailable, degraded: true);
        }
    }

    private static Thumbnail Placeholder(StoredDocument stored, string variant, DiagramThemeChoice? theme, ThumbnailKind kind, ThumbnailPlaceholder placeholder, bool degraded = false)
    {
        return Make(stored, variant, kind, SvgRenderer.RenderThumbnailPlaceholder(placeholder, theme), degraded);
    }

    private static Thumbnail Make(StoredDocument stored, string variant, ThumbnailKind kind, string svg, bool degraded)
    {
        return new Thumbnail(Encoding.UTF8.GetBytes(svg), stored.Version, variant, kind, degraded);
    }

    private static string BuildIdentityOf(BuildInfo build)
    {
        return build.Version + "@" + build.BuildDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
