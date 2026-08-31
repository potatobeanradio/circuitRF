using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>Knobs on the direct integrator, exposed so convergence can be TESTED, not asserted.</summary>
public sealed record SommerfeldSettings(
    double RelativeTolerance = 1e-11,
    int    PanelNodes        = 16,
    int    MaxSubdivisions   = 14,
    int    MaxTailPanels     = 500,
    double OscillationDensity = 8.0)
{
    public static readonly SommerfeldSettings Default = new();

    /// <summary>A deliberately coarser setting, for the "refine and it must converge" gate.</summary>
    public SommerfeldSettings Coarser(double factor) => this with
    {
        RelativeTolerance  = RelativeTolerance * factor,
        OscillationDensity = Math.Max(2.0, OscillationDensity / Math.Sqrt(factor)),
    };
}

/// <summary>What the integrator returned, and what it had to do to get there.</summary>
public sealed record SommerfeldResult(
    Complex Value,
    Complex Direct,
    Complex QuasiStatic,
    Complex Integrated,
    long    Evaluations,
    int     TailPanels,
    bool    TailConverged,
    double  TailResidual);

/// <summary>
/// <b>M2 — direct numerical Sommerfeld integration. This is the ORACLE, not the product.</b>
///
/// <para><b>D3, and it is the technique that has actually worked three times in this area.</b>
/// Kernel A's exact image ground was proved sound by replacing it with an explicitly meshed 60 h
/// ground plate — ~4800 unknowns, no image at all — reproducing ε_eff to 0.14%. Route A's error at
/// L7b-b was measured against a closed-form 2×2 eigen-decomposition sharing the block construction
/// with production. Both times the point was the same: <b>a second formulation that shares no
/// approximation with the first</b>. DCIM validated against DCIM proves nothing.</para>
///
/// <para>Direct contour integration is slow, fiddly to make robust, and completely unsuitable as
/// the production path — which is exactly what makes it the right oracle. It shares nothing with
/// <see cref="Dcim"/> except <see cref="SpectralGreens"/>, i.e. the one thing that is <i>supposed</i>
/// to be common.</para>
///
/// <para><b>The contour, and why it is on the real axis.</b> The textbook picture deforms the path
/// off the real k_ρ axis to tame the oscillation. That is wrong here: J₀(z) grows like
/// e^{|Im z|}/√|z|, so a path lifted by b turns an O(1) answer into a difference of terms of size
/// e^{bρ} — at ρ = 10λ and a lift of 0.2k₀ that is 3e5, i.e. five digits gone before any physics.
/// The path therefore stays real and the two genuine difficulties are removed at the source
/// instead:</para>
/// <list type="number">
///   <item><b>The 1/k_z0 branch singularity at k_ρ = k₀ is removed exactly by substitution</b> —
///     k_ρ = k₀ sin θ below it (k_z0 = k₀cos θ, and dk_ρ = k₀cos θ dθ cancels the 1/k_z0 outright)
///     and k_ρ = k₀ cosh u above it (k_z0 = −jk₀ sinh u, same cancellation). Nothing is ever
///     divided by a small number; there is no integrable singularity left to resolve.</item>
///   <item><b>The non-decaying tail is removed by extracting the two closed-form pieces first</b> —
///     the free-space direct term and the k_ρ → ∞ quasi-static constant, each of which inverts to
///     an exact <c>C·e^{−jk₀ρ}/4πρ</c> by the Sommerfeld identity. What is left decays like 1/k_ρ²,
///     is partitioned at the zeros of J₀(k_ρρ), and is summed with repeated averaging.</item>
/// </list>
///
/// <para><b>It requires a LOSSY slab, and that is stated rather than worked around.</b> A lossless
/// grounded slab puts its TM₀ pole exactly on the real axis, i.e. exactly on this contour, and the
/// integral then only exists as a principal value plus a residue. Rather than build that (and its
/// own set of ways to be quietly wrong) for a path that exists only to check another path, the
/// integrator refuses tanδ = 0 by name — see <see cref="CanIntegrate"/>. The production
/// <see cref="Dcim"/> path has no such restriction, because it extracts the pole analytically.
/// Both starter substrates are lossy, so the measurement the brief asks for is unaffected.</para>
/// </summary>
public static class SommerfeldIntegral
{
    /// <summary>The free-space scalar Green's function e^{−jkR}/4πR — the normalisation everything
    /// in this file and in <see cref="SpectralGreens"/> is referred to.</summary>
    public static Complex FreeSpace(double k0, Complex r) =>
        Complex.Exp(-Complex.ImaginaryOne * k0 * r) / (4.0 * Math.PI * r);

