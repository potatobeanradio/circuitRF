using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>One term of the static image expansion: <c>a/√(ρ² + b²)</c>, b complex.</summary>
public sealed record StaticImage(Complex Amplitude, Complex Depth);

/// <summary>
/// How <see cref="InteriorStaticImages.Fit"/> samples and fits. Every default is derived from the
/// stack's own geometry at fit time; what is here is the sampling budget, not a length.
/// </summary>
/// <param name="Samples">Samples per level. Two levels are taken (see the class remarks).</param>
/// <param name="MaxOrder">Ceiling on each level's Prony order. <see cref="Prony.Fit"/> stops
/// earlier of its own accord when the system goes rank deficient, which is what over-ordering
/// looks like.</param>
/// <param name="PronyTolerance">Passed through to <see cref="Prony.Fit"/>.</param>
/// <param name="GridSpan">The dimensionless <c>α·Δk</c> each level's grid is designed around: the
/// spacing that makes a depth of that level's own scale a well-separated Prony root. 0.45 puts
/// <c>e^{−αΔ}</c> near 0.64 — neither indistinguishable from 1 nor lost in round-off.</param>
public sealed record InteriorStaticFitSettings(
    int    Samples        = 96,
    int    MaxOrder       = 14,
    double PronyTolerance = 1e-13,
    double GridSpan       = 0.45)
{
    public static readonly InteriorStaticFitSettings Default = new();
}

/// <summary>
/// The fitted static Green's function at one pair of heights: an exact <c>1/ρ</c> term plus a small
/// set of complex images. Evaluating it is a handful of square roots, which is what lets the fill's
/// radial table be built at all.
/// </summary>
public sealed class InteriorStaticModel
{
    public LayerStack Stack  { get; }
    public bool       Scalar { get; }
    public double     Z      { get; }
    public double     Zp     { get; }

    /// <summary>
    /// <b>c_∞ — the exact coefficient of the 1/ρ singularity, before the 1/4π.</b> Not fitted:
    /// <see cref="InteriorStaticGreens.AsymptoticConstant"/> is closed form, and leaving the
    /// singular part to a least-squares fit is how a kernel acquires a wrong self-term.
    /// </summary>
    public Complex Singular { get; }

    public IReadOnlyList<StaticImage> Images { get; }

    /// <summary>RMS of the spectral fit residual over every sample, relative to the remainder's own
    /// peak. This is the number the method is judged on and it is carried, not discarded.</summary>
    public double Residual { get; }

    /// <summary>The largest k the fit was sampled at — the range over which <see cref="Residual"/>
    /// means anything.</summary>
    public double SampledToK { get; }

    internal InteriorStaticModel(LayerStack stack, bool scalar, double z, double zp,
                                 Complex singular, IReadOnlyList<StaticImage> images,
                                 double residual, double sampledToK)
    {
        Stack = stack; Scalar = scalar; Z = z; Zp = zp;
        Singular = singular; Images = images; Residual = residual; SampledToK = sampledToK;
    }

    /// <summary>
    /// <c>G(ρ)</c>, the same normalisation as everything else here: free space is <c>1/(4πρ)</c> and
    /// <c>φ = (1/ε₀)∫G q dS′</c>.
    /// </summary>
    public Complex Evaluate(double rhoM)
    {
        if (!(rhoM > 0)) throw new ArgumentOutOfRangeException(nameof(rhoM), rhoM, "ρ must be positive.");
        Complex v = Singular / rhoM;
        foreach (var im in Images) v += im.Amplitude / Root(rhoM, im.Depth);
        return v / (4.0 * Math.PI);
    }

    /// <summary>
    /// The ρ → 0 value of the SMOOTH part — <c>G(ρ) − c_∞/(4πρ)</c> as ρ → 0, which is what
    /// <see cref="PlanarKernelTerms"/>'s <c>Constant</c> extraction coefficient is.
    /// </summary>
    public Complex SmoothAtZero
    {
        get
        {
            Complex v = Complex.Zero;
            foreach (var im in Images) v += im.Amplitude / im.Depth;
            return v / (4.0 * Math.PI);
        }
    }

    /// <summary>
    /// <c>√(ρ² + b²)</c> on the branch that continues from <c>b</c> at ρ = 0 — i.e. the one with a
    /// positive real part. <see cref="Complex.Sqrt"/>'s principal branch flips for a strongly
    /// complex depth, and a flipped image is a sign error in the potential rather than a small one.
    /// </summary>
    internal static Complex Root(double rho, Complex depth)
    {
        Complex s = Complex.Sqrt(rho * rho + depth * depth);
        return s.Real < 0 ? -s : s;
    }
}

