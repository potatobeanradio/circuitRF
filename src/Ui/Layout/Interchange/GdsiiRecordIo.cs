// Low-level GDSII Stream record record I/O (docs/sonnet-briefs/brief-L4a-gdsii-interchange.md §2.5) —
// streams one record at a time, never materializing a whole file. A record is a 4-byte header
// (2-byte big-endian total length including the header, 1-byte record type, 1-byte data type)
// followed by (length-4) bytes of payload. Written from the public spec — no GDSII library
// dependency, never ingests GPL sources.

using System.Text;

namespace CircuitRF.Ui.Layout.Interchange;

public enum GdsiiRecordType : byte
{
    Header = 0x00,
    BgnLib = 0x01,
    LibName = 0x02,
    Units = 0x03,
    EndLib = 0x04,
    BgnStr = 0x05,
    StrName = 0x06,
    EndStr = 0x07,
    Boundary = 0x08,
    Path = 0x09,
    SRef = 0x0A,
    ARef = 0x0B,
    Text = 0x0C,
    Layer = 0x0D,
    Datatype = 0x0E,
    Width = 0x0F,
    Xy = 0x10,
    EndEl = 0x11,
    SName = 0x12,
    ColRow = 0x13,
    TextType = 0x16,
    Presentation = 0x17,
    StringRec = 0x19,
    Strans = 0x1A,
    Mag = 0x1B,
    Angle = 0x1C,
    PathType = 0x21,
    BgnExtn = 0x30,
    EndExtn = 0x31,
}

public enum GdsiiDataType : byte
{
    NoData = 0,
    BitArray = 1,
    Int2 = 2,
    Int4 = 3,
    Real4 = 4,
    Real8 = 5,
    Ascii = 6,
}

/// <summary>One record's raw header + payload bytes (big-endian encoded), before any typed decoding.</summary>
public readonly struct GdsiiRecord(GdsiiRecordType type, GdsiiDataType dataType, byte[] payload)
{
    public GdsiiRecordType Type { get; } = type;
    public GdsiiDataType DataType { get; } = dataType;
    public byte[] Payload { get; } = payload;

    public short[] AsInt16Array()
    {
        var result = new short[Payload.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = (short)((Payload[i * 2] << 8) | Payload[i * 2 + 1]);
        return result;
    }

    public int[] AsInt32Array()
    {
        var result = new int[Payload.Length / 4];
        for (int i = 0; i < result.Length; i++)
        {
            int o = i * 4;
            result[i] = (Payload[o] << 24) | (Payload[o + 1] << 16) | (Payload[o + 2] << 8) | Payload[o + 3];
        }
        return result;
    }

    public double[] AsReal8Array()
    {
        var result = new double[Payload.Length / 8];
        for (int i = 0; i < result.Length; i++)
            result[i] = GdsiiReal8.ToDouble(Payload.AsSpan(i * 8, 8));
        return result;
    }

    public string AsAscii()
    {
        int len = Payload.Length;
        while (len > 0 && Payload[len - 1] == 0) len--;
        return Encoding.ASCII.GetString(Payload, 0, len);
    }
}

/// <summary>Streams GDSII records lazily from a <see cref="Stream"/> — never materializes the whole
/// file (§2.5). Reads exactly one record per call to <see cref="TryReadNext"/>.</summary>
public sealed class GdsiiRecordReader(Stream stream)
{
    private readonly byte[] _header = new byte[4];

    public bool TryReadNext(out GdsiiRecord record)
    {
        record = default;
        int read = ReadFully(_header, 4);
        if (read == 0) return false;
        if (read != 4) throw new InvalidDataException("Truncated GDSII record header.");

        int length = (_header[0] << 8) | _header[1];
        if (length < 4) throw new InvalidDataException($"Invalid GDSII record length {length}.");
        var type = (GdsiiRecordType)_header[2];
        var dataType = (GdsiiDataType)_header[3];

        int payloadLen = length - 4;
        var payload = payloadLen == 0 ? [] : new byte[payloadLen];
        if (payloadLen > 0 && ReadFully(payload, payloadLen) != payloadLen)
            throw new InvalidDataException("Truncated GDSII record payload.");

        record = new GdsiiRecord(type, dataType, payload);
        return true;
    }

    private int ReadFully(byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>Streams GDSII records to a <see cref="Stream"/> as they are built — never materializes
/// the whole file (§2.5).</summary>
public sealed class GdsiiRecordWriter(Stream stream)
{
    public void WriteRecord(GdsiiRecordType type, GdsiiDataType dataType, ReadOnlySpan<byte> payload)
    {
        int length = 4 + payload.Length;
        if (length > ushort.MaxValue)
            throw new InvalidDataException(
                $"GDSII record {type} exceeds the 16-bit length field ({length} bytes) — split into multiple elements.");

        Span<byte> header = stackalloc byte[4];
        header[0] = (byte)(length >> 8);
        header[1] = (byte)length;
        header[2] = (byte)type;
        header[3] = (byte)dataType;
        stream.Write(header);
        if (payload.Length > 0) stream.Write(payload);
    }

    public void WriteNoData(GdsiiRecordType type) => WriteRecord(type, GdsiiDataType.NoData, []);

    /// <summary>STRANS/PRESENTATION's own data type — physically identical 2-byte-per-value encoding
    /// to <see cref="WriteInt16Array"/>, but a DIFFERENT declared <see cref="GdsiiDataType"/> (BitArray,
    /// not Int2). A strict reader (e.g. KLayout) enforces the record-type↔data-type pairing per the
    /// spec and rejects the wrong one even though the bytes decode identically either way — this is
    /// not cosmetic.</summary>
    public void WriteBitArray(GdsiiRecordType type, ushort bits)
    {
        Span<byte> payload = stackalloc byte[2];
        payload[0] = (byte)(bits >> 8);
        payload[1] = (byte)bits;
        WriteRecord(type, GdsiiDataType.BitArray, payload);
    }

    public void WriteInt16Array(GdsiiRecordType type, ReadOnlySpan<short> values)
    {
        var payload = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            payload[i * 2] = (byte)(values[i] >> 8);
            payload[i * 2 + 1] = (byte)values[i];
        }
        WriteRecord(type, GdsiiDataType.Int2, payload);
    }

    public void WriteInt32Array(GdsiiRecordType type, ReadOnlySpan<int> values)
    {
        var payload = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            int o = i * 4;
            int v = values[i];
            payload[o] = (byte)(v >> 24);
            payload[o + 1] = (byte)(v >> 16);
            payload[o + 2] = (byte)(v >> 8);
            payload[o + 3] = (byte)v;
        }
        WriteRecord(type, GdsiiDataType.Int4, payload);
    }

    public void WriteReal8Array(GdsiiRecordType type, ReadOnlySpan<double> values)
    {
        var payload = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
            GdsiiReal8.WriteTo(payload.AsSpan(i * 8, 8), values[i]);
        WriteRecord(type, GdsiiDataType.Real8, payload);
    }

    public void WriteAscii(GdsiiRecordType type, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        // GDSII strings pad to an even total length with a trailing null byte.
        var payload = bytes.Length % 2 == 0 ? bytes : [.. bytes, (byte)0];
        WriteRecord(type, GdsiiDataType.Ascii, payload);
    }
}
