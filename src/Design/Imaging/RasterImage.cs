// A decoded picture — brief-img-1-raster-core.md R-im1-1, R-im1-10.
//
// Every later phase reads a picture through this type: decoded by SkiaSharp's SKCodec (already below the firewall),
// turned upright from its EXIF orientation, converted to sRGB, and flattened onto WHITE — a screenshot with a
// transparent background is a drawing on paper, and nothing downstream should have to know there was an alpha channel.
//
// Pixel space: x right, y DOWN, origin at the top-left pixel's corner, pixel centres at ½. PixelFrame is the one place
// that leaves it.

using System.Buffers.Binary;
using SkiaSharp;

namespace CircuitRF.Design.Imaging;

/// <summary>The resolution a file states, in dots per inch — recorded, never acted on (a screenshot's 72 or 96 dpi
/// describes a screen, not the board it shows).</summary>
/// <param name="Source">Where it was read: <c>PNG pHYs</c>, <c>JFIF density</c> or <c>BMP header</c>.</param>
public sealed record ImageResolution(double XDpi, double YDpi, string Source);

/// <summary>An sRGB RGBA8 raster with alpha already composited onto white (every A byte is 255).</summary>
public sealed class RasterImage
{
    /// <summary>The largest raster kept; a larger picture is area-averaged down to it (D16).</summary>
    public const long MaxPixels = 50_000_000;

    /// <summary>The formats <see cref="Decode"/> reads, in the words the refusal uses.</summary>
    public const string FormatsRead = "PNG, JPEG, BMP, GIF (first frame) and WebP";

    /// <summary>The refusal for a picture that does not decode. TIFF and HEIC are named because they are what a phone
    /// or a screenshot tool most often produces.</summary>
    public const string UndecodableRefusal =
        "The picture could not be read. circuitRF reads " + FormatsRead +
        "; TIFF and HEIC are not read. Export it as PNG and try again.";

    public RasterImage(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "A raster has at least one pixel.");
        if (rgba.Length != (long)width * height * 4) throw new ArgumentException("RGBA8 length does not match the size.", nameof(rgba));
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major RGBA8, four bytes a pixel, top row first.</summary>
    public byte[] Rgba { get; }

    /// <summary>The resolution the file stated, if it stated one.</summary>
    public ImageResolution? StatedResolution { get; init; }

    /// <summary>The linear factor the picture was reduced by to fit <see cref="MaxPixels"/> (1 when it was not):
    /// one pixel here is <c>1 / ReductionFactor</c> pixels of the file on a side.</summary>
    public double ReductionFactor { get; init; } = 1.0;

