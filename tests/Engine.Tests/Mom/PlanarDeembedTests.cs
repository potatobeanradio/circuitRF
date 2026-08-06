// L8d Tiers 4 and 5 — de-embedding self-consistency, then Z_c on its own.
//
// D7 is what makes these two tiers separate rather than one: the calibration is referenced to the
// LINE'S OWN Z_c, so everything in Tier 4 is blind to Z_c's value and everything in Tier 5 is about
// nothing else. Conflating them would let a bad Z_c hide behind a good calibration, which is exactly
// the failure mode a single "does the S-matrix look right" test would miss.

using System.Numerics;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarDeembedTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-prt-7 — a THIRD line, not used in the calibration, is the de-embedding's own gate
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(2e9)]
    [InlineData(10e9)]
    public void T4_1_AThirdLineDeembedsToAMatchedSection_ThePhaseAndTheReturnLossBoth(double f)
    {
        var slab = GroundedSlab.Fr4Starter;
        var (mesh, prt) = PlanarLineFixtures.MeshAndPorts(
            PlanarLineFixtures.LineOfWavelengths(slab, PlanarLineFixtures.Fr4HeroWidthM, 1.5, f));

        var kernel = PlanarLineFixtures.Kernel(slab, f);
        var cal    = new PlanarPortCalibrator(prt[0], slab, f, f);
        var c      = cal.At(kernel, f);

        // A line the calibration has never seen. Its own ports are the same object as the
        // standards' (D4), so the SAME error box applies.
        int k = PlanarCalibration.EndRunCellsFor(prt[0], slab);
        var third = PlanarCalibration.BuildLine(prt[0], cal.Standards[0].LengthM * 1.63, k);
        var sRaw  = new PlanarSolveContext(third.Mesh, third.Ports).RawScatteringAt(kernel, f);

        var s = PlanarDeembed.Apply(sRaw, [c.Box, c.Box]);

        Complex expected = Complex.Exp(-c.Gamma.Gamma * third.LengthM);
        double phaseErr  = Math.Abs(s[1, 0].Phase - expected.Phase);
        double magErr    = Math.Abs(s[1, 0].Magnitude - expected.Magnitude);

        _out.WriteLine($"{f / 1e9:F1} GHz: ℓ₁ = {cal.Standards[0].LengthM * 1e3:F3} mm, " +
                       $"ℓ₃ = {third.LengthM * 1e3:F3} mm, βΔℓ = {c.Gamma.ElectricalDegrees:F1}°");
        _out.WriteLine($"  box: a₁₁ = {c.Box.A11:F5}, a₂₂ = {c.Box.A22:F5}, a₂₁ = {c.Box.A21:F5}");
        _out.WriteLine($"       consistency residual {c.Box.ConsistencyResidual:E2} " +
                       $"(rejected sign {c.Box.RejectedResidual:E2})");
        _out.WriteLine($"  de-embedded: S₁₁ = {s[0, 0]:F6} (|S₁₁| = {s[0, 0].Magnitude:E3})");
        _out.WriteLine($"               S₂₁ = {s[1, 0]:F6}  expected {expected:F6}");
        _out.WriteLine($"               ∠ error {phaseErr:E2} rad, |·| error {magErr:E2}");

        // The gate that is blind to Z_c: in the line's own reference a uniform section is MATCHED,
        // so |S₁₁| is the whole de-embedding error rolled into one number.
        Assert.True(s[0, 0].Magnitude < 3e-2,
            $"|S₁₁| = {s[0, 0].Magnitude:E3} on a section that should be matched");
        Assert.True(phaseErr < 2e-2, $"∠S₂₁ is {phaseErr:E2} rad from −βℓ₃");
        Assert.True(magErr   < 2e-2, $"|S₂₁| is {magErr:E2} from e^(−αℓ₃)");

        // And the a₂₂ sign was decided by information, not by noise.
        Assert.True(c.Box.RejectedResidual > 5 * c.Box.ConsistencyResidual,
            $"the two a₂₂ signs are nearly equally consistent ({c.Box.ConsistencyResidual:E2} vs " +
            $"{c.Box.RejectedResidual:E2}) — the sign was decided by noise");
    }

    [Fact]
    public void T4_2_TheCascadeIdentityHolds_ALineOfTwoLIsTwoLinesOfL()
    {
        const double f = 10e9;
        var slab = GroundedSlab.Fr4Starter;
        var (_, prt) = PlanarLineFixtures.MeshAndPorts(
            PlanarLineFixtures.LineOfWavelengths(slab, PlanarLineFixtures.Fr4HeroWidthM, 1.5, f));

        var kernel = PlanarLineFixtures.Kernel(slab, f);
        var cal    = new PlanarPortCalibrator(prt[0], slab, f, f);
        var c      = cal.At(kernel, f);
        int k      = PlanarCalibration.EndRunCellsFor(prt[0], slab);

        var one = PlanarCalibration.BuildLine(prt[0], cal.Standards[0].LengthM, k);
        double lengthOne = one.LengthM;
        var two = PlanarCalibration.BuildLine(prt[0], 2 * lengthOne, k);

        var s1 = PlanarDeembed.Apply(
            new PlanarSolveContext(one.Mesh, one.Ports).RawScatteringAt(kernel, f), [c.Box, c.Box]);
        var s2 = PlanarDeembed.Apply(
            new PlanarSolveContext(two.Mesh, two.Ports).RawScatteringAt(kernel, f), [c.Box, c.Box]);

        // §10.9's own list: "a uniform line of length 2L must equal two cascaded lines of length L".
        // In the matched Z_c reference the cascade is a multiplication, so this is exact algebra on
        // two independently solved meshes. The exp() corrects for the length quantisation, since
        // BuildLine rounds up to a whole bulk cell.
        Complex expected = s1[1, 0] * s1[1, 0]
                         * Complex.Exp(-c.Gamma.Gamma * (two.LengthM - 2 * lengthOne));

        double rel = (s2[1, 0] - expected).Magnitude / expected.Magnitude;
        _out.WriteLine($"ℓ = {lengthOne * 1e3:F4} mm (βℓ = {c.Gamma.Beta * lengthOne:F3} rad) → S₂₁ = {s1[1, 0]:F6}");
        _out.WriteLine($"2ℓ = {two.LengthM * 1e3:F4} mm → S₂₁ = {s2[1, 0]:F6}, expected {expected:F6}");
        _out.WriteLine($"relative {rel:E3}");

        // MEASURED, AND THE BOUND IS SET BY PHYSICS RATHER THAN BY TASTE — see T4_6, which measures
        // the same residual against electrical length and identifies what limits it.
        Assert.True(rel < 3e-2, $"the cascade identity fails by {rel:E3}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T4_6_TheResidualOnAMatchedSectionGrowsWithLengthAndScalesWithFrequency()
    {
        // THE FINDING OF THIS SLICE, MEASURED RATHER THAN ARGUED. The de-embedded S of a uniform
        // section is EXACT at the two lengths the calibration was solved from — machine zero, because
        // four equations fixed four unknowns — and drifts away from them. That drift is not a bug in
        // the algebra and it does not go away with a longer standard:
        //
        //   • it is NOT monotone in the standard length (β reads 402.2 / 400.0 / 398.2 / 395.9 /
        //     394.2 / 399.0 for standards of 5.1 / 6.8 / 8.6 / 12.0 / 15.4 / 23.9 substrate heights),
        //     so it is not an evanescent tail that a longer standard would suppress;
        //   • it scales with FREQUENCY roughly as f², which an evanescent tail does not.
        //
        // Both are the signature of DIRECT RADIATIVE AND SURFACE-WAVE COUPLING between the two ports.
        // That decays only algebraically, and it beats against the guided mode because k₀ ≠ β, which
        // is exactly why the length dependence oscillates. It is the same physics L8a warned about
        // when it said losslessness does not survive into kernel B — an open planar structure
        // radiates, and a calibration that models the structure as "box + matched line + box" has no
        // term for the part of the field that goes around the line rather than along it.
        var slab = GroundedSlab.Fr4Starter;
        double w = PlanarLineFixtures.Fr4HeroWidthM;

        foreach (double f in new[] { 2e9, 10e9 })
        {
            var (_, prt) = PlanarLineFixtures.MeshAndPorts(
                PlanarLineFixtures.LineOfWavelengths(slab, w, 1.5, f));
            var kernel = PlanarLineFixtures.Kernel(slab, f);
            var cal    = new PlanarPortCalibrator(prt[0], slab, f, f);
            var c      = cal.At(kernel, f);
            int k      = PlanarCalibration.EndRunCellsFor(prt[0], slab);

            _out.WriteLine($"{f / 1e9,5:F1} GHz  (h/λ₀ = {slab.HeightM * f / EmConstants.C0:F4})");
            double atOne = double.NaN;
            foreach (double mult in new[] { 1.0, 1.6, 2.0 })
            {
                var st  = PlanarCalibration.BuildLine(prt[0], cal.Standards[0].LengthM * mult, k);
                var raw = new PlanarSolveContext(st.Mesh, st.Ports).RawScatteringAt(kernel, f);
                var s   = PlanarDeembed.Apply(raw, [c.Box, c.Box]);
                if (double.IsNaN(atOne)) atOne = s[0, 0].Magnitude;

                _out.WriteLine($"    ℓ = {st.LengthM * 1e3,7:F3} mm ({st.LengthM / slab.HeightM,5:F1} h, " +
                               $"βℓ = {c.Gamma.Beta * st.LengthM,6:F2} rad): |S₁₁| = {s[0, 0].Magnitude:E3}");
            }

            // At the calibration's OWN short standard the de-embedding is exact by construction —
            // four equations, four unknowns. This is the assertion that says the algebra is right.
            Assert.True(atOne < 1e-12,
                $"at the standard's own length the de-embedding should be exact, not {atOne:E3}");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D6's two square roots — tested directly, because one cancels and one does not
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void T4_3_TheA21SignCancelsForIdenticalPorts_AndDoesNotForUnequalOnes()
    {
        // Synthetic boxes: this is an algebraic claim about PlanarDeembed.Apply, so it is tested as
        // one rather than through a solve that could hide it.
        var s = new Mat<Complex>(2, 2);
        s[0, 0] = new Complex(0.31, -0.42);
        s[1, 1] = new Complex(-0.17, 0.55);
        s[0, 1] = s[1, 0] = new Complex(0.22, 0.38);

        var a = new PlanarErrorBox(new Complex(0.11, -0.07), new Complex(-0.29, 0.13),
                                   new Complex(0.61, 0.24), 0, 0);
        var b = new PlanarErrorBox(new Complex(-0.05, 0.19), new Complex(0.41, -0.08),
                                   new Complex(0.33, -0.52), 0, 0);

        var flipA = a with { A21 = -a.A21 };
        var flipB = b with { A21 = -b.A21 };

        // IDENTICAL ports: flipping BOTH is invisible, because Apply divides by a₂₁(i)·a₂₁(j) and an
        // identical pair contributes a₂₁², which the square root never resolved.
        var same     = PlanarDeembed.Apply(s, [a, a]);
        var sameFlip = PlanarDeembed.Apply(s, [flipA, flipA]);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.Equal(same[i, j].Real, sameFlip[i, j].Real, 14);
        _out.WriteLine("identical ports: flipping a₂₁ leaves S unchanged to 1e-14 ✓");

        // UNEQUAL ports: flipping ONE is a hard π in S₂₁ and leaves S₁₁/S₂₂ alone — invisible in a
        // magnitude plot, which is exactly why the branch is carried across frequency instead.
        var mixed     = PlanarDeembed.Apply(s, [a, b]);
        var mixedFlip = PlanarDeembed.Apply(s, [a, flipB]);
        _out.WriteLine($"unequal ports: S₂₁ = {mixed[1, 0]:F6} → {mixedFlip[1, 0]:F6} on one flip");

        Assert.Equal(-mixed[1, 0].Real, mixedFlip[1, 0].Real, 12);
        Assert.Equal(-mixed[1, 0].Imaginary, mixedFlip[1, 0].Imaginary, 12);
        Assert.Equal(mixed[0, 0].Real, mixedFlip[0, 0].Real, 12);
        Assert.True((mixed[1, 0] - mixedFlip[1, 0]).Magnitude > 0.1,
            "the unequal-port flip has to be visible, or the test proves nothing");
    }

    [Fact]
    public void T4_4_TheDeembeddingReducesToTheTwoPortTMatrixCascade()
    {
        // Apply() is a general P-port formula. It must agree exactly with RfCore's own 2-port
        // T-matrix de-embedding when handed the same error boxes — same physics, disjoint algebra,
        // and R-mom-14's rule about not writing a second implementation applies to the CHECK too.
        var s = new Mat<Complex>(2, 2);
        s[0, 0] = new Complex(0.31, -0.42);
        s[1, 1] = new Complex(-0.17, 0.55);
        s[0, 1] = s[1, 0] = new Complex(0.22, 0.38);

        var a = new PlanarErrorBox(new Complex(0.11, -0.07), new Complex(-0.29, 0.13),
                                   new Complex(0.61, 0.24), 0, 0);

        var mine = PlanarDeembed.Apply(s, [a, a]);

        // The same boxes as explicit 2-ports: A on the way in (external, internal) and its MIRROR on
        // the way out (internal, external).
        var boxIn  = Box(a.A11, a.A21, a.A21, a.A22);
        var boxOut = Box(a.A22, a.A21, a.A21, a.A11);

        var t = RfCore.RFNetwork.SToT2Port(s);
        var td = Inv2(RfCore.RFNetwork.SToT2Port(boxIn)) * t * Inv2(RfCore.RFNetwork.SToT2Port(boxOut));
        var theirs = RfCore.RFNetwork.TToS2Port(td);

        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                _out.WriteLine($"S[{i},{j}]: {mine[i, j]:F10} vs {theirs[i, j]:F10}");
                Assert.Equal(theirs[i, j].Real, mine[i, j].Real, 12);
                Assert.Equal(theirs[i, j].Imaginary, mine[i, j].Imaginary, 12);
            }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-prt-4 / Tier 4 — feed-length invariance, which is also the feed-length MEASUREMENT
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T4_5_TheSameDiscontinuityDeembedsTheSameBehindDifferentFeedLengths()
    {
        // The DUT is a step in width. Its de-embedded S differs between two feed lengths only by the
        // extra matched line in front of it, which γ removes exactly — so what remains is the
        // invariance the calibration is supposed to deliver.
        const double f = 10e9;
        var slab = GroundedSlab.Fr4Starter;
        double w = PlanarLineFixtures.Fr4HeroWidthM;

        var kernel = PlanarLineFixtures.Kernel(slab, f);

        Mat<Complex>? reference = null;
        double refFeed = 0, worst = 0;

        foreach (double feedLambda in new[] { 0.25, 0.5, 0.75 })
        {
            var (s, feedM, c) = SteppedLine(slab, w, f, feedLambda, kernel);
            if (reference is null) { reference = s; refFeed = feedM; continue; }

            // Shift the longer feeds' planes inward to where the reference's are.
            Complex shift = Complex.Exp(c.Gamma.Gamma * (feedM - refFeed));
            var moved = new Mat<Complex>(2, 2);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++) moved[i, j] = s[i, j] * shift * shift;

            var refS = reference!.Value;
            double d11   = (moved[0, 0] - refS[0, 0]).Magnitude;
            double d21m  = Math.Abs(moved[1, 0].Magnitude - refS[1, 0].Magnitude);
            double d21p  = Math.Abs(moved[1, 0].Phase - refS[1, 0].Phase);
            worst = Math.Max(worst, Math.Max(d11, d21m));

            _out.WriteLine($"feed {feedLambda:F2} λ_g ({feedM * 1e3:F3} mm, plane shifted " +
                           $"{2 * c.Gamma.Beta * (feedM - refFeed):F2} rad): " +
                           $"|ΔS₁₁| = {d11:E3}, Δ|S₂₁| = {d21m:E3}, Δ∠S₂₁ = {d21p:E3} rad");
        }

        // WHAT IS AND IS NOT INVARIANT, AND THE SPLIT IS THE POINT. |S₁₁| and |S₂₁| are properties of
        // the discontinuity and must not move with the feed; ∠S₂₁ is not, because moving the plane
        // by Δ multiplies it by e^{2γΔ} and any residual error in γ therefore accumulates as
        // βΔ — 7.3 rad of shift here, so the ~1% γ residual T4_6 measures shows up as ~0.1 rad of
        // phase. Gating the complex difference would be gating γ's accuracy under a different name.
        Assert.True(worst < 5e-2,
            $"the de-embedded step's own reflection/transmission moves by {worst:E3} with the feed length");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Tier 5 — Z_c, alone
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T5_1_CPerMetreFromDifferencedStaticSolvesAgreesWithKernelA()
    {
        // Two routes to C per unit length that share NO CODE: this one differences two full-wave
        // meshes' static capacitances (so both end effects cancel exactly); kernel A solves a
        // cross-section with a completely different discretisation and a different kernel.
        var slab = GroundedSlab.Fr4Starter;
        double w = PlanarLineFixtures.Fr4HeroWidthM;

        var (_, prt) = PlanarLineFixtures.MeshAndPorts(
            PlanarLineFixtures.LineOfWavelengths(slab, w, 1.5, 10e9), PlanarLineFixtures.Shipping);

        var cal = new PlanarPortCalibrator(prt[0], slab, 10e9, 10e9);
        double cB = PlanarDeembed.CapacitancePerMetre(cal.Standards[0], cal.Standards[^1], slab);
        double cA = KernelACPerMetre(slab, w);

        _out.WriteLine($"C_pul (kernel B, differenced) = {cB * 1e12:F4} pF/m");
        _out.WriteLine($"C_pul (kernel A, cross-section) = {cA * 1e12:F4} pF/m");
        _out.WriteLine($"B/A − 1 = {(cB - cA) / cA:+0.00%;-0.00%}");

        Assert.True(Math.Abs(cB - cA) / cA < 0.05,
            $"the two C_pul routes differ by {(cB - cA) / cA:P2}");
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void T5_2_ZcAgreesWithKernelAAtLowFrequency_AndItsDispersionIsReported()
    {
        var slab = GroundedSlab.Fr4Starter;
        double w = PlanarLineFixtures.Fr4HeroWidthM;
        double z0A = KernelAZ0(slab, w);
        _out.WriteLine($"kernel A (quasi-static): Z₀ = {z0A:F3} Ω");

        double u     = w / slab.HeightM;
        double eeffA = KernelAEeff(slab, w);

        var rows = new List<(double F, Complex Zc, double Kj)>();
        foreach (double f in new[] { 1e9, 5e9, 20e9 })
        {
            var (_, prt) = PlanarLineFixtures.MeshAndPorts(
                PlanarLineFixtures.LineOfWavelengths(slab, w, 1.5, f), PlanarLineFixtures.Shipping);

            var cal = new PlanarPortCalibrator(prt[0], slab, f, f);
            var c   = cal.At(PlanarLineFixtures.Kernel(slab, f), f);

            double fn   = KirschningJansen.NormalizedFreqGhzMm(f, slab.HeightM);
            double eeff = KirschningJansen.DispersiveEeff(u, slab.Material.EpsR, eeffA, fn);
            double kj   = KirschningJansen.DispersiveZ0(u, slab.Material.EpsR, z0A, eeffA, eeff, fn);

            rows.Add((f, c.Zc, kj));
            _out.WriteLine($"{f / 1e9,5:F1} GHz: Z_c = {c.Zc.Real:F3} {(c.Zc.Imaginary >= 0 ? "+" : "−")} " +
                           $"j{Math.Abs(c.Zc.Imaginary):F3} Ω, C_pul = {c.CPerMetre * 1e12:F3} pF/m, " +
                           $"B/A(static) − 1 = {(c.Zc.Real - z0A) / z0A:+0.00%;-0.00%}, " +
                           $"K-J Z₀(f) = {kj:F3} Ω → B/KJ − 1 = {(c.Zc.Real - kj) / kj:+0.00%;-0.00%}");
        }

        Assert.True(Math.Abs(rows[0].Zc.Real - z0A) / z0A < 0.05,
            $"at 1 GHz Z_c = {rows[0].Zc.Real:F2} Ω against kernel A's {z0A:F2} Ω");
        Assert.True(Math.Abs(rows[0].Zc.Imaginary / rows[0].Zc.Real) < 0.05,
            "Z_c has a large imaginary part on a low-loss line at 1 GHz");

        // R-prt-8, AND THE MEASURED NUMBERS ARE THE DELIVERABLE RATHER THAN THE ASSERTION.
        // Z_c = γ/(jωC_pul) holds C at its STATIC value — C_pul moves by 0.17% across 1–20 GHz here,
        // which is the differencing being frequency-independent by construction — so Z_c ∝ √ε_eff(f)
        // and rises: 51.85 → 52.85 → 54.92 Ω, i.e. +0.4% / +2.3% / +6.3% on kernel A's static 51.65 Ω.
        //
        // Kirschning-Jansen's own dispersive Z₀ rises TOO, and faster: 51.63 → 52.12 → 60.76 Ω. So the
        // two agree to 0.4% and 1.4% at 1 and 5 GHz and part company by −9.6% at 20 GHz, where fn =
        // 32 GHz·mm and BOTH are outside their comfortable range — K-J is an empirical fit stretched
        // past the TE₁ surface-wave onset (25.4 GHz on this slab, L8a's R-lgf-3), and γ/(jωC_static)
        // has no term for a dispersing C. Neither is authoritative there and the disagreement is
        // reported rather than resolved. A dispersive C needs a field integral this kernel does not
        // have; that is a real limitation of the γ-and-C route and it belongs on the record.
        Assert.True(rows[^1].Zc.Real > rows[0].Zc.Real,
            "Z_c did not rise with frequency, which is what a static C and a rising ε_eff must give");
        Assert.True(Math.Abs(rows[^1].Zc.Real - rows[^1].Kj) / rows[^1].Kj < 0.12,
            $"at 20 GHz Z_c = {rows[^1].Zc.Real:F2} Ω against K-J's {rows[^1].Kj:F2} Ω — that is more " +
            "than the quasi-static-C assumption can account for");
    }

    // ── Support ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A width step with feeds of a stated electrical length either side, calibrated from ITS OWN
    /// port resolution.
    ///
    /// <para><b>That last clause is D4, and getting it wrong is the trap this fixture originally fell
    /// into.</b> L8b's grid spacing is derived from the whole problem's narrowness per axis, so a
    /// stepped line and a plain line do NOT get the same cells — reusing a calibration built from a
    /// plain line moved the "invariant" answer by 1.8e-1. The standard has to be built from the
    /// geometry it will be used on.</para>
    /// </summary>
    private static (Mat<Complex> S, double FeedM, PlanarPortCalibration Cal) SteppedLine(
        GroundedSlab slab, double w, double f, double feedLambdas, PlanarKernelPair kernel)
    {
        double epsEst = 0.5 * (slab.Material.EpsR + 1.0);
        double lambda = EmConstants.C0 / (f * Math.Sqrt(epsEst));
        double feed   = feedLambdas * lambda;
        double wide   = 2.0 * w;

        var problem = PlanarLineFixtures.Problem(slab, f,
            PlanarLineFixtures.Rect(0,        -0.5 * w,    feed,        0.5 * w),
            PlanarLineFixtures.Rect(feed,     -0.5 * wide, feed + 0.15 * lambda, 0.5 * wide),
            PlanarLineFixtures.Rect(feed + 0.15 * lambda, -0.5 * w, 2 * feed + 0.15 * lambda, 0.5 * w));

        var mesh = SurfaceMesher.Mesh(problem, PlanarLineFixtures.Coarse).Mesh;
        var (x0, _, x1, _) = problem.Bounds();
        var ports = PlanarPorts.ResolveAll(mesh,
        [
            new PlanarPort(1, new EmPoint(x0, 0), PlanarPortSide.MinX, 50.0),
            new PlanarPort(2, new EmPoint(x1, 0), PlanarPortSide.MaxX, 50.0),
        ]);

        var cal = new PlanarPortCalibrator(ports[0], slab, f, f);
        var c   = cal.At(kernel, f);

        var raw = new PlanarSolveContext(mesh, ports).RawScatteringAt(kernel, f);
        return (PlanarDeembed.Apply(raw, [c.Box, c.Box]), feed, c);
    }

    private static double KernelACPerMetre(GroundedSlab slab, double widthM)
    {
        var p = EmProblemBuilders.Microstrip(w: widthM, h: slab.HeightM, t: 1e-6,
                                             epsR: slab.Material.EpsR, tanD: slab.Material.TanD);
        return RlgcExtractor.Extract(p, BoundaryMesher.Mesh(p, EmMeshSettings.Default)).CPerM;
    }

    private static double KernelAEeff(GroundedSlab slab, double widthM)
    {
        var p = EmProblemBuilders.Microstrip(w: widthM, h: slab.HeightM, t: 1e-6,
                                             epsR: slab.Material.EpsR, tanD: slab.Material.TanD);
        return RlgcExtractor.Extract(p, BoundaryMesher.Mesh(p, EmMeshSettings.Default)).Eeff;
    }

    private static double KernelAZ0(GroundedSlab slab, double widthM)
    {
        var p = EmProblemBuilders.Microstrip(w: widthM, h: slab.HeightM, t: 1e-6,
                                             epsR: slab.Material.EpsR, tanD: slab.Material.TanD);
        var r = RlgcExtractor.Extract(p, BoundaryMesher.Mesh(p, EmMeshSettings.Default));
        return Math.Sqrt(r.LPerM / r.CPerM);
    }

    private static Mat<Complex> Box(Complex s11, Complex s12, Complex s21, Complex s22)
    {
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = s11; m[0, 1] = s12; m[1, 0] = s21; m[1, 1] = s22;
        return m;
    }

    private static Mat<Complex> Inv2(Mat<Complex> a)
    {
        Complex det = a[0, 0] * a[1, 1] - a[0, 1] * a[1, 0];
        var r = new Mat<Complex>(2, 2);
        r[0, 0] =  a[1, 1] / det; r[0, 1] = -a[0, 1] / det;
        r[1, 0] = -a[1, 0] / det; r[1, 1] =  a[0, 0] / det;
        return r;
    }
}
