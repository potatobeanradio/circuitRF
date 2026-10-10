// brief-img-1-raster-core.md R-im1-7 — sub-pixel, nested, snapped contours.

using System;
using System.Linq;
using Clipper2Lib;
using CircuitRF.Design.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ContourTests
{
    private static readonly SKColor Copper = new(200, 117, 51);

    private static ContourRegion Single(RasterImage img, ContourOptions? options = null)
    {
        var set = ColourClusters.Find(img);
        var regions = Contours.Trace(set.Coverage(Pictures.ClusterOf(set, 0xC87533)), options);
        return Assert.Single(regions);
    }

    [Fact]
    public void AnAntiAliasedRectangle_ComesBackWithinAQuarterPixel_ExactlyRectilinear()
    {
        var rect = new SKRect(10.2f, 8.7f, 10.2f + 37.3f, 8.7f + 12.6f);
        var region = Single(Pictures.Draw(64, 40, c => c.DrawRect(rect, Pictures.Fill(Copper))));

        Assert.Empty(region.Holes);
        Assert.Equal(4, region.Outer.Count);
        for (int i = 0; i < 4; i++)
        {
            var a = region.Outer[i];
            var b = region.Outer[(i + 1) % 4];
            Assert.True(a.x == b.x || a.y == b.y, $"edge {i} is not axis-aligned: ({a.x}, {a.y}) → ({b.x}, {b.y})");
        }
        var r = Clipper.GetBounds(region.Outer);
        Assert.InRange(r.left, rect.Left - 0.25, rect.Left + 0.25);
        Assert.InRange(r.top, rect.Top - 0.25, rect.Top + 0.25);
        Assert.InRange(r.right, rect.Right - 0.25, rect.Right + 0.25);
        Assert.InRange(r.bottom, rect.Bottom - 0.25, rect.Bottom + 0.25);
        Assert.True(Clipper.Area(region.Outer) > 0);   // Clipper2's convention: an outer boundary is positive
    }

    [Fact]
    public void ARectangleWithAHole_ComesBackWithItsHole()
    {
        var region = Single(Pictures.Draw(64, 52, c =>
        {
            c.DrawRect(new SKRect(10, 10, 50, 40), Pictures.Fill(Copper));
            c.DrawRect(new SKRect(23, 20, 37, 30), Pictures.Fill(SKColors.White));
        }));

        var hole = Assert.Single(region.Holes);
        Assert.True(Clipper.Area(hole) < 0);
        var h = Clipper.GetBounds(hole);
        Assert.Equal(23, h.left, 0.25);
        Assert.Equal(20, h.top, 0.25);
        Assert.Equal(37, h.right, 0.25);
        Assert.Equal(30, h.bottom, 0.25);
        Assert.Equal(40 * 30 - 14 * 10, region.Area, 1.0);
    }

    [Fact]
    public void ARectangleRotatedOneAndAHalfDegrees_IsNotSnapped_AtAOneDegreeTolerance()
    {
        var img = Pictures.Draw(80, 50, c =>
        {
            c.RotateDegrees(1.5f, 40, 25);
            c.DrawRect(new SKRect(15, 15, 65, 35), Pictures.Fill(Copper));
        });

        var region = Single(img, new ContourOptions { SnapAngleDeg = 1.0 });
        Assert.Equal(4, region.Outer.Count);
        for (int i = 0; i < 4; i++)
        {
            var a = region.Outer[i];
            var b = region.Outer[(i + 1) % 4];
            double deg = Math.Atan2(b.y - a.y, b.x - a.x) * 180 / Math.PI;
            double offAxis = ((deg % 90) + 90) % 90;
            Assert.True(offAxis is > 1.2 and < 1.8, string.Join(" ", region.Outer.Select(p => $"({p.x:F3},{p.y:F3})")));   // still rotated: snapping did not engage
        }
    }
}
