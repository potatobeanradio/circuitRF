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

    /// <summary>A raster encoded as PNG bytes — what a file or the clipboard hands <c>ImageSource</c>.</summary>
    public static byte[] Png(RasterImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(image.Rgba, 0, bmp.GetPixels(), image.Rgba.Length);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A layout picture: copper and a second layer as filled rectangles of several sizes on white.</summary>
    public static RasterImage Layout() => Draw(400, 300, c =>
    {
        using var copper = Fill(new SKColor(0xc8, 0x75, 0x33));
        using var green = Fill(new SKColor(0x2e, 0x8b, 0x57));
        c.DrawRect(30, 30, 140, 70, copper);
        c.DrawRect(30, 200, 90, 60, copper);
        c.DrawRect(230, 40, 50, 210, copper);
        c.DrawRect(300, 180, 70, 70, green);
        c.DrawRect(140, 140, 60, 40, green);
    });

    /// <summary>A schematic picture: thin orthogonal wires, a resistor zig-zag and two capacitor plates.</summary>
    public static RasterImage Schematic() => Draw(400, 300, c =>
    {
        using var pen = Stroke(SKColors.Black, 2);
        using var plate = Stroke(SKColors.Black, 3);
        c.DrawLine(40, 150, 120, 150, pen);
        using var zig = new SKPath();
        zig.MoveTo(120, 150);
        for (int k = 0; k < 6; k++) zig.LineTo(127 + 13 * k, k % 2 == 0 ? 140 : 160);
        zig.LineTo(200, 150);
        c.DrawPath(zig, pen);
        c.DrawLine(200, 150, 260, 150, pen);
        c.DrawLine(260, 125, 260, 175, plate);
        c.DrawLine(272, 125, 272, 175, plate);
        c.DrawLine(272, 150, 360, 150, pen);
        c.DrawLine(40, 150, 40, 240, pen);
        c.DrawLine(40, 240, 360, 240, pen);
        c.DrawLine(360, 240, 360, 150, pen);
    });

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
