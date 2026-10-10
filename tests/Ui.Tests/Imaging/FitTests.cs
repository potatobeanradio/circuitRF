// brief-img-1-raster-core.md R-im1-8 — a drawn circle comes back as a circle.

using System.Linq;
using Clipper2Lib;
using CircuitRF.Design.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class FitTests
{
    [Fact]
    public void ADrawnCircleOfRadius6Point4_FitsWithinAFifthOfAPixel()
    {
        var img = Pictures.Draw(40, 40, c => c.DrawCircle(20.3f, 18.7f, 6.4f, Pictures.Fill(SKColors.Black)));
        var set = ColourClusters.Find(img);
        var ring = Contours.Iso(set.Coverage(Pictures.ClusterOf(set, 0x000000))).OrderByDescending(r => System.Math.Abs(Clipper.Area(r))).First();

        var fit = Fit.Circle(ring);
        Assert.InRange(fit.R, 6.4 - 0.2, 6.4 + 0.2);
        Assert.InRange(fit.Cx, 20.3 - 0.2, 20.3 + 0.2);
        Assert.InRange(fit.Cy, 18.7 - 0.2, 18.7 + 0.2);
        Assert.True(fit.Residual < 0.2, $"residual {fit.Residual}");
    }
}
