using CircuitRF.Core.Matching;

namespace CircuitRF.Core.Tests.Match;

/// <summary>
/// match.md §13.2 rev 2 — the lowpass and highpass forms of §16.
/// </summary>
/// <remarks>
/// <b>Three of §16's own numbers did not reproduce, and each is pinned here in its corrected form
/// rather than to the letter of the document</b> (the whole of it is in
/// <c>src/Core/Match/RESOLVED.md</c>):
/// <list type="bullet">
/// <item>§3.7's physical golden line describes the r = 0.1 network, not a 50 ohm far end. The
/// normalised g-vector it quotes is exact and is asserted as written.</item>
/// <item>The orientation is decided by the impedance RATIO, not by the analysis end's topology, so
/// "shunt-first for a parallel analysis end" is not a thing the synthesis can honour.</item>
/// <item>K's floor is 1e-12, not 1e-6: K is the response's own return-loss floor as much as a
/// numerical one, and 1e-6 costs 0.12 dB on §16.2's deepest cell and 2.4e-3 on the from-DC
/// reduction.</item>
/// </list>
/// </remarks>
public class MatchFormSynthesisTests
{
    private const double Wc5GHz = 2.0 * Math.PI * 5e9;

    private static MatchDesign Design(
        NetworkForm form, int order, Termination t1, Termination t2,
        double f1 = 2.5e9, double f2 = 5.0e9,
        ResponseShape response = ResponseShape.ChebyshevFano) => new()
        {
            Form = form,
            Order = order,
            Response = response,
            F1 = f1,
            F2 = f2,
            Term1 = t1,
            Term2 = t2,
            AnalysisEnd = AnalysisEndChoice.Term1,
        };

    // ── 1. Golden A — the normalised g-vector and its physical denormalisation ──

