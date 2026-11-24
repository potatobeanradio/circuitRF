using CircuitRF.Render.DataDisplay;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

/// <summary>
/// A long plot title lost its last characters: shrunk to the canvas's width, it was then centred on the AXES, which sit
/// right of the canvas's centre by the Y labels' margin, so its tail ran past the right edge. It is sized to the room
/// either side of the axes' centre instead, so it stays centred over the plot.
/// </summary>
public sealed class PlotTitleFitTests
{
    [Fact]
    public void ALongTitle_StaysInsideTheCanvas_CentredOverThePlot()
    {
        var plot = new Plot
        {
            CustomTitleOn = true,
            CustomTitle = "Hairpin filter, 2.28-2.52 GHz: circuit model vs Planar MoM vs EMerge 3D, before and after optimization",
        };
        var tf = new TransformSet
        {
            Primary    = (1.0, 1.0, 0.0, 0.0),
            Secondary  = (1.0, 1.0, 0.0, 0.0),
            CanvasSize = (1000.0, 560.0),
            Viewport   = new PlotRect(0.18, 0.12, 0.78, 0.75),
        };
        using var surface = SKSurface.Create(new SKImageInfo(1000, 560));
        var rects = AxesRenderer.DrawTitleAndAxisLabels(surface.Canvas, (1000.0, 560.0), plot, tf, RenderTheme.Light);

        Assert.True(rects.Title.Left >= 0f, $"title starts at {rects.Title.Left}");
        Assert.True(rects.Title.Right <= 1000f, $"title ends at {rects.Title.Right}, past the 1000 px canvas");
        float axesCentre = (float)((0.18 + 0.78 / 2) * 1000.0);
        Assert.InRange(rects.Title.MidX, axesCentre - 1f, axesCentre + 1f);
    }
}
