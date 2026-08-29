using System.Diagnostics;
using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>Phase L9b — DCIM for the general layered medium, and its oracle ladder.</b>
///
/// <para>L8a's ordering rule, unchanged: <b>each tier passes before the next is written</b>, the
/// exact reductions come before anything empirical, and <b>when a rung disagrees the first
/// hypothesis is the rung</b> — this area has now had five occasions where the ORACLE, not the
/// method, was at fault. Tier 4's oracle was re-checked on the two open-below stacks before a single
/// number below it was believed (<see cref="R4_0_TheOracleIsTrustworthyOnTheOpenBelowStacks"/>).</para>
///
/// <list type="bullet">
///   <item><b>Tier 0</b> — structural and free: the branch-point sum rule on every stack, and the
///         k_z0 entry point agreeing with the k_ρ one where both are defined AND reaching where the
///         k_ρ one cannot.</item>
///   <item><b>Tier 1</b> — the one-layer reduction. The SAMPLES the fit is built from are
///         machine-precision identical; the fit's own discrete decisions are not, and that is the
///         finding rather than a tolerance.</item>
///   <item><b>Tier 2</b> — split-a-layer invariance of the FIT.</item>
///   <item><b>Tier 3</b> — the ω → 0 limit against a genuinely electrostatic solver.</item>
///   <item><b>Tier 4</b> — DCIM against direct integration: <b>the R-dcm-4 curve, and the reported
///         measurement of this slice.</b></item>
///   <item><b>Tier 5</b> — the height-pair rung: D5's shift theorem, measured.</item>
/// </list>
/// </summary>
public sealed class LayeredDcimTests
{
    private readonly ITestOutputHelper _out;
    public LayeredDcimTests(ITestOutputHelper output) => _out = output;

    private static double Rel(Complex expected, Complex actual, double floor = 1e-300) =>
        (expected - actual).Magnitude / Math.Max(expected.Magnitude, floor);

    // =============================================================================================
    // TIER 0 — structural, free, no inverse transform anywhere.
    // =============================================================================================

