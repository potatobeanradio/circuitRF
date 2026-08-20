using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Tests.Match;

/// <summary>The §4.9 golden values, the absorption identity, and the duality claim of §5.</summary>
public class MatchSynthesisTests
{
    private const double F1 = 3.3e9, F2 = 5.0e9;

    [Fact]
    public void BandCentreAndFractionalBandwidth_MatchTheDesignDoc()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        Assert.Equal(4.06202e9, d.Omega0 / (2 * Math.PI), 4.06202e9 * 1e-5);
        Assert.Equal(0.418511, d.W, 1e-6);
    }

    [Fact]
    public void TerminationQ_MatchesTheDesignDoc()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        Assert.Equal(0.63806, d.Term1.QAt(d.Omega0), 1e-5);
        Assert.Equal(3.13450, d.Term2.QAt(d.Omega0), 1e-5);
    }

    [Fact]
    public void FanoGValues_ReproduceTheDesignDocAtOrderFour()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        double[] g = MatchSynthesis.FanoG(4, d.Term2.QAt(d.Omega0), d.W)!;
        double[] expected = [1, 1.311823, 1.106975, 1.717201, 0.508891, 1.344236];
        Assert.Equal(expected.Length, g.Length);
        for (int i = 0; i < g.Length; i++) Assert.Equal(expected[i], g[i], 1e-5);
    }

    [Theory]
    [InlineData(2, 1.900)]
    [InlineData(4, 1.635)]
    [InlineData(6, 1.468)]
    public void QFar_MatchesTheDesignDocAtEveryValidOrder(int order, double expectedQFar)
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.Order = order;
        var r = MatchSynthesis.Synthesize(d);
        Assert.True(r.Ok, r.Refusal?.Message);
        Assert.Equal(expectedQFar, r.QFarSynthesised, 1e-3);
    }

    [Fact]
    public void GoldenLadder_ReproducesEveryElementValue()
    {
        var r = MatchSynthesis.Synthesize(MatchAbcdOracle.GoldenDesign());
        Assert.True(r.Ok, r.Refusal?.Message);
        var net = r.Network!;

        // Term1-first: the far (200 ohm) end's shunt arm comes first.
        (bool shunt, double l, double c)[] expected =
        [
            (true, 40.27824e-12, 38.11411e-12),
            (false, 200.95667e-12, 7.63931e-12),
            (true, 18.51644e-12, 82.90847e-12),
            (false, 153.51694e-12, 10.0e-12),
        ];

        Assert.Equal(8, net.Elements.Count);
        for (int arm = 0; arm < 4; arm++)
        {
            var el = net.Elements[2 * arm];
            var ec = net.Elements[2 * arm + 1];
            Assert.Equal($"L{arm + 1}", el.Name);
            Assert.Equal($"C{arm + 1}", ec.Name);
            Assert.Equal(expected[arm].shunt, el.IsShunt);
            Assert.Equal(expected[arm].shunt, ec.IsShunt);
            Assert.Equal(expected[arm].l, el.Value, expected[arm].l * 1e-5);
            Assert.Equal(expected[arm].c, ec.Value, expected[arm].c * 1e-5);
        }

        Assert.Equal(1.68030, r.RFarSynthesised, 1e-4);
        Assert.Equal(119.027, r.RequiredTransformRatio, 1e-3);
        Assert.Equal(1.68030, net.R1, 1e-4);
        Assert.Equal(1.25, net.R2, 1e-9);
    }

    [Fact]
    public void AbsorbedElement_EqualsTheTerminationsOwnValueExactly()
    {
        var r = MatchSynthesis.Synthesize(MatchAbcdOracle.GoldenDesign());
        var absorbed = r.Network!.Elements.Single(e => e.AbsorbedEnd == 2);
        Assert.Equal("C4", absorbed.Name);
        Assert.Equal(10e-12, absorbed.Value, 10e-12 * 1e-12);
    }

    [Fact]
    public void GoldenResponse_MatchesTheDesignDoc()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        var r = MatchSynthesis.Synthesize(d);
        var (loss, ripple) = MatchAbcdOracle.Il(r.Network!, F1, F2);
        Assert.Equal(-16.663, MatchAbcdOracle.WorstS11Db(r.Network!, F1, F2), 0.02);
        Assert.Equal(0.095, loss, 0.02);
        Assert.Equal(0.0361, ripple, 0.002);
    }

    [Fact]
    public void InductiveTermination_IsExactlyDualToTheCapacitiveOne()
    {
        // match.md §5: replacing the 1.25 ohm + 10 pF series load with 1.25 ohm + 153.5169 pH gives
        // the same Q, the same prototype, the same ladder and the same response - the 153.5169 pH now
        // arriving from the load and the 10 pF being ours to build.
        var cap = MatchAbcdOracle.GoldenDesign();
        var ind = MatchAbcdOracle.GoldenDesign();
        ind.Term2 = new Termination(1.25, ReactanceKind.L, TerminationTopology.Series, 153.51694e-12);

        Assert.Equal(3.13450, ind.Term2.QAt(ind.Omega0), 1e-5);

        var a = MatchSynthesis.Synthesize(cap);
        var b = MatchSynthesis.Synthesize(ind);
        Assert.True(b.Ok, b.Refusal?.Message);

        Assert.Equal(a.Network!.Elements.Count, b.Network!.Elements.Count);
        for (int i = 0; i < a.Network.Elements.Count; i++)
        {
            Assert.Equal(a.Network.Elements[i].Type, b.Network.Elements[i].Type);
            Assert.Equal(a.Network.Elements[i].IsShunt, b.Network.Elements[i].IsShunt);
            Assert.Equal(a.Network.Elements[i].Value, b.Network.Elements[i].Value,
                         a.Network.Elements[i].Value * 1e-5);
        }

        // ... and the absorbed element is now the INDUCTOR, not the capacitor.
        Assert.Equal(ElementType.L, b.Network.Elements.Single(e => e.AbsorbedEnd == 2).Type);

        foreach (double f in MatchAbcdOracle.Band(F1, F2))
        {
            var sa = MatchAbcdOracle.S(a.Network, f);
            var sb = MatchAbcdOracle.S(b.Network, f);
            Assert.True((sa.S11 - sb.S11).Magnitude < 1e-5);
            Assert.True((sa.S21 - sb.S21).Magnitude < 1e-5);
        }
    }

    [Fact]
    public void OrderParity_FollowsTheTerminationTopologies()
    {
        var mixed = MatchAbcdOracle.GoldenDesign();
        Assert.Equal([2, 4, 6], MatchOrders.ValidOrders(mixed.Term1, mixed.Term2));

        var like = new Termination(1.25, ReactanceKind.C, TerminationTopology.Parallel, 10e-12);
        Assert.Equal([3, 5], MatchOrders.ValidOrders(mixed.Term1, like));

        var resistive = Termination.Resistive(50.0);
        Assert.Equal([2, 3, 4, 5, 6], MatchOrders.ValidOrders(mixed.Term1, resistive));
        Assert.Equal([2, 3, 4, 5, 6], MatchOrders.ValidOrders(resistive, resistive));
    }

    [Fact]
    public void AnUnsatisfiableOrder_IsRefusedWithItsNumbers()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.Order = 3;
        var r = MatchSynthesis.Synthesize(d);
        Assert.False(r.Ok);
        Assert.Equal(MatchRefusalKind.InvalidOrder, r.Refusal!.Kind);
        Assert.Equal(3.0, r.Refusal.Numbers["order"]);
    }

    [Theory]
    [InlineData(2, new[] { 1.0, 0.8431, 0.6220, 1.3554 })]
    [InlineData(3, new[] { 1.0, 1.0316, 1.1474, 1.0316, 1.0 })]
    [InlineData(4, new[] { 1.0, 1.1088, 1.3062, 1.7704, 0.8181, 1.3554 })]
    [InlineData(5, new[] { 1.0, 1.1468, 1.3712, 1.9750, 1.3712, 1.1468, 1.0 })]
    [InlineData(6, new[] { 1.0, 1.1681, 1.4040, 2.0562, 1.5171, 1.9029, 0.8618, 1.3554 })]
    public void RipplePrototype_MatchesThePublishedMatthaeiTable(int n, double[] expected)
    {
        double[] g = MatchSynthesis.RippleG(n, 0.1);
        Assert.Equal(expected.Length, g.Length);
        for (int i = 0; i < g.Length; i++) Assert.Equal(expected[i], g[i], 5e-5);
    }

    [Fact]
    public void TwoEndedPrototype_PrescribesBothEndsExactly()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.Response = ResponseShape.ChebyshevTwoEnded;
        double qAna = d.Term2.QAt(d.Omega0), qFar = d.Term1.QAt(d.Omega0);

        double[] g = MatchSynthesis.TwoEndedG(4, qAna, qFar, d.W)!;
        Assert.Equal(qAna * d.W, g[1], qAna * d.W * 1e-12);
        Assert.Equal(qFar, g[4] * g[5] / d.W, qFar * 1e-12);

        var r = MatchSynthesis.Synthesize(d);
        Assert.True(r.Ok, r.Refusal?.Message);
        Assert.Equal(qFar, r.QFarSynthesised, qFar * 1e-12);
        Assert.False(r.NeedsExcessElement);   // and therefore no CFano is ever produced
        Assert.DoesNotContain(
            MatchSynthesis.WithEndSplits(r.Network!, r, d).Elements, e => e.IsExcess);
    }

    [Fact]
    public void BothEndsResistive_FallsThroughToTheRipplePrototypeAndSaysSo()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        d.Term1 = Termination.Resistive(50.0);
        d.Term2 = Termination.Resistive(10.0, TerminationTopology.Series);
        d.Response = ResponseShape.ChebyshevTwoEnded;

        var r = MatchSynthesis.Synthesize(d);
        Assert.True(r.Ok, r.Refusal?.Message);
        Assert.True(r.UsedRipplePrototype);
        Assert.Contains(r.Notes, s => s.Contains("equal-ripple", StringComparison.Ordinal));
    }

    [Fact]
    public void ExcessSplit_LeavesTheTerminationsOwnValueAndDoesNotMoveTheResponse()
    {
        var d = MatchAbcdOracle.GoldenDesign();
        var r = MatchSynthesis.Synthesize(d);
        Assert.True(r.NeedsExcessElement);

        var split = MatchSynthesis.WithEndSplits(r.Network!, r, d);
        var fano = Assert.Single(split.Elements, e => e.IsExcess);
        Assert.Equal("CFano", fano.Name);

        // The far port is still at the SYNTHESISED 1.6803 ohm here (no transforms yet), so the kept
        // value is the load's own 0.125 pF scaled by the transform ratio still owed.
        var kept = split.Elements.Single(e => e.AbsorbedEnd == 1);
        Assert.Equal(0.125e-12 * r.RequiredTransformRatio, kept.Value, 0.125e-12 * r.RequiredTransformRatio * 1e-9);

        foreach (double f in MatchAbcdOracle.Band(F1, F2))
        {
            var a = MatchAbcdOracle.S(r.Network!, f);
            var b = MatchAbcdOracle.S(split, f);
            Assert.True((a.S11 - b.S11).Magnitude < 1e-12, $"S11 moved at {f}");
            Assert.True((a.S21 - b.S21).Magnitude < 1e-12, $"S21 moved at {f}");
        }
    }
}
