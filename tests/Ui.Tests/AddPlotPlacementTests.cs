// ================================================================
//  AddPlotPlacementTests.cs — "Add Plot" always lands in the viewport
//
//  The user must always SEE a newly-added plot. ComputeNewPlotPosition
//  centers in the viewport when nothing is in view, follows the in-view grid
//  otherwise, and — the fix — keeps the new plot inside the viewport when the
//  grid would grow off-screen.
// ================================================================

using System.Linq;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class AddPlotPlacementTests
{
    private const double CanvasW = 800, CanvasH = 600;

    private static DataDisplayViewModel MakeDisplay()
    {
        var ddvm = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        ddvm.CanvasSizeProvider = () => (CanvasW, CanvasH);   // zoom 1, offset 0 → viewport [0,800]×[0,600]
        return ddvm;
    }

    private static void AssertWithinViewport(
        PlotContainerViewModel p, double vx0, double vy0, double vx1, double vy1)
    {
        Assert.True(
            p.Left >= vx0 - 0.01 && p.Top >= vy0 - 0.01 &&
            p.Left + p.Width <= vx1 + 0.01 && p.Top + p.Height <= vy1 + 0.01,
            $"plot at ({p.Left:0},{p.Top:0}) size ({p.Width:0}×{p.Height:0}) " +
            $"is not within viewport [{vx0:0},{vy0:0} … {vx1:0},{vy1:0}]");
    }

    // The fix: as the grid fills the viewport, each new plot's grid slot would extend off-screen —
    // it must be repositioned to stay fully visible.
    [Fact]
    public void AddPlot_GridWouldExtendOffScreen_NewPlotStaysVisible()
    {
        var ddvm = MakeDisplay();
        for (int i = 0; i < 5; i++)
        {
            var p = ddvm.AddPlot(PlotType.Rect);   // 520×360 — wider than half the 800 viewport
            AssertWithinViewport(p, 0, 0, CanvasW, CanvasH);
        }
    }

    // Viewport priority: after panning the existing plots off-screen, a new plot lands in the
    // CURRENT viewport (centered), not back at the old plots' position.
    [Fact]
    public void AddPlot_AfterPanningAway_NewPlotIsInCurrentViewport()
    {
        var ddvm = MakeDisplay();
        ddvm.AddPlot(PlotType.Rect);   // p1 at the default (offset 0) view

        // Pan far away so p1 is off-screen; the current logical viewport shifts with the pan.
        ddvm.ViewOffsetX = -2000;
        ddvm.ViewOffsetY = -1500;
        double vx0 = 2000, vy0 = 1500, vx1 = 2000 + CanvasW, vy1 = 1500 + CanvasH;

        var p2 = ddvm.AddPlot(PlotType.Rect);
        AssertWithinViewport(p2, vx0, vy0, vx1, vy1);
        Assert.True(ddvm.Plots.Count == 2);
    }
}
