// ================================================================
//  HarmonicStemPlotTests.cs  —  Trace.IsHarmonicStem flag
//
//  Model-level gate for the stem-plot detection:
//    T1 — cube trace with xAxisName="harmonic"  → IsHarmonicStem=true
//    T2 — cube trace with xAxisName="freq"      → IsHarmonicStem=false
//    T3 — non-cube (SNP) trace                  → IsHarmonicStem=false
// ================================================================

using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class HarmonicStemPlotTests
{
    private static Trace MakeCubeTrace() =>
        new Trace(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Mag)
        {
            CubeName  = "V",
            Slice     = new[] { new AxisSlice("harmonic", AxisRole.KeepAsX, 0) },
            Transform = CubeTransform.Mag,
        };

    // T1 — cube trace with xAxisName="harmonic" → IsHarmonicStem=true
    [Fact]
    public void IsHarmonicStem_HarmonicAxis_True()
    {
        var trace = MakeCubeTrace();
        trace.SetCubeData(
            new double[] { 0, 1, 2 },
            new Complex[] { new(1, 0), new(2, 0), new(3, 0) },
            realValues:  null,
            xAxisName:   Trace.HarmonicAxisName,
            xUnit:       null,
            PlotType.Rect, FreqUnit.GHz);

        Assert.True(trace.IsHarmonicStem);
    }

    // T2 — cube trace with xAxisName="freq" → IsHarmonicStem=false
    [Fact]
    public void IsHarmonicStem_FreqAxis_False()
    {
        var trace = MakeCubeTrace();
        trace.SetCubeData(
            new double[] { 1e9, 2e9, 3e9 },
            complexValues: null,
            new double[] { 1.0, 2.0, 3.0 },
            xAxisName:   "freq",
            xUnit:       "Hz",
            PlotType.Rect, FreqUnit.GHz);

        Assert.False(trace.IsHarmonicStem);
    }

    // T3 — non-cube SNP trace → IsHarmonicStem=false
    [Fact]
    public void IsHarmonicStem_NetworkTrace_False()
    {
        var snp   = new SNP(new double[] { 1e9, 2e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        // CubeName is null → IsCubeBound=false
        Assert.False(trace.IsCubeBound);
        Assert.False(trace.IsHarmonicStem);
    }
}
