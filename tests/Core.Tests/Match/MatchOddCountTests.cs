using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Tests.Match;

/// <summary>
/// match.md §16.3 and §18.5's <b>odd element counts</b> — the weighted family
/// <c>Phi(u) = (u + uR) R_k(u)^2</c>, which is what lets a LIKE termination pair be absorbed at both
/// ends in lowpass, highpass and multiband form.
/// </summary>
/// <remarks>
/// <b>The odd count is about ABSORPTION, not about return loss</b>, and that correction is the first
/// thing these tests pin. The brief this work came from expected the 5-element member to beat the
/// 4-element one; it does not, and cannot: <c>Phi(0) = uR . R_k(0)^2</c> rises monotonically in uR
/// toward the EVEN family's own <c>T_k(x0)^2</c> and never past it, so the odd member at k is a hair
/// worse than the even member at the same k however the extra pole is placed. What it buys is a
/// ladder whose two ends share one orientation — which the even family cannot produce at any order,
/// and without which the classic shunt-C-to-shunt-C interstage has no network at all. See
/// <c>src/Core/Match/RESOLVED.md</c> §MN-MB2.
/// </remarks>
public class MatchOddCountTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static MatchDesign Lowpass(int order, Termination t1, Termination t2) => new()
    {
        F1 = 1.0e9,
        F2 = 2.0e9,
        Order = order,
        Form = NetworkForm.Lowpass,
        Response = ResponseShape.ChebyshevFano,
        Term1 = t1,
        Term2 = t2,
        AnalysisEnd = AnalysisEndChoice.Term1,
    };

    private static readonly Termination HighEnd =
        new(50.0, ReactanceKind.C, TerminationTopology.Parallel, 0.4e-12);
    private static readonly Termination LowEnd =
        new(10.0, ReactanceKind.C, TerminationTopology.Parallel, 3e-12);

    // ── 1. What the odd count is, and is not ─────────────────────────────────

    /// <summary>
    /// The element count is <c>2n + 1</c>, both ends are absorbed, and both are shunt.
    /// </summary>
    [Fact]
    public void ALikeParallelPair_ExtractsAnOddLadderWithBothEndsShuntAndAbsorbed()
    {
        for (int n = 2; n <= 4; n++)
        {
            var result = MatchSynthesis.Synthesize(Lowpass(n, HighEnd, LowEnd));
            Assert.True(result.Ok, $"order {n}: {result.Refusal?.Message}");

            var el = result.Network!.Elements;
            Assert.Equal(2 * n + 1, el.Count);
            Assert.All(el.Where(e => e.AbsorbedEnd != 0), e => Assert.True(e.IsShunt));
            Assert.Equal([1, 2], el.Where(e => e.AbsorbedEnd != 0).Select(e => e.AbsorbedEnd).Order());

            // The DC pin makes the far port land on its target exactly, so no transform is needed.
            Assert.Equal(50.0, result.Network!.R1, 1e-9);
            Assert.Equal(10.0, result.Network!.R2, 1e-8);

            // Each absorbed end element is at least what its termination supplies — the feasibility
            // test doing its job, in the units TheFarEndFeasibilityTest_IsInTheAnalysisEndsUnits pins.
            Assert.True(el.First(e => e.AbsorbedEnd == 1).Value >= 0.4e-12);
            Assert.True(el.First(e => e.AbsorbedEnd == 2).Value >= 3e-12);

            double worst = MatchAbcdOracle.WorstS11Db(result.Network!, 1e9, 2e9, 401);
            output.WriteLine($"order {n}: {el.Count} elements, worst {worst:0.###} dB");
            Assert.True(worst < -13.0, $"order {n}: {worst:0.###} dB");
        }
    }

    /// <summary>
    /// <b>The odd member at order n is a shade WORSE than the even member at the same order</b>, and
    /// better than the even member one order below — so the counts interleave 2n, 2n + 1, 2n + 2 in
    /// element count and in return loss, with the odd rung sitting just under the even one above it.
    /// </summary>
    /// <remarks>
    /// This is the corrected form of match.md §16.3's claim, and it is why the odd family is offered
    /// for ABSORPTION rather than as a finer grain of performance. <c>Phi(0)</c>, which the DC pin is
    /// written against, is <c>uR . R_n(0)^2</c> and rises monotonically toward the even family's
    /// <c>T_n(x0)^2</c> as the extra pole recedes — never past it.
    /// </remarks>
    [Fact]
    public void TheOddMember_SitsJustBelowTheEvenMemberOfTheSameOrder()
    {
        const double A = 0.5, Ratio = 5.0;
        for (int n = 2; n <= 4; n++)
        {
            double even = MatchFormPrototype.BestReturnLossDb(n, A, Ratio);
            double evenBelow = MatchFormPrototype.BestReturnLossDb(n - 1, A, Ratio);

            double best = double.PositiveInfinity;
            foreach (double uR in (double[])[0.4, 2.5, 15.0, 150.0, 1500.0])
            {
                var r = MatchRemez.MinimaxWeightedScaled(n, uR, [(A * A, 1.0)]);
                Assert.NotNull(r);
                double r0 = r!.At(0.0), phi0 = uR * r0 * r0;
                double g0 = (Ratio - 1) / (Ratio + 1), g02 = g0 * g0;
                best = Math.Min(best, 10.0 * Math.Log10(g02 / (g02 + phi0 * (1.0 - g02))));
            }

            output.WriteLine(
                $"n = {n}: even {2 * n} elements {even:0.###} dB, odd {2 * n + 1} elements "
                + $"{best:0.###} dB, even {2 * n - 2} elements {evenBelow:0.###} dB");

            Assert.True(best > even, "the odd member never beats the even one at the same order");
            Assert.True(best < evenBelow, "but it beats the even one an order below");
            Assert.True(best - even > -0.2 && best - even < 0.2,
                $"and it approaches it: {best - even:0.###} dB apart");
        }
    }

    // ── 2. The multiband like pair ───────────────────────────────────────────

    private static MatchDesign Multiband(int order, int bands) => new()
    {
        BandCount = bands,
        F1 = 0.5e9, F2 = 0.6e9, F3 = 0.9e9, F4 = 1.1e9, F5 = 1.65e9, F6 = 1.98e9,
        Order = order,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 4e-12),
        Term2 = new Termination(25.0, ReactanceKind.C, TerminationTopology.Parallel, 1e-12),
        AnalysisEnd = AnalysisEndChoice.Term1,
    };

    /// <summary>
    /// A like pair over two or three bands gives <c>4n + 2</c> elements, both ends absorbed — the
    /// parity gap match.md §18.2 recorded, closed.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void AMultibandLikePair_GivesFourNPlusTwoElementsWithBothEndsAbsorbed()
    {
        foreach (int bands in (int[])[2, 3])
            // Dual caps at 3 and tri at 6 (match.md §21 rev 5) — the odd family has to hold at every
            // order each band count actually offers, not at the lower of the two.
            for (int n = 1;
                 n <= (bands >= 3 ? MatchOrders.MaxTriBandOrder : MatchOrders.MaxMultibandOrder);
                 n++)
            {
                var design = Multiband(n, bands);
                var result = MatchSynthesis.Synthesize(design);
                Assert.True(result.Ok, $"{bands} bands, order {n}: {result.Refusal?.Message}");

                Assert.Equal(4 * n + 2, result.Network!.Elements.Count);
                var ends = result.Network!.Elements.Where(e => e.AbsorbedEnd != 0).ToList();
                Assert.Equal(2, ends.Count);
                Assert.All(ends, e => Assert.True(e.IsShunt));

                // §18.9's identity holds for the odd family too: Q is read at omega0.
                Assert.Equal(4e-12, ends.First(e => e.AbsorbedEnd == 1).Value, 1e-12 * 4e-12);

                double worst = design.Bands.Max(
                    b => MatchAbcdOracle.WorstS11Db(result.Network!, b.Lo, b.Hi, 401));
                output.WriteLine(
                    $"{bands} bands, order {n}: {4 * n + 2} elements, worst {worst:0.###} dB");
                Assert.True(worst < -10.0);
            }
    }

    // ── 3. The parity trap ───────────────────────────────────────────────────

    /// <summary>
    /// <b>An odd extraction's terminating value is a CONDUCTANCE ratio, not an impedance one</b> —
    /// reading it with the even rule inverts the far resistance and turns a -14.3 dB match into
    /// -0.5 dB with every element value still correct.
    /// </summary>
    /// <remarks>
    /// Each Cauer removal swaps impedance for admittance, so the remainder after an odd number of
    /// them is the reciprocal of what it is after an even number: the extraction returns
    /// <c>min(r, 1/r)</c> where the even family returns <c>max(r, 1/r)</c>. This test is the
    /// measurement that found it, kept as a gate because the failure is a plausible-looking network
    /// with correct components and a hopeless response.
    /// </remarks>
    [Fact]
    public void TheOddTerminatingValue_IsTheReciprocalOfTheEvenOne()
    {
        var result = MatchSynthesis.Synthesize(Lowpass(2, HighEnd, LowEnd));
        Assert.True(result.Ok, result.Refusal?.Message);

        double right = MatchAbcdOracle.WorstS11Db(result.Network!, 1e9, 2e9, 401);

        // The same five elements, with the far port at the reciprocal resistance.
        var wrong = result.Network!.Clone();
        wrong.R2 = wrong.R1 * wrong.R1 / result.Network!.R2;
        double flipped = MatchAbcdOracle.WorstS11Db(wrong, 1e9, 2e9, 401);

        output.WriteLine($"as built {right:0.###} dB, far port inverted {flipped:0.###} dB");
        Assert.True(right < -13.0);
        Assert.True(flipped - right > 5.0,
            $"inverting the far port must ruin the match: {right:0.###} -> {flipped:0.###} dB");

        // And the g-vector's terminating value really is the SMALLER of the two ratios.
        Assert.Equal(1.0 / 5.0, result.G[^1], 1e-6);
    }

    // ── 4. Refusals ──────────────────────────────────────────────────────────

    /// <summary>Butterworth has no odd member — a Remez exchange produces equiripple or nothing.</summary>
    [Fact]
    public void ButterworthHasNoOddMember_InMultibandForm()
    {
        var design = Multiband(2, 2);
        design.Response = ResponseShape.Butterworth;
        var result = MatchSynthesis.Synthesize(design);
        Assert.False(result.Ok);
        Assert.Equal(MatchRefusalKind.ResponseInfeasible, result.Refusal!.Kind);
        Assert.Contains("odd element count", result.Refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The far-end feasibility test compares BOTH ends in the analysis end's units, which is the
    /// prototype's own convention — a lowpass ladder's element values do not rescale at the far port.
    /// </summary>
    /// <remarks>
    /// <b>Kept as a gate because getting it wrong refuses designs that work.</b> A 50 ohm ‖ 0.4 pF to
    /// 10 ohm + 1 nH lowpass design builds a 1.4755 nH far element, which absorbs the termination's
    /// 1 nH with room to spare; normalising the termination at its own 10 ohm instead made it look
    /// like 1.2566 against 0.3708 and refused.
    /// </remarks>
    [Fact]
    public void TheFarEndFeasibilityTest_IsInTheAnalysisEndsUnits()
    {
        var design = Lowpass(
            2,
            HighEnd,
            new Termination(10.0, ReactanceKind.L, TerminationTopology.Series, 1e-9));

        var result = MatchSynthesis.Synthesize(design);
        Assert.True(result.Ok, result.Refusal?.Message);

        var farEl = result.Network!.Elements.First(e => e.AbsorbedEnd == 2);
        output.WriteLine($"far element {farEl.Name} = {farEl.Value:e5} against 1e-9 supplied");
        Assert.Equal(ElementType.L, farEl.Type);
        Assert.False(farEl.IsShunt);
        Assert.True(farEl.Value >= 1e-9, $"{farEl.Value:e5} must absorb the termination's 1 nH");
    }
}
