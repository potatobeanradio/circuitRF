// ================================================================
//  CommaLabelSliceTests.cs — quote-aware bracket-token splitting
//
//  A two-tone mixIndex label like "(1,-1)" contains a comma. The slice
//  splitter must treat it as ONE token, not split it on the inner comma
//  (which produced 'expected 3 axis token(s), got 4').
// ================================================================

using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class CommaLabelSliceTests
{
    [Theory]
    [InlineData(":, \"Vout\", \"(1,-1)\"", 3)]   // comma inside the quoted label is NOT a separator
    [InlineData("\"Vout\", \"(0,0)\"",     2)]
    [InlineData(":, :, 1",                 3)]
    [InlineData("",                        0)]   // bare / rank-0 reference
    public void SplitTokens_QuotedCommaIsNotASeparator(string body, int expected)
        => Assert.Equal(expected, SliceTokenParser.SplitTokens(body).Length);

    private static DataSet MakeTwoToneDs()
    {
        var pin  = new Axis("Pin",      new[] { 0.0, 5.0 });
        var node = new Axis("node",     new[] { 0.0 }, "", new[] { "Vout" });
        var mix  = new Axis("mixIndex", new[] { 0.0, 1.95e9, 0.1e9 }, "Hz",
                            new[] { "(0,0)", "(1,0)", "(1,-1)" });
        var data = new Complex[2 * 1 * 3];
        for (int i = 0; i < data.Length; i++) data[i] = new Complex(i + 1, 0);
        var ds = new DataSet();
        ds.AddToGroup("HB1", "V", new DataCube(new[] { pin, node, mix }, data));
        return ds;
    }

    // The user's exact failing case: a quoted "(k1,k2)" label now parses.
    [Fact]
    public void CubeTraceSpecParser_MixIndexLabel_Parses()
    {
        var ds = MakeTwoToneDs();
        bool ok = CubeTraceSpecParser.TryParse(
            "HB1.V[:, \"Vout\", \"(1,-1)\"]", ds,
            out string cubeName, out var slice, out _, out string error);

        Assert.True(ok, error);
        Assert.Equal("HB1.V", cubeName);
        Assert.NotNull(slice);
        Assert.Equal(3, slice!.Length);
        Assert.Equal(AxisRole.KeepAsX,    slice[0].Role);    // Pin kept as X
        Assert.Equal(AxisRole.PinToIndex, slice[1].Role);    // node "Vout"
        Assert.Equal(AxisRole.PinToIndex, slice[2].Role);    // mixIndex "(1,-1)"
        Assert.Equal(2,                   slice[2].Index);   // "(1,-1)" → label index 2
    }

    // The same label inside a multi-cube expression (TraceExpression) also parses + evaluates.
    [Fact]
    public void TraceExpression_MixIndexLabel_Evaluates()
    {
        var ds = MakeTwoToneDs();
        bool ok = TraceExpression.TryEvaluate(
            "mag(HB1.V[:, \"Vout\", \"(1,-1)\"])", ds, PlotType.Rect,
            out _, out _, out var rz, out _, out _, out _, out string error);

        Assert.True(ok, error);
        Assert.NotNull(rz);
        Assert.Equal(2, rz!.Length);    // rank-1 over Pin (2 points)
    }
}