    /// <summary>
    /// <b>L9c / D3 — the same identity at a COMPLEX wavenumber.</b> The Sommerfeld identity itself
    /// is unchanged for complex k; what changes is that an interior source's own region is lossy, so
    /// the wavenumber every extraction is referenced to is complex where the top half-space's k₀ is
    /// real. <b>The <c>double</c> overload is deliberately kept and is what the shipped
    /// top-half-space path still calls</b> — promoting a real k₀ into a <c>Complex</c> multiply
    /// re-associates the arithmetic and R-via-1 asks for that path to be bit-identical, which it is.
    /// </summary>
    public static Complex FreeSpace(Complex k, Complex r) =>
        Complex.Exp(-Complex.ImaginaryOne * k * r) / (4.0 * Math.PI * r);

    /// <summary>R-mom-17-shaped refusal: name the feature, name where it is handled instead.</summary>
    public static EmSuitability CanIntegrate(SpectralGreens g)
    {
        bool hasSlab = (g.EpsR - 1.0).Magnitude > 1e-12;
        if (hasSlab && g.Slab.Material.TanD <= 0)
            return EmSuitability.No(
                "The direct Sommerfeld integrator needs a LOSSY slab (tanδ > 0). A lossless grounded " +
                "slab puts its TM₀ surface-wave pole exactly on the real-k_ρ contour this integrator " +
                "uses, so the integral exists only as a principal value plus a residue — machinery " +
                "this path deliberately does not carry, because it exists to CHECK another path, not " +
                "to be one. Dcim has no such restriction: it extracts the pole in closed form. Both " +
                "starter substrates are lossy.");

        return EmSuitability.Yes;
    }

    /// <summary>
    /// The spatial-domain Green's function at lateral separation ρ, with source and observer both
    /// on the metal plane z = z′ = h (D2).
    /// </summary>
    public static SommerfeldResult Evaluate(SpectralGreens g, GreensKernel kernel, double rhoM,
                                            SommerfeldSettings? settings = null)
    {
        var ok = CanIntegrate(g);
        if (!ok.Ok) throw new ArgumentException(ok.Reason);
        if (!(rhoM > 0)) throw new ArgumentOutOfRangeException(nameof(rhoM), rhoM, "ρ must be positive.");

        var s = settings ?? SommerfeldSettings.Default;

        // Piece 1 — free space, exact.  Piece 2 — the quasi-static image at zero depth, exact.
        Complex direct      = FreeSpace(g.K0, rhoM);
        Complex kInfinity   = g.AsymptoticReflection(kernel);
        Complex quasiStatic = kInfinity * direct;

        // Piece 3 — everything left, integrated.  The numerator vanishes as k_ρ → ∞.
        Complex Numerator(Complex kRho) => g.Reflection(kernel, kRho) - kInfinity;

        double kSplit = SplitWavenumber(g);
        var breaks = new List<double>();
        foreach (var m in g.SurfaceWaveModes)
            AddPoleBreakpoints(breaks, g, m, kSplit);

        var integrated = Transform(Numerator, g.K0, rhoM, kSplit, breaks, s);

        return integrated with
        {
            Value       = direct + quasiStatic + integrated.Value,
            Direct      = direct,
            QuasiStatic = quasiStatic,
            Integrated  = integrated.Value,
        };
    }

