// ================================================================
//  TableCubeTraceTests.cs  —  Gate tests for Table plot cube-trace support
//
//  Tests:
//  1. CubeShorthand_Format  — Trace.CubeShorthand produces index-form label
//  2. FormatCubeCell_Transform — dB20 and None transforms produce correct strings
//  3. GetSortedRowAxis_UsesCubeX — cube plot rows come from CubeXValues, not SNP freq
//  4. Cube_NoNaNForValidIndex — every valid index formats to a non-NaN cell (regression)
//  5. Parser_RoundTrip — CubeShorthand round-trips through CubeTraceSpecParser
//  6. Parser_BadNode_Invalid — bad label → parse failure with axis list in error
//  7. Parser_RankMismatch — wrong token count → parse failure
//  8. Parser_TwoColons — multiple ':' → parse failure
//  9. Parser_Transform — "db20 V[...]" → CubeTransform.dB20
// 10. InvalidState_BlankCells — Trace.InvalidSpecText → FormatCubeCell returns ""
// ================================================================

using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class TableCubeTraceTests
{
    // ---- Helpers -----------------------------------------------------------

    private static Trace MakeTrace() =>
        new Trace(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);

    private static Trace MakeRealCubeTrace(double[] xVals, double[] yVals, string axisName = "Pin")
    {
        var t = MakeTrace();
        t.CubeName  = "PAE";
        t.Slice     = new[] { new AxisSlice(axisName, AxisRole.KeepAsX, 0) };
        t.Transform = CubeTransform.None;
        t.SetCubeData(xVals, complexValues: null, yVals, axisName, "dBm", PlotType.Table, FreqUnit.GHz);
        return t;
    }

    private static Trace MakeComplexCubeTrace(double[] xVals, Complex[] cVals, CubeTransform transform,
                                               string axisName = "Pin")
    {
        var t = MakeTrace();
        t.CubeName  = "V";
        t.Slice     = new[] { new AxisSlice(axisName, AxisRole.KeepAsX, 0) };
        t.Transform = transform;
        t.SetCubeData(xVals, cVals, realValues: null, axisName, null, PlotType.Table, FreqUnit.GHz);
        return t;
    }

    // ---- Test 1: CubeShorthand_Format -------------------------------------

    [Fact]
    public void CubeShorthand_Format()
    {
        // Slice: [Vout pinned at index 0, harmonic pinned at index 1, Pin kept]
        // Index-form (documented fallback): V[0, 1, :]
        var t = MakeTrace();
        t.CubeName  = "V";
        t.Transform = CubeTransform.None;
        t.Slice = new[]
        {
            new AxisSlice("Vout",     AxisRole.PinToIndex, 0),
            new AxisSlice("harmonic", AxisRole.PinToIndex, 1),
            new AxisSlice("Pin",      AxisRole.KeepAsX,    0),
        };

        Assert.Equal("V[0, 1, :]", t.CubeShorthand);
    }

    [Fact]
    public void CubeShorthand_WithTransform_PrependsPrefix()
    {
        var t = MakeTrace();
        t.CubeName  = "V";
        t.Transform = CubeTransform.dB20;
        t.Slice = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };

        Assert.Equal("db20(V[:])", t.CubeShorthand);
    }

    [Fact]
    public void CubeShorthand_NonCube_FallsBackToShortDescription()
    {
        var t = MakeTrace();  // IsCubeBound = false
        Assert.Equal(t.ShortDescription, t.CubeShorthand);
    }

    // ---- Test 2: FormatCubeCell_Transform ----------------------------------

    [Fact]
    public void FormatCubeCell_Transform_dB20()
    {
        // Complex values: [z0=(1,0), z1=(10,0), z2=(0,1)]
        // At index 1: |z| = 10 → dB20 = 20*log10(10) = 20
        var xVals = new double[] { 0, 5, 10 };
        var cVals = new Complex[]
        {
            new Complex(1.0,  0.0),
            new Complex(10.0, 0.0),
            new Complex(0.0,  1.0),
        };
        var t = MakeComplexCubeTrace(xVals, cVals, CubeTransform.dB20);

        string cell1 = t.FormatCubeCell(1, PrecisionFormat.F, 3);
        Assert.Equal("20.000", cell1);

        // Index 0: |z| = 1 → dB20 = 0
        string cell0 = t.FormatCubeCell(0, PrecisionFormat.F, 3);
        Assert.Equal("0.000", cell0);
    }

    [Fact]
    public void FormatCubeCell_Transform_None_ShowsMA()
    {
        // None on complex → mag∠deg format
        var xVals = new double[] { 0 };
        var cVals = new Complex[] { new Complex(1.0, 0.0) };  // magnitude 1, phase 0
        var t = MakeComplexCubeTrace(xVals, cVals, CubeTransform.None);

        string cell = t.FormatCubeCell(0, PrecisionFormat.F, 3);
        // Should contain ∠ character (MA format)
        Assert.Contains("∠", cell);
        Assert.Contains("1.000", cell);
    }

    [Fact]
    public void FormatCubeCell_OutOfRange_ReturnsNaN()
    {
        var xVals = new double[] { 0, 5 };
        var yVals = new double[] { 1.0, 2.0 };
        var t = MakeRealCubeTrace(xVals, yVals);

        Assert.Equal("NaN", t.FormatCubeCell(-1, PrecisionFormat.F, 3));
        Assert.Equal("NaN", t.FormatCubeCell(2, PrecisionFormat.F, 3));
    }

    // ---- Test 3: GetSortedRowAxis_UsesCubeX --------------------------------

    [Fact]
    public void GetSortedRowAxis_CubePlot_ReturnsCubeXValues()
    {
        double[] pinValues = { -10, 0, 5, 10 };

        var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        var t1   = MakeRealCubeTrace(pinValues, new double[] { 1, 2, 3, 4 });
        var t2   = MakeRealCubeTrace(pinValues, new double[] { 5, 6, 7, 8 });
        plot.Traces.Add(t1);
        plot.Traces.Add(t2);

        double[] rows = TableRenderer.GetSortedRowAxis(plot);

        Assert.Equal(pinValues, rows);
    }

    [Fact]
    public void GetSortedRowAxis_CubePlot_UnionOfTwoTraces()
    {
        // Two traces with partially overlapping X axes → union
        var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        var t1   = MakeRealCubeTrace(new double[] { 0, 5 },    new double[] { 1, 2 });
        var t2   = MakeRealCubeTrace(new double[] { 5, 10, 15 }, new double[] { 3, 4, 5 });
        plot.Traces.Add(t1);
        plot.Traces.Add(t2);

        double[] rows = TableRenderer.GetSortedRowAxis(plot);

        Assert.Equal(new double[] { 0, 5, 10, 15 }, rows);
    }

    [Fact]
    public void GetSortedRowAxis_LegacySnpPlot_ReturnsFrequencies()
    {
        double[] snpFreqs = { 1e9, 2e9, 3e9 };
        var snp  = new SNP(snpFreqs, 2);
        var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        plot.Traces.Add(new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db));

        double[] rows = TableRenderer.GetSortedRowAxis(plot);

        // Should be the SNP frequencies (legacy path)
        Assert.Equal(snpFreqs, rows);
    }

    // ---- Test 4: Cube_NoNaNForValidIndex -----------------------------------

    [Fact]
    public void Cube_NoNaNForValidIndex_Real()
    {
        double[] xVals = { -10, 0, 5, 10 };
        double[] yVals = {  20, 25, 30, 33 };
        var t = MakeRealCubeTrace(xVals, yVals);

        for (int i = 0; i < xVals.Length; i++)
        {
            string cell = t.FormatCubeCell(i, PrecisionFormat.F, 3);
            Assert.NotEqual("NaN", cell);
        }
    }

    [Fact]
    public void Cube_NoNaNForValidIndex_Complex()
    {
        double[] xVals = { -10, 0, 5 };
        var cVals = new Complex[]
        {
            new Complex(0.9, 0.1),
            new Complex(0.5, 0.5),
            new Complex(0.1, 0.9),
        };
        // None transform → MA format (contains ∠, not "NaN")
        var t = MakeComplexCubeTrace(xVals, cVals, CubeTransform.None);

        for (int i = 0; i < xVals.Length; i++)
        {
            string cell = t.FormatCubeCell(i, PrecisionFormat.F, 3);
            Assert.NotEqual("NaN", cell);
            Assert.Contains("∠", cell);
        }
    }
}

