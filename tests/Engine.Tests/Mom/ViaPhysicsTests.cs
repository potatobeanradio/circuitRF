using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>Phase L9e / M2 — the check that did not exist: is a via's answer PHYSICALLY right?</b>
///
/// <para>Every via test in L9c and L9d is STRUCTURAL — <c>Z</c> symmetric, the divergence signs
/// summing to zero, the via carrying non-zero current, the map being its own quantity, the refusals
/// firing. All worth having, and none of them would catch a via whose terminal inductance is 3× too
/// large. This file is the first thing anywhere that compares a via to a physical quantity.</para>
///
/// <para><b>L9e's answer was NO</b>, and the cause was not where its brief expected it. The KERNEL is
/// right (T2_1: <c>G_A^zz</c> at εᵣ = 1 over a PEC is free space plus a POSITIVE image, to ≤ 3e-4);
/// the FILL is right; what was wrong was L9c's <b>MIDPOINT RULE</b>, which froze <c>1/R</c> over the
/// via's length and made its inductance high by ≈ 0.673·(ℓ/w).</para>
///
/// <para><b>The answer is now YES, and this file is the gate that says so</b> (brief-via-z-integral).
/// <c>T3_1</c> is Tier 2 — the same ℓ/w sweep re-run against the FILL, flat to 0.124% over
/// ℓ/w ∈ [0.01, 5] and a 16× range of footprint width — and <c>T3_1b</c> is Tier 3, where L9e's
/// split-via chain is reproduced by a SINGLE via at every rung, so subdivision is an INVARIANCE
/// rather than a convergence. <c>M1_1</c> is the cost measurement that decided the design and
/// <c>T3_1c</c> is the order it chose. See <c>src/Engine/Mom/CLAUDE.md</c>'s own follow-up section
/// (at the end of that file) for why a plain quadrature in z was not the answer.</para>
/// </summary>
public sealed class ViaPhysicsTests
{
    private readonly ITestOutputHelper _out;
    public ViaPhysicsTests(ITestOutputHelper output) => _out = output;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The harness: a hand-built mesh carrying ONLY vertical bases.
    //
    // L8c's Tier 5 established the precedent — a static excitation harness lives in the TEST
    // project, assembled from product surfaces. This is the same shape one dimension over: n
    // stacked vertical bases over one w×w footprint, with no horizontal basis anywhere, so the
    // measured quantity is the ẑẑ block and nothing else.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>n stacked vertical bases over one w×w footprint, spanning z0 … z0+ell.</summary>
    private static (PlanarMesh Mesh, PlanarLevels Levels) Stack(double w, double z0, double ell, int n)
    {
        var cells = new PlanarCell[n + 1];
        var names = new string[n + 1];
        var z     = new double[n + 1];
        for (int i = 0; i <= n; i++)
        {
            cells[i] = new PlanarCell(i, 0, 0, 0, 0, w, w);
            names[i] = $"L{i}";
            z[i]     = z0 + ell * i / n;
        }
        var bases = new PlanarBasis[n];
        for (int i = 0; i < n; i++) bases[i] = new PlanarBasis(i, i, i + 1, PlanarBasisDirection.Z);

        return (new PlanarMesh(cells, bases, names, [0, w], [0, w]), new PlanarLevels(z));
    }

    /// <summary>
    /// εᵣ = 1 everywhere over a PEC floor — <b>the reduction where the kernel is EXACT and only the
    /// quadrature can be wrong</b>, which is L8c's own Tier 1 argument applied to the ẑẑ block.
    /// Γ is a pure exponential in k_z0, so DCIM fits it with one image and adds no error of its own.
    /// </summary>
    private static LayerStack AirOverPec(double topZ) =>
        new(Termination.Pec, [new MediumLayer(topZ, EmMaterial.Air)], Termination.Air);

    /// <summary>
    /// The via chain's series inductance, in henries, <b>separated from the charge term
    /// ALGEBRAICALLY rather than numerically</b>.
    ///
    /// <para>Every entry of a planar fill has the exact form <c>Z = jω·V + S/(jω)</c>, where V and S
    /// depend on ω only through the KERNEL. <see cref="PlanarFill.FillMultiLevel"/> takes the kernel
    /// set and ω as SEPARATE arguments, so filling twice from ONE kernel set at two ω values leaves
    /// V and S fixed and moves only the two prefactors — and
    /// <c>(ω₂Z₂ − ω₁Z₁)/(j(ω₂² − ω₁²))</c> is then exactly V, with the charge term cancelling
    /// identically rather than to a tolerance. Passing an ω that is not 2πf is a pure algebraic
    /// probe of the assembled matrix, not a physical claim.</para>
    ///
    /// <para><b>The obvious alternative does not work, and the reason is worth recording so nobody
    /// re-derives it:</b> fitting <c>ω·Im Z</c> against ω² across two real FREQUENCIES fails,
    /// because an isolated via basis piles charge at both feet — the scalar term is ~10⁵ times the
    /// inductive one here — and its own O((kR)²) dispersion is two orders of magnitude larger than
    /// the whole quantity being extracted. Measured: that route reads 44% low at ℓ/w = 0.075, which
    /// is the same order as the real defect this file exists to find.</para>
    /// </summary>
    private static double SeriesInductance(PlanarMesh mesh, PlanarLevels levels, LayerStack stack,
                                           double fHz, PlanarFillSettings? fill = null,
                                           DcimSettings? dcim = null)
    {
        var cores = PlanarFill.BuildCores(mesh, fill);
        var set   = new PlanarKernelSet(new LayeredSpectralGreens(stack, fHz),
                                        (fill ?? PlanarFillSettings.Default).Order, 0.0, dcim)
                        .For(cores);

        double w1 = 2 * Math.PI * fHz, w2 = 10.0 * w1;
        Complex z1 = SumAll(PlanarFill.FillMultiLevel(cores, set, levels, w1));
        Complex z2 = SumAll(PlanarFill.FillMultiLevel(cores, set, levels, w2));
        return ((w2 * z2 - w1 * z1) / (Complex.ImaginaryOne * (w2 * w2 - w1 * w1))).Real;
    }