    /// <summary>
    /// The generic Hankel/Sommerfeld transform of a spectral function written as
    /// <c>G̃(k_ρ) = F(k_ρ)/(2j k_z0)</c>, i.e. <c>G(ρ) = (1/2π)∫₀^∞ G̃ J₀(k_ρρ) k_ρ dk_ρ</c>.
    ///
    /// <para>Exposed because the Sommerfeld <i>identity</i> — the single mechanism the whole of DCIM
    /// rests on — has to be checkable standalone, on one exponential term, before a sum of them is
    /// checked. Pass <c>F = e^{−j k_z0 b}</c> and the answer must be <c>e^{−jk₀R}/4πR</c> with
    /// <c>R = √(ρ² + b²)</c>, for complex b as well as real.</para>
    /// </summary>
    public static SommerfeldResult Transform(Func<Complex, Complex> numerator, double k0, double rhoM,
                                             double kSplit, IReadOnlyList<double> breakpoints,
                                             SommerfeldSettings s)
    {
        var evals = new EvalCounter();
        double scaleHint = 1.0 / (4.0 * Math.PI * rhoM);      // the free-space term, as a magnitude
        double tol = s.RelativeTolerance * scaleHint;

        // ---- Segment A: k_ρ = k₀ sin θ on [0, π/2].  Below the light line; no poles live here.
        Complex FA(double theta)
        {
            evals.N++;
            double sin = Math.Sin(theta);
            Complex kRho = k0 * sin;
            return k0 / (4.0 * Math.PI * Complex.ImaginaryOne)
                 * numerator(kRho) * Bessel.J0(k0 * sin * rhoM) * sin;
        }
        int nA = BasePanels(k0 * rhoM, s);
        Complex a = IntegrateOver(FA, 0, Math.PI / 2, nA, [], tol, s);

        // ---- Segment B: k_ρ = k₀ cosh u on [0, u_split].  The surface-wave poles are in here.
        double uSplit = Math.Acosh(Math.Max(1.0, kSplit / k0));
        Complex FB(double u)
        {
            evals.N++;
            double cosh = Math.Cosh(u);
            return k0 / (4.0 * Math.PI) * numerator(k0 * cosh) * Bessel.J0(k0 * cosh * rhoM) * cosh;
        }
        var uBreaks = new List<double>();
        foreach (double kp in breakpoints)
            if (kp > k0 && kp < kSplit) uBreaks.Add(Math.Acosh(kp / k0));
        int nB = BasePanels(kSplit * rhoM, s);
        Complex b = IntegrateOver(FB, 0, uSplit, nB, uBreaks, tol, s);

        // ---- Segment C: plain k_ρ from kSplit outward.  1/k_ρ² envelope times J₀ — the tail.
        Complex FC(double kRho)
        {
            evals.N++;
            double alpha = Math.Sqrt(kRho * kRho - k0 * k0);
            return numerator(kRho) * Bessel.J0(kRho * rhoM) * kRho / (4.0 * Math.PI * alpha);
        }
        var tail = IntegrateTail(FC, rhoM, kSplit, tol, s);

        return new SommerfeldResult(a + b + tail.Value, 0, 0, a + b + tail.Value,
                                    evals.N, tail.Panels, tail.Converged, tail.Residual);
    }

    // ===========================================================================================
    // The tail — a J₀-zero-partitioned alternating series, summed by repeated averaging.
    // ===========================================================================================

    private static (Complex Value, int Panels, bool Converged, double Residual) IntegrateTail(
        Func<double, Complex> f, double rhoM, double kSplit, double tol, SommerfeldSettings s)
    {
        Complex acc = Complex.Zero;
        int panels = 0;

        // Before the first zero of J₀(k_ρρ) above kSplit there is no oscillation to exploit and the
        // integrand simply decays; walk it with geometric panels. For small ρ this stretch is long
        // (the first zero sits at 2.405/ρ) and is where most of the answer actually is.
        int firstIdx = FirstBesselZeroIndexAbove(kSplit * rhoM);
        double firstZero = BesselZero(firstIdx) / rhoM;
        double k = kSplit;
        while (k * 1.5 < firstZero && panels < s.MaxTailPanels)
        {
            double next = Math.Min(k * 1.5, firstZero);
            acc += IntegrateOver(f, k, next, 4, [], tol, s);
            k = next;
            panels++;
        }
        if (k < firstZero)
        {
            acc += IntegrateOver(f, k, firstZero, 4, [], tol, s);
            panels++;
            k = firstZero;
        }

        // From here on one panel per half-period. The panel integrals alternate in sign and decay
        // like n^{-5/2}; repeated averaging of the partial sums converges in a few dozen terms
        // where naive summation would need thousands.
        var row = new List<Complex>();
        Complex estimate = acc, previous = acc;
        double residual = double.PositiveInfinity;
        bool converged = false;
        int m = firstIdx;

        for (int i = 0; i < s.MaxTailPanels; i++, panels++)
        {
            double next = BesselZero(++m) / rhoM;
            acc += IntegrateOver(f, k, next, 4, [], tol, s);
            k = next;

            var newRow = new List<Complex>(row.Count + 1) { acc };
            for (int r = 0; r < row.Count; r++) newRow.Add(0.5 * (newRow[r] + row[r]));
            row = newRow;

            previous = estimate;
            estimate = row[^1];
            residual = (estimate - previous).Magnitude;
            if (i >= 5 && residual <= tol) { converged = true; break; }
        }

        return (estimate, panels, converged, residual);
    }

    // ===========================================================================================
    // Quadrature.
    // ===========================================================================================

