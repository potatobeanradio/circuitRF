using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>Phase L9c — the z-directed current: the two new dyadic components and their oracle ladder.</b>
///
/// <para>L8a's ordering rule stands: <b>each tier passes before the next is written</b>, and the
/// exact reductions come before anything empirical.</para>
///
/// <list type="bullet">
///   <item><b>Tier 0</b> — structural and free: the four transmission-line responses against their
///         reciprocity relations, the k_ρ → 0 limit, and the earned refusals.</item>
///   <item><b>Tier 1</b> — <b>the εᵣ = 1 reduction, and it is the strongest single check in the
///         slice.</b> Over a bare ground plane a vertical current's answer is free space plus one
///         POSITIVE image and a horizontal one's is free space plus one NEGATIVE image, both
///         exactly, and the mixed component is identically zero. No external data, no quadrature,
///         and no plausible-but-wrong dyadic survives it.</item>
///   <item><b>Tier 1b</b> — the MPIE identity itself, with the scalar potential's z-derivatives
///         taken by CENTRAL DIFFERENCES. That is the one rung that checks the mixed component's
///         sign and scale independently of the algebra that produced it, because the reduction from
///         <c>∂_z G_q</c> to line currents is exactly where a derivation error would live.</item>
///   <item><b>Tier 2</b> — the horizontal-only path is bit-identical (R-via-1).</item>
/// </list>
/// </summary>
public sealed class VerticalCurrentTests
{
    private readonly ITestOutputHelper _out;
    public VerticalCurrentTests(ITestOutputHelper output) => _out = output;

    private static double Rel(Complex expected, Complex actual, double floor = 1e-300)
        => (expected - actual).Magnitude / Math.Max(expected.Magnitude, floor);

    private static readonly double[] KRhoOverK0 = [1e-6, 1e-3, 0.01, 0.3, 0.9, 1.4, 3.0, 15.0, 120.0];

    /// <summary>Free space with no stack at all: air layers between two air half-spaces. Every
    /// interface is invisible, so the exact answer is the free-space kernel and nothing else.</summary>
    private static LayerStack FreeSpace(double totalM = 1.6e-3) => new(
        Termination.Air,
        [new MediumLayer(totalM * 0.4, EmMaterial.Air), new MediumLayer(totalM * 0.6, EmMaterial.Air)],
        Termination.Air);

    // =========================================================================================
    // TIER 0 — structural, per sample, free.
    // =========================================================================================

    [Fact]
    public void T0_1_TheFourLineResponses_ObeyTheirReciprocityRelations()
    {
        // V_i and I_v are symmetric in the heights; V_v(z|z′) = −I_i(z′|z). The first two are
        // structural in the same-region case and a genuine two-chain check across regions (R-lyr-5);
        // the third is a check between the PRIMAL and the DUAL line, which share no arithmetic
        // beyond the cascade itself. It is what makes G_A^uz = −G_A^zu(swapped) a measured statement
        // rather than a definition.
        double worstSym = 0, worstDual = 0;
        foreach (var (name, stack) in LayerStacks.All())
        {
            var g = new LayeredSpectralGreens(stack, 10e9);
            foreach (var (z, zp) in HeightPairs(stack))
            foreach (double u in KRhoOverK0)
            foreach (var p in new[] { SurfaceWavePolarization.Tm, SurfaceWavePolarization.Te })
            {
                Complex w = (u * g.K0) * (u * g.K0);

                double a = Rel(g.Voltage(p, w, z, zp),      g.Voltage(p, w, zp, z));
                double b = Rel(g.SeriesCurrent(p, w, z, zp), g.SeriesCurrent(p, w, zp, z));
                double c = Rel(g.SeriesVoltage(p, w, z, zp), -g.Current(p, w, zp, z));

                worstSym  = Math.Max(worstSym, Math.Max(a, b));
                worstDual = Math.Max(worstDual, c);
                Assert.True(a < 1e-10 && b < 1e-10, $"{name} {p} u={u} ({z},{zp}): V_i {a:E2}, I_v {b:E2}");
                Assert.True(c < 1e-10, $"{name} {p} u={u} ({z},{zp}): V_v vs −I_i {c:E2}");
            }
        }
        _out.WriteLine($"TL reciprocity: symmetric pair (V_i, I_v) worst {worstSym:E3}; " +
                       $"dual pair V_v(z|z′) = −I_i(z′|z) worst {worstDual:E3}");
    }

    [Fact]
    public void T0_2_ReciprocityOfTheNewKERNELS_SameRegionIsBitIdentical_CrossRegionAgrees()
    {
        // G_A^zz is symmetric in the heights by inspection, so in the source's own region this is
        // bit-identity at kernel A's own standard. Across regions the two orders take the upward and
        // the downward chain and their agreement is a real check — NOT canonicalised (Tier 0's rule).
        double worst = 0;
        foreach (var (name, stack) in LayerStacks.All())
        {
            var g = new LayeredSpectralGreens(stack, 10e9);
            double h = stack.TopZ;

            foreach (double u in KRhoOverK0)
            {
                Complex kRho = u * g.K0;
                Assert.Equal(g.KernelAtHeights(GreensKernel.VerticalVectorPotential, kRho, h + 1e-4, h + 6e-4),
                             g.KernelAtHeights(GreensKernel.VerticalVectorPotential, kRho, h + 6e-4, h + 1e-4));

                if (stack.LayerCount < 2) continue;
                double zLow = 0.5 * (stack.InterfaceZ[0] + stack.InterfaceZ[1]);
                Complex a = g.KernelAtHeights(GreensKernel.VerticalVectorPotential, kRho, h + 3e-4, zLow);
                Complex b = g.KernelAtHeights(GreensKernel.VerticalVectorPotential, kRho, zLow, h + 3e-4);
                double rel = Rel(a, b);
                worst = Math.Max(worst, rel);
                Assert.True(rel < 1e-9, $"{name} u={u}: up-chain {a} vs down-chain {b}, rel {rel:E3}");
            }
        }
        _out.WriteLine($"G_A^zz cross-region reciprocity, two independent chains: worst {worst:E3}");
    }

