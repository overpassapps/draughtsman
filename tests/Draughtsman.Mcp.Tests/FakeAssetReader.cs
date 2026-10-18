using Draughtsman.Core.Storage;

namespace Draughtsman.Mcp.Tests;

/// <summary>An in-memory <see cref="IAssetReader"/> so render tests can supply an image's bytes without a data folder.</summary>
public sealed class FakeAssetReader : IAssetReader
{
    private readonly Dictionary<string, AssetContent> _assets = new(StringComparer.Ordinal);

    public void Add(string id, string contentType, byte[] bytes)
    {
        _assets[id] = new AssetContent(contentType, bytes);
    }

    public Task<AssetContent?> ReadAsset(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_assets.GetValueOrDefault(id));
    }
}
