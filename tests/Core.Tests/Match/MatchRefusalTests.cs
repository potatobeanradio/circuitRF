using CircuitRF.Core.Matching;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Match;

/// <summary>
/// Brief §9: every "cannot" is a returned value carrying its numbers. MN-3 renders these verbatim, so
/// a refusal that does not carry its numbers is not finished.
/// </summary>
public class MatchRefusalTests(ITestOutputHelper output)
{
    [Fact]
    public void FarEndNotAbsorbable_NamesTheEndTheTwoQsAndTheRatio()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.AnalysisEnd = AnalysisEndChoice.Term1;      // forced onto the LOWER-Q end
        var r = MatchSynthesis.Synthesize(d);

        Assert.False(r.Ok);
        var f = r.Refusal!;
        Assert.Equal(MatchRefusalKind.FarEndNotAbsorbable, f.Kind);
        Assert.Equal(2, f.End);
        Assert.Equal(3.1345, f.Numbers["qActual"], 1e-4);
        Assert.True(f.Numbers["qFar"] < f.Numbers["qActual"]);
        Assert.Equal(f.Numbers["qFar"] / f.Numbers["qActual"], f.Numbers["ratio"], 1e-12);
        Assert.Contains("2", f.Message, StringComparison.Ordinal);
        output.WriteLine(f.Message);
    }

    [Fact]
    public void InvalidOrder_NamesTheOrderAndTheValidRange()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.Order = 5;
        var f = MatchSynthesis.Synthesize(d).Refusal!;
        Assert.Equal(MatchRefusalKind.InvalidOrder, f.Kind);
        Assert.Equal(5.0, f.Numbers["order"]);
        Assert.Equal(2.0, f.Numbers["minValid"]);
        Assert.Equal(6.0, f.Numbers["maxValid"]);
        output.WriteLine(f.Message);
    }

    [Fact]
    public void InvalidTermination_RefusesAShortRatherThanTreatingItAsNoReactance()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.Term2 = new Termination(1.25, ReactanceKind.L, TerminationTopology.Series, 0.0);
        var f = MatchSynthesis.Synthesize(d).Refusal!;
        Assert.Equal(MatchRefusalKind.InvalidTermination, f.Kind);
        Assert.Equal(2, f.End);
        Assert.Equal(0.0, f.Numbers["value"]);

        var neg = MatchAbcdOracle.GoldenDesign();
        neg.Term1 = neg.Term1 with { R = -1.0 };
        Assert.Equal(MatchRefusalKind.InvalidTermination,
                     MatchSynthesis.Synthesize(neg).Refusal!.Kind);
    }

    [Fact]
    public void NoTransformablePairs_CarriesTheLadderAsSynthesised()
    {
        // A parallel INDUCTIVE end and a series CAPACITIVE end make both L and C absorbed types, so
        // at order 2 every candidate pair touches an absorbed element of its own type. Under the
        // reference implementation's "an L pair is always allowed" rule this design would have
        // offered a transform that breaks the absorption.
        var d = MatchAbcdOracle.GoldenDesign();
        d.Order = 2;
        d.Term1 = new Termination(200.0, ReactanceKind.L, TerminationTopology.Parallel, 8e-9);

        var basis = MatchSynthesis.Synthesize(d);
        Assert.True(basis.Ok, basis.Refusal?.Message);
        Assert.Empty(NortonTransform.Discover(basis.Network!));

        var f = MatchSolutionSearch.Search(d, includeQAdjust: false).Refusal!;
        Assert.Equal(MatchRefusalKind.NoTransformablePairs, f.Kind);
        Assert.Equal(4.0, f.Numbers["elements"]);
        Assert.Equal(basis.RequiredTransformRatio, f.Numbers["required"], 1e-9);
        output.WriteLine(f.Message);
    }

    [Fact]
    public void TransformsCannotReachTarget_CarriesAchievedAgainstRequired()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.Order = 2;
        d.Term1 = Termination.Resistive(10_000.0);

        var f = MatchSolutionSearch.Search(d, includeQAdjust: false).Refusal!;
        Assert.Equal(MatchRefusalKind.TransformsCannotReachTarget, f.Kind);
        Assert.True(f.Numbers["bestAchieved"] < f.Numbers["required"]);
        Assert.Equal(1.0, f.Numbers["pairs"]);
        output.WriteLine(f.Message);
    }

    [Fact]
    public void ResponseInfeasible_CarriesTheFamilysMaximumQFar()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.Order = 4;
        d.Response = ResponseShape.Bessel;

        var f = MatchSynthesis.Synthesize(d).Refusal!;
        Assert.Equal(MatchRefusalKind.ResponseInfeasible, f.Kind);
        Assert.Equal(1, f.End);
        Assert.True(f.Numbers["maxQFar"] < f.Numbers["qRequired"]);
        Assert.Contains("Bessel", f.Message, StringComparison.Ordinal);
        output.WriteLine(f.Message);
    }

    [Fact]
    public void NoRealRoot_IsUnreachableForOrdersTwoToSix_AndTheReasonIsStructural()
    {
        // The brief and match.md both treat "P_n(c) has no real root" as a first-class outcome. It is
        // implemented and it carries its numbers, but with the design doc's own coefficient table it
        // cannot happen: n = 3 and n = 4 give a CUBIC in r and n = 5 and n = 6 a QUINTIC, and a real
        // polynomial of odd degree always has a real root. n = 2 does not root-find at all.
        //
        // Recorded rather than deleted: the refusal path is still the right shape for a future order
        // or a different table, and a reader who assumes it fires is otherwise misled.
        int nulls = 0, total = 0;
        foreach (int n in (int[])[2, 3, 4, 5, 6])
            foreach (double q in new[] { 1e-6, 1e-4, 1e-2, 0.1, 1.0, 10.0, 1e3, 1e6 })
                foreach (double w in new[] { 1e-4, 1e-2, 0.1, 0.4, 1.0, 1.9 })
                {
                    total++;
                    if (MatchSynthesis.FanoG(n, q, w) is null) nulls++;
                }

        output.WriteLine($"FanoG returned no solution in {nulls} of {total} (Q, w, n) combinations");
        Assert.Equal(0, nulls);

        // The refusal itself is still reachable through the guard, and still carries its numbers.
        Assert.Null(MatchSynthesis.FanoG(4, 0.0, 0.4));
        Assert.Null(MatchSynthesis.FanoG(7, 3.0, 0.4));
    }
}
