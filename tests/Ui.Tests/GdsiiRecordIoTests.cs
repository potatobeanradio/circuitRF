using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>Streaming record reader/writer — round trips every payload shape the GDSII reader/writer
/// will need (int16, int32, real8, ascii, no-data), and confirms records are read/written one at a
/// time (never the whole file materialized) via a plain forward-only <see cref="MemoryStream"/>.</summary>
public class GdsiiRecordIoTests
{
    [Fact]
    public void WriteThenRead_Int16Array_RoundTrips()
    {
        using var ms = new MemoryStream();
        new GdsiiRecordWriter(ms).WriteInt16Array(GdsiiRecordType.ColRow, [5, 5]);
        ms.Position = 0;

        Assert.True(new GdsiiRecordReader(ms).TryReadNext(out var rec));
        Assert.Equal(GdsiiRecordType.ColRow, rec.Type);
        Assert.Equal(GdsiiDataType.Int2, rec.DataType);
        Assert.Equal([(short)5, (short)5], rec.AsInt16Array());
    }

    [Fact]
    public void WriteThenRead_Int32Array_RoundTrips()
    {
        using var ms = new MemoryStream();
        new GdsiiRecordWriter(ms).WriteInt32Array(GdsiiRecordType.Xy, [0, 0, 1000, 0, 1000, 1000, -5, -2_000_000_000]);
        ms.Position = 0;

        Assert.True(new GdsiiRecordReader(ms).TryReadNext(out var rec));
        Assert.Equal([0, 0, 1000, 0, 1000, 1000, -5, -2_000_000_000], rec.AsInt32Array());
    }

    [Fact]
    public void WriteThenRead_Real8Array_RoundTrips()
    {
        using var ms = new MemoryStream();
        new GdsiiRecordWriter(ms).WriteReal8Array(GdsiiRecordType.Units, [0.001, 1e-9]);
        ms.Position = 0;

        Assert.True(new GdsiiRecordReader(ms).TryReadNext(out var rec));
        var values = rec.AsReal8Array();
        Assert.Equal(0.001, values[0], 10);
        Assert.Equal(1e-9, values[1], 15);
    }

    [Theory]
    [InlineData("TOP")]
    [InlineData("ODD")]  // odd length exercises the null-padding
    public void WriteThenRead_Ascii_RoundTrips(string text)
    {
        using var ms = new MemoryStream();
        new GdsiiRecordWriter(ms).WriteAscii(GdsiiRecordType.StrName, text);
        ms.Position = 0;

        Assert.True(new GdsiiRecordReader(ms).TryReadNext(out var rec));
        Assert.Equal(text, rec.AsAscii());
    }

    [Fact]
    public void WriteThenRead_BitArray_RoundTrips_WithBitArrayDataType_NotInt2()
    {
        // Regression: STRANS is a BITARRAY record (data type 1), physically identical 2-byte
        // encoding to an Int2 record (data type 2) — but a strict third-party reader (KLayout)
        // enforces the record-type/data-type pairing per spec and rejects the wrong one even though
        // the bytes decode identically either way. A real GDSII file exported by this codebase failed
        // to open in KLayout 0.30.9 for exactly this reason before this fix (STRANS was written via
        // WriteInt16Array, stamping Int2 instead of BitArray).
        using var ms = new MemoryStream();
        new GdsiiRecordWriter(ms).WriteBitArray(GdsiiRecordType.Strans, 0x8000);
        ms.Position = 0;

        Assert.True(new GdsiiRecordReader(ms).TryReadNext(out var rec));
        Assert.Equal(GdsiiRecordType.Strans, rec.Type);
        Assert.Equal(GdsiiDataType.BitArray, rec.DataType);
        Assert.NotEqual(GdsiiDataType.Int2, rec.DataType);
        Assert.Equal([unchecked((short)0x8000)], rec.AsInt16Array());
    }

    [Fact]
    public void WriteThenRead_NoData_RoundTrips()
    {
        using var ms = new MemoryStream();
        new GdsiiRecordWriter(ms).WriteNoData(GdsiiRecordType.Boundary);
        ms.Position = 0;

        Assert.True(new GdsiiRecordReader(ms).TryReadNext(out var rec));
        Assert.Equal(GdsiiRecordType.Boundary, rec.Type);
        Assert.Empty(rec.Payload);
    }

    [Fact]
    public void MultipleRecords_ReadOneAtATime_InOrder()
    {
        using var ms = new MemoryStream();
        var w = new GdsiiRecordWriter(ms);
        w.WriteAscii(GdsiiRecordType.LibName, "LIB");
        w.WriteNoData(GdsiiRecordType.BgnStr);
        w.WriteAscii(GdsiiRecordType.StrName, "CELL1");
        w.WriteNoData(GdsiiRecordType.EndStr);
        ms.Position = 0;

        var reader = new GdsiiRecordReader(ms);
        var types = new List<GdsiiRecordType>();
        while (reader.TryReadNext(out var rec)) types.Add(rec.Type);

        Assert.Equal(
            [GdsiiRecordType.LibName, GdsiiRecordType.BgnStr, GdsiiRecordType.StrName, GdsiiRecordType.EndStr],
            types);
    }

    [Fact]
    public void TryReadNext_AtEndOfStream_ReturnsFalse()
    {
        using var ms = new MemoryStream();
        Assert.False(new GdsiiRecordReader(ms).TryReadNext(out _));
    }

    [Fact]
    public void TryReadNext_TruncatedHeader_Throws()
    {
        using var ms = new MemoryStream([0x00, 0x04]); // 2 bytes, not a full 4-byte header
        Assert.Throws<InvalidDataException>(() => new GdsiiRecordReader(ms).TryReadNext(out _));
    }

    [Fact]
    public void WriteRecord_OverLengthLimit_Throws()
    {
        using var ms = new MemoryStream();
        var oversized = new byte[70_000];
        Assert.Throws<InvalidDataException>(
            () => new GdsiiRecordWriter(ms).WriteRecord(GdsiiRecordType.Xy, GdsiiDataType.Int4, oversized));
    }
}
