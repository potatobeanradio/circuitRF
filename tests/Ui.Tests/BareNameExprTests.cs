// ================================================================
//  BareNameExprTests.cs — bare cube/measurement names in TraceExpression
//
//  "(IMD2)" (or "IMD2", or "IMD2 + 5") in the trace-card expression must
//  resolve the IMD2 measurement — previously only CubeName[...] refs were
//  recognized, so a bare name gave "No cube references found."
// ================================================================

using CircuitRF.Ui.DataDisplay;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class BareNameExprTests
{
    private static DataSet MakeDsWithMeasurement()
    {
        var pin  = new Axis("Pin", new[] { 0.0, 5.0 });
        var imd2 = new DataCube(new[] { pin }, new double[] { -20.0, -18.0 });   // rank-1 real over Pin
        var ds = new DataSet();
        ds.AddToGroup("measurements", "IMD2", imd2);
        return ds;
    }

    [Theory]
    [InlineData("IMD2")]        // bare name
    [InlineData("(IMD2)")]      // the user's exact case — wrapped in parens
    [InlineData("IMD2 + 5")]    // bare name inside an expression
    public void TraceExpression_BareMeasurementName_Resolves(string expr)
    {
        var ds = MakeDsWithMeasurement();
        bool ok = TraceExpression.TryEvaluate(
            expr, ds, PlotType.Rect,
            out _, out _, out var rz, out _, out _, out _, out string error);

        Assert.True(ok, error);
        Assert.NotNull(rz);
        Assert.Equal(2, rz!.Length);     // rank-1 over Pin
    }

    [Fact]
    public void TraceExpression_BareMeasurement_ValueIsCorrect()
    {
        var ds = MakeDsWithMeasurement();
        bool ok = TraceExpression.TryEvaluate(
            "IMD2 + 5", ds, PlotType.Rect,
            out _, out _, out var rz, out _, out _, out _, out string error);
        Assert.True(ok, error);
        Assert.Equal(-15.0, rz![0], 6);  // -20 + 5
        Assert.Equal(-13.0, rz![1], 6);  // -18 + 5
    }

    // The function-call transform form on a bare measurement — "mag(IMD2)" — must round-trip through
    // CubeTraceSpecParser (BuildPickerExpression emits this; without it CommitSpec drops CubeName/Transform
    // and the transform combo can't sync).
    [Fact]
    public void CubeTraceSpecParser_TransformedBareMeasurement_RoundTrips()
    {
        var ds = MakeDsWithMeasurement();   // measurements.IMD2 (rank-1)
        bool ok = CubeTraceSpecParser.TryParse(
            "mag(IMD2)", ds, out string cubeName, out var slice, out var transform, out string error);

        Assert.True(ok, error);
        Assert.Equal("IMD2", cubeName);
        Assert.Equal(CubeTransform.Mag, transform);
        Assert.NotNull(slice);
    }

    // Regression: a bare cube name must NOT be matched inside a longer identifier.
    [Fact]
    public void TraceExpression_BareName_RespectsWordBoundary()
    {
        var pin = new Axis("Pin", new[] { 0.0, 5.0 });
        var ds  = new DataSet();
        ds.Add("V", new DataCube(new[] { pin }, new double[] { 1.0, 2.0 }));   // default group, bare "V"

        // "Vout" is not cube "V" followed by "out" — with no cube "Vout" this must fail cleanly.
        bool ok = TraceExpression.TryEvaluate(
            "Vout", ds, PlotType.Rect, out _, out _, out _, out _, out _, out _, out _);
        Assert.False(ok);
    }
}