// ================================================================
//  TableColumnPlanTests  —  Gate tests for BuildColumns / BuildCopyGrid
// ================================================================

public sealed class TableColumnPlanTests
{
    // ---- Helpers -----------------------------------------------------------

    private static Trace MakeBaseTrace() =>
        new Trace(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);

    private static Trace MakeCubeTrace(double[] xVals, string axisName = "Pin", string? unit = "dBm")
    {
        var t = MakeBaseTrace();
        t.CubeName  = "V";
        t.Slice     = new[] { new AxisSlice(axisName, AxisRole.KeepAsX, 0) };
        t.Transform = CubeTransform.None;
        var yVals = new double[xVals.Length];
        for (int i = 0; i < xVals.Length; i++) yVals[i] = i + 1.0;
        t.SetCubeData(xVals, complexValues: null, yVals, axisName, unit, PlotType.Table, FreqUnit.GHz);
        return t;
    }

    private static Plot MakePlot(params Trace[] traces)
    {
        var p = new Plot(PlotType.Table, FreqUnit.GHz);
        foreach (var t in traces) p.Traces.Add(t);
        return p;
    }

    // ---- Test 1: Two traces with different X axes → 4 columns ---------------

    [Fact]
    public void BuildColumns_TwoTraces_DifferentX_FourColumns()
    {
        var t0   = MakeCubeTrace(new double[] { 1, 2, 3 }, "Pin",  "dBm");
        var t1   = MakeCubeTrace(new double[] { 10, 20 },  "Bias", "V");
        var plot = MakePlot(t0, t1);

        var cols = TableRenderer.BuildColumns(plot);

        Assert.Equal(4, cols.Count);
        Assert.Equal(TableColKind.XAxis,      cols[0].Kind);
        Assert.Equal(TableColKind.TraceValue, cols[1].Kind);
        Assert.Equal(TableColKind.XAxis,      cols[2].Kind);
        Assert.Equal(TableColKind.TraceValue, cols[3].Kind);
        Assert.Equal(0, cols[0].FirstTraceIndex);
        Assert.Equal(0, cols[1].FirstTraceIndex);
        Assert.Equal(1, cols[2].FirstTraceIndex);
        Assert.Equal(1, cols[3].FirstTraceIndex);
    }

