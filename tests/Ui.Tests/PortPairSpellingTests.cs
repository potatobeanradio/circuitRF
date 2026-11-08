// ================================================================
//  PortPairSpellingTests.cs — DUT_2(2,1) in a trace, as a measure line writes it
//
//  A measurement DUT_2 = SP1.S(3:4, 3:4) lands in the measurements group with port axes i and j. A trace
//  typed DUT_2(2,1) reads its S21 exactly as DUT_2[:, 2, 1] does, in a single-cube spec and in an expression.
// ================================================================

using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PortPairSpellingTests
{
    [Fact]
    public void APortPairInParentheses_ReadsAsTheBracketForm()
    {
        double[] ports = [1, 2];
        var v = new Complex[3 * 2 * 2];
        for (int f = 0; f < 3; f++) for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++)
            v[(f * 2 + i) * 2 + j] = new Complex(10 * (i + 1) + (j + 1) + 100 * f, 0);
        var ds = new DataSet();
        ds.AddToGroup(DataSet.MeasurementsGroup, "DUT_2",
            new DataCube([new Axis("freq", [1e9, 2e9, 3e9], "Hz"), new Axis("i", ports, "port"), new Axis("j", ports, "port")], v));

        Assert.True(CubeTraceSpecParser.TryParse("dB(DUT_2(2,1))", ds, out var cube, out var slice, out _, out var err), err);
        Assert.True(CubeTraceSpecParser.TryParse("dB(DUT_2[:, 2, 1])", ds, out _, out var bracketSlice, out _, out _));
        Assert.Equal("DUT_2", cube);
        Assert.Equal(bracketSlice, slice);

        Assert.True(TraceExpression.TryEvaluate("DUT_2(2,1) - DUT_2(1,1)", ds, PlotType.Rect,
            out _, out _, out var re, out _, out _, out _, out err), err);
        Assert.Equal([10.0, 10.0, 10.0], re);   // S21 - S11 = 21 - 11 at every frequency
    }
}
