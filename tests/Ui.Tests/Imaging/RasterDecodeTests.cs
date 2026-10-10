// brief-img-1-raster-core.md R-im1-1 — decode: EXIF orientation applied, alpha onto white, the refusal, the 50 MP limit.

using System;
using System.Linq;
using CircuitRF.Design.Imaging;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests.Imaging;

public sealed class RasterDecodeTests
{
    [Fact]
    public void ARotatedExifJpeg_ComesBackUpright()
    {
        // Upright: 40 × 20 with a red block at the top-left. Stored as orientation 6 (the viewer turns it 90° clockwise),
        // so the stored pixels are 20 × 40 with the block at the bottom-left.
        using var stored = new SKBitmap(new SKImageInfo(20, 40, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(stored))
        {
            c.Clear(SKColors.White);
            using var red = new SKPaint { Color = SKColors.Red };
            c.DrawRect(new SKRect(0, 30, 10, 40), red);
        }
        using var img = SKImage.FromBitmap(stored);
        using var jpeg = img.Encode(SKEncodedImageFormat.Jpeg, 100);
        byte[] bytes = WithExifOrientation(jpeg.ToArray(), 6);

        var r = RasterImage.Decode(bytes);
        Assert.True(r.Ok, r.Refusal);
        var up = r.Image!;
        Assert.Equal((40, 20), (up.Width, up.Height));
        Assert.True(IsRed(up.Rgb(4, 4)), $"top-left {up.Rgb(4, 4):x6}");
        Assert.True(IsWhite(up.Rgb(35, 15)), $"bottom-right {up.Rgb(35, 15):x6}");
        Assert.True(IsWhite(up.Rgb(4, 15)), $"bottom-left {up.Rgb(4, 15):x6}");
    }

    [Fact]
    public void ATransparentPng_CompositesOntoWhite()
    {
        using var bmp = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bmp.Erase(SKColors.Transparent);
        using (var c = new SKCanvas(bmp))
            c.DrawRect(new SKRect(0, 0, 8, 16), new SKPaint { Color = new SKColor(0, 0, 0, 128), BlendMode = SKBlendMode.Src });
        using var png = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);

        var r = RasterImage.Decode(png.ToArray());
        Assert.True(r.Ok, r.Refusal);
        Assert.Equal(0xFFFFFF, r.Image!.Rgb(12, 8));                   // transparent → paper
        int grey = r.Image.Rgb(4, 8) & 0xFF;
        Assert.InRange(grey, 125, 129);                                // half-transparent black → mid grey
        Assert.All(Enumerable.Range(0, 16 * 16), i => Assert.Equal(255, r.Image.Rgba[i * 4 + 3]));
    }

    [Fact]
    public void AnUndecodableByteString_IsRefused_NamingTheFormats()
    {
        var r = RasterImage.Decode("this is not a picture at all"u8);
        Assert.False(r.Ok);
        foreach (var word in new[] { "PNG", "JPEG", "BMP", "GIF", "WebP", "TIFF", "HEIC", "Export it as PNG" })
            Assert.Contains(word, r.Refusal);
    }

    [Fact]
    public void ASixtyMegapixelRaster_IsReducedToFifty_WithItsFactor()
    {
        const int w = 10_000, h = 6_000;
        var rgba = new byte[(long)w * h * 4];
        Array.Fill(rgba, (byte)200);
        var big = new RasterImage(w, h, rgba);

        var small = RasterImage.ReduceToLimit(big);
        Assert.True((long)small.Width * small.Height <= RasterImage.MaxPixels);
        Assert.True((long)small.Width * small.Height > RasterImage.MaxPixels * 0.999);
        Assert.Equal(Math.Sqrt(50.0 / 60.0), small.ReductionFactor, 3);
        Assert.Equal((double)small.Width / w, small.ReductionFactor, 12);
        Assert.Equal(0xC8C8C8, small.Rgb(small.Width / 2, small.Height / 2));   // area-averaging keeps a flat colour
    }

    private static bool IsRed(int rgb) => ((rgb >> 16) & 0xFF) > 200 && ((rgb >> 8) & 0xFF) < 60 && (rgb & 0xFF) < 60;

    private static bool IsWhite(int rgb) => ((rgb >> 16) & 0xFF) > 230 && ((rgb >> 8) & 0xFF) > 230 && (rgb & 0xFF) > 230;

    /// <summary>The JPEG with an APP1 Exif segment holding only an orientation, inserted after SOI.</summary>
    private static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0, 0x2A, 0, 0, 0, 8,         // big-endian TIFF header, IFD at 8
            0, 1,                                               // one entry
            0x01, 0x12, 0, 3, 0, 0, 0, 1, (byte)(orientation >> 8), (byte)orientation, 0, 0,
            0, 0, 0, 0,                                         // no next IFD
        ];
        byte[] exif = [(byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, .. tiff];
        int len = exif.Length + 2;
        byte[] app1 = [0xFF, 0xE1, (byte)(len >> 8), (byte)len, .. exif];
        return [.. jpeg.AsSpan(0, 2), .. app1, .. jpeg.AsSpan(2)];
    }
}
