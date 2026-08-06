using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>One surface-wave mode of a general <see cref="LayerStack"/>.</summary>
/// <param name="Polarization">TM or TE — the equivalent line the resonance lives on.</param>
/// <param name="Index">Ordinal within its polarisation, ascending in k_ρ (0 = slowest).</param>
/// <param name="KRho">The actual (complex, if the stack is lossy) pole location.</param>
/// <param name="LosslessKRho">The real root of the same relation with every tanδ dropped — where
/// the complex search started, and the independently checkable quantity.</param>
/// <param name="Residual">|Ψ(KRho)| normalised by the local scale of Ψ; a converged pole is ~1e-12.</param>
public sealed record LayeredSurfaceWaveMode(
    SurfaceWavePolarization Polarization,
    int                     Index,
    Complex                 KRho,
    double                  LosslessKRho,
    double                  Residual)
{
    public string Name => $"{(Polarization == SurfaceWavePolarization.Tm ? "TM" : "TE")}{Index}";
}

/// <summary>
/// <b>R-lyr-6 — the search states its domain and its confidence.</b> "None found" is only an answer
/// if the domain searched is stated, so it is carried with the result rather than left in a log.
/// </summary>
public sealed record SurfaceWaveSearchReport(
    double                                  KLo,
    double                                  KHi,
    int                                     Samples,
    IReadOnlyList<LayeredSurfaceWaveMode>   Modes,
    string                                  Domain)
{
    public int TmCount => Modes.Count(m => m.Polarization == SurfaceWavePolarization.Tm);
    public int TeCount => Modes.Count(m => m.Polarization == SurfaceWavePolarization.Te);

    /// <summary>The smallest gap between the pole and the real axis, relative to Re(k_ρ) — how close
    /// a pole sits to a real-axis integration contour, which is what decides whether it matters.</summary>
    public double ClosestApproachToRealAxis =>
        Modes.Count == 0 ? double.NaN
                         : Modes.Min(m => Math.Abs(m.KRho.Imaginary) / Math.Abs(m.KRho.Real));
}

/// <summary>
/// <b>D4 — locate and COUNT the surface-wave poles of a general stack; do not assume how many
/// there are.</b> A grounded slab has a TM₀ mode with no cutoff (L8a's R-lgf-3). An N-layer stack
/// supports more, and the count depends on frequency AND on the stack, with no closed-form cutoff
/// condition to fall back on. <b>A missed pole does not produce an obvious failure; it produces a
/// plausible kernel that is wrong at large ρ</b> — precisely the failure mode L8a's M4 burned a
/// milestone on.
///
/// <para><b>The dispersion function is a CHAIN-MATRIX determinant, and that is what makes the
/// search safe.</b> The obvious residual — the denominator of a generalised reflection coefficient,
/// or <c>1 − Γ_up Γ_dn</c> — carries the reflection coefficients' own poles, so a sign-change scan
/// over it finds spurious roots and can step over real ones. Composing the layers as transmission
/// chain matrices instead gives a function that is <b>entire</b> in k_ρ² except at the open
/// terminations' own branch points, because every matrix entry is written in the combinations
/// <c>cos(k_z d)</c>, <c>sin(k_z d)/k_z</c> and <c>k_z sin(k_z d)</c> — all EVEN in k_z (R-lyr-3
/// again: no interior k_i is a branch point) and all finite at k_z = 0 (so k_ρ = k_i is not a
/// numerical event). Ψ has no poles, so every sign change on the scan is a real mode.</para>
///
/// <para><b>The scan is on the LOSSLESS stack, and that is deliberate.</b> With tanδ dropped, Ψ is
/// real-valued on the real k_ρ axis inside the guided range (the state vector stays in the
/// "V real, I imaginary" subspace the chain matrices preserve), so a mode is a genuine SIGN CHANGE
/// and bisection cannot land on the wrong root — the same reason L8a bisects rather than running a
/// carelessly-started Newton, which happily converges onto the NEIGHBOURING mode and leaves the fit
/// carrying two copies of one pole and none of the other. Loss is then a small perturbation and the
/// real root is moved to the complex pole by a secant iteration in w = k_ρ².</para>
/// </summary>
public static class SurfaceWavePoles
{
    /// <summary>
    /// Default scan density for the UNIFORM part of the grid.
    ///
    /// <para><b>A uniform grid alone is not enough, and that is a measured fact rather than a
    /// precaution.</b> A thin grounded slab's TM₀ mode sits at <c>k_ρ/k₀ − 1 ≈ 1e-12</c> — L8a's own
    /// "TM₀ has no cutoff however thin the slab, verified down to h = 1 µm" case — which no uniform
    /// grid over a range of order k₀ can ever resolve. The scan therefore adds a LOGARITHMIC
    /// refinement hugging both ends of the guided range (down to 1e-14 of it), which is where
    /// near-cutoff and near-k_max modes live. Without it the finder silently returns "no modes" for
    /// exactly the case R-lgf-3 says can never legitimately return that.</para>
    /// </summary>
    public const int DefaultSamples = 4000;

