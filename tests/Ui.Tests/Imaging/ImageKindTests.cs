// brief-img-2-image-source-and-kind.md §4 — what kind of drawing a picture is (R-im2-2) and the override (R-im2-3).
// Pictures drawn in memory (Pictures.cs), never a committed file.

using System;
using CircuitRF.Design.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class ImageKindTests(ITestOutputHelper output)
{
    private ImageKindResult Classify(RasterImage raster)
    {
        var r = ImageKind.Classify(raster);
        output.WriteLine($"{r.Name} {r.Confidence} {r.Reason}");
        foreach (var e in r.Evidence) output.WriteLine($"  {e.Name} = {e.Value} ({e.Reads})");
        return r;
    }

    [Fact]
    public void FilledRectanglesInTwoColours_ReadLayout() =>
        Assert.Equal(DrawingKind.Layout, Classify(Pictures.Layout()).Kind);

    [Fact]
    public void ThinOrthogonalStrokes_ReadSchematic() =>
        Assert.Equal(DrawingKind.Schematic, Classify(Pictures.Schematic()).Kind);

    [Fact]
    public void NoisyColourGradient_ReadsNone_AsAPhotograph()
    {
        int w = 320, h = 240;
        var rgba = new byte[w * h * 4];
        var rng = new Random(1);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double t = (double)x / (w - 1), s = (double)y / (h - 1);
                int i = (y * w + x) * 4;
                rgba[i] = Noisy(60 + 140 * t, rng);
                rgba[i + 1] = Noisy(90 + 60 * s, rng);
                rgba[i + 2] = Noisy(160 - 80 * t, rng);
                rgba[i + 3] = 255;
            }

        var r = Classify(new RasterImage(w, h, rgba));
        Assert.Equal(DrawingKind.None, r.Kind);
        Assert.Equal("a photograph-like picture with no straight line work", r.Reason);
    }

    [Fact]
    public void AWhiteRaster_ReadsNone_Blank()
    {
        var rgba = new byte[200 * 150 * 4];
        Array.Fill(rgba, (byte)255);
        var r = Classify(new RasterImage(200, 150, rgba));
        Assert.Equal(DrawingKind.None, r.Kind);
        Assert.Equal("a blank picture", r.Reason);
    }

    /// <summary>R-im2-3: a forced kind is recorded in the provenance, with the reading it overrode kept; a forced read
    /// of a None picture is not refused for its kind.</summary>
    [Fact]
    public void AForcedKind_IsRecorded()
    {
        var source = ImageSource.FromBytes(Pictures.Png(Pictures.Schematic())).Source!;
        var read = ImageKind.Classify(source.Raster);
        var forced = read.Force(DrawingKind.Layout);
        Assert.Equal(DrawingKind.Schematic, forced.Suggested);

        var p = ImageProvenance.For(source, forced, "amp.source.png", new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal("layout", p.Kind);
        Assert.True(p.KindForced);
        Assert.Null(ImageProvenance.For(source, read, "amp.source.png", DateTime.UtcNow).KindForced);

        var blank = new byte[100 * 100 * 4];
        Array.Fill(blank, (byte)255);
        var none = ImageKind.Classify(new RasterImage(100, 100, blank)).Force(DrawingKind.Schematic);
        Assert.Equal(DrawingKind.Schematic, none.Kind);
        Assert.Equal(DrawingKind.None, none.Suggested);
    }

    private static byte Noisy(double v, Random rng)
    {
        // Box–Muller, σ = 12 levels.
        double g = Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        return (byte)Math.Clamp(Math.Round(v + 12 * g), 0, 255);
    }
}