/// <summary>
/// <b>MIM-4 / milestone 2 — the static spectral kernel turned into a spatial one, by fitting the
/// remainder as a sum of complex exponentials in k_ρ and transforming each one in closed form.</b>
///
/// <para><c>∫₀^∞ e^{−b k}J₀(kρ) dk = 1/√(ρ² + b²)</c>, so a fit of
/// <c>G̃(k) − c_∞</c> as <c>Σ a_i e^{−b_i k}</c> IS an image expansion, and the classic grounded-slab
/// series is the special case where the <c>b_i</c> are the exact depths <c>2nh</c>.</para>
///
/// <para><b>Why a fit and not the exact expansion.</b> The exact multivariate geometric expansion of
/// the cascade exists — the answer is a rational function of the <c>e^{−2k d_j}</c> — but its term
/// count is bounded by AMPLITUDE decay, and a PEC floor makes the ground-plane round trip's factor
/// exactly 1 in magnitude: the shipped ONE-slab series already needs ~130 images on GaAs
/// (<see cref="StaticGreens"/> records the |K| = 0.856 ratio), and a stratified stack multiplies
/// that by every other round trip it carries. The fit reaches the same function with ~10 terms
/// because it works on <c>G̃</c> as a smooth function of k rather than on its exponential content:
/// <c>x/(1 − Kx)</c> needs 130 exact images and two complex ones.</para>
///
/// <para><b>Why two levels.</b> A thin-film stack carries depths three orders of magnitude apart —
/// 0.4 µm for a round trip in the capacitor dielectric, hundreds of µm for one through the
/// substrate — and one uniform k grid cannot resolve both: a spacing fine enough for the deep
/// images spans a range in which the shallow ones have not begun to decay. §L9b's own two-level
/// scheme, for the same reason and with the same shape: level 1 (fine spacing, short range) takes
/// the DEEP images, level 2 subtracts them and takes the shallow ones on a coarse long grid, and
/// three candidate depth sets are scored by measured residual so that "better" is never asserted.</para>
///
/// <para><b>No branch point and no pole, which is the whole reason this is tamer than DCIM.</b> At
/// ω = 0 the spectral kernel is algebraic in k_ρ — <c>Prony</c> is fitting a smooth decaying
/// function on the real axis rather than a two-sheeted one along a deformed contour, so none of
/// §L8c's fragility follows this path.</para>
/// </summary>
public static class InteriorStaticImages
{
    /// <summary>The scalar (electrostatic) model at one pair of heights.</summary>
    public static InteriorStaticModel FitScalar(LayerStack stack, double z, double zp,
                                                InteriorStaticFitSettings? settings = null)
        => Fit(stack, scalar: true, z, zp, settings);

    /// <summary>The vector (magnetostatic) model at one pair of heights.</summary>
    public static InteriorStaticModel FitVector(LayerStack stack, double z, double zp,
                                                InteriorStaticFitSettings? settings = null)
        => Fit(stack, scalar: false, z, zp, settings);

