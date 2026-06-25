// ================================================================
//  TableMarkerInfoBoxTests.cs — Table plots force marker info boxes ON
//
//  A Table has no on-canvas way to re-open a hidden info box (the box IS the
//  toggle host), so switching to Table turns every marker's box on. The off-
//  toggle is disabled while the plot is a Table (editor checkbox + context menu).
// ================================================================

using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class TableMarkerInfoBoxTests
{
    private static (Plot Plot, Marker Marker) MakePlotWithHiddenMarker(PlotType type)
    {
        var plot  = new Plot(type, FreqUnit.GHz);
        var snp   = new SNP(new double[] { 1e9 }, 2);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        plot.Traces.Add(trace);
        var m = new Marker(trace, freq: 1e9, isMulti: false, isDelta: false, index: 1)
        {
            ShowInfoBox = false,   // user had hidden it
        };
        trace.Markers.Add(m);
        return (plot, m);
    }

    [Fact]
    public void Table_ForcesAllMarkerInfoBoxesOn_AndIsIdempotent()
    {
        var (plot, m) = MakePlotWithHiddenMarker(PlotType.Table);

        Assert.True(PlotContainerViewModel.ForceMarkerInfoBoxesOnForTable(plot));
        Assert.True(m.ShowInfoBox);

        // Second call: nothing left to change.
        Assert.False(PlotContainerViewModel.ForceMarkerInfoBoxesOnForTable(plot));
        Assert.True(m.ShowInfoBox);
    }

    [Fact]
    public void NonTable_LeavesMarkerInfoBoxesUntouched()
    {
        var (plot, m) = MakePlotWithHiddenMarker(PlotType.Rect);

        Assert.False(PlotContainerViewModel.ForceMarkerInfoBoxesOnForTable(plot));
        Assert.False(m.ShowInfoBox);   // off-toggle still honored on non-Table plots
    }
}
