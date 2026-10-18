namespace Draughtsman.Core.Storage;

/// <summary>An asset's bytes and content type, read back through <see cref="IAssetReader"/> for rendering.</summary>
public sealed record AssetContent(string ContentType, byte[] Bytes);

/// <summary>
/// Reads an asset's bytes by id. <see cref="IStore"/> owns the metadata row; the bytes live on disk, so this is the
/// one seam the rest of the application uses to turn a node's <c>props.assetId</c> into a picture without knowing
/// where the data folder is (ADR 0003: no DbContext outside <c>Draughtsman.Storage</c>).
/// </summary>
public interface IAssetReader
{
    /// <summary>The asset's content, or null when the id is unknown or its file is missing.</summary>
    Task<AssetContent?> ReadAsset(string id, CancellationToken cancellationToken = default);
}
