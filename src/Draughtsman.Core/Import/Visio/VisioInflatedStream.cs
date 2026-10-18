using System.Globalization;
using System.IO.Compression;

namespace Draughtsman.Core.Import.Visio;

/// <summary>
/// A part's inflated bytes, counted as they leave the decompressor. It refuses a part that is longer than its limit while it is still being read
/// (not after the stream ends: a deflate stream can expand a thousandfold), counts every byte against the import's running total, and, once the part
/// ends, refuses one that is not exactly as long as the ZIP declared or does not have the declared CRC-32. .NET stops inflating at the declared length, so
/// a header that understates the size shows up only as a CRC that does not match, and one that overstates it as a part that ends early.
/// </summary>
internal sealed class VisioInflatedStream : Stream
{
    private static readonly uint[] s_crcTable = BuildCrcTable();

    private readonly Stream _inner;
    private readonly long _declaredLength;
    private readonly uint _declaredCrc;
    private readonly VisioBudget _budget;
    private readonly string _label;
    private long _count;
    private uint _crc = 0xFFFFFFFF;
    private bool _verified;

    /// <param name="inner">The decompressed bytes of the part.</param>
    /// <param name="declaredLength">The length the ZIP's directory says the part has.</param>
    /// <param name="declaredCrc">The CRC-32 the ZIP's directory says the part has.</param>
    public VisioInflatedStream(Stream inner, long declaredLength, uint declaredCrc, VisioBudget budget, string label)
    {
        _inner = inner;
        _declaredLength = declaredLength;
        _declaredCrc = declaredCrc;
        _budget = budget;
        _label = label;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public static VisioInflatedStream Of(ZipArchiveEntry entry, VisioBudget budget, string label)
    {
        Stream inflated;
        try
        {
            inflated = entry.Open();
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            throw VisioPackage.Reject($"{label} in the Visio file could not be opened: it is damaged, or uses a form of compression Draughtsman does not read.", ex);
        }

        return new VisioInflatedStream(inflated, entry.Length, entry.Crc32, budget, label);
    }

    /// <summary>Reads what is left and verifies the part, for a reader that stopped before its end.</summary>
    public void Complete()
    {
        byte[] buffer = new byte[8192];
        while (Read(buffer, 0, buffer.Length) > 0)
        {
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> destination)
    {
        int read;
        try
        {
            read = _inner.Read(destination);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            throw VisioPackage.Reject($"{_label} in the Visio file could not be decompressed: it is damaged or cut short.", ex);
        }

        if (read == 0)
        {
            Verify();
            return 0;
        }

        _count += read;
        if (_count > VisioLimits.MaxEntryBytes)
        {
            throw VisioPackage.Reject($"{_label} in the Visio file expands to more than {(VisioLimits.MaxEntryBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture)} MiB, which is more than Draughtsman reads from one part.");
        }

        _budget.Inflated(read);
        uint crc = _crc;
        foreach (byte b in destination[..read])
        {
            crc = s_crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        _crc = crc;
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Verify()
    {
        if (_verified)
        {
            return;
        }

        _verified = true;
        if (_count != _declaredLength)
        {
            throw VisioPackage.Reject($"{_label} in the Visio file is {_count.ToString("N0", CultureInfo.InvariantCulture)} bytes, not the {_declaredLength.ToString("N0", CultureInfo.InvariantCulture)} its header claims, so the file is damaged or lying about its size and was refused.");
        }

        if ((_crc ^ 0xFFFFFFFF) != _declaredCrc)
        {
            throw VisioPackage.Reject($"{_label} in the Visio file does not match its checksum, so the file is damaged or lying about its size and was refused.");
        }
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
