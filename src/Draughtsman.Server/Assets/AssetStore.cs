using System.Security.Cryptography;
using Draughtsman.Core.Media;
using Draughtsman.Core.Storage;
using Draughtsman.Server.Configuration;

namespace Draughtsman.Server.Assets;

/// <summary>Thrown when a file larger than <see cref="AssetConfig.MaxSizeBytes"/> is uploaded.</summary>
public sealed class AssetTooLargeException(long maxSizeBytes)
    : Exception($"The file is larger than the {maxSizeBytes} byte limit.")
{
    public long MaxSizeBytes { get; } = maxSizeBytes;
}

/// <summary>Thrown when storing an upload would take the instance past <see cref="AssetConfig.QuotaBytes"/> (AU-15).</summary>
public sealed class AssetQuotaExceededException(long quotaBytes)
    : Exception($"Storing this image would pass the instance's {quotaBytes} byte asset quota.")
{
    public long QuotaBytes { get; } = quotaBytes;
}

/// <summary>Thrown when an upload's bytes are not one of the accepted image types (see <see cref="AssetTypes"/>).</summary>
public sealed class UnsupportedAssetException(string? message = null)
    : Exception(message ?? "Only PNG, JPEG, GIF, WebP and SVG images are accepted.");

/// <summary>
/// Uploads and reads assets (build-spec-v0.1.md section 7). The metadata row lives behind <see cref="IStore"/>; the
/// bytes live content-addressed on disk under the data folder, so an identical upload deduplicates onto the same row
/// and file. This is the one place that knows both the store and the data folder, which is why reading an asset for
/// rendering goes through <see cref="IAssetReader"/> rather than the store's metadata alone.
/// </summary>
public sealed class AssetStore(IStore store, IStoreMaintenance maintenance, DataFolder dataFolder, DraughtsmanConfig config, ILogger<AssetStore> logger) : IAssetReader
{
    /// <summary>Folder, under the data folder, the asset files are written to.</summary>
    public const string DirectoryName = "assets";

    private const long DefaultMaxSizeBytes = 10 * 1024 * 1024;
    private const int CopyBufferSize = 81920;

    /// <summary>
    /// Validates and stores an upload, returning its metadata. The same bytes uploaded twice return the same asset id;
    /// a file over the configured limit or of an unaccepted type is refused before anything is written.
    /// </summary>
    public async Task<AssetInfo> UploadAsync(Stream content, long? declaredLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        long max = config.Assets.MaxSizeBytes > 0 ? config.Assets.MaxSizeBytes : DefaultMaxSizeBytes;
        if (declaredLength is > 0 && declaredLength > max)
        {
            throw new AssetTooLargeException(max);
        }

        byte[] bytes = await ReadWithinLimit(content, max, cancellationToken);
        string? contentType = AssetTypes.Detect(bytes);
        if (contentType is null)
        {
            throw new UnsupportedAssetException();
        }

        if (contentType == AssetTypes.Svg)
        {
            // An SVG is a document, not a picture: the same allowlist an admin's branding SVG passes (no script, event handler,
            // foreignObject, external reference or DTD, CSS escapes decoded) applies to an image node's asset (AU-17). It is
            // also served sandboxed and only ever drawn through <image>, but inert today is not a reason to store script.
            try
            {
                SvgSanitiser.Sanitise(bytes);
            }
            catch (UnsafeSvgException ex)
            {
                throw new UnsupportedAssetException($"The SVG was refused: {ex.Message} Export a plain SVG (no scripts, embedded pictures or external references) and upload that.");
            }
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        AssetInfo? existing = await store.FindAssetByHash(hash, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        // A new image is what counts against the quota; one already stored returned above at no cost.
        long quota = config.Assets.QuotaBytes;
        if (quota > 0 && await maintenance.TotalAssetBytes(cancellationToken) + bytes.LongLength > quota)
        {
            throw new AssetQuotaExceededException(quota);
        }

        string relative = $"{DirectoryName}/{hash[..2]}/{hash}{AssetTypes.ExtensionFor(contentType)}";
        string absolute = Path.Combine(dataFolder.Path, relative);
        string? directory = Path.GetDirectoryName(absolute);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        // Content-addressed: the target name is decided by the bytes, so a concurrent write of the same content is the
        // same file. Write a temp file and rename it into place, so a reader never sees a half-written image.
        if (!File.Exists(absolute))
        {
            string temp = absolute + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            try
            {
                File.Move(temp, absolute);
            }
            catch (IOException) when (File.Exists(absolute))
            {
                // Another request stored the same content first; its file is the one that stays.
                File.Delete(temp);
            }
        }

        var info = new AssetInfo(StoreIds.NewAssetId(), hash, contentType, bytes.LongLength, relative, DateTimeOffset.UtcNow);
        try
        {
            return await store.CreateAsset(info, cancellationToken);
        }
        catch (AssetExistsException)
        {
            // Lost an insert race; return the row that won so the caller still sees one id per content hash.
            AssetInfo? winner = await store.FindAssetByHash(hash, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }
    }

    public async Task<AssetContent?> ReadAsset(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        AssetInfo? info = await store.GetAsset(id, cancellationToken);
        if (info is null)
        {
            return null;
        }

        string absolute = Path.Combine(dataFolder.Path, info.Path);
        if (!File.Exists(absolute))
        {
            logger.LogWarning("Asset {AssetId} has a row pointing at {Path}, but the file is missing", id, absolute);
            return null;
        }

        return new AssetContent(info.ContentType, await File.ReadAllBytesAsync(absolute, cancellationToken));
    }

    private static async Task<byte[]> ReadWithinLimit(Stream content, long max, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[CopyBufferSize];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > max)
            {
                throw new AssetTooLargeException(max);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