    public static InteriorStaticModel Fit(LayerStack stack, bool scalar, double z, double zp,
                                          InteriorStaticFitSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(stack);
        var s = settings ?? InteriorStaticFitSettings.Default;

        z  = InteriorStaticGreens.SnapToInterface(stack, z);
        zp = InteriorStaticGreens.SnapToInterface(stack, zp);

        Complex cInf = InteriorStaticGreens.AsymptoticConstant(stack, scalar, z, zp);
        Complex Remainder(double k) =>
            InteriorStaticGreens.Spectral(InteriorStaticGreens.Build(stack, scalar, k), z, zp) - cInf;

        var (aMin, aMax) = InteriorStaticGreens.DecayScales(stack, z, zp);

        // Level 1 resolves the DEEP images (large b, fast decay in k); level 2 the shallow ones.
        double dDeep    = s.GridSpan / aMax;
        double dShallow = s.GridSpan / aMin;

        var (kDeep, yDeep)       = Sample(Remainder, dDeep, s.Samples);
        var (zDeep, aDeep, _)    = Prony.Fit(yDeep, s.MaxOrder, s.PronyTolerance);
        var deep                 = Depths(zDeep, dDeep);

        var (kShallow, yShallow) = Sample(Remainder, dShallow, s.Samples);
        var shallowResidual      = new Complex[yShallow.Length];
        for (int i = 0; i < yShallow.Length; i++)
        {
            Complex level1 = Complex.Zero;
            for (int j = 0; j < deep.Count; j++)
                level1 += aDeep[j] * Complex.Exp(-deep[j] * kShallow[i]);
            shallowResidual[i] = yShallow[i] - level1;
        }
        var (zShallow, _, _) = Prony.Fit(shallowResidual, s.MaxOrder, s.PronyTolerance);
        var shallow          = Depths(zShallow, dShallow);

        var both = new List<Complex>(deep);
        both.AddRange(shallow);

        // ── A third sample block, for the AMPLITUDES only ───────────────────────────────────────
        //
        // Both Prony grids are uniform, so their first step IS their resolution near k = 0 — and the
        // spatial far field at ρ is governed by k ≈ 1/ρ, which for any ρ past a few stack heights
        // falls INSIDE that first step. §L9b's own path fit has the same hole and fills it the same
        // way — a block neither of ITS paths visits, added to the least squares alone. Geometric, so
        // it spans five decades in 18 points; uniform samples could not reach there without a grid
        // 10^5 times longer. Measured: it cut the spatial far-field error 10-60x.
        var lowK = new List<double>(24);
        for (double k = dDeep * 1e-5; k < dDeep; k *= 1.9) lowK.Add(k);
        var kLow = lowK.ToArray();
        var yLow = new Complex[kLow.Length];
        for (int i = 0; i < kLow.Length; i++) yLow[i] = Remainder(kLow[i]);

        double[] ks = [.. kDeep, .. kShallow, .. kLow];
        Complex[] ys = [.. yDeep, .. yShallow, .. yLow];
        double peak = 0;
        foreach (var v in ys) peak = Math.Max(peak, v.Magnitude);
        if (!(peak > 0))
            return new InteriorStaticModel(stack, scalar, z, zp, cInf, [], 0.0, kShallow[^1]);

        // ── THE SUM RULE, imposed exactly rather than fitted ────────────────────────────────────
        //
        // Σa_j is the model's own value at k = 0, and c_∞ + Σa_j is the coefficient of the SPATIAL
        // function's 1/ρ tail. Over a PEC floor that coefficient is exactly zero — G̃(0) = 0, because
        // Γ↓(0) = −1 through any stack — so the far field is a cancellation between c_∞ and the
        // image sum, and an unconstrained least squares gets it right only to its own residual.
        // Measured, not assumed: without it the fit is exact to 2.4e-15 in the SPECTRUM and still
        // 8.5e-6 wrong in the spatial far field, which is the entire error budget spent on one number
        // the sampling already knows exactly. (It is NOT the whole of that error — the second moment
        // Σa·b² is the rest, and the low-k block above is what addresses that. See RESOLVED.md
        // §MIM-4 finding 3, which found the two in that order.)
        Complex rule = Remainder(0.0);

        List<StaticImage> best = [];
        double bestResidual = double.PositiveInfinity;

        foreach (var candidate in new[] { Deduplicate(both, aMin), Deduplicate(deep, aMin),
                                          Deduplicate(shallow, aMin) })
        {
            if (candidate.Count == 0) continue;
            if (!Solve(candidate, ks, ys, rule, out var amps)) continue;

            // Prune what the fit itself says is nothing and re-solve on the columns that are left:
            // an over-ordered Prony contributes depths whose amplitudes come back at 1e-16, and they
            // cost an evaluation each for ever after while adding no accuracy.
            var kept = Prune(candidate, amps);
            if (kept.Count < candidate.Count && Solve(kept, ks, ys, rule, out var reamps))
            { candidate.Clear(); candidate.AddRange(kept); amps = reamps; }

            double residual = Residual(candidate, amps, ks, ys) / peak;
            if (!(residual < bestResidual)) continue;

            bestResidual = residual;
            best = [.. candidate.Select((b, j) => new StaticImage(amps[j], b))];
        }

        return new InteriorStaticModel(stack, scalar, z, zp, cInf, best, bestResidual, kShallow[^1]);
    }

    private static (double[] K, Complex[] Y) Sample(Func<double, Complex> f, double spacing, int n)
    {
        var k = new double[n];
        var y = new Complex[n];
        for (int i = 0; i < n; i++) { k[i] = i * spacing; y[i] = f(k[i]); }
        return (k, y);
    }