    /// <summary>
    /// <b>D2 — the far-field sum rule is still a THEOREM for a cascade, and this is the measurement
    /// that says so rather than the argument that says so.</b>
    ///
    /// <para>At k_z0 = 0 the top interface's cross-multiplied Fresnel coefficient reduces to
    /// <c>(µ_b·0 − µ_t k_zb)/(µ_b·0 + µ_t k_zb) = −1</c> for TE and <c>+1</c> for TM <b>whatever is
    /// below</b>, because k_z0 is the only thing that vanishes and the Möbius ladder underneath
    /// enters only through the other, finite, argument. A Möbius step with r = ±1 returns ±1
    /// regardless of its second argument, so Γ^h → −1, Γ^e → +1 and
    /// <c>Γ^q = Γ^e − (k₀²/k_ρ²)(Γ^e − Γ^h) = 1 − 2 = −1</c>. Hence <c>1 + Γ</c> vanishes identically
    /// at the branch point for any number of layers, provided the top termination is an open
    /// half-space — which is what keeps <c>Σ A_i = −(1 + Γ(∞))</c> exact and
    /// <c>BranchPointOrders = 1</c> a theorem rather than a knob.</para>
    /// </summary>
    [Fact]
    public void R0_1_TheBranchPointSumRule_SurvivesGeneralisation_OnEveryStack()
    {
        double worst = 0;
        foreach (var (name, stack) in LayerStacks.All())
        foreach (double f in new[] { 2e9, 10e9, 20e9 })
        {
            var g = new LayeredSpectralGreens(stack, f);
            foreach (var k in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
            {
                double residual = (1.0 + g.TopInterfaceReflectionAtKz0(k, Complex.Zero)).Magnitude;
                worst = Math.Max(worst, residual);
                Assert.True(residual < 1e-14,
                    $"{name} @ {f / 1e9} GHz, {k}: 1 + Γ(k_z0 = 0) = {residual:E3}, which is the " +
                    $"quantity the whole far-field constraint rests on.");
            }
        }
        _out.WriteLine($"1 + Γ at k_z0 = 0: worst |1 + Γ| over every stack, both kernels, " +
                       $"2/10/20 GHz = {worst:E3}");
    }

    /// <summary>Where both parameterisations are defined they must agree — otherwise the new entry
    /// point is a second, subtly different kernel rather than the same one asked differently.</summary>
    [Fact]
    public void R0_2_TheKz0EntryPoint_AgreesWithTheKRhoOne_WhereverBothAreDefined()
    {
        double worst = 0; string at = "";
        foreach (var (name, stack) in LayerStacks.All())
        foreach (double f in new[] { 2e9, 10e9, 20e9 })
        {
            var g = new LayeredSpectralGreens(stack, f);
            foreach (double u in new[] { 1e-6, 1e-3, 0.1, 0.5, 0.9, 1.001, 1.5, 3.0, 10.0, 100.0 })
            foreach (var k in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
            {
                Complex kRho = u * g.K0;
                double rel = Rel(g.TopInterfaceReflection(k, kRho),
                                 g.TopInterfaceReflectionAtKz0(k, g.Kz0(kRho)));
                if (rel > worst) { worst = rel; at = $"{name} @ {f / 1e9} GHz, k_ρ/k₀ = {u}, {k}"; }
            }
        }
        Assert.True(worst < 1e-11, $"k_z0 vs k_ρ parameterisation disagree by {worst:E3} at {at}");
        _out.WriteLine($"k_z0-parameterised vs k_ρ-parameterised Γ: worst relative {worst:E3} ({at})");
    }

    /// <summary>
    /// <b>The half that makes the entry point load-bearing rather than decorative (Tier 0).</b> It
    /// must reach k_z0 &lt; 0, which the k_ρ route cannot express at all — <c>w = k_top² − k_z0²</c> is
    /// EVEN in k_z0, so a round trip through k_ρ silently returns the positive branch. This test pins
    /// the round trip losing the sign, the answer genuinely differing between the two signs, and the
    /// function being smooth (analytic) straight through k_z0 = 0, which is what makes DCIM's
    /// central-difference Taylor expansion there legitimate rather than a fudge.
    /// </summary>
    [Fact]
    public void R0_3_TheKz0EntryPoint_ReachesNegativeKz0_WhereTheKRhoRouteCannot()
    {
        foreach (var (name, stack) in LayerStacks.All())
        {
            var g = new LayeredSpectralGreens(stack, 10e9);
            foreach (var k in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
            {
                double d = 1e-3 * g.K0;                       // Dcim.BranchPointTaylor's own step

                // 1. the round trip through k_ρ cannot even carry the question: w = k_top² − k_z0² is
                //    even, so k_ρ(−d) and k_ρ(+d) are the same number and k_z0 comes back positive.
                Complex kRhoAtMinusD = Complex.Sqrt(g.TopWavenumberSquared - (-d) * (-d));
                Complex roundTrip    = g.Kz0(kRhoAtMinusD);
                Assert.True(roundTrip.Real > 0,
                    $"{name}: k_ρ → k_z0 returned {roundTrip}, so the round trip did not lose the sign " +
                    $"and this test has stopped demonstrating why the k_z0 entry point exists.");

                // 2. the two signs are genuinely different values, so the sign is not decoration.
                Complex plus  = g.TopInterfaceReflectionAtKz0(k, d);
                Complex minus = g.TopInterfaceReflectionAtKz0(k, -d);
                Assert.True(double.IsFinite(minus.Real) && double.IsFinite(minus.Imaginary),
                    $"{name}, {k}: Γ at k_z0 = −{d:E2} is {minus}");
                Assert.True((plus - minus).Magnitude > 1e-9 * Math.Max(1, plus.Magnitude),
                    $"{name}, {k}: Γ(+d) and Γ(−d) agree to {(plus - minus).Magnitude:E2} — the sign is " +
                    $"being discarded somewhere.");

                // 3. and it is ANALYTIC through zero: the second central difference converges as d².
                Complex zero = g.TopInterfaceReflectionAtKz0(k, Complex.Zero);
                double c1 = (plus + minus - 2.0 * zero).Magnitude;
                Complex p2 = g.TopInterfaceReflectionAtKz0(k, 2 * d);
                Complex m2 = g.TopInterfaceReflectionAtKz0(k, -2 * d);
                double c2 = (p2 + m2 - 2.0 * zero).Magnitude;
                Assert.True(c2 > 3.5 * c1 && c2 < 4.5 * c1,
                    $"{name}, {k}: Γ(±d) + Γ(∓d) − 2Γ(0) is {c1:E3} at d and {c2:E3} at 2d — a ratio of " +
                    $"{c2 / c1:F2} rather than the 4 an analytic function gives, so Γ is not analytic " +
                    $"through k_z0 = 0 and the branch-point Taylor expansion has no meaning.");
            }
        }
        _out.WriteLine("k_z0 < 0 is reachable, sign-carrying and analytic through zero on every stack.");
    }

    // =============================================================================================
    // TIER 1 — the one-layer reduction (R-dcm-1).
    // =============================================================================================

    /// <summary>
    /// <b>R-dcm-1 — the shipped one-layer fit is BIT-IDENTICAL after the refactor.</b>
    ///
    /// <para>The values below were captured at full precision from <c>Dcim.Fit(SpectralGreens, …)</c>
    /// <i>before</i> the shared internals were touched, exactly as L7b-b did when it reconstructed
    /// its pre-change extractor and found two one-ulp re-associations that way. The spatial
    /// evaluations are the strong part: each one folds every image amplitude, every image depth,
    /// every pole residue and the quasi-static constant into one number, so a single changed bit
    /// anywhere in the fit shows up here.</para>
    ///
    /// <para>The Tier 2 oracles carry tolerances and structurally cannot catch a one-ulp move, which
    /// is why this test asserts exact equality and not a tolerance.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(PinnedOneLayerFits))]
    public void R1_1_TheShippedOneLayerFit_IsUnchangedBitForBit(
        string label, double f, GreensKernel kernel, int images, double fitResidual, double[] spatial)
    {
        var slab = label == "Fr4" ? GroundedSlab.Fr4Starter : GroundedSlab.GaAsStarter;
        var m = Dcim.Fit(new SpectralGreens(slab, f), kernel);
        double lam = EmConstants.C0 / f;

        Assert.Equal(images, m.Images.Count);
        Assert.Equal(fitResidual, m.FitResidual);
        Assert.Equal(new Complex(spatial[0], spatial[1]), m.Evaluate(1e-3 * lam));
        Assert.Equal(new Complex(spatial[2], spatial[3]), m.Evaluate(0.1  * lam));
        Assert.Equal(new Complex(spatial[4], spatial[5]), m.Evaluate(3.0  * lam));
    }

    /// <summary>Captured before the L9b refactor; see <see cref="R1_1_TheShippedOneLayerFit_IsUnchangedBitForBit"/>.</summary>
    public static TheoryData<string, double, GreensKernel, int, double, double[]>
        PinnedOneLayerFits() => new()
    {
        { "Fr4", 2000000000, GreensKernel.ScalarPotential, 14, 2.668395569993774E-06, new[] { 184.8916468835523, 3.0557797443743477, -0.014983207011568737, 0.018279984313007664, -0.0010329947180506384, 0.0002825825340174807 } },
        { "Fr4", 2000000000, GreensKernel.VectorPotential, 17, 8.623704177871786E-07, new[] { 506.47436594237996, -0.01575768198323402, 0.1396256740292309, -0.009738463715273257, 4.585605497635179E-06, 8.533427668123701E-05 } },
        { "Fr4", 10000000000, GreensKernel.ScalarPotential, 20, 3.170824338035376E-05, new[] { 970.6371384157627, 19.431622483163487, 0.9135825003300502, 3.286957699622521, -0.026547233426626793, 0.36505778069762995 } },
        { "Fr4", 10000000000, GreensKernel.VectorPotential, 13, 3.3268365535589477E-06, new[] { 2641.6907766420322, -1.787809741536956, 12.249617392137536, -1.5892061698960824, 0.0009794145446389985, 0.01394331684027406 } },
        { "Fr4", 20000000000, GreensKernel.ScalarPotential, 20, 6.803572050865606E-05, new[] { 1991.8581096696328, 43.750280748266, 42.39324545420013, 7.896224661356115, -4.088959712828667, -1.4768137206316514 } },
        { "Fr4", 20000000000, GreensKernel.VectorPotential, 20, 8.563309305068827E-06, new[] { 5339.000290690801, -29.747282993085644, 60.05534990182736, -27.926340669692806, 0.13867920381940063, 0.39749516096545123 } },
        { "GaAs", 2000000000, GreensKernel.ScalarPotential, 19, 1.321229320895182E-07, new[] { 16.735717046760094, -0.9603281088934549, -0.00012520656665497093, 9.373892069884601E-05, -4.754694519774632E-06, 6.664236469083232E-07 } },
        { "GaAs", 2000000000, GreensKernel.VectorPotential, 6, 1.8701878155989604E-12, new[] { 212.51559342817234, -6.954564226987884E-05, 0.0005568317063973842, -3.755642060680154E-05, 1.7504618938587747E-08, 3.299483127944926E-07 } },
        { "GaAs", 10000000000, GreensKernel.ScalarPotential, 14, 5.888827207598031E-06, new[] { 305.9564922412385, 0.5862489058529179, -0.01576195277172504, 0.012026597966789732, -0.0005437294806012106, 3.5842436553645E-05 } },
        { "GaAs", 10000000000, GreensKernel.VectorPotential, 4, 1.607748399832747E-07, new[] { 2262.2248318220936, -0.007011488273001021, 0.06963346827414796, -0.004710411233837419, 2.196658614228575E-06, 4.13817535443217E-05 } },
        { "GaAs", 20000000000, GreensKernel.ScalarPotential, 14, 8.918402691711008E-06, new[] { 686.258364843482, 1.388663046380802, -0.12760873295101402, 0.09993944619811196, -0.0048353646169701685, 0.0009758079995666577 } },
        { "GaAs", 20000000000, GreensKernel.VectorPotential, 16, 1.2197592178173926E-07, new[] { 4917.900097977073, -0.049362464676609576, 0.5577676695619228, -0.03808416104228351, 1.7729891626536844E-05, 0.0003345425584489689 } },
    };

    /// <summary>
    /// <b>Tier 1 for the FIT, and the brief's expected answer is the one thing measured that
    /// does NOT hold — for a structural reason worth writing down.</b>
    ///
    /// <para>What the general path and the shipped path share is the sampled remainder, and
    /// <b>that</b> is machine-precision identical: L9a's Tier 1 puts Γ^e/Γ^h/Γ^q at 6.2e-14, and this
    /// test confirms the samples DCIM actually consumes — remainder-after-pole-subtraction, along the
    /// real fitting path — at the same level.</para>
    ///
    /// <para><b>The fit built from them is not.</b> Prony chooses its ORDER by a residual threshold,
    /// its roots by Durand-Kerner, and the final image set by scoring three candidate depth sets
    /// against each other; every one of those is a DISCRETE decision taken on samples that differ in
    /// the last two digits. Measured, the two paths pick different orders often enough that the image
    /// COUNT differs (13 vs 19 on FR-4's G_A at 10 GHz, 6 vs 14 on GaAs's at 2 GHz) while both fits
    /// are equally good against the oracle. Demanding bit-identity here would be demanding that a
    /// discontinuous function of the samples be continuous.</para>
    ///
    /// <para>So the honest gate is the one Tier 4 answers: the general path's error <i>against the
    /// oracle</i> must match the shipped path's. This test pins the input identity — which is what a
    /// regression would actually break — and reports the output divergence rather than hiding it.</para>
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void R1_2_TheOneLayerReduction_FeedsTheFitIdenticalSamples_ThoughTheFitItselfDiverges()
    {
        double worstSample = 0; string at = "";
        _out.WriteLine("                                 samples      images      fit residual");
        foreach (var (label, slab) in new[]
                 {
                     ("FR-4", GroundedSlab.Fr4Starter),
                     ("GaAs", GroundedSlab.GaAsStarter),
                 })
        foreach (double f in new[] { 2e9, 10e9, 20e9 })
        foreach (var k in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
        {
            var one = new SpectralGreens(slab, f);
            var gen = new LayeredSpectralGreens(LayerStack.FromGroundedSlab(slab), f);

            // The DCIM sampling path itself, k_z0(t) = k₀[(1 − t/T) − jt], on both parameterisations.
            double sampleWorst = 0;
            for (int i = 0; i < 200; i++)
            {
                double t = 300.0 * i / 199.0;
                Complex kz0 = one.K0 * new Complex(1.0 - t / 300.0, -t);
                double rel = Rel(one.ReflectionAtKz0(k, kz0), gen.TopInterfaceReflectionAtKz0(k, kz0));
                if (rel > sampleWorst) sampleWorst = rel;
            }
            if (sampleWorst > worstSample) { worstSample = sampleWorst; at = $"{label} {f / 1e9} GHz {k}"; }

            var a = Dcim.Fit(one, k);
            var b = Dcim.Fit(gen, k);
            _out.WriteLine($"{label} {f / 1e9,2:F0} GHz {k,-16} {sampleWorst:E2}   " +
                           $"{a.Images.Count,2} vs {b.Images.Count,-2}   " +
                           $"{a.FitResidual:E2} vs {b.FitResidual:E2}");
        }

        // 1e-10, and the reason is L9a's own recorded one rather than a chosen tolerance: the
        // sampling path runs out to |k_z0| ≈ 300 k₀, and past ~100 k₀ the two kernels compute the
        // SAME exactly-Fresnel limit by two different underflowing routes — the shipped one saturates
        // tan at |Im| > 30, the cascade's e^{−2jk_z d} has already flushed to zero — on quantities
        // that have decayed to ~1e-5 of unity. L9a measured that as 1e-12 on Γ itself; the DCIM path
        // goes three times further out.
        Assert.True(worstSample < 1e-10,
            $"the two kernels disagree by {worstSample:E3} on the samples the fit is built from, at {at}");
        _out.WriteLine($"\nworst relative disagreement on the SAMPLED remainder = {worstSample:E3} ({at})");
    }

    // =============================================================================================
    // TIER 2 — split-a-layer invariance of the FIT.
    // =============================================================================================

    /// <summary>
    /// L9a's Tier 2 for the kernel; here for the model. Splitting a layer into sub-layers of the same
    /// material changes no physics, so the fitted answer must not move.
    ///
    /// <para><b>It moves by the fit's OWN accuracy, not by the kernel's, and that is R1_2's finding
    /// showing up again.</b> L9a measured the kernel's split-invariance at 1.1e-13 (not bit-identical,
    /// because <c>exp(a)exp(b) ≠ exp(a+b)</c>). The fit turns that into whatever its discrete
    /// decisions turn it into: where the answer is well-determined the two fits agree to 1e-8, and
    /// where the fit is at its own resolution limit they differ by that limit. So the gate is over the
    /// window where the fit is well-determined, and the full row is REPORTED so the two ends are
    /// visible rather than averaged away.</para>
    ///
    /// <para>The two ends are both explicable. <b>Below ρ/λ ≈ 1e-3 the near field is limited by
    /// <c>PathExtent</c></b>: the path reaches k_ρ = 300 k₀, so structure finer than
    /// <c>1/(300 k₀) = λ/1885</c> was never sampled — and the MMIC stack has a 3 µm spacer sitting
    /// exactly at ρ/λ = 1e-4 at 10 GHz. <b>Above ρ/λ ≈ 3 it is the far field</b>, where R4's own
    /// curve puts G_q's accuracy at 6e-3–2e-2 anyway.</para>
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void R2_1_SplittingALayer_DoesNotMoveTheFittedAnswer()
    {
        double f = 10e9, lam = EmConstants.C0 / f;
        _out.WriteLine("scaled move |ΔG|·4πρ at ρ/λ = 1e-4 … 10, half a decade apart:\n");
        foreach (var (name, stack, index, fractions) in new[]
                 {
                     ("PCB 3-layer, middle in halves", LayerStacks.Pcb3Layer,    1, new[] { 0.5, 0.5 }),
                     ("PCB 3-layer, bottom 0.3/0.7",   LayerStacks.Pcb3Layer,    0, new[] { 0.3, 0.7 }),
                     ("MMIC, GaAs in thirds",          LayerStacks.MmicTwoLevel, 0, new[] { 1.0, 1.0, 1.0 }),
                     ("MMIC, spacer in halves",        LayerStacks.MmicTwoLevel, 1, new[] { 0.5, 0.5 }),
                 })
        {
            var split = stack.WithLayerSplit(index, fractions);
            foreach (var k in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
            {
                var a = Dcim.Fit(new LayeredSpectralGreens(stack, f), k);
                var b = Dcim.Fit(new LayeredSpectralGreens(split, f), k);
                var row = new List<string>();
                double worst = 0, worstAt = 0;
                for (double rl = 1e-4; rl <= 10.001; rl *= Math.Pow(10, 0.5))
                {
                    double rho = rl * lam;
                    // Scaled, as everywhere else: G_q's dipole cancellation zone makes a relative
                    // measure say more about the zero than about the fit (R-dcm-4).
                    double d = (a.Evaluate(rho) - b.Evaluate(rho)).Magnitude * 4 * Math.PI * rho;
                    row.Add($"{d:E1}");
                    if (rl >= 9e-4 && rl <= 1.01 && d > worst) { worst = d; worstAt = rl; }
                }
                _out.WriteLine($"{name,-32} {(k == GreensKernel.ScalarPotential ? "G_q" : "G_A")} " +
                               $"{string.Join(" ", row)}");
                Assert.True(worst < 1e-3,
                    $"{name}, {k}: splitting a layer moved the fitted kernel by {worst:E3} (scaled) at " +
                    $"ρ/λ = {worstAt:E2} — inside the window where the fit is well determined, so this " +
                    $"is physics the split cannot have changed.");
            }
        }
    }

    // =============================================================================================
    // TIER 3 — the static limit.
    // =============================================================================================

    /// <summary>
    /// As ω → 0 the fitted model must converge onto <see cref="LayeredStaticGreens"/> quadratically
    /// and with no floor.
    ///
    /// <para><b>THE FINDING OF THIS RUNG: it does, but only if the sampled k_ρ range is held fixed in
    /// PHYSICAL units, and DCIM's default does not.</b> <c>DcimSettings.PathExtent = 300</c> means the
    /// path reaches <c>k_ρ = 300 k₀</c>, while the thing being sampled — the stack's image structure —
    /// lives at <c>k_ρ ~ 1/H</c>, which does not move with frequency. The product
    /// <c>300·k₀H</c> is therefore what decides whether the fit sees the stack at all, and on this
    /// 1.4 mm stack it falls through 1 between 300 MHz and 100 MHz. Below that the error <b>grows</b>
    /// as the frequency falls, which is not a floor and is not the oracle: it is the fit sampling a
    /// range that no longer contains the physics. Holding <c>k_ρ,max·H = 100</c> instead gives ratios
    /// of <b>11.19 and 9.16 against the 11.1 and 9.0 that (f₁/f₂)² predicts</b> — exactly quadratic.
    /// Both columns are reported below so the effect is visible rather than worked around.</para>
    ///
    /// <para>This is a genuine low-frequency limit on the FIT, and it sits <b>far above</b>
    /// <c>GroundedSlab.MinElectricalThickness</c> (k₀H ≥ 1e-6), which is a limit on the KERNEL. It is
    /// recorded rather than acted on: changing a shipped default belongs with L9e's audit, and a
    /// frequency-aware path extent is exactly the shape of L9e's adaptive frequency sampling.</para>
    ///
    /// <para>L8a's warning still applies to everything below the turn-around: <b>a floor there would
    /// mean the ORACLE is wrong</b> — that has happened five times in this area, most recently a
    /// static series that needed a COMPLEX K and read exactly like a convergence floor without it.</para>
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void R3_1_AsFrequencyFalls_TheFittedModel_ConvergesQuadraticallyOntoTheStaticSolver()
    {
        var stack = LayerStacks.Pcb3Layer;
        double h = stack.TopZ, rho = 3.0 * h;
        Complex stat = LayeredStaticGreens.ScalarPotential(stack, rho, h, h);
        _out.WriteLine($"static reference at ρ = 3H: {stat:E8}\n");
        _out.WriteLine("            k₀H        default T₀ = 300      k_ρ,max·H held at 100");

        var scaled = new List<double>();
        var plain  = new List<double>();
        double[] frequencies = [1e9, 300e6, 100e6];
        foreach (double f in frequencies)
        {
            var g = new LayeredSpectralGreens(stack, f);
            double t0 = Math.Max(300.0, 100.0 / (g.K0 * h));

            double a = Rel(stat, Dcim.Fit(g, GreensKernel.ScalarPotential).Evaluate(rho));
            double b = Rel(stat, Dcim.Fit(g, GreensKernel.ScalarPotential,
                                          DcimSettings.Default with { PathExtent = t0 }).Evaluate(rho));
            plain.Add(a);
            scaled.Add(b);
            _out.WriteLine($"{f / 1e6,6:F0} MHz  {g.K0 * h:E2}   {a:E3} (300·k₀H = {300 * g.K0 * h:F2})" +
                           $"   {b:E3} (T₀ = {t0:E2})");
        }

        // (f₁/f₂)² is 11.1 for 1000→300 and 9.0 for 300→100.
        double[] predicted = [11.1, 9.0];
        for (int i = 0; i + 1 < scaled.Count; i++)
        {
            double ratio = scaled[i] / scaled[i + 1];
            _out.WriteLine($"  step {i}: ratio {ratio:F2} against the {predicted[i]:F1} predicted");
            Assert.True(ratio > 0.65 * predicted[i],
                $"convergence stalled between step {i} and {i + 1}: ratio {ratio:F2} against the " +
                $"{predicted[i]:F1} that (f₁/f₂)² predicts — a FLOOR, which on past form means the " +
                $"oracle is wrong rather than the method.");
        }

        // And the default's failure is pinned, so this test cannot quietly stop demonstrating it.
        Assert.True(plain[2] > plain[1],
            $"the DEFAULT path extent no longer degrades as the frequency falls ({plain[1]:E2} at " +
            $"300 MHz against {plain[2]:E2} at 100 MHz) — either it was fixed, in which case this " +
            $"note should go, or the measurement has stopped measuring it.");
    }

    // =============================================================================================
    // TIER 4 — DCIM against direct integration.  THE REPORTED MEASUREMENT (R-dcm-4).
    // =============================================================================================

    /// <summary>
    /// <b>Check the oracle before concluding anything from it — the rule that has now cost this area
    /// five milestones.</b> The two open-below stacks are the least comfortable configuration this
    /// contour has ever been asked for: the alumina one puts a pole 1.0e-8 of its own real part off
    /// the axis, and the silicon one carries a genuine second branch point at k_ρ = k_b = 3.45 k₀,
    /// i.e. a square-root kink sitting inside segment B. If the oracle were wrong there, D3's answer
    /// would be an artefact of it.
    ///
    /// <para>Tagged Benchmark: it is ~7 minutes, essentially all of it the deliberately over-tight
    /// reference integration.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void R4_0_TheOracleIsTrustworthyOnTheOpenBelowStacks()
    {
        double worstRel = 0, worstScaled = 0;
        foreach (var (name, stack) in new[]
                 {
                     ("Alumina, open below", LayerStacks.OpenBelow),
                     ("Oxide on silicon",    LayerStacks.FilmOnSilicon),
                 })
        {
            var g = new LayeredSpectralGreens(stack, 10e9);
            double lam = EmConstants.C0 / 10e9, h = stack.TopZ;
            _out.WriteLine($"--- {name} @ 10 GHz ---");
            for (double rl = 1e-4; rl <= 10.001; rl *= 10)
            foreach (var k in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
            {
                double rho = rl * lam;
                var coarse = SommerfeldIntegral.EvaluateLayered(g, k, rho, h, h);
                var fine   = SommerfeldIntegral.EvaluateLayered(g, k, rho, h, h,
                                 SommerfeldSettings.Default with
                                 { RelativeTolerance = 1e-13, OscillationDensity = 32 });
                double rel    = Rel(fine.Value, coarse.Value);
                double scaled = (fine.Value - coarse.Value).Magnitude * 4 * Math.PI * rho;
                worstRel = Math.Max(worstRel, rel);
                worstScaled = Math.Max(worstScaled, scaled);
                _out.WriteLine($"  ρ/λ={rl,8:E1} {k,-16} |G| = {coarse.Value.Magnitude:E4}   " +
                               $"a 100× refinement moves it by rel {rel:E2}, scaled {scaled:E2}");
                Assert.True(coarse.TailConverged && fine.TailConverged,
                    $"{name} at ρ/λ = {rl:E1}, {k}: the tail series did not converge, so this " +
                    $"reference value is not one.");
            }
        }
        _out.WriteLine($"\nThe oracle moves by at most rel {worstRel:E2} / scaled {worstScaled:E2} under a " +
                       $"100× refinement on BOTH open-below stacks. Anything Tier 4 finds there is the " +
                       $"method's, not the reference's.");
        Assert.True(worstScaled < 1e-9, $"the oracle itself moves by {worstScaled:E3} (scaled)");
    }

    /// <summary>The routine gate: one grounded multilayer stack, coarse ρ grid. The full reported
    /// curve is <see cref="R4_2_TheFullErrorCurve_EveryStackAcrossTheBand"/> and is opt-in.</summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void R4_1_TheDcimError_AgainstDirectIntegration_OnAMultilayerStack() =>
        MeasureErrorCurve("PCB 3-layer 0.8/0.5/0.1 mm", LayerStacks.Pcb3Layer, 10e9, step: 0.5);

    /// <summary>
    /// <b>*** THE DELIVERABLE OF L9b (R-dcm-4). ***</b> Not "DCIM works for a general medium" — the
    /// CURVE, per stack, per kernel, across the band, and where it stops being trustworthy in each
    /// variable. L9c's matrix fill is scheduled against the SCALED column.
    /// </summary>
    [Theory]
    [Trait("Category", "Benchmark")]
    [MemberData(nameof(FullCurveCases))]
    public void R4_2_TheFullErrorCurve_EveryStackAcrossTheBand(string name, int stackIndex, double f) =>
        MeasureErrorCurve(name, LayerStacks.All().ElementAt(stackIndex).Stack, f, step: 0.25);

    public static TheoryData<string, int, double> FullCurveCases()
    {
        var data = new TheoryData<string, int, double>();
        int i = 0;
        foreach (var (name, _) in LayerStacks.All())
        {
            foreach (double f in new[] { 2e9, 10e9, 20e9 }) data.Add(name, i, f);
            i++;
        }
        return data;
    }

    private void MeasureErrorCurve(string name, LayerStack stack, double f, double step)
    {
        var g = new LayeredSpectralGreens(stack, f);
        double lam = EmConstants.C0 / f, h = stack.TopZ;

        var models = new Dictionary<GreensKernel, DcimModel>
        {
            [GreensKernel.ScalarPotential] = Dcim.Fit(g, GreensKernel.ScalarPotential),
            [GreensKernel.VectorPotential] = Dcim.Fit(g, GreensKernel.VectorPotential),
        };

        _out.WriteLine($"=== {name}, f = {f / 1e9:F0} GHz, k₀H = {g.K0 * h:E3} ===");
        foreach (var (kind, m) in models)
            _out.WriteLine($"  {kind,-16}: {m.Images.Count} images, {m.SurfaceWaves.Count} surface-wave " +
                           $"term(s), spectral fit residual {m.FitResidual:E2}, " +
                           $"sum-rule residual {m.SumRuleResidual.Magnitude:E2}");
        foreach (var sw in models[GreensKernel.ScalarPotential].SurfaceWaves)
            _out.WriteLine($"  pole {sw.Name}: k_ρ/k₀ = {sw.KRho / g.K0}, residue {sw.Residue:E3}");
        _out.WriteLine($"  Dcim.CanFit: {(Dcim.CanFit(stack).Ok ? "yes" : "NO")}");

        _out.WriteLine("");
        _out.WriteLine("   ρ/λ        ρ (m)        |G_q| direct      rel G_q    scaled G_q    " +
                       "rel G_A    scaled G_A");

        var worstRel    = new Dictionary<GreensKernel, (double Err, double At)>();
        var worstScaled = new Dictionary<GreensKernel, (double Err, double At)>();
        var inRange     = new Dictionary<GreensKernel, (double Err, double At)>();
        foreach (var k in models.Keys)
        { worstRel[k] = (0, 0); worstScaled[k] = (0, 0); inRange[k] = (0, 0); }
        double relBreaks = double.PositiveInfinity;

        for (double rl = 1e-4; rl <= 10.001; rl *= Math.Pow(10, step))
        {
            double rho = rl * lam;
            double freeSpaceScale = 1.0 / (4 * Math.PI * rho);
            var cells = new List<string>();
            double magQ = 0;

            foreach (var kind in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
            {
                Complex exact = SommerfeldIntegral.EvaluateLayered(g, kind, rho, h, h).Value;
                Complex got   = models[kind].Evaluate(rho);
                double abs    = (got - exact).Magnitude;
                double rel    = abs / exact.Magnitude;
                double scaled = abs / freeSpaceScale;

                if (kind == GreensKernel.ScalarPotential)
                {
                    magQ = exact.Magnitude;
                    if (rel > 1e-2 && rl < relBreaks) relBreaks = rl;
                }
                if (rel    > worstRel[kind].Err)    worstRel[kind]    = (rel, rl);
                if (scaled > worstScaled[kind].Err) worstScaled[kind] = (scaled, rl);
                if (Dcim.WithinValidatedRangeLayered(kind, g, rl).Ok && scaled > inRange[kind].Err)
                    inRange[kind] = (scaled, rl);
                cells.Add($"{rel,10:E3} {scaled,13:E3}");
            }
            _out.WriteLine($"{rl,8:E2}  {rho,11:E3}  {magQ,15:E5}  {string.Join("  ", cells)}");
        }

        _out.WriteLine("");
        foreach (var k in models.Keys)
            _out.WriteLine($"  {k,-16} worst relative {worstRel[k].Err:E3} at ρ/λ = {worstRel[k].At:E2}  |  " +
                           $"worst scaled {worstScaled[k].Err:E3} at ρ/λ = {worstScaled[k].At:E2}");
        _out.WriteLine($"  strict relative accuracy on G_q first exceeds 1e-2 at ρ/λ = " +
                       (double.IsInfinity(relBreaks) ? "never in this span" : $"{relBreaks:E2}"));

        // ---- The gate is on what the refusal CLAIMS, and only on that (R-dcm-6). A stack or a ρ
        // this fit cannot represent is refused BY NAME rather than tolerated at a loosened threshold,
        // and the refusal has to be EARNED: if the answer out there were fine, the refusal would be
        // the thing that is wrong.
        var whole = Dcim.WithinValidatedRangeLayered(GreensKernel.ScalarPotential, g, 0.5);
        if (!whole.Ok)
        {
            _out.WriteLine($"\n  REFUSED for every ρ: {whole.Reason}");
            Assert.True(worstScaled[GreensKernel.ScalarPotential].Err > 3e-2,
                $"{name}: this stack is refused, but the fit is accurate to " +
                $"{worstScaled[GreensKernel.ScalarPotential].Err:E3} (scaled) — the refusal is then " +
                $"unearned and must be narrowed rather than left standing.");
            return;
        }

        foreach (var k in models.Keys)
        {
            _out.WriteLine($"  {k,-16} worst scaled INSIDE the validated range " +
                           $"{inRange[k].Err:E3} at ρ/λ = {inRange[k].At:E2}");
            Assert.True(inRange[k].Err < 3e-2,
                $"{name} at {f / 1e9:F0} GHz: worst SCALED DCIM {k} error INSIDE the range " +
                $"Dcim.WithinValidatedRangeLayered admits is {inRange[k].Err:E3} at " +
                $"ρ/λ = {inRange[k].At:E2}");
        }
    }

    // =============================================================================================
    // TIER 5 — the height-pair rung (D5), and D6's obstruction.
    // =============================================================================================

    /// <summary>
    /// <b>D5 — the shift is EXACT ALGEBRA, and this pins the algebra separately from the accuracy.</b>
    ///
    /// <para>At Σ = Δ = 0 the height-pair form must reproduce <see cref="DcimModel.Evaluate(double)"/>
    /// to rounding; and on the εᵣ = 1 control stack — where Γ is exactly <c>−e^{−2jk_z0 H}</c>, one
    /// image and no fit error at all — the shifted answer must be exactly free space minus a PEC
    /// image at depth <c>z + z′</c>, which is elementary and needs no oracle. If the shift bookkeeping
    /// were wrong, this is where it would show, uncontaminated by how good the fit is.</para>
    /// </summary>
    [Fact]
    public void R5_1_TheHeightPairShift_IsExactAlgebra_AndReducesToThePecImage()
    {
        double f = 10e9, lam = EmConstants.C0 / f;
        var stack = LayerStacks.AirOverGround();
        double bigH = stack.TopZ;
        var g = new LayeredSpectralGreens(stack, f);

        foreach (var k in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
        {
            var m = Dcim.Fit(g, k);

            // (a) the two overloads coincide at the reference height.
            for (double rl = 1e-3; rl <= 3.001; rl *= 10)
            {
                double rho = rl * lam;
                Assert.True(Rel(m.Evaluate(rho), m.Evaluate(rho, bigH, bigH)) < 1e-13,
                    $"{k}: Evaluate(ρ) and Evaluate(ρ, H, H) differ by " +
                    $"{Rel(m.Evaluate(rho), m.Evaluate(rho, bigH, bigH)):E3}");
            }

            // (b) εᵣ = 1 over a ground plane: free space minus ONE image at depth z + z′, exactly.
            double worst = 0;
            foreach (double dz in new[] { 0.0, 0.05, 0.4 })
            foreach (double dzp in new[] { 0.0, 0.2 })
            foreach (double rl in new[] { 1e-3, 0.03, 1.0 })
            {
                double z = bigH + dz * lam, zp = bigH + dzp * lam, rho = rl * lam;
                double delta = Math.Abs(z - zp), image = z + zp;
                Complex exact = SommerfeldIntegral.FreeSpace(g.K0, Math.Sqrt(rho * rho + delta * delta))
                              - SommerfeldIntegral.FreeSpace(g.K0, Math.Sqrt(rho * rho + image * image));
                worst = Math.Max(worst, Rel(exact, m.Evaluate(rho, z, zp)));
            }
            _out.WriteLine($"εᵣ = 1 over ground, {k,-16}: worst relative against free space minus one " +
                           $"PEC image at depth z + z′ = {worst:E3}");
            Assert.True(worst < 1e-9,
                $"{k}: the shifted model is {worst:E3} from the elementary image answer, so the shift " +
                $"bookkeeping — not the fit — is wrong.");
        }
    }

    /// <summary>
    /// <b>D5's measurement: the shift needs no refit, and the ERROR is not uniform in Σ.</b>
    ///
    /// <para>Δ is held at zero and Σ swept, so this isolates the height-pair variable from the
    /// separation variable. The last column is the diagnostic that explains the answer: the fitted
    /// Γ's own error over the PROPAGATING region k_ρ ≤ k₀, which is the region <c>e^{−jk_z0Σ}</c>
    /// weights ever more heavily as Σ grows (out there <c>|e^{−jk_z0Σ}| = e^{−k_ρΣ}</c> kills the
    /// evanescent spectrum the sampling path resolves best, and leaves the fit standing on the small
    /// piece it only ever extrapolated into, constrained by the branch-point sum rule alone).</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void R5_2_TheShiftTheorem_MeasuredAcrossHeightPairs()
    {
        double f = 10e9, lam = EmConstants.C0 / f;
        _out.WriteLine("Δ = 0 throughout; Σ = z + z′ − 2H swept. 'scaled' is |ΔG|·4πρ as everywhere " +
                       "else.\nThe last column is the fitted Γ's own worst error over k_ρ ≤ k₀ — the " +
                       "region a growing Σ weights.\n");
        foreach (var (name, stack) in LayerStacks.All())
        {
            var g = new LayeredSpectralGreens(stack, f);
            double h = stack.TopZ;
            foreach (var k in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
            {
                var m = Dcim.Fit(g, k);
                double gammaErr = 0;
                for (int i = 0; i <= 40; i++)
                {
                    Complex kRho = (i / 40.0) * g.K0;
                    Complex kz0 = g.Kz0(kRho);
                    gammaErr = Math.Max(gammaErr,
                        (FittedReflection(m, g, kz0) - g.TopInterfaceReflection(k, kRho)).Magnitude);
                }

                var cells = new List<string>();
                foreach (double sigma in new[] { 0.0, 0.01, 0.1, 0.3 })
                {
                    double z = h + 0.5 * sigma * lam;
                    double worst = 0;
                    for (double rl = 1e-4; rl <= 10.001; rl *= Math.Pow(10, 0.5))
                    {
                        double rho = rl * lam;
                        Complex exact = SommerfeldIntegral.EvaluateLayered(g, k, rho, z, z).Value;
                        worst = Math.Max(worst, (m.Evaluate(rho, z, z) - exact).Magnitude * 4 * Math.PI * rho);
                    }
                    cells.Add($"Σ/λ={sigma:F2}: {worst:E2}");
                }
                _out.WriteLine($"{name,-46} {(k == GreensKernel.ScalarPotential ? "G_q" : "G_A")}  " +
                               $"{string.Join("  ", cells)}   |ΔΓ|(k_ρ≤k₀) {gammaErr:E2}");
            }
        }
    }

    /// <summary>The model's own Γ, reassembled from what it fitted — the diagnostic R5_2 reports.</summary>
    private static Complex FittedReflection(DcimModel m, LayeredSpectralGreens g, Complex kz0)
    {
        Complex kRhoSq = g.TopWavenumberSquared - kz0 * kz0;
        Complex value = m.QuasiStatic;
        foreach (var im in m.Images)
            value += im.Amplitude * Complex.Exp(-Complex.ImaginaryOne * kz0 * im.Depth);
        Complex poles = Complex.Zero;
        foreach (var p in m.SurfaceWaves) poles += p.Residue / (kRhoSq - p.KRho * p.KRho);
        return value + 2.0 * Complex.ImaginaryOne * kz0 * poles;
    }

    /// <summary>
    /// <b>D6 — the interior-height case, stated as an obstruction with its shape MEASURED rather than
    /// guessed at, because L9b deliberately fits nothing it has no oracle for.</b>
    ///
    /// <para>The refusal is real: <see cref="DcimModel.Evaluate(double, double, double)"/>,
    /// <see cref="SommerfeldIntegral.EvaluateLayered"/> and <see cref="LayeredStaticGreens"/> all
    /// refuse a source inside the stack by name. What this test establishes is the SHAPE of what L9c
    /// has to build, so that it is a scoped job rather than a discovery.</para>
    ///
    /// <para><b>The interior same-region kernel is still an exact shift — of FOUR exponential families
    /// rather than one.</b> With source and observer both in region m the transmission-line voltage is
    /// <c>(Z_m/2·denom)·[e^{−jk_zm Δ} + Γ_t e^{−jk_zm(2d−Σ_b)} + Γ_b e^{−jk_zm Σ_b} +
    /// Γ_tΓ_b e^{−jk_zm(2d−Δ)}]</c> with <c>Δ = |z − z′|</c> and <c>Σ_b = z + z′ − 2z_b</c>. The four
    /// coefficients do not depend on the heights at all. This test MEASURES that: it solves for them
    /// from four height pairs and predicts a fifth, which comes out right to ~1e-15 on every stack and
    /// both polarisations. So a fit per height pair is wrong for the interior case too — but it is
    /// four fits in <c>k_zm</c>, not one in k_z0, and the source region's k_m is COMPLEX for a lossy
    /// layer where the top half-space's k₀ is real.</para>
    ///
    /// <para><b>What that costs L9c, precisely:</b> <see cref="SommerfeldIntegral.FreeSpace"/> takes a
    /// <c>double</c> wavenumber and has to widen to a complex one (the Sommerfeld identity itself holds
    /// unchanged for complex k); the oracle needs THREE closed-form extractions rather than two,
    /// because a source sitting exactly on an interior interface makes the down-reflection term
    /// non-decaying as well as the direct one; and the k_ρ = k₀ sinθ / k₀ cosh u substitutions are not
    /// needed at all, because a lossy interior region's k_zm never vanishes on the real axis — but the
    /// top half-space's own branch point at k_ρ = k₀ is still in the integrand as a square-root kink
    /// and needs breakpoints. <b>The cross-region case (source in m, observer in n ≠ m — which is
    /// exactly two metal levels at two different interfaces) mixes k_zm and k_zn and has no single
    /// reference wavenumber to be an exact shift IN</b>; it is a genuinely different question and is
    /// reported as one.</para>
    /// </summary>
    [Fact]
    public void R5_3_TheInteriorHeightCase_IsAFourFamilyShift_AndIsRefusedRatherThanFitted()
    {
        // 1. It is refused by name at every level.
        //
        // L9e/M4 — UPDATED, NOT LOOSENED. These used to assert the refusals named the PHASE ("L9c").
        // L9c arrived and built the interior fit, so what the refusal must name now is the API that
        // does the job — Dcim.FitAtHeights — rather than a schedule that has expired.
        var stack = LayerStacks.Pcb3Layer;
        var g = new LayeredSpectralGreens(stack, 10e9);
        var m = Dcim.Fit(g, GreensKernel.ScalarPotential);
        double inside = 0.5 * (stack.InterfaceZ[0] + stack.InterfaceZ[1]);

        foreach (var (what, expect, act) in new (string, string, Action)[]
                 {
                     ("DcimModel.Evaluate", "Dcim.FitAtHeights",
                      () => m.Evaluate(1e-3, inside, inside)),
                     ("SommerfeldIntegral.EvaluateLayered", "EvaluateInterior",
                      () => SommerfeldIntegral.EvaluateLayered(
                                g, GreensKernel.ScalarPotential, 1e-3, inside, inside)),
                     ("LayeredStaticGreens", "inside the stack",
                      () => LayeredStaticGreens.ScalarPotential(stack, 1e-3, inside, inside)),
                 })
        {
            var ex = Assert.Throws<ArgumentException>(act);
            Assert.Contains(expect, ex.Message);
            _out.WriteLine($"{what} refuses an interior source by name.");
        }

        // 2. And the shape of what L9c must build is measured, not asserted: four families, exactly.
        double worst = 0; string at = "";
        foreach (var (name, s) in LayerStacks.All())
        {
            if (s.LayerCount == 0) continue;
            var gg = new LayeredSpectralGreens(s, 10e9);
            double zb = s.RegionBottomZ(1), zt = s.RegionTopZ(1), d = zt - zb;
            foreach (var pol in new[] { SurfaceWavePolarization.Tm, SurfaceWavePolarization.Te })
            foreach (double u in new[] { 0.3, 2.0, 15.0 })
            {
                Complex w = (u * gg.K0) * (u * gg.K0);
                Complex kzm = gg.KzOfRegion(1, w);
                var pairs = new[] { (0.1, 0.2), (0.35, 0.55), (0.6, 0.15), (0.8, 0.9), (0.25, 0.7) };

                var rows = new Complex[4, 4];
                var rhs  = new Complex[4];
                for (int i = 0; i < 4; i++)
                {
                    double z = zb + pairs[i].Item1 * d, zp = zb + pairs[i].Item2 * d;
                    var e = FourFamilyBasis(kzm, z, zp, zb, d);
                    for (int j = 0; j < 4; j++) rows[i, j] = e[j];
                    rhs[i] = gg.Voltage(pol, w, z, zp);
                }
                var c = Solve4(rows, rhs);
                Assert.NotNull(c);

                double z5 = zb + pairs[4].Item1 * d, zp5 = zb + pairs[4].Item2 * d;
                var b5 = FourFamilyBasis(kzm, z5, zp5, zb, d);
                Complex predicted = c[0] * b5[0] + c[1] * b5[1] + c[2] * b5[2] + c[3] * b5[3];
                double rel = Rel(gg.Voltage(pol, w, z5, zp5), predicted);
                if (rel > worst) { worst = rel; at = $"{name} {pol} k_ρ/k₀ = {u}"; }
            }
        }
        _out.WriteLine($"\nInterior same-region kernel: four height-independent coefficients solved from " +
                       $"four height pairs predict a FIFTH to {worst:E3} (worst over every stack, both " +
                       $"polarisations, k_ρ/k₀ ∈ {{0.3, 2, 15}}) — at {at}.");
        Assert.True(worst < 1e-12,
            $"the four-family form does not reproduce a fifth height pair ({worst:E3} at {at}), so the " +
            $"claim L9c is being handed is wrong.");
    }

    private static Complex[] FourFamilyBasis(Complex kzm, double z, double zp, double zBottom, double d)
    {
        Complex j = Complex.ImaginaryOne;
        double delta = Math.Abs(z - zp), sigma = z + zp - 2 * zBottom;
        return
        [
            Complex.Exp(-j * kzm * delta),
            Complex.Exp(-j * kzm * (2 * d - sigma)),
            Complex.Exp(-j * kzm * sigma),
            Complex.Exp(-j * kzm * (2 * d - delta)),
        ];
    }

    private static Complex[]? Solve4(Complex[,] a, Complex[] b)
    {
        var t = (Complex[,])a.Clone();
        var y = (Complex[])b.Clone();
        for (int k = 0; k < 4; k++)
        {
            int p = k;
            for (int i = k + 1; i < 4; i++) if (t[i, k].Magnitude > t[p, k].Magnitude) p = i;
            if (t[p, k].Magnitude == 0) return null;
            if (p != k)
            {
                for (int j = 0; j < 4; j++) (t[k, j], t[p, j]) = (t[p, j], t[k, j]);
                (y[k], y[p]) = (y[p], y[k]);
            }
            for (int i = k + 1; i < 4; i++)
            {
                Complex fac = t[i, k] / t[k, k];
                for (int j = k; j < 4; j++) t[i, j] -= fac * t[k, j];
                y[i] -= fac * y[k];
            }
        }
        var x = new Complex[4];
        for (int i = 3; i >= 0; i--)
        {
            Complex s = y[i];
            for (int j = i + 1; j < 4; j++) s -= t[i, j] * x[j];
            x[i] = s / t[i, i];
        }
        return x;
    }

    // =============================================================================================
    // M3 — the second branch point (D3), and the BranchPointOrders table re-run.
    // =============================================================================================

    /// <summary>
    /// <b>D3 — WHERE the second branch point sits, for every stack, in the k_z0 plane the fit lives
    /// in.</b> Free: no fit and no oracle, just arithmetic on the wavenumbers the cascade uses.
    ///
    /// <para>Γ depends on the bottom half-space through <c>k_zb = √(k_b² − k_top² + k_z0²)</c>, whose
    /// branch points are at <c>k_z0 = ±√(k_top² − k_b²)</c>. With εᵣ ≥ 1 enforced and air on top,
    /// k_b ≥ k_top always, so that is <b>±j·k₀√(εᵣµᵣ − 1)</b> — on the imaginary axis, and the minus
    /// root is in the same half-plane as the sampling path. A closed bottom has no k_zb at all
    /// (Γ_bottom is exactly ±1), so there is no second branch point to find.</para>
    /// </summary>
    [Fact]
    public void R6_1_TheSecondBranchPoint_IsLocatedRelativeToBothSamplingPaths()
    {
        var s = DcimSettings.Default;
        _out.WriteLine("distances are in units of k₀; the paths are k_z0(t) = k₀[(1 − t/T) − jt].\n");
        foreach (var (name, stack) in LayerStacks.All())
        {
            var g = new LayeredSpectralGreens(stack, 10e9);
            if (!stack.Bottom.IsOpen)
            {
                _out.WriteLine($"{name,-46} closed below ({stack.Bottom}) — Γ_bottom is exactly ±1 and " +
                               $"carries no k_zb, so there is NO second branch point.");
                continue;
            }

            Complex bp = Complex.Sqrt(g.TopWavenumberSquared - g.RegionWavenumberSquared(0)) / g.K0;
            if (bp.Imaginary > 0) bp = -bp;                     // the root in the sampled half-plane

            double far  = MinDistance(bp, s.FarPathExtent, s.FarSamples);
            double near = MinDistance(bp, s.PathExtent, s.Samples);
            double taylor = new[] { 0.0, 1e-3, -1e-3, 2e-3, -2e-3 }.Min(x => (bp - x).Magnitude);

            _out.WriteLine($"{name,-46} open below ({stack.Bottom})");
            _out.WriteLine($"{"",-46}   k_z0/k₀ = {bp.Real:F4} {(bp.Imaginary < 0 ? "-" : "+")} " +
                           $"{Math.Abs(bp.Imaginary):F4}j");
            _out.WriteLine($"{"",-46}   nearest approach: far path {far:F4}, near path {near:F4}, " +
                           $"branch-point Taylor points {taylor:F4}");
            _out.WriteLine($"{"",-46}   Dcim.CanFit: {(Dcim.CanFit(stack).Ok ? "yes" : "NO")}");

            if (bp.Magnitude < 1e-9)
            {
                Assert.True(Dcim.CanFit(stack).Ok,
                    $"{name}: the two branch points COINCIDE, so there is only one cut and the fit is " +
                    $"not structurally obstructed — refusing it would be wrong.");
            }
            else
            {
                Assert.False(Dcim.CanFit(stack).Ok,
                    $"{name}: a genuinely separate second branch point at {bp} is accepted by CanFit.");
                Assert.True(far < 0.5,
                    $"{name}: the far sampling path stays {far:F3}·k₀ away from the second branch " +
                    $"point, which is far enough that the fit failure would need another explanation.");
            }
        }

        static double MinDistance(Complex bp, double t0, int n)
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i < n; i++)
            {
                double t = t0 * i / (n - 1.0);
                best = Math.Min(best, (bp - new Complex(1.0 - t / t0, -t)).Magnitude);
            }
            return best;
        }
    }

    /// <summary>
    /// <b>D2 — L8a chose <c>BranchPointOrders = 1</c> by measurement on two ONE-LAYER substrates, and
    /// that choice is not automatically transferable.</b> Here is the same table on the multilayer
    /// stacks, in both measures, so the default is re-earned rather than inherited.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void R6_2_TheBranchPointOrdersTable_ReRunOnTheMultilayerStacks()
    {
        double f = 10e9, lam = EmConstants.C0 / f;
        _out.WriteLine("worst error over ρ/λ ∈ [1e-4, 10] at 10 GHz — 'rel' is |ΔG|/|G|, " +
                       "'sc' is |ΔG|·4πρ.\n");
        foreach (var (name, stack) in LayerStacks.All())
        {
            var g = new LayeredSpectralGreens(stack, f);
            double h = stack.TopZ;
            foreach (var k in new[] { GreensKernel.ScalarPotential, GreensKernel.VectorPotential })
            {
                var cells = new List<string>();
                foreach (int orders in new[] { 0, 1, 2, 3 })
                {
                    var m = Dcim.Fit(g, k, DcimSettings.Default with { BranchPointOrders = orders });
                    double wr = 0, ws = 0;
                    for (double rl = 1e-4; rl <= 10.001; rl *= Math.Pow(10, 0.5))
                    {
                        double rho = rl * lam;
                        Complex exact = SommerfeldIntegral.EvaluateLayered(g, k, rho, h, h).Value;
                        double abs = (m.Evaluate(rho) - exact).Magnitude;
                        wr = Math.Max(wr, abs / exact.Magnitude);
                        ws = Math.Max(ws, abs * 4 * Math.PI * rho);
                    }
                    cells.Add($"{orders}: rel {wr:E1} sc {ws:E1}");
                }
                _out.WriteLine($"{name,-46} {(k == GreensKernel.ScalarPotential ? "G_q" : "G_A")}  " +
                               string.Join(" | ", cells));
            }
        }
    }

    // =============================================================================================
    // M5 — the cost, after D7 (§8.5).
    // =============================================================================================

    /// <summary>
    /// <b>§8.5 — the number L9c and L9d are scheduled against, and it is not L9a's projection.</b>
    /// Reported as a FIT cost per frequency per kernel, plus the per-sample cascade cost in L9a's own
    /// units so the two are comparable and the D7 cache's effect can be read off directly.
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void R7_1_TheFitCost_PerFrequency_AfterTheD7CascadeCache()
    {
        const int reps = 5;
        _out.WriteLine("fit cost per frequency per kernel, and the per-sample cascade cost in L9a's " +
                       "own units (40 000 samples of KernelAtHeights at 10 GHz).\n");
        foreach (var (name, stack) in LayerStacks.All())
        {
            // Warm the JIT, then time fresh instances so no cache crosses a measurement.
            Dcim.Fit(new LayeredSpectralGreens(stack, 10e9), GreensKernel.ScalarPotential);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < reps; i++)
                Dcim.Fit(new LayeredSpectralGreens(stack, 10e9), GreensKernel.ScalarPotential);
            double q = sw.Elapsed.TotalMilliseconds / reps;

            sw.Restart();
            for (int i = 0; i < reps; i++)
                Dcim.Fit(new LayeredSpectralGreens(stack, 10e9), GreensKernel.VectorPotential);
            double a = sw.Elapsed.TotalMilliseconds / reps;

            var g = new LayeredSpectralGreens(stack, 10e9);
            double hh = stack.TopZ;
            const int n = 40000;
            Complex acc = Complex.Zero;
            sw.Restart();
            for (int i = 0; i < n; i++)
                acc += g.KernelAtHeights(GreensKernel.ScalarPotential,
                                         g.K0 * (0.001 + 3.0 * i / n), hh, hh);
            double perSample = sw.Elapsed.TotalSeconds / n * 1e6;

            // acc is summed and printed so the loop cannot be optimised into nothing.
            _out.WriteLine($"{name,-46} fit G_q {q,6:F1} ms   fit G_A {a,6:F1} ms   " +
                           $"cascade {perSample:F3} µs/sample   (Σ = {acc.Real:E1})");
        }

        Dcim.Fit(new SpectralGreens(GroundedSlab.Fr4Starter, 10e9), GreensKernel.ScalarPotential);
        var s2 = Stopwatch.StartNew();
        for (int i = 0; i < reps; i++)
            Dcim.Fit(new SpectralGreens(GroundedSlab.Fr4Starter, 10e9), GreensKernel.ScalarPotential);
        _out.WriteLine($"\nshipped ONE-LAYER closed-form fit, G_q, for comparison: " +
                       $"{s2.Elapsed.TotalMilliseconds / reps:F1} ms");
    }
}
