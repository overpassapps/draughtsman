using System.Text;
using System.Xml;

namespace Draughtsman.Core.Media;

/// <summary>
/// Which image types an asset upload accepts, decided by content and never by file extension (build-spec-v0.1.md
/// section 7). Raster formats are recognised from their magic bytes; SVG is recognised only when it is valid UTF-8
/// text that parses as an XML document whose root element is <c>svg</c>, so a renamed text file is not an image and a
/// deliberately malformed SVG is not served as one. The caller still has to serve an SVG with a restrictive
/// Content-Security-Policy: parsing says what the file is, not that scripts inside it are safe.
/// </summary>
public static class AssetTypes
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Webp = "image/webp";
    public const string Svg = "image/svg+xml";

    /// <summary>Every content type an upload may carry.</summary>
    public static readonly IReadOnlyList<string> Allowed = [Png, Jpeg, Gif, Webp, Svg];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // A UTF-8 string literal cannot spell byte 0x89 (it would encode U+0089 as two bytes), so the PNG signature is a
    // byte array. The other raster signatures are pure ASCII and read as `u8` spans.
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The content type these bytes really are, or null when they are not an accepted image.</summary>
    public static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (IsPng(bytes))
        {
            return Png;
        }

        if (IsJpeg(bytes))
        {
            return Jpeg;
        }

        if (IsGif(bytes))
        {
            return Gif;
        }

        if (IsWebp(bytes))
        {
            return Webp;
        }

        return IsSvg(bytes) ? Svg : null;
    }

    /// <summary>The file extension to store an asset of this content type under, leading dot included.</summary>
    public static string ExtensionFor(string contentType)
    {
        return contentType switch
        {
            Png => ".png",
            Jpeg => ".jpg",
            Gif => ".gif",
            Webp => ".webp",
            Svg => ".svg",
            _ => throw new ArgumentOutOfRangeException(nameof(contentType), contentType, "Not an accepted asset content type."),
        };
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 8 && bytes[..8].SequenceEqual(PngSignature);
    }

    private static bool IsJpeg(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
    }

    private static bool IsGif(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8));
    }

    private static bool IsWebp(ReadOnlySpan<byte> bytes)
    {
        // RIFF....WEBP: the four-byte chunk size between them, so bytes 4-8 are not checked.
        return bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8);
    }

    private static bool IsSvg(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        // A NUL byte means binary, and a parseable XML document cannot contain one; reject before handing it to XML.
        if (text.Contains('\0'))
        {
            return false;
        }

        int first = 0;
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            first = 1;
        }

        while (first < text.Length && char.IsWhiteSpace(text[first]))
        {
            first++;
        }

        // XML documents start with '<'; anything else is not XML, so skip the parser for ordinary text.
        if (first >= text.Length || text[first] != '<')
        {
            return false;
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                // SVG is an element soup by nature; ignore a mismatched namespace or attribute and just find the root.
                IgnoreComments = true,
                IgnoreWhitespace = true,
            });
            return reader.MoveToContent() == XmlNodeType.Element && reader.LocalName == "svg";
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