    /// <summary>
    /// The dispersion function Ψ(k_ρ): entire (bar the open terminations' branch points), zero
    /// exactly on a surface-wave pole, and with no poles of its own.
    /// </summary>
    public static Complex DispersionFunction(LayerStack stack, double frequencyHz,
                                             SurfaceWavePolarization pol, Complex kRho)
    {
        double omega = 2.0 * Math.PI * frequencyHz;
        double k0    = omega / EmConstants.C0;
        Complex w    = kRho * kRho;
        int top      = stack.RegionCount - 1;

        Complex KzOf(int region)
        {
            var m = stack.MaterialOfRegion(region);
            Complex kSq = k0 * (Complex)k0 * m.EpsComplex * m.MuR;
            return SpectralGreens.ProperRoot(kSq - w);
        }

        // --- bottom state, normalised into the (V real, I imaginary) subspace ---
        Complex v, i;
        switch (stack.Bottom.Kind)
        {
            case TerminationKind.Pec: v = Complex.Zero; i = Complex.ImaginaryOne; break;
            case TerminationKind.Pmc: v = Complex.One;  i = Complex.Zero;         break;
            default:
            {
                Complex kzb = KzOf(0);
                var mb = stack.MaterialOfRegion(0);
                if (pol == SurfaceWavePolarization.Tm)
                {
                    // (V, I) ∝ (−j Z_b, j), scaled by ωε_b so nothing is divided by k_zb.
                    v = -Complex.ImaginaryOne * kzb;
                    i =  Complex.ImaginaryOne * omega * EmConstants.Eps0 * mb.EpsComplex;
                }
                else
                {
                    v = -Complex.ImaginaryOne * omega * EmConstants.Mu0 * mb.MuR;
                    i =  Complex.ImaginaryOne * kzb;
                }
                break;
            }
        }

        // --- propagate up through every finite layer ---
        for (int r = 1; r <= stack.LayerCount; r++)
        {
            double d  = stack.Layers[r - 1].ThicknessM;
            var    mm = stack.MaterialOfRegion(r);
            Complex kz = KzOf(r);
            Complex u  = kz * d;
            Complex c  = Complex.Cos(u);
            Complex sOverKz = d * Sinc(u);          // sin(k_z d)/k_z, even, finite at k_z = 0
            Complex kzS     = kz * kz * d * Sinc(u); // k_z sin(k_z d),  even

            Complex zs, ys;                          // Z·sin(u) and Y·sin(u)
            if (pol == SurfaceWavePolarization.Tm)
            {
                zs = kzS / (omega * EmConstants.Eps0 * mm.EpsComplex);
                ys = omega * EmConstants.Eps0 * mm.EpsComplex * sOverKz;
            }
            else
            {
                zs = omega * EmConstants.Mu0 * mm.MuR * sOverKz;
                ys = kzS / (omega * EmConstants.Mu0 * mm.MuR);
            }

            Complex vNew = c * v - Complex.ImaginaryOne * zs * i;
            Complex iNew = -Complex.ImaginaryOne * ys * v + c * i;
            v = vNew;
            i = iNew;

            // Keep the state O(1): a thick evanescent layer grows cosh(αd), and Ψ = 0 is
            // scale-invariant, so renormalise rather than overflow.
            double scale = Math.Max(v.Magnitude, i.Magnitude);
            if (scale > 1e100 || (scale < 1e-100 && scale > 0)) { v /= scale; i /= scale; }
        }

        // --- top condition ---
        switch (stack.Top.Kind)
        {
            case TerminationKind.Pec: return v;
            case TerminationKind.Pmc: return -Complex.ImaginaryOne * i;
            default:
            {
                Complex kzt = KzOf(top);
                var mt = stack.MaterialOfRegion(top);
                return pol == SurfaceWavePolarization.Tm
                    ? omega * EmConstants.Eps0 * mt.EpsComplex * v - kzt * i
                    : kzt * v - omega * EmConstants.Mu0 * mt.MuR * i;
            }
        }
    }

