// ================================================================
//  MaxGainDisplayFormTests.cs  —  Max Gain says which form it is plotting (2026-08-30)
//
//  MAG/MSG is a POWER gain, so its dB is 10·log10 (fixed 2026-08-29, see
//  src/Ui/DataDisplay/RESOLVED.md §"MAG/MSG was 2× too large in dB"). The DISPLAY was still
//  describing it as a 20·log10 quantity — the axis label and every marker readout said
//  "Max Gain dB20" — and the trace card offered transforms (dB20, dB, Real, Imag, Phase, Conj)
//  that either misname the arithmetic or describe a complex value a real positive power gain
//  never has.
//
//  These gates pin the three things that make the display honest:
//    1. The label/readout language says dB10, and says nothing about dB when the trace is linear.
//    2. The transform combo offers exactly None / dB10 / Mag and keys the rest out.
//    3. The combo is a real choice — the plotted number actually changes — and the default a
//       freshly-picked Max Gain trace lands on is the log form.
//
//  The oracle for the numbers is the unilateral limit, which is the one that fixed §1: with
//  S11 = S22 = 0 and S12 → 0, MAG → |S21|², a transducer power gain defined without reference
//  to K or |S21/S12|. So the linear form must read |S21|² and the dB form 20·log10(|S21|).
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using NumFlat;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class MaxGainDisplayFormTests
{
    private const double S21 = 4.0;      // |S21|² = 16 → 12.0412 dB

    // S12 is small enough for the unilateral limit to hold to well inside the tolerances below,
    // and large enough that K = 1/(2·|S12·S21|) ≈ 1250 does not turn K − √(K²−1) into pure
    // cancellation noise — the same balance tests/RfCore.Tests/StabilityAndPassivityTests.cs
    // strikes for the same oracle.
    private const double S12 = 1e-4;

    /// <summary>Unilateral matched two-port: MAG collapses to |S21|² exactly.</summary>
    private static SNP UnilateralSnp()
    {
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = Complex.Zero;
        m[0, 1] = new Complex(S12, 0);
        m[1, 0] = new Complex(S21, 0);
        m[1, 1] = Complex.Zero;
        return new SNP([1e9], [m], MatrixType.S, MatrixFormat.RI, new Complex(50, 0));
    }

    private static Trace MaxGainTrace()
        => new(UnilateralSnp(), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            Derived = DerivedParameters.MaxGain, InputPort = 1, OutputPort = 2,
        };

    // ── 1. The label language ───────────────────────────────────────────────────

    [Fact]
    public void MaxGain_LogForm_LabelsItselfDb10_NeverDb20()
    {
        var t = MaxGainTrace();

        Assert.Equal(CubeTransform.dB10, t.DisplayTransform);
        Assert.Equal("Max Gain dB10", TraceLabeler.QuantityFor(t));
        Assert.Equal("Max Gain dB10", t.ReadoutDescription(showFilePrefix: false));
        Assert.DoesNotContain("dB20", TraceLabeler.ComputeMinimalLabels([t])[0]);
    }

    [Theory]
    [InlineData(CubeTransform.Mag,  "Max Gain Mag")]
    [InlineData(CubeTransform.None, "Max Gain")]
    public void MaxGain_LinearForm_CarriesNoDbSuffix(CubeTransform form, string expected)
    {
        var t = MaxGainTrace();
        Assert.True(t.SetDisplayTransform(form));

        Assert.Equal(form, t.DisplayTransform);
        Assert.Equal(expected, TraceLabeler.QuantityFor(t));
        Assert.Equal(expected, t.ReadoutDescription(showFilePrefix: false));
    }

    /// <summary>
    /// An ordinary S-parameter trace is still dB20 — this change is about Max Gain's arithmetic,
    /// not about renaming dB everywhere.
    /// </summary>
    [Fact]
    public void OrdinarySParameterTrace_IsStillDb20()
    {
        var t = new Trace(UnilateralSnp(), MatrixType.S, 1, 0, DependentVarFormat.Db);
        Assert.Equal(CubeTransform.dB20, t.DisplayTransform);
        Assert.Equal("S(2,1) dB20", TraceLabeler.QuantityFor(t));
    }

    // ── 2. What the transform combo offers ──────────────────────────────────────

    [Fact]
    public void MaxGain_TransformCombo_OffersOnlyNoneDb10AndMag()
    {
        var items = TraceRowViewModel.BuildTransformItems(
            isCubeBound: false, PlotType.Rect, isComplexData: true, DerivedParameters.MaxGain);

        // Every entry is still SHOWN — the unavailable ones are keyed out, not hidden.
        Assert.Equal(Enum.GetValues<CubeTransform>().Length, items.Count);

        var enabled = items.Where(i => i.Enabled).Select(i => i.Transform).ToArray();
        Assert.Equal(
            new[] { CubeTransform.None, CubeTransform.dB10, CubeTransform.Mag }.OrderBy(x => x),
            enabled.OrderBy(x => x));

        foreach (var keyedOut in new[]
                 {
                     CubeTransform.dB20, CubeTransform.dB, CubeTransform.Phase,
                     CubeTransform.Real, CubeTransform.Imag, CubeTransform.Conj,
                 })
            Assert.False(items.Single(i => i.Transform == keyedOut).Enabled, keyedOut.ToString());
    }

    [Fact]
    public void MaxGain_RefusesATransformItDoesNotOffer()
    {
        var t = MaxGainTrace();
        Assert.False(t.SetDisplayTransform(CubeTransform.dB20));
        Assert.False(t.SetDisplayTransform(CubeTransform.Real));

        // Refused means nothing was written — the trace is untouched, not left half-set.
        Assert.Equal(CubeTransform.dB10, t.DisplayTransform);
    }

    /// <summary>The gating is Max Gain's alone; no other network trace's list changed.</summary>
    [Fact]
    public void NonMaxGainNetworkTrace_KeepsItsExistingTransformList()
    {
        var before = TraceRowViewModel.BuildTransformItems(
            isCubeBound: false, PlotType.Rect, isComplexData: true, DerivedParameters.None);
        var mu = TraceRowViewModel.BuildTransformItems(
            isCubeBound: false, PlotType.Rect, isComplexData: true, DerivedParameters.Mu);

        Assert.Equal(before.Select(i => (i.Transform, i.Enabled)), mu.Select(i => (i.Transform, i.Enabled)));
        Assert.True(before.Single(i => i.Transform == CubeTransform.dB20).Enabled);
        Assert.False(before.Single(i => i.Transform == CubeTransform.dB10).Enabled);
    }

    // ── 3. The combo is a real choice, and the default is the log form ──────────

    [Fact]
    public void SelectingMaxGain_DefaultsToTheLogForm()
    {
        // Exactly what the trace picker does: assign Derived and let the trace choose its form.
        var t = new Trace(UnilateralSnp(), MatrixType.S, 1, 0, DependentVarFormat.Complex);
        t.Derived = DerivedParameters.MaxGain;

        Assert.True(t.MaxGainIsLog);
        Assert.Equal(CubeTransform.dB10, t.DisplayTransform);
        Assert.Equal(20.0 * Math.Log10(S21), t.DataPointScalar(1e9), 4);
    }

    [Fact]
    public void MaxGain_LinearAndLogFormsPlotDifferentNumbers()
    {
        var t = MaxGainTrace();
        double db = t.DataPointScalar(1e9);

        Assert.True(t.SetDisplayTransform(CubeTransform.Mag));
        double linMag = t.DataPointScalar(1e9);

        Assert.True(t.SetDisplayTransform(CubeTransform.None));
        double linNone = t.DataPointScalar(1e9);

        Assert.Equal(20.0 * Math.Log10(S21), db, 4);   // = 10·log10(|S21|²)
        Assert.Equal(S21 * S21,              linMag, 4);
        Assert.Equal(linMag,                 linNone, 12);
        Assert.Equal(db, 10.0 * Math.Log10(linMag), 4);
    }

    /// <summary>
    /// The plotted curve must move too, not just the readout — the memoized derived array is keyed
    /// on the form, so a switch that returned the cached other form would draw the old curve.
    /// </summary>
    [Fact]
    public void SwitchingForm_RebuildsThePlottedPath()
    {
        var t = MaxGainTrace();
        t.BuildPath(PlotType.Rect, FreqUnit.GHz);
        float dbY = t.Points[0].Y;

        Assert.True(t.SetDisplayTransform(CubeTransform.Mag));
        t.BuildPath(PlotType.Rect, FreqUnit.GHz);
        float linY = t.Points[0].Y;

        Assert.Equal(20.0 * Math.Log10(S21), dbY,  3);
        Assert.Equal(S21 * S21,              linY, 3);
    }

    // ── The linear form is a REAL SCALAR, not a complex value ───────────────────

    /// <summary>
    /// "None" maps to <c>YAxis == Complex</c>, which every readout path used as its "this trace is
    /// complex" test. On a Max Gain trace that would format a plain gain as "16 + j0" and offer it
    /// an impedance it has no reflection coefficient for.
    /// </summary>
    [Fact]
    public void LinearMaxGain_ReadsOutAsAScalar_NotAsAComplexValue()
    {
        var t = MaxGainTrace();
        Assert.True(t.SetDisplayTransform(CubeTransform.None));
        Assert.False(t.YAxisIsComplexValue);

        var m = new Marker(t, 1e9, isMulti: false, isDelta: false, index: 1);
        string s = t.GetMarkerValString(m, showFilePrefix: false);

        Assert.StartsWith("Max Gain=", s);
        Assert.DoesNotContain("j", s);
        Assert.DoesNotContain("dB", s);
        Assert.False(t.MarkerShowsImpedance(m));
    }

    [Fact]
    public void LogMaxGain_ReadoutCarriesTheDbUnit()
    {
        var t = MaxGainTrace();
        var m = new Marker(t, 1e9, isMulti: false, isDelta: false, index: 1);

        string s = t.GetMarkerValString(m, showFilePrefix: false);
        Assert.StartsWith("Max Gain dB10=", s);
        Assert.EndsWith(" dB", s);
    }

    /// <summary>A stability circle is a genuine Γ-plane locus and stays complex.</summary>
    [Fact]
    public void StabilityCircle_IsStillAComplexTrace()
    {
        var t = new Trace(UnilateralSnp(), MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            Derived = DerivedParameters.LoadStabilityCircle,
        };
        Assert.True(t.YAxisIsComplexValue);
    }
}
