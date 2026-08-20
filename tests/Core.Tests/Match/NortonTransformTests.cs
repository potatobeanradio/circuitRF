using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Tests.Match;

/// <summary>
/// The single most valuable test in the brief: <b>a Norton transform must not change the response.</b>
/// If applying one moves S11 or S21 at all, the implementation is wrong, not the tolerance — it
/// catches sign errors, propagation-direction errors, net-renaming errors and swap errors at once.
/// </summary>
public class NortonTransformTests
{
    private const double F1 = 3.3e9, F2 = 5.0e9;

    private static (MatchNetwork Net, MatchSynthesisResult Result) Golden(int order = 4)
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.Order = order;
        var r = MatchSynthesis.Synthesize(d);
        Assert.True(r.Ok, r.Refusal?.Message);
        return (r.Network!, r);
    }

    [Fact]
    public void EveryTransform_AtEveryNInRange_LeavesS11AndS21Unchanged()
    {
        double worst = 0.0;
        int cases = 0;

        foreach (int order in (int[])[2, 4, 6])
        {
            var (net, r) = Golden(order);
            var before = MatchAbcdOracle.Band(F1, F2).Select(f => (f, s: MatchAbcdOracle.S(net, f))).ToList();

            foreach (var pair in NortonTransform.Discover(net))
            {
                var range = NortonTransform.Range(net, pair, r.AnalysisIsTerm1, allowNegative: false);
                Assert.True(range.IsUsable, $"{pair.NameA}/{pair.NameB} range collapsed");

                foreach (double frac in (double[])[0.02, 0.25, 0.5, 0.75, 0.98])
                {
                    double n = range.Min + (range.Max - range.Min) * frac;
                    foreach (var form in (TransformForm[])[TransformForm.Pi, TransformForm.T])
                    {
                        var applied = NortonTransform.Apply(
                            net, pair, n, form, r.AnalysisIsTerm1, allowNegative: false, ordinal: 1);
                        Assert.False(applied.GuardFired,
                            $"an absolute guard fired for {pair.NameA}/{pair.NameB} {form} at N={n}");

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
        }

        Assert.True(cases > 20, $"only {cases} transform cases were exercised");
        Assert.True(worst < 1e-9, $"a transform moved the response by {worst:0.###e+00}");
    }

    [Fact]
    public void TransformProducts_StayPositiveAcrossASweepOfTerminationsOrdersAndResponses()
    {
        int checked_ = 0;
        foreach (var (t1, t2) in TerminationSweep())
        {
            foreach (int order in MatchOrders.ValidOrders(t1, t2))
            {
                foreach (var shape in (ResponseShape[])
                         [ResponseShape.ChebyshevFano, ResponseShape.ChebyshevTwoEnded])
                {
                    var d = MatchAbcdOracle.GoldenDesign();
                    d.Term1 = t1;
                    d.Term2 = t2;
                    d.Order = order;
                    d.Response = shape;

                    var r = MatchSynthesis.Synthesize(d);
                    if (!r.Ok) continue;
                    Assert.All(r.Network!.Elements, e => Assert.True(e.Value > 0, $"{e.Name} <= 0"));

                    foreach (var pair in NortonTransform.Discover(r.Network))
                    {
                        var range = NortonTransform.Range(r.Network, pair, r.AnalysisIsTerm1, false);
                        if (!range.IsUsable) continue;
                        foreach (double frac in (double[])[0.05, 0.5, 0.95])
                        foreach (var form in (TransformForm[])[TransformForm.Pi, TransformForm.T])
                        {
                            var a = NortonTransform.Apply(
                                r.Network, pair, range.Min + (range.Max - range.Min) * frac,
                                form, r.AnalysisIsTerm1, false, 1);
                            Assert.False(a.GuardFired, "an absolute guard fired");
                            Assert.All(a.Network.Elements,
                                       e => Assert.True(e.Value > 0, $"{e.Name} = {e.Value}"));
                            checked_++;
                        }
                    }
                }
            }
        }
        Assert.True(checked_ > 100, $"only {checked_} networks were checked");
    }

    private static IEnumerable<(Termination, Termination)> TerminationSweep()
    {
        Termination[] ends =
        [
            new(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
            new(50.0, ReactanceKind.C, TerminationTopology.Parallel, 0.5e-12),
            new(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
            new(5.0, ReactanceKind.L, TerminationTopology.Series, 100e-12),
            new(120.0, ReactanceKind.L, TerminationTopology.Parallel, 2e-9),
        ];
        foreach (var a in ends)
            foreach (var b in ends)
                if (!ReferenceEquals(a, b)) yield return (a, b);
    }

    [Fact]
    public void MovableRule_KeysOnTheAbsorbedElementsOwnType_NotOnLBeingSafe()
    {
        // Capacitive load: the absorbed elements are capacitors, so an L pair is always allowed and
        // the reference implementation's "L is safe" shortcut happens to give the right answer.
        var (capNet, _) = Golden();
        var capPairs = NortonTransform.Discover(capNet);
        Assert.Contains(capPairs, p => p.NameA == "L3" && p.NameB == "L4");

        // The dual load absorbs an INDUCTOR instead. L4 is now an absorbed element, so the L3/L4 pair
        // must be refused - and the reference's rule would have allowed it, because its type is L.
        var d = MatchAbcdOracle.GoldenDesign();
        d.Term2 = new Termination(1.25, ReactanceKind.L, TerminationTopology.Series, 153.51694e-12);
        var r = MatchSynthesis.Synthesize(d);
        Assert.True(r.Ok, r.Refusal?.Message);

        var absorbed = r.Network!.Elements.Single(e => e.AbsorbedEnd == 2);
        Assert.Equal("L4", absorbed.Name);

        var indPairs = NortonTransform.Discover(r.Network);
        Assert.DoesNotContain(indPairs, p => p.NameA == "L3" && p.NameB == "L4");

        // With BOTH ends absorbed the far end's capacitor is protected too, so the pairs that survive
        // are exactly those touching neither absorbed element.
        Assert.All(indPairs, p =>
        {
            var a = r.Network.Find(p.NameA)!;
            var b = r.Network.Find(p.NameB)!;
            Assert.False(a.IsAbsorbed && b.IsAbsorbed);
            if (a.IsAbsorbed || b.IsAbsorbed)
                Assert.DoesNotContain(r.Network.Elements.Where(e => e.IsAbsorbed), e => e.Type == a.Type);
        });
    }

    [Fact]
    public void AdjacencyMoves_NeverSwapAcrossAnOrientationBoundary()
    {
        // The move offsets in match.md §4.7 are only response-preserving because every swap they ask
        // for is inside one arm. Apply() asserts that rather than trusting the table; this test
        // exercises every discovered pair at every reachable order so the assert is really hit.
        foreach (int order in (int[])[2, 4, 6])
        {
            var (net, r) = Golden(order);
            foreach (var pair in NortonTransform.Discover(net))
            {
                var range = NortonTransform.Range(net, pair, r.AnalysisIsTerm1, false);
                var ex = Record.Exception(() => NortonTransform.Apply(
                    net, pair, 0.5 * (range.Min + range.Max), TransformForm.Pi,
                    r.AnalysisIsTerm1, false, 1));
                Assert.Null(ex);
            }
        }
    }

    [Fact]
    public void PropagationIsAlwaysAwayFromTheAnalysisEnd()
    {
        // This is what keeps the analysis end's absorbed element at exactly the termination's own
        // value however many transforms are applied, and what makes "achieved = product of N^2"
        // a statement about the FAR port rather than an average over both.
        var (net, r) = Golden();
        Assert.False(r.AnalysisIsTerm1);
        Assert.All(NortonTransform.Discover(net),
                   p => Assert.False(NortonTransform.Range(net, p, r.AnalysisIsTerm1, false).PropagateRight));

        // Mirrored: the high-Q series end is now termination 1, so the analysis end is Term1 and
        // every transform must propagate the other way.
        var mirrored = MatchAbcdOracle.GoldenDesign();
        (mirrored.Term1, mirrored.Term2) = (mirrored.Term2, mirrored.Term1);
        var r2 = MatchSynthesis.Synthesize(mirrored);
        Assert.True(r2.Ok, r2.Refusal?.Message);
        Assert.True(r2.AnalysisIsTerm1);
        Assert.All(NortonTransform.Discover(r2.Network!),
                   p => Assert.True(NortonTransform.Range(r2.Network!, p, r2.AnalysisIsTerm1, false).PropagateRight));
    }

    [Fact]
    public void AllowNegativeComponents_IsTheOnlyThingThatWidensTheRangePastTheThreshold()
    {
        var (net, r) = Golden();
        var pair = NortonTransform.Discover(net).First(p => p.NameA == "L1");

        var bounded = NortonTransform.Range(net, pair, r.AnalysisIsTerm1, allowNegative: false);
        var wide = NortonTransform.Range(net, pair, r.AnalysisIsTerm1, allowNegative: true);
        Assert.True(bounded.Max < bounded.Threshold);
        Assert.True(wide.Max > bounded.Threshold);

        // Past the threshold the element values really do go negative - which is what makes the
        // bounded range, and not any downstream check, the thing enforcing positivity.
        var over = NortonTransform.Apply(
            net, pair, bounded.Threshold * 1.5, TransformForm.Pi, r.AnalysisIsTerm1,
            allowNegative: true, ordinal: 1);
        Assert.Contains(over.Network.Elements, e => e.Value < 0);

        // ... and the response is STILL invariant, which is why the option exists at all.
        foreach (double f in MatchAbcdOracle.Band(F1, F2))
        {
            var a = MatchAbcdOracle.S(net, f);
            var b = MatchAbcdOracle.S(over.Network, f);
            Assert.True((a.S11 - b.S11).Magnitude < 1e-9);
            Assert.True((a.S21 - b.S21).Magnitude < 1e-9);
        }
    }
}
