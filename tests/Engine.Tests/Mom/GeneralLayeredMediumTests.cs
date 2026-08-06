using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>Phase L9a — the general layered medium, and its oracle ladder.</b>
///
/// <para>The ordering rule is L8a's, unchanged: <b>each tier passes before the next is written</b>,
/// and the exact reductions come before anything empirical. Four of the five rungs need no external
/// data at all, which matters because §11's L9 gate sentence asks for agreement with "published
/// reference structures" — see the report for whether that survives this project's own rules.</para>
///
/// <list type="bullet">
///   <item><b>Tier 0</b> — structural, per sample, free: reciprocity, the k_ρ → 0 limit, Γ^q(0) ≠ Γ^e(0).</item>
///   <item><b>Tier 1</b> — the one-layer reduction at machine precision against the SHIPPED kernel (D5).</item>
///   <item><b>Tier 2</b> — split-a-layer invariance: cheap, exact, and it catches every cascade
///         bookkeeping error that Tier 1 cannot see.</item>
///   <item><b>Tier 3</b> — the static limit against a purely electrostatic solver.</item>
///   <item><b>Tier 4</b> — direct Sommerfeld integration on a genuinely multilayer stack: the
///         REPORTED measurement.</item>
/// </list>
///
/// <para><b>Note what Tier 4 does and does not establish.</b> It shares the spectral kernel with the
/// thing under test, so it validates the INVERSION, not the kernel. Tiers 1–3 are what validate the
/// kernel. That is why the ladder has five entries instead of one.</para>
/// </summary>
public sealed class GeneralLayeredMediumTests
{
    private readonly ITestOutputHelper _out;
    public GeneralLayeredMediumTests(ITestOutputHelper output) => _out = output;

    /// <summary>Relative comparison with an absolute floor, mirroring L8a's own helper: deep in the
    /// evanescent tail these quantities underflow out of physics and a purely relative gate would
    /// be asserting on numbers that no longer exist.</summary>
    private static double Rel(Complex expected, Complex actual, double floor = 1e-300)
    {
        double scale = Math.Max(expected.Magnitude, floor);
        return (expected - actual).Magnitude / scale;
    }

    private static readonly double[] KRhoOverK0 =
        [0.0, 1e-8, 1e-5, 1e-3, 0.01, 0.1, 0.5, 0.9, 0.999, 1.001, 1.5, 2.0, 3.5, 10.0, 40.0, 300.0];

    // =========================================================================================
    // TIER 0 — structural, per sample, free.  No inverse transform is involved anywhere here.
    // =========================================================================================