    // ---- Test 2: Two traces with identical X → 3 columns (deduped) ----------

    [Fact]
    public void BuildColumns_TwoTraces_IdenticalX_ThreeColumns()
    {
        var xVals = new double[] { 1, 2, 3 };
        var t0    = MakeCubeTrace(xVals, "Pin", "dBm");
        var t1    = MakeCubeTrace(xVals, "Pin", "dBm");
        var plot  = MakePlot(t0, t1);

        var cols = TableRenderer.BuildColumns(plot);

        Assert.Equal(3, cols.Count);   // [X, V0, V1]
        Assert.Equal(TableColKind.XAxis,      cols[0].Kind);
        Assert.Equal(TableColKind.TraceValue, cols[1].Kind);
        Assert.Equal(TableColKind.TraceValue, cols[2].Kind);
        // Both trace-value columns share the single X array by reference.
        Assert.Same(cols[0].XValues, cols[1].XValues);
        Assert.Same(cols[0].XValues, cols[2].XValues);
    }

    // ---- Test 3: Traces A, B, A — A is not adjacent to itself → 6 columns --

    [Fact]
    public void BuildColumns_ThreeTraces_NonAdjacentSameX_SixColumns()
    {
        var xAC  = new double[] { 1, 2, 3 };
        var t0   = MakeCubeTrace(xAC,             "Pin",  "dBm");
        var t1   = MakeCubeTrace(new double[] { 10, 20 }, "Bias", "V");
        var t2   = MakeCubeTrace(xAC,             "Pin",  "dBm");
        var plot = MakePlot(t0, t1, t2);

        var cols = TableRenderer.BuildColumns(plot);

        // t0 and t2 share the same X but t1 breaks adjacency → no dedup for t2.
        Assert.Equal(6, cols.Count);   // [XPin, V0, XBias, V1, XPin, V2]
        Assert.Equal(TableColKind.XAxis, cols[0].Kind);
        Assert.Equal(TableColKind.XAxis, cols[2].Kind);
        Assert.Equal(TableColKind.XAxis, cols[4].Kind);
    }

    // ---- Test 4: Same values, different axis name → 4 columns (not deduped) -