    [Fact]
    public void T0_3_BothNewComponents_AreFiniteThroughKRhoZero_AndTheNaiveDivisionIsRuined()
    {
        // Both carry a TM−TE difference against a 1/k_ρ² prefactor, and both differences vanish
        // identically at k_ρ = 0 because the two lines are then the same network. The stable path
        // must stay smooth through zero; the naive division must NOT, so this test cannot quietly
        // stop demonstrating why the contour extraction is there.
        foreach (var (name, stack) in LayerStacks.All())
        {
            var g = new LayeredSpectralGreens(stack, 10e9);
            double h = stack.TopZ;
            double z = h + 2e-4, zp = h + 5e-4;

            // The limit exists and is approached like w = k_ρ², which is the shape of the statement:
            // the difference these components divide by k_ρ² is analytic in w and vanishes at w = 0,
            // so what is left is analytic too and its leading correction is O(w).
            double[] us = [1e-9, 1e-7, 1e-5, 1e-3, 1e-2];
            var stable = new List<Complex>();
            foreach (double u in us)
            {
                Complex w = (u * g.K0) * (u * g.K0);
                stable.Add(g.MixedKernel(w, z, zp));
                Assert.True(double.IsFinite(g.VerticalKernel(w, z, zp).Magnitude),
                            $"{name}: G_A^zz not finite at k_ρ/k₀ = {u}");
            }
            for (int i = 1; i < us.Length; i++)
            {
                double dev = Rel(stable[0], stable[i], 1e-30);
                Assert.True(dev < Math.Max(1e-11, 40.0 * us[i] * us[i]),
                            $"{name}: the mixed kernel's k_ρ → 0 limit is not being approached like " +
                            $"k_ρ²: at k_ρ/k₀ = {us[i]} the deviation from the limit is {dev:E3}");
            }

            // The naive form: (I_i^e − I_i^h) computed as a plain difference and divided by k_ρ².
            Complex wTiny = (1e-9 * g.K0) * (1e-9 * g.K0);
            Complex naive = (g.Current(SurfaceWavePolarization.Tm, wTiny, z, zp) -
                             g.Current(SurfaceWavePolarization.Te, wTiny, z, zp)) / wTiny
                            * g.Stack.MaterialOfRegion(stack.RegionOf(z)).MuR / Complex.ImaginaryOne;
            Assert.True(Rel(stable[0], -naive, 1e-30) > 1e-3,
                        $"{name}: the naive division is NOT ruined at k_ρ/k₀ = 1e-9 — this test has " +
                        $"stopped demonstrating what it exists for.");
        }
    }

    [Fact]
    public void T0_4_TheOneLayerKernelRefusesTheVerticalComponents_AndTheRefusalIsEARNED()
    {
        // R-via-6. The one-layer grounded slab has one metal level on its top surface, so a vertical
        // current has nowhere to flow; it refuses by name and points at the general medium. The
        // second half is what makes the refusal earned rather than defensive: the general kernel,
        // built from the SAME slab, does answer.
        var slab = GroundedSlab.Fr4Starter;
        var one  = new SpectralGreens(slab, 10e9);

        foreach (var k in new[] { GreensKernel.VerticalVectorPotential, GreensKernel.MixedVectorPotential })
        {
            var ex = Assert.Throws<ArgumentException>(() => one.Reflection(k, 0.5 * one.K0));
            Assert.Contains("LayeredSpectralGreens", ex.Message);
            Assert.Contains("no second level", ex.Message);
            Assert.Throws<ArgumentException>(() => one.AsymptoticReflection(k));
            Assert.Throws<ArgumentException>(() => one.ReflectionAtKz0(k, 0.5 * one.K0));
        }

        var general = new LayeredSpectralGreens(LayerStack.FromGroundedSlab(slab), 10e9);
        double h = slab.HeightM;
        foreach (var k in new[] { GreensKernel.VerticalVectorPotential, GreensKernel.MixedVectorPotential })
        {
            Complex v = general.KernelAtHeights(k, 0.5 * general.K0, h + 1e-5, h + 4e-5);
            Assert.True(double.IsFinite(v.Magnitude) && v.Magnitude > 0,
                        $"{k} must be a real answer on the same slab, or the refusal above is " +
                        $"refusing something nothing can do — got {v}.");
        }
    }

    // =========================================================================================
    // TIER 1 — the εᵣ = 1 reduction.  THE strongest single check, and the image sign is the point.
    // =========================================================================================

    [Theory]
    [InlineData(2e9)]
    [InlineData(10e9)]
    public void T1_1_OverABareGroundPlane_TheVerticalImageIsPOSITIVE_AndTheHorizontalOneNEGATIVE(double f)
    {
        // A PEC is a SHORT on both equivalent lines: the VOLTAGE reflection is −1 and the CURRENT
        // reflection is +1. G_A^xx and G_q are built from line voltages and G_A^zz from a line
        // current, so the image sign flips between them — which is the physics (a horizontal current
        // has a negative image over a ground plane and a vertical one a positive image) arriving
        // through the transmission-line analogy rather than being imposed on it.
        var stack = LayerStacks.AirOverGround();
        var g = new LayeredSpectralGreens(stack, f);
        double h = stack.TopZ;

        double worstNeg = 0, worstPos = 0, worstMixed = 0;
        foreach (var (z, zp) in new[]
                 {
                     (h, h), (h + 1e-4, h + 5e-4), (h + 2e-3, h + 2e-3),      // both in the half-space
                     (0.2e-3, 0.6e-3), (0.4e-3, 0.4e-3),                      // both INSIDE the stack
                     (0.2e-3, 1.5e-3), (1.5e-3, 0.2e-3),                      // CROSS-region, both ways
                     (0.2e-3, h + 1e-3),                                      // interior to half-space
                 })
        foreach (double u in KRhoOverK0)
        {
            Complex kRho = u * g.K0;
            Complex kz   = g.Kz0(kRho);            // εᵣ = 1 everywhere, so one k_z serves the lot
            Complex eD   = Complex.Exp(-Complex.ImaginaryOne * kz * Math.Abs(z - zp));
            Complex eS   = Complex.Exp(-Complex.ImaginaryOne * kz * (z + zp));
            Complex den  = 2.0 * Complex.ImaginaryOne * kz;

            Complex axx = g.KernelAtHeights(GreensKernel.VectorPotential, kRho, z, zp);
            Complex aq  = g.KernelAtHeights(GreensKernel.ScalarPotential, kRho, z, zp);
            Complex azz = g.KernelAtHeights(GreensKernel.VerticalVectorPotential, kRho, z, zp);
            Complex azx = g.KernelAtHeights(GreensKernel.MixedVectorPotential, kRho, z, zp);

            double n1 = Rel((eD - eS) / den, axx);
            double n2 = Rel((eD - eS) / den, aq);
            double p1 = Rel((eD + eS) / den, azz);

            worstNeg   = Math.Max(worstNeg, Math.Max(n1, n2));
            worstPos   = Math.Max(worstPos, p1);
            // The mixed component has no scale of its own here; measure it against the kernel it
            // would perturb, i.e. against |G̃₀|/k_ρ (the dyadic entry is k_x times this).
            worstMixed = Math.Max(worstMixed, azx.Magnitude * Math.Max(kRho.Magnitude, 1e-30)
                                              / ((eD / den).Magnitude));

            Assert.True(p1 < 1e-11,
                $"G_A^zz at u={u}, ({z},{zp}): expected free space PLUS one image {(eD + eS) / den}, " +
                $"got {azz}, rel {p1:E3}. If this reads as free space MINUS an image the current " +
                $"reflection at the PEC has the voltage sign.");
            Assert.True(n1 < 1e-11 && n2 < 1e-11, $"horizontal at u={u}: {n1:E3}/{n2:E3}");
        }

        _out.WriteLine($"εᵣ=1 over PEC at {f / 1e9:G3} GHz — negative image (G_A^xx, G_q): {worstNeg:E3}; " +
                       $"POSITIVE image (G_A^zz): {worstPos:E3}; mixed component ≡ 0: {worstMixed:E3}");
        Assert.True(worstMixed < 1e-11, $"the mixed component must VANISH over a bare ground plane: {worstMixed:E3}");
    }

