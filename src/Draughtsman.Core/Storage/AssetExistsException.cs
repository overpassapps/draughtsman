namespace Draughtsman.Core.Storage;

/// <summary>Thrown by <see cref="IStore.CreateAsset"/> when an asset with the same content hash is already stored.</summary>
public sealed class AssetExistsException(string hash)
    : Exception($"An asset with content hash '{hash}' already exists.")
{
    public string Hash { get; } = hash;
}
