using System.Diagnostics;
using CircuitRF.Core.Matching;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Match;

/// <summary>Candidate enumeration, the N drive, ranking, and the linkage arithmetic.</summary>
public class MatchSolutionSearchTests(ITestOutputHelper output)
{
    private const double F1 = 3.3e9, F2 = 5.0e9;

    [Fact]
    public void TheGoldenProblem_HasSolutionsThatReachTheFarTerminationExactly()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        var set = MatchSolutionSearch.Search(d, includeQAdjust: false);
        Assert.Null(set.Refusal);
        Assert.NotEmpty(set.Solutions);

        foreach (var s in set.Solutions)
        {
            Assert.Equal(119.027, s.Required, 1e-3);
            Assert.Equal(s.Required, s.Achieved, s.Required * 1e-9);

            // The far port has arrived at the requested 200 ohm, and the far termination's own
            // 0.125 pF has fallen out of the split exactly.
            Assert.Equal(200.0, s.Network.R1, 200.0 * 1e-9);
            Assert.Equal(1.25, s.Network.R2, 1e-12);
            var kept = s.Network.Elements.Single(e => e.AbsorbedEnd == 1);
            Assert.Equal(0.125e-12, kept.Value, 0.125e-12 * 1e-9);
            var absorbed2 = s.Network.Elements.Single(e => e.AbsorbedEnd == 2);
            Assert.Equal(10e-12, absorbed2.Value, 10e-12 * 1e-12);

            Assert.All(s.Network.Elements, e => Assert.True(e.Value > 0, $"{e.Name} = {e.Value}"));
        }
    }

    [Fact]
    public void ASolutionsResponse_IsTheBasisResponse()
    {
        // Everything the transforms do is by construction response-preserving, and the excess split
        // is an identity. So a solved network must still measure the design doc's -16.663 dB.
        var d = MatchAbcdOracle.GoldenDesign();
        var set = MatchSolutionSearch.Search(d, includeQAdjust: false);
        foreach (var s in set.Solutions)
            Assert.Equal(-16.663, MatchAbcdOracle.WorstS11Db(s.Network, F1, F2), 0.02);
    }

    [Fact]
    public void SolutionsAreRankedFewestTransformsFirst()
    {
        var set = MatchSolutionSearch.Search(MatchAbcdOracle.GoldenDesign(), includeQAdjust: false);
        var counts = set.Solutions.Select(s => s.Transforms.Count).ToList();
        Assert.Equal(counts.Order(), counts);
    }

    [Fact]
    public void FingerprintsAreStableAndDistinguishSolutions()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        var a = MatchSolutionSearch.Search(d, includeQAdjust: false);
        var b = MatchSolutionSearch.Search(d, includeQAdjust: false);
        Assert.Equal(a.Solutions.Select(s => s.Fingerprint), b.Solutions.Select(s => s.Fingerprint));
        Assert.Equal(a.Solutions.Count, a.Solutions.Select(s => s.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void ConflictingPairsAreNeverProposedTogether()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.Order = 6;
        var basis = MatchSynthesis.Synthesize(d);
        var pairs = NortonTransform.Discover(basis.Network!);
        foreach (var set in MatchSolutionSearch.EnumerateSets(pairs))
            for (int i = 0; i < set.Count; i++)
                for (int j = i + 1; j < set.Count; j++)
                    Assert.False(NortonTransform.Conflicts(pairs[set[i]], pairs[set[j]]),
                                 $"conflicting pair {set[i]}/{set[j]} was proposed together");
    }

    [Fact]
    public void SolutionEnumerationAtOrderSix_IsMeasuredNotAssumed()
    {
        // The brief asks for this number. It is reported rather than asserted tightly, because a
        // threshold nobody measured is not a gate.
        var d = MatchAbcdOracle.GoldenDesign();
        d.Order = 6;
        var warm = MatchSolutionSearch.Search(d, includeQAdjust: false);
        Assert.Null(warm.Refusal);

        var sw = Stopwatch.StartNew();
        var set = MatchSolutionSearch.Search(d, includeQAdjust: false);
        sw.Stop();
        var pairs = NortonTransform.Discover(set.Basis.Network!);

        output.WriteLine(
            $"n=6: {pairs.Count} pairs, {MatchSolutionSearch.EnumerateSets(pairs).Count} candidate " +
            $"sets, {set.Solutions.Count} solutions, {sw.Elapsed.TotalMilliseconds:0.0} ms");

        var swQ = Stopwatch.StartNew();
        _ = MatchSolutionSearch.Search(d, includeQAdjust: true);
        swQ.Stop();
        output.WriteLine($"n=6 including the Q-adjust bisection: {swQ.Elapsed.TotalMilliseconds:0.0} ms");

        Assert.True(sw.Elapsed.TotalSeconds < 1.0,
                    $"the n=6 enumeration took {sw.Elapsed.TotalSeconds:0.00} s");
    }

    [Fact]
    public void Linkage_KeepsTheProductOnTargetWhenOneSliderMoves()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        var set = MatchSolutionSearch.Search(d, includeQAdjust: false);
        var chosen = set.Solutions.First(s => s.Transforms.Count >= 2);

        var basis = set.Basis;
        var seq = MatchRebuild.ApplySequence(basis, chosen.Transforms, allowNegative: false);
        var slots = seq.Applied
            .Select(a => new LinkSlot(a.Record.N, a.Record.Locked, a.Range))
            .ToList();

        double required = basis.RequiredTransformRatio;
        double before = slots.Aggregate(1.0, (p, s) => p * s.N * s.N);
        Assert.Equal(required, before, required * 1e-9);

        // Drag the first slider a quarter of the way up its range; the others must absorb it.
        double target = slots[0].Range.Min + 0.25 * (slots[0].Range.Max - slots[0].Range.Min);
        var link = MatchLinkage.Redistribute(slots, 0, target, required, link: true);
        Assert.True(link.OnTarget, $"shortfall {link.Shortfall}");
    }

    [Fact]
    public void Linkage_WithOneTransformAndLinkOn_FullyDeterminesN()
    {
        var range = new TransformRange(1.0, 100.0, PropagateRight: false, Threshold: 100.0, NGreaterThanOne: true);
        var link = MatchLinkage.Redistribute([new LinkSlot(1.0, false, range)], 0, 3.0, 119.027, link: true);
        Assert.Equal(Math.Sqrt(119.027), link.N[0], 1e-12);
        Assert.True(link.OnTarget);
    }

    [Fact]
    public void Linkage_NeverWritesALockedTransform()
    {
        var wide = new TransformRange(1e-3, 1000.0, false, 1000.0, true);
        var slots = new List<LinkSlot>
        {
            new(2.0, Locked: false, wide),
            new(3.0, Locked: true, wide),
            new(5.0, Locked: false, wide),
        };
        var link = MatchLinkage.Redistribute(slots, 0, 4.0, 900.0, link: true);
        Assert.Equal(3.0, link.N[1], 1e-12);
    }

    [Fact]
    public void Linkage_ReportsTheShortfallWhenTheProductCannotBeReached()
    {
        var tight = new TransformRange(1.0, 1.5, false, 1.5, true);
        var slots = new List<LinkSlot> { new(1.0, false, tight), new(1.0, false, tight) };
        var link = MatchLinkage.Redistribute(slots, 0, 1.5, 119.027, link: true);
        Assert.False(link.OnTarget);
        Assert.True(link.Shortfall < 1.0);
        Assert.NotEmpty(link.AtLimit);
    }
}
