using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

public class DxfRecordIoTests
{
    [Fact]
    public void WriteThenRead_StringIntDouble_RoundTrips()
    {
        using var sw = new StringWriter();
        var w = new DxfGroupWriter(sw);
        w.WriteString(0, "LINE");
        w.WriteInt(70, 42);
        w.WriteDouble(10, 3.140000);

        using var sr = new StringReader(sw.ToString());
        var r = new DxfGroupReader(sr);

        Assert.True(r.TryReadNext(out var g1));
        Assert.Equal(0, g1.Code);
        Assert.Equal("LINE", g1.Value);

        Assert.True(r.TryReadNext(out var g2));
        Assert.Equal(70, g2.Code);
        Assert.Equal(42, g2.AsInt());

        Assert.True(r.TryReadNext(out var g3));
        Assert.Equal(10, g3.Code);
        Assert.Equal(3.14, g3.AsDouble(), 9);

        Assert.False(r.TryReadNext(out _));
    }

    [Fact]
    public void WriteCoord_ScalesDbuToDrawingUnit()
    {
        using var sw = new StringWriter();
        var w = new DxfGroupWriter(sw);
        w.WriteCoord(10, 1_000_000, 0.001); // 1,000,000 DBU at 0.001 drawing-units-per-DBU = 1000.0

        using var sr = new StringReader(sw.ToString());
        var r = new DxfGroupReader(sr);
        Assert.True(r.TryReadNext(out var g));
        Assert.Equal(1000.0, g.AsDouble(), 6);
    }

    [Fact]
    public void GroupReader_TruncatedFile_ReturnsFalse_NoThrow()
    {
        using var sr = new StringReader("10\n");
        var r = new DxfGroupReader(sr);
        Assert.False(r.TryReadNext(out _));
    }
}