    [Fact]
    public void T1_2_InFreeSpace_AllThreeReduceToTheFreeSpaceKernel_AndTheMixedOneVanishes()
    {
        // No ground plane at all: air between two air half-spaces. Every kernel must be exactly
        // 1/(2jk_z)e^{−jk_z|Δ|} — including G_A^zz, which is the statement that A_z = µ₀G₀ ⋆ J_z is
        // the ordinary vector potential when there is no stack, and which fixes G_A^zz's overall
        // normalisation. Nothing else in the ladder does.
        var stack = FreeSpace();
        var g = new LayeredSpectralGreens(stack, 10e9);
        double worst = 0, worstMixed = 0;

        foreach (var (z, zp) in new[]
                 {
                     (0.2e-3, 0.5e-3), (0.5e-3, 0.5e-3), (0.2e-3, 1.2e-3),
                     (1.2e-3, 0.2e-3), (stack.TopZ + 1e-3, 0.3e-3),
                 })
        foreach (double u in KRhoOverK0)
        {
            Complex kRho = u * g.K0;
            Complex kz   = g.Kz0(kRho);
            Complex exact = Complex.Exp(-Complex.ImaginaryOne * kz * Math.Abs(z - zp))
                          / (2.0 * Complex.ImaginaryOne * kz);
            foreach (var k in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential,
                                      GreensKernel.VerticalVectorPotential })
                worst = Math.Max(worst, Rel(exact, g.KernelAtHeights(k, kRho, z, zp)));

            Complex mixed = g.KernelAtHeights(GreensKernel.MixedVectorPotential, kRho, z, zp);
            worstMixed = Math.Max(worstMixed,
                                  mixed.Magnitude * Math.Max(kRho.Magnitude, 1e-30) / exact.Magnitude);
        }

