namespace Draughtsman.Server.Thumbnails;

/// <summary>
/// A bounded, thread-safe, in-memory cache of finished thumbnails, least recently used out first, held to both an entry count and a
/// byte total (whichever is reached first). The key is the document id, its version and the thumbnail variant (the organisation theme
/// and the renderer), so an edit, a theme change or an upgrade simply asks for a key that is not there; nothing is invalidated by
/// hand. A document keeps at most one entry, so the superseded one is dropped the moment its replacement arrives rather than waiting
/// to be pushed out. Nothing here is persisted: a restart renders again, which is the cost of the "no storage change" decision.
/// </summary>
public sealed class ThumbnailCache
{
    private readonly object _gate = new();
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly LinkedList<Entry> _recent = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _keyOfDocument = new(StringComparer.Ordinal);
    private long _bytes;

    public ThumbnailCache(int maxEntries, long maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byKey.Count;
            }
        }
    }

    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    public static string KeyOf(string documentId, string version, string variant)
    {
        // NUL cannot appear in an id, a version tag or a variant, so no two different triples share a key.
        return string.Concat(documentId, "\0", version, "\0", variant);
    }

    /// <summary>The thumbnail cached under <paramref name="key"/>, which becomes the most recently used; null when there is none.</summary>
    public Thumbnail? TryGet(string key)
    {
        lock (_gate)
        {
            if (!_byKey.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                return null;
            }

            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Thumbnail;
        }
    }

    /// <summary>
    /// Adds a thumbnail as the most recently used, dropping the same document's earlier entry and then the least recently used
    /// ones until both bounds hold. A thumbnail larger than the whole byte bound is not kept (it would only push everything out).
    /// </summary>
    public void Set(string documentId, string key, Thumbnail thumbnail)
    {
        long size = thumbnail.Svg.LongLength;
        lock (_gate)
        {
            Remove(key);
            if (_keyOfDocument.TryGetValue(documentId, out string? previous))
            {
                Remove(previous);
            }

            if (size > _maxBytes)
            {
                return;
            }

            _byKey[key] = _recent.AddFirst(new Entry(documentId, key, thumbnail, size));
            _keyOfDocument[documentId] = key;
            _bytes += size;
            while (_byKey.Count > _maxEntries || _bytes > _maxBytes)
            {
                Remove(_recent.Last!.Value.Key);
            }
        }
    }

    private void Remove(string key)
    {
        if (!_byKey.Remove(key, out LinkedListNode<Entry>? node))
        {
            return;
        }

        _recent.Remove(node);
        _bytes -= node.Value.Size;
        if (_keyOfDocument.TryGetValue(node.Value.DocumentId, out string? current) && current == key)
        {
            _keyOfDocument.Remove(node.Value.DocumentId);
        }
    }

    private sealed record Entry(string DocumentId, string Key, Thumbnail Thumbnail, long Size);
}