    /// <summary>sin(z)/z, entire, exactly 1 at z = 0 and stable for tiny |z|.</summary>
    private static Complex Sinc(Complex z)
    {
        if (z.Magnitude < 1e-5)
        {
            Complex z2 = z * z;
            return 1.0 - z2 / 6.0 + z2 * z2 / 120.0;
        }
        return Complex.Sin(z) / z;
    }

    /// <summary>The stack with every tanδ removed — the real-valued problem the scan runs on.</summary>
    public static LayerStack Lossless(LayerStack stack)
    {
        var layers = stack.Layers
            .Select(l => new MediumLayer(l.ThicknessM, new EmMaterial(l.Material.EpsR, 0, l.Material.MuR)))
            .ToArray();
        static Termination Strip(Termination t) =>
            t.Kind == TerminationKind.HalfSpace
                ? Termination.OpenTo(new EmMaterial(t.Material.EpsR, 0, t.Material.MuR))
                : t;
        return new LayerStack(Strip(stack.Bottom), layers, Strip(stack.Top));
    }

    /// <summary>
    /// Every surface-wave mode of the stack at this frequency, plus the domain that was searched.
    /// </summary>
    public static SurfaceWaveSearchReport Find(LayerStack stack, double frequencyHz,
                                               int samples = DefaultSamples)
    {
        double k0 = 2.0 * Math.PI * frequencyHz / EmConstants.C0;
        var lossless = Lossless(stack);

        // A guided mode is evanescent in every OPEN termination and propagating somewhere inside,
        // so k_ρ lies strictly between the fastest open termination and the slowest medium.
        double kLo = 0;
        foreach (var t in new[] { stack.Bottom, stack.Top })
            if (t.Kind == TerminationKind.HalfSpace)
                kLo = Math.Max(kLo, k0 * Math.Sqrt(t.Material.EpsR * t.Material.MuR));

        double kHi = 0;
        foreach (var l in stack.Layers)
            kHi = Math.Max(kHi, k0 * Math.Sqrt(l.Material.EpsR * l.Material.MuR));

        string domain = kHi <= kLo
            ? $"k_ρ/k₀ ∈ ({kLo / k0:G6}, {kHi / k0:G6}) — EMPTY: no medium in the stack is slower " +
              $"than its fastest open termination, so no mode can be guided."
            : $"k_ρ/k₀ ∈ ({kLo / k0:G6}, {kHi / k0:G6}), {samples} uniform samples per polarisation " +
              $"(Δk_ρ/k₀ = {(kHi - kLo) / k0 / samples:E2}) PLUS a logarithmic refinement hugging both " +
              $"ends down to 1e-14 of the range; sign changes of the lossless chain-matrix dispersion " +
              $"function, bisected then moved to the complex pole by secant.";

        var modes = new List<LayeredSurfaceWaveMode>();
        if (kHi <= kLo * (1 + 1e-12))
            return new SurfaceWaveSearchReport(kLo, kHi, samples, modes, domain);

        // The padding is 1e-14 of the range, not something comfortable like 1e-9: a thin grounded
        // slab's TM₀ mode sits at ~1e-12 of the range above cutoff, so a "safe" guard band is the
        // difference between finding it and reporting that a stack with no cutoff has no mode.
        double lo = kLo + (kHi - kLo) * 1e-14;
        double hi = kHi - (kHi - kLo) * 1e-14;

        foreach (var pol in new[] { SurfaceWavePolarization.Tm, SurfaceWavePolarization.Te })
        {
            var grid = BuildGrid(lo, hi, samples);
            var vals = new double[grid.Length];
            var raw  = new Complex[grid.Length];
            for (int s = 0; s < grid.Length; s++)
                raw[s] = DispersionFunction(lossless, frequencyHz, pol, grid[s]);

            // The lossless Ψ is real-valued in this range up to an overall constant phase; take
            // whichever component actually carries the signal rather than assuming which.
            double reScale = raw.Sum(c => Math.Abs(c.Real));
            double imScale = raw.Sum(c => Math.Abs(c.Imaginary));
            bool useReal = reScale >= imScale;
            for (int s = 0; s < grid.Length; s++) vals[s] = useReal ? raw[s].Real : raw[s].Imaginary;

            int index = 0;
            for (int s = 0; s + 1 < grid.Length; s++)
            {
                if (vals[s] == 0 || vals[s] * vals[s + 1] > 0) continue;

                double a = grid[s], b = grid[s + 1], fa = vals[s];
                for (int it = 0; it < 200; it++)
                {
                    double mid = 0.5 * (a + b);
                    var    c   = DispersionFunction(lossless, frequencyHz, pol, mid);
                    double fm  = useReal ? c.Real : c.Imaginary;
                    if (fa * fm <= 0) b = mid; else { a = mid; fa = fm; }
                }

                double root = 0.5 * (a + b);
                Complex refined = RefineComplex(stack, frequencyHz, pol, root);
                double residual = NormalisedResidual(stack, frequencyHz, pol, refined, kHi - kLo);
                modes.Add(new LayeredSurfaceWaveMode(pol, index++, refined, root, residual));
            }
        }

        // Ascending in k_ρ within each polarisation is what the enumeration above already produces.
        return new SurfaceWaveSearchReport(kLo, kHi, samples, modes, domain);
    }