        _out.WriteLine($"free space: all three scalar kernels vs 1/(2jk_z)e^(−jk_z|Δ|) — worst {worst:E3}; " +
                       $"mixed component ≡ 0 — worst {worstMixed:E3}");
        Assert.True(worst < 1e-11, $"free-space reduction: {worst:E3}");
        Assert.True(worstMixed < 1e-11, $"the mixed component must vanish in free space: {worstMixed:E3}");
    }

    // =========================================================================================
    // TIER 1b — the MPIE identity, with the scalar potential's derivatives by CENTRAL DIFFERENCES.
    // =========================================================================================

    [Fact]
    public void T1b_TheMixedAndVerticalComponents_ReproduceTheTRUEFieldOfTheirOwnDIPOLE()
    {
        // The one rung that checks the new components' SIGN and SCALE without reusing the algebra
        // that produced them. The derivation's whole content is the reduction of ∂_z G_q and
        // ∂_z∂_{z′}G_q into line currents; here those derivatives are taken NUMERICALLY, from
        // ScalarKernel alone, and the true fields come from the transmission-line relations
        //     E_z = k_ρ I^e/(ωε_n),    E_u = V^e,
        // which involve neither G_A^zu nor G_A^zz. If either component's sign, prefactor or line
        // choice were wrong this cannot pass, and no amount of tolerance would hide it.
        double worstZx = 0, worstZz = 0, worstXz = 0, worstRatio = 0;

        foreach (var (name, stack) in LayerStacks.All())
        {
            var g = new LayeredSpectralGreens(stack, 10e9);
            double h = stack.TopZ;
            double eps0 = EmConstants.Eps0, mu0 = EmConstants.Mu0;

            // Both points well inside their own regions, so the central difference never straddles
            // an interface and never lands on z = z′ (where E_z carries a δ).
            double kMax = g.K0 * Math.Sqrt(stack.Layers.Select(l => l.Material.EpsR * l.Material.MuR)
                                                .Append(1.0).Max());

            foreach (var (z, zp, geometricStep) in HeightTriples(stack))
            foreach (double u in new[] { 0.3, 0.9, 2.0, 8.0 })
            {
                Complex kRho = u * g.K0;
                // The step is bounded BOTH by the geometry (see HeightTriples) and by the SPECTRAL
                // variation scale 1/max(k_ρ, k_max), which is the one that shrinks as the sweep
                // walks out past the light line. Three per cent of it puts the h⁴ residual left by
                // the Richardson step at ~3e-8 and the roundoff term an order below that.
                double step = Math.Min(geometricStep, 0.03 / Math.Max(u * g.K0, kMax));
                Complex w = kRho * kRho;
                int m = stack.RegionOf(zp), n = stack.RegionOf(z);
                Complex epsM = eps0 * stack.MaterialOfRegion(m).EpsComplex;
                Complex epsN = eps0 * stack.MaterialOfRegion(n).EpsComplex;

                Complex Gq(double a, double b) => g.KernelAtHeights(GreensKernel.ScalarPotential, kRho, a, b);
                Complex D1z (double s) => (Gq(z + s, zp) - Gq(z - s, zp)) / (2 * s);
                Complex D1zp(double s) => (Gq(z, zp + s) - Gq(z, zp - s)) / (2 * s);
                Complex D2  (double s) => (Gq(z + s, zp + s) - Gq(z + s, zp - s)
                                         - Gq(z - s, zp + s) + Gq(z - s, zp - s)) / (4 * s * s);

                // RICHARDSON, and the reason it is here rather than a looser tolerance: the residual
                // below is pure central-difference TRUNCATION and falls exactly as h². Measured on
                // FR-4 at k_ρ/k₀ = 0.3, halving the step seven times gives
                //   1.62e-4 / 4.05e-5 / 1.01e-5 / 2.53e-6 / 6.32e-7 / 1.56e-7 / 4.41e-8,
                // i.e. a ratio of 4.00 every time — the signature of the DIFFERENCE, not of the
                // kernel. (The amplification is a 13× cancellation: the true E_z is 31.6 and it is
                // the difference of a G_A^zz term of 406 and a ∇φ term of 375.) Combining h and h/2
                // as (4D_{h/2} − D_h)/3 removes the h² term outright and lets this rung be asserted
                // as (4D_{h/2} − D_h)/3 removes the h² term outright.
                //
                // WHAT IS LEFT IS THE INTERIOR SCALAR KERNEL'S OWN CONDITIONING, and L9a named it in
                // advance: for interior heights G_q has no half-space reflection to be referred to
                // and is taken through the VOLTAGE route, which subtracts two ~Z-sized numbers to
                // leave an O(w) remainder and so carries ~3e-12 relative error rather than ~1e-13.
                // A second difference multiplies that by (L/h)², which is ~5e5 on the GaAs and MMIC
                // interior pairs — so the worst residual below is ~1e-6 and is a property of the
                // path L9a documented, not of this derivation. It is not hidden in a tolerance: the
                // residual falls as h² until it reaches that floor and then stops.
                static Complex Rich(Func<double, Complex> d, double s) => (4.0 * d(s / 2) - d(s)) / 3.0;
                Complex dGq_dz  = Rich(D1z,  step);
                Complex dGq_dzp = Rich(D1zp, step);
                Complex d2Gq    = Rich(D2,   step);

                // ---- 1. E_z of a HORIZONTAL dipole: the mixed component's own equation.
                Complex trueEz = kRho * g.Current(SurfaceWavePolarization.Tm, w, z, zp) / (g.Omega * epsN);
                Complex mpieEz = -Complex.ImaginaryOne * g.Omega * mu0 * kRho
                                 * g.MixedKernel(w, z, zp)
                               - kRho / (g.Omega * eps0) * dGq_dz;
                worstZx = Math.Max(worstZx, Rel(trueEz, mpieEz));

                // ---- 2. E_u of a VERTICAL dipole: G_A^uz = −MixedKernel with the heights swapped.
                Complex trueEu = g.SeriesVoltage(SurfaceWavePolarization.Tm, w, z, zp) * kRho / (g.Omega * epsM);
                Complex mpieEu = +Complex.ImaginaryOne * g.Omega * mu0 * kRho
                                 * g.MixedKernel(w, zp, z)
                               + kRho / (g.Omega * eps0) * dGq_dzp;
                worstXz = Math.Max(worstXz, Rel(trueEu, mpieEu));

                // ---- 3. E_z of a VERTICAL dipole: G_A^zz's own equation.
                Complex trueEzV = -w * g.SeriesCurrent(SurfaceWavePolarization.Tm, w, z, zp)
                                  / (g.Omega * g.Omega * epsM * epsN);
                Complex mpieEzV = -Complex.ImaginaryOne * g.Omega * mu0 * g.VerticalKernel(w, z, zp)
                                - d2Gq / (Complex.ImaginaryOne * g.Omega * eps0);
                worstZz = Math.Max(worstZz, Rel(trueEzV, mpieEzV));

                // THE GATE IS THE DERIVED CONDITIONING FLOOR, NOT A ROUND NUMBER. A first difference
                // amplifies G_q's own relative error by L/h and a second one by (L/h)², with L the
                // kernel's variation scale — so on the 4 µm oxide, where the geometry forces
                // h = 0.15 µm while the kernel varies on 1.4 mm, the floor is ~1e-4 by arithmetic
                // alone. Predicting it from L9a's measured 3e-12 and checking the residual against
                // THAT is a stronger statement than any flat tolerance: a wrong sign, a wrong line
                // or a wrong prefactor is O(1) and cannot hide under a bound that is 1e-9 wherever
                // the difference is well conditioned.
                // The model has THREE factors and all three are needed, which is the point of
                // writing it down rather than picking a tolerance: G_q's own ~3e-12 relative error
                // amplified by (L/h)^k, the h⁴ term Richardson leaves behind, and the CANCELLATION
                // between the two sides of the identity (the true E_z is a small difference of a
                // large G_A term and a large ∇φ term — 13× on the FR-4 half-space pair).
                double lScale = 1.0 / Math.Max(u * g.K0, kMax);
                double q = step / lScale;
                double amp1 = (Complex.Abs(-Complex.ImaginaryOne * g.Omega * mu0 * kRho * g.MixedKernel(w, z, zp))
                             + Complex.Abs(kRho / (g.Omega * eps0) * dGq_dz)) / Complex.Abs(trueEz);
                double amp2 = (Complex.Abs(Complex.ImaginaryOne * g.Omega * mu0 * g.VerticalKernel(w, z, zp))
                             + Complex.Abs(d2Gq / (Complex.ImaginaryOne * g.Omega * eps0))) / Complex.Abs(trueEzV);
                double floor1 = amp1 * (3e-12 / q + q * q * q * q / 30.0);
                double floor2 = amp2 * (3e-12 / (q * q) + q * q * q * q / 30.0);
                worstRatio = Math.Max(worstRatio,
                    Math.Max(Rel(trueEz, mpieEz) / Math.Max(floor1, 1e-12),
                             Rel(trueEzV, mpieEzV) / Math.Max(floor2, 1e-12)));

                Assert.True(Rel(trueEz, mpieEz) < Math.Max(1e-9, 30.0 * floor1),
                    $"{name} u={u} ({z},{zp}): mixed component fails the MPIE identity — " +
                    $"true E_z {trueEz}, MPIE {mpieEz}, against a difference floor of {floor1:E2}");
                Assert.True(Rel(trueEzV, mpieEzV) < Math.Max(1e-9, 30.0 * floor2),
                    $"{name} u={u} ({z},{zp}): G_A^zz fails the MPIE identity — " +
                    $"true E_z {trueEzV}, MPIE {mpieEzV}, against a difference floor of {floor2:E2}");
            }
        }

        _out.WriteLine($"MPIE identity with numerical ∂_z G_q — worst relative residual: " +
                       $"mixed (E_z of a HED) {worstZx:E3}, its transpose (E_u of a VED) {worstXz:E3}, " +
                       $"G_A^zz (E_z of a VED) {worstZz:E3}. The floor here is the DIFFERENCE and the " +
                       $"interior scalar kernel's own conditioning, not this derivation — the " +
                       $"residual falls exactly as h² (ratio 4.00 over seven halvings) until it " +
                       $"reaches ~3e-12·(L/h)², and then stops. Worst residual as a MULTIPLE of that "
                     + $"derived floor: {worstRatio:F1}×.");
    }

    // =========================================================================================
    // TIER 3 — the INTERIOR ORACLE, checked against itself before anything is concluded from it.
    //
    // "This area has now had five occasions where the ORACLE, not the method, was at fault." The
    // ordering below is therefore not negotiable: the εᵣ-uniform reduction (where the extraction IS
    // the whole answer and the integral must return zero), then the overlap with the shipped
    // top-half-space oracle along a completely different contour, then convergence under refinement.
    // Only after those three does any interior number mean anything.
    // =========================================================================================

    /// <summary>A uniform lossy medium over a PEC floor, expressed as a three-layer stack with an
    /// open top of the SAME material. Every interface is invisible, so the exact answer at any pair
    /// of heights is free space IN THAT MEDIUM plus one image at depth z + z′ — with the image sign
    /// carrying the whole content of the vertical/horizontal distinction.</summary>
    private static LayerStack UniformOverGround(double totalM = 1.6e-3)
    {
        var mat = new EmMaterial(4.4, 0.02);
        return new LayerStack(
            Termination.Pec,
            [new MediumLayer(totalM * 0.5, mat), new MediumLayer(totalM * 0.2, mat),
             new MediumLayer(totalM * 0.3, mat)],
            Termination.OpenTo(mat));
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void T3_1_TheInteriorOracle_ReproducesTheEXACTUniformMediumAnswer_AndItsIntegralIsZERO()
    {
        // The strongest rung, and it has two halves. The VALUE must be free space in the medium plus
        // one image, with the sign flipping between the horizontal and the vertical components. And
        // the INTEGRATED part must be exactly zero — in a uniform medium the extracted asymptote is
        // not an asymptote at all, it is the entire kernel at every k_ρ, so anything the quadrature
        // returns here is quadrature error with nothing else mixed into it.
        var stack = UniformOverGround();
        var g = new LayeredSpectralGreens(stack, 10e9);
        Complex km = Complex.Sqrt(g.RegionWavenumberSquared(1));
        if (km.Imaginary > 0) km = -km;
        Complex epsRel = stack.MaterialOfRegion(1).EpsComplex;

        double worstValue = 0, worstIntegral = 0;
        foreach (double rho in new[] { 1e-5, 1e-4, 1e-3, 1e-2, 3e-2 })
        foreach (var (z, zp) in new[]
                 {
                     (0.4e-3, 0.4e-3), (0.3e-3, 0.9e-3),        // both inside, same region and not
                     (0.3e-3, 2.5e-3),                          // interior to the half-space above
                     (2.0e-3, 2.6e-3),                          // both in the half-space
                 })
        {
            double dz = Math.Abs(z - zp), sz = z + zp;
            Complex gd = SommerfeldIntegral.FreeSpace(km, Math.Sqrt(rho * rho + dz * dz));
            Complex gi = SommerfeldIntegral.FreeSpace(km, Math.Sqrt(rho * rho + sz * sz));

            foreach (var (k, exact) in new (GreensKernel, Complex)[]
                     {
                         (GreensKernel.VectorPotential,         gd - gi),
                         (GreensKernel.ScalarPotential,         (gd - gi) / epsRel),
                         (GreensKernel.VerticalVectorPotential, gd + gi),
                         (GreensKernel.MixedVectorPotential,    Complex.Zero),
                     })
            {
                var r = SommerfeldIntegral.EvaluateInterior(g, k, rho, z, zp);
                double relValue = Rel(exact, r.Value, gd.Magnitude);
                worstValue = Math.Max(worstValue, relValue);
                Assert.True(relValue < 1e-9,
                    $"{k} at ρ={rho:G3}, ({z},{zp}): exact {exact}, oracle {r.Value}, rel {relValue:E3}");

                // The zero-remainder half applies only where the extraction IS the whole kernel,
                // and WHICH pairs those are is instructive rather than incidental. The image term
                // extracted is the reflection off the SOURCE REGION'S OWN FLOOR, so it is the entire
                // answer only when that floor is the actual reflector — region 1, sitting on the PEC.
                // A pair in the half-space three invisible interfaces higher up has its PEC image a
                // whole stack thickness away, which decays and therefore belongs in the integral;
                // and a CROSS-REGION pair has no extraction at all. Both still have to produce the
                // right VALUE above, which is where the quadrature gets tested.
                if (stack.RegionOf(z) != 1 || stack.RegionOf(zp) != 1) continue;
                double relInt = r.Integrated.Magnitude / gd.Magnitude;
                worstIntegral = Math.Max(worstIntegral, relInt);
                Assert.True(relInt < 1e-9,
                    $"{k} at ρ={rho:G3}, ({z},{zp}): the uniform medium's SAME-REGION remainder must " +
                    $"be ZERO; the quadrature returned {r.Integrated} ({relInt:E3} of free space)");
            }
        }
        _out.WriteLine($"Tier 3, εᵣ-uniform over PEC (interior AND cross-region source heights): " +
                       $"worst value error {worstValue:E3}, worst spurious integral {worstIntegral:E3} " +
                       $"— both as a fraction of the free-space kernel.");
    }

    [Fact]
    public void T3_2_TheInteriorOracle_AgreesWithTheSHIPPEDTopHalfSpaceOracle_OnADifferentContour()
    {
        // EvaluateLayered removes the 1/k_z0 branch singularity with two substitutions referenced to
        // k₀; EvaluateInterior walks the plain real axis and references its extractions to the source
        // region's own k_m. On a top-half-space height pair the two are computing the same number
        // along different contours, with different partitions and different closed forms — so this is
        // an independent check on the new path, not a restatement of it.
        double worst = 0;
        foreach (var (name, stack) in LayerStacks.All())
        {
            var g = new LayeredSpectralGreens(stack, 10e9);
            double h = stack.TopZ;
            foreach (double rho in new[] { 1e-4, 1e-3, 1e-2 })
            foreach (var (z, zp) in new[] { (h, h), (h + 2e-4, h + 5e-4) })
            foreach (var k in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential })
            {
                Complex a = SommerfeldIntegral.EvaluateLayered(g, k, rho, z, zp).Value;
                Complex b = SommerfeldIntegral.EvaluateInterior(g, k, rho, z, zp).Value;
                double scaled = (a - b).Magnitude * 4.0 * Math.PI * rho;
                worst = Math.Max(worst, scaled);
                Assert.True(scaled < 1e-5,
                    $"{name} {k} at ρ={rho:G3}, ({z},{zp}): substituted contour {a}, real-axis " +
                    $"contour {b}, scaled difference {scaled:E3}");
            }
        }
        _out.WriteLine($"Tier 3, the two contours on a top-half-space pair: worst scaled " +
                       $"difference {worst:E3}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T3_3_TheInteriorOracle_MovesLittleUnderRefinement_AndSaysWhenItsTailDidNotConverge()
    {
        // The oracle's OWN convergence, before it is used to judge anything. It also reports the tail
        // state honestly: a cross-region pair separated by a thin spacer needs the tail out to
        // k_ρ ~ 1/t and will exhaust MaxTailPanels first — that is a fact about this integrator, and
        // a caller must be able to see it rather than receive a plausible wrong number.
        double worst = 0;
        int unconverged = 0, total = 0;
        var coarse = SommerfeldSettings.Default.Coarser(100.0);

        foreach (var (name, stack) in LayerStacks.All())
        {
            if (stack.LayerCount == 0) continue;
            var g = new LayeredSpectralGreens(stack, 10e9);
            double z0 = stack.InterfaceZ[0], t0 = stack.Layers[0].ThicknessM;

            foreach (double rho in new[] { 1e-4, 1e-3, 1e-2 })
            foreach (var (z, zp) in new[] { (z0 + 0.3 * t0, z0 + 0.3 * t0), (z0 + 0.2 * t0, z0 + 0.8 * t0) })
            foreach (var k in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential,
                                      GreensKernel.VerticalVectorPotential })
            {
                var fine = SommerfeldIntegral.EvaluateInterior(g, k, rho, z, zp);
                var crude = SommerfeldIntegral.EvaluateInterior(g, k, rho, z, zp, coarse);
                total++;
                if (!fine.TailConverged) unconverged++;
                double scaled = (fine.Value - crude.Value).Magnitude * 4.0 * Math.PI * rho;
                worst = Math.Max(worst, scaled);
                Assert.True(scaled < 1e-6,
                    $"{name} {k} at ρ={rho:G3}, ({z},{zp}): a 100× coarsening moves the oracle by " +
                    $"{scaled:E3} of the free-space kernel — it is not converged.");
            }
        }
        _out.WriteLine($"Tier 3, interior oracle self-convergence: a 100× coarsening moves it by at " +
                       $"most {worst:E3} of the free-space kernel; {unconverged} of {total} tails did " +
                       $"not reach the residual target inside MaxTailPanels.");
    }

    [Fact]
    public void T3_4_TheInteriorOracle_IsReciprocalInTheHeights()
    {
        double worst = 0;
        foreach (var (name, stack) in LayerStacks.All())
        {
            if (stack.LayerCount == 0) continue;
            var g = new LayeredSpectralGreens(stack, 10e9);
            double z0 = stack.InterfaceZ[0], t0 = stack.Layers[0].ThicknessM;
            double zLow = z0 + 0.25 * t0, zHigh = stack.TopZ + 0.5 * stack.TopZ;
            foreach (double rho in new[] { 1e-4, 1e-3 })
            foreach (var k in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential,
                                      GreensKernel.VerticalVectorPotential })
            {
                Complex a = SommerfeldIntegral.EvaluateInterior(g, k, rho, zHigh, zLow).Value;
                Complex b = SommerfeldIntegral.EvaluateInterior(g, k, rho, zLow, zHigh).Value;
                double scaled = (a - b).Magnitude * 4.0 * Math.PI * rho;
                worst = Math.Max(worst, scaled);
                Assert.True(scaled < 1e-7, $"{name} {k} at ρ={rho:G3}: {a} vs {b}, scaled {scaled:E3}");
            }
        }
        _out.WriteLine($"Tier 3, cross-region spatial reciprocity: worst scaled {worst:E3}");
    }

    // =========================================================================================
    // M3 / D4 — THE THREE HEIGHT PAIRINGS, AND WHETHER THE CROSS-REGION ONE IS A SHIFT.
    //
    // "Whether it is a shift in SOME variable, or genuinely needs a two-variable treatment, is the
    // question — and it is the one §10.2's original warning was actually about." L9b narrowed it to
    // exactly this case and measured the other two. These two tests answer it, and they answer it the
    // way L9b answered its own (measure the span, then locate the obstruction) rather than by
    // asserting a structure.
    // =========================================================================================

    [Fact]
    public void M3_1_EveryHeightDependence_LivesInTheSameFOURDimensionalSpan_CrossRegionIncluded()
    {
        // THE STRUCTURE, MEASURED. At a fixed k_ρ and a fixed region pair (m, n), every kernel
        // component's dependence on (z, z′) lies in the span of the four products
        //     e^{∓j k_zm z′} · e^{∓j k_zn z},
        // with coefficients that do not depend on the heights at all. Four pairs determine them; a
        // FIFTH pair is predicted, and the prediction is what is reported. That is L9b's R5_3
        // generalised in two directions at once — to the cross-region case it explicitly did not
        // cover, and to all four kernel components rather than the voltage alone.
        //
        // Note what makes the generalisation free: k_zm and k_zn are POLARISATION-INDEPENDENT, so the
        // TM and TE lines share the same four families and every component built from them — G_A^xx,
        // G_q, G_A^zz and the mixed one — lies in the SAME four-dimensional space.
        double worstSame = 0, worstCross = 0;

        foreach (var (name, stack) in LayerStacks.All())
        {
            if (stack.LayerCount < 1) continue;
            var g = new LayeredSpectralGreens(stack, 10e9);
            double z0 = stack.InterfaceZ[0], t0 = stack.Layers[0].ThicknessM, h = stack.TopZ;

            foreach (bool cross in new[] { false, true })
            {
                // Five (z, z′) pairs, all with z > z′ so the same-region |z − z′| never changes
                // branch, and all well inside their own regions.
                var pairs = cross
                    ? new (double Z, double Zp)[]
                      {
                          (h + 0.20 * h, z0 + 0.20 * t0), (h + 0.35 * h, z0 + 0.35 * t0),
                          (h + 0.50 * h, z0 + 0.20 * t0), (h + 0.20 * h, z0 + 0.55 * t0),
                          (h + 0.42 * h, z0 + 0.31 * t0),
                      }
                    : new (double Z, double Zp)[]
                      {
                          (z0 + 0.70 * t0, z0 + 0.20 * t0), (z0 + 0.80 * t0, z0 + 0.35 * t0),
                          (z0 + 0.60 * t0, z0 + 0.15 * t0), (z0 + 0.90 * t0, z0 + 0.45 * t0),
                          (z0 + 0.75 * t0, z0 + 0.28 * t0),
                      };

                int m = stack.RegionOf(pairs[0].Zp), n = stack.RegionOf(pairs[0].Z);
                if (cross == (m == n)) continue;      // the stack did not give us the pairing we asked for

                foreach (double u in new[] { 0.3, 2.0, 15.0 })
                foreach (var k in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential,
                                          GreensKernel.VerticalVectorPotential,
                                          GreensKernel.MixedVectorPotential })
                {
                    Complex kRho = u * g.K0, w = kRho * kRho;
                    Complex kzm = g.KzOfRegion(m, w), kzn = g.KzOfRegion(n, w);

                    Complex[] Basis(double z, double zp)
                    {
                        var b = new Complex[4];
                        int idx = 0;
                        foreach (double sm in new[] { -1.0, +1.0 })
                        foreach (double tn in new[] { -1.0, +1.0 })
                            b[idx++] = Complex.Exp(-Complex.ImaginaryOne * (sm * kzm * zp + tn * kzn * z));
                        return b;
                    }

                    var a = new Complex[4, 4];
                    var rhs = new Complex[4];
                    for (int p = 0; p < 4; p++)
                    {
                        var b = Basis(pairs[p].Z, pairs[p].Zp);
                        for (int q = 0; q < 4; q++) a[p, q] = b[q];
                        rhs[p] = g.KernelAtHeights(k, kRho, pairs[p].Z, pairs[p].Zp);
                    }
                    if (!Solve4(a, rhs, out var c)) continue;     // degenerate sample set, not a result

                    var bt = Basis(pairs[4].Z, pairs[4].Zp);
                    Complex predicted = c[0] * bt[0] + c[1] * bt[1] + c[2] * bt[2] + c[3] * bt[3];
                    Complex actual = g.KernelAtHeights(k, kRho, pairs[4].Z, pairs[4].Zp);

                    double rel = Rel(actual, predicted, 1e-30);
                    if (cross) worstCross = Math.Max(worstCross, rel);
                    else       worstSame  = Math.Max(worstSame, rel);
                    Assert.True(rel < 1e-8,
                        $"{name} {k} u={u} {(cross ? "CROSS" : "same")}-region: four coefficients from " +
                        $"four height pairs predict the fifth to {rel:E3} — the span is not four.");
                }
            }
        }

        _out.WriteLine(
            $"M3/D4 — the height dependence at fixed k_ρ spans exactly four exponential products " +
            $"e^(∓jk_zm z′)·e^(∓jk_zn z), for BOTH pairings and all four kernel components. Four " +
            $"coefficients solved from four height pairs predict a fifth to: same-region " +
            $"{worstSame:E3}, CROSS-region {worstCross:E3}.");
    }

    [Fact]
    public void M3_2_TheCrossRegionCase_CarriesASecondBranchPoint_InWhicheverVariableIsChosen()
    {
        // AND THIS IS WHY THE SPAN BEING FOUR IS NOT THE END OF THE STORY. DCIM fits a sum of
        // exponentials in ONE vertical wavenumber, which is an ENTIRE function of it — so the four
        // families are only fittable if a single variable makes all four entire. The cross-region
        // product e^{−jk_zm z′}e^{−jk_zn z} makes that impossible in both directions:
        //
        //   • in k_z0 (the top half-space's), the source region's k_zm = √(k_m² − k_top² + k_z0²) is
        //     two-valued, with branch points at k_z0 = ±j k₀√(εᵣ,m µᵣ,m − 1);
        //   • in k_zm (the source region's), the observer region's k_zn is two-valued, with branch
        //     points at k_zm = ±√(k_m² − k_n²).
        //
        // That is EXACTLY the shape of L9b's open-below obstruction — a second cut on the imaginary
        // axis, in the half-plane the sampling path runs into, that a sum of exponentials cannot
        // carry — and this test locates it and measures how close the default path comes, in the same
        // two columns L9b's D3 table uses. It reports; it does not refuse. Building the fit and
        // measuring what the cut costs is the next slice's.
        var s = DcimSettings.Default;
        _out.WriteLine("stack                                     | εᵣ low | in k_z0: bp/k₀      " +
                       "| far path / near path | in k_zm: bp/k₀");

        foreach (var (name, stack) in LayerStacks.All())
        {
            if (stack.LayerCount < 1) continue;
            // The lower metal level sits on interface 0, i.e. at the bottom of region 1.
            var mat = stack.MaterialOfRegion(1);
            var top = stack.MaterialOfRegion(stack.RegionCount - 1);
            Complex diff = mat.EpsComplex * mat.MuR - top.EpsComplex * top.MuR;

            // k_zm² = k_m² − k_top² + k_z0² vanishes at k_z0 = ±j k₀√(εᵣ,m µᵣ,m − εᵣ,t µᵣ,t).
            Complex bpKz0 = Complex.ImaginaryOne * Complex.Sqrt(diff);
            if (bpKz0.Imaginary > 0) bpKz0 = -bpKz0;              // the one the sampling path runs into
            // …and, in the source region's OWN variable, k_z0² = k_top² − k_m² + k_zm² vanishes at
            // k_zm = ±k₀√(εᵣ,m µᵣ,m − εᵣ,t µᵣ,t) — the REAL axis, where a k_zm-parameterised path starts.
            Complex bpKzm = Complex.Sqrt(diff);

            double far  = PathDistance(bpKz0, s.FarPathExtent, s.FarSamples);
            double near = PathDistance(bpKz0, s.PathExtent, s.Samples);
            _out.WriteLine($"{name,-41} | {mat.EpsR,6:G4} | {bpKz0.Real,7:F4} {bpKz0.Imaginary,+8:F4} j " +
                           $"| {far,8:F3} / {near,7:F3} | {bpKzm.Real,7:F4} {bpKzm.Imaginary,+8:F4} j");

            Assert.True(bpKz0.Magnitude > 0 || mat.EpsR <= 1.0 + 1e-12,
                        $"{name}: a lower level in εᵣ = {mat.EpsR} must put the second branch point " +
                        $"somewhere; it came out at the origin.");
        }
        _out.WriteLine(
            "READ THIS TABLE THE WAY L9b's D3 TABLE IS READ, AND NO FURTHER. The locations are\n" +
            "DERIVED and the distances COMPUTED; what a cut this close costs a fit is NOT measured\n" +
            "here, because the fit is not built. L9b measured 59× the free-space kernel for a cut at\n" +
            "0.178 of the far path, and GaAs and the MMIC stack put one at 0.137 — closer.\n" +
            "\n" +
            "WHICH PAIRINGS THIS TOUCHES, stated precisely rather than optimistically:\n" +
            "  • HIGH–HIGH is clean and L9b measured it. In k_z0 every interior region's k_zi is EVEN\n" +
            "    (D1's one rule), so no interior branch can matter and the fit is entire.\n" +
            "  • LOW–LOW is NOT automatically clean, and saying so corrects the obvious reading of\n" +
            "    L9b's D6. Its four families are exact shifts in k_zm — M3_1 measures that at 2.4e-13\n" +
            "    — but their COEFFICIENTS still contain the top half-space's k_z0, which is two-valued\n" +
            "    in k_zm (flipping it sends the top reflection to its reciprocal). 'The coefficients do\n" +
            "    not depend on the heights' and 'the coefficients are entire in the fit variable' are\n" +
            "    different statements and only the first is established.\n" +
            "  • LOW–HIGH has no single variable that works at all: the product e^(−jk_zm z′)e^(−jk_zn z)\n" +
            "    is entire in neither, and this is what §10.2's warning was actually about.");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void M5_TheCostOfTheNewComponents_PerSample_AndOfOneInteriorOraclePoint()
    {
        // D7 asks for a cost REPORTED rather than projected from a per-sample number, and L9b's own
        // experience is why: L9a's projection was wrong by 15–35× because it scaled the wrong
        // quantity. So this measures the two things that actually exist — the per-sample kernel cost
        // and the per-point oracle cost — and says explicitly what it does NOT measure, which is the
        // fill and the fit, because neither is built.
        const int N = 20000;
        var g = new LayeredSpectralGreens(LayerStacks.MmicTwoLevel, 10e9);
        double h = g.Stack.TopZ, zLow = g.Stack.InterfaceZ[0] + 0.3 * g.Stack.Layers[0].ThicknessM;

        foreach (var (label, z, zp) in new[] { ("high–high", h + 1e-5, h + 1e-5),
                                               ("low–low",   zLow, zLow),
                                               ("low–high",  h + 1e-5, zLow) })
        {
            foreach (var k in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential,
                                      GreensKernel.VerticalVectorPotential,
                                      GreensKernel.MixedVectorPotential })
            {
                var g2 = new LayeredSpectralGreens(LayerStacks.MmicTwoLevel, 10e9);
                Complex sink = Complex.Zero;
                // The 0.37 offset keeps the sweep off k_ρ = k₀ exactly. A top-half-space
                // source's kernel has a genuine 1/k_z0 pole at the branch point, so landing on it is
                // an infinity in the checksum rather than a defect — but a NaN in a cost table reads
                // like one, and a reader should not have to work that out.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < N; i++)
                    sink += g2.KernelAtHeights(k, (0.1 + 40.0 * (i + 0.37) / N) * g2.K0, z, zp);
                sw.Stop();
                _out.WriteLine($"{label,-10} {k,-24} {sw.Elapsed.TotalMilliseconds * 1000.0 / N,7:F3} µs/sample" +
                               $"   (checksum {sink.Magnitude:E2})");
            }
        }

        foreach (var (label, z, zp) in new[] { ("low–low", zLow, zLow), ("low–high", h + 1e-5, zLow) })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = SommerfeldIntegral.EvaluateInterior(g, GreensKernel.VerticalVectorPotential, 1e-4, z, zp);
            sw.Stop();
            _out.WriteLine($"one interior ORACLE point, {label}: {sw.Elapsed.TotalMilliseconds:F0} ms, " +
                           $"{r.Evaluations} kernel evaluations, tail converged {r.TailConverged}");
        }

        _out.WriteLine("NOT MEASURED HERE, and it must not be inferred from the above: the cost of a " +
                       "FIT (not built) or of a two-level FILL (not built). L9a's projection from a " +
                       "per-sample number was wrong by 15–35×; the same mistake is available here.");
    }

    /// <summary>Distance from <paramref name="p"/> (in units of k₀) to the DCIM sampling path.</summary>
    private static double PathDistance(Complex p, double extent, int samples)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i <= samples; i++)
        {
            double t = extent * i / samples;
            var kz = new Complex(1.0 - t / extent, -t);
            best = Math.Min(best, (p - kz).Magnitude);
        }
        return best;
    }

    /// <summary>4×4 complex Gaussian elimination with partial pivoting — a dozen lines, no package.</summary>
    private static bool Solve4(Complex[,] a, Complex[] b, out Complex[] x)
    {
        x = new Complex[4];
        var m = (Complex[,])a.Clone();
        var r = (Complex[])b.Clone();
        for (int col = 0; col < 4; col++)
        {
            int piv = col;
            for (int row = col + 1; row < 4; row++)
                if (m[row, col].Magnitude > m[piv, col].Magnitude) piv = row;
            if (m[piv, col].Magnitude < 1e-14) return false;
            if (piv != col)
            {
                for (int q = 0; q < 4; q++) (m[col, q], m[piv, q]) = (m[piv, q], m[col, q]);
                (r[col], r[piv]) = (r[piv], r[col]);
            }
            for (int row = col + 1; row < 4; row++)
            {
                Complex f = m[row, col] / m[col, col];
                for (int q = col; q < 4; q++) m[row, q] -= f * m[col, q];
                r[row] -= f * r[col];
            }
        }
        for (int row = 3; row >= 0; row--)
        {
            Complex sum = r[row];
            for (int q = row + 1; q < 4; q++) sum -= m[row, q] * x[q];
            x[row] = sum / m[row, row];
        }
        return true;
    }

    // =========================================================================================
    // TIER 2 — R-via-1: the horizontal-only path is untouched.
    // =========================================================================================

    [Fact]
    public void T2_1_TheShippedVoltage_IsBitIdenticalAfterTheDualLineRefactor()
    {
        // R-via-1. LineResponse now serves four transmission-line Green's functions where Voltage
        // used to be written out on its own, and EVERY L9a and L9b number depends on that
        // arithmetic. These values were dumped at full precision BEFORE the change (600 of them,
        // across all six stacks, two frequencies, five k_ρ, five height pairs including interior and
        // cross-region, both polarisations — all 600 came back byte-identical); fourteen are pinned
        // here as EXACT equalities, one per stack per branch of LineResponse. The Tier oracles all
        // carry tolerances and structurally cannot catch a one-ulp re-association — L7b-b found two
        // that way and this is the same gate.
        (LayerStack Stack, double F, double U, double Z, double Zp, SurfaceWavePolarization P,
         double Re, double Im)[] pinned =
        [
            (LayerStacks.Fr4Slab,      2e9, 0.3, 0.0018000000000000002, 0.0008, SurfaceWavePolarization.Tm,
             0.9680482580753664, 12.41759309459738),
            (LayerStacks.Fr4Slab,      2e9, 0.9, 0.0016, 0.0016, SurfaceWavePolarization.Tm,
             2.667211820240384, 20.37982725572171),
            (LayerStacks.Fr4Slab,     10e9, 0.3, 0.0008, 0.0018000000000000002, SurfaceWavePolarization.Te,
             30.087377179086733, 68.91830843733688),
            (LayerStacks.GaAsSlab,     2e9, 0.3, 0.00030000000000000003, 5E-05, SurfaceWavePolarization.Te,
             0.009472603750976144, 0.7895866778775664),
            (LayerStacks.GaAsSlab,     2e9, 0.9, 0.0002, 0.0006000000000000001, SurfaceWavePolarization.Tm,
             0.03250196027873667, 1.7797489497495513),
            (LayerStacks.GaAsSlab,    10e9, 0.9, 5E-05, 5E-05, SurfaceWavePolarization.Te,
             0.0181344467689313, 3.9546791097737706),
            (LayerStacks.Pcb3Layer,    2e9, 0.3, 0.0004, 0.0004, SurfaceWavePolarization.Tm,
             0.1103226268572912, 6.199964247072043),
            (LayerStacks.Pcb3Layer,    2e9, 0.9, 0.0004, 0.0016, SurfaceWavePolarization.Tm,
             0.5931827022240949, 5.115155511381443),
            (LayerStacks.Pcb3Layer,   10e9, 2.0, 0.0014, 0.0014, SurfaceWavePolarization.Te,
             0.04641067780501366, 72.92492320029326),
            (LayerStacks.MmicTwoLevel, 2e9, 0.3, 0.00010300000000000001, 0.00010300000000000001,
             SurfaceWavePolarization.Te, 0.006700032402338319, 1.6266039317688665),
            (LayerStacks.MmicTwoLevel, 2e9, 2.0, 5E-05, 5E-05, SurfaceWavePolarization.Tm,
             0.0004905816868008182, 0.5452261783561202),
            (LayerStacks.MmicTwoLevel, 10e9, 2.0, 0.000303, 5E-05, SurfaceWavePolarization.Te,
             1.8217496110937096E-05, 3.5451815639652975),
            (LayerStacks.MmicTwoLevel, 10e9, 120.0, 5E-05, 0.000303, SurfaceWavePolarization.Tm,
             0.009609697447472747, -5.29889934236272),
            (LayerStacks.OpenBelow,    2e9, 15.0, 0.0005, 0.0005, SurfaceWavePolarization.Tm,
             0.7181753979001829, -1158.8612787557618),
        ];

        foreach (var (stack, f, u, z, zp, p, re, im) in pinned)
        {
            var g = new LayeredSpectralGreens(stack, f);
            Complex w = (u * g.K0) * (u * g.K0);
            Assert.Equal(new Complex(re, im), g.Voltage(p, w, z, zp));
        }
    }

    // -----------------------------------------------------------------------------------------

    private static IEnumerable<(double Z, double Zp)> HeightPairs(LayerStack stack)
    {
        double h = stack.TopZ;
        yield return (h, h);
        yield return (h + 1e-4, h + 5e-4);
        if (stack.LayerCount == 0) yield break;
        double inLayer1 = 0.5 * (stack.InterfaceZ[0] + stack.InterfaceZ[1]);
        yield return (inLayer1, inLayer1);
        yield return (inLayer1, inLayer1 * 1.4);
        yield return (h + 2e-4, inLayer1);
        yield return (inLayer1, h + 2e-4);
    }

    /// <summary>
    /// Height pairs plus the central-difference step to use with them.
    ///
    /// <para><b>The step is chosen from the DISTANCE TO THE NEAREST INTERFACE, not from the thinnest
    /// layer in the stack, and that distinction is what makes this rung work.</b> A second cross
    /// difference divides by <c>4h²</c>, so its roundoff floor is <c>ε·L²/h²</c> relative, with L the
    /// kernel's own variation scale — a step chosen 1e-3 of a 3 µm spacer while the two points sit in
    /// the half-space above, where the kernel varies on 1/k ≈ 5 mm, puts that floor at ~1e-3 and the
    /// rung reads as a kernel error. Two per cent of the nearest interface distance keeps both the
    /// truncation and the roundoff term near 1e-8.</para>
    /// </summary>
    private static IEnumerable<(double Z, double Zp, double Step)> HeightTriples(LayerStack stack)
    {
        double h = stack.TopZ;
        var pairs = new List<(double Z, double Zp)> { (h + 0.25 * h, h + 0.60 * h) };
        if (stack.LayerCount > 0)
        {
            double z0 = stack.InterfaceZ[0], t0 = stack.Layers[0].ThicknessM;
            pairs.Add((z0 + 0.25 * t0, z0 + 0.70 * t0));      // both inside the bottom layer
            pairs.Add((z0 + 0.30 * t0, h + 0.40 * h));        // cross-region, source below
            pairs.Add((h + 0.40 * h, z0 + 0.30 * t0));        // cross-region, source above
        }
        foreach (var (z, zp) in pairs)
            yield return (z, zp,
                0.15 * Math.Min(Math.Abs(z - zp), Math.Min(GapToInterface(stack, z), GapToInterface(stack, zp))));
    }

    private static double GapToInterface(LayerStack stack, double x)
    {
        double d = double.PositiveInfinity;
        foreach (double zi in stack.InterfaceZ) d = Math.Min(d, Math.Abs(x - zi));
        return d;
    }
}
