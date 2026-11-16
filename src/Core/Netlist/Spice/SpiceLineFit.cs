using System.Numerics;

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>A line's R, L, G and C over its whole length (Ω, H, S, F), written per unit length with LEN=1.</summary>
public readonly record struct SpiceRlgc(double R, double L, double G, double C);

/// <summary>A symmetric coupled pair's per-unit-length matrices, as the dialect's Maxwell form: [11, 12] of each (the pair is
/// symmetric, so 22 = 11). The dialect's CPL takes R and G as diagonals only.</summary>
public readonly record struct SpiceRlgcPair(SpiceRlgc Self, SpiceRlgc Mutual);

/// <summary>
/// The SPICE dialect's lossy lines fitted to circuitRF's own line models at one frequency, and how far they drift from them
/// elsewhere (docs/design/transient.md §9.2.3, brief TRN-6 R-trn6-9).
///
/// <para><b>Exact at the fit frequency by construction.</b> A uniform line is its characteristic impedance Zc and its γl;
/// R + jωL = Zc·γl and G + jωC = γl/Zc there, so the dialect's line built from those four numbers has the model's Zc and γl
/// at that one frequency to round-off. Away from it constant R, L, G, C cannot carry the model's dispersion or its √f loss,
/// and <see cref="LargestDeltaS"/> says by how much, on <see cref="Grid"/>.</para>
///
/// <para><b>The dialect's CPL takes per-unit-length matrices in Maxwell form, R and G as diagonals only</b> (its manual:
/// "For the R and G matrices the diagonal elements must be specified"). A symmetric pair's modes are its even and odd ones,
/// so L and C are [[Le+Lo, Le−Lo], …]/2 from each mode's own fit; R and G have an off-diagonal (Re − Ro)/2 that must be zero
/// for the dialect to express the pair, and a pair whose modes lose differently is refused.</para>
/// </summary>
public static class SpiceLineFit
{
    /// <summary>How many frequencies <see cref="Grid"/> holds.</summary>
    public const int GridPoints = 401;

    /// <summary>The comparison grid: <see cref="GridPoints"/> frequencies spaced logarithmically from <paramref name="loHz"/>
    /// to <paramref name="hiHz"/>, both included.</summary>
    public static double[] Grid(double loHz, double hiHz)
    {
        if (!(loHz > 0) || !(hiHz > loHz)) return [hiHz > 0 ? hiHz : loHz];
        var f = new double[GridPoints];
        double a = Math.Log(loHz), b = Math.Log(hiHz);
        for (int k = 0; k < GridPoints; k++) f[k] = Math.Exp(a + (b - a) * k / (GridPoints - 1));
        f[0] = loHz;
        f[^1] = hiHz;
        return f;
    }

    /// <summary>R, L, G, C over the whole line that reproduce <paramref name="zc"/> and <paramref name="gammaLength"/> at
    /// <paramref name="fHz"/>.</summary>
    public static SpiceRlgc FromLine(double zc, Complex gammaLength, double fHz)
    {
        double w = 2 * Math.PI * fHz;
        Complex z = zc * gammaLength, y = gammaLength / zc;
        return new SpiceRlgc(z.Real, z.Imaginary / w, y.Real, y.Imaginary / w);
    }

    /// <summary>The characteristic impedance and γl of a line of totals <paramref name="r"/> at <paramref name="fHz"/>.</summary>
    public static (Complex Zc, Complex GammaLength) Of(SpiceRlgc r, double fHz)
    {
        double w = 2 * Math.PI * fHz;
        Complex z = new(r.R, w * r.L), y = new(r.G, w * r.C);
        Complex gl = Complex.Sqrt(z * y);
        if (gl.Real < 0) gl = -gl;
        return (gl / y, gl);
    }

    /// <summary>S11 and S21 of a uniform line of <paramref name="zc"/> and <paramref name="gammaLength"/> between two
    /// <paramref name="z0"/> ports.</summary>
    public static (Complex S11, Complex S21) LineS(Complex zc, Complex gammaLength, double z0)
    {
        Complex a = Complex.Cosh(gammaLength), sh = Complex.Sinh(gammaLength);
        Complex b = zc * sh, c = sh / zc;
        Complex den = a + b / z0 + c * z0 + a;
        return ((b / z0 - c * z0) / den, 2 / den);
    }

