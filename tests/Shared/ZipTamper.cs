using System.Buffers.Binary;

namespace Draughtsman.Tests.Shared;

/// <summary>
/// Edits the headers of a ZIP written by <see cref="VisioDocBuilder"/> (one entry per local header, no ZIP64, no comments), to make the archives a
/// hostile file would be: a size header that lies, an entry marked encrypted, a ZIP64 claim of eight gigabytes. The data itself is untouched, which is
/// the point: the archive is well-formed except for what the test says it lies about.
/// </summary>
internal static class ZipTamper
{
    private const uint LocalHeader = 0x04034b50;
    private const uint CentralHeader = 0x02014b50;
    private const uint EndOfCentralDirectory = 0x06054b50;

    /// <summary>The offset of every record with the given signature, in file order.</summary>
    private static List<int> Find(byte[] zip, uint signature)
    {
        var found = new List<int>();
        for (int i = 0; i + 4 <= zip.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == signature)
            {
                found.Add(i);
            }
        }

        return found;
    }

    public static string NameOf(byte[] zip, int centralOffset)
    {
        int length = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(centralOffset + 28));
        return System.Text.Encoding.UTF8.GetString(zip, centralOffset + 46, length);
    }

    private static int CentralOf(byte[] zip, string name)
    {
        return Find(zip, CentralHeader).Single(o => NameOf(zip, o) == name);
    }

    /// <summary>Sets the uncompressed size the central directory and the local header of <paramref name="name"/> claim.</summary>
    public static byte[] ClaimUncompressedSize(byte[] zip, string name, uint size)
    {
        byte[] copy = (byte[])zip.Clone();
        int central = CentralOf(copy, name);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(central + 24), size);
        int local = (int)BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(central + 42));
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(local + 22), size);
        return copy;
    }

    /// <summary>Sets the compressed size the central directory and the local header of <paramref name="name"/> claim.</summary>
    public static byte[] ClaimCompressedSize(byte[] zip, string name, uint size)
    {
        byte[] copy = (byte[])zip.Clone();
        int central = CentralOf(copy, name);
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(central + 20), size);
        int local = (int)BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(central + 42));
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(local + 18), size);
        return copy;
    }

    /// <summary>Sets bit 0 of the general purpose flag (encrypted) on <paramref name="name"/>, in both headers.</summary>
    public static byte[] MarkEncrypted(byte[] zip, string name)
    {
        byte[] copy = (byte[])zip.Clone();
        int central = CentralOf(copy, name);
        copy[central + 8] |= 1;
        int local = (int)BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(central + 42));
        copy[local + 6] |= 1;
        return copy;
    }

    /// <summary>
    /// Makes the central directory say, through a ZIP64 extra field, that <paramref name="name"/> expands to <paramref name="claimed"/> bytes (and is
    /// <paramref name="claimedCompressed"/> compressed), with the 32-bit size fields at their ZIP64 marker value, as a real ZIP64 archive writes them.
    /// </summary>
    public static byte[] ClaimZip64(byte[] zip, string name, long claimed, long claimedCompressed)
    {
        int central = CentralOf(zip, name);
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(central + 28));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(central + 30));
        int insertAt = central + 46 + nameLength + extraLength;
        byte[] extra = new byte[4 + 16];
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(0), 0x0001);
        BinaryPrimitives.WriteUInt16LittleEndian(extra.AsSpan(2), 16);
        BinaryPrimitives.WriteInt64LittleEndian(extra.AsSpan(4), claimed);
        BinaryPrimitives.WriteInt64LittleEndian(extra.AsSpan(12), claimedCompressed);

        byte[] result = new byte[zip.Length + extra.Length];
        Array.Copy(zip, 0, result, 0, insertAt);
        Array.Copy(extra, 0, result, insertAt, extra.Length);
        Array.Copy(zip, insertAt, result, insertAt + extra.Length, zip.Length - insertAt);

        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(central + 20), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(central + 24), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(central + 30), (ushort)(extraLength + extra.Length));

        int end = Find(result, EndOfCentralDirectory).Single();
        uint directorySize = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(end + 12));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 12), directorySize + (uint)extra.Length);
        return result;
    }

    /// <summary>Flips a bit in the data of <paramref name="name"/> (past its local header), so the compressed stream is damaged and the ZIP's headers are not.</summary>
    public static byte[] Corrupt(byte[] zip, string name, int atDataOffset)
    {
        byte[] copy = (byte[])zip.Clone();
        int central = CentralOf(copy, name);
        int local = (int)BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(central + 42));
        int dataStart = local + 30 + BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(local + 26)) + BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(local + 28));
        copy[dataStart + atDataOffset] ^= 0xFF;
        return copy;
    }

    /// <summary>The number of local headers, to prove a tamper left the archive with every entry.</summary>
    public static int EntryCount(byte[] zip)
    {
        return Find(zip, LocalHeader).Count;
    }
}
