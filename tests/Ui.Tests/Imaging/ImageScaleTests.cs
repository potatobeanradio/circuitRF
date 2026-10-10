// brief-img-3-trace-layout-image.md §4 — the scale is never a silent guess (R-im3-2, D6).

using System.Linq;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Recognition.Image;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ImageScaleTests
{
    private static ImageScaleCandidate? Inferred(params (string Code, double X, double Y)[] pairs)
    {
        var input = new ImageTraceInput { Source = TracePictures.Source(TracePictures.Pairs(25, pairs)), Technology = TracePictures.Tech() };
        return ImageScale.Choose(ImageScale.Candidates(input), targetIsPlacedLayout: false);
    }

    [Fact]
    public void FourOf0603AndTwoOf0402_InferTheScaleTheyWereDrawnAt()
    {
        var scale = Inferred(("0603", 20, 20), ("0603", 220, 20), ("0603", 420, 20),
                             ("0603", 20, 160), ("0402", 220, 160), ("0402", 420, 160));
        Assert.NotNull(scale);
        Assert.Equal(ImageScaleKind.Parts, scale!.Kind);
        Assert.InRange(scale.MetresPerPixel, 25e-6 * 0.99, 25e-6 * 1.01);
        Assert.Equal(6, scale.Support);
        Assert.Contains("4 × 0603, 2 × 0402", scale.Evidence);
    }

    [Fact]
    public void TwoPairsAlone_GiveNoScale()
    {
        Assert.Null(Inferred(("0603", 20, 20), ("0603", 220, 160)));
    }

    [Fact]
    public void APlacedBitmapsRect_WinsWhenTheTargetIsItsLayout()
    {
        var placed = ImageScale.FromPlacement(
            new LayoutBitmapPlacement("/nowhere/board.clay", 0, 0, 6_000_000, 3_000_000, new LayerKey(7, 0)), 600, 300)!;
        Assert.Equal(10e-6, placed.MetresPerPixel, 12);
        var parts = new ImageScaleCandidate(ImageScaleKind.Parts, 25e-6, 6, "6 land patterns agree");

        Assert.Same(placed, ImageScale.Choose([parts, placed], targetIsPlacedLayout: true));
        Assert.Same(parts, ImageScale.Choose([parts, placed], targetIsPlacedLayout: false));
    }

    [Fact]
    public void ADistanceWithNoUnit_IsRefused()
    {
        Assert.Null(ImageScale.FromTwoPoints(new PixelPoint(10, 10), new PixelPoint(110, 10), "1.6", out string? why));
        Assert.Contains("no unit", why);

        var withUnit = ImageScale.FromTwoPoints(new PixelPoint(10, 10), new PixelPoint(110, 10), "1.6 mm", out why);
        Assert.Null(why);
        Assert.Equal(16e-6, withUnit!.MetresPerPixel, 12);
    }
}
