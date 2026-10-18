using System.Buffers.Binary;

namespace Draughtsman.Core.Media;

/// <summary>Which branding image is being replaced: the primary logo, the light-on-dark logo, or the favicon.</summary>
public enum BrandingSlot
{
    Logo,
    LogoLight,
    Favicon,
}

/// <summary>Why a branding image was refused, so a caller can pick the right HTTP status.</summary>
public enum BrandingImageFailure
{
    /// <summary>Not an accepted type for the slot (a favicon-only ICO, a text file renamed as an image, a dangerous SVG).</summary>
    UnsupportedType,

    /// <summary>Larger than <see cref="BrandingImages.MaxSizeBytes"/>.</summary>
    TooLarge,

    /// <summary>A sensible type but an implausible or malformed pixel size.</summary>
    BadDimensions,
}

/// <summary>Thrown when an uploaded branding image is the wrong type, too large, or the wrong dimensions.</summary>
public sealed class InvalidBrandingImageException(BrandingImageFailure failure, string message) : Exception(message)
{
    public BrandingImageFailure Failure { get; } = failure;
}

/// <summary>An accepted branding image: its bytes, the content type decided by those bytes, and the intrinsic size where it has one.</summary>
public sealed record ValidatedBrandingImage(byte[] Content, string ContentType, int? Width, int? Height);

/// <summary>
/// Validates an uploaded branding image (M3 branding upload). The type is decided by content, never by the file name
/// or the declared Content-Type: a logo may be a PNG or an SVG, a favicon a PNG, SVG or ICO. A file over
/// <see cref="MaxSizeBytes"/> is refused, and so is an image whose intrinsic size is absurd. An SVG is additionally
/// put through <see cref="SvgSanitiser"/>'s strict allowlist, since unlike a raster it is a document that can carry
/// script. Serving is still done as an image with a restrictive policy; this decides what may be stored at all.
/// </summary>
public static class BrandingImages
{
    /// <summary>Largest branding upload accepted, in bytes. Much smaller than a document image: a logo is not a photo.</summary>
    public const long MaxSizeBytes = 512 * 1024;

    /// <summary>Longest side a logo may have, in pixels.</summary>
    public const int MaxDimension = 4096;

    /// <summary>Longest side a favicon may have, in pixels. 256 is the largest size browsers read from an ICO.</summary>
    public const int MaxFaviconDimension = 256;

    /// <summary>Longest product name an admin may store, in characters.</summary>
    public const int MaxProductNameLength = 80;

    public const string Ico = "image/x-icon";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The distinct asset slots, so a caller can enumerate them without hard-coding the list twice.</summary>
    public static readonly IReadOnlyList<BrandingSlot> Slots = [BrandingSlot.Logo, BrandingSlot.LogoLight, BrandingSlot.Favicon];

    /// <summary>
    /// Validates an upload for <paramref name="slot"/>. Throws <see cref="InvalidBrandingImageException"/> with a
    /// human-readable reason when it is refused, and returns the accepted bytes and type otherwise.
    /// </summary>
    public static ValidatedBrandingImage Validate(ReadOnlySpan<byte> bytes, BrandingSlot slot)
    {
        if (bytes.IsEmpty)
        {
            throw new InvalidBrandingImageException(BrandingImageFailure.UnsupportedType, "The file is empty.");
        }

        if (bytes.Length > MaxSizeBytes)
        {
            throw new InvalidBrandingImageException(BrandingImageFailure.TooLarge, $"The file is larger than the {MaxSizeBytes / 1024} KiB limit for a branding image.");
        }

        if (IsPng(bytes))
        {
            (int width, int height) = ReadPngSize(bytes);
            EnforceDimensions(slot, width, height);
            return new ValidatedBrandingImage(bytes.ToArray(), AssetTypes.Png, width, height);
        }

        if (IsIco(bytes))
        {
            if (slot != BrandingSlot.Favicon)
            {
                throw new InvalidBrandingImageException(BrandingImageFailure.UnsupportedType, "An ICO file is only accepted as a favicon.");
            }

            (int width, int height) = ReadIcoSize(bytes);
            EnforceDimensions(slot, width, height);
            return new ValidatedBrandingImage(bytes.ToArray(), Ico, width, height);
        }

        // SVG last: a parseable SVG whose root is <svg> is not the same as a safe one, so it goes through the allowlist.
        if (AssetTypes.Detect(bytes) == AssetTypes.Svg)
        {
            SanitizedSvg svg;
            try
            {
                svg = SvgSanitiser.Sanitise(bytes);
            }
            catch (UnsafeSvgException ex)
            {
                throw new InvalidBrandingImageException(BrandingImageFailure.UnsupportedType, ex.Message);
            }

            if (svg.Width is { } width && svg.Height is { } height)
            {
                EnforceDimensions(slot, (int)Math.Ceiling(width), (int)Math.Ceiling(height));
            }

            return new ValidatedBrandingImage(svg.Content, AssetTypes.Svg, Round(svg.Width), Round(svg.Height));
        }

        throw new InvalidBrandingImageException(BrandingImageFailure.UnsupportedType, slot == BrandingSlot.Favicon
            ? "The favicon must be a PNG, SVG or ICO image."
            : "The logo must be a PNG or SVG image.");
    }

    private static void EnforceDimensions(BrandingSlot slot, int width, int height)
    {
        int max = slot == BrandingSlot.Favicon ? MaxFaviconDimension : MaxDimension;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidBrandingImageException(BrandingImageFailure.BadDimensions, "The image reports a zero or negative size.");
        }

        if (width > max || height > max)
        {
            throw new InvalidBrandingImageException(BrandingImageFailure.BadDimensions, $"The image is {width}×{height}; the maximum for a {(slot == BrandingSlot.Favicon ? "favicon" : "logo")} is {max} pixels on each side.");
        }
    }

    private static int? Round(double? value)
    {
        return value is { } v ? (int)Math.Round(v) : null;
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature);
    }

    /// <summary>ICO: reserved 0, type 1 (icon), a non-zero image count. CUR files (type 2) are not accepted.</summary>
    private static bool IsIco(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 6
            && bytes[0] == 0x00 && bytes[1] == 0x00
            && bytes[2] == 0x01 && bytes[3] == 0x00
            && BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..6]) > 0;
    }

    private static (int Width, int Height) ReadPngSize(ReadOnlySpan<byte> bytes)
    {
        // PNG: 8-byte signature, then the IHDR length/type, then width and height as big-endian int32s.
        if (bytes.Length < 24 || !bytes[12..16].SequenceEqual("IHDR"u8))
        {
            throw new InvalidBrandingImageException(BrandingImageFailure.BadDimensions, "The PNG header is malformed.");
        }

        int width = BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]);
        int height = BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidBrandingImageException(BrandingImageFailure.BadDimensions, "The PNG reports a zero or negative size.");
        }

        return (width, height);
    }

    private static (int Width, int Height) ReadIcoSize(ReadOnlySpan<byte> bytes)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..6]);
        if (bytes.Length < 6 + (count * 16))
        {
            throw new InvalidBrandingImageException(BrandingImageFailure.BadDimensions, "The ICO directory is truncated.");
        }

        int width = 0;
        int height = 0;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + (i * 16);
            // A zero byte means 256 in the ICO format.
            int w = bytes[entry] == 0 ? 256 : bytes[entry];
            int h = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            width = Math.Max(width, w);
            height = Math.Max(height, h);
        }

        return (width, height);
    }
}
