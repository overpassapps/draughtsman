using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Draughtsman.Core.Import.Visio;

/// <summary>One <c>Relationship</c> of an OPC <c>.rels</c> part. An external one (a link to another machine) is read as text and never followed.</summary>
internal sealed record VisioRelationship(string Id, string Type, string Target, bool External);

/// <summary>What one import has used so far, shared by every part it reads so the limits are on the import, not on each part.</summary>
internal sealed class VisioBudget
{
    public int Elements { get; private set; }

    public long InflatedBytes { get; private set; }

    /// <summary>Counts the element the reader is on against the element and depth limits. Call it for every element the reader steps onto.</summary>
    public void Element(XmlReader reader)
    {
        if (++Elements > VisioLimits.MaxElements)
        {
            throw VisioPackage.Reject($"The Visio file has more than {VisioLimits.MaxElements.ToString("N0", CultureInfo.InvariantCulture)} XML elements, which is more than Draughtsman reads.");
        }

        if (reader.Depth >= VisioLimits.MaxDepth)
        {
            throw VisioPackage.Reject($"The Visio file nests XML more than {VisioLimits.MaxDepth.ToString(CultureInfo.InvariantCulture)} levels deep, which no drawing does.");
        }

        if (reader.AttributeCount > VisioLimits.MaxAttributesPerElement)
        {
            throw VisioPackage.Reject($"An element in the Visio file has more than {VisioLimits.MaxAttributesPerElement.ToString(CultureInfo.InvariantCulture)} attributes, which is more than Draughtsman reads.");
        }
    }

    /// <summary>Counts <paramref name="count"/> more inflated bytes against the total.</summary>
    public void Inflated(int count)
    {
        InflatedBytes += count;
        if (InflatedBytes > VisioLimits.MaxInflatedBytes)
        {
            throw VisioPackage.Reject($"The Visio file expands to more than {(VisioLimits.MaxInflatedBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MiB when its parts are read, which is more than Draughtsman reads.");
        }
    }
}

/// <summary>
/// A .vsdx file as an Open Packaging Convention package: a ZIP whose entries are XML parts joined by relationships. The file is untrusted, so
/// opening it is a series of refusals before a single part is read: a file over <see cref="VisioLimits.MaxFileBytes"/>, a ZIP with too many entries,
/// an entry whose name could leave the package when extracted (an absolute name, a drive letter, a <c>..</c> segment), two entries of one name (two
/// readers could pick different ones), an encrypted entry, an entry whose declared size is past a limit or larger than the file itself, and a total
/// of declared sizes past a limit. Reading is then through a stream that counts what actually comes out of the decompressor against the same
/// limits and, at the end of each part, checks that it is exactly as long as the ZIP said and has the CRC the ZIP said, so a header that lies
/// about its size (in either direction) refuses the file rather than being believed. Nothing is ever extracted to disk or fetched.
/// </summary>
internal sealed class VisioPackage : IDisposable
{
    private const string PackageRelationships = "_rels/.rels";

    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly VisioBudget _budget;

    private VisioPackage(ZipArchive archive, VisioBudget budget)
    {
        _archive = archive;
        _budget = budget;
    }

