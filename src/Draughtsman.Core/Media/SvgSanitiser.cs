using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace Draughtsman.Core.Media;

/// <summary>Thrown when an uploaded SVG carries a construct the strict allowlist refuses (script, foreignObject, event handler, external reference, DTD).</summary>
public sealed class UnsafeSvgException(string message) : Exception(message);

/// <summary>A validated SVG: the original bytes (validation is rejection-based, so a passing file is never rewritten) and its intrinsic size where the root declares one.</summary>
public sealed record SanitizedSvg(byte[] Content, double? Width, double? Height);

/// <summary>
/// A strict allowlist for uploaded SVG (M3 branding upload). An SVG is a document that can carry script, so a
/// branding logo is accepted only when it uses a known element vocabulary and holds no event handler, external
/// reference, <c>foreignObject</c>, <c>script</c> or DTD. Anything outside the allowlist is rejected rather than
/// rewritten: an admin can see the refusal and supply a clean export. The caller serves a passing file as an image
/// with a restrictive Content-Security-Policy as well (belt and braces), never inlined into this origin's DOM.
/// </summary>
public static partial class SvgSanitiser
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    /// <summary>Elements that may appear in an uploaded SVG. Deliberately narrow: no script, foreignObject, image, animation or external paint server.</summary>
    private static readonly HashSet<string> AllowedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "svg", "g", "defs", "title", "desc", "metadata", "symbol", "use", "switch", "style",
        "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "textpath", "tref",
        "lineargradient", "radialgradient", "stop",
        "clippath", "mask", "pattern", "marker",
        "filter", "fegaussianblur", "feoffset", "feblend", "fecolormatrix", "fecomposite",
        "feflood", "femerge", "femergenode", "femorphology", "feturbulence",
        "fedisplacementmap", "fedropshadow", "fecomponenttransfer", "fefunca", "fefuncr", "fefuncg", "fefuncb",
    };

    // A value this looks like a script, a CSS expression, a binding or an external URL. `url(#id)` (an internal
    // paint server or filter reference) is the one URL form allowed. Also the other CSS functions that fetch a URL
    // (image-set, src, cross-fade). Run on the value with its CSS escapes decoded
    // (DecodeCssEscapes), since CSS reads `u\72l(https://x)` as `url(https://x)`.
    [GeneratedRegex("(?i)(javascript:|vbscript:|expression\\s*\\(|@import|-moz-binding|behavior\\s*:|url\\s*\\(\\s*['\"]?\\s*(?!#)|image-set\\s*\\(|src\\s*\\(|cross-fade\\s*\\()", RegexOptions.None, RegexLimits.TimeoutMilliseconds)]
    private static partial Regex DangerousValue();

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Validates <paramref name="bytes"/> as a safe SVG, returning it unchanged when it passes. Throws
    /// <see cref="UnsafeSvgException"/> for anything the allowlist refuses, and never resolves an external entity or DTD.
    /// </summary>
    public static SanitizedSvg Sanitise(ReadOnlySpan<byte> bytes)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new UnsafeSvgException("The SVG is not valid UTF-8 text.");
        }

        if (text.Contains('\0'))
        {
            throw new UnsafeSvgException("The SVG contains a NUL byte.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = false,
            ConformanceLevel = ConformanceLevel.Document,
        };

        bool rootSeen = false;
        double? width = null;
        double? height = null;
        var stack = new Stack<string>();

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        string name = reader.LocalName;
                        if (!rootSeen)
                        {
                            if (!name.Equals("svg", StringComparison.OrdinalIgnoreCase))
                            {
                                throw new UnsafeSvgException("The root element must be <svg>.");
                            }

                            rootSeen = true;
                            (width, height) = ReadSize(reader);
                        }

                        if (!IsSvgNamespace(reader.NamespaceURI))
                        {
                            throw new UnsafeSvgException($"<{name}> is not in the SVG namespace.");
                        }

                        if (!AllowedElements.Contains(name))
                        {
                            throw new UnsafeSvgException($"<{name}> is not allowed in an uploaded SVG.");
                        }

                        ValidateAttributes(reader, name);
                        stack.Push(name.ToLowerInvariant());
                        if (reader.IsEmptyElement)
                        {
                            stack.Pop();
                        }

                        break;

                    case XmlNodeType.EndElement:
                        stack.Pop();
                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.Whitespace:
                        if (stack.Count > 0 && stack.Peek() == "style")
                        {
                            ValidateStyle(reader.Value);
                        }

                        break;

                    case XmlNodeType.ProcessingInstruction:
                        throw new UnsafeSvgException("Processing instructions are not allowed in an uploaded SVG.");

                    case XmlNodeType.DocumentType:
                        throw new UnsafeSvgException("A DOCTYPE is not allowed in an uploaded SVG.");
                }
            }
        }
        catch (XmlException ex)
        {
            throw new UnsafeSvgException($"The SVG could not be parsed: {ex.Message}");
        }

        if (!rootSeen)
        {
            throw new UnsafeSvgException("The file is not an SVG document.");
        }

        return new SanitizedSvg(bytes.ToArray(), width, height);
    }

    private static bool IsSvgNamespace(string namespaceUri)
    {
        return string.IsNullOrEmpty(namespaceUri) || string.Equals(namespaceUri, SvgNamespace, StringComparison.Ordinal);
    }

    private static void ValidateAttributes(XmlReader reader, string element)
    {
        if (!reader.HasAttributes)
        {
            return;
        }

        for (int i = 0; i < reader.AttributeCount; i++)
        {
            reader.MoveToAttribute(i);
            string local = reader.LocalName;
            string value = reader.Value;

            if (local.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnsafeSvgException($"The event handler attribute \"{local}\" is not allowed on <{element}>.");
            }

            // href/xlink:href may only point inside this same document. Every external reference (http, data, file)
            // is refused, which also rules out <use href="https://..."> pulling in remote script-bearing content.
            if (local.Equals("href", StringComparison.OrdinalIgnoreCase) && !value.TrimStart().StartsWith('#'))
            {
                throw new UnsafeSvgException($"An external reference (\"{value}\") is not allowed in an uploaded SVG.");
            }

            if (IsDangerous(value))
            {
                throw new UnsafeSvgException($"The attribute \"{local}\" contains a script, binding or external URL.");
            }
        }

        reader.MoveToElement();
    }

    private static void ValidateStyle(string css)
    {
        if (IsDangerous(css))
        {
            throw new UnsafeSvgException("A <style> block contains a script, binding or external URL.");
        }
    }

    /// <summary>True when the text, as CSS reads it once its escapes are decoded, holds something <see cref="DangerousValue"/> matches. Only the decoded form is judged: it is what the browser sees, and the spelling of an escape (<c>url(\23 grad)</c>) says nothing about it.</summary>
    private static bool IsDangerous(string text)
    {
        return DangerousValue().IsMatch(text.Contains('\\', StringComparison.Ordinal) ? DecodeCssEscapes(text) : text);
    }

    /// <summary>
    /// Decodes CSS escapes the way a stylesheet parser does: a backslash and 1 to 6 hex digits (and the one white-space
    /// character that may end them) is that code point, a backslash before a newline is a line continuation and vanishes, and a
    /// backslash before anything else is that character. Linear in the text. The decoded form is only ever tested, never
    /// stored or served: the file that passes is the original, unchanged.
    /// </summary>
    internal static string DecodeCssEscapes(string css)
    {
        var decoded = new StringBuilder(css.Length);
        for (int i = 0; i < css.Length; i++)
        {
            char c = css[i];
            if (c != '\\')
            {
                decoded.Append(c);
                continue;
            }

            if (i + 1 >= css.Length)
            {
                break; // a lone trailing backslash
            }

            char next = css[i + 1];
            if (next is '\n' or '\r' or '\f')
            {
                i += next == '\r' && i + 2 < css.Length && css[i + 2] == '\n' ? 2 : 1;
                continue;
            }

            if (!Uri.IsHexDigit(next))
            {
                decoded.Append(next);
                i++;
                continue;
            }

            int digits = 0;
            int codePoint = 0;
            int j = i + 1;
            while (j < css.Length && digits < 6 && Uri.IsHexDigit(css[j]))
            {
                codePoint = (codePoint * 16) + Convert.ToInt32(css[j].ToString(), 16);
                digits++;
                j++;
            }

            if (j < css.Length && css[j] is ' ' or '\t' or '\n' or '\f')
            {
                j++;
            }
            else if (j + 1 < css.Length && css[j] == '\r' && css[j + 1] == '\n')
            {
                j += 2;
            }
            else if (j < css.Length && css[j] == '\r')
            {
                j++;
            }

            decoded.Append(codePoint is 0 or > 0x10FFFF || (codePoint >= 0xD800 && codePoint <= 0xDFFF) ? '\uFFFD' : char.ConvertFromUtf32(codePoint));
            i = j - 1;
        }

        return decoded.ToString();
    }

    /// <summary>Reads the root's viewBox, or failing that its width/height, as the intrinsic size. Percentage and unknown units are ignored.</summary>
    private static (double? Width, double? Height) ReadSize(XmlReader reader)
    {
        string? viewBox = reader.GetAttribute("viewBox");
        if (viewBox is not null)
        {
            string[] parts = viewBox.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4
                && double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out double w)
                && double.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out double h)
                && w > 0 && h > 0)
            {
                return (w, h);
            }
        }

        return (ParseLength(reader.GetAttribute("width")), ParseLength(reader.GetAttribute("height")));
    }

    private static double? ParseLength(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string trimmed = raw.Trim().TrimEnd('%');
        if (raw.Trim().EndsWith('%'))
        {
            return null; // a percentage is relative to the embedding context, not an intrinsic pixel size.
        }

        return double.TryParse(trimmed, System.Globalization.CultureInfo.InvariantCulture, out double value) && value > 0
            ? value
            : null;
    }
}
