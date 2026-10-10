// Pictures for the raster-core gates, drawn in memory with SkiaSharp (anti-aliased) — brief-img-1-raster-core.md §4.
// Never a committed file.

using System;
using CircuitRF.Design.Imaging;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.Imaging;

internal static class Pictures
{
    /// <summary>A white picture of <paramref name="w"/> × <paramref name="h"/> with <paramref name="draw"/> on it.</summary>
    public static RasterImage Draw(int w, int h, Action<SKCanvas> draw, SKColor? ground = null)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(bmp))
        {
            c.Clear(ground ?? SKColors.White);
            draw(c);
        }
        return RasterImage.FromBitmap(bmp);
    }

    public static SKPaint Fill(SKColor colour) => new() { Color = colour, IsAntialias = true, Style = SKPaintStyle.Fill };

    public static SKPaint Stroke(SKColor colour, float width) => new()
    {
        Color = colour, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width, StrokeCap = SKStrokeCap.Butt,
    };

    /// <summary>The cluster nearest <paramref name="rgb"/>.</summary>
    public static int ClusterOf(ColourClusterSet set, int rgb)
    {
        var target = CieLab.FromRgb(rgb);
        int best = 0;
        for (int k = 1; k < set.Clusters.Count; k++)
            if (set.Clusters[k].Lab.DeltaE2(target) < set.Clusters[best].Lab.DeltaE2(target)) best = k;
        return best;
    }
}