    /// <summary>The largest |ΔS| (over S11 and S21) between the model, given as its Zc and γl at each frequency, and the
    /// dialect's line of totals <paramref name="r"/>, on <paramref name="grid"/>; and where it occurs.</summary>
    public static (double MaxDeltaS, double AtHz) LargestDeltaS(Func<double, (double Zc, Complex GammaLength)> model,
                                                                SpiceRlgc r, IReadOnlyList<double> grid, double z0 = 50)
    {
        double worst = 0, at = grid.Count > 0 ? grid[0] : 0;
        foreach (double f in grid)
        {
            var (zm, gm) = model(f);
            var (sm11, sm21) = LineS(zm, gm, z0);
            var (zt, gt) = Of(r, f);
            var (st11, st21) = LineS(zt, gt, z0);
            double d = Math.Max((sm11 - st11).Magnitude, (sm21 - st21).Magnitude);
            if (!(d <= worst)) { worst = d; at = f; }
        }
        return (worst, at);
    }

    /// <summary>The pair's per-unit-length matrices from each mode's per-line Zc and γl at <paramref name="fHz"/>.</summary>
    public static (SpiceRlgcPair Pair, SpiceRlgc Even, SpiceRlgc Odd) FromPair(double ze, Complex glE, double zo, Complex glO, double fHz)
    {
        var e = FromLine(ze, glE, fHz);
        var o = FromLine(zo, glO, fHz);
        var self = new SpiceRlgc((e.R + o.R) / 2, (e.L + o.L) / 2, (e.G + o.G) / 2, (e.C + o.C) / 2);
        var mutual = new SpiceRlgc((e.R - o.R) / 2, (e.L - o.L) / 2, (e.G - o.G) / 2, (e.C - o.C) / 2);
        return (new SpiceRlgcPair(self, mutual), e, o);
    }

    /// <summary>
    /// The largest |ΔS| over the pair's 4×4 S matrix between the model (each mode's Zc and γl per frequency) and the dialect's
    /// pair of matrices <paramref name="p"/>, on <paramref name="grid"/>. A symmetric pair's S is half the sum and half the
    /// difference of its two modes' line S, so its entries' errors are (ΔSe ± ΔSo)/2.
    /// </summary>
    public static (double MaxDeltaS, double AtHz) LargestDeltaS(
        Func<double, (double Ze, Complex GlE, double Zo, Complex GlO)> model, SpiceRlgcPair p, IReadOnlyList<double> grid,
        double z0 = 50)
    {
        var even = new SpiceRlgc(p.Self.R + p.Mutual.R, p.Self.L + p.Mutual.L, p.Self.G + p.Mutual.G, p.Self.C + p.Mutual.C);
        var odd = new SpiceRlgc(p.Self.R - p.Mutual.R, p.Self.L - p.Mutual.L, p.Self.G - p.Mutual.G, p.Self.C - p.Mutual.C);
        double worst = 0, at = grid.Count > 0 ? grid[0] : 0;
        foreach (double f in grid)
        {
            var m = model(f);
            var (me11, me21) = LineS(m.Ze, m.GlE, z0);
            var (mo11, mo21) = LineS(m.Zo, m.GlO, z0);
            var (ze, ge) = Of(even, f);
            var (zo, go) = Of(odd, f);
            var (te11, te21) = LineS(ze, ge, z0);
            var (to11, to21) = LineS(zo, go, z0);
            Complex de11 = me11 - te11, de21 = me21 - te21, do11 = mo11 - to11, do21 = mo21 - to21;
            double d = new[] { (de11 + do11).Magnitude, (de11 - do11).Magnitude, (de21 + do21).Magnitude, (de21 - do21).Magnitude }.Max() / 2;
            if (!(d <= worst)) { worst = d; at = f; }
        }
        return (worst, at);
    }
}