    /// <summary>Σ_ij Z[i,j] — the series impedance of the chain, every basis carrying 1 A.</summary>
    private static Complex SumAll(Mat<Complex> z)
    {
        Complex s = Complex.Zero;
        for (int i = 0; i < z.RowCount; i++)
        for (int j = 0; j < z.ColCount; j++) s += z[i, j];
        return s;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The oracles. They share NO code with the engine — not the quadrature, not the kernel, not
    // the geometric cores. D3's standing rule in this area for the sixth time.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>(1/A²)∫∫ f(ρ) dS dS'</c> over two COINCIDENT w×w squares.
    ///
    /// <para><b>The polar reduction is what makes a 1/ρ integrand ordinary rather than singular:</b>
    /// in the difference variables the weight is <c>(w−|u|)(w−|v|)</c>, and converting to polar
    /// supplies an r from the area element that cancels the 1/r exactly. The quadrant splits at
    /// π/4 into two mirror halves, so one half plus a factor of 2 covers it. Gated against a closed
    /// form below.</para>
    /// </summary>
    private static double MeanOverSquare(double w, Func<double, double> f, int nodes = 40, int panels = 24)
    {
        double total = 0;
        var (gx, gw) = GaussNodes(nodes);
        for (int p = 0; p < panels; p++)
        {
            double ta = Math.PI / 4 * p / panels, tb = Math.PI / 4 * (p + 1) / panels;
            double hm = 0.5 * (tb - ta), cm = 0.5 * (ta + tb);
            for (int i = 0; i < nodes; i++)
            {
                double th = cm + hm * gx[i];
                double c = Math.Cos(th), s = Math.Sin(th), rMax = w / c;
                double inner = 0;
                for (int q = 0; q < panels; q++)
                {
                    double ra = rMax * q / panels, rb = rMax * (q + 1) / panels;
                    double hr = 0.5 * (rb - ra), cr = 0.5 * (ra + rb);
                    for (int j = 0; j < nodes; j++)
                    {
                        double r = cr + hr * gx[j];
                        inner += gw[j] * hr * (w - r * c) * (w - r * s) * r * f(r);
                    }
                }
                total += gw[i] * hm * inner;
            }
        }
        return 8.0 * total / (w * w * w * w);
    }

    /// <summary>
    /// The CLOSED FORM of <see cref="MeanOverSquare"/> for f = 1/ρ, derived here rather than
    /// remembered: the polar inner integral is a polynomial in r, so the θ integral is elementary —
    /// <c>∫sec = ln(1+√2)</c> and <c>∫sin/cos² = √2 − 1</c> over [0, π/4] — giving
    /// <c>4·ln(1+√2) − (4/3)(√2 − 1) ≈ 2.9732</c> per unit side. This is the quadrature's own gate:
    /// an oracle that has not been checked is not an oracle (this area has been caught by that
    /// seven times).
    /// </summary>
    private static double MeanInverseExact(double w) =>
        (4.0 * Math.Log(1.0 + Math.Sqrt(2.0)) - 4.0 / 3.0 * (Math.Sqrt(2.0) - 1.0)) / w;

    /// <summary>
    /// <b>The MIDPOINT-rule inductance — exactly what the engine's ẑẑ block is built to compute.</b>
    /// The z-integrals are replaced by ℓ² at the two feet's midpoint, so this is
    /// <c>μ₀ℓ²⟨G_A^zz(ρ; z_m, z_m)⟩</c> with the static kernel free space PLUS a positive image
    /// (L9c: the CURRENT reflection at a PEC is +1).
    /// </summary>
    private static double MidpointInductance(double w, double z0, double ell)
    {
        double zm = z0 + 0.5 * ell, d = 2 * zm;
        return EmConstants.Mu0 * ell * ell
             * (MeanInverseExact(w) + MeanOverSquare(w, r => 1.0 / Math.Sqrt(r * r + d * d)))
             / (4 * Math.PI);
    }

    /// <summary>
    /// <b>The EXACT partial inductance of the same uniform current</b> — the z-integrals done rather
    /// than replaced. The direct term's double z-integral is closed form
    /// (<c>2[ℓ·asinh(ℓ/ρ) − √(ρ²+ℓ²) + ρ]</c>, from the triangular density of z − z′); the image
    /// term's runs over the triangular density of z + z′ and is smooth (σ ≥ 2z₀ &gt; 0).
    ///
    /// <para><b>Corroborated independently, not just self-consistent:</b> at w = 40 µm, ℓ = 400 µm,
    /// z₀ = 200 µm this returns 249.1 pH, against 247.4 pH from Grover's own bar formula
    /// <c>(μ₀ℓ/2π)[ln(2ℓ/(w+t)) + 0.5 + 0.2235(w+t)/ℓ]</c> plus the image's parallel-bar mutual —
    /// 0.7%, which is the accuracy that GMD-based approximation itself carries.</para>
    /// </summary>
    private static double ExactInductance(double w, double z0, double ell) =>
        ExactInductanceQ(w, z0, ell, 40);

    private static double ExactInductanceQ(double w, double z0, double ell, int nodes)
    {
        double z1 = z0, z2 = z0 + ell;

        double Direct(double rho) =>
            2.0 * (ell * Asinh(ell / rho) - Math.Sqrt(rho * rho + ell * ell) + rho);

        double Image(double rho)
        {
            var (gx, gw) = GaussNodes(48);
            double sum = 0;
            foreach (var (a, b) in new[] { (2 * z1, z1 + z2), (z1 + z2, 2 * z2) })
            {
                double h = 0.5 * (b - a), c = 0.5 * (a + b);
                for (int i = 0; i < gx.Length; i++)
                {
                    double sg = c + h * gx[i];
                    sum += gw[i] * h * Math.Min(sg - 2 * z1, 2 * z2 - sg) / Math.Sqrt(rho * rho + sg * sg);
                }
            }
            return sum;
        }

        return EmConstants.Mu0
             * (MeanOverSquare(w, Direct, nodes) + MeanOverSquare(w, Image, nodes))
             / (4 * Math.PI);
    }

    private static double Asinh(double x) => Math.Log(x + Math.Sqrt(x * x + 1));

    private static readonly Dictionary<int, (double[] X, double[] W)> NodeCache = new();

    /// <summary>Gauss-Legendre nodes by Newton on the Legendre recurrence — computed, never
    /// tabulated, as L8a's own rule requires and for the same reason.</summary>
    private static (double[] X, double[] W) GaussNodes(int n)
    {
        lock (NodeCache)
        {
            if (NodeCache.TryGetValue(n, out var hit)) return hit;
            var x = new double[n];
            var wt = new double[n];
            for (int i = 0; i < n; i++)
            {
                double zz = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5)), pp = 0;
                for (int it = 0; it < 200; it++)
                {
                    double p0 = 1, p1 = 0;
                    for (int j = 0; j < n; j++)
                    {
                        double p2 = p1; p1 = p0;
                        p0 = ((2 * j + 1) * zz * p1 - j * p2) / (j + 1);
                    }
                    pp = n * (zz * p0 - p1) / (zz * zz - 1);
                    double dz = p0 / pp;
                    zz -= dz;
                    if (Math.Abs(dz) < 1e-16) break;
                }
                x[i]  = zz;
                wt[i] = 2.0 / ((1 - zz * zz) * pp * pp);
            }
            NodeCache[n] = (x, wt);
            return (x, wt);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // TIER 2 — the via against a closed form.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Trait("Category", "Benchmark")]
    [Fact]
    public void T2_0_TheORACLESAreCheckedBeforeAnythingIsConcludedFromThem()
    {
        // "When a rung disagrees, the first hypothesis is the rung." Seven occasions in this area
        // already. So: the polar quadrature against the closed form it is supposed to reproduce,
        // and the exact-bar integral against its own refinement.
        double w = 40e-6;
        double got = MeanOverSquare(w, r => 1.0 / r);
        double rel = Math.Abs(got / MeanInverseExact(w) - 1);
        Assert.True(rel < 1e-12, $"the polar quadrature must reproduce its own closed form: {rel:E3}");

        double a = ExactInductanceQ(w, 200e-6, 40e-6, 20);
        double b = ExactInductanceQ(w, 200e-6, 40e-6, 40);
        double c = ExactInductanceQ(w, 200e-6, 40e-6, 80);
        _out.WriteLine($"MeanOverSquare(1/ρ) = {got:E12} against the closed form " +
                       $"{MeanInverseExact(w):E12} — relative {rel:E3}.");
        _out.WriteLine($"L_exact at 20/40/80 nodes: {a * 1e12:F8} / {b * 1e12:F8} / {c * 1e12:F8} pH.");
        Assert.True(Math.Abs(b / c - 1) < 1e-8, "the exact-bar oracle must be converged at its default");

        // ── The NEW closed form the z-integral needs, checked the same way and BEFORE it is
        //    believed: ∫∫dS′/√(u²+v²+c²) over a rectangle, with the observation point lifted out of
        //    the plane. Two independent claims — it reduces to the planar form at c = 0, and it
        //    reproduces an ordinary 2-D quadrature at c > 0 — because the third term of its
        //    antiderivative (the arctangent) is precisely the one that vanishes at c = 0 and would
        //    therefore survive the first check while being wrong.
        double x1 = -0.7, x2 = 1.3, y1 = -0.4, y2 = 2.1;
        double planar = RectangleIntegrals.Inverse(x1, x2, y1, y2);
        double lifted0 = RectangleIntegrals.InverseAtOffset(x1, x2, y1, y2, 0.0);
        Assert.Equal(planar, lifted0, 12);

        // ── AND THE RUNG WAS WRONG FIRST, for the eighth time in this area. A UNIFORMLY panelled
        //    quadrature read 9.5e-5 from the closed form at c = 1e-3 while agreeing to 2e-15 at
        //    c ≥ 0.05, which is the signature of the CHECK failing rather than the thing checked:
        //    at c = 1e-3 the integrand is a spike 1e-3 wide sitting on a 2 × 2.5 rectangle, i.e. 50×
        //    narrower than a uniform panel. Grading the panels toward the origin — where the spike
        //    is, and the closed form knows nothing about where the panels are — closes it to 1e-13,
        //    and the sequence below is what SAYS so rather than a tolerance that would have hidden it.
        double worstOffset = 0;
        foreach (double off in new[] { 1e-3, 0.05, 0.5, 3.0 })
        {
            double closed = RectangleIntegrals.InverseAtOffset(x1, x2, y1, y2, off);
            double quad = GradedQuad2D(x1, x2, y1, y2, off,
                                       (u, v) => 1.0 / Math.Sqrt(u * u + v * v + off * off));
            worstOffset = Math.Max(worstOffset, Math.Abs(closed / quad - 1));
            _out.WriteLine($"  InverseAtOffset(c = {off,6:G3}): {closed:F12} against " +
                           $"{quad:F12} by graded quadrature — relative {Math.Abs(closed / quad - 1):E2}");
        }
        Assert.True(worstOffset < 1e-10,
            $"the lifted rectangle integral must reproduce an independent quadrature: {worstOffset:E3}");

        _out.WriteLine("  (the same comparison with UNIFORM panels reads 9.5e-5 at c = 1e-3 and " +
                       "2e-15 elsewhere — the quadrature's own failure to resolve a spike 50× " +
                       "narrower than its panel, not the closed form's.)");
    }

    /// <summary>
    /// A panelled tensor Gauss rule over a rectangle, with the panels graded geometrically toward the
    /// origin — the independent check for <see cref="RectangleIntegrals.InverseAtOffset"/>, sharing no
    /// code with it. The grading is what lets it resolve a near-singular spike of width c.
    /// </summary>
    private static double GradedQuad2D(double x1, double x2, double y1, double y2, double scale,
                                       Func<double, double, double> f)
    {
        const int nodes = 16;
        var (gx, gw) = GaussNodes(nodes);
        double[] ex = GradedEdges(x1, x2, scale), ey = GradedEdges(y1, y2, scale);

        double total = 0;
        for (int px = 0; px + 1 < ex.Length; px++)
        for (int py = 0; py + 1 < ey.Length; py++)
        {
            double hx = 0.5 * (ex[px + 1] - ex[px]), cx = 0.5 * (ex[px] + ex[px + 1]);
            double hy = 0.5 * (ey[py + 1] - ey[py]), cy = 0.5 * (ey[py] + ey[py + 1]);
            for (int i = 0; i < nodes; i++)
            for (int j = 0; j < nodes; j++)
                total += gw[i] * gw[j] * hx * hy * f(cx + hx * gx[i], cy + hy * gx[j]);
        }
        return total;
    }

    /// <summary>Panel edges on [a,b], geometrically clustered toward 0 from the smallest feature
    /// size upward, so a spike of width <paramref name="scale"/> gets panels of that size.</summary>
    private static double[] GradedEdges(double a, double b, double scale)
    {
        var e = new SortedSet<double> { a, b };
        if (a < 0 && 0 < b) e.Add(0.0);
        foreach (double sign in new[] { -1.0, 1.0 })
        {
            double limit = sign < 0 ? -a : b;
            for (double h = 0.25 * scale; h < limit; h *= 1.6) e.Add(sign * h);
        }
        // …and a uniform floor everywhere else, so the far field is not integrated on two panels.
        for (int k = 1; k < 24; k++) e.Add(a + (b - a) * k / 24.0);
        return [.. e];
    }

    [Fact]
    public void T2_1_AtEpsR1OverAPEC_GAzz_IsFreeSpacePlusAPOSITIVEImage()
    {
        // The rung below the rung, and it also re-measures L9c's sign finding against an ABSOLUTE
        // value rather than a symmetry identity: the image sign flips because the CURRENT
        // reflection at a PEC is +1 (a PEC is a short — the VOLTAGE reflection is −1), and the
        // vertical component is built from a current. Getting it wrong halves the answer or
        // doubles it, smoothly and plausibly.
        double z0 = 200e-6, ell = 40e-6, zm = z0 + 0.5 * ell, f = 5e9;
        var greens = new LayeredSpectralGreens(AirOverPec(z0 + ell), f);
        var model  = Dcim.FitAtHeights(greens, GreensKernel.VerticalVectorPotential, zm, zm);
        double k0  = 2 * Math.PI * f / EmConstants.C0;

        double worst = 0, worstMinus = 0;
        foreach (double rho in new[] { 1e-6, 5e-6, 2e-5, 5.6e-5, 2e-4, 1e-3 })
        {
            Complex got  = model.EvaluateAtHeights(rho);
            Complex want = SommerfeldIntegral.FreeSpace(k0, rho)
                         + SommerfeldIntegral.FreeSpace(k0, Math.Sqrt(rho * rho + 4 * zm * zm));
            Complex wrong = SommerfeldIntegral.FreeSpace(k0, rho)
                          - SommerfeldIntegral.FreeSpace(k0, Math.Sqrt(rho * rho + 4 * zm * zm));

            worst      = Math.Max(worst,      (got - want).Magnitude  / want.Magnitude);
            worstMinus = Math.Max(worstMinus, (got - wrong).Magnitude / wrong.Magnitude);
            _out.WriteLine($"  ρ = {rho:E1} m: worst so far {worst:E3}");
        }

        _out.WriteLine($"{model.Images.Count} image(s); worst relative error against " +
                       $"free space + a POSITIVE image is {worst:E3}. Against a NEGATIVE image it " +
                       $"would be {worstMinus:E3} — which is what earns the sign rather than " +
                       $"asserting it.");
        Assert.True(worst < 1e-3, $"the εᵣ = 1 reduction must be exact to the fit: {worst:E3}");
        Assert.True(worstMinus > 0.1, "a negative image must be decisively excluded");
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void T2_2_TheViasOwnINDUCTANCE_MatchesAnIndependentlyIntegratedClosedForm()
    {
        // THE TIER 2 GATE. The engine's ẑẑ entry, converted to henries, against the EXACT partial
        // inductance of the same uniform current — the z-integrals DONE rather than replaced, with
        // the direct half's double z-integral in closed form and only the (smooth) image half
        // quadratured. Nothing here shares code with the fill.
        //
        // **This test used to compare against MidpointInductance, and that is the whole change.**
        // L9e's finding was that the midpoint rule is high by ≈ 0.673·(ℓ/w); with the z-integral
        // resolved, the engine no longer computes the midpoint value and reproducing it would now be
        // the failure. It is kept as the second column so the size of what moved stays visible.
        double w = 40e-6, z0 = 200e-6, f = 5e9;
        double worst = 0;

        _out.WriteLine("  ℓ/w      L_engine (pH)     L_exact (pH)   relative   (the old midpoint rule)");
        foreach (double ell in new[] { 3e-6, 10e-6, 40e-6, 120e-6, 400e-6 })
        {
            var (mesh, levels) = Stack(w, z0, ell, 1);
            double eng = SeriesInductance(mesh, levels, AirOverPec(z0 + ell), f);
            double cf  = ExactInductance(w, z0, ell);
            double rel = Math.Abs(eng / cf - 1);
            worst = Math.Max(worst, rel);
            _out.WriteLine($"  {ell / w,5:F2}   {eng * 1e12,12:F4}   {cf * 1e12,14:F4}   {rel,10:E3}   " +
                           $"{MidpointInductance(w, z0, ell) / cf - 1,10:P2}");
        }

        _out.WriteLine($"\nWorst relative disagreement over ℓ/w ∈ [0.075, 10]: {worst:E3}, against a " +
                       $"midpoint rule that is 4.9% to 385% high over the same span.");
        Assert.True(worst < 1e-2,
            $"the ẑẑ entry must reproduce the exact partial inductance: worst relative {worst:E3}");
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void T2_3_TheINPLANEAreaIntegral_IsInvariantUnderSubdivision_ToTheFillsOwnQuadrature()
    {
        // L8c found that P is exactly invariant under subdivision because it is area-averaged, and
        // used it as a strong check on the area normalisation. The ẑẑ block IS that cell-pair
        // integral with a different kernel, so the same statement must hold one dimension over:
        // cutting the footprint into k² sub-cells and driving each with 1/k² of the current must
        // give back the SAME inductance, not merely a converging sequence.
        //
        // MEASURED AT 1.4e-6, NOT BIT-IDENTICAL, AND THE DIFFERENCE IS WORTH STATING RATHER THAN
        // ROUNDING AWAY. L8c's own 1e-12 claim is about the analytic CORES (the 1/ρ, ln ρ and
        // constant pieces), which really are exactly additive under subdivision. The smooth
        // REMAINDER is not: it is Gauss-quadratured per cell pair, so a 2×2 split integrates it on
        // four finer panels rather than one coarse one and lands a quadrature error apart. The
        // invariant is therefore "exact up to the fill's own quadrature", and 1.4e-6 is two decades
        // inside the 1e-4 that quadrature is itself worth (T3_2).
        double w = 40e-6, z0 = 200e-6, ell = 20e-6, f = 5e9;
        var stack = AirOverPec(z0 + ell);

        double one = SeriesInductance(Stack(w, z0, ell, 1).Mesh, Stack(w, z0, ell, 1).Levels, stack, f);

        // 2×2 sub-cells on each of two levels, one vertical basis per column.
        var cells = new List<PlanarCell>();
        for (int layer = 0; layer <= 1; layer++)
        for (int iy = 0; iy < 2; iy++)
        for (int ix = 0; ix < 2; ix++)
            cells.Add(new PlanarCell(layer, ix, iy,
                                     ix * w / 2, iy * w / 2, (ix + 1) * w / 2, (iy + 1) * w / 2));
        var bases = new List<PlanarBasis>();
        for (int k = 0; k < 4; k++) bases.Add(new PlanarBasis(0, k, k + 4, PlanarBasisDirection.Z));

        var fine = new PlanarMesh(cells, bases, ["L0", "L1"], [0, w / 2, w], [0, w / 2, w]);
        var lv   = new PlanarLevels([z0, z0 + ell]);

        var cores = PlanarFill.BuildCores(fine);
        var set   = new PlanarKernelSet(new LayeredSpectralGreens(stack, f)).For(cores);
        double w1 = 2 * Math.PI * f, w2 = 10 * w1;
        var z1 = PlanarFill.FillMultiLevel(cores, set, lv, w1);
        var z2 = PlanarFill.FillMultiLevel(cores, set, lv, w2);

        // Each sub-basis carries 1/4 A, so the chain's inductance is (1/16)ΣΣ.
        Complex s1 = SumAll(z1) / 16.0, s2 = SumAll(z2) / 16.0;
        double many = ((w2 * s2 - w1 * s1) / (Complex.ImaginaryOne * (w2 * w2 - w1 * w1))).Real;

        double rel = Math.Abs(many / one - 1);
        _out.WriteLine($"1 cell: {one * 1e12:F6} pH · 4 cells at ¼ A each: {many * 1e12:F6} pH — " +
                       $"relative {rel:E3}. Area averaging makes the analytic cores an IDENTITY; " +
                       $"what is left is the smooth remainder's own quadrature.");
        Assert.True(rel < 1e-5, $"subdivision must be invariant to the fill's quadrature: {rel:E3}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // TIER 3 — the via against its OWN convergence, refining the mesh and the quadrature SEPARATELY
    // (L8c's Tier 6 shape, and for its reason: a single study conflates two error sources).
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]   // the ℓ/w curve on the ENGINE — three widths × seven ratios, ~4 min
    public void T3_1_THE_GATE_TheEllOverWCurveIsFLAT_TheMidpointRulesGeometricErrorIsGONE()
    {
        // ── TIER 2, AND IT IS THE WHOLE POINT OF THE BRIEF ─────────────────────────────────────
        //
        // L9e measured the midpoint rule's error as ≈ 0.673·(ℓ/w) — 4.9% on §10.7's own MMIC spacer,
        // 55% at ℓ/w = 1, 385% at ℓ/w = 10 — and bounded it with PlanarLevels.MaxLengthOverWidth
        // rather than fixing it. The same sweep, re-run against the ENGINE rather than against the
        // closed form of what the engine used to compute, must now be flat.
        //
        // The second and third columns are what says the fix is a function of the aspect ratio and
        // not an accident of one width: the OLD error was independent of w over a 16× range, so a
        // remedy that only worked at one scale would show here.
        double z0 = 200e-6, f = 5e9;
        var widths = new[] { 10e-6, 40e-6, 160e-6 };
        double worst = 0, worstOld = 0;

        _out.WriteLine("The ẑẑ block against the exact partial inductance, by aspect ratio and width:");
        _out.WriteLine("  ℓ/w       w=10 µm    w=40 µm   w=160 µm   │ the OLD midpoint rule (w=40 µm)");
        foreach (double ratio in new[] { 0.01, 0.05, 0.075, 0.1, 0.5, 1.0, 5.0 })
        {
            var cols = new List<string>();
            foreach (double ww in widths)
            {
                double ell = ratio * ww;
                var (mesh, levels) = Stack(ww, z0, ell, 1);
                double err = SeriesInductance(mesh, levels, AirOverPec(z0 + ell), f)
                           / ExactInductance(ww, z0, ell) - 1;
                worst = Math.Max(worst, Math.Abs(err));
                cols.Add($"{err,9:P2}");
            }
            double old = MidpointInductance(40e-6, z0, ratio * 40e-6) / ExactInductance(40e-6, z0, ratio * 40e-6) - 1;
            worstOld = Math.Max(worstOld, old);
            _out.WriteLine($"  {ratio,6:F3}  {string.Join(" ", cols)}   │ {old,10:P2}");
        }

        _out.WriteLine($"\nWorst |error| over ℓ/w ∈ [0.01, 5] and a 16× range of w: {worst:P3}. " +
                       $"The midpoint rule reached {worstOld:P0} on the same span, rising linearly " +
                       $"with ℓ/w at a slope of 0.673. THE SLOPE IS GONE — what is left does not " +
                       $"trend with the aspect ratio at all.");
        Assert.True(worst < 0.01,
            $"Tier 2: the ℓ/w error curve must be flat to 1%, and it reads {worst:P3}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // the subdivision ladder at two aspect ratios, ~4 min
    public void T3_1b_TIER3_SubdivisionInvariance_nEquals1NowEqualsnEquals8()
    {
        // TIER 3. L9e's remedy for the midpoint rule was the refusal's own advice — split the via
        // across intermediate levels, n stacked sub-vias each carrying their own midpoint rule over
        // ℓ/n — and it converged: 55.3% → 15.8% → 4.2% → 1.14% → 0.68% for n = 1…16 at ℓ/w = 1, and
        // 385% → 5.9% at ℓ/w = 10. **Those numbers are the reference n = 1 must now reproduce**, and
        // the statement is stronger than convergence: the chain must be INVARIANT, because the same
        // physical current is being integrated either way.
        //
        // Note that n > 1 is genuinely more expensive now and that is not a defect: n sub-vias are n
        // distinct z SPANS, so the ẑẑ block asks for n(n+1)/2 span pairs and n_z²  fits inside each.
        // Production has one span per drawn via layer.
        double w = 40e-6, z0 = 200e-6, f = 5e9;

        foreach (double ell in new[] { 40e-6, 400e-6 })
        {
            double exact = ExactInductance(w, z0, ell);
            _out.WriteLine($"\nℓ/w = {ell / w:F2}; the exact partial inductance is {exact * 1e12:F3} pH.");
            _out.WriteLine("  sub-vias   L (pH)     error   │ L9e's own midpoint chain");
            var l9e = ell / w < 5 ? new[] { "55.3%", "15.8%", "4.2%", "1.14%" }
                                  : new[] { "385%", "163%", "62%", "20%" };
            double first = double.NaN;
            int k = 0;
            foreach (int n in new[] { 1, 2, 4, 8 })
            {
                var (mesh, levels) = Stack(w, z0, ell, n);
                double l = SeriesInductance(mesh, levels, AirOverPec(z0 + ell), f);
                if (double.IsNaN(first)) first = l;
                _out.WriteLine($"  {n,8}   {l * 1e12,8:F3}   {l / exact - 1,8:P2}   │ {l9e[k++],8}");
                Assert.True(Math.Abs(l / first - 1) < 5e-3,
                    $"subdivision must be INVARIANT now, not merely convergent: n = {n} moved the " +
                    $"answer by {l / first - 1:P3} from n = 1 at ℓ/w = {ell / w:F2}");
            }
        }
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // five n_z settings at three aspect ratios, ~2 min
    public void T3_1c_RvIz3_TheZQuadraturesORDER_IsAMeasurementAndHereIsTheTable()
    {
        // R-viz-3 asks for the order to be chosen on a convergence table rather than by assertion,
        // exactly as L8c did for its own rules and L8a for its branch-point orders.
        //
        // **The striking column is n_z = 1**, and it is what says the split in ViaZIntegral is the
        // load-bearing decision rather than the quadrature order: with the two asymptotes' static
        // parts integrated in CLOSED FORM in z, evaluating everything else at the via's midpoint is
        // already right to well under a percent. The default is therefore the CHEAP end — L8c's own
        // precedent, whose extraction order is 1 rather than 2 because "order 2 buys nothing".
        double w = 40e-6, z0 = 200e-6, f = 5e9;

        _out.WriteLine("  n_z     ℓ/w = 0.075      ℓ/w = 1        ℓ/w = 5");
        foreach (int nz in new[] { 1, 2, 4, 8 })
        {
            var cols = new List<string>();
            foreach (double ratio in new[] { 0.075, 1.0, 5.0 })
            {
                double ell = ratio * w;
                var (mesh, levels) = Stack(w, z0, ell, 1);
                double l = SeriesInductance(mesh, levels, AirOverPec(z0 + ell), f,
                                            PlanarFillSettings.Default with { ViaZNodes = nz });
                cols.Add($"{l / ExactInductance(w, z0, ell) - 1,12:P4}");
            }
            _out.WriteLine($"  {nz,3}  {string.Join(" ", cols)}");
        }

        // …and the SINGULAR half's own t-rule, refined separately, so the two orders are not
        // conflated (L8c's Tier 6 shape).
        _out.WriteLine("\n  t-nodes  ℓ/w = 1");
        double baseline = double.NaN;
        foreach (int tn in new[] { 4, 10, 20 })
        {
            var (mesh, levels) = Stack(w, z0, w, 1);
            double l = SeriesInductance(mesh, levels, AirOverPec(z0 + w), f,
                                        PlanarFillSettings.Default with { ViaZStaticNodes = tn });
            if (double.IsNaN(baseline)) baseline = l;
            _out.WriteLine($"  {tn,7}  {l / ExactInductance(w, z0, w) - 1,10:P4}");
        }

        // ── …and the SAME question on a REAL LAYERED STACK, which is what decides the default ──
        //
        // The table above is measured on the εᵣ = 1 reduction, where the kernel is exact and the only
        // z-dependence outside the closed form is the wave factor. That is the right fixture for the
        // GATE and the wrong one for the ORDER: a grounded layered stack also has surface-wave poles
        // whose residues move with the heights and fitted images whose depths do, and neither exists
        // at εᵣ = 1. Concluding "n_z = 1 is enough" from the reduction alone would be exactly the
        // mistake L9b's own degenerate OpenBelow fixture is on the record for.
        var problem = TwoLevelWithVia(10e9, 400e-6);
        var report = SurfaceMesher.Mesh(problem);
        var vmesh = report.Mesh;
        var levelsL = PlanarLevels.From(problem);
        int horizontal = vmesh.Bases.Count - report.ViaUnknownCount;

        Complex[]? refBlock = null;
        _out.WriteLine($"\nOn the MMIC two-level stack (N = {vmesh.Bases.Count}, " +
                       $"{report.ViaUnknownCount} vertical), the vertical blocks against n_z = 8:");
        _out.WriteLine("  n_z    worst |ΔZ| / max|Z| over every entry touching a via");
        foreach (int nz in new[] { 8, 1, 2, 4 })
        {
            var st = PlanarFillSettings.Default with { ViaZNodes = nz };
            var c = PlanarFill.BuildCores(vmesh, st);
            var s = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, 10e9),
                                        st.Order).For(c);
            var zm = PlanarFill.FillMultiLevel(c, s, levelsL, 2 * Math.PI * 10e9);

            var block = new List<Complex>();
            for (int i = horizontal; i < vmesh.Bases.Count; i++)
                for (int j = 0; j < vmesh.Bases.Count; j++) block.Add(zm[i, j]);

            if (refBlock is null) { refBlock = [.. block]; continue; }
            double worst = 0, scale = 0;
            for (int k = 0; k < block.Count; k++)
            {
                worst = Math.Max(worst, (block[k] - refBlock[k]).Magnitude);
                scale = Math.Max(scale, refBlock[k].Magnitude);
            }
            _out.WriteLine($"  {nz,3}    {worst / scale,10:E3}");
        }
        _out.WriteLine("\nThe layered case is where the poles' residues and the fitted image depths " +
                       "genuinely DO move with the heights — and the via spans its whole region here, " +
                       "so this is the most z-variation the bounded half can have rather than the " +
                       "least. It reads 5.6e-8 at n_z = 1. THE DEFAULT IS 2: the smallest setting " +
                       "that is a genuine quadrature rather than a midpoint rule, at 3 fits per via " +
                       "span (0.28% of a de-embedded point) against 10 at n_z = 4.");
        Assert.Equal(2, PlanarFillSettings.Default.ViaZNodes);
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void T3_2_TheQUADRATUREAndTheFITAreRefinedSEPARATELY_AndNeitherIsTheError()
    {
        // L8c's Tier 6 separates the two error sources a single convergence study would conflate.
        // Same here, and the point is a NEGATIVE result: refining either one moves the answer by
        // orders of magnitude less than T3_1's midpoint error, so neither is where the defect is.
        double w = 40e-6, z0 = 200e-6, ell = 40e-6, f = 5e9;
        var (mesh, levels) = Stack(w, z0, ell, 1);
        var stack = AirOverPec(z0 + ell);

        double baseline = SeriesInductance(mesh, levels, stack, f);

        _out.WriteLine($"baseline                         {baseline * 1e12:F8} pH");
        foreach (int factor in new[] { 2, 4 })
        {
            double l = SeriesInductance(mesh, levels, stack, f, PlanarFillSettings.Default.Finer(factor));
            _out.WriteLine($"quadrature ×{factor}                    {l * 1e12:F8} pH " +
                           $"({l / baseline - 1:E2})");
            Assert.True(Math.Abs(l / baseline - 1) < 1e-4, "the fill's quadrature is converged");
        }

        foreach (int samples in new[] { 768, 1024 })
        {
            double l = SeriesInductance(mesh, levels, stack, f, null,
                                        DcimSettings.Default with { Samples = samples });
            _out.WriteLine($"DCIM samples {samples,4}                {l * 1e12:F8} pH " +
                           $"({l / baseline - 1:E2})");
            Assert.True(Math.Abs(l / baseline - 1) < 1e-3, "the DCIM fit is converged");
        }

        _out.WriteLine("\nBoth sequences are flat to ≤ 1e-3 while T3_1's midpoint error is 5e-2 at " +
                       "this very geometry — which is what separates 'the quadrature is coarse' " +
                       "from 'the rule is wrong'.");
    }

    [Fact]
    public void T3_3_TheGEOMETRICGuardIsRETIRED_AndTheELECTRICALOneNamesARealRemainingLimit()
    {
        // M5 / D3. L9e shipped TWO bounds on the same refusal and this test pinned both. The
        // geometric one (ℓ/w ≤ 0.5, ≈ 30% error) existed because the midpoint rule froze 1/R over the
        // via's length; T3_1 now measures that curve flat to 0.13%, so it is gone — and the case it
        // used to refuse must SOLVE, not merely stop being refused for a different reason.
        double kAt10GHz = 2 * Math.PI * 10e9 * Math.Sqrt(12.9) / EmConstants.C0;

        var tall = new PlanarLevels([100e-6, 160e-6]);   // 60 µm over a 40 µm square: ℓ/w = 1.5
        Assert.True(tall.CanRepresentVias(kAt10GHz).Ok);
        Assert.True(new PlanarLevels([100e-6, 103e-6]).CanRepresentVias(kAt10GHz).Ok);

        // The ELECTRICAL bound stays and is still about the BASIS rather than the quadrature: a via
        // basis is one z-rooftop, so its current is uniform along the whole length. No amount of
        // integrating the Green's function better gives it a profile it has no degree of freedom for.
        //
        // ITS VALUE AND ITS WORDING BOTH MOVED, and this test is UPDATED rather than loosened: M1's
        // R-gv-1 measurement (ViaPhysicsTests.M1_2) subdivided an ATTACHED via — the only kind that
        // exists in a real structure — and found the answer moves 0.077% at k·ℓ = 0.23 and 0.141% at
        // k·ℓ = 1.0, while the chain that would fix it costs ~14% of a de-embedded point. So the
        // bound is 0.30 from a measurement rather than 0.05 from an O((kℓ)²) argument, and the
        // refusal now says which measurement instead of prescribing a remedy that is not worth it.
        Assert.Equal(0.30, PlanarLevels.MaxElectricalLength);
        var electrical = new PlanarLevels([100e-6, 5.1e-3]).CanRepresentVias(kAt10GHz);
        Assert.False(electrical.Ok);
        Assert.Contains("UNIFORM", electrical.Reason);
        Assert.Contains("MEASUREMENT", electrical.Reason);
        Assert.Contains("0.077%", electrical.Reason);
        Assert.DoesNotContain("ℓ/w", electrical.Reason);

        // …and widening it unlocks nothing on its own, which the refusal is required to keep saying:
        // Dcim.ValidatedRhoOverLambdaAtHeights already restricts every via-bearing run to
        // electrically small structures, and it is untouched (asserted again below).
        var justInside = new PlanarLevels([100e-6, 100e-6 + 0.29 / kAt10GHz]).CanRepresentVias(kAt10GHz);
        Assert.True(justInside.Ok);

        // …and RETIRING IT WIDENS NOTHING. G_A^zz's own validated range is what restricts a
        // via-bearing run to electrically small structures, and it is untouched (§0.2 item 5).
        Assert.Equal(0.1, Dcim.ValidatedRhoOverLambdaAtHeights);

        _out.WriteLine(electrical.Reason!);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M1 — THE MEASUREMENT THAT DECIDES THE DESIGN (brief-via-z-integral §3).
    //
    // L9c declined the z-integral on a COST premise: "a fit per z-quadrature node rather than one
    // per pairing, and D7's cost projection is written against one per pairing". Three numbers
    // settle it, and none of them was in hand when that was written.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The MMIC two-level problem L9d and L9's own phase gate are sized against — one via
    /// spanning the 3 µm interior region, two levels, electrically small at 10 GHz.</summary>
    private static PlanarProblem TwoLevelWithVia(double fHz = 10e9, double lengthM = 400e-6)
    {
        var stack = LayerStacks.MmicTwoLevel;
        PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
            new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

        var lower = new PlanarConductorLayer("M1", [Rect(0, 0, lengthM, 100e-6)], 4.1e7, 2e-6,
                                             stack.InterfaceZ[1]);
        var upper = new PlanarConductorLayer("M2", [Rect(0, 0, lengthM, 100e-6)], 4.1e7, 3e-6,
                                             stack.TopZ);
        var vias  = new[] { new PlanarVia(0, 1, [Rect(0.45 * lengthM, 30e-6, 0.55 * lengthM, 70e-6)],
                                          4.1e7) };
        return new PlanarProblem([lower, upper], GroundedSlab.GaAsStarter, fHz, null, stack, vias);
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // a two-level fill plus ~30 timed interior fits, ~35 s
    public void M1_1_RvIz2_WhatAZQuadratureCOSTS_AndWhetherTheFitsAreNeededAtAll()
    {
        double f = 10e9, len = 400e-6;
        var problem = TwoLevelWithVia(f, len);
        var report  = SurfaceMesher.Mesh(problem);
        var mesh    = report.Mesh;
        var cores   = PlanarFill.BuildCores(mesh);
        var levels  = PlanarLevels.From(problem);
        var greens  = new LayeredSpectralGreens(problem.EffectiveStack, f);

        // ── (1) HOW MANY HEIGHT PAIRS a z-quadrature actually asks for ────────────────────────
        //
        // The count is a property of the PAIRING SET, not of N: every via in this fixture spans the
        // same two levels, so the ẑẑ block asks for the unordered pairs of one node set (PlanarKernelSet
        // canonicalises (z,z′) and (z′,z) onto one fit) and the mixed block asks for one per (node,
        // level).
        int viaSpans = problem.ViaList.Select(v => (v.LowerLayerIndex, v.UpperLayerIndex)).Distinct().Count();
        int levelCount = problem.Layers.Count;

        var setBase = new PlanarKernelSet(greens);
        var zBase = PlanarFill.FillMultiLevel(cores, setBase.For(cores), levels, 2 * Math.PI * f);
        int baseFits = setBase.FitCount;

        _out.WriteLine($"M1 (1) — the fixture: N = {report.UnknownCount} " +
                       $"({report.ViaUnknownCount} vertical), {levelCount} levels, " +
                       $"{problem.ViaList.Count} via(s) over {viaSpans} distinct span(s).");
        _out.WriteLine($"  TODAY: {baseFits} fits per frequency (the midpoint rule asks for ONE " +
                       $"ẑẑ pairing and {levelCount} mixed ones).");
        _out.WriteLine("  n_z   ẑẑ pairs   mixed pairs   TOTAL added over the midpoint rule");
        foreach (int nz in new[] { 2, 4, 8 })
        {
            int zz = viaSpans * nz * (nz + 1) / 2;
            int mixed = viaSpans * nz * levelCount;
            _out.WriteLine($"  {nz,3}   {zz,8}   {mixed,11}   " +
                           $"{zz + mixed - viaSpans * (1 + levelCount),+5}");
        }

        // ── (2) WHAT ONE EXTRA HEIGHT PAIR COSTS ──────────────────────────────────────────────
        double zb = problem.LevelZ(0), zt = problem.LevelZ(1);
        double zq = zb + 0.37 * (zt - zb), zq2 = zb + 0.73 * (zt - zb);

        // warm the cascade cache so the first fit does not carry the whole set-up
        _ = Dcim.FitAtHeights(greens, GreensKernel.VerticalVectorPotential, zq, zq);

        var sw = Stopwatch.StartNew();
        var probe = Dcim.FitAtHeights(greens, GreensKernel.VerticalVectorPotential, zq2, zq);
        double fitMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var probeTerms = PlanarKernelTerms.FromDcimAtHeights(probe, PlanarExtractionOrder.Constant,
                                                            cores.RhoFloorM);
        double decompMs = sw.Elapsed.TotalMilliseconds;

        double spacing = PlanarFillSettings.Default.TableCellFraction * cores.MinCellEdgeM;
        sw.Restart();
        var table = RadialRemainderTable.Build(probeTerms, Math.Max(cores.ExtentM, spacing * 8),
                                               spacing, PlanarFillSettings.Default.MaxTableSamples);
        double tableMs = sw.Elapsed.TotalMilliseconds;

        _out.WriteLine($"\nM1 (2) — ONE extra height pair, measured on this mesh " +
                       $"(min cell {cores.MinCellEdgeM * 1e6:F2} µm, extent {cores.ExtentM * 1e6:F0} µm):");
        _out.WriteLine($"  Dcim.FitAtHeights          {fitMs,8:F1} ms");
        _out.WriteLine($"  FromDcimAtHeights          {decompMs,8:F3} ms");
        _out.WriteLine($"  RadialRemainderTable.Build {tableMs,8:F1} ms  " +
                       $"({table.SampleCount:N0} samples, {table.SampleCount * 16L / 1024.0:F0} kB)");
        _out.WriteLine($"  → per added pair: {fitMs + decompMs + tableMs:F1} ms and " +
                       $"{table.SampleCount * 16L / 1024.0:F0} kB.");
        foreach (int nz in new[] { 2, 4, 8 })
        {
            int added = viaSpans * (nz * (nz + 1) / 2 + nz * levelCount) - viaSpans * (1 + levelCount);
            _out.WriteLine($"    n_z = {nz}: {added,3} added pairs ⇒ " +
                           $"{added * (fitMs + decompMs + tableMs) / 1000.0,6:F2} s and " +
                           $"{added * table.SampleCount * 16L / (1024.0 * 1024.0),5:F1} MB per frequency, " +
                           $"i.e. {added * (fitMs + decompMs + tableMs) / 1000.0 / 149.9,6:P2} of a " +
                           $"149.9 s de-embedded point.");
        }

        // ── (3) WHETHER THE FITS ARE NEEDED AT ALL ────────────────────────────────────────────
        //
        // L9c's M3 established that the interior height dependence spans exactly FOUR exponential
        // families with height-independent COEFFICIENTS. The spatial question is whether that makes a
        // fifth height pair a constant-coefficient combination of four fitted ones, the way L9b's D5
        // shift makes a top-half-space pair one fit. It does NOT, and the reason is derivable before
        // it is measured: the four basis functions are e^{∓jk_zm z}e^{∓jk_zn z′}, which are themselves
        // functions of k_ρ, so the 4×4 matrix that recovers the coefficients is k_ρ-dependent and does
        // not survive the inverse transform. Measured rather than asserted, because "asserting the
        // absence of a theorem needs the same evidence as asserting one" (L9c's own finding).
        double lambda = EmConstants.C0 / f;
        var rhos = new double[12];
        for (int i = 0; i < rhos.Length; i++)
            rhos[i] = lambda * Math.Pow(10.0, -3.0 + 2.0 * i / (rhos.Length - 1.0));   // ρ/λ ∈ [1e-3, 0.1]

        (double, double)[] refPairs =
        [
            (zb + 0.10 * (zt - zb), zb + 0.10 * (zt - zb)),
            (zb + 0.90 * (zt - zb), zb + 0.90 * (zt - zb)),
            (zb + 0.10 * (zt - zb), zb + 0.90 * (zt - zb)),
            (zb + 0.35 * (zt - zb), zb + 0.65 * (zt - zb)),
        ];
        var refModels = refPairs
            .Select(p => Dcim.FitAtHeights(greens, GreensKernel.VerticalVectorPotential, p.Item1, p.Item2))
            .ToArray();

        double z5 = zb + 0.60 * (zt - zb), z5p = zb + 0.25 * (zt - zb);
        var fifth = Dcim.FitAtHeights(greens, GreensKernel.VerticalVectorPotential, z5, z5p);

        // Solve the four coefficients from four ρ points, then measure the prediction on the rest.
        var a = new Complex[4, 4];
        var rhs = new Complex[4];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++) a[r, c] = refModels[c].EvaluateAtHeights(rhos[3 * r]);
            rhs[r] = fifth.EvaluateAtHeights(rhos[3 * r]);
        }
        var alpha = Solve4(a, rhs);

        double worstSpan = 0, worstShift = 0;
        for (int i = 0; i < rhos.Length; i++)
        {
            Complex want = fifth.EvaluateAtHeights(rhos[i]);
            Complex got = Complex.Zero;
            for (int c = 0; c < 4; c++) got += alpha[c] * refModels[c].EvaluateAtHeights(rhos[i]);
            double scale = SommerfeldIntegral.FreeSpace(2 * Math.PI * f / EmConstants.C0, rhos[i]).Magnitude;
            worstSpan = Math.Max(worstSpan, (got - want).Magnitude / scale);
            worstShift = Math.Max(worstShift, 0.0);
        }
        _ = worstShift;

        _out.WriteLine("\nM1 (3) — can FOUR fitted models predict a FIFTH height pair's SPATIAL kernel?");
        _out.WriteLine($"  Four reference pairs, coefficients solved from four ρ points, measured over " +
                       $"ρ/λ ∈ [1e-3, 0.1] as a fraction of the free-space kernel: worst {worstSpan:E2}.");
        _out.WriteLine("  The four-family span is a SPECTRAL statement at fixed k_ρ. Its basis functions " +
                       "e^{∓jk_zm z}e^{∓jk_zn z′} depend on k_ρ themselves, so the recombination " +
                       "coefficients do too and they do not pass through the inverse transform. This is " +
                       "NOT L9b's D5, where the height pair shifts a DEPTH and every amplitude is " +
                       "unchanged.");

        // What IS height-independent, and it is the whole basis of the design that follows: the two
        // extracted ASYMPTOTE coefficients. They are k_ρ → ∞ limits of the cascade — the local Fresnel
        // coefficients of the source region — so they do not move with the heights at all, and their
        // DEPTHS are exactly Δ = |z − z′| and Σ_b = z + z′ − 2z_b. That makes the SINGULAR half of the
        // z-integral a closed form with no fit anywhere in it, and leaves only a bounded remainder for
        // the per-node fits.
        Complex c0d = Complex.Zero, c0i = Complex.Zero;
        double worstCoefficientDrift = 0;
        foreach (var (z, zp) in refPairs.Append((z5, z5p)))
        {
            var asym = greens.AsymptoticAtHeights(GreensKernel.VerticalVectorPotential, z, zp);
            if (c0d == Complex.Zero && c0i == Complex.Zero) { c0d = asym.DirectCoefficient; c0i = asym.ImageCoefficient; }
            worstCoefficientDrift = Math.Max(worstCoefficientDrift,
                Math.Max((asym.DirectCoefficient - c0d).Magnitude / c0d.Magnitude,
                         (asym.ImageCoefficient - c0i).Magnitude / Math.Max(c0i.Magnitude, 1e-300)));
            Assert.Equal(Math.Abs(z - zp), asym.DirectDepth, 15);
        }
        _out.WriteLine($"\n  But the two ASYMPTOTE coefficients ARE height-independent, to " +
                       $"{worstCoefficientDrift:E2} over the same five pairs, and their depths are " +
                       $"exactly Δ and Σ_b. The singular half of ∫∫dz dz′ therefore needs NO fit.");
        Assert.True(worstCoefficientDrift < 1e-14,
            $"the asymptote coefficients must not depend on the heights: {worstCoefficientDrift:E2}");

        // And the smallest fitted image depth across the z-node set, which is what says the BOUNDED
        // remainder stays smooth on the mesh's own scale (R-fil-8's ratio, asked of the z-quadrature).
        double worstRatio = double.PositiveInfinity;
        foreach (var (z, zp) in refPairs.Append((z5, z5p)))
        {
            var t = PlanarKernelTerms.FromDcimAtHeights(
                Dcim.FitAtHeights(greens, GreensKernel.VerticalVectorPotential, z, zp));
            worstRatio = Math.Min(worstRatio, t.SmallestImageDepth / cores.MinCellEdgeM);
        }
        _out.WriteLine($"  R-fil-8's ratio over the same pairs: min|b_i|/cell = {worstRatio:F2} — the " +
                       $"fitted images stay smooth on the mesh's own scale at every height pair.");

        Assert.Equal(mesh.Bases.Count, zBase.RowCount);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M3 / TIER 1 — THE GROUND-ATTACHMENT BASIS AGAINST A CLOSED FORM
    //
    // A backside via joins a signal level to the GROUND PLANE, which is the laterally infinite PEC
    // the Green's function handles analytically and is never a meshed level — so L9c's rooftop-in-z
    // cannot express it at all, and L9's own phase gate had to re-word §11's gate sentence rather
    // than fail it. What it needs is an ATTACHMENT (half) basis, and here is its oracle.
    //
    // §2's own observation, and it is exact: a via to ground at εᵣ = 1 over a PEC is a bar of
    // length ℓ PLUS ITS EQUAL-DIRECTION IMAGE (L9e's T2_1 earned that sign: the CURRENT reflection
    // at a PEC is +1) — i.e. exactly the half of a 2ℓ bar ExactInductance already integrates in
    // closed form, evaluated at z₀ = 0. No new oracle is needed and none is written.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>ONE ground-attachment basis over a w×w footprint, its meshed foot at z = ℓ and its
    /// other terminal the plane at z = 0. Hand-built, exactly as <see cref="Stack"/> is.</summary>
    private static (PlanarMesh Mesh, PlanarLevels Levels) GroundAttachment(double w, double ell)
    {
        var cell  = new PlanarCell(0, 0, 0, 0, 0, w, w);
        var basis = new PlanarBasis(0, 0, 0, PlanarBasisDirection.Z, AttachesToGround: true);
        return (new PlanarMesh([cell], [basis], ["M1"], [0, w], [0, w]),
                new PlanarLevels([ell], 0.0));
    }

    /// <summary>εᵣ = 1 over a PEC, SPLIT at the metal level so the source sits on an INTERIOR
    /// interface — the pairing a real backside via has, and the one whose asymptote carries the
    /// non-decaying Σ_b term L9c's M2/D3 found. The two halves are physically identical air.</summary>
    private static LayerStack AirOverPecSplitAt(double ell, double aboveM) =>
        new(Termination.Pec,
            [new MediumLayer(ell, EmMaterial.Air), new MediumLayer(aboveM, EmMaterial.Air)],
            Termination.Air);

    [Fact]
    [Trait("Category", "Benchmark")]   // the ℓ/w curve for the attachment basis, ~3 min
    public void T4_1_TIER1_THE_GATE_TheGROUNDVIAsOwnInductance_MatchesTheClosedForm()
    {
        // The same sweep T3_1 runs for an interior via, for the basis that did not exist until now.
        // Three widths spanning 16× so a remedy that only worked at one scale would show.
        double f = 5e9;
        var widths = new[] { 10e-6, 40e-6, 160e-6 };
        double worst = 0;

        _out.WriteLine("The GROUND-ATTACHMENT basis against the exact partial inductance of a bar " +
                       "plus its equal-direction image (ExactInductance at z₀ = 0):");
        _out.WriteLine("  ℓ/w       w=10 µm    w=40 µm   w=160 µm");
        foreach (double ratio in new[] { 0.01, 0.05, 0.075, 0.1, 0.5, 1.0, 5.0 })
        {
            var cols = new List<string>();
            foreach (double ww in widths)
            {
                double ell = ratio * ww;
                var (mesh, levels) = GroundAttachment(ww, ell);
                double got   = SeriesInductance(mesh, levels, AirOverPecSplitAt(ell, ell), f);
                double exact = ExactInductance(ww, 0.0, ell);
                double err   = got / exact - 1;
                worst = Math.Max(worst, Math.Abs(err));
                cols.Add($"{err,9:P2}");
            }
            _out.WriteLine($"  {ratio,6:F3}  {string.Join(" ", cols)}");
        }

        _out.WriteLine($"\nWorst |error| over ℓ/w ∈ [0.01, 5] and a 16× range of w: {worst:P3}.");
        Assert.True(worst < 0.01,
            $"Tier 1: the ground via's own inductance must match the closed form to 1%, and it " +
            $"reads {worst:P3}");
    }

    [Fact]
    public void T4_2_TIER3_TheAttachmentsNetChargeIsNOTZero_AndTheGroundPlaneIsTheReturn()
    {
        // §0.2 item 4(a). L9c's D5 asserts ∫∇·f dS = 0 as an EQUALITY on every basis; an attachment
        // has ONE pulse, so its net charge is ∓1 — balanced by its IMAGE below the plane, not by a
        // second pulse on the metal. The equality that survives is |net| = 1, and it is asserted
        // exactly rather than to a tolerance for the same reason D5's own zero is: a basis whose
        // charge is merely approximately right puts a monopole on a cell.
        var (mesh, levels) = GroundAttachment(40e-6, 20e-6);
        var b = mesh.Bases[0];
        Assert.True(b.AttachesToGround);

        Assert.Equal(-1.0, PlanarBasisFunctions.NetCharge(mesh, b));

        var (ha, hb) = PlanarBasisFunctions.Halves(mesh, b);
        Assert.Equal(-1.0, ha.Sign);
        Assert.Equal(0.0, hb.Sign);            // the GROUNDED half — no pulse, and Sign = 0 is what
        Assert.Equal(ha.CellIndex, hb.CellIndex);  // makes the fill's four-term sum drop it with no
                                                    // special case anywhere.

        var c = mesh.Cells[0];
        Assert.Equal(-1.0 / c.Area, PlanarBasisFunctions.Divergence(mesh, b, c.CenterX, c.CenterY, 0));

        // …and it still carries a full 1 A across its footprint, uniformly, exactly like every other
        // vertical basis. The half-ness is in the CHARGE, not in the current.
        Assert.Equal(1.0 / c.Area, PlanarBasisFunctions.VerticalWeight(mesh, b, c.CenterX, c.CenterY));
        Assert.Equal(1.0, PlanarBasisFunctions.VerticalWeight(mesh, b, c.CenterX, c.CenterY) * c.Area, 15);
        Assert.Equal(c.Area, PlanarBasisFunctions.SharedFootprintArea(mesh, b));

        // THE MEASUREMENT, not the comment (R-gv-2): the basis PLUS ITS IMAGE is neutral, so the
        // scalar kernel its one pulse is convolved with must fall FASTER than a monopole's 1/ρ. If
        // the plane's image were absent or wrongly signed, it would fall like 1/ρ exactly — which is
        // what this separates.
        //
        // TAKEN AT A TOP-SURFACE HEIGHT, and that limitation is gap B rather than a shortcut:
        // LayeredStaticGreens is referenced to the top half-space and REFUSES an interior source by
        // name (the very refusal Part B exists to lift), so the arrangement is reproduced with the
        // metal on the slab's own top surface. Same physics — one charge at height h over a PEC —
        // and it is the arrangement the claim is about; the assembled row at a genuinely BURIED
        // height needs the interior static kernel Part B builds.
        double h = levels.Of(0);
        var topSurface = LayerStack.FromGroundedSlab(new GroundedSlab(h, EmMaterial.Air));
        double p1 = LayeredStaticGreens.ScalarPotential(topSurface, 1e-3, h, h).Real;
        double p2 = LayeredStaticGreens.ScalarPotential(topSurface, 1e-2, h, h).Real;
        double slope = Math.Log(p2 / p1) / Math.Log(10.0);

        _out.WriteLine($"net charge = {PlanarBasisFunctions.NetCharge(mesh, b):F1} (one pulse, at the " +
                       $"meshed foot); the plane's image is the return.");
        _out.WriteLine($"scalar-block row decay over a decade in ρ: ρ^{slope:F2} — a monopole is " +
                       $"ρ^-1, a neutral (charge + image) pair falls faster.");
        Assert.True(slope < -1.5,
            $"the basis plus its image must decay faster than a monopole; measured ρ^{slope:F2}");
    }

    [Fact]
    public void T4_3_TIER7_TheEXTRACTEDCONSTANT_NoLongerCancelsOnANetChargedRow_AndMustStillNotMatter()
    {
        // §0.2 item 4(b), and it is the real trap of this whole slice. L8c's own header records that
        // s_A + s_B = 0, so "any part of G_q that does not depend on ρ contributes exactly ZERO to
        // the scalar block". That cancellation is what makes the extracted CONSTANT harmless
        // everywhere else — and it FAILS for the attachment row, whose four-term signed sum
        // degenerates to one term. The constant now survives into Z^φ, in exactly one row, and
        // NOTHING IN THE FILL NOTICES.
        //
        // What must still be true is that the SPLIT is arbitrary: PlanarExtractionOrder decides how
        // much of G_q rides in the closed-form constant and how much in the numerically-integrated
        // remainder, and the two must add back up. If a net-charged row let a spurious constant
        // through — or dropped a real one — the orders would disagree, and they would disagree ONLY
        // on that row. This asks the question directly rather than reasoning about the sign.
        double w = 40e-6, ell = 20e-6, f = 5e9;
        var (mesh, levels) = GroundAttachment(w, ell);
        var stack = AirOverPecSplitAt(ell, ell);

        Complex At(PlanarExtractionOrder order)
        {
            var st    = PlanarFillSettings.Default with { Order = order };
            var cores = PlanarFill.BuildCores(mesh, st);
            var set   = new PlanarKernelSet(new LayeredSpectralGreens(stack, f), order, 0.0).For(cores);
            return PlanarFill.FillMultiLevel(cores, set, levels, 2 * Math.PI * f)[0, 0];
        }

        var atInverse  = At(PlanarExtractionOrder.Inverse);
        var atConstant = At(PlanarExtractionOrder.Constant);
        var atLinear   = At(PlanarExtractionOrder.Linear);

        double dc = (atConstant - atInverse).Magnitude / atInverse.Magnitude;
        double dl = (atLinear   - atInverse).Magnitude / atInverse.Magnitude;

        _out.WriteLine($"the attachment's own Z[0,0], by extraction order (the split is arbitrary; " +
                       $"the total must not be):");
        _out.WriteLine($"  Inverse  {atInverse}");
        _out.WriteLine($"  Constant {atConstant}   ({dc:E2} relative)");
        _out.WriteLine($"  Linear   {atLinear}   ({dl:E2} relative)");
        _out.WriteLine("  The constant no longer cancels on this row (s_A + s_B = −1, not 0), so this " +
                       "agreement is a real statement about it rather than a vacuous one.");

        Assert.True(dc < 1e-6 && dl < 1e-6,
            $"the extracted constant must not change the answer on a net-charged row: " +
            $"Constant {dc:E2}, Linear {dl:E2} against Inverse");

        // …and the cancellation genuinely IS gone, so the agreement above is not vacuous.
        Assert.Equal(-1.0, PlanarBasisFunctions.NetCharge(mesh, mesh.Bases[0]));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M1 — WHAT A z-CHAIN COSTS, AND WHETHER IT IS NEEDED
    // (brief-ground-vias-and-interior-electrostatics §3, R-gv-1)
    //
    // The via z-INTEGRAL is resolved (the follow-up above). What is NOT resolved is the via's
    // current PROFILE: L9c's basis is ONE z-rooftop per inter-level gap, so the current it carries
    // is uniform along the whole length, and `MaxElectricalLength` (k·ℓ ≤ 0.05) is the bound on
    // exactly that. §0.2 item 3 says a 100 µm GaAs backside via at 30 GHz is k·ℓ = 0.23 — 4.5×
    // over — so the question this milestone answers is whether a CHAIN of z-segments is affordable
    // and whether it changes the answer where it would be used.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The mesh a CHAIN of n z-segments per via would produce.</b> n − 1 new interior levels that
    /// carry only the via footprint cells, and n vertical bases per original via basis, with the
    /// junction continuity at each interior level being the same ±1/Area pair L9c's D5 already gates.
    ///
    /// <para>Built HERE, in the test, on purpose: M1 changes no production code (D1), and the fit
    /// count it measures is a property of the PAIRING SET the fill asks for — which this construction
    /// reproduces exactly, because <c>FillMultiLevel</c> keys every fit off
    /// <c>PlanarLevels.Of(cell.LayerIndex)</c> and <c>SpanOf(levels, basis.LayerIndex)</c> and has no
    /// other source of heights.</para>
    ///
    /// <para>R-via-5's ordering (every horizontal basis before every vertical one) and R-msh-2's cell
    /// order (LayerIndex, IY, IX) are both preserved by construction.</para>
    /// </summary>
    private static (PlanarMesh Mesh, PlanarLevels Levels) ChainVias(
        PlanarMesh mesh, PlanarLevels levels, int n)
    {
        if (n <= 1) return (mesh, levels);
        if (levels.Z.Count != 2)
            throw new ArgumentException("the chain helper assumes exactly two meshed levels.");

        double z0 = levels.Z[0], z1 = levels.Z[1];
        var newZ = new double[n + 1];
        for (int k = 0; k <= n; k++) newZ[k] = z0 + (z1 - z0) * k / n;

        var vias = mesh.Bases.Where(b => b.Direction == PlanarBasisDirection.Z).ToArray();

        var cells    = new List<PlanarCell>();
        var oldToNew = new Dictionary<int, int>();
        for (int i = 0; i < mesh.Cells.Count; i++)
            if (mesh.Cells[i].LayerIndex == 0) { oldToNew[i] = cells.Count; cells.Add(mesh.Cells[i]); }

        // The interior levels carry NOTHING but the via footprints — that is the whole point: they
        // are levels of the BASIS, not levels of metal.
        var order = Enumerable.Range(0, vias.Length)
            .OrderBy(v => mesh.Cells[vias[v].CellA].IY)
            .ThenBy(v => mesh.Cells[vias[v].CellA].IX)
            .ToArray();
        var midCell = new int[Math.Max(0, n - 1)][];
        for (int k = 1; k < n; k++)
        {
            midCell[k - 1] = new int[vias.Length];
            foreach (int v in order)
            {
                midCell[k - 1][v] = cells.Count;
                cells.Add(mesh.Cells[vias[v].CellA] with { LayerIndex = k });
            }
        }

        for (int i = 0; i < mesh.Cells.Count; i++)
            if (mesh.Cells[i].LayerIndex == 1)
            {
                oldToNew[i] = cells.Count;
                cells.Add(mesh.Cells[i] with { LayerIndex = n });
            }

        var bases = new List<PlanarBasis>();
        foreach (var b in mesh.Bases)
            if (b.Direction != PlanarBasisDirection.Z)
                bases.Add(new PlanarBasis(b.LayerIndex == 0 ? 0 : n,
                                          oldToNew[b.CellA], oldToNew[b.CellB], b.Direction));

        for (int v = 0; v < vias.Length; v++)
        {
            int lower = oldToNew[vias[v].CellA];
            for (int k = 0; k < n; k++)
            {
                int upper = k == n - 1 ? oldToNew[vias[v].CellB] : midCell[k][v];
                bases.Add(new PlanarBasis(k, lower, upper, PlanarBasisDirection.Z));
                lower = upper;
            }
        }

        var names = new string[n + 1];
        for (int k = 0; k <= n; k++)
            names[k] = k == 0 ? mesh.LayerNames[0]
                     : k == n ? mesh.LayerNames[1]
                     : $"__chain{k}";

        return (new PlanarMesh(cells, bases, names, mesh.GridX, mesh.GridY), new PlanarLevels(newZ));
    }

    /// <summary>εᵣ = 12.9 GaAs over a PEC floor, air above — the medium §0.2 item 3's own k·ℓ number
    /// is computed in, so the necessity measurement below is taken where the bound actually binds.</summary>
    private static LayerStack GaAsOverPec(double thicknessM) =>
        new(Termination.Pec, [new MediumLayer(thicknessM, new EmMaterial(12.90, 0.0020))],
            Termination.Air);

    /// <summary>
    /// <b>A via that is electrically LONG and ATTACHED at both ends</b> — the case k·ℓ ≤ 0.05 is
    /// actually about. Two conductor levels <paramref name="gapM"/> apart inside one GaAs slab, on
    /// interior interfaces that are physically invisible (the same trick
    /// <c>LayerStacks.AirOverGround</c> uses: split one medium so a level has an interface to sit on).
    /// </summary>
    private static PlanarProblem AttachedLongVia(double fHz, double gapM,
                                                 double lenM = 200e-6, double zLowerM = 20e-6)
    {
        var gaas  = new EmMaterial(12.90, 0.0020);
        var stack = new LayerStack(Termination.Pec,
            [new MediumLayer(zLowerM, gaas), new MediumLayer(gapM, gaas), new MediumLayer(80e-6, gaas)],
            Termination.Air);

        PlanarPolygon Rect(double x0, double y0, double x1, double y1) =>
            new([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]);

        var lower = new PlanarConductorLayer("M1", [Rect(0, 0, lenM, 80e-6)], 4.1e7, 2e-6, zLowerM);
        var upper = new PlanarConductorLayer("M2", [Rect(0, 0, lenM, 80e-6)], 4.1e7, 2e-6, zLowerM + gapM);
        var vias  = new[] { new PlanarVia(0, 1, [Rect(0.4 * lenM, 20e-6, 0.4 * lenM + 40e-6, 60e-6)],
                                          4.1e7) };
        return new PlanarProblem([lower, upper], GroundedSlab.GaAsStarter, fHz, null, stack, vias);
    }

    [Fact]
    [Trait("Category", "Benchmark")]   // four chain fills on the phase-gate medium, ~3 min
    public void M1_2_RgV1_WhatAZCHAINCosts_InFITS_SECONDS_AndWhetherItChangesTheAnswer()
    {
        // ── (1) HOW MANY HEIGHT PAIRINGS AN n-SEGMENT CHAIN ASKS FOR ───────────────────────────
        //
        // Measured, not derived: the fill keys every fit off PlanarKernelSet's canonicalised
        // (kernel, min z, max z), so the only honest count is the one a real fill produces.
        double f = 10e9, len = 400e-6;
        var problem = TwoLevelWithVia(f, len);
        var report  = SurfaceMesher.Mesh(problem);
        var levels0 = PlanarLevels.From(problem);
        double omega = 2 * Math.PI * f;

        _out.WriteLine($"M1 (1) — the fixture: N = {report.UnknownCount} " +
                       $"({report.ViaUnknownCount} vertical) on {problem.Layers.Count} levels, " +
                       $"one via spanning the 3 µm interior region, at {f / 1e9:F0} GHz.\n");
        _out.WriteLine("    n   N      vertical   fits/frequency   added over n = 1   seconds @105 ms" +
                       "   % of a 149.9 s point");

        int baseFits = 0;
        var fitsByN = new Dictionary<int, int>();
        foreach (int n in new[] { 1, 2, 4, 8 })
        {
            var (mesh, levels) = ChainVias(report.Mesh, levels0, n);
            var cores = PlanarFill.BuildCores(mesh);
            var set   = new PlanarKernelSet(new LayeredSpectralGreens(problem.EffectiveStack, f));
            var z     = PlanarFill.FillMultiLevel(cores, set.For(cores), levels, omega);
            Assert.Equal(mesh.Bases.Count, z.RowCount);

            int fits = set.FitCount;
            fitsByN[n] = fits;
            if (n == 1) baseFits = fits;

            int vertical = mesh.Bases.Count(b => b.Direction == PlanarBasisDirection.Z);
            double sec   = (fits - baseFits) * 0.105;
            _out.WriteLine($"  {n,3}   {mesh.Bases.Count,-6} {vertical,-10} {fits,-16} " +
                           $"{fits - baseFits,-18} {sec,-19:F2} {sec / 149.9,-6:P2}");
        }

        // The growth law, stated from the measurement rather than assumed. Equal-length segments do
        // NOT share pairings: a fit is keyed on ABSOLUTE heights, and segment k's node set sits at
        // z₀ + kℓ/n + offsets, so every span pair is a distinct pairing.
        double g24 = (fitsByN[4] - baseFits) / (double)Math.Max(1, fitsByN[2] - baseFits);
        double g48 = (fitsByN[8] - baseFits) / (double)Math.Max(1, fitsByN[4] - baseFits);
        _out.WriteLine($"\n  Growth of the ADDED fits: n = 2→4 is {g24:F2}×, n = 4→8 is {g48:F2}×. " +
                       $"A quadratic law doubles at 4× per doubling; a linear one at 2×.");
        _out.WriteLine("  Equal-length segments share NO pairing — a fit is keyed on ABSOLUTE heights " +
                       "and segment k's nodes sit at z₀ + kℓ/n + offsets, so the ẑẑ block's n(n+1)/2 " +
                       "span pairs are all distinct. The SCALAR block adds its own (n+1)(n+2)/2 level " +
                       "pairings, which the ẑẑ arithmetic alone does not predict.");

        // ── (3) IS THE CHAIN NEEDED AT THE FREQUENCIES §0.2 ITEM 3 NAMES? ──────────────────────
        //
        // A shorted-stub fixture needs the ATTACHMENT basis, which is M3's. The physics question is
        // the same one and is answerable today: give a via of the SAME electrical length a chain of
        // n segments in a real GaAs medium and see whether the answer moves. The chain's currents are
        // NOT forced equal — each interior foot cell carries its own ±1/Area divergence pulse, so
        // charge accumulates there and displacement current lets i_k ≠ i_{k+1}. That freedom is
        // exactly what k·ℓ ≤ 0.05 exists to bound.
        //
        // The comparable scalar across n is the REACTION vᵀZ⁻¹v with v_m = ℓ_m (a uniform incident
        // E_z over the whole via): the excitation is the same physical field at every n, and the
        // quantity converges to the true response as the basis is refined.
        const double w = 40e-6, ell = 100e-6, zBot = 20e-6, slab = 200e-6;
        double kOf(double fHz) => 2 * Math.PI * fHz * Math.Sqrt(12.90) / EmConstants.C0;
        double fFor(double kl) => kl * EmConstants.C0 / (2 * Math.PI * ell * Math.Sqrt(12.90));

        _out.WriteLine($"\nM1 (3) — a {ell * 1e6:F0} µm via in {slab * 1e6:F0} µm GaAs, w = {w * 1e6:F0} µm. " +
                       $"MaxElectricalLength = {PlanarLevels.MaxElectricalLength}.");
        _out.WriteLine("   k·ℓ      f (GHz)    n=1 reaction        n=2 vs n=1   n=4 vs n=1   n=8 vs n=1" +
                       "   worst |i_k/mean − 1| at n=8");

        foreach (double kl in new[] { 0.01, 0.05, 0.10, 0.23 })
        {
            double fHz = fFor(kl);
            var medium = GaAsOverPec(slab);
            Complex first = Complex.Zero;
            var rel = new double[3];
            double worstProfile = 0;
            int col = 0;

            foreach (int n in new[] { 1, 2, 4, 8 })
            {
                var (mesh, lv) = Stack(w, zBot, ell, n);
                var cores = PlanarFill.BuildCores(mesh);
                var set   = new PlanarKernelSet(new LayeredSpectralGreens(medium, fHz)).For(cores);
                var z     = PlanarFill.FillMultiLevel(cores, set, lv, 2 * Math.PI * fHz);

                var v = new Vec<Complex>(n);
                for (int m = 0; m < n; m++) v[m] = ell / n;
                var i = z.Lu().Solve(v);

                Complex reaction = Complex.Zero;
                for (int m = 0; m < n; m++) reaction += v[m] * i[m];

                if (n == 1) first = reaction;
                else rel[col++] = (reaction - first).Magnitude / first.Magnitude;

                if (n == 8)
                {
                    Complex mean = Complex.Zero;
                    for (int m = 0; m < n; m++) mean += i[m];
                    mean /= n;
                    for (int m = 0; m < n; m++)
                        worstProfile = Math.Max(worstProfile, (i[m] - mean).Magnitude / mean.Magnitude);
                }
            }

            _out.WriteLine($"  {kl,5:F2}   {fHz / 1e9,8:F2}   {first.Magnitude,10:E3}   " +
                           $"{rel[0],10:P3}   {rel[1],10:P3}   {rel[2],10:P3}   {worstProfile,10:P2}");
            Assert.True(kOf(fHz) * ell > 0, "k·ℓ must be positive");
        }

        _out.WriteLine("\n  READ THE FREQUENCY COLUMN FIRST. If the movement with n is the same at " +
                       "k·ℓ = 0.01 as at 0.23, it is NOT the electrical effect MaxElectricalLength " +
                       "bounds — it is a static one, and the bound is being blamed for the wrong " +
                       "quantity (exactly L9e's own finding about the midpoint rule, one layer up).");
        _out.WriteLine("  It is also the WRONG BOUNDARY CONDITION for a via in a circuit: this rod is " +
                       "FLOATING, so its true current vanishes at both ends and a uniform-current " +
                       "basis can never represent that however short the via is. (3b) attaches it.");

        // ── (3b) THE SAME QUESTION WITH THE VIA ATTACHED AT BOTH ENDS ─────────────────────────
        //
        // A via in a real structure lands on metal at both ends and its terminal current is not zero
        // there — which is precisely the boundary condition (3a) cannot have. Here the via joins two
        // conductor levels a real 100 µm apart; only the VERTICAL bases are excited (v_m = ℓ_m, a
        // uniform E_z over the via), and the horizontal metal responds through the matrix.
        _out.WriteLine("\nM1 (3b) — the SAME via, now ATTACHED to horizontal metal at both ends.");
        _out.WriteLine("   k·ℓ      f (GHz)   N     n=1 reaction        n=2 vs n=1   n=4 vs n=1   " +
                       "n=8 vs n=1   worst |i_k/mean − 1| at n=8");

        // 0.23 is §0.2 item 3's own number; 0.5 and 1.0 are past it, and 1.0 is as far as
        // Dcim.ValidatedRhoOverLambdaAtHeights = 0.1 permits on this fixture (200 µm at 133 GHz is
        // 0.089 λ₀). Going further would be measuring outside the kernel's own validated range.
        foreach (double kl in new[] { 0.01, 0.23, 0.5, 1.0 })
        {
            double fHz = fFor(kl);
            var prob   = AttachedLongVia(fHz, ell);
            var rep    = SurfaceMesher.Mesh(prob);
            var lv0    = PlanarLevels.From(prob);

            Complex first = Complex.Zero;
            var rel = new double[3];
            double worstProfile = 0;
            int col = 0, nUnknown = 0;

            foreach (int n in new[] { 1, 2, 4, 8 })
            {
                var (mesh, lv) = ChainVias(rep.Mesh, lv0, n);
                var cores = PlanarFill.BuildCores(mesh);
                var set   = new PlanarKernelSet(new LayeredSpectralGreens(prob.EffectiveStack, fHz));
                var z     = PlanarFill.FillMultiLevel(cores, set.For(cores), lv, 2 * Math.PI * fHz);
                nUnknown  = mesh.Bases.Count;

                var vertical = Enumerable.Range(0, mesh.Bases.Count)
                    .Where(m => mesh.Bases[m].Direction == PlanarBasisDirection.Z).ToArray();

                var v = new Vec<Complex>(mesh.Bases.Count);
                foreach (int m in vertical) v[m] = ell / n;
                var i = z.Lu().Solve(v);

                Complex reaction = Complex.Zero;
                foreach (int m in vertical) reaction += v[m] * i[m];

                if (n == 1) first = reaction;
                else rel[col++] = (reaction - first).Magnitude / first.Magnitude;

                if (n == 8)
                {
                    Complex mean = Complex.Zero;
                    foreach (int m in vertical) mean += i[m];
                    mean /= vertical.Length;
                    foreach (int m in vertical)
                        worstProfile = Math.Max(worstProfile, (i[m] - mean).Magnitude / mean.Magnitude);
                }
            }

            double rhoOverLambda = SurfaceMesher.Mesh(prob) is { } r2
                ? Math.Sqrt(Math.Pow(200e-6, 2) + Math.Pow(80e-6, 2)) * fHz / EmConstants.C0
                : double.NaN;
            _out.WriteLine($"  {kl,5:F2}   {fHz / 1e9,8:F2}   {nUnknown,-5} {first.Magnitude,10:E3}   " +
                           $"{rel[0],10:P3}   {rel[1],10:P3}   {rel[2],10:P3}   {worstProfile,10:P2}" +
                           $"   (ρ/λ ≤ {rhoOverLambda:F3})");
            Assert.True(rhoOverLambda <= Dcim.ValidatedRhoOverLambdaAtHeights,
                $"the necessity measurement must stay inside G_A^zz's own validated range: " +
                $"ρ/λ = {rhoOverLambda:F3} at k·ℓ = {kl}");
        }

        _out.WriteLine("\n  The RECOMMENDATION follows from the difference between these two tables and " +
                       "from the fit-count table above, and it is written up in " +
                       "src/Engine/Mom/CLAUDE.md rather than asserted here.");
    }

    /// <summary>A 4×4 complex solve by Gaussian elimination with partial pivoting — four lines, and
    /// nothing in this repository's linear-algebra dependency is needed for it.</summary>
    private static Complex[] Solve4(Complex[,] a, Complex[] b)
    {
        int n = b.Length;
        var m = (Complex[,])a.Clone();
        var r = (Complex[])b.Clone();
        for (int k = 0; k < n; k++)
        {
            int piv = k;
            for (int i = k + 1; i < n; i++)
                if (m[i, k].Magnitude > m[piv, k].Magnitude) piv = i;
            if (piv != k)
            {
                for (int j = 0; j < n; j++) (m[k, j], m[piv, j]) = (m[piv, j], m[k, j]);
                (r[k], r[piv]) = (r[piv], r[k]);
            }
            for (int i = k + 1; i < n; i++)
            {
                Complex fac = m[i, k] / m[k, k];
                for (int j = k; j < n; j++) m[i, j] -= fac * m[k, j];
                r[i] -= fac * r[k];
            }
        }
        var x = new Complex[n];
        for (int i = n - 1; i >= 0; i--)
        {
            Complex s = r[i];
            for (int j = i + 1; j < n; j++) s -= m[i, j] * x[j];
            x[i] = s / m[i, i];
        }
        return x;
    }
}
