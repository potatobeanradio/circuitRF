// ================================================================
//  MatchDcBlockTests.cs — MN-DCB and MN-DCB2, the DC block on a termination's first shunt inductor
//  (match.md §22).
//
//  MN-DCB's claims, in the brief's own order:
//
//    1. The compensation is EXACT at ω₀ — the branch's reactance there is what the synthesis asked
//       for, on every golden ladder with a shunt end arm.
//    2. What a block costs across the band, measured rather than assumed.
//    3. The response evaluator agrees with an independently-written ABCD cascade that carries the
//       branch as an explicit series L-C to ground.
//    6. The block re-attaches by NODE, so a Norton π that replaces L1 by a product does not lose it.
//    7. An end with no shunt inductor on its DC path STORES the value and applies nothing, saying why.
//    8. Both values round-trip, and a payload written before rev 6 decodes to no block at all.
//
//  MN-DCB2's — the block follows the DC PATH, not the end node (§22.1 corrected):
//
//    P1. A series-RC end (a FET input) hosts the block on the shunt inductor one series inductor in.
//    P2. A Norton T on the end pair moves the host one series product in, and the block follows.
//    P4. A REAL series capacitor (CFano / CDetune) isolates the end; the block is withheld, naming it.
//    P5. A highpass series-C end is absorbed and transparent; the host is the inductor behind it.
//    P8. Two series ends host two distinct interior blocks — or collide on one, reported once.
//    P9. No node in any ladder the rebuild produces carries two real shunt inductors — verified over
//        every golden fixture × {none, π, T} × {no split, Fano, detune}, not restated.
//
//  Tests 4, 5 and P7 (flatten ⇔ stamp ⇔ response, and the DC open) need the engine and live in
//  tests/Engine.Tests/Match/MatchFlattenPlanTests.cs beside the equivalence gate they extend.
// ================================================================

using System.Globalization;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Matching;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Match;

