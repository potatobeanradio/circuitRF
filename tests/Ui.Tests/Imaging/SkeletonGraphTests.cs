// brief-img-1-raster-core.md R-im1-5, R-im1-6 — a drawn T is three edges and one junction; a 3 px stroke reads 3.

using System.Linq;
using CircuitRF.Design.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class SkeletonGraphTests
{
    [Fact]
    public void ADrawnT_GivesThreeEdgesAndOneJunction()
    {
        var img = Pictures.Draw(80, 80, c =>
        {
            using var ink = Pictures.Stroke(SKColors.Black, 3);
            c.DrawLine(10, 20.5f, 70, 20.5f, ink);
            c.DrawLine(40.5f, 20.5f, 40.5f, 70, ink);
        });

        var g = SkeletonGraph.Build(LineArt.Binarise(img));
        Assert.Equal(3, g.Edges.Count);
        var junction = Assert.Single(g.Nodes, n => n.IsJunction);
        Assert.Equal(3, junction.Degree);
        Assert.Equal(3, g.Nodes.Count(n => n.IsEnd));
        Assert.InRange(junction.X, 39, 42);
        Assert.InRange(junction.Y, 19, 22);
    }

    [Fact]
    public void AThreePixelStroke_ReadsStrokeWidthThree()
    {
        var img = Pictures.Draw(80, 40, c =>
        {
            using var ink = Pictures.Stroke(SKColors.Black, 3);
            c.DrawLine(8, 20.5f, 72, 20.5f, ink);
        });

        Assert.InRange(StrokeWidth.Estimate(LineArt.Binarise(img)), 2.5, 3.5);
    }
}
