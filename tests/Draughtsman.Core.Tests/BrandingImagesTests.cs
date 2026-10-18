using System.Buffers.Binary;
using System.Text;
using Draughtsman.Core.Media;

namespace Draughtsman.Core.Tests;

/// <summary>
/// Branding upload validation (M3): the type is decided by content, the size and dimensions are bounded, and an SVG
/// goes through the allowlist. Both directions are asserted - a clean image of each accepted type passes and each
/// refusal names its reason.
/// </summary>
public class BrandingImagesTests
{
    private const string CleanSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 40 20\"><rect width=\"40\" height=\"20\"/></svg>";

    [Fact]
    public void A_clean_svg_logo_is_accepted()
    {
        ValidatedBrandingImage image = BrandingImages.Validate(Encoding.UTF8.GetBytes(CleanSvg), BrandingSlot.Logo);

        Assert.Equal(AssetTypes.Svg, image.ContentType);
        Assert.Equal(40, image.Width);
        Assert.Equal(20, image.Height);
    }

    [Fact]
    public void Png_dimensions_are_read()
    {
        ValidatedBrandingImage image = BrandingImages.Validate(Png(240, 120), BrandingSlot.Logo);

        Assert.Equal(AssetTypes.Png, image.ContentType);
        Assert.Equal(240, image.Width);
        Assert.Equal(120, image.Height);
    }

    [Fact]
    public void An_ico_favicon_is_accepted()
    {
        ValidatedBrandingImage image = BrandingImages.Validate(Ico(32, 32), BrandingSlot.Favicon);

        Assert.Equal(BrandingImages.Ico, image.ContentType);
        Assert.Equal(32, image.Width);
    }

    [Fact]
    public void A_script_bearing_svg_is_rejected()
    {
        InvalidBrandingImageException error = Assert.Throws<InvalidBrandingImageException>(() =>
            BrandingImages.Validate(Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"), BrandingSlot.Logo));

        Assert.Equal(BrandingImageFailure.UnsupportedType, error.Failure);
    }

    [Fact]
    public void An_ico_is_refused_as_a_logo()
    {
        InvalidBrandingImageException error = Assert.Throws<InvalidBrandingImageException>(() =>
            BrandingImages.Validate(Ico(32, 32), BrandingSlot.Logo));

        Assert.Equal(BrandingImageFailure.UnsupportedType, error.Failure);
    }

    [Fact]
    public void A_text_file_renamed_as_an_image_is_refused()
    {
        InvalidBrandingImageException error = Assert.Throws<InvalidBrandingImageException>(() =>
            BrandingImages.Validate(Encoding.UTF8.GetBytes("not an image"), BrandingSlot.Logo));

        Assert.Equal(BrandingImageFailure.UnsupportedType, error.Failure);
    }

    [Fact]
    public void A_file_over_the_size_limit_is_refused()
    {
        byte[] big = new byte[BrandingImages.MaxSizeBytes + 1];

        InvalidBrandingImageException error = Assert.Throws<InvalidBrandingImageException>(() => BrandingImages.Validate(big, BrandingSlot.Logo));

        Assert.Equal(BrandingImageFailure.TooLarge, error.Failure);
    }

    [Fact]
    public void A_logo_with_an_absurd_dimension_is_refused()
    {
        InvalidBrandingImageException error = Assert.Throws<InvalidBrandingImageException>(() =>
            BrandingImages.Validate(Png(BrandingImages.MaxDimension + 1, 10), BrandingSlot.Logo));

        Assert.Equal(BrandingImageFailure.BadDimensions, error.Failure);
    }

    [Fact]
    public void A_favicon_larger_than_256_is_refused()
    {
        InvalidBrandingImageException error = Assert.Throws<InvalidBrandingImageException>(() =>
            BrandingImages.Validate(Png(512, 512), BrandingSlot.Favicon));

        Assert.Equal(BrandingImageFailure.BadDimensions, error.Failure);
    }

    /// <summary>A bare signature is not a PNG: without IHDR the size cannot be trusted, so it is refused rather than waved through.</summary>
    [Fact]
    public void A_png_without_an_ihdr_is_refused()
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

        InvalidBrandingImageException error = Assert.Throws<InvalidBrandingImageException>(() => BrandingImages.Validate(bytes, BrandingSlot.Logo));

        Assert.Equal(BrandingImageFailure.BadDimensions, error.Failure);
    }

    private static byte[] Png(int width, int height)
    {
        byte[] bytes = new byte[24];
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }

    private static byte[] Ico(int width, int height)
    {
        byte[] bytes = new byte[22];
        bytes[2] = 0x01; // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 1);
        bytes[6] = (byte)(width == 256 ? 0 : width);
        bytes[7] = (byte)(height == 256 ? 0 : height);
        return bytes;
    }
}
