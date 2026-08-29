using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Tests.Match;

/// <summary>
/// match.md §13.2 rev 3 — the dual-band synthesis of §18.
/// </summary>
/// <remarks>
/// <b>Every golden number in §18.4 reproduced.</b> The design note's arithmetic and this
/// implementation agree to the digits printed, which is why the assertions below are written against
/// its own values rather than against whatever came out — see <c>src/Core/Match/RESOLVED.md</c> §MN-MB1
/// for the two places where the SEARCH lands somewhere the note did not predict (a slightly different
/// K, at the same return loss) and for the Butterworth figure the note left open.
///
/// <para>The response is scored with <see cref="MatchAbcdOracle"/> — a second ABCD cascade written
/// from the two-port definitions — rather than with <c>MatchResponse</c>, so the numbers are not
/// checked by the code that produced them.</para>
/// </remarks>
public class MatchMultibandSynthesisTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // match.md §18.4's problem: 20 Ω ‖ 2.5 pF into 50 Ω over the two Wi-Fi bands. The REQUESTED
    // bands do not mirror; §18.3's rule widens band 1 down to 2.2008547 GHz.
    private static MatchDesign Problem(int order = 2, ResponseShape shape = ResponseShape.ChebyshevFano)
        => new()
        {
            BandCount = 2,
            F1 = 2.4e9, F2 = 2.5e9, F3 = 5.15e9, F4 = 5.85e9,
            Order = order,
            Response = shape,
            Term1 = new Termination(20.0, ReactanceKind.C, TerminationTopology.Parallel, 2.5e-12),
            Term2 = Termination.Resistive(50.0),
            AnalysisEnd = AnalysisEndChoice.Term1,
        };

    private static readonly (double Lo, double Hi)[] EffectiveBands =
        [(2.2008547e9, 2.5e9), (5.15e9, 5.85e9)];

    private static double WorstOverBothBands(MatchNetwork net) =>
        Math.Max(MatchAbcdOracle.WorstS11Db(net, 2.2008547e9, 2.5e9, 401),
                 MatchAbcdOracle.WorstS11Db(net, 5.15e9, 5.85e9, 401));

    private static double GapMax(MatchNetwork net) =>
        MatchAbcdOracle.Band(2.5e9, 5.15e9, 401).Max(f => MatchAbcdOracle.S(net, f).S11.Magnitude);

    // ── 1. The golden member, taken at its own (K, eps^2) ─────────────────────

    /// <summary>
    /// match.md §18.4's eight-element member: the g-vector, the four arms' L and C, the worst return
    /// loss over both bands and the gap maximum.
    /// </summary>
    /// <remarks>
    /// <b>Asserted at the STATED K and eps^2, not at the search's optimum.</b> A member is exact at
    /// its parameters; the optimum over K is only defined to the search's tolerance, and §18.4
    /// measures the worst return loss moving by up to 0.1 dB across a decade of K. Conflating the two
    /// would make this test a statement about the search rather than about the family.
    /// </remarks>
    [Fact]
    public void TheGoldenMember_ReproducesSection18Point4_ToTheDigitsItPrints()
    {
        double[] g = MatchFormPrototype.GvaluesAt(
            ResponseShape.ChebyshevFano, 2, 0.7261974, 3.654984e-5, 6.255720e-4)!;

        double[] expected = [1.1464128, 0.9341372, 2.4818781, 0.4175330, 2.6060058];
        Assert.Equal(expected.Length, g.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], g[i], 1e-6);

        // Through the ordinary §4.4 bandpass transformation at an arm count of 2n — written
        // independently in the oracle, which is the whole structural claim of §18.2 checked by
        // something other than the code that makes it.
        var design = Problem();
        var built = MatchAbcdOracle.LadderFromG(design, [1.0, .. g], 4, design.Term1, anaIsTerm1: true);

        var el = built.Net.Elements;
        Assert.Equal(8, el.Count);
        double[] pico =
        [
            786.96065, 2.5000000,      // shunt arm 0 — the load's own 2.5 pF, exactly
            814.83495, 2.4144787,      // series arm 1
            363.50768, 5.4122698,      // shunt arm 2
            364.20823, 5.4018594,      // series arm 3
        ];
        for (int i = 0; i < pico.Length; i++)
            Assert.Equal(pico[i], el[i].Value * 1e12, 1e-6 * pico[i]);

        Assert.Equal(7.674580, built.RFar, 1e-5);
        Assert.Equal(-31.793, WorstOverBothBands(built.Net), 0.05);
        Assert.Equal(0.4454, GapMax(built.Net), 0.002);
    }

    // ── 2. The search, end to end ─────────────────────────────────────────────

    /// <summary>
    /// <c>MatchSynthesis.Synthesize</c> on the REQUESTED bands finds the optimum, widens band 1, and
    /// absorbs the load's own capacitance exactly.
    /// </summary>
    [Fact]
    public void TheSearch_ReachesTheGoldenReturnLoss_AbsorbsTheLoad_AndSaysItWidenedBandOne()
    {
        var result = MatchSynthesis.Synthesize(Problem());
        Assert.True(result.Ok, result.Refusal?.Message);

        output.WriteLine($"worst over both bands: {WorstOverBothBands(result.Network!):0.####} dB");
        Assert.Equal(-31.793, WorstOverBothBands(result.Network!), 0.05);
        Assert.Equal(7.6746, result.RFarSynthesised, 1e-3);
        Assert.Equal(6.515014, result.RequiredTransformRatio, 1e-3);

        // §18.9's identity: Q is taken at omega0, so the absorbed element comes out equal to the
        // termination's own to machine precision.
        var first = result.Network!.Elements.First(e => e.Type == ElementType.C);
        Assert.Equal(1, first.AbsorbedEnd);
        Assert.Equal(2.5e-12, first.Value, 1e-12 * 2.5e-12);

        Assert.Contains(result.Notes, n => n.Contains("Band 1 widened to 2.201–2.5 GHz", StringComparison.Ordinal));
    }

    // ── 3. What it buys ───────────────────────────────────────────────────────

    /// <summary>
    /// <b>The same eight elements, matched over the two bands instead of the whole span, is 13 dB
    /// better</b> — which is match.md §18.1's reclaim, measured.
    /// </summary>
    [Fact]
    public void EightElementsDualBand_BeatsEightElementsSingleBand_ByOverTenDecibels()
    {
        var dual = MatchSynthesis.Synthesize(Problem());
        Assert.True(dual.Ok, dual.Refusal?.Message);

        // The classical Fano-optimum Chebyshev over the WHOLE span, same element count (order 4
        // bandpass is eight elements), same terminations.
        var single = Problem();
        single.BandCount = 1;
        single.F1 = 2.2008547e9;
        single.F2 = 5.85e9;
        single.Order = 4;
        var wide = MatchSynthesis.Synthesize(single);
        Assert.True(wide.Ok, wide.Refusal?.Message);
        Assert.Equal(8, wide.Network!.Elements.Count);

        double dualWorst = WorstOverBothBands(dual.Network!);
        double wideWorst = WorstOverBothBands(wide.Network!);
        output.WriteLine($"dual {dualWorst:0.###} dB vs single-band {wideWorst:0.###} dB "
                         + $"over the same two bands ({wideWorst - dualWorst:0.###} dB)");
        Assert.True(wideWorst - dualWorst >= 10.0,
            $"the dual-band network is only {wideWorst - dualWorst:0.##} dB better");
    }

    // ── 4. Symmetrisation ─────────────────────────────────────────────────────

    /// <summary>match.md §18.3: keep the wider band, widen the narrower one AWAY from the gap.</summary>
    [Fact]
    public void Symmetrise_WidensTheNarrowerBandOutward_AndAlwaysLandsOnTheMirrorCondition()
    {
        // Band 1 narrower (the 2.4/5 GHz case): widened DOWNWARD.
        var a = MatchBands.Symmetrise(2.4e9, 2.5e9, 5.15e9, 5.85e9);
        Assert.True(a.Widened);
        Assert.Equal(1, a.WidenedBand);
        Assert.Equal(2.2008547e9, a.F1, 1e-6 * 2.2008547e9);
        Assert.Equal(2.5e9, a.F2);
        Assert.Equal(5.15e9, a.F3);
        Assert.Equal(5.85e9, a.F4);

        // Band 2 narrower: widened UPWARD, band 1 untouched.
        var b = MatchBands.Symmetrise(2.2008547e9, 2.5e9, 5.15e9, 5.4e9);
        Assert.True(b.Widened);
        Assert.Equal(2, b.WidenedBand);
        Assert.Equal(2.2008547e9, b.F1);
        Assert.Equal(5.15e9 * (2.5 / 2.2008547), b.F4, 1e-6 * b.F4);

        // Already mirrored: nothing moves, and no note is shown for a widening that did not happen.
        var c = MatchBands.Symmetrise(1e9, 1.2e9, 4e9, 4.8e9);
        Assert.False(c.Widened);
        Assert.Equal(0, c.WidenedBand);
        Assert.Null(c.Note);

        foreach (var e in (EffectiveBands[])[a, b, c])
            Assert.Equal(e.F2 * e.F3, e.F1 * e.F4, 1e-12 * e.F2 * e.F3);
    }

    // ── 5. Parity ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>A like-topology pair now HAS a dual-band order</b> — MN-MB2's weighted family closed the
    /// parity gap match.md §18.2 recorded, with an odd ARM count whose two ends share one
    /// orientation.
    /// </summary>
    /// <remarks>
    /// Order still means match points per band; only the element count moves, from 4n to 4n + 2. The
    /// family is equiripple by construction, so a like pair is Chebyshev-only.
    /// </remarks>
    [Fact]
    public void ALikeTopologyPair_TakesTheOddArmCount()
    {
        var t1 = new Termination(20.0, ReactanceKind.C, TerminationTopology.Parallel, 2.5e-12);
        var t2 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 0.3e-12);

        Assert.Equal([1, 2, 3], MatchOrders.ValidOrders(t1, t2, NetworkForm.Bandpass, 2));
        Assert.True(MatchOrders.NeedsOddCount(t1, t2));

        var design = Problem();
        design.Term2 = t2;
        var result = MatchSynthesis.Synthesize(design);
        Assert.True(result.Ok, result.Refusal?.Message);

        // 4n + 2 elements, both ends absorbed, and the analysis end's own capacitance exactly.
        Assert.Equal(4 * design.Order + 2, result.Network!.Elements.Count);
        var ends = result.Network!.Elements.Where(e => e.AbsorbedEnd != 0).ToList();
        Assert.Equal(2, ends.Count);
        Assert.All(ends, e => Assert.False(e.IsShunt == false && e.Type == ElementType.C));
        Assert.Equal(2.5e-12, ends[0].Value, 1e-12 * 2.5e-12);

        output.WriteLine(
            $"{result.Network!.Elements.Count} elements, worst "
            + $"{WorstOverBothBands(result.Network!):0.###} dB");
        Assert.True(WorstOverBothBands(result.Network!) < -20.0);

        // Butterworth has no odd member: the weighted family comes out of a Remez exchange.
        design.Response = ResponseShape.Butterworth;
        var refused = MatchSynthesis.Synthesize(design);
        Assert.False(refused.Ok);
        Assert.Contains("odd element count", refused.Refusal!.Message, StringComparison.Ordinal);
    }

    /// <summary>A mixed pair, and a pair with one resistive end, both offer 1, 2 and 3.</summary>
    [Fact]
    public void AMixedPair_AndAResistiveEnd_BothOfferOrdersOneTwoAndThree()
    {
        var mixed = Problem();
        mixed.Term2 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Series, 4e-12);
        Assert.Equal([1, 2, 3], MatchOrders.ValidOrders(mixed.Term1, mixed.Term2, NetworkForm.Bandpass, 2));

        var resistiveEnd = Problem();
        Assert.Equal([1, 2, 3],
            MatchOrders.ValidOrders(resistiveEnd.Term1, resistiveEnd.Term2, NetworkForm.Bandpass, 2));

        // ...and every one of them synthesises, which is what makes the list an offer rather than a
        // claim: 4, 8 and 12 elements at orders 1, 2 and 3.
        foreach (int order in (int[])[1, 2, 3])
        {
            var d = Problem(order);
            var r = MatchSynthesis.Synthesize(d);
            Assert.True(r.Ok, $"order {order}: {r.Refusal?.Message}");
            Assert.Equal(4 * order, r.Network!.Elements.Count);
            output.WriteLine($"order {order}: {4 * order} elements, "
                             + $"{WorstOverBothBands(r.Network):0.###} dB, gap {GapMax(r.Network):0.####}");
        }
    }

    // ── 6. The far end ────────────────────────────────────────────────────────

    /// <summary>
    /// match.md §4.5, unchanged over two bands: a far-end reactance the synthesis OVERSHOOTS becomes
    /// an excess element; one it cannot reach is a refusal naming both numbers.
    /// </summary>
    [Fact]
    public void TheFarEnd_TakesAnExcessElementWhenSmall_AndRefusesWhenLarger()
    {
        var small = Problem();
        small.Term2 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Series, 4e-12);
        var ok = MatchSynthesis.Synthesize(small);
        Assert.True(ok.Ok, ok.Refusal?.Message);
        Assert.True(ok.NeedsExcessElement);

        // Through the SOLUTION, not the basis: WithEndSplits runs last, after the transforms have
        // brought the far port to its target resistance, and that is what makes the kept value equal
        // the termination's own exactly rather than only after scaling (MatchSynthesis.WithEndSplits'
        // own remark). Splitting the basis instead is a legal call that gives a different number.
        var set = MatchSolutionSearch.Search(small, includeQAdjust: false);
        var solution = set.Solutions.First(s => s.QAdjust == 0.0);
        Assert.Equal(50.0, solution.Network.R2, 1e-6 * 50.0);
        Assert.Contains(solution.Network.Elements, e => e.IsExcess);
        var absorbed = solution.Network.Elements.Single(e => e.AbsorbedEnd == 2);
        Assert.Equal(4e-12, absorbed.Value, 1e-9 * 4e-12);

        var large = Problem();
        large.Term2 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Series, 0.5e-12);
        var refused = MatchSynthesis.Synthesize(large);
        Assert.False(refused.Ok);
        Assert.Equal(MatchRefusalKind.FarEndNotAbsorbable, refused.Refusal!.Kind);
        Assert.Contains("qFar", refused.Refusal.Numbers.Keys);
        Assert.Contains("qActual", refused.Refusal.Numbers.Keys);
    }

    // ── 7. Norton, on a ladder that is twice as long ──────────────────────────

    /// <summary>
    /// <b>The dual-band ladder is an ordinary alternating bandpass ladder</b>, so §4.7's pair scan
    /// finds its pairs and every one of them is response-preserving — and the solution search reaches
    /// the requested 50 Ω through them. Nothing in <c>NortonTransform</c> or
    /// <c>MatchSolutionSearch</c> was changed for this; that they work is the claim.
    /// </summary>
    [Fact]
    public void EveryNortonPairOnTheEightElementLadder_LeavesTheResponseUnchanged_AndReaches50Ohms()
    {
        var design = Problem();
        var basis = MatchSynthesis.Synthesize(design);
        Assert.True(basis.Ok, basis.Refusal?.Message);

        var net = basis.Network!;
        var pairs = NortonTransform.Discover(net);
        Assert.NotEmpty(pairs);

        var before = MatchAbcdOracle.Band(2.2e9, 5.9e9, 81)
            .Select(f => (f, s: MatchAbcdOracle.S(net, f))).ToList();

        double worst = 0.0;
        int cases = 0;
        foreach (var pair in pairs)
        {
            var range = NortonTransform.Range(net, pair, basis.AnalysisIsTerm1, allowNegative: false);
            Assert.True(range.IsUsable, $"{pair.NameA}/{pair.NameB} range collapsed");
            foreach (double frac in (double[])[0.05, 0.5, 0.95])
            {
                double n = range.Min + (range.Max - range.Min) * frac;
                foreach (var form in (TransformForm[])[TransformForm.Pi, TransformForm.T])
                {
                    var applied = NortonTransform.Apply(
                        net, pair, n, form, basis.AnalysisIsTerm1, allowNegative: false, ordinal: 1);
                    Assert.False(applied.GuardFired);
                    foreach (var (f, s) in before)
                    {
                        var t = MatchAbcdOracle.S(applied.Network, f);
                        worst = Math.Max(worst, (s.S11 - t.S11).Magnitude);
                        worst = Math.Max(worst, (s.S21 - t.S21).Magnitude);
                    }
                    cases++;
                }
            }
        }

        output.WriteLine($"{pairs.Count} pairs, {cases} cases, worst |ΔS| {worst:0.###e+00}");
        Assert.True(cases > 20, $"only {cases} transform cases were exercised");
        Assert.True(worst < 1e-9, $"a transform moved the response by {worst:0.###e+00}");

        var set = MatchSolutionSearch.Search(design, includeQAdjust: false);
        Assert.NotEmpty(set.Solutions);
        var reaching = set.Solutions.First();
        Assert.Equal(6.515014, basis.RequiredTransformRatio, 1e-3);
        Assert.Equal(50.0, reaching.Network.R2, 1e-6 * 50.0);
    }

    // ── 8. Resistive ends ─────────────────────────────────────────────────────

    /// <summary>
    /// match.md §18.2: with NEITHER end reactive there is no g1 to prescribe, so K sits on its floor
    /// and eps^2 comes from the requested ripple — and the achieved return loss is exactly what that
    /// ripple implies.
    /// </summary>
    [Fact]
    public void WithBothEndsResistive_TheRipplePrototypeRuns_AndHitsTheRippleItWasGiven()
    {
        var design = Problem();
        design.Term1 = Termination.Resistive(20.0);
        design.Term2 = Termination.Resistive(50.0);
        design.RippleDb = 0.1;

        var result = MatchSynthesis.Synthesize(design);
        Assert.True(result.Ok, result.Refusal?.Message);
        Assert.True(result.UsedRipplePrototype);
        Assert.Equal(8, result.Network!.Elements.Count);

        // The ripple is an INSERTION-loss figure, so |Gamma|^2 = 1 - 10^(-ripple/10).
        double implied = 10.0 * Math.Log10(1.0 - Math.Pow(10.0, -0.1 / 10.0));
        output.WriteLine($"implied {implied:0.####} dB, achieved {WorstOverBothBands(result.Network):0.####} dB");
        Assert.Equal(implied, WorstOverBothBands(result.Network), 0.05);
    }

    // ── 9. Butterworth ────────────────────────────────────────────────────────

    /// <summary>Butterworth extracts over two bands, and is worse than Chebyshev at the same n.</summary>
    [Fact]
    public void Butterworth_Extracts_AndIsWorseThanChebyshevAtTheSameOrder()
    {
        var cheb = MatchSynthesis.Synthesize(Problem());
        var butter = MatchSynthesis.Synthesize(Problem(2, ResponseShape.Butterworth));
        Assert.True(butter.Ok, butter.Refusal?.Message);
        Assert.Equal(8, butter.Network!.Elements.Count);

        double b = WorstOverBothBands(butter.Network), c = WorstOverBothBands(cheb.Network!);
        output.WriteLine($"Butterworth {b:0.###} dB vs Chebyshev {c:0.###} dB ({b - c:0.###} dB worse)");
        Assert.True(b > c, "Butterworth should not beat the equiripple family");
    }

    // ── 10. Persistence ───────────────────────────────────────────────────────

    /// <summary>
    /// <b>A pre-rev-3 payload decodes as single-band and rebuilds to the identical ladder</b>, which
    /// is what makes the four new fields additive and lets <c>Version</c> stay 1.
    /// </summary>
    [Fact]
    public void APayloadWrittenBeforeMultiband_DecodesAsSingleBand_AndRebuildsIdentically()
    {
        const string legacy = """
            {"Version":1,"F1":3300000000,"F2":5000000000,"Order":4,"Response":"ChebyshevFano",
             "RippleDb":0.1,
             "Term1":{"R":200,"Kind":"C","Topology":"Parallel","Value":1.25e-13},
             "Term2":{"R":1.25,"Kind":"C","Topology":"Series","Value":1e-11},
             "AnalysisEnd":"Highest"}
            """;

        Assert.True(MatchEmbedding.TryDecode(legacy, out var decoded));
        Assert.Equal(1, decoded!.BandCount);
        Assert.Equal(0.0, decoded.F3);
        Assert.Equal(0.0, decoded.A);
        Assert.Equal(2.0 * Math.PI * Math.Sqrt(3.3e9 * 5.0e9), decoded.Omega0, 1e-6);

        var fromLegacy = MatchSynthesis.Synthesize(decoded);
        var fromGolden = MatchSynthesis.Synthesize(MatchAbcdOracle.GoldenDesign());
        Assert.True(fromLegacy.Ok);
        Assert.Equal(fromGolden.BasisFingerprint, fromLegacy.BasisFingerprint);
    }

    /// <summary>A dual-band design round-trips through the payload to the identical ladder.</summary>
    [Fact]
    public void ADualBandDesign_RoundTripsToTheIdenticalLadder()
    {
        var design = Problem();
        Assert.True(MatchEmbedding.TryDecode(MatchEmbedding.Encode(design), out var back));

        Assert.Equal(2, back!.BandCount);
        Assert.Equal(5.15e9, back.F3);
        Assert.Equal(5.85e9, back.F4);
        Assert.Equal(
            MatchSynthesis.Synthesize(design).BasisFingerprint,
            MatchSynthesis.Synthesize(back).BasisFingerprint);
    }

    // ── 11. The forms multiband does not have ─────────────────────────────────

    /// <summary>match.md §18.6: lowpass and highpass multiband is route B, and is refused by name.</summary>
    [Theory]
    [InlineData(NetworkForm.Lowpass)]
    [InlineData(NetworkForm.Highpass)]
    public void AMultibandDesignInLowpassOrHighpassForm_IsRefusedNamingSection18Point6(NetworkForm form)
    {
        var design = Problem();
        design.Form = form;

        var result = MatchSynthesis.Synthesize(design);
        Assert.False(result.Ok);
        Assert.Equal(MatchRefusalKind.ResponseInfeasible, result.Refusal!.Kind);
        // No section reference: a refusal is rendered verbatim in the Designer's status strip, and a
        // user does not read design-note sections (owner, 2026-08-28).
        Assert.DoesNotContain("§", result.Refusal.Message, StringComparison.Ordinal);
        Assert.Contains("multiband networks are not offered", result.Refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Bessel and the double-match Chebyshev are not offered over two bands (§18.2).</summary>
    [Theory]
    [InlineData(ResponseShape.Bessel)]
    [InlineData(ResponseShape.ChebyshevTwoEnded)]
    public void BesselAndTheDoubleMatchChebyshev_AreRefusedOverTwoBands(ResponseShape shape)
    {
        var result = MatchSynthesis.Synthesize(Problem(2, shape));
        Assert.False(result.Ok);
        Assert.Equal(MatchRefusalKind.ResponseInfeasible, result.Refusal!.Kind);
        Assert.DoesNotContain("§", result.Refusal.Message, StringComparison.Ordinal);
        Assert.Contains("not offered in dual-band form", result.Refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>The four edges must increase, and the refusal carries all four numbers.</summary>
    [Fact]
    public void BandsThatDoNotIncrease_AreRefusedWithAllFourNumbers()
    {
        var design = Problem();
        design.F3 = 2.4e9;   // inside band 1 — there is no gap

        var result = MatchSynthesis.Synthesize(design);
        Assert.False(result.Ok);
        Assert.Equal(MatchRefusalKind.InvalidTermination, result.Refusal!.Kind);
        foreach (string key in (string[])["f1", "f2", "f3", "f4"])
            Assert.Contains(key, result.Refusal.Numbers.Keys);
    }
}