    [Fact]
    public void GoldenA_NormalisedGVector_MatchesTheDesignDocToOnePartInAHundredThousand()
    {
        // a = 0.5, n = 2 (four elements), r = 10, K = 1e-6 — match.md §3.7, quoted AT its K rather
        // than at the synthesis floor, which is 1e-12 (see MatchFormPrototype.KFloor).
        double[] g = MatchFormPrototype.Gvalues(ResponseShape.ChebyshevFano, 2, 0.5, 10.0, 1e-6)!;

        Assert.Equal(5, g.Length);
        double[] expected = [2.485340, 0.674662, 6.761736, 0.247821, 10.000000];
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], g[i], 1e-5 * Math.Max(1.0, expected[i]));
    }

    [Fact]
    public void GoldenA_WorstReturnLoss_IsTheClosedFormMinus10Point511dB()
    {
        double g02 = Math.Pow(9.0 / 11.0, 2);
        double eps2 = (g02 - 1e-6) / (MatchFormPrototype.PhiAtDc(ResponseShape.ChebyshevFano, 2, 0.5)
                                      * (1.0 - g02));
        double worst = 10.0 * Math.Log10(MatchFormPrototype.WorstInBand(1e-6, eps2));
        Assert.Equal(-10.511, worst, 0.001);
        Assert.Equal(-10.511, MatchFormPrototype.BestReturnLossDb(2, 0.5, 10.0), 0.001);
    }

    [Fact]
    public void GoldenA_Physical_IsTheShuntFirstLadderSteppingDOWN_NotUpToFiftyOhms()
    {
        // §3.7 prints C1 = 15.8222 pF, L1 = 107.376 pH, C2 = 43.0466 pF, L2 = 39.4420 pH for
        // "5 ohm (analysis, parallel side) -> 50 ohm". Those are the g's denormalised at R_ana = 5
        // and read SHUNT-first, which is the r = 0.1 network: 5 ohm down to 0.5. Asserting the
        // element values AND the far resistance together is what makes the correction a test rather
        // than a comment.
        var d = Design(NetworkForm.Lowpass, 2,
            Termination.Resistive(5.0), Termination.Resistive(0.5));
        var r = MatchSynthesis.Synthesize(d);

        Assert.True(r.Ok, r.Refusal?.Message);
        Assert.Equal(0.5, r.Network!.R2, 1e-12);
        Assert.Equal(0.5, r.RFarSynthesised, 1e-9);

        // At the 1e-12 floor rather than §3.7's 1e-6, so the values move in the fourth digit.
        double[] gAtFloor = MatchFormPrototype.Gvalues(
            ResponseShape.ChebyshevFano, 2, 0.5, 0.1, MatchFormPrototype.KFloor)!;
        double[] want =
        [
            gAtFloor[0] / (Wc5GHz * 5.0), gAtFloor[1] * 5.0 / Wc5GHz,
            gAtFloor[2] / (Wc5GHz * 5.0), gAtFloor[3] * 5.0 / Wc5GHz,
        ];
        Assert.Equal(4, r.Network.Elements.Count);
        for (int i = 0; i < 4; i++)
            Assert.Equal(want[i], r.Network.Elements[i].Value, 1e-4 * want[i]);

        // Shunt C, series L, shunt C, series L — the high-impedance port takes the shunt capacitor.
        Assert.True(r.Network.Elements[0] is { IsShunt: true, Type: ElementType.C });
        Assert.True(r.Network.Elements[1] is { IsShunt: false, Type: ElementType.L });
        Assert.True(r.Network.Elements[3] is { IsShunt: false, Type: ElementType.L });
    }

    [Fact]
    public void SteppingUp_PutsTheSeriesInductorOnTheLowImpedancePort()
    {
        var d = Design(NetworkForm.Lowpass, 2,
            Termination.Resistive(5.0), Termination.Resistive(50.0));
        var r = MatchSynthesis.Synthesize(d);

        Assert.True(r.Ok, r.Refusal?.Message);
        Assert.Equal(50.0, r.RFarSynthesised, 1e-7);
        Assert.True(r.Network!.Elements[0] is { IsShunt: false, Type: ElementType.L });
        Assert.True(r.Network.Elements[^1] is { IsShunt: true, Type: ElementType.C });

        double worst = MatchAbcdOracle.WorstS11Db(r.Network, d.F1, d.F2, 401);
        Assert.Equal(-10.511, worst, 0.05);
    }

    // ── 2. The closed-form oracle — match.md §16.2's table ──

    [Theory]
    [InlineData(0.33, 2, 2.0)]
    [InlineData(0.33, 2, 10.0)]
    [InlineData(0.33, 3, 2.0)]
    [InlineData(0.33, 3, 10.0)]
    [InlineData(0.50, 2, 2.0)]
    [InlineData(0.50, 2, 10.0)]
    [InlineData(0.50, 3, 2.0)]
    [InlineData(0.50, 3, 10.0)]
    [InlineData(0.66, 2, 2.0)]
    [InlineData(0.66, 2, 10.0)]
    [InlineData(0.66, 3, 2.0)]
    [InlineData(0.66, 3, 10.0)]
    public void RealToReal_WorstReturnLoss_IsTheClosedForm(double a, int n, double ratio)
    {
        double f2 = 5e9, f1 = a * f2, rAna = 10.0;
        var d = Design(NetworkForm.Lowpass, n,
            Termination.Resistive(rAna), Termination.Resistive(rAna * ratio), f1, f2);
        var r = MatchSynthesis.Synthesize(d);
        Assert.True(r.Ok, r.Refusal?.Message);
        Assert.Equal(2 * n, r.Network!.Elements.Count);

        double measured = MatchAbcdOracle.WorstS11Db(r.Network, f1, f2, 801);
        Assert.Equal(MatchFormPrototype.BestReturnLossDb(n, a, ratio), measured, 0.05);
    }

    // ── 3. From DC, the family IS the textbook Chebyshev lowpass of order 2n ──

    [Fact]
    public void FromDc_ReducesToTheClassicalRipplePrototype()
    {
        // 0.1 dB, four elements: r = coth^2(B/4) is exactly what an even-order equal-ripple
        // prototype transforms, so the pin lands on the table's own terminating value.
        double[] ripple = MatchSynthesis.RippleG(4, 0.1);
        double r = ripple[5];

        double[] g = MatchFormPrototype.Gvalues(
            ResponseShape.ChebyshevFano, 2, 0.0, r, MatchFormPrototype.KFloor)!;

        for (int i = 1; i <= 5; i++)
            Assert.Equal(ripple[i], g[i - 1], 1e-5 * ripple[i]);
    }

    // ── 4. Absorption — Goldens B, C and D ──

    [Fact]
    public void GoldenB_AbsorbsTheTerminationExactly_AndAddsNothing()
    {
        // 5 ohm || 25 pF into 0.5 ohm: g1_actual = 3.92699, which the family reaches at K = 0.086588.
        var d = Design(NetworkForm.Lowpass, 2,
            new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 25e-12),
            Termination.Resistive(0.5));
        var basis = MatchSynthesis.Synthesize(d);
        Assert.True(basis.Ok, basis.Refusal?.Message);

        double[] expected = [3.926991, 0.462564, 8.892688, 0.169822, 10.0];
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], basis.G[i + 1], 1e-4 * Math.Max(1.0, expected[i]));

        var net = MatchSynthesis.WithEndSplits(basis.Network!, basis, d);
        Assert.Equal(4, net.Elements.Count);                       // nothing added
        Assert.DoesNotContain(net.Elements, e => e.IsExcess);
        Assert.Equal(1, net.Elements[0].AbsorbedEnd);
        Assert.Equal(25e-12, net.Elements[0].Value, 25e-12 * 1e-6);
        Assert.Equal(-8.010, MatchAbcdOracle.WorstS11Db(net, d.F1, d.F2, 801), 0.05);
    }

    [Fact]
    public void GoldenC_ASmallTermination_LeavesTheSurplusAsAnExcessElement()
    {
        var d = Design(NetworkForm.Lowpass, 2,
            new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 3e-12),
            Termination.Resistive(0.5));
        var basis = MatchSynthesis.Synthesize(d);
        Assert.True(basis.Ok, basis.Refusal?.Message);

        // Nothing binds, so K stays on its floor and the arm is the best-return-loss one.
        double[] atFloor = MatchFormPrototype.Gvalues(
            ResponseShape.ChebyshevFano, 2, 0.5, 0.1, MatchFormPrototype.KFloor)!;
        Assert.Equal(atFloor[0], basis.G[1], 1e-9 * atFloor[0]);

        var net = MatchSynthesis.WithEndSplits(basis.Network!, basis, d);
        Assert.Equal(5, net.Elements.Count);
        var excess = Assert.Single(net.Elements, e => e.IsExcess);
        Assert.Equal("CExcess1", excess.Name);
        Assert.False(excess.IsDetune);                              // match.md §16.4 item 3
        Assert.Equal(ElementType.C, excess.Type);
        Assert.True(excess.IsShunt);

        double whole = atFloor[0] / (Wc5GHz * 5.0);
        Assert.Equal(3e-12, net.Elements.First(e => e.AbsorbedEnd == 1).Value, 1e-24);
        Assert.Equal(whole - 3e-12, excess.Value, 1e-4 * (whole - 3e-12));
        Assert.True(basis.NeedsExcessElement);
    }

    [Fact]
    public void GoldenD_ATerminationBeyondTheFamily_IsRefusedWithBothNumbers()
    {
        var d = Design(NetworkForm.Lowpass, 2,
            new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 80e-12),
            Termination.Resistive(0.5));
        var basis = MatchSynthesis.Synthesize(d);

        Assert.False(basis.Ok);
        Assert.Equal(MatchRefusalKind.AnalysisEndNotAbsorbable, basis.Refusal!.Kind);
        Assert.Equal(Wc5GHz * 5.0 * 80e-12, basis.Refusal.Numbers["gActual"], 1e-6);
        Assert.True(basis.Refusal.Numbers["gMax"] < basis.Refusal.Numbers["gActual"]);
        Assert.Contains("12.566", basis.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFarEndBeyondTheFamily_IsRefusedAsTheFarEnd()
    {
        // Mixed pair, both reactive: the near end binds K from below and the far end from above, and
        // this far-end inductance is bigger than what is left once the near end is met.
        var d = Design(NetworkForm.Lowpass, 2,
            new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 4e-12),
            new Termination(5.0, ReactanceKind.L, TerminationTopology.Series, 4e-9));
        var basis = MatchSynthesis.Synthesize(d);

        Assert.False(basis.Ok);
        Assert.Equal(MatchRefusalKind.FarEndNotAbsorbable, basis.Refusal!.Kind);
        Assert.Equal(2, basis.Refusal.End);
        Assert.True(basis.Refusal.Numbers["gActual"] > basis.Refusal.Numbers["gFar"]);
        Assert.Contains("gNear", basis.Refusal.Numbers.Keys);
    }

    // ── 5. Form versus kind ──

    [Fact]
    public void Lowpass_CannotAbsorbAParallelInductance()
    {
        var d = Design(NetworkForm.Lowpass, 2,
            new Termination(50.0, ReactanceKind.L, TerminationTopology.Parallel, 1e-9),
            Termination.Resistive(5.0));
        var basis = MatchSynthesis.Synthesize(d);

        Assert.False(basis.Ok);
        Assert.Equal(MatchRefusalKind.FormCannotAbsorb, basis.Refusal!.Kind);
        Assert.Equal(1, basis.Refusal.End);
        Assert.Contains("highpass", basis.Refusal.Message, StringComparison.Ordinal);
        Assert.Contains("bandpass", basis.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Highpass_CannotAbsorbAParallelCapacitance()
    {
        var d = Design(NetworkForm.Highpass, 2,
            new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 1e-12),
            Termination.Resistive(5.0));
        var basis = MatchSynthesis.Synthesize(d);

        Assert.False(basis.Ok);
        Assert.Equal(MatchRefusalKind.FormCannotAbsorb, basis.Refusal!.Kind);
        Assert.Contains("lowpass", basis.Refusal.Message, StringComparison.Ordinal);
        Assert.Contains("bandpass", basis.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsorbableKindOnTheWrongEnd_IsRefusedForTheRatio_NotForTheKind()
    {
        // A parallel C IS a lowpass kind, but it sits on the LOW-impedance side of a step-up, where
        // the ladder puts its series inductor. Nothing about the form's repertoire is wrong; the
        // ratio is. Only bandpass form takes this.
        var d = Design(NetworkForm.Lowpass, 2,
            new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 2e-12),
            Termination.Resistive(50.0));
        var basis = MatchSynthesis.Synthesize(d);

        Assert.False(basis.Ok);
        Assert.Equal(MatchRefusalKind.FormCannotAbsorb, basis.Refusal!.Kind);
        Assert.Contains("LOW-impedance", basis.Refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Use highpass", basis.Refusal.Message, StringComparison.Ordinal);
    }

    // ── 6. A like-topology pair now HAS an order in these forms (MN-MB2) ──

    /// <summary>
    /// <b>The like-pair gap of match.md §16.4 item 2 is closed</b>: every order serves a like pair
    /// too, through §18.5's weighted family, and the element count is <c>2n + 1</c> instead of
    /// <c>2n</c>.
    /// </summary>
    /// <remarks>
    /// Which end must be the analysis end is decided by the ratio and by the pair's own topology —
    /// an odd ladder's two ends share one orientation, and a shunt-ended one steps DOWN — so a
    /// parallel pair is analysed from the HIGHER resistance. The other way round is a refusal that
    /// names the remedy, which the test below it checks.
    /// </remarks>
    [Fact]
    public void ALikeTopologyPair_TakesTheOddElementCountInLowpassAndHighpassForm()
    {
        var t1 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 0.4e-12);
        var t2 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 5e-12);

        Assert.Equal([2, 3, 4, 5, 6], MatchOrders.ValidOrders(t1, t2, NetworkForm.Lowpass));
        Assert.Equal([2, 3, 4, 5, 6], MatchOrders.ValidOrders(t1, t2, NetworkForm.Highpass));
        Assert.True(MatchOrders.NeedsOddCount(t1, t2));

        // The bandpass rule is unchanged: there, order IS the arm count and parity is the order's.
        Assert.Equal([3, 5], MatchOrders.ValidOrders(t1, t2, NetworkForm.Bandpass));
        Assert.Equal([3, 5], MatchOrders.ValidOrders(t1, t2));

        var basis = MatchSynthesis.Synthesize(Design(NetworkForm.Lowpass, 3, t1, t2));
        Assert.True(basis.Ok, basis.Refusal?.Message);
        Assert.Equal(7, basis.Network!.Elements.Count);

        // Both ends absorbed, both shunt — which is the whole point of the odd count.
        var ends = basis.Network!.Elements.Where(e => e.AbsorbedEnd != 0).ToList();
        Assert.Equal(2, ends.Count);
        Assert.All(ends, e => Assert.True(e.IsShunt));
        Assert.All(ends, e => Assert.Equal(ElementType.C, e.Type));
    }

    /// <summary>
    /// A like pair analysed from the wrong end is refused with the remedy named — <b>the analysis
    /// end</b>, not the form, because an odd ladder's ends flip together.
    /// </summary>
    [Fact]
    public void ALikeParallelPair_AnalysedFromTheLowerResistance_IsRefusedNamingTheOtherEnd()
    {
        var t1 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 5e-12);
        var t2 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 0.4e-12);

        var basis = MatchSynthesis.Synthesize(Design(NetworkForm.Lowpass, 3, t1, t2));
        Assert.False(basis.Ok);
        Assert.Equal(MatchRefusalKind.FormCannotAbsorb, basis.Refusal!.Kind);
        Assert.Contains("BOTH ends", basis.Refusal.Message, StringComparison.Ordinal);
        Assert.Contains("HIGHER resistance", basis.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMixedPair_KeepsEveryOrderInLowpassAndHighpassForm()
    {
        var t1 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 1e-12);
        var t2 = new Termination(5.0, ReactanceKind.L, TerminationTopology.Series, 1e-9);

        Assert.Equal([2, 3, 4, 5, 6], MatchOrders.ValidOrders(t1, t2, NetworkForm.Lowpass));
        Assert.Equal([2, 4, 6], MatchOrders.ValidOrders(t1, t2, NetworkForm.Bandpass));
    }

    // ── 7. No Norton pairs, and nothing to reach ──

    [Theory]
    [InlineData(NetworkForm.Lowpass, 2)]
    [InlineData(NetworkForm.Lowpass, 3)]
    [InlineData(NetworkForm.Lowpass, 4)]
    [InlineData(NetworkForm.Lowpass, 5)]
    [InlineData(NetworkForm.Lowpass, 6)]
    [InlineData(NetworkForm.Highpass, 2)]
    [InlineData(NetworkForm.Highpass, 4)]
    [InlineData(NetworkForm.Highpass, 6)]
    public void ASingleElementLadder_HasNoNortonPairsAndNeedsNone(NetworkForm form, int order)
    {
        var d = Design(form, order,
            new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 0.4e-12),
            Termination.Resistive(5.0));
        if (form == NetworkForm.Highpass)
            d.Term1 = new Termination(50.0, ReactanceKind.L, TerminationTopology.Parallel, 4e-9);

        var basis = MatchSynthesis.Synthesize(d);
        Assert.True(basis.Ok, basis.Refusal?.Message);

        // The UNCHANGED scan, on both the basis and the finished ladder.
        Assert.Empty(NortonTransform.Discover(basis.Network!));
        Assert.Empty(NortonTransform.Discover(MatchSynthesis.WithEndSplits(basis.Network!, basis, d)));
        Assert.Equal(1.0, basis.RequiredTransformRatio, 1e-9);

        var set = MatchSolutionSearch.Search(d);
        Assert.Null(set.Refusal);
        var only = Assert.Single(set.Solutions);
        Assert.Empty(only.Transforms);
    }

    // ── 8. Highpass is the dual ──

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Highpass_IsTheLowpassDual_SameGVectorSameReturnLoss(int order)
    {
        var lp = Design(NetworkForm.Lowpass, order,
            Termination.Resistive(10.0), Termination.Resistive(40.0));
        var hp = Design(NetworkForm.Highpass, order,
            Termination.Resistive(10.0), Termination.Resistive(40.0));

        var rl = MatchSynthesis.Synthesize(lp);
        var rh = MatchSynthesis.Synthesize(hp);
        Assert.True(rl.Ok && rh.Ok);
        Assert.Equal(rl.G, rh.G);

        // L becomes C and C becomes L, in the same positions and the same orientations.
        for (int i = 0; i < rl.Network!.Elements.Count; i++)
        {
            var a = rl.Network.Elements[i];
            var b = rh.Network!.Elements[i];
            Assert.Equal(a.IsShunt, b.IsShunt);
            Assert.NotEqual(a.Type, b.Type);
        }

        double wl = MatchAbcdOracle.WorstS11Db(rl.Network, lp.F1, lp.F2, 801);
        double wh = MatchAbcdOracle.WorstS11Db(rh.Network!, hp.F1, hp.F2, 801);
        Assert.Equal(wl, wh, 1e-6);
    }

    // ── 9. A pre-rev-2 payload has no Form and rebuilds identically ──

    [Fact]
    public void APayloadWithoutForm_DecodesAsBandpassAndRebuildsTheSameLadder()
    {
        const string json = """
        {
          "Version": 1, "F1": 1800000000.0, "F2": 2200000000.0, "Order": 4,
          "Response": "ChebyshevFano", "RippleDb": 0.1,
          "Term1": { "R": 50.0, "Kind": "None", "Topology": "Parallel", "Value": 0.0 },
          "Term2": { "R": 10.0, "Kind": "C", "Topology": "Parallel", "Value": 1e-12 },
          "AnalysisEnd": "Highest", "QAdjust": 0.0, "PlotBandFraction": 0.1, "PlotPoints": 401
        }
        """;
        Assert.True(MatchEmbedding.TryDecode(json, out var decoded));
        Assert.Equal(NetworkForm.Bandpass, decoded!.Form);

        var explicitBandpass = decoded.Clone();
        explicitBandpass.Form = NetworkForm.Bandpass;
        var a = MatchSynthesis.Synthesize(decoded);
        var b = MatchSynthesis.Synthesize(explicitBandpass);
        Assert.True(a.Ok);
        Assert.Equal(a.BasisFingerprint, b.BasisFingerprint);
    }

    [Fact]
    public void Form_SurvivesACloneAndARoundTrip()
    {
        var d = Design(NetworkForm.Highpass, 3,
            Termination.Resistive(50.0), Termination.Resistive(10.0));
        Assert.Equal(NetworkForm.Highpass, d.Clone().Form);

        Assert.True(MatchEmbedding.TryDecode(MatchEmbedding.Encode(d), out var back));
        Assert.Equal(NetworkForm.Highpass, back!.Form);
    }

    // ── 10. The K = 0 trap ──

    [Fact]
    public void KEqualsZeroExactly_DoesNotExtract_AndTheFloorDoes()
    {
        Assert.Null(MatchFormPrototype.Gvalues(ResponseShape.ChebyshevFano, 2, 0.5, 10.0, 0.0));
        Assert.NotNull(MatchFormPrototype.Gvalues(ResponseShape.ChebyshevFano, 2, 0.5, 10.0, 1e-6));
        Assert.NotNull(MatchFormPrototype.Gvalues(
            ResponseShape.ChebyshevFano, 2, 0.5, 10.0, MatchFormPrototype.KFloor));
    }

    // ── Coverage the brief's own gates imply but do not enumerate ──

    [Fact]
    public void EveryOrderAndBothFamilies_Extract_AcrossBandwidthsAndRatios()
    {
        // The sweep that decided the algorithm: the polynomial route of MatchPrototypes fails 144 of
        // these; this one fails none. Cheap (a closed-form root set and a 2n-step continued fraction).
        foreach (int n in new[] { 2, 3, 4, 5, 6 })
            foreach (double a in new[] { 0.0, 0.2, 0.33, 0.5, 0.66, 0.8 })
                foreach (double r in new[] { 0.1, 0.5, 0.9, 2.0, 10.0, 50.0 })
                    foreach (var shape in new[] { ResponseShape.ChebyshevFano, ResponseShape.Butterworth })
                    {
                        double[]? g = MatchFormPrototype.Gvalues(shape, n, a, r, MatchFormPrototype.KFloor);
                        Assert.True(g is not null, $"n={n} a={a} r={r} {shape}");
                        Assert.All(g!, v => Assert.True(v > 0.0));
                        Assert.Equal(Math.Max(r, 1.0 / r), g[2 * n], 1e-6 * Math.Max(r, 1.0 / r));
                    }
    }

    [Theory]
    [InlineData(0.5, 2, -6.82)]
    [InlineData(0.5, 3, -10.64)]
    [InlineData(0.66, 2, -13.36)]
    public void Butterworth_ReachesMatchMdSection162sNumbers(double a, int n, double expectedDb)
    {
        double f2 = 5e9, f1 = a * f2;
        var d = Design(NetworkForm.Lowpass, n,
            Termination.Resistive(5.0), Termination.Resistive(50.0), f1, f2,
            ResponseShape.Butterworth);
        var r = MatchSynthesis.Synthesize(d);
        Assert.True(r.Ok, r.Refusal?.Message);
        Assert.Equal(expectedDb, MatchAbcdOracle.WorstS11Db(r.Network!, f1, f2, 801), 0.02);
    }

    [Theory]
    [InlineData(ResponseShape.Bessel)]
    [InlineData(ResponseShape.ChebyshevTwoEnded)]
    public void BesselAndDoubleMatchChebyshev_AreRefusedInTheseForms(ResponseShape shape)
    {
        var d = Design(NetworkForm.Lowpass, 2,
            Termination.Resistive(5.0), Termination.Resistive(50.0), response: shape);
        var basis = MatchSynthesis.Synthesize(d);

        Assert.False(basis.Ok);
        Assert.Equal(MatchRefusalKind.ResponseInfeasible, basis.Refusal!.Kind);
        Assert.Contains("ONE free parameter", basis.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RippleDbAndQAdjust_AreIgnoredRatherThanRefused()
    {
        var baseline = Design(NetworkForm.Lowpass, 2,
            Termination.Resistive(5.0), Termination.Resistive(50.0));
        var noisy = baseline.Clone();
        noisy.RippleDb = 3.0;
        noisy.QAdjust = 7.5;

        var a = MatchSynthesis.Synthesize(baseline);
        var b = MatchSynthesis.Synthesize(noisy);
        Assert.True(a.Ok && b.Ok);
        Assert.Equal(a.BasisFingerprint, b.BasisFingerprint);
    }

    [Fact]
    public void ALowpassMayRunFromDc_WhichABandpassDesignMayNot()
    {
        var d = Design(NetworkForm.Lowpass, 2,
            Termination.Resistive(50.0), Termination.Resistive(50.0 / 1.355382532984766),
            f1: 0.0, f2: 5e9);
        var r = MatchSynthesis.Synthesize(d);
        Assert.True(r.Ok, r.Refusal?.Message);

        var bandpass = d.Clone();
        bandpass.Form = NetworkForm.Bandpass;
        Assert.False(MatchSynthesis.Synthesize(bandpass).Ok);

        var highpass = d.Clone();
        highpass.Form = NetworkForm.Highpass;
        Assert.Equal(MatchRefusalKind.InvalidTermination,
                     MatchSynthesis.Synthesize(highpass).Refusal!.Kind);
    }

    [Fact]
    public void EqualPortResistances_AreTheDegenerateCase_AndSayThemselves()
    {
        var d = Design(NetworkForm.Lowpass, 2,
            Termination.Resistive(50.0), Termination.Resistive(50.0));
        var r = MatchSynthesis.Synthesize(d);

        Assert.True(r.Ok, r.Refusal?.Message);
        Assert.Contains(r.Notes, s => s.Contains("no DC pin", StringComparison.Ordinal));
        Assert.True(MatchAbcdOracle.WorstS11Db(r.Network!, d.F1, d.F2, 401) < -39.0);
    }
}
