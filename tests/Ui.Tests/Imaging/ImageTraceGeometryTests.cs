// brief-img-3-trace-layout-image.md §4 — a layout picture traced at a stated scale lands on the layout it was drawn from
// (R-im3-4, R-im3-5).

using System;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition.Image;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ImageTraceGeometryTests
{
    /// <summary>10 µm a pixel: 10,000 DBU, so half a pixel is 5,000 DBU. The picture's top-left is the origin, y up.</summary>
    private const double Dbu = 10_000, Half = 5_000;

    private static readonly Lazy<ImageTraceResult> Traced = new(() => ImageTrace.Trace(new ImageTraceInput
    {
        Source = TracePictures.Source(TracePictures.TwoLayerBoard()),
        Technology = TracePictures.Tech(),
        Scale = ImageScaleStatement.Stated(10e-6),
    }));

    private static (double X, double Y) At(double px, double py) => (px * Dbu, -py * Dbu);

    private static LayoutShape[] On(string layer) =>
        [.. Traced.Value.Layers.Single(l => l.Name == layer).Shapes];

    [Fact]
    public void EveryEdge_LandsWithinHalfAPixel_AndThe45DegreeJogIsExact()
    {
        var r = Traced.Value;
        Assert.True(r.Ok, r.Refusal);

        var (ax0, ay1) = At(TracePictures.RectA.X0, TracePictures.RectA.Y0);
        var (ax1, ay0) = At(TracePictures.RectA.X1, TracePictures.RectA.Y1);
        var a = Assert.Single(On("Top Copper").OfType<RectShape>());
        Assert.InRange(a.X1, ax0 - Half, ax0 + Half);
        Assert.InRange(a.X2, ax1 - Half, ax1 + Half);
        Assert.InRange(a.Y1, ay0 - Half, ay0 + Half);
        Assert.InRange(a.Y2, ay1 - Half, ay1 + Half);

        var (bx0, by1) = At(TracePictures.RectB.X0, TracePictures.RectB.Y0);
        var (bx1, by0) = At(TracePictures.RectB.X1, TracePictures.RectB.Y1);
        var b = Assert.Single(On("Bottom Copper").OfType<RectShape>());
        Assert.InRange(b.X1, bx0 - Half, bx0 + Half);
        Assert.InRange(b.X2, bx1 - Half, bx1 + Half);
        Assert.InRange(b.Y1, by0 - Half, by0 + Half);
        Assert.InRange(b.Y2, by1 - Half, by1 + Half);

        var jog = Assert.Single(On("Top Copper").OfType<PolygonShape>());
        Assert.Equal(TracePictures.Jog.Length, jog.Xy.Length / 2);
        foreach (var (px, py) in TracePictures.Jog)
        {
            var (x, y) = At(px, py);
            double nearest = Enumerable.Range(0, jog.Xy.Length / 2).Min(i => Math.Max(Math.Abs(jog.Xy[2 * i] - x), Math.Abs(jog.Xy[2 * i + 1] - y)));
            Assert.InRange(nearest, 0, Half);
        }
        int diagonals = 0;
        for (int i = 0; i < jog.Xy.Length / 2; i++)
        {
            int j = (i + 1) % (jog.Xy.Length / 2);
            long dx = jog.Xy[2 * j] - jog.Xy[2 * i], dy = jog.Xy[2 * j + 1] - jog.Xy[2 * i + 1];
            if (dx != 0 && dy != 0)
            {
                Assert.Equal(Math.Abs(dx), Math.Abs(dy));   // exactly 45°, not a DBU off it
                diagonals++;
            }
        }
        Assert.Equal(2, diagonals);

        var disc = Assert.Single(On("Top Copper").OfType<CircleShape>());
        var (cx, cy) = At(TracePictures.Disc.X, TracePictures.Disc.Y);
        Assert.InRange(disc.Cx, cx - Half, cx + Half);
        Assert.InRange(disc.Cy, cy - Half, cy + Half);
        Assert.InRange(disc.R, TracePictures.Disc.R * Dbu - Half, TracePictures.Disc.R * Dbu + Half);
    }

    [Fact]
    public void TheOverlapColour_LandsOnBothLayers()
    {
        var map = Traced.Value.LayerMap!;
        var overlap = Assert.Single(map.Rows, r => r.Overlap);
        Assert.Equal(["Bottom Copper", "Top Copper"], overlap.Layers.Order());
    }

    [Fact]
    public void ADrillCircle_BecomesAVia()
    {
        var via = Assert.Single(Traced.Value.Shapes.OfType<ViaShape>());
        var tech = TracePictures.Tech();
        Assert.Equal("Drill", tech.Layers.Single(l => l.Key == via.Layer).Name);
        Assert.Equal("Top Copper", tech.Layers.Single(l => l.Key == via.LandingLayer).Name);
        var (x, y) = At(TracePictures.Via.X, TracePictures.Via.Y);
        Assert.InRange(via.X, x - Half, x + Half);
        Assert.InRange(via.Y, y - Half, y + Half);
        Assert.InRange(via.DrillSize, 2 * TracePictures.Via.Drill * Dbu - 2 * Half, 2 * TracePictures.Via.Drill * Dbu + 2 * Half);
        Assert.InRange(via.PadSize, 2 * TracePictures.Via.R * Dbu - 2 * Half, 2 * TracePictures.Via.R * Dbu + 2 * Half);
        // The pad became the via's: it is not also a copper disc.
        Assert.Single(On("Top Copper").OfType<CircleShape>());
    }
}