    /// <exception cref="SourceRejectedException">Not a ZIP, a damaged ZIP, or one that crosses a limit or has a hostile entry.</exception>
    public static VisioPackage Open(byte[] file, VisioBudget budget)
    {
        CheckFile(file);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(file, writable: false), ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: null);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or ArgumentException)
        {
            throw Reject($"The Visio file is damaged or cut short, so it cannot be read as a ZIP package ({Shorten(ex.Message, 120)}). Open it in Visio and save it again, or export it again from the tool that made it.", ex);
        }

        var package = new VisioPackage(archive, budget);
        try
        {
            package.Index(file.Length);
            return package;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _archive.Dispose();
    }

    public bool Has(string partName)
    {
        return _entries.ContainsKey(partName);
    }

    /// <summary>
    /// Reads a part as XML through <paramref name="read"/>, which is given a reader on the part with a DOCTYPE refused, no resolver and every
    /// limit counting. The part is read to its end and checked against the ZIP's own size and CRC even if <paramref name="read"/> stopped early.
    /// </summary>
    public void ReadXml(string partName, string label, Action<XmlReader> read)
    {
        ZipArchiveEntry entry = _entries[partName];
        using var stream = VisioInflatedStream.Of(entry, _budget, label);
        try
        {
            using XmlReader reader = XmlReader.Create(stream, CreateSettings());
            read(reader);
        }
        catch (XmlException ex)
        {
            throw Reject(Describe(ex, label), ex);
        }

        stream.Complete();
    }

    /// <summary>The relationships of <paramref name="sourcePart"/> (the empty string for the package itself), or none when it has no <c>.rels</c> part.</summary>
    public IReadOnlyList<VisioRelationship> Relationships(string sourcePart)
    {
        string relsPart = sourcePart.Length == 0 ? PackageRelationships : RelsPartOf(sourcePart);
        var relationships = new List<VisioRelationship>();
        if (!Has(relsPart))
        {
            return relationships;
        }

        ReadXml(relsPart, $"The relationships of {Shorten(sourcePart.Length == 0 ? "the package" : sourcePart, 80)}", reader =>
        {
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "Relationships")
            {
                throw Reject($"{relsPart} in the Visio file is not a relationships part.");
            }

            _budget.Element(reader);
            VisioXml.Children(reader, _budget, name =>
            {
                if (name == "Relationship")
                {
                    if (relationships.Count >= VisioLimits.MaxRelationships)
                    {
                        throw Reject($"{relsPart} in the Visio file lists more than {VisioLimits.MaxRelationships.ToString("N0", CultureInfo.InvariantCulture)} relationships, which is more than Draughtsman reads.");
                    }

                    relationships.Add(new VisioRelationship(
                        reader.GetAttribute("Id") ?? string.Empty,
                        reader.GetAttribute("Type") ?? string.Empty,
                        reader.GetAttribute("Target") ?? string.Empty,
                        string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)));
                }

                VisioXml.Skip(reader, _budget);
            });
        });
        return relationships;
    }

    /// <summary>
    /// The name of the part a relationship of <paramref name="sourcePart"/> points at: a relative target is resolved against the folder of the part
    /// that holds it, an absolute one (a leading slash) against the root of the package. A target that climbs out of the package, names a drive,
    /// or carries a backslash or a control character is a hostile package and refuses the file.
    /// </summary>
    public static string Resolve(string sourcePart, string target)
    {
        if (target.Length == 0 || target.Length > VisioLimits.MaxEntryNameLength || target.Any(c => c == '\\' || char.IsControl(c)) || HasDrive(target))
        {
            throw Reject($"The Visio file has a relationship to '{Shorten(Printable(target), 60)}', which is not a part name inside the package, so it was refused.");
        }

        var segments = new List<string>();
        if (!target.StartsWith('/'))
        {
            int slash = sourcePart.LastIndexOf('/');
            if (slash > 0)
            {
                segments.AddRange(sourcePart[..slash].Split('/'));
            }
        }

        foreach (string segment in target.TrimStart('/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw Reject($"The Visio file has a relationship to '{Shorten(Printable(target), 60)}', which climbs out of the package. It was refused.");
                }

                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }

    internal static SourceRejectedException Reject(string message, Exception? inner = null)
    {
        return new SourceRejectedException(message, inner);
    }

    internal static string Shorten(string text, int length)
    {
        return text.Length <= length ? text : text[..length] + "...";
    }

    /// <summary>A name from the file as it may safely be shown in a message: control characters become '?'.</summary>
    internal static string Printable(string text)
    {
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
    }

    internal static XmlReaderSettings CreateSettings()
    {
        return new XmlReaderSettings
        {
            // The whole defence against XML's own attacks: a DOCTYPE is refused, so there is no entity to expand, no external entity or DTD
            // to fetch, and nothing for a resolver to resolve (and none is set). Whitespace is kept: a label's spaces are its text.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = VisioLimits.MaxEntryBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
            CloseInput = false,
        };
    }

    private static string Describe(XmlException ex, string subject)
    {
        // The framework words a refused DOCTYPE as "DTD is prohibited": say it in the file's own terms.
        return ex.Message.Contains("DTD", StringComparison.Ordinal)
            ? $"{subject} in the Visio file contains a DOCTYPE declaration. Visio never writes one, and Draughtsman refuses it: it is how an XML file smuggles in entity bombs and reads of other files."
            : $"{subject} in the Visio file is not well-formed XML: {Shorten(ex.Message, 160)}";
    }

    private static void CheckFile(byte[] file)
    {
        if (file.Length == 0)
        {
            throw Reject("The Visio file is empty.");
        }

        if (file.Length > VisioLimits.MaxFileBytes)
        {
            throw Reject($"The Visio file is larger than {VisioLimits.MaxFileBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes, which is more than Draughtsman reads. Remove embedded pictures, or split the drawing into one file per page.");
        }

        ReadOnlySpan<byte> head = file;
        if (head.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
        {
            throw Reject("That is an older Visio binary file (.vsd), or a password-protected Visio file, not a .vsdx drawing, and Draughtsman cannot read it. Open it in Visio, remove any password, and choose File, Save As, Visio Drawing (.vsdx), then import that file.");
        }

        if (!head.StartsWith(new byte[] { (byte)'P', (byte)'K', 3, 4 }) && !head.StartsWith(new byte[] { (byte)'P', (byte)'K', 5, 6 }))
        {
            int first = 0;
            while (first < file.Length && file[first] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                first++;
            }

            bool text = first < file.Length && (file[first] == (byte)'<' || file[first] == 0xEF);
            throw Reject(text
                ? "That is text, not a Visio .vsdx file (which is a ZIP package). If it is a draw.io file, use Import draw.io."
                : "That is not a Visio .vsdx file: it is not a ZIP package.");
        }
    }

    /// <summary>Checks every entry against the limits and the name rules, from the ZIP's directory alone: nothing is inflated.</summary>
    private void Index(int fileLength)
    {
        if (_archive.Entries.Count > VisioLimits.MaxEntries)
        {
            throw Reject($"The Visio file has more than {VisioLimits.MaxEntries.ToString("N0", CultureInfo.InvariantCulture)} entries, which is more than Draughtsman reads.");
        }

        long declared = 0;
        foreach (ZipArchiveEntry entry in _archive.Entries)
        {
            string name = entry.FullName;
            CheckName(name);
            if (entry.IsEncrypted)
            {
                throw Reject($"The Visio file has an encrypted entry ('{Shorten(Printable(name), 60)}'), which Draughtsman cannot read. Remove the password in Visio and save it again.");
            }

            if (entry.Length > VisioLimits.MaxEntryBytes || entry.Length < 0)
            {
                throw Reject($"'{Shorten(Printable(name), 60)}' in the Visio file claims to be {entry.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes, which is more than the {(VisioLimits.MaxEntryBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MiB Draughtsman reads from one part.");
            }

            if (entry.CompressedLength > fileLength || entry.CompressedLength < 0)
            {
                throw Reject($"'{Shorten(Printable(name), 60)}' in the Visio file claims a compressed size larger than the file itself, so its size header is lying and the file was refused.");
            }

            declared += entry.Length;
            if (declared > VisioLimits.MaxDeclaredTotalBytes)
            {
                throw Reject($"The Visio file's parts claim to expand to more than {(VisioLimits.MaxDeclaredTotalBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MiB in all, which is more than Draughtsman reads.");
            }

            if (!_entries.TryAdd(name, entry))
            {
                throw Reject($"The Visio file has two entries named '{Shorten(Printable(name), 60)}'. A package never does, and two readers could pick different ones, so it was refused.");
            }
        }
    }

    /// <summary>Zip-slip: a name that, extracted, would land outside the folder it is extracted to. Nothing here is extracted; a package with such a name is refused anyway.</summary>
    private static void CheckName(string name)
    {
        bool bad = name.Length == 0
            || name.Length > VisioLimits.MaxEntryNameLength
            || name.Any(char.IsControl)
            || name[0] is '/' or '\\'
            || HasDrive(name)
            || name.Split('/', '\\').Any(segment => segment == "..");
        if (bad)
        {
            throw Reject($"The Visio file has an entry named '{Shorten(Printable(name), 60)}', which could write outside its own folder if the file were unpacked. A real Visio file never has one, and it was refused.");
        }
    }

    private static bool HasDrive(string name)
    {
        return name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':';
    }

    private static string RelsPartOf(string sourcePart)
    {
        int slash = sourcePart.LastIndexOf('/');
        return slash < 0 ? $"_rels/{sourcePart}.rels" : $"{sourcePart[..slash]}/_rels/{sourcePart[(slash + 1)..]}.rels";
    }
}
