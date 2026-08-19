// ================================================================
//  PlotVersusTests.cs  —  "plot versus" (Y vs X): a trace whose X
//  data is another quantity, not the swept axis.
//
//  Anchors the whole feature: the separator grammar, single-curve and
//  family resolution, the point-count/real/plot-type gates, the
//  cross-source X, the per-trace X label rows, the Table's index
//  pairing, the marker readout, and .cdd round-trip.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PlotVersusTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────
    //
    //  The PA case the feature exists for: a Pin sweep (5 points) with Gain and
    //  Pout measured at each point, and a second fixture that also sweeps RFfreq
    //  so Gain-vs-Pout can be a family.

    private static readonly double[] PinVals  = { -10, -5, 0, 5, 10 };
    private static readonly double[] GainVals = { 15.0, 14.9, 14.5, 13.0, 10.0 };
    private static readonly double[] PoutVals = {   5.0,  9.9, 14.5, 18.0, 20.0 };

    private static DataSet MakeSweptDs()
    {
        var pin = new Axis("Pin", PinVals, "dBm");
        var ds  = new DataSet();
        ds.Add("Gain", new DataCube(new[] { pin }, (double[])GainVals.Clone()));
        ds.Add("Pout", new DataCube(new[] { pin }, (double[])PoutVals.Clone()));
        return ds;
    }

    /// <summary>Gain[Pin, RFfreq] and Pout[Pin, RFfreq] — Pout differs per frequency, which is the
    /// whole reason a versus family needs a per-curve X.</summary>
    private static DataSet MakeFamilyDs()
    {
        var pin  = new Axis("Pin",    PinVals, "dBm");
        var freq = new Axis("RFfreq", new[] { 2.0e9, 2.4e9 }, "Hz");

        var gain = new double[PinVals.Length * 2];
        var pout = new double[PinVals.Length * 2];
        for (int i = 0; i < PinVals.Length; i++)
            for (int k = 0; k < 2; k++)
            {
                gain[i * 2 + k] = GainVals[i] - k * 0.5;     // 2.4 GHz is 0.5 dB down
                pout[i * 2 + k] = PoutVals[i] - k * 1.0;     // and 1 dB lower Pout
            }

        var ds = new DataSet();
        ds.Add("Gain", new DataCube(new[] { pin, freq }, gain));
        ds.Add("Pout", new DataCube(new[] { pin, freq }, pout));
        return ds;
    }

    private static Trace MakeTrace() =>
        new(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);

    private static Trace MakeVersusTrace(string cube, string xSpec, params AxisSlice[] slice)
    {
        var t = MakeTrace();
        t.CubeName = cube;
        t.Slice    = slice;
        t.XSpec    = xSpec;
        t.Expression = t.CubeShorthand;
        return t;
    }

    // ── 1. The separator grammar ─────────────────────────────────────────────

    [Theory]
    [InlineData("Gain vs Pout",       "Gain", "Pout")]
    [InlineData("Gain versus Pout",   "Gain", "Pout")]
    [InlineData("Gain VS Pout",       "Gain", "Pout")]
    [InlineData("dB20(A[:, 1]) vs mag(B[:, 0])", "dB20(A[:, 1])", "mag(B[:, 0])")]
    public void Split_SeparatesYFromX(string text, string y, string x)
    {
        Assert.True(VersusSpec.TrySplit(text, out var ySide, out var xSide, out var err), err);
        Assert.Equal(y, ySide);
        Assert.Equal(x, xSide);
        Assert.Equal("", err);
    }

    [Fact]
    public void Split_IgnoresSeparatorInsideBracketsAndQuotes()
    {
        // A quoted net name may contain the word, and a bracket body is never top level.
        Assert.False(VersusSpec.TrySplit("V[:, \"a vs b\"]", out _, out _, out var e1));
        Assert.Equal("", e1);
        Assert.False(VersusSpec.TrySplit("mag(A[1 vs 2])", out _, out _, out var e2));
        Assert.Equal("", e2);
    }

    [Fact]
    public void Split_RejectsTwoSeparators_AndEmptySides()
    {
        Assert.False(VersusSpec.TrySplit("A vs B vs C", out _, out _, out var e1));
        Assert.Contains("Only one", e1);

        Assert.False(VersusSpec.TrySplit("Gain vs ", out _, out _, out var e2));
        Assert.Contains("each side", e2);
    }

    [Fact]
    public void Split_PlainSpecIsNotVersus()
    {
        Assert.False(VersusSpec.TrySplit("dB20(HB1.V[:, \"Vout\", 1])", out _, out _, out var err));
        Assert.Equal("", err);
        Assert.False(VersusSpec.ContainsSeparator("Gain"));
    }

    // ── 2. Single-curve resolution ───────────────────────────────────────────

    [Fact]
    public void Versus_XComesFromTheXSpec_NotTheSweptAxis()
    {
        var ds = MakeSweptDs();
        var t  = MakeVersusTrace("Gain", "Pout", new AxisSlice("Pin", AxisRole.KeepAsX, 0));

        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);

        Assert.Null(t.ExpressionError);
        Assert.Equal(PoutVals, t.CubeXValues!.ToArray());
        Assert.Equal("Pout", t.CubeXAxisName);
        // Cube VALUES carry no unit anywhere in the data model — so a versus X has none.
        Assert.Null(t.CubeXUnit);

        // The rendered points pair Pout (X) with Gain (Y), sample for sample.
        Assert.Equal(PinVals.Length, t.Points.Count);
        for (int i = 0; i < PinVals.Length; i++)
        {
            Assert.Equal((float)PoutVals[i], t.Points[i].X, 4);
            Assert.Equal((float)GainVals[i], t.Points[i].Y, 4);
        }
    }

    [Fact]
    public void Versus_ShorthandRoundTripsThroughTheSpecText()
    {
        var t = MakeVersusTrace("Gain", "Pout", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        Assert.Equal("Gain vs Pout", t.CubeShorthand);
        Assert.True(t.IsVersus);
    }

    [Fact]
    public void Versus_PointCountMismatch_IsReportedAndRendersNothing()
    {
        var ds  = MakeSweptDs();
        // A 3-point X against a 5-point Y.
        ds.Add("PoutShort", new DataCube(new[] { new Axis("Pin", new[] { -10.0, 0.0, 10.0 }, "dBm") },
                                         new[] { 1.0, 2.0, 3.0 }));

        var t = MakeVersusTrace("Gain", "PoutShort", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);

        Assert.NotNull(t.ExpressionError);
        Assert.Contains("3 point", t.ExpressionError);
        Assert.Contains("5", t.ExpressionError);
        Assert.Empty(t.Points);
    }

    [Fact]
    public void Versus_ComplexX_IsRefusedWithAnActionableMessage()
    {
        var ds = MakeSweptDs();
        var pin = new Axis("Pin", PinVals, "dBm");
        ds.Add("Vout", new DataCube(new[] { pin }, PinVals.Select(v => new Complex(v, 1.0)).ToArray()));

        var t = MakeVersusTrace("Gain", "Vout", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);

        Assert.NotNull(t.ExpressionError);
        Assert.Contains("must be real", t.ExpressionError);
        Assert.Empty(t.Points);

        // …and the named remedy actually works.
        t.XSpec = "mag(Vout)";
        t.ExpressionError = null;
        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
        Assert.Null(t.ExpressionError);
        Assert.Equal(PinVals.Length, t.Points.Count);
    }

    [Fact]
    public void Versus_IsRefusedOnSmithAndPolar()
    {
        var ds = MakeSweptDs();
        foreach (var pt in new[] { PlotType.Smith, PlotType.Polar })
        {
            var t = MakeVersusTrace("Gain", "Pout", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
            PlotInspectorViewModel.SetCubeDataFrom(t, ds, pt, FreqUnit.GHz);
            Assert.NotNull(t.ExpressionError);
            Assert.Contains("Rect and Table", t.ExpressionError);
            Assert.Empty(t.Points);
        }
    }

    [Fact]
    public void Versus_ScalarY_IsRefused()
    {
        var ds = MakeSweptDs();
        var t  = MakeVersusTrace("Gain", "Pout", new AxisSlice("Pin", AxisRole.PinToIndex, 2));
        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Table, FreqUnit.GHz);

        Assert.NotNull(t.ExpressionError);
        Assert.Contains("swept Y", t.ExpressionError);
    }

    [Fact]
    public void Versus_XSideTransformIsApplied()
    {
        var ds = MakeSweptDs();
        var t  = MakeVersusTrace("Gain", "dB10(Pout)", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);

        Assert.Null(t.ExpressionError);
        for (int i = 0; i < PoutVals.Length; i++)
            Assert.Equal(10.0 * Math.Log10(PoutVals[i]), t.CubeXValues![i], 9);
    }

    // ── 3. Families ──────────────────────────────────────────────────────────

    [Fact]
    public void VersusFamily_EachCurveCarriesItsOwnX()
    {
        var ds = MakeFamilyDs();
        var t  = MakeVersusTrace("Gain", "Pout",
                    new AxisSlice("Pin",    AxisRole.KeepAsX,       0),
                    new AxisSlice("RFfreq", AxisRole.FamilyIterate, 0));

        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);

        Assert.Null(t.ExpressionError);
        Assert.True(t.IsFamily);
        Assert.Equal(2, t.FamilyCurves.Count);
        Assert.Equal("RFfreq", t.FamilyAxisName);
        Assert.Equal("Pout", t.CubeXAxisName);

        for (int k = 0; k < 2; k++)
        {
            var fc = t.FamilyCurves[k];
            Assert.NotNull(fc.RawX);
            for (int i = 0; i < PinVals.Length; i++)
            {
                Assert.Equal(PoutVals[i] - k * 1.0, fc.RawX![i], 9);
                // …and the drawn geometry uses that curve's own X, not curve 0's.
                Assert.Equal((float)(PoutVals[i] - k * 1.0), fc.Points[i].X, 4);
                Assert.Equal((float)(GainVals[i] - k * 0.5), fc.Points[i].Y, 4);
            }
        }

        // The two curves genuinely differ in X — the property a shared X array cannot express.
        Assert.NotEqual(t.FamilyCurves[0].RawX![0], t.FamilyCurves[1].RawX![0]);
    }

    [Fact]
    public void VersusFamily_BareXSideInheritsTheFamilyAxisByName()
    {
        // "Gain[:, ~] vs Pout" — the X side names no roles at all and must adopt the Y side's.
        var ds = MakeFamilyDs();
        var t  = MakeVersusTrace("Gain", "Pout",
                    new AxisSlice("Pin",    AxisRole.KeepAsX,       0),
                    new AxisSlice("RFfreq", AxisRole.FamilyIterate, 0));
        Assert.Equal("Gain[:, ~] vs Pout", t.CubeShorthand);

        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);
        Assert.Null(t.ExpressionError);
        Assert.Equal(2, t.FamilyCurves.Count);
    }

    [Fact]
    public void VersusFamily_ExplicitXSideMustIterateTheSameAxis()
    {
        var ds = MakeFamilyDs();
        var t  = MakeVersusTrace("Gain", "Pout[:, 0]",     // pinned, not iterated
                    new AxisSlice("Pin",    AxisRole.KeepAsX,       0),
                    new AxisSlice("RFfreq", AxisRole.FamilyIterate, 0));

        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);

        Assert.NotNull(t.ExpressionError);
        Assert.Contains("RFfreq", t.ExpressionError);
        Assert.Empty(t.FamilyCurves);
    }

    [Fact]
    public void OrdinaryFamily_StillSharesOneX()
    {
        // The non-versus family path must be untouched: no per-curve X, shared axis values.
        var ds = MakeFamilyDs();
        var t  = MakeTrace();
        t.CubeName = "Gain";
        t.Slice    = new[]
        {
            new AxisSlice("Pin",    AxisRole.KeepAsX,       0),
            new AxisSlice("RFfreq", AxisRole.FamilyIterate, 0),
        };
        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);

        Assert.Equal(2, t.FamilyCurves.Count);
        Assert.All(t.FamilyCurves, fc => Assert.Null(fc.RawX));
        Assert.Equal(PinVals, t.CubeXValues!.ToArray());
        Assert.Equal("Pin", t.CubeXAxisName);
    }

    // ── 4. Cross-source X ────────────────────────────────────────────────────

    [Fact]
    public void Versus_XMayComeFromADifferentDataSet()
    {
        var yDs = MakeSweptDs();

        // A second file holding only Pout, with values of its own (a "measured" run).
        var measured = new double[] { 4.0, 9.0, 14.0, 17.5, 19.5 };
        var xDs = new DataSet();
        xDs.Add("Pout", new DataCube(new[] { new Axis("Pin", PinVals, "dBm") }, measured));

        var t = MakeVersusTrace("Gain", "Pout", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        PlotInspectorViewModel.SetCubeDataFrom(t, yDs, PlotType.Rect, FreqUnit.GHz, xDs);

        Assert.Null(t.ExpressionError);
        Assert.Equal(measured, t.CubeXValues!.ToArray());   // the X file's values, not the Y file's
    }

    [Fact]
    public void Versus_CrossSourceCountMismatch_IsCaughtByTheSameGate()
    {
        var yDs = MakeSweptDs();
        var xDs = new DataSet();
        xDs.Add("Pout", new DataCube(new[] { new Axis("Pin", new[] { 0.0, 1.0 }, "dBm") },
                                     new[] { 1.0, 2.0 }));

        var t = MakeVersusTrace("Gain", "Pout", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        PlotInspectorViewModel.SetCubeDataFrom(t, yDs, PlotType.Rect, FreqUnit.GHz, xDs);

        Assert.NotNull(t.ExpressionError);
        Assert.Contains("2 point", t.ExpressionError);
        Assert.Empty(t.Points);
    }

    // ── 5. X-axis labels ─────────────────────────────────────────────────────

    [Fact]
    public void XLabel_ReadsTheXSpec_AndPerTraceRowsAppearOnlyWhenTheyDiffer()
    {
        var ds = MakeSweptDs();
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);

        var vsPout = MakeVersusTrace("Gain", "Pout", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        PlotInspectorViewModel.SetCubeDataFrom(vsPout, ds, PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(vsPout);

        Assert.Equal("Pout", plot.XLabel);
        Assert.False(plot.XLabelsDiffer);          // one trace: nothing to disagree with

        // A second trace against the ordinary swept axis → the two X quantities differ.
        var vsPin = MakeTrace();
        vsPin.CubeName = "Gain";
        vsPin.Slice    = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };
        PlotInspectorViewModel.SetCubeDataFrom(vsPin, ds, PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(vsPin);

        Assert.True(plot.XLabelsDiffer);
        Assert.Equal("Pout",       plot.XLabelFor(vsPout));
        Assert.Equal("Pin (dBm)",  plot.XLabelFor(vsPin));

        // Two traces sharing an X quantity keep the single centred label.
        plot.Traces.Remove(vsPin);
        var alsoPout = MakeVersusTrace("Gain", "Pout", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        PlotInspectorViewModel.SetCubeDataFrom(alsoPout, ds, PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(alsoPout);
        Assert.False(plot.XLabelsDiffer);
    }

    [Fact]
    public void XAxisUnitLabel_FollowsTheXQuantity_NotTheFrequencyUnit()
    {
        var ds   = MakeSweptDs();
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);

        var swept = MakeTrace();
        swept.CubeName = "Gain";
        swept.Slice    = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };
        PlotInspectorViewModel.SetCubeDataFrom(swept, ds, PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(swept);
        Assert.Equal("(dBm)", plot.XAxisUnitLabel);      // was hardcoded "(GHz)" for every Rect plot

        plot.Traces.Clear();
        var vs = MakeVersusTrace("Gain", "Pout", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        PlotInspectorViewModel.SetCubeDataFrom(vs, ds, PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(vs);
        Assert.Equal("", plot.XAxisUnitLabel);           // a versus X has no unit to claim
    }

    // ── 6. Table ─────────────────────────────────────────────────────────────

    [Fact]
    public void Table_VersusColumnPairsByIndex_AndKeepsSweepOrder()
    {
        // A deliberately NON-monotonic X: Pout folds back past compression, and one value repeats.
        var ds = MakeSweptDs();
        var folded = new double[] { 5.0, 12.0, 18.0, 18.0, 16.0 };
        ds.Add("PoutFold", new DataCube(new[] { new Axis("Pin", PinVals, "dBm") }, folded));

        var t = MakeVersusTrace("Gain", "PoutFold", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Table, FreqUnit.GHz);

        var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        plot.Traces.Add(t);
        var cols = TableRenderer.BuildColumns(plot);

        var xCol = cols.First(c => c.Kind == TableColKind.XAxis);
        var yCol = cols.First(c => c.Kind == TableColKind.TraceValue);
        Assert.True(xCol.PairByIndex);
        Assert.True(yCol.PairByIndex);
        Assert.Equal("PoutFold", xCol.Header);              // no unit — the quantity names itself
        Assert.Equal(folded, xCol.XValues);                  // unsorted, undeduplicated: 5 rows, not 4

        // Row 3 and row 4 share an X value; each must still read ITS OWN Y.
        Assert.Equal(GainVals[3].ToString("F3"), TableRenderer.FormatColumnCell(yCol, 3, plot));
        Assert.Equal(GainVals[2].ToString("F3"), TableRenderer.FormatColumnCell(yCol, 2, plot));
    }

    [Fact]
    public void Table_VersusFamily_EmitsAnXYColumnPairPerCurve()
    {
        var ds = MakeFamilyDs();
        var t  = MakeVersusTrace("Gain", "Pout",
                    new AxisSlice("Pin",    AxisRole.KeepAsX,       0),
                    new AxisSlice("RFfreq", AxisRole.FamilyIterate, 0));
        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Table, FreqUnit.GHz);

        var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        plot.Traces.Add(t);
        var cols = TableRenderer.BuildColumns(plot);

        // Two curves → two (X, Y) pairs, in curve order.
        Assert.Equal(4, cols.Count);
        Assert.Equal(TableColKind.XAxis,      cols[0].Kind);
        Assert.Equal(TableColKind.TraceValue, cols[1].Kind);
        Assert.Equal(TableColKind.XAxis,      cols[2].Kind);
        Assert.Equal(TableColKind.TraceValue, cols[3].Kind);
        Assert.All(cols, c => Assert.True(c.PairByIndex));

        Assert.StartsWith("Pout @ RFfreq", cols[0].Header);
        Assert.Contains("Gain",            cols[1].Header);

        // Each pair carries its own curve's X — and the second curve's is 1 dB lower.
        Assert.Equal(PoutVals[0],       cols[0].XValues[0], 9);
        Assert.Equal(PoutVals[0] - 1.0, cols[2].XValues[0], 9);

        // And each Y column reads its own curve.
        Assert.Equal((GainVals[0]).ToString("F3"),       TableRenderer.FormatColumnCell(cols[1], 0, plot));
        Assert.Equal((GainVals[0] - 0.5).ToString("F3"), TableRenderer.FormatColumnCell(cols[3], 0, plot));
    }

    // ── 7. Marker readout ────────────────────────────────────────────────────

    [Fact]
    public void Marker_ReadsTheXQuantityByName()
    {
        var ds = MakeSweptDs();
        var t  = MakeVersusTrace("Gain", "Pout", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);

        var m = new Marker(t, freq: 0.0, isMulti: false, isDelta: false, index: 1)
        {
            PositionStatic = new System.Numerics.Vector2((float)PoutVals[3], (float)GainVals[3]),
        };
        var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
        string dump = string.Join(" | ", lines.Select(l => l.Text));

        Assert.True(lines.Exists(l => l.Text.StartsWith("Pout=")), $"expected a Pout X row: {dump}");
        Assert.Contains("18", lines.First(l => l.Text.StartsWith("Pout=")).Text);
        Assert.DoesNotContain(lines, l => l.Text.StartsWith("Pin="));
        Assert.DoesNotContain(lines, l => l.Text.StartsWith("freq="));
    }

    [Fact]
    public void Marker_OnAVersusFamily_ReadsTheMarkedCurvesOwnX()
    {
        var ds = MakeFamilyDs();
        var t  = MakeVersusTrace("Gain", "Pout",
                    new AxisSlice("Pin",    AxisRole.KeepAsX,       0),
                    new AxisSlice("RFfreq", AxisRole.FamilyIterate, 0));
        PlotInspectorViewModel.SetCubeDataFrom(t, ds, PlotType.Rect, FreqUnit.GHz);

        // Marker on curve 1 (2.4 GHz), sample 3: its Pout is 1 dB below curve 0's.
        var m = new Marker(t, freq: 0.0, isMulti: false, isDelta: false, index: 1)
        {
            PositionStatic = new System.Numerics.Vector2((float)(PoutVals[3] - 1.0),
                                                         (float)(GainVals[3] - 0.5)),
        };
        var lines = t.BuildMarkerBoxLines(m, FreqUnit.GHz);
        string xRow = lines.First(l => l.Text.StartsWith("Pout=")).Text;

        Assert.Contains("17", xRow);      // 17.0, curve 1 — not curve 0's 18.0
    }

    // ── 8. Persistence ───────────────────────────────────────────────────────

    [Fact]
    public void Versus_RoundTripsThroughTheTraceConfig()
    {
        var t = MakeVersusTrace("Gain", "Pout", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        t.SourcePath  = "/results/run.npy";
        t.XSourcePath = "/results/measured.npy";

        var cfg = DataDisplayViewModel.BuildTraceConfig(t, configDir: "/results");
        Assert.Equal("Pout", cfg.XSpec);
        Assert.Equal("/results/measured.npy", cfg.XSourcePath);

        // A trace with no versus writes nothing — old .cdd files stay byte-compatible in shape.
        var plain = MakeTrace();
        plain.CubeName = "Gain";
        plain.Slice    = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };
        var plainCfg = DataDisplayViewModel.BuildTraceConfig(plain, configDir: "/results");
        Assert.Null(plainCfg.XSpec);
        Assert.Null(plainCfg.XSourcePath);
    }

    [Fact]
    public void Versus_CopyConstructorCarriesTheXBinding()
    {
        var t = MakeVersusTrace("Gain", "Pout", new AxisSlice("Pin", AxisRole.KeepAsX, 0));
        t.XSourcePath = "/results/measured.npy";
        t.XSourceAlias = "measured";

        var copy = new Trace(t);
        Assert.Equal("Pout", copy.XSpec);
        Assert.Equal("/results/measured.npy", copy.XSourcePath);
        Assert.Equal("measured", copy.XSourceAlias);
        Assert.Equal("Gain vs measured::Pout", copy.CubeShorthand);
    }
}
