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

    // T4 — two-tone mixIndex axis → IsMixIndexStem=true (drives the spectrum stem plot), not harmonic.
    // Single-sided: negative-frequency products fold to +|f| (no negative x).
    [Fact]
    public void IsMixIndexStem_MixIndexAxis_True_AndFoldsToPositiveFrequencies()
    {
        var trace = MakeCubeTrace();
        // mixIndex VALUES are the signed product frequencies: DC, f1, f2, f1−f2 (negative).
        trace.SetCubeData(
            new double[] { 0, 1.95e9, 2.05e9, -0.1e9 },
            complexValues: null,
            new double[] { 0.1, 1.0, 1.0, 0.2 },
            xAxisName: Trace.MixIndexAxisName,
            xUnit:     "Hz",
            PlotType.Rect, FreqUnit.GHz);

        Assert.True(trace.IsMixIndexStem);
        Assert.False(trace.IsHarmonicStem);

        // Single-sided: ALL stems are at non-negative x; the f1−f2 product folds onto +0.1 GHz.
        Assert.All(trace.Points, p => Assert.True(p.X >= -1e-6f));
        Assert.Contains(trace.Points, p => System.Math.Abs(p.X - 0.1f) < 1e-3f);  // |f1−f2| = +0.1 GHz
    }

    // T5 — harmonic axis is NOT a mixIndex stem.
    [Fact]
    public void IsMixIndexStem_HarmonicAxis_False()
    {
        var trace = MakeCubeTrace();
        trace.SetCubeData(new double[] { 0, 1, 2 }, null, new double[] { 1, 2, 3 },
            Trace.HarmonicAxisName, null, PlotType.Rect, FreqUnit.GHz);
        Assert.False(trace.IsMixIndexStem);
    }

    // T6 — arrow-key stepping over a mixIndex spectrum follows FREQUENCY order, not the array/lattice
    // order the products are stored in (the crux for tight two-tone IMD spacings).
    [Fact]
    public void StepMarker_MixIndex_FollowsFrequencyOrder_AndStopsAtEnds()
    {
        var t = MakeCubeTrace();
        // mixIndex VALUES in lattice order (NOT sorted): f1, f2, DC, |f1−f2|. Frequency order is
        // 0.0, 0.1, 1.95, 2.05 GHz — different from array order.
        t.SetCubeData(
            new double[] { 1.95e9, 2.05e9, 0.0, 0.1e9 },
            complexValues: null,
            new double[] { 1.0, 1.0, 0.1, 0.2 },
            xAxisName: Trace.MixIndexAxisName,
            xUnit:     "Hz",
            PlotType.Rect, FreqUnit.GHz);

        var m = new Marker(t, freq: 0, isMulti: false, isDelta: false, index: 1)
        {
            PositionStatic = new Vector2(0f, 0f),   // start on the DC product (0 GHz)
        };

        // Up steps to the next HIGHER frequency, regardless of array position.
        Assert.True(t.StepMarkerAlongX(m, +1));
        Assert.Equal(0.10, (double)m.PositionStatic.X, 3);   // |f1−f2| (array index 3)
        Assert.True(t.StepMarkerAlongX(m, +1));
        Assert.Equal(1.95, (double)m.PositionStatic.X, 3);   // f1 (array index 0)
        Assert.True(t.StepMarkerAlongX(m, +1));
        Assert.Equal(2.05, (double)m.PositionStatic.X, 3);   // f2 (array index 1)

        // At the top — no wrap, no move.
        Assert.False(t.StepMarkerAlongX(m, +1));
        Assert.Equal(2.05, (double)m.PositionStatic.X, 3);

        // Down steps back to the next lower frequency.
        Assert.True(t.StepMarkerAlongX(m, -1));
        Assert.Equal(1.95, (double)m.PositionStatic.X, 3);
    }

    // T7 — a plain monotonic cube X (e.g. a Pin sweep) steps one sample per key.
    [Fact]
    public void StepMarker_MonotonicCubeX_StepsOneSample()
    {
        var t = MakeCubeTrace();
        t.SetCubeData(
            new double[] { 0, 5, 10, 15 }, complexValues: null, new double[] { -3, 0, 2, 3 },
            xAxisName: "Pin", xUnit: "dBm", PlotType.Rect, FreqUnit.GHz);

        var m = new Marker(t, freq: 0, isMulti: false, isDelta: false, index: 1)
        {
            PositionStatic = new Vector2(5f, 0f),   // Pin = 5 (no freq scaling on a non-freq axis)
        };

        Assert.True(t.StepMarkerAlongX(m, +1));
        Assert.Equal(10.0, (double)m.PositionStatic.X, 3);
        Assert.True(t.StepMarkerAlongX(m, -1));
        Assert.Equal(5.0, (double)m.PositionStatic.X, 3);
    }
}
