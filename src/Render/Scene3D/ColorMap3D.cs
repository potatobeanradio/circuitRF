// brief-em3d-28 R-em3d28-1a — the colour maps brief 29's fields are painted with, defined once below the
// firewall so a later headless picture uses the same ones. Each is a list of stops interpolated
// linearly in sRGB; the stops are published colour-map data, not code.

namespace CircuitRF.Render.Scene3D;

/// <summary>A colour map from [0, 1] to RGB.</summary>
public sealed class ColorMap3D
{
    public string Name { get; }
    private readonly (float T, byte R, byte G, byte B)[] _stops;

    private ColorMap3D(string name, params (float, byte, byte, byte)[] stops) { Name = name; _stops = stops; }

    /// <summary>Perceptually uniform, dark to bright: magnitudes (|E|, surface current).</summary>
    public static ColorMap3D Viridis { get; } = new("viridis",
        (0.0f, 0x44, 0x01, 0x54), (0.1f, 0x48, 0x24, 0x75), (0.2f, 0x41, 0x44, 0x87), (0.3f, 0x35, 0x5f, 0x8d),
        (0.4f, 0x2a, 0x78, 0x8e), (0.5f, 0x21, 0x91, 0x8c), (0.6f, 0x22, 0xa8, 0x84), (0.7f, 0x44, 0xbf, 0x70),
        (0.8f, 0x7a, 0xd1, 0x51), (0.9f, 0xbd, 0xdf, 0x26), (1.0f, 0xfd, 0xe7, 0x25));

    /// <summary>Diverging, blue through grey to red: signed quantities and differences.</summary>
    public static ColorMap3D CoolWarm { get; } = new("coolwarm",
        (0.0f, 0x3b, 0x4c, 0xc0), (0.25f, 0x8d, 0xb0, 0xfe), (0.5f, 0xdd, 0xdd, 0xdd), (0.75f, 0xf4, 0x9a, 0x7b),
        (1.0f, 0xb4, 0x04, 0x26));

    public static IReadOnlyList<ColorMap3D> All { get; } = [Viridis, CoolWarm];

    /// <summary>The colour at <paramref name="t"/>, clamped to [0, 1].</summary>
    public (byte R, byte G, byte B) Sample(float t)
    {
        t = float.IsNaN(t) ? 0 : Math.Clamp(t, 0, 1);
        for (int i = 1; i < _stops.Length; i++)
        {
            if (t > _stops[i].T && i < _stops.Length - 1) continue;
            var (t0, r0, g0, b0) = _stops[i - 1];
            var (t1, r1, g1, b1) = _stops[i];
            float f = t1 > t0 ? (t - t0) / (t1 - t0) : 0;
            return (Lerp(r0, r1, f), Lerp(g0, g1, f), Lerp(b0, b1, f));
        }
        var s = _stops[^1];
        return (s.R, s.G, s.B);
    }

    /// <summary><paramref name="n"/> RGBA8 texels, for a 1-D lookup texture.</summary>
    public byte[] Table(int n = 256)
    {
        var t = new byte[4 * n];
        for (int i = 0; i < n; i++)
        {
            var (r, g, b) = Sample(n == 1 ? 0 : i / (float)(n - 1));
            t[4 * i] = r; t[4 * i + 1] = g; t[4 * i + 2] = b; t[4 * i + 3] = 255;
        }
        return t;
    }

    private static byte Lerp(byte a, byte b, float f) => (byte)Math.Clamp(MathF.Round(a + (b - a) * f), 0, 255);
}
