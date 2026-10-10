// sRGB → CIELAB (D65), and the CIE76 colour difference — brief-img-1-raster-core.md R-im1-2.
//
// The conversion is table-driven per channel byte, so a pixel's Lab is a pure function of its three bytes. The cube root
// and the gamma curve are evaluated once per table entry; two platforms whose libm disagreed in the last place would
// disagree in a table entry, never in the order anything is visited.

namespace CircuitRF.Design.Imaging;

/// <summary>A CIELAB colour.</summary>
public readonly record struct Lab(double L, double A, double B)
{
    /// <summary>CIE76 ΔE — the Euclidean distance in Lab.</summary>
    public double DeltaE(Lab o) => Math.Sqrt(DeltaE2(o));

    public double DeltaE2(Lab o)
    {
        double dl = L - o.L, da = A - o.A, db = B - o.B;
        return dl * dl + da * da + db * db;
    }
}

public static class CieLab
{
    private static readonly double[] Linear = BuildLinear();

    private static double[] BuildLinear()
    {
        var t = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return t;
    }

    /// <summary>The Lab of an sRGB colour.</summary>
    public static Lab FromRgb(int r, int g, int b)
    {
        double rl = Linear[r], gl = Linear[g], bl = Linear[b];
        double x = (0.4124564 * rl + 0.3575761 * gl + 0.1804375 * bl) / 0.95047;
        double y = 0.2126729 * rl + 0.7151522 * gl + 0.0721750 * bl;
        double z = (0.0193339 * rl + 0.1191920 * gl + 0.9503041 * bl) / 1.08883;
        double fx = F(x), fy = F(y), fz = F(z);
        return new Lab(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    /// <summary>The Lab of a packed 0xRRGGBB.</summary>
    public static Lab FromRgb(int rgb) => FromRgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private static double F(double t) => t > 216.0 / 24389.0 ? Math.Cbrt(t) : (24389.0 / 27.0 * t + 16) / 116;
}
