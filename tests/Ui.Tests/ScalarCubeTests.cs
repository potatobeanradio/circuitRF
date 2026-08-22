// ================================================================
//  ScalarCubeTests.cs  —  Gate tests for scalar (rank-0) cube support
//  (brief-iprobe-currents-scalars-table)
//
//  1. Scalar_OnTable_RendersValueCell
//  1b. Scalar_MixedWithSweptTrace_KeepsBothColumns
//  1c. Scalar_GridStopsAtLastColumn
//  2. Scalar_PickerVisibleOnlyOnTable
//  3. Scalar_OnRect_IsInvalid
//  4. Scalar_AddTrace_OnTableOnly
//  5. Rank1_Unchanged
//  6. NoAxisIndex_OnRank0
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class ScalarCubeTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Trace MakeTrace() =>
        new Trace(new SNP(new[] { 1e9 }, 2), MatrixType.S, 0, 0, DependentVarFormat.Db);

    private static DataCube MakeScalarReal(double value) =>
        new DataCube(Array.Empty<Axis>(), new[] { value });

    private static DataCube MakeScalarComplex(Complex value) =>
        new DataCube(Array.Empty<Axis>(), new[] { value });

    private static DataCube MakeRank1Real(int len = 3)
    {
        var axis = new Axis("Pin", Enumerable.Range(0, len).Select(i => (double)i).ToArray(), "dBm");
        var data = Enumerable.Range(0, len).Select(i => (double)i * 1.1).ToArray();
        return new DataCube(new[] { axis }, data);
    }

    private static async Task<(string path, DataSourceLibraryViewModel lib)> ExportAndLoad(DataSet ds)
    {
        string path = Path.Combine(Path.GetTempPath(), $"crf_sct_{Guid.NewGuid():N}.npy");
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        await lib.SelectDataSourceAsync(path);
        return (path, lib);
    }

    private static TraceRowViewModel BuildInspectorWithCubeTrace(
        DataSourceLibraryViewModel lib, string sourcePath, string cubeName, PlotType plotType)
    {
        var snp   = new SNP(new[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.SourcePath = sourcePath;
        trace.CubeName   = cubeName;
        trace.Slice      = Array.Empty<AxisSlice>();
        trace.Transform  = CubeTransform.None;

        var plot      = new Plot(plotType, FreqUnit.GHz);
        plot.Traces.Add(trace);
        var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
        inspector.RebuildAndNotify();
        return inspector.Traces[0];
    }

    // ── 1. Scalar_OnTable_RendersValueCell ────────────────────────────────────

    [Fact]
    public void Scalar_OnTable_RendersValueCell()
    {
        const double pdcValue = 0.042;

        var t = MakeTrace();
        t.CubeName  = "PDC";
        t.Slice     = Array.Empty<AxisSlice>();
        t.Transform = CubeTransform.None;

        t.SetScalarCubeData(complexValue: null, realValue: pdcValue,
                            PlotType.Table, FreqUnit.GHz);

        // Build column plan
        var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        plot.Traces.Add(t);
        var cols = TableRenderer.BuildColumns(plot);

        // A rank-0 cube has no sweep axis, so there is NO X column: the table is exactly one
        // column wide (header + one value row). The old blank, header-less anchor column is gone.
        Assert.Single(cols);

        var valCol = cols[0];
        Assert.Equal(TableColKind.TraceValue, valCol.Kind);
        Assert.Equal("PDC", valCol.Header);

        // Exactly one data row.
        Assert.Equal(1, TableRenderer.RowCount(cols));
        Assert.Equal("", TableRenderer.FormatColumnCell(valCol, 1, plot));

        // Value column renders the number, not "" or "NaN"
        string cellText = TableRenderer.FormatColumnCell(valCol, 0, plot);
        Assert.NotEqual("",    cellText);
        Assert.NotEqual("NaN", cellText);
        Assert.Contains("0.04", cellText);   // contains the value digits

        // The rendered grid is literally 1 column x 1 data row (2 rows counting the header).
        var (headers, rows) = TableRenderer.BuildCopyGrid(plot, (400.0, 200.0));
        Assert.Single(headers);
        Assert.Single(rows);
        Assert.Single(rows[0]);

        // Trace has no geometry (Table reads CubeXValues, not Points)
        Assert.Empty(t.Points);
        Assert.False(t.ScalarOnNonTableInvalid);
    }

    // ── 1b. Scalar_MixedWithSweptTrace_KeepsBothColumns ───────────────────────

    /// <summary>
    /// A scalar trace losing its (blank) X column must not disturb a swept trace sharing the table:
    /// the swept trace still gets its own X + value pair, the table's row count comes from the
    /// LONGEST column, and the scalar's single value sits on row 0 with blanks below it.
    /// </summary>
    [Fact]
    public void Scalar_MixedWithSweptTrace_KeepsBothColumns()
    {
        var scalar = MakeTrace();
        scalar.CubeName  = "PDC";
        scalar.Slice     = Array.Empty<AxisSlice>();
        scalar.Transform = CubeTransform.None;
        scalar.SetScalarCubeData(complexValue: null, realValue: 0.042,
                                 PlotType.Table, FreqUnit.GHz);

        double[] xVals = { 0.0, 1.0, 2.0 };
        double[] yVals = { 0.1, 0.2, 0.3 };
        var swept = MakeTrace();
        swept.CubeName  = "PAE";
        swept.Slice     = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };
        swept.Transform = CubeTransform.None;
        swept.SetCubeData(xVals, complexValues: null, yVals, "Pin", "dBm",
                          PlotType.Table, FreqUnit.GHz);

        var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        plot.Traces.Add(scalar);
        plot.Traces.Add(swept);

        var cols = TableRenderer.BuildColumns(plot);

        // PDC (value only), then Pin (X) + PAE (value).
        Assert.Equal(3, cols.Count);
        Assert.Equal(TableColKind.TraceValue, cols[0].Kind);
        Assert.Equal(TableColKind.XAxis,      cols[1].Kind);
        Assert.Equal(TableColKind.TraceValue, cols[2].Kind);
        Assert.Equal("Pin (dBm)", cols[1].Header);
        Assert.Equal(3, cols[1].XValues.Length);

        // Row count follows the swept trace, not the scalar.
        Assert.Equal(3, TableRenderer.RowCount(cols));

        // The scalar's value is on row 0; rows below it are blank, not "NaN".
        Assert.NotEqual("", TableRenderer.FormatColumnCell(cols[0], 0, plot));
        Assert.Equal("",    TableRenderer.FormatColumnCell(cols[0], 1, plot));
        Assert.Equal("",    TableRenderer.FormatColumnCell(cols[0], 2, plot));

        // The swept trace still reads all three of its rows.
        for (int r = 0; r < 3; r++)
        {
            Assert.NotEqual("",    TableRenderer.FormatColumnCell(cols[2], r, plot));
            Assert.NotEqual("NaN", TableRenderer.FormatColumnCell(cols[2], r, plot));
        }
    }

    // ── 1c. Scalar_GridStopsAtLastColumn ──────────────────────────────────────

    /// <summary>
    /// Pixel gate for the second half of the "blank column" report: the plot BOX is wider than the
    /// single column, and the row shading / horizontal rules used to run to the canvas edge, drawing
    /// an open, header-less cell to the right of the only column. Nothing may be painted past the
    /// last column's right edge.
    /// </summary>
    [Fact]
    public void Scalar_GridStopsAtLastColumn()
    {
        var prevTypeface = SkiaFonts.TestOverrideTypeface;
        SkiaFonts.TestOverrideTypeface = SKTypeface.Default;
        try
        {
            var t = MakeTrace();
            t.CubeName    = "PDC";
            t.Slice       = Array.Empty<AxisSlice>();
            t.Transform   = CubeTransform.None;
            t.ColumnWidth = 115;
            t.SetScalarCubeData(complexValue: null, realValue: 0.042,
                                PlotType.Table, FreqUnit.GHz);

            var plot = new Plot(PlotType.Table, FreqUnit.GHz) { FontSize = 12, ColumnWidth = 115 };
            plot.Traces.Add(t);

            // Canvas deliberately wider (200) and taller (64) than the one 115-px column needs.
            const int w = 200, h = 64;
            using var surface = SKSurface.Create(new SKImageInfo(w, h));
            TableRenderer.Draw(surface.Canvas, (w, h), plot, RenderTheme.Light);
            using var img = surface.Snapshot();
            using var bmp = SKBitmap.FromImage(img);

            SKColor bg = RenderTheme.Light.BackgroundColor;
            bool PaintedAt(int x)
            {
                for (int y = 0; y < h; y++)
                {
                    var px = bmp.GetPixel(x, y);
                    if (px.Red != bg.Red || px.Green != bg.Green || px.Blue != bg.Blue) return true;
                }
                return false;
            }

            // Inside the column: header band, rules and the value are drawn.
            Assert.True(PaintedAt(50), "expected the single column to be drawn at x = 50");

            // Right of the column (115) and inside the canvas (200): nothing at all.
            foreach (int x in new[] { 120, 150, 180, 199 })
                Assert.False(PaintedAt(x), $"nothing may be painted right of the last column (x = {x})");
        }
        finally
        {
            SkiaFonts.TestOverrideTypeface = prevTypeface;
        }
    }

    // ── 2. Scalar_PickerVisibleOnlyOnTable ────────────────────────────────────

    [Fact]
    public async Task Scalar_PickerVisibleOnlyOnTable()
    {
        var ds = new DataSet();
        ds.Add("PDC", MakeScalarReal(0.05));
        ds.Add("V",   MakeRank1Real());           // rank-1 → always visible

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // On a Table, PDC (rank-0) must appear
            var trvmTable = BuildInspectorWithCubeTrace(lib, path, "V", PlotType.Table);
            var namesTable = trvmTable.AvailableSignals
                .Where(s => s.IsCubeBound)
                .Select(s => s.CubeName)
                .ToList();
            Assert.Contains("PDC", namesTable);
            Assert.Contains("V",   namesTable);

            // On a Rect, PDC (rank-0) must NOT appear
            var trvmRect = BuildInspectorWithCubeTrace(lib, path, "V", PlotType.Rect);
            var namesRect = trvmRect.AvailableSignals
                .Where(s => s.IsCubeBound)
                .Select(s => s.CubeName)
                .ToList();
            Assert.DoesNotContain("PDC", namesRect);
            Assert.Contains("V", namesRect);

            // On a Smith, PDC must NOT appear
            var trvmSmith = BuildInspectorWithCubeTrace(lib, path, "V", PlotType.Smith);
            var namesSmith = trvmSmith.AvailableSignals
                .Where(s => s.IsCubeBound)
                .Select(s => s.CubeName)
                .ToList();
            Assert.DoesNotContain("PDC", namesSmith);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 3. Scalar_OnRect_IsInvalid ────────────────────────────────────────────

    [Fact]
    public void Scalar_OnRect_IsInvalid()
    {
        var t = MakeTrace();
        t.CubeName  = "PDC";
        t.Slice     = Array.Empty<AxisSlice>();
        t.Transform = CubeTransform.None;

        // Bind as scalar but on a Rect plot type
        t.SetScalarCubeData(complexValue: null, realValue: 0.042,
                            PlotType.Rect, FreqUnit.GHz);

        Assert.Empty(t.Points);
        Assert.True(t.ScalarOnNonTableInvalid);

        // CubeShorthand must end with <invalid>
        Assert.EndsWith("<invalid>", t.CubeShorthand, StringComparison.Ordinal);

        // Description must also end with <invalid>
        Assert.EndsWith("<invalid>", t.Description, StringComparison.Ordinal);
        Assert.EndsWith("<invalid>", t.ShortDescription, StringComparison.Ordinal);
    }

    // ── 4. Scalar_AddTrace_OnTableOnly ────────────────────────────────────────

    [Fact]
    public async Task Scalar_AddTrace_OnTableOnly()
    {
        // Scalar-only DataSet (no SNP, no rank-1+ cube)
        var ds = new DataSet();
        ds.Add("PDC", MakeScalarReal(0.05));

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // On a Table: CanAddTrace is true; AddTrace seeds scalar trace
            var tablePlot      = new Plot(PlotType.Table, FreqUnit.GHz);
            var tableInspector = new PlotInspectorViewModel(tablePlot, () => { }, lib);

            Assert.True(tableInspector.CanAddTrace);

            tableInspector.AddTraceCommand.Execute(null);

            Assert.Single(tablePlot.Traces);
            var seeded = tablePlot.Traces[0];
            Assert.True(seeded.IsCubeBound);
            Assert.NotNull(seeded.Slice);
            Assert.Empty(seeded.Slice!);               // empty slice (rank-0)
            Assert.Equal("PDC", seeded.Expression);    // bare name, no brackets

            // On a Rect: CanAddTrace is false (scalar-only source)
            var rectPlot      = new Plot(PlotType.Rect, FreqUnit.GHz);
            var rectInspector = new PlotInspectorViewModel(rectPlot, () => { }, lib);
            Assert.False(rectInspector.CanAddTrace);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── 5. Rank1_Unchanged ────────────────────────────────────────────────────

    [Fact]
    public void Rank1_Unchanged()
    {
        double[] xVals = { 0.0, 1.0, 2.0 };
        double[] yVals = { 0.1, 0.2, 0.3 };

        var t = MakeTrace();
        t.CubeName  = "PAE";
        t.Slice     = new[] { new AxisSlice("Pin", AxisRole.KeepAsX, 0) };
        t.Transform = CubeTransform.None;

        // Rect: points are built, scalar flags clear
        t.SetCubeData(xVals, complexValues: null, yVals, "Pin", "dBm", PlotType.Rect, FreqUnit.GHz);
        Assert.Equal(3, t.Points.Count);
        Assert.False(t.ScalarOnNonTableInvalid);
        Assert.False(t.CubeIsScalar);
        Assert.DoesNotContain("<invalid", t.CubeShorthand, StringComparison.Ordinal);

        // Table: Table renderer reads CubeXValues directly (Points are not built for real+Table),
        // but flags remain clear and the column plan is not marked scalar.
        t.SetCubeData(xVals, complexValues: null, yVals, "Pin", "dBm", PlotType.Table, FreqUnit.GHz);
        Assert.False(t.ScalarOnNonTableInvalid);
        Assert.False(t.CubeIsScalar);

        // BuildColumns for the Table case: a swept trace still gets its own X column
        var plot = new Plot(PlotType.Table, FreqUnit.GHz);
        plot.Traces.Add(t);
        var cols = TableRenderer.BuildColumns(plot);
        var xCol = cols.First(c => c.Kind == TableColKind.XAxis);
        Assert.Equal(xVals.Length, xCol.XValues.Length);

        // FormatColumnCell returns real values for the value column (not "" or "NaN")
        var valCol = cols.First(c => c.Kind == TableColKind.TraceValue);
        string cell = TableRenderer.FormatColumnCell(valCol, 0, plot);
        Assert.NotEqual("",    cell);
        Assert.NotEqual("NaN", cell);
    }

    // ── 6. NoAxisIndex_OnRank0 ────────────────────────────────────────────────

    [Fact]
    public async Task NoAxisIndex_OnRank0()
    {
        var ds = new DataSet();
        ds.Add("PDC",  MakeScalarReal(1.23));
        ds.Add("IDC",  MakeScalarComplex(new Complex(0.01, 0.0)));
        ds.Add("Gain", MakeRank1Real());          // mix: one rank-1 to verify nothing breaks

        var (path, lib) = await ExportAndLoad(ds);
        try
        {
            // TrySetCubeData on a Table trace backed by a scalar — must not throw
            var snp   = new SNP(new[] { 1e9 }, 2);
            var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
            trace.SourcePath = path;
            trace.CubeName   = "PDC";
            trace.Slice      = Array.Empty<AxisSlice>();
            trace.Transform  = CubeTransform.None;

            var ex = Record.Exception(() =>
                PlotInspectorViewModel.TrySetCubeData(trace, lib, PlotType.Table, FreqUnit.GHz));
            Assert.Null(ex);

            Assert.True(trace.CubeIsScalar);
            Assert.False(trace.ScalarOnNonTableInvalid);

            // Same on a Rect — must not throw and sets the invalid flag
            var ex2 = Record.Exception(() =>
                PlotInspectorViewModel.TrySetCubeData(trace, lib, PlotType.Rect, FreqUnit.GHz));
            Assert.Null(ex2);
            Assert.True(trace.ScalarOnNonTableInvalid);

            // Seeding a rank-0 in BuildSeedCubeTrace — exercise via AddTraceCommand on a Table
            var plot      = new Plot(PlotType.Table, FreqUnit.GHz);
            var inspector = new PlotInspectorViewModel(plot, () => { }, lib);
            var ex3 = Record.Exception(() => inspector.AddTraceCommand.Execute(null));
            Assert.Null(ex3);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
