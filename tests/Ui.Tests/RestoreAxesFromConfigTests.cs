// ================================================================
//  RestoreAxesFromConfigTests.cs  —  Rect trace restore regression
//
//  Gate tests for the paste/load Rect-trace-off-screen bug.
//    T1 — valid saved window is honored; no autoscale clobber
//    T2 — degenerate saved window triggers autoscale to frame data
//    T3 — Smith/Polar unaffected (unit-circle fallback preserved)
//    T4 — Fix B1: Rect autoscale with no points preserves valid window
// ================================================================

using Avalonia;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class RestoreAxesFromConfigTests
{
    // ---- Helpers ------------------------------------------------------------

    private static Plot MakeRectPlotWithData()
    {
        // SNP at 1 GHz and 10 GHz → X range ≈ 1e9..1e10 Hz (0.001..10 GHz), Y ≈ dB values
        var snp   = new SNP(new double[] { 1e9, 1e10 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        var plot  = new Plot(PlotType.Rect, FreqUnit.GHz);
        trace.BuildPath(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        return plot;
    }

    // A Rect plot with no traces → no points in any trace bounding-box pass.
    private static Plot MakeRectPlotNoData() => new Plot(PlotType.Rect, FreqUnit.GHz);

    // ---- T1: valid saved window is honored ----------------------------------

    [Fact]
    public void RestoreAxesFromConfig_ValidWindow_WindowUnchanged()
    {
        var plot       = MakeRectPlotWithData();
        var savedWin   = new Rect(0, -40, 10, 50);   // 0..10 GHz x -40..10 dB (valid)
        var savedWin2  = new Rect(0, -40, 10, 50);

        plot.RestoreAxesFromConfig(
            autoscaleX: true, autoscaleY: true, autoscaleRightY: true, autoscaleMag: true,
            window: savedWin, windowSecondary: savedWin2);

        // The saved window must survive — NOT be replaced by the autoscale box (0..2 × 0..2).
        Assert.Equal(savedWin.X,      plot.Axes.Window.X,      3);
        Assert.Equal(savedWin.Y,      plot.Axes.Window.Y,      3);
        Assert.Equal(savedWin.Width,  plot.Axes.Window.Width,  3);
        Assert.Equal(savedWin.Height, plot.Axes.Window.Height, 3);
    }

    // ---- T2: degenerate saved window triggers autoscale ---------------------

    [Fact]
    public void RestoreAxesFromConfig_DegenerateWindow_AutoscalesToData()
    {
        var plot = MakeRectPlotWithData();

        // A degenerate (zero-size) saved window: autoscale should run and frame the data.
        plot.RestoreAxesFromConfig(
            autoscaleX: true, autoscaleY: true, autoscaleRightY: false, autoscaleMag: true,
            window: new Rect(0, 0, 0, 0), windowSecondary: new Rect(0, 0, 0, 0));

        // After autoscale the window must have positive extent that covers the data range.
        Assert.True(plot.Axes.Window.Width  > 0, "Width must be positive after autoscale");
        Assert.True(plot.Axes.Window.Height > 0, "Height must be positive after autoscale");
        // The X range covers 1..10 GHz — must not collapse to the 0..2 origin box.
        Assert.True(plot.Axes.Window.Width > 2,
            $"Width={plot.Axes.Window.Width} — expected data-framing range, not 0..2 origin box");
    }

    // ---- T3: Smith/Polar unaffected (unit-circle fallback preserved) ---------

    [Fact]
    public void RestoreAxesFromConfig_SmithPlot_UnitCirclePreserved()
    {
        var snp   = new SNP(new double[] { 1e9, 2e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Complex);
        var plot  = new Plot(PlotType.Smith, FreqUnit.GHz);
        trace.BuildPath(PlotType.Smith, FreqUnit.GHz);
        plot.Traces.Add(trace);

        // Smith with a degenerate saved window should still get the unit circle.
        plot.RestoreAxesFromConfig(
            autoscaleX: false, autoscaleY: false, autoscaleRightY: false, autoscaleMag: true,
            window: new Rect(0, 0, 0, 0), windowSecondary: new Rect(0, 0, 0, 0));

        // Window must be the unit circle: approximately centred on origin, side ≥ 2.
        Assert.True(plot.Axes.Window.Width  >= 2,  $"Width={plot.Axes.Window.Width}");
        Assert.True(plot.Axes.Window.Height >= 2, $"Height={plot.Axes.Window.Height}");
    }

    // ---- T4: Fix B1 — no-data Rect Autoscale preserves valid window ----------

    [Fact]
    public void Autoscale_NoData_ValidWindow_WindowPreserved()
    {
        var plot       = MakeRectPlotNoData();
        var savedWin   = new Rect(0, -40, 10, 50);
        plot.Axes.Window = savedWin;

        // Force autoscale with no data points in the trace.
        plot.Autoscale(force: true);

        // The window must NOT collapse to the 0..2 origin box.
        Assert.True(plot.Axes.Window.Width  > 2,
            $"Width={plot.Axes.Window.Width} — should preserve valid window, not 0..2 box");
    }
}
