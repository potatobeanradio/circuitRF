// A per-pixel value in 0…1 — brief-img-1-raster-core.md R-im1-2, R-im1-7.
//
// What a contour is traced on. A cluster's coverage map says how much of each pixel is that colour; a binary mask is a
// coverage map with only 0 and 1. Tracing at 0.5 on coverage rather than on labels is where sub-pixel accuracy comes
// from: an anti-aliased edge pixel at 0.3 puts the edge 0.2 px away from its centre, not on a pixel boundary.

namespace CircuitRF.Design.Imaging;

public sealed class CoverageMap
{
    public CoverageMap(int width, int height, float[] values)
    {
        if (values.Length != (long)width * height) throw new ArgumentException("Length does not match the size.", nameof(values));
        Width = width;
        Height = height;
        Values = values;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major, top row first.</summary>
    public float[] Values { get; }

    public float this[int x, int y] => Values[y * Width + x];

    /// <summary>A mask as coverage: set pixels 1, the rest 0.</summary>
    public static CoverageMap FromMask(BinaryImage mask)
    {
        var v = new float[mask.Pixels.Length];
        for (int i = 0; i < v.Length; i++) v[i] = mask.Pixels[i] != 0 ? 1f : 0f;
        return new CoverageMap(mask.Width, mask.Height, v);
    }

    /// <summary>The pixels at or above <paramref name="level"/>.</summary>
    public BinaryImage Threshold(float level = 0.5f)
    {
        var m = new BinaryImage(Width, Height);
        for (int i = 0; i < Values.Length; i++) m.Pixels[i] = Values[i] >= level ? (byte)1 : (byte)0;
        return m;
    }
}

/// <summary>A one-bit picture: 1 is foreground (ink, copper), 0 background.</summary>
public sealed class BinaryImage
{
    public BinaryImage(int width, int height) : this(width, height, new byte[(long)width * height]) { }

    public BinaryImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != (long)width * height) throw new ArgumentException("Length does not match the size.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major, top row first; 0 or 1.</summary>
    public byte[] Pixels { get; }

    public bool this[int x, int y] => Pixels[y * Width + x] != 0;

    /// <summary>False outside the picture — the edge of the picture is the paper.</summary>
    public bool At(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height && Pixels[y * Width + x] != 0;

    public int Count()
    {
        int n = 0;
        foreach (var p in Pixels) n += p;
        return n;
    }

    public BinaryImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    public BinaryImage Inverted()
    {
        var r = new BinaryImage(Width, Height);
        for (int i = 0; i < Pixels.Length; i++) r.Pixels[i] = (byte)(1 - Pixels[i]);
        return r;
    }
}
