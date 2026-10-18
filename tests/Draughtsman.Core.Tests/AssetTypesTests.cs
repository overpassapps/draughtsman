using System.Text;
using Draughtsman.Core.Media;

namespace Draughtsman.Core.Tests;

/// <summary>
/// Asset content sniffing (build-spec-v0.1.md section 7): an accepted image is recognised from its bytes and a file
/// that merely claims to be one is not. Asserting both directions keeps the detector from passing by accepting
/// everything, and the renamed-text case is the exact mistake an extension check would make.
/// </summary>
public class AssetTypesTests
{
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46];
    private static readonly byte[] GifHeader = Encoding.ASCII.GetBytes("GIF89a");
    private static byte[] WebpHeader() => [.. "RIFF"u8, 0x1A, 0x00, 0x00, 0x00, .. "WEBP"u8, 0x56, 0x50, 0x38, 0x20];

    private const string Svg = "<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>";
    private const string SvgWithLeadingWhitespace = "\n  <svg xmlns=\"http://www.w3.org/2000/svg\"><circle r=\"4\"/></svg>";

    public static TheoryData<byte[], string> Accepted => new()
    {
        { [.. PngHeader, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52], AssetTypes.Png },
        { JpegHeader, AssetTypes.Jpeg },
        { [.. GifHeader, 0x01, 0x00, 0x01], AssetTypes.Gif },
        { WebpHeader(), AssetTypes.Webp },
        { Encoding.UTF8.GetBytes(Svg), AssetTypes.Svg },
        { Encoding.UTF8.GetBytes(SvgWithLeadingWhitespace), AssetTypes.Svg },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Detects_each_accepted_type_from_its_content(byte[] bytes, string expected)
    {
        string? detected = AssetTypes.Detect(bytes);

        Assert.Equal(expected, detected);
        // The content type is one the upload endpoint admits, and it has a stored extension.
        Assert.Contains(detected, AssetTypes.Allowed);
        Assert.StartsWith(".", AssetTypes.ExtensionFor(expected));
    }

    [Theory]
    [InlineData("this is not an image, it is a note\r\n")]
    [InlineData("<!DOCTYPE html><html><body>hi</body></html>")]
    [InlineData("<?xml version=\"1.0\"?><rss><channel/></rss>")]
    [InlineData("{ \"assetId\": \"asset_1\" }")]
    public void Does_not_detect_a_non_image(string text)
    {
        Assert.Null(AssetTypes.Detect(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Recognises_an_svg_that_carries_a_script_as_an_svg()
    {
        // It is a real image; the endpoint serves it with a restrictive CSP rather than refusing it, so the detector
        // must still call it an SVG (a rejection here would be the wrong side of the property).
        byte[] bytes = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

        Assert.Equal(AssetTypes.Svg, AssetTypes.Detect(bytes));
    }

    [Fact]
    public void Empty_and_binary_input_are_not_images()
    {
        Assert.Null(AssetTypes.Detect([]));
        Assert.Null(AssetTypes.Detect([0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE]));
    }

    [Fact]
    public void An_invalid_utf8_byte_stream_is_not_an_svg()
    {
        // 0xFF is never valid UTF-8; even though it starts with '<svg' as ASCII, the bytes cannot be text.
        byte[] bytes = [.. "<svg"u8, 0xFF, 0xFE, (byte)'>'];

        Assert.Null(AssetTypes.Detect(bytes));
    }
}