    [Fact]
    public void T0_1_SameRegionReciprocityInHeights_IsBitIdentical()
    {
        // R-lyr-5 at kernel A's own standard. In the source's own region the four-term form depends
        // on the heights only through |z − z′|, z + z′ and the two interface distances — all
        // symmetric — so this is bit-identity, not a tolerance.
        foreach (var (name, stack) in LayerStacks.All())
        foreach (double f in new[] { 2e9, 10e9 })
        {
            var g = new LayeredSpectralGreens(stack, f);
            double h = stack.TopZ;
            foreach (double u in new[] { 0.2, 0.8, 1.4, 5.0 })
            foreach (var (z, zp) in new[] { (h, h), (h, h + 0.4e-3), (h + 0.1e-3, h + 2e-3) })
            foreach (var kind in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential })
            {
                Complex kRho = u * g.K0;
                Assert.Equal(g.KernelAtHeights(kind, kRho, z, zp),
                             g.KernelAtHeights(kind, kRho, zp, z));
            }
            Assert.NotNull(name);
        }
    }

    [Fact]
    public void T0_2_CrossRegionReciprocity_TwoIndependentPathsAgree_ToMachinePrecision()
    {
        // Source below, observer above takes an UPWARD generalised-transmission chain; the reverse
        // takes a downward one. They share no line of arithmetic, so their agreement is a real,
        // independent check on the cascade rather than a tautology — a stronger statement than the
        // bit-identity a canonicalised ordering would have bought.
        double worst = 0;
        foreach (var (name, stack) in LayerStacks.All())
        {
            if (stack.LayerCount < 2) continue;
            foreach (double f in new[] { 2e9, 10e9, 20e9 })
            {
                var g = new LayeredSpectralGreens(stack, f);
                double zLow = 0.5 * (stack.InterfaceZ[0] + stack.InterfaceZ[1]);   // inside layer 1
                double zHigh = stack.TopZ + 0.3e-3;                                // in the air above
                foreach (double u in new[] { 0.2, 0.8, 1.4, 5.0, 30.0 })
                foreach (var kind in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential })
                {
                    Complex kRho = u * g.K0;
                    Complex a = g.KernelAtHeights(kind, kRho, zHigh, zLow);
                    Complex b = g.KernelAtHeights(kind, kRho, zLow, zHigh);
                    double rel = Rel(a, b);
                    worst = Math.Max(worst, rel);
                    Assert.True(rel < 1e-11,
                        $"{name} @ {f / 1e9} GHz, {kind}, k_ρ/k₀ = {u}: up-chain {a} vs down-chain {b}, rel {rel:E3}");
                }
            }
        }
        _out.WriteLine($"cross-region reciprocity: worst relative disagreement between the two " +
                       $"independent chains = {worst:E3}");
    }

    [Fact]
    public void T0_3_TheScalarKernelsRemovableSingularity_IsRemovedStably_AndTheNaiveFormIsRuined()
    {
        // R-lyr-4. Γ^q carries a k₀²/k_ρ² prefactor multiplying a difference that vanishes as k_ρ².
        // The one-layer kernel cancels it in closed form; a cascade has no such closed form, so the
        // contour-extracted Taylor series stands in for it. This test pins BOTH halves: the stable
        // form stays smooth through k_ρ = 0, and the naive division IS ruined there — so the test
        // cannot quietly stop demonstrating why the stable path matters.
        foreach (var (name, stack) in LayerStacks.All())
        {
            var g = new LayeredSpectralGreens(stack, 10e9);
            double h = stack.TopZ;

            Complex atZero = g.KernelAtHeights(GreensKernel.ScalarPotential, Complex.Zero, h, h);
            Assert.True(double.IsFinite(atZero.Real) && double.IsFinite(atZero.Imaginary),
                $"{name}: G̃_q at k_ρ = 0 is {atZero}");

            // Where the naive form still has digits left the two must agree closely.
            for (double u = 0.9; u >= 1e-3; u /= 2)
            {
                Complex stable = g.KernelAtHeights(GreensKernel.ScalarPotential, u * g.K0, h, h);
                Complex naive  = g.ScalarKernelNaive(u * g.K0, h, h);
                Assert.True(Rel(stable, naive) < 1e-6,
                    $"{name} at k_ρ/k₀ = {u}: stable {stable} vs naive {naive}");
            }

            // G̃_q is a function of k_ρ², so approaching from a thousandth of k₀ must already be
            // within O(1e-6) of the value at the removable point itself.
            Assert.True(Rel(atZero, g.KernelAtHeights(GreensKernel.ScalarPotential, 1e-3 * g.K0, h, h)) < 1e-4,
                $"{name}: G̃_q is not continuous into k_ρ = 0");

            double noise = Rel(g.KernelAtHeights(GreensKernel.ScalarPotential, 1e-8 * g.K0, h, h),
                               g.ScalarKernelNaive(1e-8 * g.K0, h, h));
            _out.WriteLine($"{name}: naive-form error at k_ρ = 1e-8·k₀ = {noise:E3} (stable form is smooth)");
            Assert.True(noise > 1e-4,
                $"{name}: the naive form is supposed to be ruined at k_ρ = 1e-8·k₀ — if it is not, " +
                $"this test has stopped demonstrating why the stable path matters");
        }
    }

    [Fact]
    public void T0_4_GammaQAtZero_DoesNotCollapseOntoGammaE_AndGammaEEqualsGammaHThere()
    {
        // L8a asserts this precisely because if the two coincided the k₀²/k_ρ² term would be missing
        // and everything downstream would still look plausible.
        foreach (var (name, stack) in LayerStacks.All())
        {
            var g = new LayeredSpectralGreens(stack, 10e9);
            Complex ge = g.TopInterfaceFresnel(SurfaceWavePolarization.Tm, Complex.Zero);
            Complex gh = g.TopInterfaceFresnel(SurfaceWavePolarization.Te, Complex.Zero);
            Complex gq = g.TopInterfaceReflection(GreensKernel.ScalarPotential, Complex.Zero);

            Assert.True(Rel(ge, gh) < 1e-12,
                $"{name}: at k_ρ = 0 the TM and TE networks are identical, so Γ^e ({ge}) must equal Γ^h ({gh})");
            // 0.01 rather than something rounder: the separation is ≈ 2k₀H(εᵣ−1)/εᵣ, which on the
            // 100 µm GaAs starter at 10 GHz is 0.039 — small, real, and the whole content of the
            // k₀²/k_ρ² term. A threshold above it would be asserting the wrong physics.
            Assert.True((gq - ge).Magnitude > 0.01,
                $"{name}: Γ^q(0) = {gq} must NOT collapse onto Γ^e(0) = {ge} — if it did, the k₀²/k_ρ² " +
                $"term would be missing");
        }
    }

    [Fact]
    public void T0_5_TheProperBranch_IsTheDecayingOne_InEveryRegion()
    {
        foreach (var (name, stack) in LayerStacks.All())
        {
            var g = new LayeredSpectralGreens(stack, 10e9);
            for (double u = 0.0; u <= 50.0; u += 0.37)
            for (int r = 0; r < stack.RegionCount; r++)
            {
                Complex w  = (u * g.K0) * (Complex)(u * g.K0);
                Complex kz = g.KzOfRegion(r, w);
                Assert.True(kz.Imaginary <= 1e-9 * Math.Max(1, kz.Magnitude),
                    $"{name}: Im k_z of region {r} at k_ρ/k₀ = {u} is {kz.Imaginary}");
            }
        }
    }

    [Fact]
    public void T0_6_EveryRefusal_NamesTheSpecificFeature()
    {
        // R-lyr-8 / R-mom-17: a stack this kernel cannot represent is refused with the feature
        // named, not returned as a NaN two phases later.
        var zeroThickness = LayerStack.CanRepresent(
            Termination.Pec, [new MediumLayer(0.0, EmMaterial.Air)], Termination.Air);
        Assert.False(zeroThickness.Ok);
        Assert.Contains("ZERO-THICKNESS", zeroThickness.Reason);

        var badEps = LayerStack.CanRepresent(
            Termination.Pec, [new MediumLayer(1e-3, new EmMaterial(0.5))], Termination.Air);
        Assert.False(badEps.Ok);
        Assert.Contains("εᵣ ≥ 1", badEps.Reason);

        var tooLow = LayeredSpectralGreens.CanSolveAt(LayerStacks.Fr4Slab, 1.0);
        Assert.False(tooLow.Ok);
        Assert.Contains("LayeredStaticGreens", tooLow.Reason);
        Assert.Contains("quasi-static", tooLow.Reason);

        // A source inside a PEC wall is refused by name rather than silently evaluated.
        var g = new LayeredSpectralGreens(LayerStacks.Fr4Slab, 10e9);
        var wall = Assert.Throws<ArgumentException>(() =>
            g.KernelAtHeights(GreensKernel.VectorPotential, 0.5 * g.K0, LayerStacks.Fr4Slab.TopZ, -1e-6));
        Assert.Contains("PEC", wall.Message);
        Assert.Contains("wall", wall.Message);

        // A closed top has no half-space for a reflection coefficient to live in.
        var boxed = new LayerStack(Termination.Pec, [new MediumLayer(1e-3, new EmMaterial(2.2))], Termination.Pec);
        var bg = new LayeredSpectralGreens(boxed, 10e9);
        var noHalfSpace = Assert.Throws<InvalidOperationException>(() =>
            bg.TopInterfaceReflection(GreensKernel.VectorPotential, 0.5 * bg.K0));
        Assert.Contains("solid wall", noHalfSpace.Message);
    }

    // =========================================================================================
    // TIER 1 — the one-layer reduction, at machine precision, against the SHIPPED kernel (D5).
    //
    // This is the strongest single check in the ladder. The general medium instantiated as one
    // grounded slab must reproduce Γ^e, Γ^h, Γ^q, G̃_A and G̃_q from SpectralGreens — which is
    // itself already validated to ≤ 6e-3 against direct Sommerfeld integration and is what L8's
    // three phase-gate sentences rest on. If it does not, the cascade is wrong; there is no
    // tolerance to negotiate, because the two are the same formula.
    // =========================================================================================

    public static TheoryData<string, double, double, double> SlabCases() => new()
    {
        { "FR-4  εᵣ=4.4  h=1.6 mm",   4.4,  0.02,  1.6e-3 },
        { "GaAs  εᵣ=12.9 h=100 µm",  12.9,  0.002, 100e-6 },
    };

    [Theory]
    [MemberData(nameof(SlabCases))]
    public void T1_1_TheReflectionCoefficients_ReproduceTheShippedKernel(
        string label, double epsR, double tanD, double heightM)
    {
        var slab = new GroundedSlab(heightM, new EmMaterial(epsR, tanD));
        var stack = LayerStack.FromGroundedSlab(slab);
        double worst = 0, worstMain = 0;

        foreach (double f in new[] { 1e9, 2e9, 10e9, 20e9, 40e9 })
        {
            var shipped = new SpectralGreens(slab, f);
            var general = new LayeredSpectralGreens(stack, f);

            foreach (double u in KRhoOverK0)
            {
                Complex kRho = u * shipped.K0;
                worst = Math.Max(worst, Check(shipped.ReflectionTe(kRho),
                                              general.TopInterfaceFresnel(SurfaceWavePolarization.Te, kRho),
                                              $"Γ^h at u={u}, {f / 1e9} GHz", u));
                worst = Math.Max(worst, Check(shipped.ReflectionTm(kRho),
                                              general.TopInterfaceFresnel(SurfaceWavePolarization.Tm, kRho),
                                              $"Γ^e at u={u}, {f / 1e9} GHz", u));
                worst = Math.Max(worst, Check(shipped.ReflectionScalar(kRho),
                                              general.TopInterfaceReflection(GreensKernel.ScalarPotential, kRho),
                                              $"Γ^q at u={u}, {f / 1e9} GHz", u));
            }
        }

        _out.WriteLine($"{label}: Γ^e/Γ^h/Γ^q vs the shipped kernel — worst relative {worst:E3} " +
                       $"(worst inside k_ρ ≤ 40 k₀: {worstMain:E3})");

        double Check(Complex expected, Complex actual, string what, double u)
        {
            double rel = Rel(expected, actual, 1e-14);
            // D5's ~1e-13 holds everywhere the reflection coefficient is a physically live quantity.
            // Past k_ρ ≈ 100 k₀ both kernels are computing the SAME exactly-Fresnel limit by two
            // different underflowing routes — the shipped one saturates tan at |Im| > 30, the
            // cascade's propagation factor e^{−2jk_z d} has already flushed to zero — on numbers
            // that have decayed to ~1e-5 of unity. The residual there is 1e-12, and it is a
            // difference between two ways of writing zero, not a disagreement about the medium.
            double tol = u <= 40 ? 1e-13 : 2e-11;
            Assert.True(rel < tol, $"{label} {what}: shipped {expected}, general {actual}, rel {rel:E3}");
            if (u <= 40) worstMain = Math.Max(worstMain, rel);
            return rel;
        }
    }

    [Theory]
    [MemberData(nameof(SlabCases))]
    public void T1_2_TheKernelsAtHeights_ReproduceTheShippedKernel(
        string label, double epsR, double tanD, double heightM)
    {
        var slab  = new GroundedSlab(heightM, new EmMaterial(epsR, tanD));
        var stack = LayerStack.FromGroundedSlab(slab);
        double worst = 0;

        foreach (double f in new[] { 1e9, 2e9, 10e9, 20e9, 40e9 })
        {
            var shipped = new SpectralGreens(slab, f);
            var general = new LayeredSpectralGreens(stack, f);
            double h = heightM;

            foreach (double u in KRhoOverK0)
            {
                if (Math.Abs(u - 1.0) < 1e-12) continue;   // k_z0 = 0 exactly: 0/0 in BOTH kernels
                Complex kRho = u * shipped.K0;
                foreach (var (z, zp) in new[] { (h, h), (h, h + 0.35 * heightM), (h + 0.1 * heightM, h + 3 * heightM) })
                foreach (var kind in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential })
                {
                    Complex expected = shipped.KernelAtHeights(kind, kRho, z, zp);
                    Complex actual   = general.KernelAtHeights(kind, kRho, z, zp);
                    double rel = Rel(expected, actual, 1e-30);
                    worst = Math.Max(worst, rel);
                    // The reflections themselves agree to 1e-13 (T1_1). The KERNEL is looser near
                    // k_ρ → 0 for a reason that belongs to the quantity, not to the cascade:
                    // G̃ = (1 + Γ)/(2j k_z0), and on a thin substrate 1 + Γ is a cancellation —
                    // |1 + Γ^q| ≈ 3e-4 on 100 µm GaAs at 1 GHz — so the last bit of Γ is amplified
                    // by ~1/|1 + Γ| ≈ 3000. BOTH kernels compute it that way and both lose the same
                    // digits; 2e-11 of disagreement here is ~5e-15 of disagreement in Γ.
                    Assert.True(rel < 1e-10,
                        $"{label} {kind} at u={u}, {f / 1e9} GHz, z={z:G6}, z′={zp:G6}: " +
                        $"shipped {expected}, general {actual}, rel {rel:E3}");
                }
            }
        }

        _out.WriteLine($"{label}: G̃_A/G̃_q at heights vs the shipped kernel — worst relative {worst:E3}");
    }

    [Fact]
    public void T1_3_WithEpsR1_TheGeneralKernel_IsExactlyTheBareGroundPlane()
    {
        // The direct analogue of L8a's own εᵣ = 1 reduction, and it needs no external data: with no
        // dielectric anywhere the answer is Γ = −e^{−2jk_z0 H} EXACTLY, in both polarisations, for
        // any number of (physically invisible) interior interfaces.
        var stack = LayerStacks.AirOverGround();
        var g = new LayeredSpectralGreens(stack, 10e9);
        double h = stack.TopZ;

        foreach (double u in new[] { 0.0, 0.3, 0.999, 1.0, 1.001, 2.0, 10.0, 300.0 })
        {
            Complex kRho = u * g.K0;
            Complex expected = -Complex.Exp(-2.0 * Complex.ImaginaryOne * g.Kz0(kRho) * h);
            Assert.True(Rel(expected, g.TopInterfaceFresnel(SurfaceWavePolarization.Te, kRho), 1e-14) < 1e-12,
                $"Γ^h PEC image at k_ρ/k₀ = {u}");
            Assert.True(Rel(expected, g.TopInterfaceFresnel(SurfaceWavePolarization.Tm, kRho), 1e-14) < 1e-12,
                $"Γ^e PEC image at k_ρ/k₀ = {u}");
            Assert.True(Rel(expected, g.TopInterfaceReflection(GreensKernel.ScalarPotential, kRho), 1e-14) < 1e-11,
                $"Γ^q PEC image at k_ρ/k₀ = {u}");
        }
    }

    // =========================================================================================
    // TIER 2 — split-a-layer invariance.  Cheap, exact, and it catches every cascade bookkeeping
    // error: interface bookkeeping, propagation-direction sign errors and impedance-transform
    // slips all break this, and none of them breaks Tier 1.
    // =========================================================================================

    public static TheoryData<string, int, double[]> SplitCases() => new()
    {
        { "two halves",            0, new[] { 0.5, 0.5 } },
        { "three thirds",          0, new[] { 1.0, 1.0, 1.0 } },
        { "asymmetric 0.3 / 0.7",  0, new[] { 0.3, 0.7 } },
        { "middle layer, halves",  1, new[] { 0.5, 0.5 } },
        { "top layer, 0.25/0.75",  2, new[] { 0.25, 0.75 } },
    };

    [Theory]
    [MemberData(nameof(SplitCases))]
    public void T2_1_SplittingALayer_ChangesNothing(string label, int layerIndex, double[] fractions)
    {
        var stack = LayerStacks.Pcb3Layer;
        var split = stack.WithLayerSplit(layerIndex, fractions);
        Assert.Equal(stack.LayerCount + fractions.Length - 1, split.LayerCount);
        Assert.Equal(stack.TopZ, split.TopZ, 15);

        double worst = 0;
        foreach (double f in new[] { 2e9, 10e9, 20e9 })
        {
            var a = new LayeredSpectralGreens(stack, f);
            var b = new LayeredSpectralGreens(split, f);
            double h = stack.TopZ;

            foreach (double u in KRhoOverK0)
            {
                if (Math.Abs(u - 1.0) < 1e-12) continue;
                Complex kRho = u * a.K0;
                foreach (var (z, zp) in new[] { (h, h), (h + 0.2e-3, h + 1.1e-3) })
                foreach (var kind in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential })
                {
                    Complex x = a.KernelAtHeights(kind, kRho, z, zp);
                    Complex y = b.KernelAtHeights(kind, kRho, z, zp);
                    double rel = Rel(x, y, 1e-300);
                    worst = Math.Max(worst, rel);
                    // 1e-12, not bit-identity — see T2_2 for why bit-identity is unattainable and
                    // what the residual actually is. The scalar kernel is the looser of the two
                    // because its small-k_ρ Taylor coefficients are extracted independently for
                    // each stack, so the two paths do not even see the same rounding.
                    Assert.True(rel < 1e-12,
                        $"{label}, {kind}, u={u}, {f / 1e9} GHz: unsplit {x}, split {y}, rel {rel:E3}");
                }
            }
        }

        _out.WriteLine($"split-a-layer ({label}): worst relative change {worst:E3}");
    }

    [Fact]
    public void T2_2_SplittingALayer_IsNotQuiteBitIdentical_AndTheReasonIsFloatingPointExponentials()
    {
        // The brief asks for BIT-IDENTICAL. It is not attainable, and the reason is worth recording
        // rather than papering over with a tolerance: splitting a layer of thickness d into d₁ + d₂
        // replaces one propagation factor e^{−2jk_z d} by the product e^{−2jk_z d₁}·e^{−2jk_z d₂},
        // and exp(a)·exp(b) ≠ exp(a+b) in floating point. The internal interface itself IS exactly
        // transparent — its cross-multiplied Fresnel coefficient evaluates to an exact zero for
        // identical materials — so the residual is purely the exponential re-association, and it is
        // a handful of ulp, not a physics difference. T2_1 is what actually gates the invariance.
        var stack = LayerStacks.Pcb3Layer;
        var split = stack.WithLayerSplit(0, 0.5, 0.5);
        var a = new LayeredSpectralGreens(stack, 10e9);
        var b = new LayeredSpectralGreens(split, 10e9);
        double h = stack.TopZ;

        int identical = 0, total = 0;
        double worst = 0;
        foreach (double u in KRhoOverK0)
        {
            if (Math.Abs(u - 1.0) < 1e-12) continue;
            Complex kRho = u * a.K0;
            Complex x = a.KernelAtHeights(GreensKernel.VectorPotential, kRho, h, h);
            Complex y = b.KernelAtHeights(GreensKernel.VectorPotential, kRho, h, h);
            total++;
            if (x.Equals(y)) identical++;
            worst = Math.Max(worst, Rel(x, y, 1e-300));
        }

        _out.WriteLine($"split-a-layer: {identical}/{total} samples bit-identical, worst relative {worst:E3}");
        Assert.True(worst < 2e-14, "the residual must stay at the ulp level, not become a physics difference");
    }

    // =========================================================================================
    // M3 — the pole finder.  D4: locate and COUNT; do not assume how many there are.
    // =========================================================================================

    [Theory]
    [MemberData(nameof(SlabCases))]
    public void M3_1_ThePoleFinder_ReproducesTheShippedSlabsOwnModes(
        string label, double epsR, double tanD, double heightM)
    {
        var slab  = new GroundedSlab(heightM, new EmMaterial(epsR, tanD));
        var stack = LayerStack.FromGroundedSlab(slab);

        foreach (double f in new[] { 2e9, 10e9, 20e9, 40e9, 60e9 })
        {
            var shipped = new SpectralGreens(slab, f);
            var report  = SurfaceWavePoles.Find(stack, f);
            var (tmExpected, teExpected) = shipped.ModeCountFromCutoffs();

            Assert.True(report.TmCount == tmExpected && report.TeCount == teExpected,
                $"{label} @ {f / 1e9} GHz: general finder found TM={report.TmCount}, TE={report.TeCount}; " +
                $"the slab's own cutoff conditions say TM={tmExpected}, TE={teExpected}. Domain: {report.Domain}");

            // Matched by ORDER within each polarisation, not by Index: the shipped slab numbers its
            // TE modes from 1 (TE₁ is the first that exists) while the general finder cannot know
            // that convention, having no cutoff formula to count against. Order is the thing both
            // agree on.
            foreach (var pol in new[] { SurfaceWavePolarization.Tm, SurfaceWavePolarization.Te })
            {
                var mineList   = report.Modes.Where(m => m.Polarization == pol)
                                             .OrderBy(m => m.KRho.Real).ToList();
                var theirsList = shipped.SurfaceWaveModes.Where(m => m.Polarization == pol)
                                             .OrderBy(m => m.KRho.Real).ToList();
                Assert.Equal(theirsList.Count, mineList.Count);
                for (int i = 0; i < mineList.Count; i++)
                {
                var mine = mineList[i];
                var theirs = theirsList[i];
                double rel = Math.Abs(mine.KRho.Real - theirs.KRho.Real) / theirs.KRho.Real;
                Assert.True(rel < 1e-9,
                    $"{label} @ {f / 1e9} GHz {mine.Name}: general {mine.KRho}, shipped {theirs.KRho}, rel {rel:E3}");
                Assert.True(mine.Residual < 1e-8,
                    $"{label} {mine.Name}: dispersion residual {mine.Residual:E3} — the search did not land on a root");
                }
            }
        }
    }

    [Fact]
    public void M3_2_TM0_HasNoCutoff_HoweverThinTheStack()
    {
        // R-lgf-3 generalised: a grounded stack always supports at least one mode, so "there is no
        // surface wave here" is never a valid simplification.
        foreach (double h in new[] { 1e-6, 1e-5, 1e-4, 1e-3 })
        {
            var stack = new LayerStack(Termination.Pec,
                                       [new MediumLayer(h, new EmMaterial(4.4, 0.02))],
                                       Termination.Air);
            var report = SurfaceWavePoles.Find(stack, 100e6);
            Assert.True(report.TmCount >= 1,
                $"h = {h:G3} m: found no TM mode. Domain searched: {report.Domain}");
        }
    }

    [Fact]
    public void M3_3_ThePoleCounts_AreReportedForEveryStack_WithTheDomainSearched()
    {
        // R-lyr-6: "none found" is only an answer if the domain searched is stated, so the report
        // carries it. This is §8.3's deliverable, printed rather than asserted into a number nobody
        // can check.
        foreach (var (name, stack) in LayerStacks.All())
        foreach (double f in new[] { 2e9, 10e9, 20e9, 40e9 })
        {
            var r = SurfaceWavePoles.Find(stack, f);
            _out.WriteLine($"{name} @ {f / 1e9,4:F0} GHz: TM={r.TmCount} TE={r.TeCount}  " +
                           $"closest approach to the real axis = {r.ClosestApproachToRealAxis:E3}");
            foreach (var m in r.Modes)
                _out.WriteLine($"    {m.Name,-4} k_ρ/k₀ = {m.KRho.Real / (2 * Math.PI * f / 2.99792458e8):F6} " +
                               $"{(m.KRho.Imaginary >= 0 ? "+" : "-")} j{Math.Abs(m.KRho.Imaginary) / (2 * Math.PI * f / 2.99792458e8):E3}   " +
                               $"residual {m.Residual:E2}");
            _out.WriteLine($"    domain: {r.Domain}");
            foreach (var m in r.Modes)
                Assert.True(m.Residual < 1e-6, $"{name} {m.Name}: residual {m.Residual:E3}");
        }
    }

    // =========================================================================================
    // TIER 3 — the static limit.
    //
    // L8a's own warning applies directly and is the reason the oracle is checked before it is
    // believed: the static image series had to be COMPLEX, and getting that wrong looked exactly
    // like a kernel bug (a frequency-INDEPENDENT 1.1e-6 floor that reads as convergence bottoming
    // out). This area has now had four occasions where the ORACLE, not the method, was at fault.
    // =========================================================================================

    [Theory]
    [MemberData(nameof(SlabCases))]
    public void T3_1_TheStaticSolver_ReproducesL8aOwnImageSeries_ForAOneLayerStack(
        string label, double epsR, double tanD, double heightM)
    {
        // The oracle is checked FIRST, against the closed-form image series L8a already validated
        // independently. Only then is it used as the multilayer reference.
        var slab  = new GroundedSlab(heightM, new EmMaterial(epsR, tanD));
        var stack = LayerStack.FromGroundedSlab(slab);
        double worstQrel = 0, worstArel = 0, worstQscaled = 0, worstAscaled = 0;

        foreach (double rho in new[] { 1e-5, 1e-4, 1e-3, 5e-3, 0.02, 0.1 })
        {
            Complex q = LayeredStaticGreens.ScalarPotential(stack, rho, heightM, heightM);
            Complex a = LayeredStaticGreens.VectorPotential(stack, rho, heightM, heightM);
            Complex qRef = StaticGreens.ScalarPotential(slab, rho);
            double  aRef = StaticGreens.VectorPotential(slab, rho);

            double qScaled = (qRef - q).Magnitude * 4 * Math.PI * rho;
            double aScaled = (aRef - a).Magnitude * 4 * Math.PI * rho;
            worstQrel = Math.Max(worstQrel, Rel(qRef, q));
            worstArel = Math.Max(worstArel, Rel(aRef, a));
            worstQscaled = Math.Max(worstQscaled, qScaled);
            worstAscaled = Math.Max(worstAscaled, aScaled);

            // Gated on the SCALED measure — |ΔG| as a fraction of the free-space kernel at the same
            // ρ — for L8a's own R-lgf-4 reason: a few substrate heights out, charge plus its ground
            // image is a DIPOLE, so G_q falls like h²/ρ³ while its constituents fall like 1/ρ. On
            // 100 µm GaAs at ρ = 0.1 m the value has collapsed to 9.6e-9 against a free-space 0.8,
            // and a strict relative error against a quantity that is nearly zero says more about the
            // zero than about the quadrature. Both measures are reported.
            Assert.True(qScaled < 1e-11, $"{label} G_q at ρ = {rho:G3}: series {qRef}, spectral {q}, scaled {qScaled:E3}");
            Assert.True(aScaled < 1e-11, $"{label} G_A at ρ = {rho:G3}: series {aRef}, spectral {a}, scaled {aScaled:E3}");
        }

        _out.WriteLine($"{label}: static spectral solver vs L8a's image series — scaled (|ΔG|·4πρ) " +
                       $"G_q {worstQscaled:E3}, G_A {worstAscaled:E3}; strict relative " +
                       $"G_q {worstQrel:E3}, G_A {worstArel:E3}");
    }

    [Fact]
    public void T3_2_AsFrequencyFalls_TheLayeredKernel_ConvergesQuadraticallyOntoTheStaticSolver()
    {
        // The multilayer analogue of L8a's T1_4. Quadratic in frequency, with NO floor: a 3× drop
        // must shrink the discrepancy by ~9×. A floor would mean the static oracle is wrong (which
        // is exactly how L8a caught its own real-εᵣ bug) or the cascade has a spurious term.
        var stack = LayerStacks.Pcb3Layer;
        double h = stack.TopZ, rho = 2 * h;

        double previous = double.NaN;
        foreach (double f in new[] { 300e6, 100e6, 30e6, 10e6 })
        {
            var g = new LayeredSpectralGreens(stack, f);
            var got = SommerfeldIntegral.EvaluateLayered(g, GreensKernel.ScalarPotential, rho, h, h);
            Complex stat = LayeredStaticGreens.ScalarPotential(stack, rho, h, h);
            double rel = Rel(stat, got.Value);

            var gotA = SommerfeldIntegral.EvaluateLayered(g, GreensKernel.VectorPotential, rho, h, h);
            Complex statA = LayeredStaticGreens.VectorPotential(stack, rho, h, h);
            double relA = Rel(statA, gotA.Value);

            _out.WriteLine($"f = {f / 1e6,6:F0} MHz  k₀H = {g.K0 * h:E2}   " +
                           $"G_q rel = {rel,11:E3}   G_A rel = {relA,11:E3}");

            if (!double.IsNaN(previous))
                Assert.True(rel < 0.2 * previous,
                    $"static convergence stalled: {rel:E3} after {previous:E3} — a FLOOR here means the " +
                    $"oracle is wrong, not the kernel (L8a's own real-εᵣ bug looked exactly like this)");
            previous = rel;
            Assert.True(relA < Math.Max(3e-3, 5 * rel), $"G_A vs its own static limit at {f / 1e6} MHz: {relA:E3}");
        }

        Assert.True(previous < 5e-6, $"at 10 MHz the discrepancy is still {previous:E3}");
    }

    [Fact]
    public void T3_3_TheStaticReflection_MustCarryACOMPLEXK_OrItLeavesAFrequencyIndependentFloor()
    {
        // L8a's finding, re-pinned for the general recursion: nothing in the electrostatic
        // derivation uses the realness of ε, and dropping tanδ leaves a discrepancy that does NOT
        // move with frequency — which reads exactly like a convergence floor and is nothing of the
        // sort. The lossless-material stack stands in for "written with a real εᵣ".
        var stack   = LayerStacks.Pcb3Layer;
        var realEps = SurfaceWavePoles.Lossless(stack);
        double h = stack.TopZ, rho = 2 * h;

        Complex withComplexK = LayeredStaticGreens.ScalarPotential(stack, rho, h, h);
        Complex withRealK    = LayeredStaticGreens.ScalarPotential(realEps, rho, h, h);
        double gap = Rel(withComplexK, withRealK);
        _out.WriteLine($"static G_q: complex K {withComplexK}, real-εᵣ K {withRealK}, gap {gap:E3}");
        Assert.True(gap > 1e-4, "if a real εᵣ made no difference this test would have stopped saying anything");

        // And the full-wave kernel converges onto the COMPLEX one, not the real one.
        double prevComplex = double.NaN, prevReal = double.NaN;
        foreach (double f in new[] { 100e6, 30e6, 10e6 })
        {
            var g = new LayeredSpectralGreens(stack, f);
            Complex got = SommerfeldIntegral.EvaluateLayered(g, GreensKernel.ScalarPotential, rho, h, h).Value;
            double toComplex = Rel(withComplexK, got);
            double toReal    = Rel(withRealK, got);
            _out.WriteLine($"f = {f / 1e6,5:F0} MHz  → complex-K {toComplex:E3}   → real-εᵣ-K {toReal:E3}");
            if (!double.IsNaN(prevComplex))
            {
                Assert.True(toComplex < 0.25 * prevComplex, "convergence onto the complex-K series stalled");
                Assert.True(toReal > 0.5 * prevReal,
                    "the discrepancy against the REAL-εᵣ series must NOT shrink — that is the whole " +
                    "point: it is a frequency-independent floor, not a convergence limit");
            }
            prevComplex = toComplex;
            prevReal    = toReal;
        }
    }

    // =========================================================================================
    // TIER 4 — direct Sommerfeld integration through the general medium.
    //
    // Said plainly, because it changes what this rung can be: at L9a there is NO DCIM for the
    // general medium (L9b's), so there is nothing to measure the oracle AGAINST in the way L8a's
    // Tier 2 measured DCIM against it. What L9a can deliver — and what L9b will be scheduled
    // against — is three things: the oracle's own convergence on a genuinely multilayer stack
    // (where it stops being trustworthy, in ρ/λ and in the height pair), a TRUE error curve on the
    // one multilayer geometry whose answer is known exactly (εᵣ = 1 → free space + one image), and
    // agreement with L8a's own independently-validated one-layer oracle.
    // =========================================================================================

    [Fact]
    public void T4_1_OnAnEpsR1Stack_TheLayeredOracle_IsExactlyFreeSpacePlusOneImage()
    {
        // The one multilayer case with an exact closed-form answer, so this is a genuine error
        // curve rather than a self-consistency check. THREE interior interfaces are present and
        // physically invisible; a cascade that mishandles any of them fails here.
        var stack = LayerStacks.AirOverGround();
        var g = new LayeredSpectralGreens(stack, 10e9);
        double h = stack.TopZ;
        double lambda = 2 * Math.PI / g.K0;

        double worstRel = 0, worstScaled = 0;
        foreach (double over in new[] { 1e-3, 1e-2, 0.1, 0.5, 1.0, 3.0 })
        foreach (var (z, zp) in new[] { (h, h), (h, h + 0.3 * lambda), (h + 0.1 * lambda, h + 0.6 * lambda) })
        foreach (var kind in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential })
        {
            double rho = over * lambda;
            var got = SommerfeldIntegral.EvaluateLayered(g, kind, rho, z, zp);
            Complex expected = SommerfeldIntegral.FreeSpace(g.K0, Math.Sqrt(rho * rho + (z - zp) * (z - zp)))
                             - SommerfeldIntegral.FreeSpace(g.K0, Math.Sqrt(rho * rho + (z + zp) * (z + zp)));
            double rel    = Rel(expected, got.Value);
            double scaled = (expected - got.Value).Magnitude * 4 * Math.PI * rho;
            worstRel = Math.Max(worstRel, rel);
            worstScaled = Math.Max(worstScaled, scaled);
            Assert.True(rel < 1e-7,
                $"{kind} at ρ/λ = {over}, z={z:G4}, z′={zp:G4}: {got.Value} vs free-space+image {expected}, rel {rel:E3}");
        }

        _out.WriteLine($"εᵣ = 1 over ground, three invisible interfaces: worst relative {worstRel:E3}, " +
                       $"worst scaled (|ΔG|·4πρ) {worstScaled:E3}");
    }

    [Theory]
    [MemberData(nameof(SlabCases))]
    public void T4_2_TheLayeredOracle_AgreesWithL8aOwnOneLayerOracle(
        string label, double epsR, double tanD, double heightM)
    {
        // Two independently written inverse transforms over the same physics: L8a's Evaluate builds
        // its numerator from GroundedSlab's closed-form Γ; EvaluateLayered builds it from the
        // cascade and finds its own poles. Agreement ties the new path to the validated old one.
        var slab  = new GroundedSlab(heightM, new EmMaterial(epsR, tanD));
        var stack = LayerStack.FromGroundedSlab(slab);
        var shipped = new SpectralGreens(slab, 10e9);
        var general = new LayeredSpectralGreens(stack, 10e9);
        double lambda = 2 * Math.PI / shipped.K0;
        double worst = 0;

        foreach (double over in new[] { 1e-3, 1e-2, 0.1, 1.0 })
        foreach (var kind in new[] { GreensKernel.VectorPotential, GreensKernel.ScalarPotential })
        {
            double rho = over * lambda;
            Complex a = SommerfeldIntegral.Evaluate(shipped, kind, rho).Value;
            Complex b = SommerfeldIntegral.EvaluateLayered(general, kind, rho, heightM, heightM).Value;
            double scaled = (a - b).Magnitude * 4 * Math.PI * rho;
            worst = Math.Max(worst, scaled);
            Assert.True(scaled < 1e-8,
                $"{label} {kind} at ρ/λ = {over}: L8a {a}, layered {b}, scaled {scaled:E3}");
        }

        _out.WriteLine($"{label}: layered oracle vs L8a's own oracle — worst scaled (|ΔG|·4πρ) {worst:E3}");
    }

    /// <summary>
    /// <b>§8.1's deliverable.</b> Where the layered oracle stops being trustworthy, in ρ/λ AND in
    /// the height pair, on a real multilayer stack — reported in BOTH measures, because they answer
    /// different questions and reporting only one misstates the result in both directions: the
    /// SCALED error (|ΔG| as a fraction of the free-space kernel at the same ρ) is what a matrix
    /// fill experiences, while the strict relative error says more about G_q's own deep
    /// cancellation zones than about the method wherever G_q is passing through a near-zero.
    /// </summary>
    [Theory]
    [Trait("Category", "Benchmark")]
    [InlineData(2e9)]
    [InlineData(10e9)]
    [InlineData(20e9)]
    public void T4_3_TheOraclesOwnConvergence_OverRhoAndHeightPairs_IsTheReportedCurve(double frequencyHz)
    {
        // Coarsened by 100× rather than refined by 100×, using SommerfeldSettings.Coarser — L8a's
        // own API for exactly this. It is the same trustworthiness statement (how much of the
        // answer is quadrature rather than physics) and it is affordable: RelativeTolerance is an
        // ABSOLUTE tolerance once scaled, so tightening it at ρ/λ = 10 drives every one of ~1200
        // base panels to maximum subdivision depth and the sweep never finishes.
        var coarse = SommerfeldSettings.Default.Coarser(100);

        foreach (var (name, stack) in new[] { ("PCB 3-layer", LayerStacks.Pcb3Layer),
                                              ("MMIC 2-level", LayerStacks.MmicTwoLevel) })
        {
            var g = new LayeredSpectralGreens(stack, frequencyHz);
            double h = stack.TopZ;
            double lambda = 2 * Math.PI / g.K0;
            double worstScaled = 0;

            _out.WriteLine($"--- {name} @ {frequencyHz / 1e9:F0} GHz  (H = {h * 1e3:G4} mm, λ = {lambda * 1e3:G4} mm, " +
                           $"TM={g.SurfaceWaves.TmCount} TE={g.SurfaceWaves.TeCount}) ---");
            _out.WriteLine("  kernel   ρ/λ        Σ/λ         |G|          scaled       strict rel");

            foreach (var kind in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
            foreach (double over in new[] { 1e-4, 1e-3, 1e-2, 0.1, 1.0, 10.0 })
            foreach (var (z, zp) in new[] { (h, h), (h + 0.1 * lambda, h + 0.4 * lambda) })
            {
                double rho = over * lambda;
                Complex fine   = SommerfeldIntegral.EvaluateLayered(g, kind, rho, z, zp).Value;
                Complex rough  = SommerfeldIntegral.EvaluateLayered(g, kind, rho, z, zp, coarse).Value;
                double scaled  = (fine - rough).Magnitude * 4 * Math.PI * rho;
                double strict  = Rel(fine, rough);
                worstScaled = Math.Max(worstScaled, scaled);
                _out.WriteLine($"  {(kind == GreensKernel.ScalarPotential ? "G_q" : "G_A")}   " +
                               $"{over,8:G3}  {(z + zp - 2 * h) / lambda,8:F3}  " +
                               $"{fine.Magnitude,11:E3}  {scaled,11:E3}  {strict,11:E3}");
            }

            _out.WriteLine($"  worst scaled movement under a 100× coarsening: {worstScaled:E3}");
            Assert.True(worstScaled < 1e-5,
                $"{name} @ {frequencyHz / 1e9} GHz: the oracle moved by {worstScaled:E3} when coarsened " +
                $"100× — it is not trustworthy there and nothing may be concluded from it");
        }
    }

    // =========================================================================================
    // M5 — cost.  §8.2: the per-sample cost of the cascade against the slab's closed form, and
    // what that projects to for L9b's DCIM budget.  Measure it; do not estimate it.
    // =========================================================================================

    [Fact]
    [Trait("Category", "Benchmark")]
    public void M5_ThePerSampleCostOfTheCascade_AgainstTheSlabsClosedForm()
    {
        const int n = 40000;
        var slab = GroundedSlab.Fr4Starter;
        double f = 10e9;
        var shipped = new SpectralGreens(slab, f);
        double k0 = shipped.K0;

        double closed = Time(() =>
        {
            var g = new SpectralGreens(slab, f);
            double h = slab.HeightM;
            for (int i = 0; i < n; i++)
                _ = g.Kernel(GreensKernel.ScalarPotential, (0.05 + 8.0 * i / n) * k0);
        });

        _out.WriteLine($"closed-form one-layer kernel : {closed / n * 1e6,8:F3} µs/sample");

        foreach (var (name, stack) in LayerStacks.All())
        {
            double t = Time(() =>
            {
                var g = new LayeredSpectralGreens(stack, f);
                double h = stack.TopZ;
                for (int i = 0; i < n; i++)
                    _ = g.KernelAtHeights(GreensKernel.ScalarPotential, (0.05 + 8.0 * i / n) * k0, h, h);
            });
            _out.WriteLine($"{name,-40}: {t / n * 1e6,8:F3} µs/sample   ×{t / closed,6:F2} the closed form " +
                           $"({stack.LayerCount} layer(s))");
        }

        // The small-k_ρ contour extraction is a ONE-OFF per (height pair, frequency) — the number
        // that decides whether R-lyr-4's Taylor path is affordable inside a DCIM fit at L9b.
        var st = LayerStacks.Pcb3Layer;
        double warm = Time(() =>
        {
            var g = new LayeredSpectralGreens(st, f);
            _ = g.KernelAtHeights(GreensKernel.ScalarPotential, 1e-6 * k0, st.TopZ, st.TopZ);
        });
        double reuse = Time(() =>
        {
            var g = new LayeredSpectralGreens(st, f);
            _ = g.KernelAtHeights(GreensKernel.ScalarPotential, 1e-6 * k0, st.TopZ, st.TopZ);
            for (int i = 0; i < n; i++)
                _ = g.KernelAtHeights(GreensKernel.ScalarPotential, (1e-8 + 1e-3 * i / n) * k0, st.TopZ, st.TopZ);
        });
        _out.WriteLine($"small-k_ρ Taylor extraction  : {warm * 1e6,8:F1} µs once per (height pair, frequency); " +
                       $"{(reuse - warm) / n * 1e6:F3} µs/sample thereafter");

        double poleTime = Time(() => _ = SurfaceWavePoles.Find(LayerStacks.Pcb3Layer, f));
        _out.WriteLine($"surface-wave pole search     : {poleTime * 1e3,8:F1} ms once per (stack, frequency)");

        static double Time(Action a)
        {
            a();                                   // warm the JIT and any per-instance caches
            var sw = System.Diagnostics.Stopwatch.StartNew();
            a();
            return sw.Elapsed.TotalSeconds;
        }
    }
}