    /// <summary>
    /// Prony's roots <c>z = e^{−bΔ}</c> turned back into depths. A root outside the unit disk is a
    /// GROWING exponential — no such term can appear in a decaying remainder, and keeping one would
    /// let the design matrix reproduce the samples and diverge everywhere else, so it is dropped
    /// rather than clamped.
    /// </summary>
    private static List<Complex> Depths(Complex[] roots, double spacing)
    {
        var kept = new List<Complex>(roots.Length);
        foreach (var r in roots)
        {
            if (r == Complex.Zero || r.Magnitude >= 1.0) continue;
            Complex b = -Complex.Log(r) / spacing;
            if (!(b.Real > 0) || double.IsNaN(b.Real) || double.IsNaN(b.Imaginary)) continue;
            kept.Add(b);
        }
        return kept;
    }

    /// <summary>
    /// Drop a depth that duplicates one already kept. §L9b's own note applies unchanged: two levels
    /// fitted independently will sometimes find the same exponential twice, and a design matrix with
    /// two near-identical columns does not fail loudly — it returns two enormous amplitudes that
    /// cancel on the samples and are noise everywhere else.
    /// </summary>
    private static List<Complex> Deduplicate(List<Complex> depths, double scale)
    {
        var kept = new List<Complex>(depths.Count);
        foreach (var b in depths)
        {
            bool dup = false;
            foreach (var k in kept)
                if ((b - k).Magnitude < 1e-3 * scale) { dup = true; break; }
            if (!dup) kept.Add(b);
        }
        return kept;
    }

    /// <summary>
    /// Least squares over the sampled spectrum, subject to <c>Σa_j = rule</c> exactly.
    ///
    /// <para>The constraint is eliminated rather than penalised: with
    /// <c>a_p = rule − Σ_{j≠p} a_j</c> the residual becomes
    /// <c>‖(φ_j − φ_p)b − (y − φ_p·rule)‖</c> over the remaining columns, which is an ordinary
    /// unconstrained problem of one lower order and needs no weight anybody has to choose. The
    /// eliminated column is the DEEPEST image — <c>φ_p</c> is then a spike near k = 0 and
    /// <c>φ_j − φ_p</c> is φ_j almost everywhere, so the conditioning is the unconstrained
    /// problem's.</para>
    /// </summary>
    private static bool Solve(List<Complex> depths, double[] ks, Complex[] ys, Complex rule,
                              out Complex[] amps)
    {
        int n = depths.Count;
        amps = new Complex[n];
        if (n == 0) return false;
        if (n == 1) { amps[0] = rule; return true; }

        int p = 0;
        for (int j = 1; j < n; j++) if (depths[j].Magnitude > depths[p].Magnitude) p = j;

        var psi = new Complex[ks.Length, n - 1];
        var rhs = new Complex[ks.Length];
        for (int i = 0; i < ks.Length; i++)
        {
            Complex pivot = Complex.Exp(-depths[p] * ks[i]);
            rhs[i] = ys[i] - pivot * rule;
            int c = 0;
            for (int j = 0; j < n; j++)
            {
                if (j == p) continue;
                psi[i, c++] = Complex.Exp(-depths[j] * ks[i]) - pivot;
            }
        }

        if (!LinearAlgebra.LeastSquares(psi, rhs, out var b)) return false;

        Complex sum = Complex.Zero;
        int t = 0;
        for (int j = 0; j < n; j++)
        {
            if (j == p) continue;
            amps[j] = b[t++];
            sum += amps[j];
        }
        amps[p] = rule - sum;
        return true;
    }

    private static List<Complex> Prune(List<Complex> depths, Complex[] amps)
    {
        double peak = 0;
        foreach (var a in amps) peak = Math.Max(peak, a.Magnitude);
        var kept = new List<Complex>(depths.Count);
        for (int j = 0; j < depths.Count; j++)
            if (amps[j].Magnitude > 1e-12 * peak) kept.Add(depths[j]);
        return kept.Count == 0 ? depths : kept;
    }

    private static double Residual(List<Complex> depths, Complex[] amps, double[] ks, Complex[] ys)
    {
        double sum = 0;
        for (int i = 0; i < ks.Length; i++)
        {
            Complex model = Complex.Zero;
            for (int j = 0; j < depths.Count; j++)
                model += amps[j] * Complex.Exp(-depths[j] * ks[i]);
            sum += (model - ys[i]).Magnitude * (model - ys[i]).Magnitude;
        }
        return Math.Sqrt(sum / ks.Length);
    }
}
