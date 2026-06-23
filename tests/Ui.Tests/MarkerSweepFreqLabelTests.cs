// ================================================================
//  MarkerSweepFreqLabelTests.cs  —  Gate tests for brief-marker-sweep-freq-label
//
//  Tests:
//  T1 — Marker_Family_FreqVarSweep_UsesVarName: family axis "RFfreq"/Hz → "RFfreq=2 GHz",
//         no "freq=" and no "harmonic="
//  T2 — Marker_Family_FreqVarSweep_Untagged: family axis "RFfreq"/no-unit → "RFfreq=2",
//         no "harmonic="
//  T3 — Marker_Family_HarmonicAxis_Preserved: family axis "harmonic"/Hz still produces
//         "freq=… GHz" + "harmonic=<order>"
//  T4 — Marker_Family_NonFreqVar: family axis "Vds"/"V" → "Vds=48 V"
//  T5 — Marker_X_FreqVarSweep_UsesVarName: X axis "RFfreq"/Hz → "RFfreq=2 GHz", not "freq=";
//         X axis "harmonic"/Hz → "freq=… GHz" + "harmonic=<idx>" preserved
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class MarkerSweepFreqLabelTests
{
    private static Trace MakeTrace() =>
        new(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);

    private static Marker MakeMarker(Trace t, float xPos, int curveIndex = 0)
    {
        var m = new Marker(t, freq: 1e9, isMulti: false, isDelta: false, index: 1);
        m.PositionStatic = new System.Numerics.Vector2(xPos, curveIndex);
        return m;
    }

    private static IReadOnlyList<(double, string?, Complex[]?, double[]?)> MakeCurves(
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

    // T1 — family axis "RFfreq" with unit "Hz": readout uses the axis name (not "freq")
    //      and scales to the plot's freq unit; no "harmonic=" row.
    [Fact]
    public void Marker_Family_FreqVarSweep_UsesVarName()
    {
        var t = MakeTrace();
        t.CubeName  = "Pout_dBm";
        t.Slice     = new[]
        {
            new AxisSlice("RFfreq", AxisRole.FamilyIterate, 0),
            new AxisSlice("Pin",    AxisRole.KeepAsX,       0),
        };
        t.Transform = CubeTransform.None;

        double[] xVals      = { -10.0, -5.0, 0.0 };
        double[] rffreqVals = { 1e9, 2e9, 3e9 };
        var curves = MakeCurves(rffreqVals, xVals);

        // familyAxisUnit = "Hz" — what ParametricSweepEngine tags when var unit is GHz
        t.SetFamilyData(xVals, "Pin", "", "RFfreq", curves, PlotType.Rect, FreqUnit.GHz,
                        familyAxisUnit: "Hz");

        // Marker on curve index 1 (RFfreq=2e9 = 2 GHz)
        var m = MakeMarker(t, -5.0f, curveIndex: 1);
        var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
        string dump = string.Join(" | ", lines.ConvertAll(l => l.Text));

        // Must show variable name "RFfreq" and scale to GHz
        Assert.True(lines.Exists(l => l.Text.StartsWith("RFfreq=", StringComparison.Ordinal)
                                   && l.Text.Contains("GHz")),
            $"Expected 'RFfreq=… GHz' but got: {dump}");

        // Must NOT use hardcoded "freq=" label
        Assert.False(lines.Exists(l => l.Text.StartsWith("freq=", StringComparison.Ordinal)),
            $"Unexpected 'freq=' line: {dump}");

        // Must NOT emit a "harmonic=" row (no harmonic concept for a freq sweep)
        Assert.False(lines.Exists(l => l.Text.StartsWith("harmonic=", StringComparison.Ordinal)),
            $"Unexpected 'harmonic=' line: {dump}");
    }

    // T2 — same scenario but no unit tag: "RFfreq=2" (raw value, no GHz scaling, no harmonic)
    [Fact]
    public void Marker_Family_FreqVarSweep_Untagged()
    {
        var t = MakeTrace();
        t.CubeName  = "Pout_dBm";
        t.Slice     = new[]
        {
            new AxisSlice("RFfreq", AxisRole.FamilyIterate, 0),
            new AxisSlice("Pin",    AxisRole.KeepAsX,       0),
        };
        t.Transform = CubeTransform.None;

        double[] xVals      = { -10.0, -5.0, 0.0 };
        double[] rffreqVals = { 1.0, 2.0, 3.0 };   // unitless indices, e.g. "2 GHz" already
        var curves = MakeCurves(rffreqVals, xVals);

        // familyAxisUnit = "" (no unit tag)
        t.SetFamilyData(xVals, "Pin", "", "RFfreq", curves, PlotType.Rect, FreqUnit.GHz,
                        familyAxisUnit: "");

        var m = MakeMarker(t, -5.0f, curveIndex: 1);
        var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
        string dump = string.Join(" | ", lines.ConvertAll(l => l.Text));

        // Shows axis name with raw value, no unit suffix
        Assert.True(lines.Exists(l => l.Text.StartsWith("RFfreq=", StringComparison.Ordinal)),
            $"Expected 'RFfreq=…' but got: {dump}");

        // No freq= label
        Assert.False(lines.Exists(l => l.Text.StartsWith("freq=", StringComparison.Ordinal)),
            $"Unexpected 'freq=' line: {dump}");

        // No harmonic= row
        Assert.False(lines.Exists(l => l.Text.StartsWith("harmonic=", StringComparison.Ordinal)),
            $"Unexpected 'harmonic=' line: {dump}");
    }

    // T3 — the genuine HB "harmonic" family axis (integer orders, unit ""; stage 2) produces
    //       "harmonic=<order>" + "freq=… GHz" (reconstructed from order × injected f0)
    [Fact]
    public void Marker_Family_HarmonicAxis_Preserved()
    {
        var t = MakeTrace();
        t.CubeName  = "V";
        t.Slice     = new[]
        {
            new AxisSlice("harmonic", AxisRole.FamilyIterate, 0),
            new AxisSlice("Pin",      AxisRole.KeepAsX,       0),
        };
        t.Transform = CubeTransform.None;

        double[] xVals          = { -10.0, -5.0, 0.0 };
        double[] harmonicOrders = { 0.0, 1.0, 2.0 };  // integer orders (not Hz values)
        var curves = MakeCurves(harmonicOrders, xVals);

        // Inject f0 = 1 GHz constant across all X points, then set family data.
        t.SetSpectrumFundamentals(new double[] { 1e9, 1e9, 1e9 });
        t.SetFamilyData(xVals, "Pin", "", "harmonic", curves, PlotType.Rect, FreqUnit.GHz,
                        familyAxisUnit: "");

        // Marker on curve index 1 → order=1, xIdx=1 (Pin=-5.0), f0=1e9 → freq=1 GHz
        var m = MakeMarker(t, -5.0f, curveIndex: 1);
        var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
        string dump = string.Join(" | ", lines.ConvertAll(l => l.Text));

        // Must produce "freq=… GHz" (reconstructed from order × f0)
        Assert.True(lines.Exists(l => l.Text.StartsWith("freq=", StringComparison.Ordinal)
                                   && l.Text.Contains("GHz")),
            $"Expected 'freq=… GHz' for harmonic axis but got: {dump}");

        // Must produce "harmonic=<integer>" row
        Assert.True(lines.Exists(l => l.Text.StartsWith("harmonic=", StringComparison.Ordinal)),
            $"Expected 'harmonic=…' row for harmonic axis but got: {dump}");

        // "harmonic=" row must not contain "GHz"
        Assert.False(lines.Exists(l => l.Text.StartsWith("harmonic=", StringComparison.Ordinal)
                                    && l.Text.Contains("GHz")),
            $"'harmonic=' line must not contain 'GHz': {dump}");
    }

    // T4 — non-frequency family variable: "Vds=48 V"
    [Fact]
    public void Marker_Family_NonFreqVar()
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
        var curves = MakeCurves(vdsVals, xVals);

        t.SetFamilyData(xVals, "Vgs", "V", "Vds", curves, PlotType.Rect, FreqUnit.GHz,
                        familyAxisUnit: "V");

        // Marker on curve index 1 (Vds=48)
        var m = MakeMarker(t, 1.0f, curveIndex: 1);
        var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
        string dump = string.Join(" | ", lines.ConvertAll(l => l.Text));

        Assert.True(lines.Exists(l => l.Text.StartsWith("Vds=", StringComparison.Ordinal)
                                   && l.Text.EndsWith(" V", StringComparison.Ordinal)),
            $"Expected 'Vds=… V' but got: {dump}");

        Assert.False(lines.Exists(l => l.Text.StartsWith("freq=", StringComparison.Ordinal)),
            $"Unexpected 'freq=' line: {dump}");

        Assert.False(lines.Exists(l => l.Text.StartsWith("harmonic=", StringComparison.Ordinal)),
            $"Unexpected 'harmonic=' line: {dump}");
    }

    // T5 — X-axis: "RFfreq"/Hz → "RFfreq=2 GHz", not "freq=";
    //              X-axis "harmonic"/"" (stage 2) + injected f0 → "freq=… GHz" + "harmonic=<order>"
    [Fact]
    public void Marker_X_FreqVarSweep_UsesVarName()
    {
        // Sub-case A: non-harmonic freq X axis uses variable name
        {
            var t = MakeTrace();
            t.CubeName  = "Pout_dBm";
            t.Slice     = new[] { new AxisSlice("RFfreq", AxisRole.KeepAsX, 0) };
            t.Transform = CubeTransform.None;

            double[] xVals = { 1e9, 2e9, 3e9 };
            double[] yVals = { 10.0, 11.0, 12.0 };
            t.SetCubeData(xVals, complexValues: null, yVals, "RFfreq", "Hz", PlotType.Rect, FreqUnit.GHz);

            var m = MakeMarker(t, (float)2e9);
            var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
            string dump = string.Join(" | ", lines.ConvertAll(l => l.Text));

            Assert.True(lines.Exists(l => l.Text.StartsWith("RFfreq=", StringComparison.Ordinal)
                                       && l.Text.Contains("GHz")),
                $"[RFfreq X] Expected 'RFfreq=… GHz' but got: {dump}");

            Assert.False(lines.Exists(l => l.Text.StartsWith("freq=", StringComparison.Ordinal)),
                $"[RFfreq X] Unexpected 'freq=' label: {dump}");
        }

        // Sub-case B: genuine harmonic X axis (integer orders, unit "", stage 2) + injected f0
        //             → "freq=… GHz" + "harmonic=<order>"
        {
            var t = MakeTrace();
            t.CubeName  = "V";
            t.Slice     = new[] { new AxisSlice("harmonic", AxisRole.KeepAsX, 0) };
            t.Transform = CubeTransform.None;

            double[] xVals = { 0.0, 1.0, 2.0 };   // integer orders
            double[] yVals = { 1.0, 0.5, 0.2 };
            // f0 = 2 GHz → order-1 display position = 1 * 2e9 * 1e-9 = 2.0 in GHz units
            t.SetSpectrumFundamentals(new double[] { 2e9, 2e9, 2e9 });
            t.SetCubeData(xVals, complexValues: null, yVals, "harmonic", "", PlotType.Rect, FreqUnit.GHz);

            // Marker at 2.0 (GHz display) → nearest to order-1 point at 2.0 GHz → rawIdx=1
            var m = MakeMarker(t, 2.0f);
            var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
            string dump = string.Join(" | ", lines.ConvertAll(l => l.Text));

            Assert.True(lines.Exists(l => l.Text.StartsWith("freq=", StringComparison.Ordinal)
                                       && l.Text.Contains("GHz")),
                $"[harmonic X] Expected 'freq=… GHz' but got: {dump}");

            Assert.True(lines.Exists(l => l.Text.StartsWith("harmonic=", StringComparison.Ordinal)),
                $"[harmonic X] Expected 'harmonic=…' row but got: {dump}");
        }
    }
}