    /// <summary>
    /// Split [a, b] at the supplied breakpoints and at <paramref name="basePanels"/> uniform
    /// divisions, then refine each panel adaptively. The base partition is what makes this robust
    /// against a NARROW feature: adaptive bisection alone can step over a surface-wave peak whose
    /// width is 1e-3 of the interval and report convergence, which is the classic way a "converged"
    /// quadrature is silently wrong.
    /// </summary>
    private static Complex IntegrateOver(Func<double, Complex> f, double a, double b, int basePanels,
                                         IReadOnlyList<double> breakpoints, double tol,
                                         SommerfeldSettings s)
    {
        if (!(b > a)) return Complex.Zero;

        var cuts = new List<double> { a, b };
        for (int i = 1; i < basePanels; i++) cuts.Add(a + (b - a) * i / basePanels);
        foreach (double x in breakpoints) if (x > a && x < b) cuts.Add(x);
        cuts.Sort();

        Complex sum = Complex.Zero;
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            if (cuts[i + 1] <= cuts[i]) continue;
            sum += Adaptive(f, cuts[i], cuts[i + 1], tol / Math.Max(1, cuts.Count - 1),
                            s.MaxSubdivisions, s.PanelNodes);
        }
        return sum;
    }

    private static Complex Adaptive(Func<double, Complex> f, double a, double b, double tol,
                                    int depth, int n)
    {
        Complex whole = GaussLegendre(f, a, b, n);
        double mid = 0.5 * (a + b);
        Complex halves = GaussLegendre(f, a, mid, n) + GaussLegendre(f, mid, b, n);
        if (depth <= 0 || (whole - halves).Magnitude <= tol) return halves;
        return Adaptive(f, a, mid, 0.5 * tol, depth - 1, n)
             + Adaptive(f, mid, b, 0.5 * tol, depth - 1, n);
    }

    private static Complex GaussLegendre(Func<double, Complex> f, double a, double b, int n)
    {
        var (x, w) = LegendreNodes(n);
        double half = 0.5 * (b - a), mid = 0.5 * (a + b);
        Complex sum = Complex.Zero;
        for (int i = 0; i < n; i++) sum += w[i] * f(mid + half * x[i]);
        return sum * half;
    }

    /// <summary>
    /// Gauss-Legendre nodes and weights, <b>computed</b> by Newton iteration on the Legendre
    /// recurrence rather than tabulated. Tables of quadrature constants are exactly the kind of
    /// thing D4 forbids taking from memory, and the recurrence is three lines.
    /// </summary>
    private static (double[] X, double[] W) LegendreNodes(int n)
    {
        lock (NodeCache)
        {
            if (NodeCache.TryGetValue(n, out var hit)) return hit;

            var x = new double[n];
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                double z = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5)), pp = 0;
                for (int it = 0; it < 200; it++)
                {
                    double p0 = 1, p1 = 0;
                    for (int j = 0; j < n; j++)
                    {
                        double p2 = p1;
                        p1 = p0;
                        p0 = ((2 * j + 1) * z * p1 - j * p2) / (j + 1);
                    }
                    pp = n * (z * p0 - p1) / (z * z - 1);
                    double dz = p0 / pp;
                    z -= dz;
                    if (Math.Abs(dz) < 1e-16) break;
                }
                x[i] = z;
                w[i] = 2.0 / ((1 - z * z) * pp * pp);
            }
            NodeCache[n] = (x, w);
            return (x, w);
        }
    }

    private static readonly Dictionary<int, (double[] X, double[] W)> NodeCache = new();

    // ===========================================================================================
    // Geometry of the contour.
    // ===========================================================================================

    /// <summary>
    /// Where the cosh substitution hands over to plain k_ρ: past every surface-wave pole and past
    /// the slab's own wavenumber, so segment C is monotone-decaying and pole-free.
    /// </summary>
    private static double SplitWavenumber(SpectralGreens g)
    {
        double k = Math.Max(2.0 * g.K0, 1.2 * g.K1.Magnitude);
        foreach (var m in g.SurfaceWaveModes) k = Math.Max(k, 1.5 * m.KRho.Real);
        return k;
    }

    /// <summary>
    /// Panel boundaries clustered around a surface-wave pole, at geometrically growing multiples of
    /// its own width. A lightly lossy slab (GaAs at tanδ = 0.002) puts the pole within ~1e-3 of the
    /// contour, so the peak is narrow enough for a uniform partition to walk straight past it.
    /// </summary>
    private static void AddPoleBreakpoints(List<double> into, SpectralGreens g, SurfaceWaveMode m, double kSplit)
    {
        double centre = m.KRho.Real;
        double width  = Math.Max(Math.Abs(m.KRho.Imaginary), 1e-9 * centre);
        for (int i = 0; i <= 12; i++)
        {
            double d = width * Math.Pow(2, i);
            if (centre - d > g.K0) into.Add(centre - d);
            if (centre + d < kSplit) into.Add(centre + d);
        }
        into.Add(centre);
    }

    /// <summary>
    /// How many uniform panels to lay down before adaptive refinement, from the number of Bessel
    /// oscillations the segment spans. Under-seeding here is the failure mode: adaptive bisection
    /// on an oscillatory integrand can find two levels that agree by coincidence.
    /// </summary>
    private static int BasePanels(double argumentSpan, SommerfeldSettings s) =>
        Math.Clamp((int)(s.OscillationDensity * argumentSpan / Math.PI) + 4, 4, 20000);

    // ===========================================================================================
    // Zeros of J₀ — the tail's natural partition.
    // ===========================================================================================

    /// <summary>
    /// The m-th positive zero of J₀ (m ≥ 1), from McMahon's leading asymptote β = (m − ¼)π refined
    /// by Newton with J₀′ = −J₁. Only the leading term is written down, so nothing is transcribed;
    /// Newton supplies the rest.
    /// </summary>
    public static double BesselZero(int m)
    {
        double z = (m - 0.25) * Math.PI;
        for (int i = 0; i < 60; i++)
        {
            double f = Bessel.J0(z).Real, d = -Bessel.J1(z).Real;
            double step = f / d;
            z -= step;
            if (Math.Abs(step) < 1e-15 * Math.Abs(z)) break;
        }
        return z;
    }

    /// <summary>The index of the smallest zero of J₀ strictly greater than <paramref name="x"/>.</summary>
    private static int FirstBesselZeroIndexAbove(double x)
    {
        int m = Math.Max(1, (int)(x / Math.PI));
        while (BesselZero(m) <= x) m++;
        while (m > 1 && BesselZero(m - 1) > x) m--;
        return m;
    }

    // ===========================================================================================
    // L9a — the same contour, driven through the GENERAL layered medium.
    //
    // Tier 4's rung, and what it does and does not establish: this path SHARES the spectral kernel
    // with the thing under test, so it validates the INVERSION, not the kernel. Tiers 1-3 (the
    // exact one-layer reduction, split-a-layer invariance, and the static limit) are what validate
    // the kernel. That is why the ladder has five entries instead of one.
    // ===========================================================================================

    /// <summary>
    /// The same restriction the one-layer oracle carries, for the same reason: a LOSSLESS guided
    /// stack puts its surface-wave poles exactly on the real-k_ρ contour this integrator uses, so
    /// the integral exists only as a principal value plus a residue — machinery this path
    /// deliberately does not carry, because it exists to CHECK another path, not to be one.
    /// </summary>
    public static EmSuitability CanIntegrateLayered(LayerStack stack)
    {
        if (stack.Top.Kind != TerminationKind.HalfSpace)
            return EmSuitability.No(
                $"The top termination is {stack.Top}, a solid wall. This oracle is referenced to a " +
                $"half-space above the stack, where the direct and quasi-static pieces have exact " +
                $"closed-form inverses; a closed guide needs a different partition.");

        bool guided = stack.Layers.Any(l =>
            l.Material.EpsR * l.Material.MuR > stack.Top.Material.EpsR * stack.Top.Material.MuR + 1e-12);
        bool lossy = stack.Layers.Any(l => l.Material.TanD > 0);

        if (guided && !lossy)
            return EmSuitability.No(
                "The direct Sommerfeld integrator needs at least one LOSSY layer (tanδ > 0). A " +
                "lossless guided stack puts its surface-wave poles exactly on the real-k_ρ contour " +
                "this integrator uses. Dcim has no such restriction: it extracts the poles in closed " +
                "form. Both starter substrates are lossy.");

        return EmSuitability.Yes;
    }

    /// <summary>
    /// The spatial-domain kernel of a general stratified medium at lateral separation ρ, with the
    /// source and observer at arbitrary heights <b>in the top half-space</b>.
    ///
    /// <para>Heights inside the stack are refused by name rather than approximated: the two exact
    /// pieces extracted below (the direct term and the quasi-static image) are the inverse
    /// transforms of <c>e^{−jk_z0|Δ|}/(2jk_z0)</c> and <c>Γ(∞)e^{−jk_z0Σ}/(2jk_z0)</c>, both
    /// referenced to the top half-space's own k_z0, and the substitutions that keep k_z0 out of
    /// every denominator are referenced to it too. A buried source needs a different partition, and
    /// it has one: <see cref="EvaluateInterior"/> at ω &gt; 0 (L9c), and MIM-4's
    /// <c>InteriorStaticGreens</c> at ω = 0.</para>
    /// </summary>
    public static SommerfeldResult EvaluateLayered(LayeredSpectralGreens g, GreensKernel kernel,
                                                   double rhoM, double z, double zp,
                                                   SommerfeldSettings? settings = null)
    {
        var ok = CanIntegrateLayered(g.Stack);
        if (!ok.Ok) throw new ArgumentException(ok.Reason);
        if (!(rhoM > 0)) throw new ArgumentOutOfRangeException(nameof(rhoM), rhoM, "ρ must be positive.");

        double h = g.Stack.TopZ;
        if (z < h || zp < h)
            throw new ArgumentException(
                $"EvaluateLayered is referenced to the top half-space and needs z, z′ ≥ H = {h:G6} m; " +
                $"got z = {z:G6}, z′ = {zp:G6}. A source INSIDE the stack goes through " +
                $"EvaluateInterior, which references the source's own region rather than the top " +
                $"half-space; this overload cannot be made to answer it.");

        var s = settings ?? SommerfeldSettings.Default;
        double delta = Math.Abs(z - zp);
        double sigma = z + zp - 2 * h;

        Complex direct      = FreeSpace(g.K0, Math.Sqrt(rhoM * rhoM + delta * delta));
        Complex gInfinity   = g.AsymptoticTopReflection(kernel);
        Complex quasiStatic = gInfinity * FreeSpace(g.K0, Math.Sqrt(rhoM * rhoM + sigma * sigma));

        Complex Numerator(Complex kRho) =>
            (g.TopInterfaceReflection(kernel, kRho) - gInfinity) *
            Complex.Exp(-Complex.ImaginaryOne * g.Kz0(kRho) * sigma);

        var poles = g.SurfaceWaves;   // found once per (stack, frequency), not per ρ

        double kSplit = 2.0 * g.K0;
        for (int r = 0; r < g.Stack.RegionCount; r++)
        {
            var m = g.Stack.MaterialOfRegion(r);
            kSplit = Math.Max(kSplit, 1.2 * g.K0 * Math.Sqrt(m.EpsR * m.MuR));
        }
        foreach (var m in poles.Modes) kSplit = Math.Max(kSplit, 1.5 * m.KRho.Real);

        var breaks = new List<double>();
        foreach (var m in poles.Modes)
        {
            double centre = m.KRho.Real;
            double width  = Math.Max(Math.Abs(m.KRho.Imaginary), 1e-9 * centre);
            for (int i = 0; i <= 12; i++)
            {
                double d = width * Math.Pow(2, i);
                if (centre - d > g.K0) breaks.Add(centre - d);
                if (centre + d < kSplit) breaks.Add(centre + d);
            }
            breaks.Add(centre);
        }

        var integrated = Transform(Numerator, g.K0, rhoM, kSplit, breaks, s);

        return integrated with
        {
            Value       = direct + quasiStatic + integrated.Value,
            Direct      = direct,
            QuasiStatic = quasiStatic,
            Integrated  = integrated.Value,
        };
    }

    // ===========================================================================================
    // L9c / M2 — THE INVERSE TRANSFORM FOR A SOURCE THAT IS NOT IN THE TOP HALF-SPACE.
    //
    // This is the milestone L9b scoped and deliberately did not build, and its four requirements
    // came from the STRUCTURE of the integrand rather than from a guess. All four are met here and
    // the two that changed the design are worth stating before the code:
    //
    //   1. THE SUBSTITUTIONS ARE GONE, AND THAT IS NOT A SIMPLIFICATION FOR ITS OWN SAKE.
    //      EvaluateLayered removes the 1/k_z0 branch singularity at k_ρ = k₀ by substituting
    //      k_ρ = k₀sinθ and k₀cosh u, both referenced to the TOP half-space. An interior source's
    //      extractions are referenced to its OWN region's k_m, which is complex whenever that region
    //      is lossy — so 1/k_zm never blows up on the real k_ρ axis and there is nothing to remove.
    //      Substituting anyway would put the wrong cancellation in the wrong place.
    //      **But the top half-space's branch point at k_ρ = k₀ is still in the integrand**, as a
    //      square-root KINK carried in through the up-looking ladder, and an adaptive quadrature that
    //      is not told about it will happily straddle it. It gets a breakpoint, exactly as the
    //      surface-wave poles already do — and so does every other region's own k_i, for the same
    //      reason (an open bottom's k_b is a genuine second branch point; an interior region's is
    //      not, but a Fresnel coefficient still turns over there).
    //
    //   2. THREE CLOSED-FORM EXTRACTIONS, NOT TWO — see LayeredSpectralGreens.AsymptoticAtHeights
    //      for why, and note that the third one has a DIFFERENT SHAPE for the mixed component: no
    //      1/k_z exponential at all, a 1/k_ρ² tail, and a logarithm rather than an e^{−jkR}/4πR.
    //
    // L8a's warning applies to the whole of it and is not re-litigated: THE PATH STAYS REAL, because
    // J₀ grows like e^{|Im z|} and any lift turns an O(1) answer into a difference of exponentials.
    // ===========================================================================================

    /// <summary>
    /// <b>R-mom-17 — what the interior oracle refuses, by name.</b> The lossless-guided refusal is
    /// the one <see cref="CanIntegrateLayered"/> already carries, for the same reason; the two new
    /// ones are that a source cannot sit inside a solid wall, and that the closed forms are still
    /// referenced to an open top.
    /// </summary>
    public static EmSuitability CanIntegrateInterior(LayeredSpectralGreens g, double z, double zp)
    {
        var basic = CanIntegrateLayered(g.Stack);
        if (!basic.Ok) return basic;

        foreach (var (x, what) in new[] { (zp, "source"), (z, "observer") })
        {
            int r = g.Stack.RegionOf(x);
            if (g.Stack.IsWall(r))
                return EmSuitability.No(
                    $"The {what} at z = {x:G6} m is inside the stack's " +
                    $"{(r == 0 ? g.Stack.Bottom : g.Stack.Top)} termination, which is a solid wall " +
                    $"rather than a medium. Place it inside a layer or in an open half-space.");
        }
        return EmSuitability.Yes;
    }

    /// <summary>
    /// <b>M2 — the spatial-domain kernel at ARBITRARY source and observer heights, including inside
    /// the stack and across regions. The ORACLE, not the product.</b>
    ///
    /// <para>It is a strict generalisation of <see cref="EvaluateLayered"/>: with both points in the
    /// top half-space the two must agree, and they compute it along genuinely different contours (this
    /// one on the plain real axis, that one through two substitutions), so their agreement is Tier 3's
    /// load-bearing rung rather than a tautology.</para>
    ///
    /// <para><b>Check this oracle before concluding anything from it.</b> This area has now had five
    /// occasions where the ORACLE, not the method, was at fault, and L9b's whole D3 conclusion rests
    /// on having spent 6 m 40 s checking one first. <see cref="SommerfeldResult.TailConverged"/> and
    /// <see cref="SommerfeldResult.TailResidual"/> are reported for that purpose and are not
    /// decoration — a cross-region pair separated by a 3 µm spacer needs the tail out to k_ρ ~ 1/3 µm
    /// and will exhaust <see cref="SommerfeldSettings.MaxTailPanels"/> long before it converges if the
    /// default is left alone.</para>
    /// </summary>
    public static SommerfeldResult EvaluateInterior(LayeredSpectralGreens g, GreensKernel kernel,
                                                    double rhoM, double z, double zp,
                                                    SommerfeldSettings? settings = null)
    {
        var ok = CanIntegrateInterior(g, z, zp);
        if (!ok.Ok) throw new ArgumentException(ok.Reason);
        if (!(rhoM > 0)) throw new ArgumentOutOfRangeException(nameof(rhoM), rhoM, "ρ must be positive.");

        var s = settings ?? SommerfeldSettings.Default;
        var a = g.AsymptoticAtHeights(kernel, z, zp);
        var evals = new EvalCounter();

        // ---- the closed-form pieces.
        Complex direct = Complex.Zero, image = Complex.Zero;
        double  regulator = 0;
        if (a.IsMixedForm)
        {
            // C·(e^{−k_ρΣ} − e^{−k_ρ(Σ+a)})/(2jk_ρ²).  Derived, not transcribed:
            //   d/dp ∫₀^∞ e^{−pk}J₀(kρ)dk/k = −∫₀^∞ e^{−pk}J₀(kρ)dk = −1/√(p²+ρ²),
            // so ∫₀^∞ e^{−pk}J₀(kρ)dk/k = −ln(p + √(p²+ρ²)) + C and the DIFFERENCE of two such
            // integrals is finite and closed form. The second exponential is a regulator, needed
            // because the bare 1/k² piece is log-divergent at k → 0 while the true kernel is finite
            // there; it is subtracted back in the closed form exactly, so it costs nothing.
            regulator = 1.0 / Complex.Abs(a.ReferenceWavenumber);
            double p = a.ImageDepth, q = a.ImageDepth + regulator;
            image = a.ImageCoefficient / (4.0 * Math.PI * Complex.ImaginaryOne)
                  * Complex.Log((q + Math.Sqrt(q * q + rhoM * rhoM)) /
                                (p + Math.Sqrt(p * p + rhoM * rhoM)));
        }
        else
        {
            if (a.DirectCoefficient != Complex.Zero)
                direct = a.DirectCoefficient
                       * FreeSpace(a.ReferenceWavenumber, Math.Sqrt(rhoM * rhoM + a.DirectDepth * a.DirectDepth));
            if (a.ImageCoefficient != Complex.Zero)
                image = a.ImageCoefficient
                      * FreeSpace(a.ReferenceWavenumber, Math.Sqrt(rhoM * rhoM + a.ImageDepth * a.ImageDepth));
        }

        // ---- what is left, integrated on the REAL axis with no substitution.
        Complex Remainder(double kRho)
        {
            evals.N++;
            Complex w = (Complex)kRho * kRho;
            Complex value = g.KernelAtHeights(kernel, kRho, z, zp);
            if (a.IsMixedForm)
            {
                if (a.ImageCoefficient != Complex.Zero)
                    value -= a.ImageCoefficient
                           * (Math.Exp(-kRho * a.ImageDepth) - Math.Exp(-kRho * (a.ImageDepth + regulator)))
                           / (2.0 * Complex.ImaginaryOne * w);
            }
            else if (a.DirectCoefficient != Complex.Zero || a.ImageCoefficient != Complex.Zero)
            {
                Complex kzm = SpectralGreens.ProperRoot(
                    a.ReferenceWavenumber * a.ReferenceWavenumber - w);
                value -= (a.DirectCoefficient * Complex.Exp(-Complex.ImaginaryOne * kzm * a.DirectDepth)
                        + a.ImageCoefficient  * Complex.Exp(-Complex.ImaginaryOne * kzm * a.ImageDepth))
                       / (2.0 * Complex.ImaginaryOne * kzm);
            }
            return value * Bessel.J0(kRho * rhoM) * kRho / (2.0 * Math.PI);
        }

        // ---- the contour's own geometry: every wavenumber in the stack is a square-root kink and
        // every surface-wave pole is a narrow peak. Both get breakpoints; neither is a singularity
        // of the integrand here, and neither survives a quadrature that is not told about it.
        double kSplit = 2.0 * g.K0;
        var breaks = new List<double>();
        for (int r = 0; r < g.Stack.RegionCount; r++)
        {
            double ki = g.K0 * Math.Sqrt(g.Stack.MaterialOfRegion(r).EpsR * g.Stack.MaterialOfRegion(r).MuR);
            // GEOMETRICALLY CLUSTERED, not one breakpoint, and the difference is three decades. A
            // region's own k_i is where its 1/k_zi turns over, and for a LOSSLESS region that is a
            // genuine inverse-square-root singularity sitting on the contour — integrable, but a
            // Gauss panel that straddles it converges at order ½. Clustering toward it is the same
            // trick the surface-wave poles already get, and it is what lets this path meet the
            // substituted contour of EvaluateLayered rather than sitting 1e-5 away from it.
            for (int j = 0; j <= 16; j++)
            {
                double d = ki * Math.Pow(0.5, j);
                if (ki - d > 0) breaks.Add(ki - d);
                breaks.Add(ki + d);
            }
            breaks.Add(ki);
            kSplit = Math.Max(kSplit, 1.2 * ki);
        }
        foreach (var mode in g.SurfaceWaves.Modes) kSplit = Math.Max(kSplit, 1.5 * mode.KRho.Real);

        // NOT pushed past 1/Σ or 1/t, and that was measured rather than assumed. The obvious move is
        // to hand over only once the surviving exponentials have died, so the tail is monotone — but
        // on a 4 µm layer that is k_ρ ~ 1e6, and the head's uniform pre-partition (8 panels per
        // Bessel oscillation) then wants twenty thousand adaptively-refined panels and the oracle
        // takes minutes per point. The tail's J₀-zero partition plus repeated averaging is built for
        // exactly this shape — an alternating, algebraically decaying series — and an extra e^{−k_ρΣ}
        // only makes it converge sooner. Hand over at the same place EvaluateLayered does.

        foreach (var mode in g.SurfaceWaves.Modes)
        {
            double centre = mode.KRho.Real;
            double width  = Math.Max(Math.Abs(mode.KRho.Imaginary), 1e-9 * centre);
            for (int i = 0; i <= 12; i++)
            {
                double d = width * Math.Pow(2, i);
                if (centre - d > 0)      breaks.Add(centre - d);
                if (centre + d < kSplit) breaks.Add(centre + d);
            }
            breaks.Add(centre);
        }
        breaks.RemoveAll(x => !(x > 0 && x < kSplit));

        double tol = s.RelativeTolerance / (4.0 * Math.PI * rhoM);
        int panels = BasePanels(kSplit * rhoM, s);
        Complex head = IntegrateOver(Remainder, 0.0, kSplit, panels, breaks, tol, s);
        var tail = IntegrateTail(Remainder, rhoM, kSplit, tol, s);

        Complex integrated = head + tail.Value;
        return new SommerfeldResult(direct + image + integrated, direct, image, integrated,
                                    evals.N, tail.Panels, tail.Converged, tail.Residual);
    }

    private sealed class EvalCounter { public long N; }
}
