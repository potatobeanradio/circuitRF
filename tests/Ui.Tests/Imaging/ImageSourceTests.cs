// brief-img-2-image-source-and-kind.md §4 — a placed bitmap as a picture source (R-im2-1).

using System;
using System.IO;
using CircuitRF.Design.Imaging;
using CircuitRF.Design.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ImageSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-imgsrc-" + Guid.NewGuid().ToString("N")[..12]);

    public ImageSourceTests() => Directory.CreateDirectory(Path.Combine(_dir, "layout", "pics"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void APlacedBitmapShape_ResolvesRelativeToItsClay_AndCarriesItsDbuRect()
    {
        string png = Path.Combine(_dir, "layout", "pics", "board.png");
        File.WriteAllBytes(png, Pictures.Png(Pictures.Layout()));
        string clay = Path.Combine(_dir, "layout", "amp.clay");
        var shape = new BitmapShape { ImagePathRef = "pics/board.png", X = -1000, Y = 2000, W = 400_000, H = 300_000, Layer = new LayerKey(7, 0) };

        var read = ImageSource.FromLayoutBitmap(clay, shape);

        Assert.True(read.Ok, read.Refusal);
        var s = read.Source!;
        Assert.Equal(ImageOrigin.LayoutBitmap, s.Origin);
        Assert.Equal(Path.GetFullPath(png), s.Path);
        Assert.Equal("board.png", s.OriginalName);
        Assert.Equal((400, 300), (s.Raster.Width, s.Raster.Height));
        Assert.Equal(new LayoutBitmapPlacement(Path.GetFullPath(clay), -1000, 2000, 400_000, 300_000, new LayerKey(7, 0)), s.Placement);
    }

    [Fact]
    public void AnUnresolvedPath_RefusesNamingResolvePath()
    {
        var shape = new BitmapShape { ImagePathRef = "pics/gone.png", W = 10, H = 10 };
        var read = ImageSource.FromLayoutBitmap(Path.Combine(_dir, "layout", "amp.clay"), shape);
        Assert.False(read.Ok);
        Assert.Contains("Resolve Path…", read.Refusal);
    }
}
