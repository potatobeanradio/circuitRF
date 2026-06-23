// ================================================================
//  SweepAxisMarkerUnitTests.cs  —  Gate tests for brief-sweep-axis-marker-units
//
//  Tests:
//  T1 — Marker_XReadout_FreqVar: freq X axis → "freq=… GHz" (not "RFfreq=2000000000")
//  T2 — Marker_XReadout_NonFreqVar: non-freq X axis appends unit ("Vds=0.048 V")
//  T3 — Marker_FamilyReadout_NonFreqVar: family else-branch appends unit ("Vgs=1 V")
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class SweepAxisMarkerUnitTests
{
    private static Trace MakeTrace() =>
        new(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);

    private static Marker MakeMarker(Trace t, float xPos, int curveIndex = 0)
    {
        var m = new Marker(t, freq: 1e9, isMulti: false, isDelta: false, index: 1);
        m.PositionStatic = new System.Numerics.Vector2(xPos, curveIndex);
        return m;
    }

    // T1 — when X axis is named "RFfreq" with unit "Hz", the readout shows "RFfreq=2 GHz"
    //      (axis name used, not hardcoded "freq"; value is unit-scaled, not raw Hz)
    [Fact]
    public void Marker_XReadout_FreqVar()
    {
        var t = MakeTrace();
        t.CubeName  = "PAE";
        t.Slice     = new[] { new AxisSlice("RFfreq", AxisRole.KeepAsX, 0) };
        t.Transform = CubeTransform.None;

        double[] xVals = { 1e9, 2e9, 3e9 };
        double[] yVals = { 0.1, 0.2, 0.3 };
        // xUnit = "Hz" — what ParametricSweepEngine writes when origVar.Unit = "GHz"
        t.SetCubeData(xVals, complexValues: null, yVals, "RFfreq", "Hz", PlotType.Rect, FreqUnit.GHz);

        // Place marker at x=2e9 (2 GHz)
        var m = MakeMarker(t, (float)2e9);
        var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);

        // Non-harmonic freq axis → axis name + scaled value → "RFfreq=2 GHz"
        bool hasVarLine = lines.Exists(l => l.Text.StartsWith("RFfreq=", StringComparison.Ordinal)
                                         && l.Text.Contains("GHz"));
        Assert.True(hasVarLine,
            $"Expected 'RFfreq=… GHz' line but got: {string.Join(" | ", lines.ConvertAll(l => l.Text))}");

        // Must NOT use hardcoded "freq=" label
        bool hasFreqLabel = lines.Exists(l => l.Text.StartsWith("freq=", StringComparison.Ordinal));
        Assert.False(hasFreqLabel,
            $"Unexpected hardcoded 'freq=' label: {string.Join(" | ", lines.ConvertAll(l => l.Text))}");
    }

    // T2 — when X axis carries unit "V", the readout appends "V" ("Vds=0.048 V")
    [Fact]
    public void Marker_XReadout_NonFreqVar()
    {
        var t = MakeTrace();
        t.CubeName  = "Ids";
        t.Slice     = new[] { new AxisSlice("Vds", AxisRole.KeepAsX, 0) };
        t.Transform = CubeTransform.None;

        double[] xVals = { 0.0, 0.048, 0.1 };
        double[] yVals = { 0.0, 0.012, 0.025 };
        t.SetCubeData(xVals, complexValues: null, yVals, "Vds", "V", PlotType.Rect, FreqUnit.GHz);

        var m = MakeMarker(t, 0.048f);
        var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);

        bool hasUnitLine = lines.Exists(l => l.Text.StartsWith("Vds=", StringComparison.Ordinal)
                                          && l.Text.EndsWith(" V", StringComparison.Ordinal));
        Assert.True(hasUnitLine,
            $"Expected 'Vds=… V' line but got: {string.Join(" | ", lines.ConvertAll(l => l.Text))}");
    }

    // T3 — family else-branch appends familyAxisUnit ("Vgs=1 V")
    [Fact]
    public void Marker_FamilyReadout_NonFreqVar()
    {
        var t = MakeTrace();
        t.CubeName  = "Ids";
        t.Slice     = new[]
        {
            new AxisSlice("Vgs", AxisRole.FamilyIterate, 0),
            new AxisSlice("Vds", AxisRole.KeepAsX, 0),
        };
        t.Transform = CubeTransform.None;

        double[] xVals  = { 0.0, 0.1, 0.2 };
        double[] vgsVals = { 0.5, 1.0, 1.5 };

        var curves = new List<(double, string?, Complex[]?, double[]?)>();
        foreach (double vgs in vgsVals)
        {
            double[] rv = { vgs * 0.0, vgs * 0.1, vgs * 0.2 };
            curves.Add((vgs, null, null, rv));
        }

        // familyAxisUnit = "V" — what PlotInspectorViewModel will pass once the axis is tagged "V"
        t.SetFamilyData(xVals, "Vds", "V", "Vgs", curves, PlotType.Rect, FreqUnit.GHz,
                        familyAxisUnit: "V");

        // Marker on curve index 1 (Vgs=1.0), X≈0.1
        var m = MakeMarker(t, 0.1f, curveIndex: 1);
        var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);

        bool hasFamilyUnit = lines.Exists(l => l.Text.StartsWith("Vgs=", StringComparison.Ordinal)
                                            && l.Text.EndsWith(" V", StringComparison.Ordinal));
        Assert.True(hasFamilyUnit,
            $"Expected 'Vgs=… V' line but got: {string.Join(" | ", lines.ConvertAll(l => l.Text))}");
    }
}