    /// <summary>The pixel at (x, y) packed as 0xRRGGBB.</summary>
    public int Rgb(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Rgba[i] << 16) | (Rgba[i + 1] << 8) | Rgba[i + 2];
    }

    /// <summary>A raster from Skia's own drawing — what the tests draw pictures with. Premultiplied or not, the alpha is
    /// composited onto white.</summary>
    public static RasterImage FromBitmap(SKBitmap bitmap)
    {
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
        byte[] rgba = new byte[info.BytesSize];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using var src = bitmap.PeekPixels();
            if (src is null || !src.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes))
                throw new InvalidOperationException("The bitmap's pixels could not be read.");
        }
        finally { handle.Free(); }
        FlattenOntoWhite(rgba);
        return new RasterImage(bitmap.Width, bitmap.Height, rgba);
    }

    /// <summary>Reads a picture from a file. A file that cannot be opened is reported as undecodable.</summary>
    public static RasterDecodeResult Load(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return RasterDecodeResult.Refused($"The picture '{Path.GetFileName(path)}' could not be opened: {e.Message}");
        }
        return Decode(bytes);
    }

    /// <summary>Decodes PNG, JPEG, BMP, GIF (first frame) or WebP: oriented upright, converted to sRGB, flattened onto
    /// white, and reduced to <see cref="MaxPixels"/> when larger.</summary>
    public static RasterDecodeResult Decode(ReadOnlySpan<byte> bytes) => Decode(bytes, MaxPixels);

    internal static RasterDecodeResult Decode(ReadOnlySpan<byte> bytes, long maxPixels)
    {
        if (bytes.Length == 0) return RasterDecodeResult.Refused(UndecodableRefusal);
        byte[] copy = bytes.ToArray();
        using var data = SKData.CreateCopy(copy);
        using var codec = SKCodec.Create(data);
        if (codec is null) return RasterDecodeResult.Refused(UndecodableRefusal);
        var format = codec.EncodedFormat;
        if (format is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Bmp
            or SKEncodedImageFormat.Gif or SKEncodedImageFormat.Webp))
            return RasterDecodeResult.Refused(UndecodableRefusal);

        int w = codec.Info.Width, h = codec.Info.Height;
        if (w <= 0 || h <= 0) return RasterDecodeResult.Refused(UndecodableRefusal);
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul, SKColorSpace.CreateSrgb());
        byte[] rgba = new byte[info.BytesSize];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(rgba, System.Runtime.InteropServices.GCHandleType.Pinned);
        SKCodecResult result;
        try { result = codec.GetPixels(info, handle.AddrOfPinnedObject()); }
        finally { handle.Free(); }
        if (result != SKCodecResult.Success) return RasterDecodeResult.Refused(UndecodableRefusal);

        FlattenOntoWhite(rgba);
        (rgba, w, h) = Orient(rgba, w, h, codec.EncodedOrigin);
        var image = new RasterImage(w, h, rgba) { StatedResolution = ReadResolution(copy, format) };
        return RasterDecodeResult.Decoded(ReduceToLimit(image, maxPixels));
    }

    /// <summary>Alpha composited onto white, in the sRGB bytes Skia blends in.</summary>
    private static void FlattenOntoWhite(byte[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a == 255) continue;
            int inv = 255 - a;
            rgba[i] = (byte)((rgba[i] * a + 255 * inv + 127) / 255);
            rgba[i + 1] = (byte)((rgba[i + 1] * a + 255 * inv + 127) / 255);
            rgba[i + 2] = (byte)((rgba[i + 2] * a + 255 * inv + 127) / 255);
            rgba[i + 3] = 255;
        }
    }

    /// <summary>Turns the stored pixels upright: the EXIF orientation says where the stored row 0 and column 0 sit
    /// when the picture is viewed. SKCodec reports it and leaves the pixels alone.</summary>
    internal static (byte[] Rgba, int Width, int Height) Orient(byte[] src, int w, int h, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or 0) return (src, w, h);
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int ow = swap ? h : w, oh = swap ? w : h;
        byte[] dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                (int dx, int dy) = origin switch
                {
                    SKEncodedOrigin.TopRight => (w - 1 - x, y),
                    SKEncodedOrigin.BottomRight => (w - 1 - x, h - 1 - y),
                    SKEncodedOrigin.BottomLeft => (x, h - 1 - y),
                    SKEncodedOrigin.LeftTop => (y, x),
                    SKEncodedOrigin.RightTop => (h - 1 - y, x),
                    SKEncodedOrigin.RightBottom => (h - 1 - y, w - 1 - x),
                    SKEncodedOrigin.LeftBottom => (y, w - 1 - x),
                    _ => (x, y),
                };
                Buffer.BlockCopy(src, (y * w + x) * 4, dst, (dy * ow + dx) * 4, 4);
            }
        return (dst, ow, oh);
    }

    /// <summary>A raster larger than <paramref name="maxPixels"/> area-averaged down to it, with the factor recorded;
    /// any other raster unchanged.</summary>
    public static RasterImage ReduceToLimit(RasterImage image, long maxPixels = MaxPixels)
    {
        long n = (long)image.Width * image.Height;
        if (n <= maxPixels) return image;
        double f = Math.Sqrt((double)maxPixels / n);
        int ow = Math.Max(1, (int)Math.Floor(image.Width * f)), oh = Math.Max(1, (int)Math.Floor(image.Height * f));
        while ((long)ow * oh > maxPixels) { if (ow >= oh) ow--; else oh--; }
        byte[] dst = AreaAverage(image.Rgba, image.Width, image.Height, ow, oh);
        return new RasterImage(ow, oh, dst)
        {
            StatedResolution = image.StatedResolution,
            ReductionFactor = image.ReductionFactor * ((double)ow / image.Width),
        };
    }

    /// <summary>Box-filter resampling with fractional edge weights, one destination row at a time so the working set
    /// is a row, not a second raster.</summary>
    private static byte[] AreaAverage(byte[] src, int w, int h, int ow, int oh)
    {
        double sx = (double)w / ow, sy = (double)h / oh;
        // Horizontal taps, shared by every row.
        var hFirst = new int[ow];
        var hWeights = new double[ow][];
        for (int ox = 0; ox < ow; ox++)
        {
            double a = ox * sx, b = (ox + 1) * sx;
            int first = (int)Math.Floor(a), last = Math.Min(w - 1, (int)Math.Ceiling(b) - 1);
            var wts = new double[last - first + 1];
            for (int x = first; x <= last; x++) wts[x - first] = (Math.Min(b, x + 1) - Math.Max(a, x)) / sx;
            hFirst[ox] = first;
            hWeights[ox] = wts;
        }
        byte[] dst = new byte[(long)ow * oh * 4];
        var acc = new double[ow * 3];
        for (int oy = 0; oy < oh; oy++)
        {
            Array.Clear(acc);
            double a = oy * sy, b = (oy + 1) * sy;
            int first = (int)Math.Floor(a), last = Math.Min(h - 1, (int)Math.Ceiling(b) - 1);
            for (int y = first; y <= last; y++)
            {
                double wy = (Math.Min(b, y + 1) - Math.Max(a, y)) / sy;
                long row = (long)y * w * 4;
                for (int ox = 0; ox < ow; ox++)
                {
                    double r = 0, g = 0, bl = 0;
                    var wts = hWeights[ox];
                    long p = row + (long)hFirst[ox] * 4;
                    for (int k = 0; k < wts.Length; k++, p += 4)
                    {
                        r += src[p] * wts[k];
                        g += src[p + 1] * wts[k];
                        bl += src[p + 2] * wts[k];
                    }
                    acc[ox * 3] += r * wy;
                    acc[ox * 3 + 1] += g * wy;
                    acc[ox * 3 + 2] += bl * wy;
                }
            }
            long o = (long)oy * ow * 4;
            for (int ox = 0; ox < ow; ox++, o += 4)
            {
                dst[o] = (byte)Math.Clamp((int)Math.Round(acc[ox * 3]), 0, 255);
                dst[o + 1] = (byte)Math.Clamp((int)Math.Round(acc[ox * 3 + 1]), 0, 255);
                dst[o + 2] = (byte)Math.Clamp((int)Math.Round(acc[ox * 3 + 2]), 0, 255);
                dst[o + 3] = 255;
            }
        }
        return dst;
    }

    // ── Stated resolution ─────────────────────────────────────────────────────────────────────────────────────────

    internal static ImageResolution? ReadResolution(byte[] b, SKEncodedImageFormat format)
    {
        try
        {
            return format switch
            {
                SKEncodedImageFormat.Png => PngPhys(b),
                SKEncodedImageFormat.Jpeg => JfifDensity(b),
                SKEncodedImageFormat.Bmp => BmpHeader(b),
                _ => null,
            };
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;   // a malformed header states nothing
        }
    }

    private static ImageResolution? PngPhys(byte[] b)
    {
        int p = 8;
        while (p + 12 <= b.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p));
            if (len < 0 || p + 12 + len > b.Length) return null;
            var type = System.Text.Encoding.ASCII.GetString(b, p + 4, 4);
            if (type == "pHYs" && len >= 9)
            {
                uint px = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + 8));
                uint py = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p + 12));
                // Unit 1 is the metre; unit 0 states an aspect ratio only, which is no resolution.
                return b[p + 16] == 1 && px > 0 && py > 0 ? new ImageResolution(px * 0.0254, py * 0.0254, "PNG pHYs") : null;
            }
            if (type is "IDAT" or "IEND") return null;
            p += 12 + len;
        }
        return null;
    }

    private static ImageResolution? JfifDensity(byte[] b)
    {
        int p = 2;
        while (p + 4 <= b.Length && b[p] == 0xFF)
        {
            byte marker = b[p + 1];
            int len = (b[p + 2] << 8) | b[p + 3];
            if (marker == 0xE0 && len >= 14 && b[p + 4] == (byte)'J' && b[p + 5] == (byte)'F' && b[p + 6] == (byte)'I' && b[p + 7] == (byte)'F')
            {
                int units = b[p + 11];
                int dx = (b[p + 12] << 8) | b[p + 13], dy = (b[p + 14] << 8) | b[p + 15];
                if (dx == 0 || dy == 0) return null;
                return units switch
                {
                    1 => new ImageResolution(dx, dy, "JFIF density"),
                    2 => new ImageResolution(dx * 2.54, dy * 2.54, "JFIF density"),
                    _ => null,
                };
            }
            if (marker == 0xDA) return null;   // start of scan: no header follows
            p += 2 + len;
        }
        return null;
    }

    private static ImageResolution? BmpHeader(byte[] b)
    {
        if (b.Length < 46) return null;
        int px = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(38)), py = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(42));
        return px > 0 && py > 0 ? new ImageResolution(px * 0.0254, py * 0.0254, "BMP header") : null;
    }

    // ── What a file IS, by its first bytes (brief-img-2 R-im2-6) ─────────────────────────────────────────────────────

    /// <summary>The extensions a picture is recognised by, lower case with the dot: the five formats
    /// <see cref="Decode"/> reads, JPEG under both of its spellings.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    /// <summary>How many leading bytes <see cref="DetectFormat(ReadOnlySpan{byte})"/> needs.</summary>
    public const int SniffLength = 18;

    /// <summary>The format a file's first bytes announce — <c>png</c>, <c>jpg</c>, <c>bmp</c>, <c>gif</c> or <c>webp</c>,
    /// the extension a kept copy is written with — or null. The signatures only, never a decode: this is what names a
    /// picture whose extension is missing or wrong. A BMP is a two-byte signature, so its info-header size is checked as
    /// well, or every file that happens to start with "BM" would be called a picture.</summary>
    public static string? DetectFormat(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return "png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "jpg";
        if (head.Length >= 6 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8))) return "gif";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return "webp";
        if (head.Length >= 18 && head[0] == (byte)'B' && head[1] == (byte)'M'
            && BinaryPrimitives.ReadInt32LittleEndian(head[14..]) is 12 or 40 or 52 or 56 or 64 or 108 or 124)
            return "bmp";
        return null;
    }

    /// <summary><see cref="DetectFormat(ReadOnlySpan{byte})"/> on a file's first bytes; null when it cannot be read.</summary>
    public static string? DetectFormat(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[SniffLength];
            int n = stream.ReadAtLeast(head, SniffLength, throwOnEndOfStream: false);
            return DetectFormat(head[..n]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>A decoded picture, or the sentence saying why there is none.</summary>
public sealed class RasterDecodeResult
{
    private RasterDecodeResult(RasterImage? image, string? refusal) { Image = image; Refusal = refusal; }

    public RasterImage? Image { get; }
    public string? Refusal { get; }
    public bool Ok => Image is not null;

    public static RasterDecodeResult Decoded(RasterImage image) => new(image, null);
    public static RasterDecodeResult Refused(string refusal) => new(null, refusal);
}