public class MatchDcBlockTests(ITestOutputHelper output)
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    /// <summary>match.md §4.9's interstage problem — Term1's end arm is shunt, Term2's is series.</summary>
    private static MatchDesign Golden() => new()
    {
        F1 = 3.3e9,
        F2 = 5.0e9,
        Order = 4,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(200.0, ReactanceKind.C, TerminationTopology.Parallel, 0.125e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 10e-12),
    };

    /// <summary>match.md §16.6's highpass dual — series capacitors through, shunt inductors down.</summary>
    private static MatchDesign Highpass() => new()
    {
        F1 = 3.3e9,
        F2 = 5.0e9,
        Order = 4,
        Form = NetworkForm.Highpass,
        Term1 = Termination.Resistive(50.0),
        Term2 = Termination.Resistive(5.0),
    };

    /// <summary>match.md §18.4's shunt-first dual-band member — 8 elements, two resonated arms.</summary>
    private static MatchDesign DualBand() => new()
    {
        F1 = 1.8e9, F2 = 2.0e9, BandCount = 2, F3 = 2.4e9, F4 = 2.6667e9,
        Order = 2,
        Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(4.0, ReactanceKind.C, TerminationTopology.Parallel, 10e-12),
        Term2 = Termination.Resistive(50.0),
    };

    private static MatchNetwork Ladder(MatchDesign design)
    {
        var r = MatchRebuild.Rebuild(design);
        Assert.Null(r.Refusal);
        return r.Network!;
    }

    /// <summary>The branch's reactance at one frequency: ωL for a bare inductor, ωL′ − 1/(ωC) with a block.</summary>
    private static double BranchReactance(MatchElement e, double omega) =>
        omega * e.Value - (e.DcBlock > 0 ? 1.0 / (omega * e.DcBlock) : 0.0);

    // ── 1. The compensation is exact at ω₀ ────────────────────────────────────

    /// <summary>
    /// <b>The whole point of enlarging the inductor.</b> A block turns the branch's <c>jωL</c> into
    /// <c>j(ωL′ − 1/ωC)</c>, and <c>L′ = L + 1/(ω₀²C)</c> is exactly the value that leaves the
    /// bracket equal to <c>ω₀L</c> at band centre. Below that identity nothing else in this feature
    /// is worth having: it is what makes the block invisible to the synthesis, which chose L on the
    /// strength of that reactance and nothing else.
    /// </summary>
    [Theory]
    [InlineData("bandpass")]
    [InlineData("highpass")]
    [InlineData("dual-band")]
    public void TheCompensatedBranch_HasExactlyTheSynthesisedReactance_AtBandCentre(string which)
    {
        var design = which switch
        {
            "highpass"  => Highpass(),
            "dual-band" => DualBand(),
            _           => Golden(),
        };

        var free = Ladder(design);
        int idx = MatchDcBlock.ResolveHost(free, 1).Index;
        Assert.True(idx >= 0, $"{which}: termination 1's end arm should be a shunt inductor.");

        double om0 = design.Omega0;
        double l = free.Elements[idx].Value;
        double wanted = om0 * l;

        var blocked = design.Clone();
        blocked.Term1DcBlock = MatchDcBlock.DefaultFor(l, om0, MatchDesignerDefaultCap);
        var net = Ladder(blocked);

        var branch = net.Elements[idx];
        Assert.True(branch.DcBlock > 0);
        Assert.True(branch.Value > l, "the compensated inductor must be LARGER than the synthesised one");

        double got = BranchReactance(branch, om0);
        Assert.True(Math.Abs(got / wanted - 1.0) <= 1e-12,
            $"{which}: branch reactance {got} vs {wanted}");

        output.WriteLine($"{which}: L {l * 1e12:0.####} pH -> L' {branch.Value * 1e12:0.####} pH "
                         + $"with {branch.DcBlock * 1e12:0.###} pF; X(ω₀) {got:0.#########} vs {wanted:0.#########}");
    }

    /// <summary>The Designer's shipped seed cap, quoted here so Core.Tests need not see the Ui.</summary>
    private const double MatchDesignerDefaultCap = 10e-9;

    // ── 2. What a block costs, measured ───────────────────────────────────────

    /// <summary>
    /// <b>The numbers, not a tolerance pulled from the air.</b> The brief expected the default block
    /// to cost under 0.05 dB on §4.9's ladder; it costs <b>0.169 dB</b>, and the reason is the
    /// fixture: §22.2's measurements are on a 20 %-bandwidth drain network and §4.9's band is
    /// 3.3–5.0 GHz, a 42 % fractional bandwidth, where the second-order residual has twice as far to
    /// run. The assertion is written to what a default block actually costs on a band this wide, with
    /// the measured figures printed; see <c>src/Core/Match/RESOLVED.md</c> §MN-DCB.
    ///
    /// <para>The other two rows are the ones that matter for the warning: a block ten times too small
    /// is a real cost the status line must state, and it is still a working network rather than a
    /// refusal.</para>
    /// </summary>
    [Fact]
    public void ABlockCostsSecondOrderReturnLoss_AndTheCostIsReported()
    {
        var design = Golden();
        var free = Ladder(design);
        int idx = MatchDcBlock.ResolveHost(free, 1).Index;
        double l = free.Elements[idx].Value;
        double om0 = design.Omega0, f0 = om0 / (2.0 * Math.PI);
        double rlFree = -MatchAbcdOracle.WorstS11Db(free, design.F1, design.F2);

        (double Rl, DcBlockNote Note) Run(double ratio)
        {
            var d = design.Clone();
            d.Term1DcBlock = ratio / (om0 * om0 * l);
            var r = MatchRebuild.Rebuild(d);
            return (-MatchAbcdOracle.WorstS11Db(r.Network!, d.F1, d.F2), r.DcBlocks[0]);
        }

        var (rlDefault, nDefault) = Run(100.0);   // the default: f_s ≈ f₀/10
        var (rlFifth, nFifth)     = Run(25.0);    // ten times too small
        var (rlHalf, nHalf)       = Run(4.0);     // absurdly small

        output.WriteLine($"block-free worst RL {rlFree:0.####} dB (L {l * 1e12:0.###} pH, f₀ {f0 / 1e9:0.####} GHz)");
        foreach (var (tag, rl, n) in new[]
                 {
                     ("default", rlDefault, nDefault), ("k=25", rlFifth, nFifth), ("k=4", rlHalf, nHalf),
                 })
            output.WriteLine($"  {tag,-8} C {n.Farads * 1e12:0.###} pF  L' {n.InductanceAfter * 1e12:0.###} pH  "
                             + $"f_s {n.SeriesResonanceHz / 1e6:0.#} MHz (f₀/{f0 / n.SeriesResonanceHz:0.##})  "
                             + $"spread ±{n.BandSpread * 100:0.###} %  warn {n.Warn}  "
                             + $"RL {rl:0.####} dB  (Δ {rl - rlFree:+0.####;-0.####})");

        // The default: a fraction of a dB on a 42 % band, and no warning.
        Assert.InRange(rlFree - rlDefault, 0.0, 0.25);
        Assert.False(nDefault.Warn);
        Assert.InRange(nDefault.BandSpread, 0.003, 0.006);

        // ── The warn threshold is on the COMPENSATED branch's own resonance ──
        //
        // The brief expected C = 25/(ω₀²L) to fire the warning "f_s = f₀/5". It does not, and the
        // arithmetic is the reason rather than the code: the compensation enlarges L to L·(1 + 1/k),
        // so f_s = 1/(2π√(L'C)) = f₀/√(k+1), and k = 25 lands at f₀/5.099 — just inside. Quoting the
        // branch's REAL resonance is what the status line has to do, so the threshold stays where it
        // is and the expectation moves. k = 4 gives f₀/√5 = f₀/2.24 and warns.
        Assert.Equal(f0 / Math.Sqrt(26.0), nFifth.SeriesResonanceHz, 3);
        Assert.False(nFifth.Warn);
        Assert.InRange(rlFree - rlFifth, 0.25, 1.5);

        Assert.Equal(f0 / Math.Sqrt(5.0), nHalf.SeriesResonanceHz, 3);
        Assert.True(nHalf.Warn);
        Assert.True(rlFree - rlHalf > 1.0,
            $"a block at f₀/2.24 should cost more than a dB; it cost {rlFree - rlHalf:0.###}");
    }

    /// <summary>
    /// <b>§22.2's own table, reproduced from the formulas.</b> Every number in it comes out — L′, f_s
    /// and the three L_eff values — except the parenthesised spread, which the section quotes as the
    /// second-order ESTIMATE <c>±2(f_s/f₀)²(Δf_half/f₀)</c> and not as the half-range of the L_eff
    /// values printed beside it. The two differ by about 12 % because the 1/ω² term is asymmetric
    /// about ω₀. Both are computed here; the section has been corrected in place.
    /// </summary>
    [Fact]
    public void Section22_2sTable_ReproducesExceptForTheQuotedSpread()
    {
        double f1 = 1.8e9, f2 = 2.2e9;
        double om0 = 2.0 * Math.PI * Math.Sqrt(f1 * f2);
        double f0 = om0 / (2.0 * Math.PI);
        const double l = 99.5e-12;

        double Eff(double lp, double c, double hz)
        {
            double om = 2.0 * Math.PI * hz;
            return lp - 1.0 / (om * om * c);
        }

        foreach (var (c, lpPh, fsMhz) in new[]
                 {
                     (500e-12, 112.3, 672.0), (1e-9, 105.9, 490.0), (10e-9, 100.1, 159.0),
                 })
        {
            double lp = MatchDcBlock.Compensate(l, c, om0);
            double fs = MatchDcBlock.SeriesResonanceHz(lp, c);
            double half = MatchDcBlock.BandSpread(lp, c, om0, f1, f2);
            double estimate = 2.0 * Math.Pow(fs / f0, 2.0) * ((f2 - f1) / 2.0 / f0);

            output.WriteLine(
                $"{c * 1e12,7:0.#} pF  L' {lp * 1e12:0.####} pH (§22.2 {lpPh})  f_s {fs / 1e6:0.#} MHz (§22.2 {fsMhz})  "
                + $"L_eff {Eff(lp, c, 1.8e9) * 1e12:0.###}/{Eff(lp, c, 2.0e9) * 1e12:0.###}/{Eff(lp, c, 2.2e9) * 1e12:0.###} pH  "
                + $"half-range ±{half * 100:0.###} %  estimate ±{estimate * 100:0.###} %");

            Assert.True(Math.Abs(lp * 1e12 - lpPh) <= 0.05, $"L' {lp * 1e12:0.####} pH vs §22.2's {lpPh}");
            // f_s to the nearest MHz the section rounds to: 671.7 -> 672, 489.1 -> 490, 159.0 -> 159.
            Assert.True(Math.Abs(fs / 1e6 - fsMhz) <= 1.0, $"f_s {fs / 1e6:0.#} MHz vs §22.2's {fsMhz}");

            // The estimate always UNDERSTATES the half-range, by 4-6 % of itself, and never crosses it.
            Assert.True(estimate < half, "the second-order estimate should sit below the exact half-range");
            Assert.InRange(half / estimate, 1.01, 1.20);
        }

        // The three L_eff values §22.2 prints for the 500 pF row. Within a tenth of a pH — the section
        // truncates (96.657 is printed 96.6) rather than rounding, so a decimal-place comparison would
        // fail on the printing convention and not on the physics.
        double l500 = MatchDcBlock.Compensate(l, 500e-12, om0);
        Assert.True(Math.Abs(Eff(l500, 500e-12, 1.8e9) * 1e12 - 96.6) <= 0.1);
        Assert.True(Math.Abs(Eff(l500, 500e-12, 2.2e9) * 1e12 - 101.8) <= 0.1);

        // ...and the middle column, which the section labels "2.0 GHz" but which is L at ω₀ — the
        // frequency the compensation is exact at is √(1.8·2.2) = 1.98997 GHz, not 2.0.
        Assert.Equal(99.5, Eff(l500, 500e-12, f0) * 1e12, 6);
        Assert.Equal(99.628, Eff(l500, 500e-12, 2.0e9) * 1e12, 3);
    }

    // ── 3. The oracle ─────────────────────────────────────────────────────────

    /// <summary>
    /// <b><c>MatchResponse.At</c> against a cascade written from the two-port definitions.</b> The
    /// oracle builds the blocked branch as an explicit series L-C to ground — two impedances added,
    /// one shunt step — rather than reading <c>MatchElement.DcBlock</c> the way production does, so
    /// this is a check of the physics and not of one expression copied twice.
    /// </summary>
    [Fact]
    public void TheResponse_AgreesWithAnExplicitSeriesLcBranchToGround_At401Points()
    {
        var design = Golden();
        var free = Ladder(design);
        int idx = MatchDcBlock.ResolveHost(free, 1).Index;

        var d = design.Clone();
        d.Term1DcBlock = MatchDcBlock.DefaultFor(free.Elements[idx].Value, d.Omega0, MatchDesignerDefaultCap);
        var net = Ladder(d);

        double lo = design.F1 * 0.9, hi = design.F2 * 1.1;
        double worst = 0.0;
        for (int i = 0; i < 401; i++)
        {
            double f = lo + (hi - lo) * i / 400.0;
            var (s11, s21) = MatchResponse.At(net, f);
            var (o11, o21) = MatchAbcdOracle.S(net, f);
            worst = Math.Max(worst, Math.Max((s11 - o11).Magnitude, (s21 - o21).Magnitude));
        }

        output.WriteLine($"worst |Δ| over 401 points, {lo / 1e9:0.##}-{hi / 1e9:0.##} GHz: {worst:0.###e+0}");
        Assert.True(worst < 1e-12, $"worst difference {worst}");
    }

    /// <summary>
    /// At DC the blocked branch is an OPEN — zero admittance — which is the whole reason the block
    /// exists. Guarded rather than divided: <c>1/(j·0)</c> is not a number the cascade can carry.
    /// </summary>
    [Fact]
    public void AtZeroFrequency_TheBlockedBranchContributesNothing()
    {
        var e = new MatchElement
        {
            Name = "L1", Type = ElementType.L, IsShunt = true, Value = 100e-12, DcBlock = 1e-9,
        };
        var net = new MatchNetwork { R1 = 50, R2 = 50, Elements = { e } };

        var (s11, s21) = MatchResponse.At(net, 0.0);
        Assert.True(double.IsFinite(s11.Real) && double.IsFinite(s11.Imaginary));
        Assert.True(double.IsFinite(s21.Real) && double.IsFinite(s21.Imaginary));

        // A single shunt branch that is open at DC leaves the two ports connected by nothing but the
        // through path — which for a one-element ladder is a wire, so S21 = 1 and S11 = 0.
        Assert.Equal(Complex.Zero, s11);
        Assert.Equal(Complex.One, s21);
    }

    // ── 6. The block re-attaches by node, not by name ─────────────────────────

    /// <summary>
    /// <b>A Norton π on the first pair replaces <c>L1</c> with three products</b>, one of which is
    /// still a shunt inductor at <c>p1</c>. Resolving the block by NAME would lose it silently at
    /// that point; resolving it by node finds the product, and the response is the same network it
    /// was before the transform to within what the transform itself does.
    /// </summary>
    [Fact]
    public void ANortonTransformOnTheFirstPair_DoesNotLoseTheBlock()
    {
        // §4.9's own ladder offers no pair that NAMES L1 — its CFano and its absorbed C1 both hang off
        // p1 between L1 and L2, and a Norton pair is two like-type elements in adjacent arms. The
        // drain network of §22.2 does, which is the case the rule was written for anyway: a π on
        // (L1, L2) replaces L1 with three products, the first of which is still a shunt inductor at
        // p1 and is where the block has to end up.
        var design = new MatchDesign
        {
            F1 = 1.8e9, F2 = 2.2e9, Order = 4, Response = ResponseShape.ChebyshevFano,
            Term1 = new Termination(4.0, ReactanceKind.C, TerminationTopology.Parallel, 30e-12),
            Term2 = Termination.Resistive(50.0),
        };
        var basis = Ladder(design);
        int idx = MatchDcBlock.ResolveHost(basis, 1).Index;
        double c = MatchDcBlock.DefaultFor(basis.Elements[idx].Value, design.Omega0, MatchDesignerDefaultCap);

        // The pair scan is what the Designer's rack offers; take the first pair that names L1.
        var rebuiltBasis = MatchRebuild.Rebuild(design);
        var pair = NortonTransform.Discover(basis).FirstOrDefault(p => p.NameA == "L1" && p.NameB == "L2");
        Assert.NotNull(pair);
        var range = NortonTransform.Range(basis, pair!, rebuiltBasis.Basis.AnalysisIsTerm1, false);
        Assert.True(range.IsUsable);
        double n = 0.5 * (range.Min + range.Max);

        var before = design.Clone();
        before.Term1DcBlock = c;
        var withoutTransform = MatchRebuild.Rebuild(before);

        var after = before.Clone();
        after.Transforms = [new TransformRecord(pair.NameA, pair.NameB, TransformForm.Pi, n, false)];
        var withTransform = MatchRebuild.Rebuild(after);

        Assert.Null(withTransform.Refusal);

        // ── A π of inductors has TWO hosts at this end (owner, 2026-08-28) ────
        //
        // shunt L1_N1_1 / series L1_N1_2 / shunt L1_N1_3, then C2. The series product passes DC, so a
        // block on L1_N1_1 alone sends the bias straight through to L1_N1_3 — both are blocked, with
        // the one value, each compensated on its own inductance.
        Assert.Equal(2, withTransform.DcBlocks.Count);
        Assert.All(withTransform.DcBlocks, n => Assert.True(n.Applied, n.Reason));
        Assert.All(withTransform.DcBlocks, n => Assert.Equal(1, n.End));
        var note = withTransform.DcBlocks[0];
        Assert.Equal(MatchLadderNames.ProductPrefix("L1", 1) + "1", note.ElementName);
        Assert.Empty(note.Path);   // the π's first shunt product is still ON the end node
        var second = withTransform.DcBlocks[1];
        Assert.Equal(MatchLadderNames.ProductPrefix("L1", 1) + "3", second.ElementName);
        Assert.Equal([MatchLadderNames.ProductPrefix("L1", 1) + "2"], second.Path);
        Assert.Equal(2, withTransform.Network!.Elements.Count(e => e.DcBlock > 0));
        foreach (var n2 in withTransform.DcBlocks)
        {
            var el = withTransform.Network.Elements.Single(e => e.Name == n2.ElementName);
            Assert.Equal(c, el.DcBlock);
            Assert.True(Math.Abs(BranchReactance(el, design.Omega0) / (design.Omega0 * n2.InductanceBefore) - 1.0) <= 1e-12);
        }

        // ── The comparison is in LINEAR |S11|, not in dB ──────────────────────
        //
        // The brief asks for 0.05 dB, and on this fixture that is the wrong ruler: the network is
        // matched to −52 dB block-free, where |S11| is 0.0024 and a THIRD of a thousandth of a unit
        // moves the number by a whole dB. Measured here: block-free 0.002434 either side of the
        // transform (identical to 1e-12 — the transform is exactly response-preserving, which is the
        // sanity check below); with the default block, 0.003486 before the π and 0.003187 after it.
        // The block's second-order residual is simply computed against a differently-split inductor,
        // and 3e-4 of |S11| is what that is worth. See RESOLVED.md §MN-DCB.
        double freeA = Worst(MatchRebuild.Rebuild(design).Network!);
        var freeTransformed = design.Clone();
        freeTransformed.Transforms = after.Transforms;
        double freeB = Worst(MatchRebuild.Rebuild(freeTransformed).Network!);
        Assert.True(Math.Abs(freeA - freeB) < 1e-12,
            $"a Norton transform is response-preserving: {freeA} vs {freeB}");

        double a = Worst(withoutTransform.Network!);
        double b = Worst(withTransform.Network!);
        output.WriteLine($"π on {pair.NameA}/{pair.NameB} at N={n:0.####}: blocks on {note.ElementName} and {second.ElementName} (via {second.Path[0]}); "
                         + $"worst |S11| block-free {freeA:0.#######}, blocked {a:0.#######} -> {b:0.#######} "
                         + $"({-20 * Math.Log10(a):0.###} -> {-20 * Math.Log10(b):0.###} dB)");
        Assert.True(Math.Abs(a - b) <= 2.5e-3, $"{a} vs {b}");
        Assert.True(b < 5e-3, $"the transformed, blocked ladder is still matched: |S11| {b}");

        double Worst(MatchNetwork net) =>
            MatchAbcdOracle.Band(design.F1, design.F2, 401).Max(f => MatchAbcdOracle.S(net, f).S11.Magnitude);
    }

    /// <summary>The product-name rule, spelled once here so the test above does not hard-code it.</summary>
    private static class MatchLadderNames
    {
        internal static string ProductPrefix(string elementA, int ordinal) => $"{elementA}_N{ordinal}_";
    }

    // ── 7. The inactive case ──────────────────────────────────────────────────

    /// <summary>
    /// <b>Stored, not applied — and NOT a refusal.</b> Under MN-DCB this test held that §4.9's
    /// termination 2 could carry no block because its arm is a series arm. That was wrong (MN-DCB2):
    /// the arm's capacitor is the FET's own C_gs, absorbed and not on the board, and the ladder's
    /// L3 shorts the gate bias through L4. So the block now APPLIES there, and the stored-not-applied
    /// case is a real series capacitor — <see cref="ARealSeriesCapacitor_IsolatesTheEnd_AndTheBlockIsWithheld"/>.
    /// What this test keeps is the contract of the inactive note itself: the value stays on the
    /// design, the network is exactly the block-free one, and the note names the end and the reason.
    /// </summary>
    [Fact]
    public void AnEndWithNoHost_StoresTheValueAndAppliesNothing()
    {
        var design = Golden();
        design.Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 100e-12);
        design.Term2DcBlock = 1e-9;

        var rebuilt = MatchRebuild.Rebuild(design);
        Assert.Null(rebuilt.Refusal);

        var note = Assert.Single(rebuilt.DcBlocks);
        Assert.Equal(2, note.End);
        Assert.False(note.Applied);
        Assert.Equal(1e-9, note.Farads);
        Assert.Contains("termination 2", note.Reason, StringComparison.Ordinal);
        Assert.Contains("Stored, not applied", note.Reason, StringComparison.Ordinal);

        Assert.All(rebuilt.Network!.Elements, e => Assert.Equal(0.0, e.DcBlock));
        Assert.Equal(1e-9, design.Term2DcBlock);

        // The ladder is EXACTLY the block-free one — the same network, element for element.
        var free = design.Clone();
        free.Term2DcBlock = 0.0;
        var freeNet = Ladder(free);
        Assert.Equal(freeNet.Elements.Count, rebuilt.Network!.Elements.Count);
        for (int i = 0; i < freeNet.Elements.Count; i++)
            Assert.Equal(freeNet.Elements[i].Value, rebuilt.Network.Elements[i].Value, 15);

        output.WriteLine(note.Reason);
    }

    /// <summary>
    /// A LOWPASS ladder has no shunt inductor at all — it passes DC end to end — so neither end can
    /// carry one, and both say so. Blocking a lowpass through path needs a series capacitor, which is
    /// a highpass pole and a different compensation (match.md §22.1, deliberately out of scope).
    /// </summary>
    [Fact]
    public void ALowpassLadder_HasNowhereToPutABlockAtEitherEnd()
    {
        var design = new MatchDesign
        {
            F1 = 3.3e9, F2 = 5.0e9, Order = 3, Form = NetworkForm.Lowpass,
            // A like-topology pair takes an odd element count, whose two ends share one orientation,
            // so the synthesis has to be analysed from the HIGHER resistance — its own refusal says so.
            AnalysisEnd = AnalysisEndChoice.Term1,
            Term1 = new Termination(50.0, ReactanceKind.C, TerminationTopology.Parallel, 0.4e-12),
            Term2 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Parallel, 5e-12),
            Term1DcBlock = 1e-9,
            Term2DcBlock = 2e-9,
        };

        var rebuilt = MatchRebuild.Rebuild(design);
        Assert.Null(rebuilt.Refusal);
        Assert.DoesNotContain(rebuilt.Network!.Elements, e => e.IsShunt && e.Type == ElementType.L);
        Assert.Equal(2, rebuilt.DcBlocks.Count);
        Assert.All(rebuilt.DcBlocks, n => Assert.False(n.Applied));
        Assert.All(rebuilt.DcBlocks, n => Assert.Contains("lowpass ladder passes DC end to end", n.Reason, StringComparison.Ordinal));

        foreach (int end in new[] { 1, 2 })
        {
            var host = MatchDcBlock.ResolveHost(rebuilt.Network, end);
            Assert.Equal(-1, host.Index);
            Assert.Equal(DcBlockStop.EndOfLadder, host.Stop);
            Assert.Empty(host.Hosts);
            Assert.Equal("", host.StopElementName);
        }
    }

    /// <summary>
    /// The block never enters the synthesis, the transforms or either fingerprint — so setting one
    /// leaves <c>BasisFingerprint</c> and every element BUT the host inductor untouched, and the
    /// solutions list (which is fingerprinted on those) cannot move underneath it.
    /// </summary>
    [Fact]
    public void SettingABlock_ChangesNothingButTheHostInductor()
    {
        var free = MatchRebuild.Rebuild(Golden());
        var d = Golden();
        int idx = MatchDcBlock.ResolveHost(free.Network!, 1).Index;
        d.Term1DcBlock = MatchDcBlock.DefaultFor(free.Network!.Elements[idx].Value, d.Omega0, MatchDesignerDefaultCap);
        var blocked = MatchRebuild.Rebuild(d);

        Assert.Equal(free.Basis.BasisFingerprint, blocked.Basis.BasisFingerprint);
        Assert.Equal(free.Required, blocked.Required);
        Assert.Equal(free.Network!.Elements.Count, blocked.Network!.Elements.Count);

        for (int i = 0; i < free.Network.Elements.Count; i++)
        {
            var a = free.Network.Elements[i];
            var b = blocked.Network.Elements[i];
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.IsShunt, b.IsShunt);
            Assert.Equal(a.Type, b.Type);
            if (i == idx) continue;
            Assert.Equal(a.Value, b.Value, 15);
            Assert.Equal(0.0, b.DcBlock);
        }
    }

    // ── 8. Persistence ────────────────────────────────────────────────────────

    /// <summary>
    /// Both values round-trip through the payload, and <b>an MN-FH-era payload decodes with both at
    /// 0</b> — the fields are additive, so <c>Version</c> stays 1 and no existing design gains a
    /// block it never had.
    /// </summary>
    [Fact]
    public void BothBlocks_RoundTrip_AndAnOlderPayloadDecodesWithNone()
    {
        var design = Golden();
        design.Term1DcBlock = 3.81e-9;
        design.Term2DcBlock = 470e-12;

        Assert.True(MatchEmbedding.TryDecode(MatchEmbedding.EncodeToken(design), out var back));
        Assert.Equal(3.81e-9, back!.Term1DcBlock, 15);
        Assert.Equal(470e-12, back.Term2DcBlock, 15);
        Assert.Equal(1, back.Version);

        var clone = design.Clone();
        Assert.Equal(design.Term1DcBlock, clone.Term1DcBlock);
        Assert.Equal(design.Term2DcBlock, clone.Term2DcBlock);

        // ── An MN-FH-era payload: the two fields simply are not in it ─────────
        //
        // Written by renaming them out of the JSON rather than by hand-authoring one, so the fixture
        // cannot drift away from what the serializer actually emits for everything else.
        string json = MatchEmbedding.Write(Golden());
        Assert.Contains("\"Term1DcBlock\"", json, StringComparison.Ordinal);
        var older = MatchEmbedding.Read(
            json.Replace("\"Term1DcBlock\"", "\"IgnoredByAnOlderReader\"", StringComparison.Ordinal)
                .Replace("\"Term2DcBlock\"", "\"AlsoIgnored\"", StringComparison.Ordinal));
        Assert.Equal(0.0, older.Term1DcBlock);
        Assert.Equal(0.0, older.Term2DcBlock);
        Assert.Empty(MatchRebuild.Rebuild(older).DcBlocks);

        output.WriteLine(design.Term1DcBlock.ToString("0.####e0", CultureInfo.InvariantCulture));
    }
    // ══ MN-DCB2 — the block follows the DC path ═══════════════════════════════

    /// <summary>match.md §22's drain network — 4 Ω ‖ 30 pF into 50 Ω; the one golden ladder that offers a pair naming L1.</summary>
    private static MatchDesign Drain() => new()
    {
        F1 = 1.8e9, F2 = 2.2e9, Order = 4, Response = ResponseShape.ChebyshevFano,
        Term1 = new Termination(4.0, ReactanceKind.C, TerminationTopology.Parallel, 30e-12),
        Term2 = Termination.Resistive(50.0),
    };

    /// <summary>
    /// A bandpass ladder whose BOTH ends are series-RC terminations: order 5, so the arms run
    /// series / shunt / series / shunt / series and the two DC paths reach two different shunt
    /// inductors. Analysed from termination 1 so that neither end grows a Fano capacitor (which would
    /// isolate it — a different test).
    /// </summary>
    private static MatchDesign BothSeries(int order = 5) => new()
    {
        F1 = 3.3e9, F2 = 5.0e9, Order = order, Response = ResponseShape.ChebyshevFano,
        AnalysisEnd = AnalysisEndChoice.Term1,
        Term1 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Series, 10e-12),
        Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 30e-12),
    };

    /// <summary>A Norton transform on a named pair at the middle of its usable range.</summary>
    private static TransformRecord MidRange(MatchDesign design, string a, string b, TransformForm form)
    {
        var rebuilt = MatchRebuild.Rebuild(design);
        var pair = NortonTransform.Discover(rebuilt.Network!).FirstOrDefault(p => p.NameA == a && p.NameB == b);
        Assert.NotNull(pair);
        var range = NortonTransform.Range(rebuilt.Network!, pair!, rebuilt.Basis.AnalysisIsTerm1, false);
        Assert.True(range.IsUsable, $"{a}/{b} has no usable range");
        return new TransformRecord(a, b, form, 0.5 * (range.Min + range.Max), false);
    }

    private static double WorstS11(MatchNetwork net, double f1, double f2) =>
        MatchAbcdOracle.Band(f1, f2, 401).Max(f => MatchAbcdOracle.S(net, f).S11.Magnitude);

    // ── P1. The series-RC end — a FET input ───────────────────────────────────

    /// <summary>
    /// <b>The case the owner hit first.</b> §4.9's termination 2 is 1.25 Ω in series with 10 pF: the
    /// end arm is a series arm whose capacitor is the device's own C_gs, absorbed and not on the
    /// board. DC from the gate terminal crosses L4 and meets L3, which shorts the gate bias — so L3
    /// is the host, reached through L4. The compensation is the same identity as on an end node, and
    /// the response agrees with the oracle's explicit series L-C branch at that interior node.
    /// </summary>
    [Fact]
    public void ASeriesRcEnd_HostsTheBlockOneSeriesInductorIn()
    {
        var design = Golden();
        var free = Ladder(design);

        var host = MatchDcBlock.ResolveHost(free, 2);
        Assert.Single(host.Hosts);
        Assert.Equal(DcBlockStop.SeriesCapacitor, host.Stop);   // the walk ended at C2, beyond L3
        Assert.Equal("C2", host.StopElementName);
        Assert.True(host.Index >= 0);
        var hostElement = free.Elements[host.Index];
        Assert.True(hostElement.IsShunt && hostElement.Type == ElementType.L && !hostElement.IsAbsorbed);
        Assert.Equal("L3", hostElement.Name);
        Assert.Equal(["L4"], host.Path);

        // The absorbed C4 sits between L4 and the port; the walk stepped over it.
        var nets = free.AssignNets();
        Assert.NotEqual(free.RightPortNet(), nets[host.Index].A);

        double om0 = design.Omega0;
        double l = hostElement.Value;
        design.Term2DcBlock = MatchDcBlock.DefaultFor(l, om0, MatchDesignerDefaultCap);
        var rebuilt = MatchRebuild.Rebuild(design);
        Assert.Null(rebuilt.Refusal);

        var note = Assert.Single(rebuilt.DcBlocks);
        Assert.True(note.Applied, note.Reason);
        Assert.Equal(2, note.End);
        Assert.Equal("L3", note.ElementName);
        Assert.Equal(["L4"], note.Path);

        var branch = rebuilt.Network!.Elements[host.Index];
        Assert.Equal("L3", branch.Name);
        Assert.True(branch.DcBlock > 0);
        double got = BranchReactance(branch, om0);
        Assert.True(Math.Abs(got / (om0 * l) - 1.0) <= 1e-12, $"branch reactance {got} vs {om0 * l}");

        double lo = design.F1 * 0.9, hi = design.F2 * 1.1, worst = 0.0;
        for (int i = 0; i < 401; i++)
        {
            double f = lo + (hi - lo) * i / 400.0;
            var (s11, s21) = MatchResponse.At(rebuilt.Network, f);
            var (o11, o21) = MatchAbcdOracle.S(rebuilt.Network, f);
            worst = Math.Max(worst, Math.Max((s11 - o11).Magnitude, (s21 - o21).Magnitude));
        }
        Assert.True(worst < 1e-12, $"worst |Δ| vs the oracle {worst}");

        double rlFree = -20 * Math.Log10(WorstS11(free, design.F1, design.F2));
        double rlBlocked = -20 * Math.Log10(WorstS11(rebuilt.Network, design.F1, design.F2));
        output.WriteLine($"host {note.ElementName} via [{string.Join(", ", note.Path)}]: "
                         + $"L {l * 1e12:0.###} -> L' {branch.Value * 1e12:0.###} pH with {note.Farads * 1e12:0.###} pF, "
                         + $"f_s {note.SeriesResonanceHz / 1e6:0.#} MHz, spread ±{note.BandSpread * 100:0.###} %; "
                         + $"worst RL {rlFree:0.###} -> {rlBlocked:0.###} dB (Δ {rlBlocked - rlFree:+0.###;-0.###}); oracle |Δ| {worst:0.##e+0}");
    }

    // ── P2. A Norton T on the end pair ────────────────────────────────────────

    /// <summary>
    /// <b>The second case the owner hit.</b> A T on (L1, L2) replaces L1 with series-L / shunt-L /
    /// series-L, so the Term1 end arm becomes a series INDUCTOR with no capacitor at all — under
    /// MN-DCB the toggle went grey while the T's shunt product still shorted the termination. The
    /// host is that shunt product, reached through the T's first series product.
    ///
    /// <para>§4.9's own ladder offers no pair naming L1 (RESOLVED §MN-DCB), so this is §22's drain
    /// network, as the π test above is. The comparison is in linear |S11| for the reason that test
    /// gives: the network is matched to −52 dB, where a dB is three ten-thousandths of a unit.</para>
    /// </summary>
    [Fact]
    public void ANortonTOnTheEndPair_MovesTheHostOneSeriesProductIn_AndTheBlockFollows()
    {
        var design = Drain();
        var basis = Ladder(design);
        int l1 = MatchDcBlock.ResolveHost(basis, 1).Index;
        double c = MatchDcBlock.DefaultFor(basis.Elements[l1].Value, design.Omega0, MatchDesignerDefaultCap);

        var t = design.Clone();
        t.Transforms = [MidRange(design, "L1", "L2", TransformForm.T)];
        var freeT = Ladder(t);

        var host = MatchDcBlock.ResolveHost(freeT, 1);
        Assert.Single(host.Hosts);   // series / SHUNT / series, then C2 — one host, unlike the π
        Assert.Equal(MatchLadderNames.ProductPrefix("L1", 1) + "2", freeT.Elements[host.Index].Name);
        Assert.Equal([MatchLadderNames.ProductPrefix("L1", 1) + "1"], host.Path);
        var firstReal = freeT.Elements.First(e => !e.IsAbsorbed);
        Assert.True(!firstReal.IsShunt && firstReal.Type == ElementType.L,
                    "after the T the first real element at the Term1 end is a SERIES inductor");

        var blocked = t.Clone();
        blocked.Term1DcBlock = c;
        var withBlock = MatchRebuild.Rebuild(blocked);
        Assert.Null(withBlock.Refusal);
        var note = Assert.Single(withBlock.DcBlocks);
        Assert.True(note.Applied, note.Reason);
        Assert.Equal(freeT.Elements[host.Index].Name, note.ElementName);
        Assert.Equal(host.Path, note.Path);

        // The compensation is the same identity it is on an end node: exact at ω₀.
        double om0 = design.Omega0;
        var branch = withBlock.Network!.Elements[host.Index];
        double synthesised = freeT.Elements[host.Index].Value;
        Assert.True(Math.Abs(BranchReactance(branch, om0) / (om0 * synthesised) - 1.0) <= 1e-12);

        // ── What the block costs here, and why the gate is on its ORDER rather than a number ──
        //
        // The brief asks for 0.05 dB; MN-DCB already found dB the wrong ruler on this −52 dB fixture.
        // The default block moves worst |S11| by 1.8e-3 here against 1.1e-3 on the untransformed L1
        // and 0.8e-3 after the π — the T's host is a larger inductor (131 pH vs 99.5) at a node the
        // response is more sensitive to, so the same second-order residual costs more. What proves
        // the block is correctly attached and compensated is that the residual IS second order in
        // the block: ten times the capacitance must shrink the deviation by about ten times, which a
        // mis-attached or uncompensated block would not do. See RESOLVED.md §MN-DCB2.
        var bigger = t.Clone();
        bigger.Term1DcBlock = 10.0 * c;
        var withBigger = MatchRebuild.Rebuild(bigger);

        double a = WorstS11(freeT, design.F1, design.F2);
        double b = WorstS11(withBlock.Network!, design.F1, design.F2);
        double b10 = WorstS11(withBigger.Network!, design.F1, design.F2);
        double dbA = -20 * Math.Log10(a), dbB = -20 * Math.Log10(b), dbB10 = -20 * Math.Log10(b10);
        output.WriteLine($"T on L1/L2: host {note.ElementName} via [{string.Join(", ", note.Path)}]; "
                         + $"worst |S11| block-free {a:0.#######} ({dbA:0.###} dB), blocked {b:0.#######} ({dbB:0.###} dB), "
                         + $"10x block {b10:0.#######} ({dbB10:0.###} dB); "
                         + $"f_s {note.SeriesResonanceHz / 1e6:0.#} MHz, spread ±{note.BandSpread * 100:0.###} %");
        Assert.True(b < 5e-3, $"the T'd, blocked ladder is still matched: |S11| {b}");
        Assert.True(Math.Abs(a - b) <= 2.5e-3, $"{a} vs {b}");
        Assert.True(Math.Abs(a - b10) <= Math.Abs(a - b) / 5.0,
            $"a 10x block should cut the residual ~10x: {Math.Abs(a - b)} -> {Math.Abs(a - b10)}");
    }

    // ── P4. A real series capacitor isolates ──────────────────────────────────

    /// <summary>
    /// <b>Withheld, deliberately, and said.</b> A series-C termination whose Q is far below the
    /// synthesis Q gets a real <c>CFano</c> (far end) or <c>CDetune</c> (analysis end, Q-adjusted)
    /// in its through path — OUR capacitor, on the board — and that isolates the termination from
    /// DC before any shunt inductor is reached. A block beyond it would protect nothing; the note
    /// names the capacitor and says where the bias has to be fed instead (match.md §22.1, an
    /// owner-overridable assumption).
    /// </summary>
    [Theory]
    [InlineData("fano")]
    [InlineData("detune")]
    public void ARealSeriesCapacitor_IsolatesTheEnd_AndTheBlockIsWithheld(string which)
    {
        var design = Golden();
        string expected;
        if (which == "fano")
        {
            design.Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 100e-12);
            expected = "CFano";
        }
        else
        {
            design.AnalysisEnd = AnalysisEndChoice.Term2;
            design.QAdjust = 6.0;
            expected = "CDetune";
        }
        design.Term2DcBlock = 1e-9;

        var rebuilt = MatchRebuild.Rebuild(design);
        Assert.Null(rebuilt.Refusal);
        var net = rebuilt.Network!;

        var stop = net.Elements.Single(e => e.Name == expected);
        Assert.False(stop.IsShunt);
        Assert.Equal(ElementType.C, stop.Type);
        Assert.False(stop.IsAbsorbed);

        var host = MatchDcBlock.ResolveHost(net, 2);
        Assert.Equal(-1, host.Index);
        Assert.Equal(DcBlockStop.SeriesCapacitor, host.Stop);
        Assert.Equal(expected, host.StopElementName);

        var note = Assert.Single(rebuilt.DcBlocks);
        Assert.False(note.Applied);
        Assert.Equal(expected, note.StopElementName);
        Assert.Contains($"{expected} is a real capacitor in this end's through path and already isolates it from DC",
                        note.Reason, StringComparison.Ordinal);
        Assert.Contains($"feed its bias on the termination's own side of {expected}", note.Reason, StringComparison.Ordinal);
        Assert.Contains("Stored, not applied", note.Reason, StringComparison.Ordinal);
        Assert.All(net.Elements, e => Assert.Equal(0.0, e.DcBlock));

        // Termination 1 is unaffected: still the end-node host.
        Assert.Equal(0, MatchDcBlock.ResolveHost(net, 1).Index);
        output.WriteLine(note.Reason);
    }

    // ── P5. Highpass, series-C end absorbed ───────────────────────────────────

    /// <summary>
    /// In highpass form each arm is ONE element, so a series-RC termination's capacitor IS the end
    /// series arm, absorbed whole. It is transparent to the walk, and the host is the shunt inductor
    /// on the ladder-side node of that capacitor — which is the end node, so the path is empty.
    /// </summary>
    [Fact]
    public void AHighpassSeriesCEnd_IsAbsorbedAndTransparent_AndTheHostIsBehindIt()
    {
        var design = Highpass();
        design.Term2 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Series, 5e-12);
        var free = Ladder(design);

        var absorbed = free.Elements.Single(e => e.AbsorbedEnd == 2);
        Assert.False(absorbed.IsShunt);
        Assert.Equal(ElementType.C, absorbed.Type);
        Assert.Same(absorbed, free.Elements[^1]);

        var host = MatchDcBlock.ResolveHost(free, 2);
        Assert.Single(host.Hosts);
        Assert.Empty(host.Path);
        var hostElement = free.Elements[host.Index];
        Assert.True(hostElement.IsShunt && hostElement.Type == ElementType.L && !hostElement.IsAbsorbed);
        Assert.Equal(free.Elements.Count - 2, host.Index);   // the inductor right behind the absorbed C

        double om0 = design.Omega0, l = hostElement.Value;
        design.Term2DcBlock = MatchDcBlock.DefaultFor(l, om0, MatchDesignerDefaultCap);
        var rebuilt = MatchRebuild.Rebuild(design);
        var note = Assert.Single(rebuilt.DcBlocks);
        Assert.True(note.Applied, note.Reason);
        Assert.Equal(hostElement.Name, note.ElementName);
        var branch = rebuilt.Network!.Elements[host.Index];
        Assert.True(Math.Abs(BranchReactance(branch, om0) / (om0 * l) - 1.0) <= 1e-12);
        output.WriteLine($"highpass: absorbed {absorbed.Name}, host {note.ElementName} ({l * 1e12:0.###} -> {branch.Value * 1e12:0.###} pH)");
    }

    // ── P8. Both ends interior ────────────────────────────────────────────────

    /// <summary>
    /// <b>Two series ends, two interior hosts.</b> At order 5 the two DC paths cross L1 and L5 and
    /// reach two different shunt inductors; both blocks apply. At order 3 the same terminations give
    /// series / shunt / series, so BOTH walks land on the one shunt inductor — it is blocked once,
    /// and termination 2's note says so rather than overwriting termination 1's compensation.
    /// </summary>
    [Fact]
    public void TwoSeriesEnds_HostTwoDistinctInteriorBlocks_OrCollideOnOne()
    {
        var five = BothSeries(5);
        five.Term1DcBlock = 1e-9;
        five.Term2DcBlock = 2e-9;
        var r5 = MatchRebuild.Rebuild(five);
        Assert.Null(r5.Refusal);

        var h1 = MatchDcBlock.ResolveHost(r5.Network!, 1);
        var h2 = MatchDcBlock.ResolveHost(r5.Network!, 2);
        Assert.True(h1.Index >= 0 && h2.Index >= 0);
        Assert.NotEqual(h1.Index, h2.Index);
        Assert.Equal(["L1"], h1.Path);
        Assert.Equal(["L5"], h2.Path);

        Assert.Equal(2, r5.DcBlocks.Count);
        Assert.All(r5.DcBlocks, n => Assert.True(n.Applied, n.Reason));
        Assert.Equal(2, r5.Network!.Elements.Count(e => e.DcBlock > 0));
        Assert.Equal(1e-9, r5.Network.Elements[h1.Index].DcBlock);
        Assert.Equal(2e-9, r5.Network.Elements[h2.Index].DcBlock);
        output.WriteLine($"order 5: {r5.DcBlocks[0].ElementName} via [{string.Join(",", r5.DcBlocks[0].Path)}], "
                         + $"{r5.DcBlocks[1].ElementName} via [{string.Join(",", r5.DcBlocks[1].Path)}]");

        var three = BothSeries(3);
        three.Term1DcBlock = 1e-9;
        three.Term2DcBlock = 2e-9;
        var r3 = MatchRebuild.Rebuild(three);
        Assert.Null(r3.Refusal);
        Assert.Equal(MatchDcBlock.ResolveHost(r3.Network!, 1).Index, MatchDcBlock.ResolveHost(r3.Network!, 2).Index);

        Assert.Equal(2, r3.DcBlocks.Count);
        Assert.True(r3.DcBlocks[0].Applied);
        Assert.False(r3.DcBlocks[1].Applied);
        Assert.Contains("both ends of this ladder reach the same shunt inductor", r3.DcBlocks[1].Reason, StringComparison.Ordinal);
        var only = Assert.Single(r3.Network!.Elements, e => e.DcBlock > 0);
        Assert.Equal(1e-9, only.DcBlock);
        output.WriteLine($"order 3: {r3.DcBlocks[0].ElementName} via [{string.Join(",", r3.DcBlocks[0].Path)}]; {r3.DcBlocks[1].Reason}");
    }

    // ── P9. No node carries two real shunt inductors ──────────────────────────

    /// <summary>
    /// <b>Verified, not restated.</b> MN-DCB's comment said two shunt inductors on one node do not
    /// occur in any ladder the rebuild produces; with the host now allowed to be an interior node
    /// that claim carries the whole feature (a second unblocked one would be a short). Every golden
    /// fixture, × {none, π, T} on the first and the last discoverable pair, × the split cases the
    /// fixtures reach (a shunt and a series Fano, a shunt and a series detune) — and the sweep
    /// asserts that it actually saw each split kind, so a synthesis change cannot silently empty it.
    /// </summary>
    [Fact]
    public void NoNodeInAnyRebuiltLadder_CarriesTwoRealShuntInductors()
    {
        var drainDetune = Drain();
        drainDetune.QAdjust = 3.2152;   // the §22.2 fixture's Q-adjust: a shunt CDetune at Term1
        var goldenFanoSeries = Golden();
        goldenFanoSeries.Term2 = new Termination(1.25, ReactanceKind.C, TerminationTopology.Series, 100e-12);
        var goldenDetuneSeries = Golden();
        goldenDetuneSeries.AnalysisEnd = AnalysisEndChoice.Term2;
        goldenDetuneSeries.QAdjust = 6.0;
        var highpassSeriesRc = Highpass();
        highpassSeriesRc.Term2 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Series, 5e-12);
        var highpassExcess = Highpass();
        highpassExcess.Term2 = new Termination(5.0, ReactanceKind.C, TerminationTopology.Series, 10e-12);

        var fixtures = new (string Tag, MatchDesign Design)[]
        {
            ("golden (shunt CFano)", Golden()),
            ("golden, series CFano", goldenFanoSeries),
            ("golden, series CDetune", goldenDetuneSeries),
            ("drain", Drain()),
            ("drain, shunt CDetune", drainDetune),
            ("highpass", Highpass()),
            ("highpass, series-RC end", highpassSeriesRc),
            ("highpass, series CExcess", highpassExcess),
            ("dual-band", DualBand()),
            ("both series, order 5", BothSeries(5)),
            ("both series, order 3", BothSeries(3)),
        };

        int cases = 0;
        var splitKinds = new HashSet<string>();
        foreach (var (tag, design) in fixtures)
        {
            var basis = MatchRebuild.Rebuild(design);
            Assert.Null(basis.Refusal);
            var pairs = NortonTransform.Discover(basis.Network!).ToList();
            var variants = new List<(string, MatchDesign)> { ($"{tag}", design) };
            foreach (var pair in pairs.Count == 0 ? [] : pairs.Count == 1 ? [pairs[0]] : new[] { pairs[0], pairs[^1] })
                foreach (var form in new[] { TransformForm.Pi, TransformForm.T })
                {
                    var range = NortonTransform.Range(basis.Network!, pair, basis.Basis.AnalysisIsTerm1, false);
                    if (!range.IsUsable) continue;
                    var d = design.Clone();
                    d.Transforms = [new TransformRecord(pair.NameA, pair.NameB, form, 0.5 * (range.Min + range.Max), false)];
                    variants.Add(($"{tag} + {form} {pair.NameA}/{pair.NameB}", d));
                }

            foreach (var (vtag, d) in variants)
            {
                var r = MatchRebuild.Rebuild(d);
                if (r.Refusal is not null || r.Dropped.Count > 0) continue;
                cases++;
                var net = r.Network!;
                var nets = net.AssignNets();
                foreach (var e in net.Elements)
                    if (e.IsExcess || e.IsDetune)
                        splitKinds.Add($"{(e.IsShunt ? "shunt" : "series")} {e.Name}");

                var perNode = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                for (int i = 0; i < net.Elements.Count; i++)
                {
                    var e = net.Elements[i];
                    if (!e.IsShunt || e.IsAbsorbed || e.Type != ElementType.L) continue;
                    if (!perNode.TryGetValue(nets[i].A, out var list)) perNode[nets[i].A] = list = [];
                    list.Add(e.Name);
                }
                foreach (var (node, names) in perNode)
                    Assert.True(names.Count == 1,
                        $"{vtag}: node {node} carries {names.Count} real shunt inductors ({string.Join(", ", names)})");
            }
        }

        output.WriteLine($"{cases} ladders checked; split elements seen: {string.Join(", ", splitKinds.Order())}");
        Assert.True(cases >= 30, $"only {cases} ladders were exercised");
        Assert.Contains("shunt CFano", splitKinds);
        Assert.Contains("series CFano", splitKinds);
        Assert.Contains("shunt CDetune", splitKinds);
        Assert.Contains("series CDetune", splitKinds);
    }
}
