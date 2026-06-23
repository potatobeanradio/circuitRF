// ================================================================
//  HbSpectrumStage2Tests.cs
//  Gate tests for brief-hb-spectrum-2-order-axis (UI side)
//
//  T2 — Marker_HarmonicFamily_ReconstructsFreq (the regression):
//       marker on harmonic-family curve reads freq = order × f0 (not order × 1 GHz).
//  T3 — Marker_HarmonicFamily_FundamentalSwept:
//       when X = RFfreq sweep, marker freq = order × RFfreq[X-index].
//  T4 — Spectrum_XHarmonic_AxisInFreq:
//       X=harmonic + f0=5.5 GHz → Points.X positions in GHz; XLabel = "freq (GHz)".
//  T5 — NonHarmonic_Unaffected:
//       RFfreq-sweep family and Vds-family regression — no change from SetSpectrumFundamentals.
//  T6 — Table_HarmonicAxis_ShowsOrders:
//       CubeXValues holds integer orders; CubeXAxisName="harmonic".
// ================================================================

using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class HbSpectrumStage2Tests
{
    private static Trace MakeTrace() =>
        new(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);

    private static Marker MakeMarker(Trace t, float xPos, int curveIndex = 0)
    {
        var m = new Marker(t, freq: 1e9, isMulti: false, isDelta: false, index: 1);
        m.PositionStatic = new System.Numerics.Vector2(xPos, curveIndex);
        return m;
    }

    private static IReadOnlyList<(double, string?, Complex[]?, double[]?)> MakeRealCurves(
        double[] axisValues, double[] xVals)
    {
        var list = new List<(double, string?, Complex[]?, double[]?)>();
        foreach (double v in axisValues)
        {
            double[] rv = new double[xVals.Length];
            for (int i = 0; i < rv.Length; i++) rv[i] = v + i * 0.01;
            list.Add((v, null, null, rv));
        }
        return list;
    }

    // ── T2: the regression ────────────────────────────────────────────────────
    // Scenario: mag(HB1.V[1, :, "Vout", ~])
    //   RFfreq pinned at index 1 (f0 = 5.5 GHz), X = Pin, family = harmonic (orders {0,1,2,3}).
    // Marker on harmonic-1 curve must read freq=5.5 GHz (not 1 GHz = old frozen value).
    // Marker on harmonic-2 curve must read freq=11 GHz.
    [Fact]
    public void Marker_HarmonicFamily_ReconstructsFreq()
    {
        var t = MakeTrace();
        t.CubeName  = "HB1.V";
        t.Slice     = new[]
        {
            new AxisSlice("harmonic", AxisRole.FamilyIterate, 0),
            new AxisSlice("Pin",      AxisRole.KeepAsX,       0),
        };
        t.Transform = CubeTransform.None;

        double[] pinVals        = { -10.0, -5.0, 0.0 };
        double[] harmonicOrders = { 0.0, 1.0, 2.0, 3.0 };
        var curves = MakeRealCurves(harmonicOrders, pinVals);

        // RFfreq pinned at index 1 → f0 = 5.5 GHz constant across all Pin points
        t.SetSpectrumFundamentals(new double[] { 5.5e9, 5.5e9, 5.5e9 });
        t.SetFamilyData(pinVals, "Pin", "", "harmonic", curves,
                        PlotType.Rect, FreqUnit.GHz, familyAxisUnit: "");

        // Marker on harmonic-1 curve at Pin=-5.0 → xIdx=1, f0[1]=5.5e9 → freq=5.5 GHz
        {
            var m = MakeMarker(t, -5.0f, curveIndex: 1);
            var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
            string dump = string.Join(" | ", lines.ConvertAll(l => l.Text));

            Assert.True(lines.Exists(l => l.Text == "harmonic=1"),
                $"Expected 'harmonic=1' but got: {dump}");
            Assert.True(lines.Exists(l => l.Text.StartsWith("freq=") && l.Text.Contains("5.5")),
                $"Expected 'freq=5.5 GHz' but got: {dump}");
        }

        // Marker on harmonic-2 curve → order=2, freq = 2 × 5.5 = 11 GHz
        {
            var m = MakeMarker(t, -5.0f, curveIndex: 2);
            var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
            string dump = string.Join(" | ", lines.ConvertAll(l => l.Text));

            Assert.True(lines.Exists(l => l.Text == "harmonic=2"),
                $"Expected 'harmonic=2' but got: {dump}");
            Assert.True(lines.Exists(l => l.Text.StartsWith("freq=") && l.Text.Contains("11")),
                $"Expected 'freq=11 GHz' but got: {dump}");
        }
    }

    // ── T3: swept fundamental ─────────────────────────────────────────────────
    // Family = harmonic, X = RFfreq (swept from 1 GHz to 5 GHz in 5 steps).
    // At X index i, harmonic-k marker → freq = k × RFfreq[i].
    [Fact]
    public void Marker_HarmonicFamily_FundamentalSwept()
    {
        var t = MakeTrace();
        t.CubeName  = "HB1.V";
        t.Slice     = new[]
        {
            new AxisSlice("harmonic", AxisRole.FamilyIterate, 0),
            new AxisSlice("RFfreq",   AxisRole.KeepAsX,       0),
        };
        t.Transform = CubeTransform.None;

        double[] rffreqVals     = { 1e9, 2e9, 3e9, 4e9, 5e9 };
        double[] harmonicOrders = { 0.0, 1.0, 2.0 };
        var curves = MakeRealCurves(harmonicOrders, rffreqVals);

        // f0[i] = RFfreq[i]: the fundamental varies with X
        t.SetSpectrumFundamentals(rffreqVals);
        t.SetFamilyData(rffreqVals, "RFfreq", "Hz", "harmonic", curves,
                        PlotType.Rect, FreqUnit.GHz, familyAxisUnit: "");

        // Marker on harmonic-2 curve at RFfreq index 2 (3 GHz display = 3.0 GHz position)
        // f0[xIdx=2] = 3e9 → freq = 2 × 3e9 = 6 GHz
        var m = MakeMarker(t, 3.0f, curveIndex: 2);  // 3 GHz in display coords
        var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
        string dump = string.Join(" | ", lines.ConvertAll(l => l.Text));

        Assert.True(lines.Exists(l => l.Text == "harmonic=2"),
            $"Expected 'harmonic=2' but got: {dump}");
        Assert.True(lines.Exists(l => l.Text.StartsWith("freq=") && l.Text.Contains("6")),
            $"Expected 'freq=6 GHz' but got: {dump}");
    }

    // ── T4: spectrum plot X positions and X label ────────────────────────────
    // X = harmonic orders {0,1,2,3} with f0=5.5 GHz → display X = {0, 5.5, 11, 16.5} GHz.
    // Plot.XLabel must return "freq (GHz)".
    [Fact]
    public void Spectrum_XHarmonic_AxisInFreq()
    {
        var t = MakeTrace();
        t.CubeName  = "HB1.V";
        t.Slice     = new[] { new AxisSlice("harmonic", AxisRole.KeepAsX, 0) };
        t.Transform = CubeTransform.None;

        double[] orders = { 0.0, 1.0, 2.0, 3.0 };
        double[] vals   = { 1.0, 0.8, 0.4, 0.2 };
        const double f0 = 5.5e9;

        t.SetSpectrumFundamentals(new double[] { f0, f0, f0, f0 });
        t.SetCubeData(orders, complexValues: null, vals, "harmonic", "", PlotType.Rect, FreqUnit.GHz);

        // X positions in Points: order × f0 × GHz.Scale()
        Assert.Equal(4, t.Points.Count);
        Assert.Equal(0.0f,  t.Points[0].X, precision: 4);
        Assert.Equal(5.5f,  t.Points[1].X, precision: 4);
        Assert.Equal(11.0f, t.Points[2].X, precision: 4);
        Assert.Equal(16.5f, t.Points[3].X, precision: 4);

        // CubeXAxisName is "harmonic" → Plot.XLabel gives "freq (GHz)"
        Assert.Equal("harmonic", t.CubeXAxisName);

        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(t);
        Assert.Equal($"freq ({FreqUnit.GHz.Description()})", plot.XLabel);
    }

    // ── T5: non-harmonic traces unaffected (regression) ──────────────────────
    // RFfreq-sweep family (unit Hz): "RFfreq=… GHz", not "freq=".
    // Vds-sweep family (unit V): "Vds=… V".
    [Fact]
    public void NonHarmonic_Unaffected()
    {
        // Sub-case A: RFfreq family with Hz unit
        {
            var t = MakeTrace();
            t.CubeName  = "Pout";
            t.Slice     = new[]
            {
                new AxisSlice("RFfreq", AxisRole.FamilyIterate, 0),
                new AxisSlice("Pin",    AxisRole.KeepAsX,       0),
            };
            t.Transform = CubeTransform.None;

            double[] xVals      = { -10.0, -5.0, 0.0 };
            double[] rffreqVals = { 1e9, 2e9, 3e9 };
            var curves = MakeRealCurves(rffreqVals, xVals);

            // No spectrum fundamentals injected (not HB harmonic)
            t.SetFamilyData(xVals, "Pin", "", "RFfreq", curves,
                            PlotType.Rect, FreqUnit.GHz, familyAxisUnit: "Hz");

            var m = MakeMarker(t, -5.0f, curveIndex: 1);
            var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
            string dump = string.Join(" | ", lines.ConvertAll(l => l.Text));

            Assert.True(lines.Exists(l => l.Text.StartsWith("RFfreq=") && l.Text.Contains("GHz")),
                $"[RFfreq family] Expected 'RFfreq=… GHz' but got: {dump}");
            Assert.False(lines.Exists(l => l.Text.StartsWith("freq=")),
                $"[RFfreq family] Unexpected 'freq=' line: {dump}");
            Assert.False(lines.Exists(l => l.Text.StartsWith("harmonic=")),
                $"[RFfreq family] Unexpected 'harmonic=' line: {dump}");
        }

        // Sub-case B: Vds family with V unit
        {
            var t = MakeTrace();
            t.CubeName  = "Ids";
            t.Slice     = new[]
            {
                new AxisSlice("Vds", AxisRole.FamilyIterate, 0),
                new AxisSlice("Vgs", AxisRole.KeepAsX,       0),
            };
            t.Transform = CubeTransform.None;

            double[] xVals  = { 0.5, 1.0, 1.5 };
            double[] vdsVals = { 24.0, 48.0, 72.0 };
            var curves = MakeRealCurves(vdsVals, xVals);

            t.SetFamilyData(xVals, "Vgs", "V", "Vds", curves,
                            PlotType.Rect, FreqUnit.GHz, familyAxisUnit: "V");

            var m = MakeMarker(t, 1.0f, curveIndex: 1);
            var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
            string dump = string.Join(" | ", lines.ConvertAll(l => l.Text));

            Assert.True(lines.Exists(l => l.Text.StartsWith("Vds=") && l.Text.EndsWith(" V")),
                $"[Vds family] Expected 'Vds=… V' but got: {dump}");
            Assert.False(lines.Exists(l => l.Text.StartsWith("freq=")),
                $"[Vds family] Unexpected 'freq=' line: {dump}");
            Assert.False(lines.Exists(l => l.Text.StartsWith("harmonic=")),
                $"[Vds family] Unexpected 'harmonic=' line: {dump}");
        }
    }

    // ── T6: table over harmonic axis shows integer orders ───────────────────
    // CubeXValues stores raw integer orders; CubeXAxisName="harmonic".
    // TableRenderer will read CubeXValues directly (no freq scaling for unit "").
    [Fact]
    public void Table_HarmonicAxis_ShowsOrders()
    {
        var t = MakeTrace();
        t.CubeName  = "V";
        t.Slice     = new[] { new AxisSlice("harmonic", AxisRole.KeepAsX, 0) };
        t.Transform = CubeTransform.None;

        double[] orders = { 0.0, 1.0, 2.0, 3.0 };
        double[] vals   = { 1.0, 0.8, 0.4, 0.2 };

        t.SetSpectrumFundamentals(new double[] { 2e9, 2e9, 2e9, 2e9 });
        t.SetCubeData(orders, complexValues: null, vals, "harmonic", "", PlotType.Table, FreqUnit.GHz);

        // CubeXAxisName must be "harmonic"
        Assert.Equal("harmonic", t.CubeXAxisName);

        // CubeXValues must hold integer orders, not Hz values
        Assert.NotNull(t.CubeXValues);
        Assert.Equal(4, t.CubeXValues!.Count);
        for (int k = 0; k < 4; k++)
            Assert.Equal((double)k, t.CubeXValues[k], precision: 10);

        // CubeXUnit must be "" (not "Hz") — stored as empty string, not null
        Assert.True(string.IsNullOrEmpty(t.CubeXUnit), $"Expected empty unit but got: '{t.CubeXUnit}'");
    }
}
