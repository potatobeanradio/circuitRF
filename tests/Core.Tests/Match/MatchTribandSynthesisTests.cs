using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Tests.Match;

/// <summary>
/// match.md §13.2 rev 3 and §18.5 — the tri-band synthesis: a kept middle band, a mirrored outer
/// pair, and a Remez polynomial on the union of two prototype intervals.
/// </summary>
/// <remarks>
/// <b>Scored with <see cref="MatchAbcdOracle"/></b>, a second ABCD cascade written from the two-port
/// definitions, so the response numbers are not checked by the code that produced them.
/// </remarks>
public class MatchTribandSynthesisTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>match.md §18.5's own problem: 50 ohm ‖ 4 pF over three already-mirrored bands.</summary>
    /// <remarks>
    /// <b>The three bands mirror exactly as typed</b>, which is not a coincidence in the design note:
    /// <c>f3.f4 = 0.99</c> and the image of 1.65-1.98 GHz about <c>sqrt(0.99)</c> is 0.5-0.6 GHz to the
    /// digit. So this fixture measures the SYNTHESIS with symmetrisation inert, and
    /// <see cref="TheMirrorRule_WidensEachOuterBandOntoItsPartnersImage"/> measures symmetrisation on
    /// its own.
    /// </remarks>
    private static MatchDesign Problem(int order = 2) => new()
    {
        BandCount = 3,
        F1 = 0.5e9, F2 = 0.6e9, F3 = 0.9e9, F4 = 1.1e9, F5 = 1.65e9, F6 = 1.98e9,
        Order = order,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 4e-12),
        Term2 = Termination.Resistive(50.0),
        AnalysisEnd = AnalysisEndChoice.Term1,
    };

    private static double WorstOverAllBands(MatchNetwork net, MatchDesign design) =>
        design.Bands.Max(b => MatchAbcdOracle.WorstS11Db(net, b.Lo, b.Hi, 401));

    // ── 1. The band algebra ───────────────────────────────────────────────────

    /// <summary>
    /// match.md §18.3's tri-band rule: the middle band is kept, and each outer band is widened to
    /// cover both itself and the log-mirror of its partner.
    /// </summary>
    [Fact]
    public void TheMirrorRule_WidensEachOuterBandOntoItsPartnersImage()
    {
        // f3.f4 = 1.0, so the image of 1.65-1.98 is 0.50505-0.60606 and the image of 0.5-0.6 is
        // 1.6667-2.0. Band 1 grows upward to 0.60606, band 3 grows upward to 2.0.
        var e = MatchBands.Symmetrise3(0.5e9, 0.6e9, 0.8e9, 1.25e9, 1.65e9, 1.98e9);

        Assert.True(e.Widened);
        Assert.Equal(3, e.Count);
        Assert.Equal(0.5e9, e.F1, 1.0);
        Assert.Equal(0.60606060606e9, e.F2, 1.0);
        Assert.Equal(0.8e9, e.F3, 1.0);            // the middle band is untouched
        Assert.Equal(1.25e9, e.F4, 1.0);
        Assert.Equal(1.65e9, e.F5, 1.0);
        Assert.Equal(2.0e9, e.F6, 1.0);

        Assert.Contains("band 1 to 0.5–0.606 GHz", e.Note, StringComparison.Ordinal);
        Assert.Contains("band 3 to 1.65–2 GHz", e.Note, StringComparison.Ordinal);
        Assert.Contains("band 2 is kept", e.Note, StringComparison.Ordinal);

        // omega0 is the MIDDLE band's centre, and the mirror identity makes the outer pair agree.
        Assert.Equal(Math.Sqrt(0.8e9 * 1.25e9), e.Omega0 / (2.0 * Math.PI), 1.0);
        Assert.Equal(e.F3 * e.F4, e.F1 * e.F6, 1e-3 * e.F3 * e.F4);
        Assert.Equal(e.F3 * e.F4, e.F2 * e.F5, 1e-3 * e.F3 * e.F4);
    }

    /// <summary>An already-mirrored spec is left exactly alone and says nothing.</summary>
    [Fact]
    public void AnAlreadyMirroredSpec_IsNotWidenedAndCarriesNoNote()
    {
        var e = Problem().Effective;
        Assert.False(e.Widened);
        Assert.Null(e.Note);
        Assert.Equal(0.5e9, e.F1, 1.0);
        Assert.Equal(1.98e9, e.F6, 1.0);
    }

    /// <summary>
    /// <b>The overlap refusal cannot fire for an ordered spec, and that is a theorem rather than a
    /// measurement.</b>
    /// </summary>
    /// <remarks>
    /// match.md §18.5 asks for a refusal when a widened outer band reaches the middle one. The guard
    /// is implemented — <see cref="EffectiveBands.Overlaps"/>, and the synthesis names the remedy —
    /// but it is unreachable from a valid spec: <c>f2' = max(f2, f0^2/f5)</c>, and
    /// <c>f0^2/f5 &lt; f0^2/f4 = f3</c> because <c>f5 &gt; f4</c>, so both arguments of the max are
    /// already below f3. The mirror image of a band ABOVE f4 lands below f3 by construction. The
    /// symmetric statement holds at the other end.
    ///
    /// <para>So this asserts the theorem over a wide random sweep of ordered specs rather than
    /// producing a case that refuses, and separately checks that the guard still recognises a set that
    /// does overlap — which is what protects the intervals from a spec that is not yet ordered.</para>
    /// </remarks>
    [Fact]
    public void MirroringNeverPushesAnOuterBandIntoTheMiddleOne()
    {
        var rng = new Random(20260828);
        for (int i = 0; i < 20000; i++)
        {
            double f1 = 0.05 + 3.0 * rng.NextDouble();
            double f2 = f1 * (1.0 + 2.0 * rng.NextDouble());
            double f3 = f2 * (1.0 + 2.0 * rng.NextDouble());
            double f4 = f3 * (1.0 + 2.0 * rng.NextDouble());
            double f5 = f4 * (1.0 + 2.0 * rng.NextDouble());
            double f6 = f5 * (1.0 + 2.0 * rng.NextDouble());

            var e = MatchBands.Symmetrise3(f1, f2, f3, f4, f5, f6);
            Assert.False(e.Overlaps, $"{f1} {f2} {f3} {f4} {f5} {f6}");

            // And the union in u is two genuinely disjoint intervals inside [0, 1].
            var iv = e.Intervals;
            Assert.Equal(2, iv.Count);
            Assert.True(iv[0].Lo == 0.0 && iv[0].Hi > 0.0 && iv[0].Hi < iv[1].Lo && iv[1].Hi == 1.0,
                $"[{iv[0].Lo}, {iv[0].Hi}] u [{iv[1].Lo}, {iv[1].Hi}]");
        }

        // The guard itself still recognises an overlapping set, which is what a half-typed spec needs.
        Assert.True(new EffectiveBands(1, 2, 2, 3, false, 0, null, 4, 5, 3).Overlaps);
    }

    // ── 2. The golden member ──────────────────────────────────────────────────

    /// <summary>
    /// match.md §18.5's measured targets, reproduced: <b>8 elements at -12.0 dB and 12 at -14.5 dB</b>
    /// across all three bands, with <c>R_far ~ 29.9 ohm</c> at order 2.
    /// </summary>
    /// <remarks>
    /// <b>The design note's figures were a scratch exchange's, "targets to confirm, not goldens".</b>
    /// They confirm to 0.03 dB, which is inside the 0.3 dB the brief allows and close enough that the
    /// exact numbers this implementation produces are recorded in <c>RESOLVED.md</c> §MN-MB2 as the
    /// goldens from here on.
    /// </remarks>
    [Fact]
    public void TheGoldenTriBandMembers_ReproduceSection18Point5()
    {
        foreach (var (order, elements, want, rFar) in
                 (( int Order, int Elements, double Db, double RFar)[])
                 [(1, 4, -8.941, 23.679), (2, 8, -11.997, 29.918), (3, 12, -14.473, 34.107)])
        {
            var design = Problem(order);
            var result = MatchSynthesis.Synthesize(design);
            Assert.True(result.Ok, result.Refusal?.Message);

            double worst = WorstOverAllBands(result.Network!, design);
            output.WriteLine(
                $"order {order}: {result.Network!.Elements.Count} elements, worst {worst:0.####} dB, "
                + $"R_far {result.RFarSynthesised:0.####} ohm");

            Assert.Equal(elements, result.Network!.Elements.Count);
            Assert.Equal(want, worst, 0.01);
            Assert.Equal(rFar, result.RFarSynthesised, 0.01);

            // §18.9's identity: Q is read at omega0, so the absorbed element is the load's own.
            var first = result.Network!.Elements.First(x => x.Type == ElementType.C);
            Assert.Equal(1, first.AbsorbedEnd);
            Assert.Equal(4e-12, first.Value, 1e-12 * 4e-12);
        }
    }

    /// <summary>
    /// <b>All three bands come out at the SAME worst return loss</b>, which is the equiripple property
    /// of the Akhiezer polynomial showing through the whole chain — prototype, resonating transform
    /// and ladder — and is the one observable that says the union was solved rather than one interval.
    /// </summary>
    [Fact]
    public void TheResponse_IsEquirippleAcrossAllThreeBands()
    {
        foreach (int order in (int[])[1, 2, 3])
        {
            var design = Problem(order);
            var result = MatchSynthesis.Synthesize(design);
            Assert.True(result.Ok, result.Refusal?.Message);

            var perBand = design.Bands
                .Select(b => MatchAbcdOracle.WorstS11Db(result.Network!, b.Lo, b.Hi, 801))
                .ToArray();
            output.WriteLine($"order {order}: " + string.Join(" / ", perBand.Select(v => $"{v:0.####}")));
            Assert.Equal(perBand[0], perBand[1], 0.01);
            Assert.Equal(perBand[0], perBand[2], 0.01);
        }
    }

    /// <summary>
    /// The worst in-band <c>|Gamma|^2</c> is <c>(K + eps^2)/(1 + eps^2)</c> — because
    /// <c>max|p| = 1</c> on the union — checked through the ABCD oracle at a member whose K and eps^2
    /// are chosen rather than searched for.
    /// </summary>
    [Fact]
    public void TheWorstInBandFigure_IsTheClosedFormOfKAndEpsilon()
    {
        var design = Problem(2);
        var intervals = design.Effective.Intervals;

        foreach (var (k, eps2) in ((double, double)[])[(1e-9, 1e-2), (1e-4, 6e-2), (1e-6, 3e-3)])
        {
            var p = MatchRemez.MinimaxScaled(2, intervals);
            Assert.NotNull(p);
            double[]? g = MatchFormPrototype.GvaluesAtPolynomial(p!, k, eps2);
            Assert.NotNull(g);

            var built = MatchAbcdOracle.LadderFromG(
                design, [1.0, .. g!], 4, design.Term1, anaIsTerm1: true);
            double worst = WorstOverAllBands(built.Net, design);
            double closed = 10.0 * Math.Log10(MatchFormPrototype.WorstInBand(k, eps2));

            output.WriteLine($"K = {k:e1}, eps^2 = {eps2:e1}: oracle {worst:0.####} dB, "
                             + $"closed form {closed:0.####} dB");
            Assert.Equal(closed, worst, 0.05);
        }
    }

    // ── 3. Coverage ───────────────────────────────────────────────────────────

    /// <summary>The three band sets MB2 measures on — narrow bands, a wide middle, wide outers.</summary>
    private static (string Name, double[] F)[] BandSets =>
    [
        ("narrow", [0.5e9, 0.6e9, 0.9e9, 1.1e9, 1.65e9, 1.98e9]),
        ("wide middle", [0.5e9, 0.6e9, 0.8e9, 1.25e9, 1.65e9, 1.98e9]),
        ("wide outers", [0.4e9, 0.7e9, 0.9e9, 1.1e9, 1.4e9, 2.5e9]),
    ];

    private static MatchDesign With((string Name, double[] F) set, int order)
    {
        var d = Problem(order);
        var f = set.F;
        (d.F1, d.F2, d.F3, d.F4, d.F5, d.F6) = (f[0], f[1], f[2], f[3], f[4], f[5]);
        return d;
    }

    /// <summary>
    /// Every cell extracts: <b>all six orders</b> across three band sets — narrow bands, a wide
    /// middle band, and wide outer bands.
    /// </summary>
    /// <remarks>
    /// <b>Orders 4, 5 and 6 are match.md §21 rev 5's milestone 3</b>, and this is the sweep that
    /// bought them: no cell refuses, and the element count is 4n throughout, up to 24 at order 6.
    /// </remarks>
    [Fact]
    public void EveryCell_Extracts()
    {
        var sets = BandSets;

        foreach (var (name, f) in sets)
            for (int n = 1; n <= MatchOrders.MaxTriBandOrder; n++)
            {
                var design = Problem(n);
                (design.F1, design.F2, design.F3, design.F4, design.F5, design.F6) =
                    (f[0], f[1], f[2], f[3], f[4], f[5]);

                var result = MatchSynthesis.Synthesize(design);
                Assert.True(result.Ok, $"{name}, order {n}: {result.Refusal?.Message}");
                Assert.Equal(4 * n, result.Network!.Elements.Count);

                double worst = WorstOverAllBands(result.Network!, design);
                output.WriteLine($"{name}, order {n}: {4 * n} elements, worst {worst:0.###} dB");
                Assert.True(worst < 0.0, $"{name}, order {n}: worst {worst:0.###} dB");
            }
    }

    /// <summary>
    /// <b>Milestone 3's own gate</b> (match.md §21 rev 5): the Cauer extraction reaches the closed
    /// form <c>(K + eps^2)/(1 + eps^2)</c> at <b>every degree 1..6 on a UNION of intervals</b>.
    /// </summary>
    /// <remarks>
    /// <b>This is what makes raising the cap a measurement rather than a constant change.</b>
    /// <c>GvaluesAtPolynomial</c> was proven to degree 6 on a SINGLE interval, and the design note is
    /// explicit that what decays with degree there is the EXTRACTION's conditioning rather than the
    /// root finding. On a union the polynomial's roots sit differently, so degree 4, 5 and 6 had to
    /// be measured before they could be offered.
    ///
    /// <para>The member's K and eps^2 are CHOSEN here rather than searched for, exactly as
    /// <see cref="TheWorstInBandFigure_IsTheClosedFormOfKAndEpsilon"/> does at degree 2: a searched
    /// member's own optimum is not a closed form and would only be being compared with itself. What
    /// is under test is the path from a polynomial to a ladder.</para>
    /// </remarks>
    [Fact]
    public void TheExtraction_ReachesTheClosedFormAtEveryDegreeOnAUnion()
    {
        foreach (var set in BandSets)
        {
            var design = With(set, 2);
            var intervals = design.Effective.Intervals;

            for (int n = 1; n <= MatchOrders.MaxTriBandOrder; n++)
            {
                var p = MatchRemez.MinimaxScaled(n, intervals);
                Assert.NotNull(p);

                foreach (var (k, eps2) in ((double, double)[])[(1e-9, 1e-2), (1e-6, 3e-3)])
                {
                    double[]? g = MatchFormPrototype.GvaluesAtPolynomial(p!, k, eps2);
                    Assert.NotNull(g);

                    var built = MatchAbcdOracle.LadderFromG(
                        design, [1.0, .. g!], 2 * n, design.Term1, anaIsTerm1: true);
                    double worst = WorstOverAllBands(built.Net, design);
                    double closed = 10.0 * Math.Log10(MatchFormPrototype.WorstInBand(k, eps2));

                    output.WriteLine(
                        $"{set.Name}, degree {n}, K = {k:e0}, eps^2 = {eps2:e0}: "
                        + $"oracle {worst:0.####} dB, closed form {closed:0.####} dB");
                    Assert.Equal(closed, worst, 0.05);
                }
            }
        }
    }

    /// <summary>
    /// The equiripple property survives the resonating transform at <b>every</b> offered order: all
    /// three bands come out at the same worst return loss.
    /// </summary>
    /// <remarks>
    /// The observable signature of a correct extraction on a union, and the one that would break
    /// first if degree 4-6 were mis-extracted — a ladder whose polynomial is not the minimax one puts
    /// its bands at different depths.
    /// </remarks>
    [Fact]
    public void EveryOrder_PutsAllThreeBandsAtTheSameDepth()
    {
        foreach (var set in BandSets)
            for (int n = 1; n <= MatchOrders.MaxTriBandOrder; n++)
            {
                var design = With(set, n);
                var result = MatchSynthesis.Synthesize(design);
                Assert.True(result.Ok, $"{set.Name}, order {n}: {result.Refusal?.Message}");

                double[] perBand =
                    [.. design.Bands.Select(b => MatchAbcdOracle.WorstS11Db(result.Network!, b.Lo, b.Hi, 401))];

                output.WriteLine(
                    $"{set.Name}, order {n}: " + string.Join(" / ", perBand.Select(v => $"{v:0.###}")));
                Assert.Equal(perBand[0], perBand[1], 0.05);
                Assert.Equal(perBand[0], perBand[2], 0.05);
            }
    }

    // ── 4. Refusals ───────────────────────────────────────────────────────────

    /// <summary>A tri-band spec that is not six increasing frequencies is refused by name.</summary>
    [Fact]
    public void AnUnorderedTriBandSpec_IsRefused()
    {
        var design = Problem();
        design.F5 = 1.05e9;                        // below f4
        var result = MatchSynthesis.Synthesize(design);
        Assert.False(result.Ok);
        Assert.Contains("f1 < f2 < f3 < f4 < f5 < f6", result.Refusal!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Butterworth has no tri-band member, and the refusal says why rather than reporting a
    /// failure.</b> The maximally-flat family is flat at ONE interval's centre; a union has no such
    /// point, and the equiripple polynomial is the only member of the family there.
    /// </summary>
    [Theory]
    [InlineData(ResponseShape.Butterworth)]
    [InlineData(ResponseShape.Bessel)]
    [InlineData(ResponseShape.ChebyshevTwoEnded)]
    public void OnlyChebyshevHasATriBandMember(ResponseShape shape)
    {
        var design = Problem();
        design.Response = shape;
        var result = MatchSynthesis.Synthesize(design);
        Assert.False(result.Ok);
        Assert.Equal(MatchRefusalKind.ResponseInfeasible, result.Refusal!.Kind);
        Assert.Contains("Chebyshev is offered", result.Refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Lowpass and highpass tri-band is route B (match.md §18.6) and is refused.</summary>
    [Fact]
    public void LowpassTriBand_IsRefused()
    {
        var design = Problem();
        design.Form = NetworkForm.Lowpass;
        var result = MatchSynthesis.Synthesize(design);
        Assert.False(result.Ok);
        Assert.Contains("bandpass form", result.Refusal!.Message, StringComparison.Ordinal);
    }

    // ── 5. Nothing about the dual-band path moved ─────────────────────────────

    /// <summary>
    /// <b>match.md §18.4's dual-band goldens are unchanged</b> by the general-polynomial route
    /// arriving beside them — the dual path still takes the closed form, and a payload written before
    /// this brief rebuilds to the identical ladder.
    /// </summary>
    [Fact]
    public void TheDualBandGoldens_AreUntouched()
    {
        var dual = new MatchDesign
        {
            BandCount = 2,
            F1 = 2.4e9, F2 = 2.5e9, F3 = 5.15e9, F4 = 5.85e9,
            Order = 2,
            Term1 = new Termination(20.0, ReactanceKind.C, TerminationTopology.Parallel, 2.5e-12),
            Term2 = Termination.Resistive(50.0),
            AnalysisEnd = AnalysisEndChoice.Term1,
        };

        var result = MatchSynthesis.Synthesize(dual);
        Assert.True(result.Ok, result.Refusal?.Message);
        Assert.Equal(
            -31.793,
            Math.Max(MatchAbcdOracle.WorstS11Db(result.Network!, 2.2008547e9, 2.5e9, 401),
                     MatchAbcdOracle.WorstS11Db(result.Network!, 5.15e9, 5.85e9, 401)),
            0.05);
        Assert.Equal(7.6746, result.RFarSynthesised, 1e-3);

        // A design carrying F5/F6 but only two bands ignores them, which is what makes the reserved
        // fields safe to have been written since MN-MB1.
        dual.F5 = 9e9;
        dual.F6 = 11e9;
        var again = MatchSynthesis.Synthesize(dual);
        Assert.True(again.Ok);
        Assert.Equal(result.RFarSynthesised, again.RFarSynthesised, 1e-9);
    }
}