    [Fact]
    public void BuildColumns_TwoTraces_SameValues_DifferentAxisName_NotDeduped()
    {
        var xVals = new double[] { 1, 2, 3 };
        var t0    = MakeCubeTrace(xVals, "Pin",  "dBm");
        var t1    = MakeCubeTrace(xVals, "Bias", "dBm");  // same values + unit, different name
        var plot  = MakePlot(t0, t1);

        var cols = TableRenderer.BuildColumns(plot);

        Assert.Equal(4, cols.Count);
        Assert.Equal(TableColKind.XAxis, cols[0].Kind);
        Assert.Equal(TableColKind.XAxis, cols[2].Kind);
    }

    // ---- Test 5: Same axis name but different point count → 4 columns -------

    [Fact]
    public void BuildColumns_TwoTraces_SameAxis_DifferentPointCount_NotDeduped()
    {
        var t0   = MakeCubeTrace(new double[] { 1, 2, 3 }, "Pin", "dBm");
        var t1   = MakeCubeTrace(new double[] { 1, 2 },    "Pin", "dBm");  // fewer points
        var plot = MakePlot(t0, t1);

        var cols = TableRenderer.BuildColumns(plot);

        Assert.Equal(4, cols.Count);   // not deduped — different lengths
    }

    // ---- Test 6: BuildCopyGrid produces blanks when groups have unequal rows -

    [Fact]
    public void BuildCopyGrid_MatchesColumnPlan_WithBlanks()
    {
        var t0   = MakeCubeTrace(new double[] { 1, 2, 3 }, "Pin",  "dBm");  // 3 rows
        var t1   = MakeCubeTrace(new double[] { 10, 20 },  "Bias", "V");    // 2 rows
        var plot = MakePlot(t0, t1);

        var (headers, rows) = TableRenderer.BuildCopyGrid(plot, (800, 600), 1f);

        Assert.Equal(4, headers.Length);  // [XPin, V0, XBias, V1]
        Assert.Equal(3, rows.Length);     // RowCount = max(3, 2) = 3

        // Row index 2: first X group has a value; second X group is beyond its length.
        Assert.NotEmpty(rows[2][0]);  // X for t0 (index 2 of 3)
        Assert.NotEmpty(rows[2][1]);  // V0
        Assert.Equal("", rows[2][2]); // X for t1 group (only 2 rows)
        Assert.Equal("", rows[2][3]); // V1
    }

    // ---- Test 7: Sort order flip reverses X values --------------------------

    [Fact]
    public void BuildColumns_SortFlip_ReversesXValues()
    {
        var xVals   = new double[] { 3, 1, 2 };  // unsorted input
        var t       = MakeCubeTrace(xVals, "Pin", "dBm");

        var plotAsc  = MakePlot(t);
        plotAsc.TableViewAscendingSortOrder = true;

        var plotDesc = MakePlot(t);
        plotDesc.TableViewAscendingSortOrder = false;

        var xAsc  = TableRenderer.BuildColumns(plotAsc)[0].XValues;
        var xDesc = TableRenderer.BuildColumns(plotDesc)[0].XValues;

        Assert.Equal(new double[] { 1, 2, 3 }, xAsc);
        Assert.Equal(new double[] { 3, 2, 1 }, xDesc);
    }

    // ---- Test 8: Single trace → 2 columns, shared XValues reference ---------

    [Fact]
    public void BuildColumns_SingleTrace_TwoColumns_Regression()
    {
        var t    = MakeCubeTrace(new double[] { 0, 5, 10 }, "Pin", "dBm");
        var plot = MakePlot(t);

        var cols = TableRenderer.BuildColumns(plot);

        Assert.Equal(2, cols.Count);
        Assert.Equal(TableColKind.XAxis,      cols[0].Kind);
        Assert.Equal(TableColKind.TraceValue, cols[1].Kind);
        Assert.Equal(3, cols[0].XValues.Length);
        Assert.Same(cols[0].XValues, cols[1].XValues);  // shared reference
    }
}

// ================================================================
//  CubeTraceSpecParserTests  —  Gate tests for #4 inline spec editor
// ================================================================