    /// <summary>
    /// Uniform samples plus a logarithmic refinement at both ends — see <see cref="DefaultSamples"/>
    /// for why the ends need it.
    /// </summary>
    private static double[] BuildGrid(double lo, double hi, int samples)
    {
        var t = new SortedSet<double>();
        for (int j = 0; j <= samples; j++) t.Add((double)j / samples);
        for (double p = 14.0; p >= 0.0; p -= 0.25)
        {
            double e = Math.Pow(10, -p);
            t.Add(e);
            t.Add(1.0 - e);
        }
        return t.Select(x => lo + (hi - lo) * x).ToArray();
    }

    /// <summary>
    /// Move a lossless real root onto the actual complex pole by a secant iteration in w = k_ρ².
    /// Loss is a small perturbation, so this converges in a handful of steps; if it does not, the
    /// lossless root is returned unchanged rather than a wandering iterate, and the caller's own
    /// residual check will notice.
    /// </summary>
    private static Complex RefineComplex(LayerStack stack, double f, SurfaceWavePolarization pol,
                                         double losslessKRho)
    {
        Complex w0 = losslessKRho * (Complex)losslessKRho;
        Complex w1 = w0 * (1.0 + 1e-7) + 1e-30;
        Complex f0 = DispersionFunction(stack, f, pol, Complex.Sqrt(w0));
        Complex f1 = DispersionFunction(stack, f, pol, Complex.Sqrt(w1));

        for (int it = 0; it < 100 && (f1 - f0).Magnitude > 0; it++)
        {
            Complex w2 = w1 - f1 * (w1 - w0) / (f1 - f0);
            if (!double.IsFinite(w2.Real) || !double.IsFinite(w2.Imaginary)) break;
            w0 = w1; f0 = f1;
            w1 = w2; f1 = DispersionFunction(stack, f, pol, Complex.Sqrt(w1));
            if ((w1 - w0).Magnitude <= 1e-15 * w1.Magnitude) break;
        }

        Complex root = Complex.Sqrt(w1);
        if (root.Real < 0) root = -root;
        return double.IsFinite(root.Real) && double.IsFinite(root.Imaginary) ? root : losslessKRho;
    }

    /// <summary>|Ψ| at the pole, divided by the local scale of |Ψ| a mode width away — dimensionless,
    /// and the honest measure of "did the search actually land on a root".</summary>
    private static double NormalisedResidual(LayerStack stack, double f, SurfaceWavePolarization pol,
                                             Complex kRho, double range)
    {
        double step = range * 1e-3;
        double scale = 0.5 * (DispersionFunction(stack, f, pol, kRho + step).Magnitude +
                              DispersionFunction(stack, f, pol, kRho - step).Magnitude);
        double at = DispersionFunction(stack, f, pol, kRho).Magnitude;
        return scale > 0 ? at / scale : at;
    }
}
