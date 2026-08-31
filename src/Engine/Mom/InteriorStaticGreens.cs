using System.Numerics;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>MIM-4 — the ω → 0 Green's function of a general <see cref="LayerStack"/> at an ARBITRARY pair
/// of heights, source and observer each anywhere in the stack.</b>
///
/// <para><see cref="StaticGreens"/> is one grounded slab with both points on its top surface;
/// <see cref="LayeredStaticGreens"/> is N layers with both points in the top half-space. This is
/// neither restriction, and it is what a de-embedded port on a BURIED level needs — <c>C_pul</c>
/// differences two electrostatic solves, and the de-embedded S is referenced to the <c>Z_c</c> they
/// yield, so the electrostatics of the level the feed actually sits on is not a diagnostic detail.</para>
///
/// <para><b>Why the static problem has its own formulation rather than the full-wave cascade with ω
/// set small.</b> At ω = 0 there is no <c>k_z</c>: the potential in every region satisfies Laplace's
/// equation in the transform variable, <c>φ̃″ = k_ρ²φ̃</c>, so the propagation constant is <c>k_ρ</c>
/// <i>in every region alike</i> and only the interface coefficients differ. No branch point, no
/// surface-wave pole, no oscillation — which is why §L8c's fragility does not follow this path and
/// why the brief's "tamer than DCIM" is a statement about structure and not a hope.</para>
///
/// <para><b>The formulation, written out because a transcription would be unverifiable.</b> The
/// problem is a Sturm-Liouville one in z:</para>
/// <code>
///   d/dz( P(z) dG̃/dz ) − k²P(z) G̃ = −2k δ(z − z′),      P = ε*  (scalar) or 1/µ (vector)
/// </code>
/// <para>whose solution is the classical product form</para>
/// <code>
///   G̃(k; z, z′) = −2k · ψ↓(z&lt;) ψ↑(z&gt;) / W,    W = P(z)·(ψ↓ψ↑′ − ψ↓′ψ↑) = const,
/// </code>
/// <para>with ψ↓ the solution satisfying the BOTTOM termination and ψ↑ the TOP one. The
/// normalisation is fixed by the homogeneous case: <c>G̃ = e^{−k|z−z′|}/ε</c>, i.e. the SAME
/// convention <see cref="LayeredStaticGreens"/> already uses, so
/// <c>G(ρ) = (1/4π)∫₀^∞ G̃ J₀(k ρ) dk</c> and free space is <c>1/(4πρ)</c>.</para>
///
/// <para><b>Two things make this numerically well-behaved rather than merely correct.</b></para>
/// <list type="number">
/// <item><b>Every exponential is written as a DECAY over its own region</b> — <c>e^{−k(z_hi−z)}</c>
/// and <c>e^{−k(z−z_lo)}</c>, never <c>e^{+kz}</c> — so no term of the cascade overflows however
/// thick the stack or however large k gets. That matters here far more than in the full-wave
/// kernel: the static integrand is only killed by the exponentials, so k runs out to tens of
/// reciprocal layer thicknesses on a thin-film stack.</item>
/// <item><b>The inter-region scale factor telescopes ANALYTICALLY, and it has to.</b> Matching ψ↓
/// across interface i divides by <c>1 + Γ↓_{i−1}τ_i²</c>, which is ψ↓'s own value at the top of a
/// layer and genuinely vanishes as k → 0 over a PEC floor (0/0 with the numerator, which also
/// vanishes). Substituting the reflection recursion collapses it exactly:
/// <c>1 + Γ↓_i = (1+r_i)(1 + Γ↓_{i−1}τ_i²)/(1 + r_iΓ↓_{i−1}τ_i²)</c>, so the vanishing factor
/// cancels and what is left is <c>τ_{i+1}(1+r_i)/(1 + r_iΓ↓_{i−1}τ_i²)</c> — a denominator bounded
/// below by <c>1 − |r_i| &gt; 0</c> for every passive stack. Computing the ratio the obvious way
/// instead is a small/small division whose relative error is the reciprocal of the layer's own
/// electrical thickness.</item>
/// </list>
///
/// <para><b>ε* is COMPLEX throughout and that is not decoration</b> — L8a's recorded trap applies
/// here verbatim: a real-εᵣ static series sits a frequency-INDEPENDENT 1.1e-6 from the full-wave
/// kernel's ω → 0 limit, which reads exactly like a convergence floor and is nothing of the sort.
/// Nothing in the derivation above uses the realness of ε.</para>
/// </summary>
public static class InteriorStaticGreens
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // The cascade — reflection coefficients at every interface, both directions, at one k.
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The per-k state of the stack: the round-trip factors and the two reflection ladders. Built
    /// once per k and reused by every (z, z′) pair at that k — which is what makes a radial table
    /// or an image fit over a whole level pair affordable.
    /// </summary>
    public sealed class Cascade
    {
        /// <summary>e^{−k·d_i} for finite region i (1..N); 1 for the two semi-infinite ends.</summary>
        internal readonly double[] Tau;
        /// <summary>The interface coefficient at interface i for a "wave" arriving from ABOVE.</summary>
        internal readonly Complex[] R;
        /// <summary>Γ looking DOWN at interface i, referenced just above it. i = 0..N.</summary>
        internal readonly Complex[] Down;
        /// <summary>Γ looking UP at interface i, referenced just below it. i = 0..N.</summary>
        internal readonly Complex[] Up;

        internal readonly LayerStack Stack;
        internal readonly bool       Scalar;
        internal readonly double     K;

        internal Cascade(LayerStack stack, bool scalar, double k,
                         double[] tau, Complex[] r, Complex[] down, Complex[] up)
        { Stack = stack; Scalar = scalar; K = k; Tau = tau; R = r; Down = down; Up = up; }

        /// <summary>Γ looking down at interface <paramref name="i"/>, from just above it.</summary>
        public Complex ReflectionDown(int i) => Down[i];
        /// <summary>Γ looking up at interface <paramref name="i"/>, from just below it.</summary>
        public Complex ReflectionUp(int i) => Up[i];
    }

    /// <summary>
    /// The Sturm-Liouville weight of a region: <c>ε*</c> for the scalar (electrostatic) problem,
    /// <c>1/µ</c> for the vector (magnetostatic) one.
    ///
    /// <para>That pairing is what makes the vector kernel independent of εᵣ, which L8a records as a
    /// check rather than an oversight: for a non-magnetic stack every <c>P</c> is 1, every interface
    /// coefficient is 0, and the only reflection left is the ground plane's own.</para>
    /// </summary>
    public static Complex Weight(LayerStack stack, bool scalar, int region)
    {
        var m = stack.MaterialOfRegion(region);
        return scalar ? m.EpsComplex : Complex.One / m.MuR;
    }

    /// <summary>
    /// The coefficient at interface <paramref name="i"/> for a "wave" arriving from the region
    /// ABOVE — <c>(P_above − P_below)/(P_above + P_below)</c> in the weight above, which is the
    /// electrostatic <c>(ε_a − ε_b)/(ε_a + ε_b)</c> and the magnetostatic <c>(µ_b − µ_a)/(µ_b + µ_a)</c>.
    ///
    /// <para>Deliberately the SAME expression <see cref="LayeredStaticGreens"/> uses, so the two
    /// cannot drift: the reduction test that pins them together compares this whole class against
    /// that one and would not detect a sign flipped in both.</para>
    /// </summary>
    public static Complex InterfaceCoefficient(LayerStack stack, bool scalar, int i)
    {
        Complex pb = Weight(stack, scalar, i);
        Complex pa = Weight(stack, scalar, i + 1);
        return (pa - pb) / (pa + pb);
    }

    /// <summary>Builds the two reflection ladders at one k. O(N).</summary>
    public static Cascade Build(LayerStack stack, bool scalar, double kRho)
    {
        ArgumentNullException.ThrowIfNull(stack);
        if (!(kRho >= 0) || double.IsNaN(kRho))
            throw new ArgumentOutOfRangeException(nameof(kRho), kRho, "k_ρ must be finite and ≥ 0.");

        int n = stack.LayerCount;
        var tau = new double[n + 2];
        tau[0] = tau[n + 1] = 1.0;                       // the semi-infinite ends carry no round trip
        for (int i = 1; i <= n; i++) tau[i] = Math.Exp(-kRho * stack.Layers[i - 1].ThicknessM);

        var r = new Complex[n + 1];
        for (int i = 0; i <= n; i++) r[i] = InterfaceCoefficient(stack, scalar, i);

        var down = new Complex[n + 1];
        down[0] = stack.Bottom.Kind switch
        {
            TerminationKind.Pec => -Complex.One,
            TerminationKind.Pmc =>  Complex.One,
            _                   =>  r[0],
        };
        for (int i = 1; i <= n; i++)
        {
            Complex x = down[i - 1] * tau[i] * tau[i];
            down[i] = (r[i] + x) / (Complex.One + r[i] * x);
        }

        var up = new Complex[n + 1];
        up[n] = stack.Top.Kind switch
        {
            TerminationKind.Pec => -Complex.One,
            TerminationKind.Pmc =>  Complex.One,
            _                   => -r[n],                // an interface seen from BELOW is −r
        };
        for (int i = n - 1; i >= 0; i--)
        {
            Complex x = up[i + 1] * tau[i + 1] * tau[i + 1];
            up[i] = (-r[i] + x) / (Complex.One - r[i] * x);
        }

        return new Cascade(stack, scalar, kRho, tau, r, down, up);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // The spectral kernel
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>G̃(k_ρ; z, z′)</b> for the scalar (electrostatic) problem, normalised so that
    /// <c>G(ρ) = (1/4π)∫₀^∞ G̃ J₀(k_ρ ρ) dk_ρ</c> and the free-space value is <c>1/(4πρ)</c>.
    /// </summary>
    public static Complex SpectralScalar(LayerStack stack, double kRho, double z, double zp)
        => Spectral(Build(stack, scalar: true, kRho), z, zp);

    /// <summary>G̃(k_ρ; z, z′) for the vector (magnetostatic) problem, same normalisation.</summary>
    public static Complex SpectralVector(LayerStack stack, double kRho, double z, double zp)
        => Spectral(Build(stack, scalar: false, kRho), z, zp);

    /// <summary>
    /// The spectral kernel from an already-built <see cref="Cascade"/> — the form the spatial
    /// inversion calls, because the ladder is per-k and the (z, z′) pair is not.
    /// </summary>
    public static Complex Spectral(Cascade c, double z, double zp)
    {
        ArgumentNullException.ThrowIfNull(c);
        var stack = c.Stack;
        RequireHeightsSolvable(stack, z, zp);

        double zl = Math.Min(z, zp), zu = Math.Max(z, zp);
        int m = stack.RegionOf(zl), n = stack.RegionOf(zu);

        // The Wronskian is constant in z, so it may be read in whichever region is convenient; it is
        // read in z_>'s, which is what makes the inter-region factor a ψ↓ ratio and nothing else.
        Complex denom = Denominator(c, n);

        // SameRegionNumerator has already cancelled region n's own τ against the Wronskian's; the
        // cross-region product has not, so it carries it here.
        return m == n
            ? SameRegionNumerator(c, n, zl, zu) / denom
            : ScaleRatio(c, m, n) * PsiDown(c, m, zl) * PsiUp(c, n, zu) / (denom * TauOf(c, n));
    }

    /// <summary>
    /// <c>W / (−2k a_i b_i)</c> for region i — the Wronskian with ψ↓ and ψ↑'s own per-region scales
    /// divided out, which is what <see cref="SameRegionNumerator"/> and <see cref="ScaleRatio"/>
    /// are both expressed against. The layer's own <c>τ</c> is absent because
    /// <see cref="SameRegionNumerator"/> has already cancelled it.
    /// </summary>
    private static Complex Denominator(Cascade c, int region)
    {
        int n = c.Stack.LayerCount;
        Complex p = Weight(c.Stack, c.Scalar, region);
        if (region == 0 || region == n + 1) return p;
        double tau = c.Tau[region];
        return p * (Complex.One - c.Down[region - 1] * c.Up[region] * tau * tau);
    }

    /// <summary>
    /// <b>The same-region product, expanded so no exponential is ever divided by another.</b>
    ///
    /// <para>Writing ψ↓ψ↑ out and cancelling the layer's own <c>τ</c> against the Wronskian's leaves
    /// four terms whose exponents are the four classical distances — the direct one, the image in
    /// the region's floor, the image in its ceiling, and the once-round-trip image — each
    /// non-negative by construction. The obvious form <c>ψ↓ψ↑/(Pτ(1−Γ↓Γ↑τ²))</c> is the same number
    /// and overflows for a thin layer at large k.</para>
    /// </summary>
    private static Complex SameRegionNumerator(Cascade c, int region, double zl, double zu)
    {
        var stack = c.Stack;
        double k = c.K;
        int n = stack.LayerCount;
        double delta = zu - zl;

        if (region == n + 1)                                  // the top half-space
        {
            double top = stack.InterfaceZ[n];
            return Math.Exp(-k * delta) + c.Down[n] * Math.Exp(-k * (zl + zu - 2 * top));
        }
        if (region == 0)                                      // the bottom half-space
        {
            double bot = stack.InterfaceZ[0];
            return Math.Exp(-k * delta) + c.Up[0] * Math.Exp(-k * (2 * bot - zl - zu));
        }

        double lo = stack.InterfaceZ[region - 1], hi = stack.InterfaceZ[region];
        double d  = hi - lo;
        Complex gd = c.Down[region - 1], gu = c.Up[region];

        return Math.Exp(-k * delta)
             + gd      * Math.Exp(-k * (zl + zu - 2 * lo))
             + gu      * Math.Exp(-k * (2 * hi - zl - zu))
             + gd * gu * Math.Exp(-k * (2 * d - delta));
    }

    /// <summary>ψ↓ in region i, normalised to its own region so both exponentials decay.</summary>
    private static Complex PsiDown(Cascade c, int i, double z)
    {
        var stack = c.Stack; double k = c.K; int n = stack.LayerCount;
        if (i == 0) return Math.Exp(-k * (stack.InterfaceZ[0] - z));
        if (i == n + 1)
        {
            double u = z - stack.InterfaceZ[n];
            return Math.Exp(k * u) + c.Down[n] * Math.Exp(-k * u);
        }
        double lo = stack.InterfaceZ[i - 1], hi = stack.InterfaceZ[i];
        return Math.Exp(-k * (hi - z)) + c.Down[i - 1] * c.Tau[i] * Math.Exp(-k * (z - lo));
    }

    /// <summary>ψ↑ in region i, likewise.</summary>
    private static Complex PsiUp(Cascade c, int i, double z)
    {
        var stack = c.Stack; double k = c.K; int n = stack.LayerCount;
        if (i == n + 1) return Math.Exp(-k * (z - stack.InterfaceZ[n]));
        if (i == 0)
        {
            double v = stack.InterfaceZ[0] - z;
            return Math.Exp(k * v) + c.Up[0] * Math.Exp(-k * v);
        }
        double lo = stack.InterfaceZ[i - 1], hi = stack.InterfaceZ[i];
        return Math.Exp(-k * (z - lo)) + c.Up[i] * c.Tau[i] * Math.Exp(-k * (hi - z));
    }

    /// <summary>
    /// <b>a_m/a_n — ψ↓'s scale in region m relative to region n, m ≤ n.</b> See the class remarks:
    /// the naive matching ratio's vanishing factor is cancelled analytically here, which is the
    /// difference between a denominator bounded below by <c>1 − |r|</c> and one that is exactly zero
    /// at k = 0 over a PEC floor.
    /// </summary>
    private static Complex ScaleRatio(Cascade c, int m, int n)
    {
        Complex ratio = Complex.One;
        for (int i = m; i < n; i++)
        {
            double tauAbove = TauOf(c, i + 1);
            ratio *= i == 0
                ? tauAbove * (Complex.One + c.Down[0])
                : tauAbove * (Complex.One + c.R[i])
                           / (Complex.One + c.R[i] * c.Down[i - 1] * c.Tau[i] * c.Tau[i]);
        }
        return ratio;
    }

    private static double TauOf(Cascade c, int region)
        => region >= 1 && region <= c.Stack.LayerCount ? c.Tau[region] : 1.0;

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // Refusals — R-mom-17: a height this formulation cannot answer is refused by name.
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The one structural restriction left: a point cannot be INSIDE a solid wall. A PEC or PMC
    /// termination is a boundary, not a half-space, so there is no region beyond it to hold a
    /// source — unlike <see cref="LayeredStaticGreens"/>'s restriction, which was about where the
    /// inverse transform was referenced and is what this class removes.
    /// </summary>
    public static EmSuitability CanEvaluateAt(LayerStack stack, double z)
    {
        ArgumentNullException.ThrowIfNull(stack);
        int region = stack.RegionOf(z);
        if (region == 0 && stack.Bottom.Kind != TerminationKind.HalfSpace)
            return EmSuitability.No(
                $"z = {z:G6} m is BELOW the stack's bottom termination at z = " +
                $"{stack.InterfaceZ[0]:G6} m, which is a {stack.Bottom} — a solid wall, not a " +
                "half-space. There is no region inside it for a source or an observer to sit in. " +
                "Heights at or above the wall are fine, interior ones included.");
        if (region == stack.RegionCount - 1 && stack.Top.Kind != TerminationKind.HalfSpace)
            return EmSuitability.No(
                $"z = {z:G6} m is ABOVE the stack's top termination at z = {stack.TopZ:G6} m, " +
                $"which is a {stack.Top} — a solid wall, not a half-space. There is no region " +
                "inside it for a source or an observer to sit in.");
        return EmSuitability.Yes;
    }

    private static void RequireHeightsSolvable(LayerStack stack, double z, double zp)
    {
        foreach (double h in new[] { z, zp })
        {
            var ok = CanEvaluateAt(stack, h);
            if (!ok.Ok) throw new ArgumentOutOfRangeException(nameof(z), h, ok.Reason);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════
    // MIM-4 / milestone 2 — the spatial function
    // ═══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A height snapped onto an interface it is within rounding of.</b> A conductor level's z is
    /// accumulated from stackup thicknesses and a <see cref="LayerStack"/>'s interface z from layer
    /// thicknesses, so the two agree to ULPs and not to the bit — and <see cref="LayerStack.RegionOf"/>
    /// puts a point one ULP below an interface in the region BELOW it, which is the wrong side of
    /// the metal. <c>PlanarProblem.CanSolve</c> already requires every level to be on an interface,
    /// so snapping asserts what is already true rather than papering over anything.
    /// </summary>
    public static double SnapToInterface(LayerStack stack, double z)
    {
        ArgumentNullException.ThrowIfNull(stack);
        double tol = 1e-12 * Math.Max(1.0, stack.TopZ);
        foreach (double zi in stack.InterfaceZ)
            if (Math.Abs(z - zi) <= tol) return zi;
        return z;
    }

    /// <summary>
    /// <b>The k → ∞ limit of G̃</b>, which is the coefficient of the spatial function's <c>1/ρ</c>
    /// singularity — <c>(1 + Γ↓_∞)/P</c> for two points sharing a height ON an interface, <c>1/P</c>
    /// for two points sharing a height inside a layer, and ZERO whenever the two heights differ,
    /// because a cross-level kernel has no singularity at all (§3.5's "different levels ⇒ smooth").
    ///
    /// <para>On the one-slab top surface this is <c>(1 + K)/ε_air = 1 + K</c> — exactly the
    /// <c>Inverse</c> coefficient <see cref="PlanarKernelTerms.StaticScalar"/> carries, which is the
    /// arithmetic tying the two together.</para>
    /// </summary>
    public static Complex AsymptoticConstant(LayerStack stack, bool scalar, double z, double zp)
    {
        ArgumentNullException.ThrowIfNull(stack);
        RequireHeightsSolvable(stack, z, zp);
        if (z != zp) return Complex.Zero;

        int i = stack.RegionOf(z);
        Complex p = Weight(stack, scalar, i);
        if (i == 0) return Complex.One / p;
        if (z != stack.InterfaceZ[i - 1]) return Complex.One / p;

        // Γ↓ at that interface with every round trip killed: the interface's own coefficient, or the
        // wall's when the interface IS the bottom termination.
        Complex gInf = i - 1 == 0
            ? stack.Bottom.Kind switch
              {
                  TerminationKind.Pec => -Complex.One,
                  TerminationKind.Pmc =>  Complex.One,
                  _                   =>  InterfaceCoefficient(stack, scalar, 0),
              }
            : InterfaceCoefficient(stack, scalar, i - 1);
        return (Complex.One + gInf) / p;
    }

    /// <summary>
    /// The two length scales the spectral remainder lives between: the SMALLEST image depth present
    /// (which sets how far in k the integrand survives) and the LARGEST (which sets how fast it
    /// varies near k = 0). Both are geometric, so neither is guessed.
    /// </summary>
    internal static (double Min, double Max) DecayScales(LayerStack stack, double z, double zp)
    {
        double bottom = stack.InterfaceZ[0];
        double thinnest = double.PositiveInfinity;
        foreach (var l in stack.Layers) thinnest = Math.Min(thinnest, l.ThicknessM);

        double delta = Math.Abs(z - zp);
        double min = 2.0 * thinnest;
        if (delta > 0) min = Math.Min(min, delta);

        double max = 2.0 * (stack.TopZ - bottom)
                   + Math.Abs(z - bottom) + Math.Abs(zp - bottom);
        if (double.IsInfinity(min) || !(min > 0)) min = max > 0 ? max : 1.0;
        if (!(max > min)) max = 2.0 * min;
        return (min, max);
    }

    /// <summary>
    /// <b>The REFERENCE inverse transform: direct numerical Hankel integration of the spectral
    /// remainder.</b> <c>G(ρ) = (1/4π)[c_∞/ρ + ∫₀^∞ (G̃ − c_∞) J₀(k ρ) dk]</c>, with the panel
    /// boundaries at the zeros of <c>J₀(kρ)</c> so no panel straddles an oscillation and on a
    /// GEOMETRIC ladder besides, because the integrand carries two length scales that can differ by
    /// three orders of magnitude on a thin-film stack and a uniform partition sized for the finer
    /// one costs a quarter of a million panels.
    ///
    /// <para><b>This is the oracle, not the production method</b> — it shares no approximation with
    /// <see cref="InteriorStaticImages"/>, which is what makes it worth its cost, and it is far too
    /// slow to sit under a fill. It refuses by name rather than grinding when the partition it would
    /// need is absurd.</para>
    /// </summary>
    public static Complex PotentialByQuadrature(LayerStack stack, bool scalar, double rhoM,
                                                double z, double zp, double relTol = 1e-11,
                                                int maxPanels = 400_000)
    {
        ArgumentNullException.ThrowIfNull(stack);
        if (!(rhoM > 0)) throw new ArgumentOutOfRangeException(nameof(rhoM), rhoM, "ρ must be positive.");

        z = SnapToInterface(stack, z);
        zp = SnapToInterface(stack, zp);
        Complex cInf = AsymptoticConstant(stack, scalar, z, zp);
        Complex Remainder(double k) => Spectral(Build(stack, scalar, k), z, zp) - cInf;

        Complex closed = cInf / rhoM;
        var (aMin, aMax) = DecayScales(stack, z, zp);

        // How far in k the remainder survives, MEASURED rather than assumed from aMin: it is not
        // monotone, so three probes past the candidate are required before it is believed.
        double scale = Math.Max(Remainder(0).Magnitude, cInf.Magnitude);
        if (!(scale > 0)) return closed / (4.0 * Math.PI);
        double floor = relTol * scale * 1e-3;

        double kMax = 1.0 / aMin;
        while (kMax < 1e300 &&
               (Remainder(kMax).Magnitude > floor ||
                Remainder(1.3 * kMax).Magnitude > floor ||
                Remainder(1.7 * kMax).Magnitude > floor))
            kMax *= 2.0;

        var cuts = new SortedSet<double> { 0.0, kMax };
        for (double k = 0.25 / aMax; k < kMax; k *= 1.3) cuts.Add(k);
        for (int m = 1; ; m++)
        {
            double zeroAt = SommerfeldIntegral.BesselZero(m) / rhoM;
            if (zeroAt >= kMax) break;
            cuts.Add(zeroAt);
            if (cuts.Count > maxPanels) break;
        }
        if (cuts.Count > maxPanels)
            throw new InvalidOperationException(
                $"The reference integrator would need more than {maxPanels:N0} panels at ρ = " +
                $"{rhoM:G4} m on this stack (k runs to {kMax:G4}/m and J₀(kρ) oscillates " +
                $"{kMax * rhoM / Math.PI:N0} times over that). This is InteriorStaticGreens' " +
                "ORACLE, deliberately built to share no approximation with the production path and " +
                "not to be fast; the image model (InteriorStaticImages.Fit) evaluates the same " +
                "function at any ρ in closed form. Compare them at a smaller ρ, or use the model.");

        var ordered = cuts.ToArray();
        Complex sum = Complex.Zero;
        double tol = relTol * Math.Max(Math.Abs(closed.Real), scale / Math.Max(kMax, 1.0));
        for (int i = 0; i + 1 < ordered.Length; i++)
            sum += Adaptive(k => Remainder(k) * Bessel.J0(k * rhoM), ordered[i], ordered[i + 1], tol, 8);

        return (closed + sum) / (4.0 * Math.PI);
    }

    // A small adaptive Gauss-Legendre, private here for the same reason LayeredStaticGreens and
    // SommerfeldIntegral each keep one: a dozen lines, and three different lifetimes.
    private static Complex Adaptive(Func<double, Complex> f, double a, double b, double tol, int depth)
    {
        Complex whole = Gauss(f, a, b, 12);
        double mid = 0.5 * (a + b);
        Complex halves = Gauss(f, a, mid, 12) + Gauss(f, mid, b, 12);
        if (depth <= 0 || (whole - halves).Magnitude <= tol) return halves;
        return Adaptive(f, a, mid, 0.5 * tol, depth - 1) + Adaptive(f, mid, b, 0.5 * tol, depth - 1);
    }

    private static Complex Gauss(Func<double, Complex> f, double a, double b, int n)
    {
        var (x, w) = GaussNodes(n);
        double half = 0.5 * (b - a), mid = 0.5 * (a + b);
        Complex s = Complex.Zero;
        for (int i = 0; i < n; i++) s += w[i] * f(mid + half * x[i]);
        return s * half;
    }

    private static readonly Dictionary<int, (double[] X, double[] W)> NodeCache = new();

    private static (double[] X, double[] W) GaussNodes(int n)
    {
        lock (NodeCache)
        {
            if (NodeCache.TryGetValue(n, out var hit)) return hit;
            var x = new double[n];
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                double zz = Math.Cos(Math.PI * (i + 0.75) / (n + 0.5)), pp = 0;
                for (int it = 0; it < 200; it++)
                {
                    double p0 = 1, p1 = 0;
                    for (int j = 0; j < n; j++)
                    {
                        double p2 = p1;
                        p1 = p0;
                        p0 = ((2 * j + 1) * zz * p1 - j * p2) / (j + 1);
                    }
                    pp = n * (zz * p0 - p1) / (zz * zz - 1);
                    double dz = p0 / pp;
                    zz -= dz;
                    if (Math.Abs(dz) < 1e-16) break;
                }
                x[i] = zz;
                w[i] = 2.0 / ((1 - zz * zz) * pp * pp);
            }
            NodeCache[n] = (x, w);
            return (x, w);
        }
    }
}