public sealed class CubeTraceSpecParserTests
{
    // Helper: build a DataSet with a "V" cube of rank 3:
    //   axis 0: "node"     labels=["Vout","Vin"], values=[0,1]
    //   axis 1: "harmonic" values=[0, 1e9, 2e9], unit="Hz"
    //   axis 2: "Pin"      values=[-10, 0, 5, 10], unit="dBm"
    private static DataSet MakeDs()
    {
        var nodeAxis = new Axis("node", new double[] { 0, 1 }, "",
            labels: new[] { "Vout", "Vin" });
        var harmAxis = new Axis("harmonic", new double[] { 0, 1e9, 2e9 }, "Hz");
        var pinAxis  = new Axis("Pin",      new double[] { -10, 0, 5, 10 }, "dBm");

        var data = new Complex[2 * 3 * 4];  // node × harmonic × Pin
        var cube = new DataCube(new[] { nodeAxis, harmAxis, pinAxis }, data);
        var ds   = new DataSet();
        ds.Add("V", cube);
        return ds;
    }

    // ---- Test 5: Parser_RoundTrip ------------------------------------------

    [Fact]
    public void Parser_RoundTrip()
    {
        // Build a trace whose CubeShorthand is "V[0, 1, :]"
        var t = new Trace(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);
        t.CubeName  = "V";
        t.Transform = CubeTransform.None;
        t.Slice = new[]
        {
            new AxisSlice("node",     AxisRole.PinToIndex, 0),
            new AxisSlice("harmonic", AxisRole.PinToIndex, 1),
            new AxisSlice("Pin",      AxisRole.KeepAsX,    0),
        };

        string shorthand = t.CubeShorthand;   // "V[0, 1, :]"
        Assert.Equal("V[0, 1, :]", shorthand);

        var ds = MakeDs();
        bool ok = CubeTraceSpecParser.TryParse(shorthand, ds,
            out string name, out var slice, out var xform, out string error);

        Assert.True(ok, error);
        Assert.Equal("V", name);
        Assert.Equal(CubeTransform.None, xform);
        Assert.NotNull(slice);
        Assert.Equal(3, slice!.Length);
        Assert.Equal(AxisRole.PinToIndex, slice[0].Role);
        Assert.Equal(0, slice[0].Index);
        Assert.Equal(AxisRole.PinToIndex, slice[1].Role);
        Assert.Equal(1, slice[1].Index);
        Assert.Equal(AxisRole.KeepAsX, slice[2].Role);
    }

    // ---- Test 6: Parser_BadNode_Invalid ------------------------------------

    [Fact]
    public void Parser_BadNode_Invalid()
    {
        // "Voutx" is not a label in the "node" axis
        var ds = MakeDs();
        bool ok = CubeTraceSpecParser.TryParse(
            "V[\"Voutx\", 1, :]", ds,
            out _, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("Voutx", error);
        Assert.Contains("node", error);
    }

    // ---- Test 7: Parser_RankMismatch ---------------------------------------

    [Fact]
    public void Parser_RankMismatch()
    {
        // V has rank 3; provide only 2 tokens
        var ds = MakeDs();
        bool ok = CubeTraceSpecParser.TryParse(
            "V[0, :]", ds,
            out _, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("3", error);   // expected 3 tokens
        Assert.Contains("2", error);   // got 2
    }

    // ---- Test 8: Parser_TwoColons ------------------------------------------

    [Fact]
    public void Parser_TwoColons()
    {
        var ds = MakeDs();
        bool ok = CubeTraceSpecParser.TryParse(
            "V[:, :, 0]", ds,
            out _, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains(":", error);
    }

    // ---- Test 9: Parser_Transform ------------------------------------------

    [Fact]
    public void Parser_Transform_dB20()
    {
        var ds = MakeDs();
        bool ok = CubeTraceSpecParser.TryParse(
            "db20 V[0, 1, :]", ds,
            out string name, out var slice, out var xform, out string error);

        Assert.True(ok, error);
        Assert.Equal("V", name);
        Assert.Equal(CubeTransform.dB20, xform);
        Assert.NotNull(slice);
    }

    // ---- Test 10: InvalidState_BlankCells ----------------------------------

    [Fact]
    public void InvalidState_BlankCells()
    {
        // A trace with InvalidSpecText set → FormatCubeCell returns "" and HasSpecError is checked
        var t = new Trace(new SNP(new double[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);
        t.CubeName       = "V";
        t.Transform      = CubeTransform.None;
        t.Slice          = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };
        t.InvalidSpecText = "bad input";

        // CubeShorthand shows the <invalid> marker for the Table column header
        Assert.Contains("<invalid>", t.CubeShorthand);

        // FormatCubeCell returns "" (blank, not NaN) when InvalidSpecText is set
        string cell = t.FormatCubeCell(0, PrecisionFormat.F, 3);
        Assert.Equal("", cell);
    }
}
