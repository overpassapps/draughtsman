using System.Globalization;
using System.Xml;

namespace Draughtsman.Core.Import.Visio;

/// <summary>
/// Walking an <see cref="XmlReader"/> without ever trusting its size: every element the walk steps onto is counted against the import's
/// <see cref="VisioBudget"/> (elements, depth, attributes), and anything not wanted is skipped by a loop that counts too, rather than by
/// <see cref="XmlReader.Skip"/>, which would read an element of any size and depth without telling anyone.
/// </summary>
internal static class VisioXml
{
    /// <summary>
    /// Calls <paramref name="onChild"/> with the name of each child element of the one the reader is on, the reader standing on that child's start tag.
    /// The callback must consume the child (read it with its own loop or call <see cref="Skip"/>), leaving the reader on the node after it. On
    /// return the reader is on the node after the parent.
    /// </summary>
    public static void Children(XmlReader reader, VisioBudget budget, Action<string> onChild)
    {
        int depth = reader.Depth;
        bool empty = reader.IsEmptyElement;
        reader.Read();
        if (empty)
        {
            return;
        }

        while (!reader.EOF && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                budget.Element(reader);
                onChild(reader.LocalName);
            }
            else
            {
                reader.Read();
            }
        }

        // On the parent's end tag now (or at the end of a document whose XmlReader will throw for being cut short).
        reader.Read();
    }

    /// <summary>Skips the element the reader is on and everything in it, counting as it goes; the reader ends on the node after it.</summary>
    public static void Skip(XmlReader reader, VisioBudget budget)
    {
        int depth = reader.Depth;
        bool empty = reader.IsEmptyElement;
        reader.Read();
        if (empty)
        {
            return;
        }

        while (!reader.EOF && reader.Depth > depth)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                budget.Element(reader);
            }

            reader.Read();
        }

        reader.Read();
    }

    /// <summary>The cell value as a finite number (invariant culture), or null when it is missing, not a number, or infinite.</summary>
    public static double? Number(string? text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
    }

    /// <summary>True for a Visio boolean written as a number or a word: <c>1</c> or <c>TRUE</c>.</summary>
    public static bool IsOn(string? text)
    {
        return text is "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
    }
}
