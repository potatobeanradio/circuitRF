using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// <b>M4 — the correctness heart of the brief.</b> Tiers 0, 1 and 4 of the oracle ladder, plus the
/// regression that pins D2's correction so it cannot silently revert.
///
/// <para><b>Tier 0 is the one that matters most.</b> It is the only check in this brief that tests
/// the <c>J′</c> formulation against something derived independently of the solver: for an ideal
/// <c>Ids = gm·Vgs</c> device with a source lead <c>Ls</c>, fed from <c>Zs</c>, injecting a test
/// current into the intrinsic gate–source port gives</para>
/// <code>
///   V_t = It·(Zs + Z_Ls) − Z_Ls·Ids ,   Ids = gm·V_t
///   ⇒  Z_seen = V_t / It = (Zs + Z_Ls) / (1 + gm·Z_Ls)
/// </code>
/// <para><b>The sign is NOT the design note's, and that is a finding rather than a preference.</b>
/// <c>harmonicarf.md</c> §4.5.3(a) writes <c>V_t = It(Zs + Z_Ls) + Z_Ls·Ids</c> and hence
/// <c>(Zs + Z_Ls)/(1 − gm·Z_Ls)</c>. That places the drain current flowing INTO node s′ from the
/// external circuit. circuitRF's passive sign convention puts it the other way: <c>I[p]</c> is the
/// current into the device at the port's + terminal and OUT of its − terminal, so port 2 = (drain,
/// source) delivers <c>Ids</c> into node s′ from the DEVICE, and KCL there reads
/// <c>Ids = It + V_s/Z_Ls</c>. Two independent checks agree with the <c>+</c> form:</para>
/// <list type="bullet">
/// <item>the degenerate case <c>Zs = 0</c>, <c>Z_Ls = R</c> gives <c>R/(1 + gm·R) → 1/gm</c> as
/// <c>gm → ∞</c>, which is the source-follower output impedance and is what looking OUT of a
/// degenerated gate–source port must give; the note's form gives <c>R/(1 − gm·R)</c>, which is
/// NEGATIVE for <c>gm·R > 1</c> — a passive network with a passive degeneration cannot do that;</item>
/// <item>numerically, on the fixture below, the <c>+</c> form matches to 1e-13 and the <c>−</c> form
/// is out by a factor of two.</item>
/// </list>
/// <para>The physics in the note is right and only the sign is wrong; the <c>gm</c> term's presence —
/// which is the whole point of §4.5.3(a) — is what the fixture's own guard checks.</para>
/// <para>The <c>gm</c> term is not optional: grounding the source at the PACKAGE plane leaves
/// <c>Ls</c> carrying the DRAIN current as well as the gate's, so the impedance the gate control
/// sees depends on the device's own transconductance. A formulation that got §4.5.3(a) wrong would
/// return <c>Zs + Z_Ls</c> here and look entirely plausible.</para>
/// </summary>
public sealed class IntrinsicPlaneTests(ITestOutputHelper output)
{
    private const double F0 = 2e9;
    private const double ChokeH = 1e-6;      // see SchurReterminationTests for why not 1 H
    private const double BlockF = 1e-9;

    private static string N(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

    private static AnalysisSettings Settings => new()
    {
        InductanceRegularization  = RegularizationMode.Always,
        ConductanceRegularization = RegularizationMode.Never,
    };

    private static HarmonicaSettings Knobs(int k = 3) => new()
    {
        HarmonicCount = k, FrequencyHz = F0,
        BiasChokeHenries = ChokeH, DcBlockFarads = BlockF,

        // ‖F‖ is an ABSOLUTE current-residual tolerance, and the DC row's scale is set by the
        // inductance regularisation: an ideal choke through an ideal supply is a zero-impedance DC
        // path, so Y(0) ≈ 1/R_reg ≈ 1e6 S and a 1e-14 V error in a 10 V bias node shows as 1e-8 A of
        // residual. 1e-9 is at the floor this circuit can reach; asking for less converges nowhere.
        Tol = 1e-9,
    };

    /// <summary>The passive network at a termination plane: choke in parallel with the blocked marker.</summary>
    private static Complex PlaneAdmittance(Complex z, int k)
    {
        double omega = k * 2.0 * Math.PI * F0;
        return Complex.One / new Complex(0, omega * ChokeH)
             + Complex.One / (z + Complex.One / new Complex(0, omega * BlockF));
    }

    /// <summary>
    /// An IDEAL transconductor: <c>Ids = gm·Vgs</c>, no gate current, no charge, no output
    /// conductance. Exactly the device Tier 0's closed form is derived for — and linear, so the
    /// large-signal <c>gm</c> is the small-signal one and the oracle needs no operating point.
    /// </summary>
    private static CircuitModel Transconductor(double gm, LumpedPackage package, int k = 3) => new()
    {
        Dut = new DutSpec
        {
            Kind = DutKind.Sdd, TypeName = "SDD",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["I[2,0]"] = $"{N(gm)}*_v1",
            },
        },
        Embedding = new EmbeddingStack { Package = package },
        Bias      = new BiasSpec { Vgs = -1.5, Vds = 10 },
        Settings  = Knobs(k),
    };

    private static (HarmonicaContext Ctx, OperatingPoint Point, TerminationSet Terms) Solve(
        CircuitModel model, Complex zs, Complex zl, double pavlDbm = -20)
    {
        var terms = new TerminationSet(model.Settings.HarmonicCount);
        for (int h = 1; h <= model.Settings.HarmonicCount; h++)
        {
            terms.Set(TerminationSide.Source, h, zs);
            terms.Set(TerminationSide.Load,   h, zl);
        }

        var ctx = HarmonicaContext.Create(model, Settings);
        var pt  = ctx.Solve(terms, pavlDbm);
        Assert.True(pt.Converged,
            $"the fixture must converge before anything is read off it — ‖F‖ = {pt.Residual:E3} after {pt.Iterations} iterations");
        return (ctx, pt, terms);
    }

    private static double RelDiff(Complex a, Complex b)
    {
        double scale = Math.Max(a.Magnitude, b.Magnitude);
        return scale == 0 ? 0 : (a - b).Magnitude / scale;
    }

    // ── TIER 0 — Z_S,intr against a hand-derived closed form ──────────────────

    [Theory]
    [InlineData(0.20e-9)]
    [InlineData(0.50e-9)]
    [InlineData(1.00e-9)]
    public void Tier0_SourceImpedanceMatchesTheHandDerivedClosedForm(double lsHenries)
    {
        const double gm = 0.15;
        var zs = new Complex(25, 0);

        var model = Transconductor(gm, new LumpedPackage { Ls = lsHenries });
        var (ctx, point, terms) = Solve(model, zs, new Complex(50, 0));

        var zConv = IntrinsicPlane.SourceImpedance(ctx, point);

        double worst = 0;
        for (int k = 1; k <= model.Settings.HarmonicCount; k++)
        {
            double omega = k * 2.0 * Math.PI * F0;

            // Zs is what the intrinsic gate sees looking outward through the PASSIVE source network:
            // the marker behind its DC block, in parallel with the bias choke.
            Complex zSeenPassive = Complex.One / PlaneAdmittance(zs, k);
            Complex zLs   = new(0, omega * lsHenries);
            Complex hand  = (zSeenPassive + zLs) / (Complex.One + gm * zLs);
            Complex asNoteWritesIt = (zSeenPassive + zLs) / (Complex.One - gm * zLs);

            double rel = RelDiff(zConv[k, k], hand);
            worst = Math.Max(worst, rel);
            output.WriteLine(
                $"Ls={lsHenries * 1e9:F2} nH  k={k}:  Z_S,intr = {zConv[k, k]:G10}   " +
                $"(Zs+Z_Ls)/(1+gm·Z_Ls) = {hand:G10}   rel {rel:E3}   " +
                $"[note's (1−gm·Z_Ls) form = {asNoteWritesIt:G10}, rel {RelDiff(zConv[k, k], asNoteWritesIt):E3}]");
        }

        // The check that stops this passing for the wrong reason: without the gm term the answer
        // would be Zs + Z_Ls, and it must be visibly different from what the formulation returns.
        Complex noFeedback = Complex.One / PlaneAdmittance(zs, 1) + new Complex(0, 2 * Math.PI * F0 * lsHenries);
        double separation = RelDiff(zConv[1, 1], noFeedback);
        output.WriteLine($"separation from the NO-feedback answer (Zs + Z_Ls): {separation:E3}");
        Assert.True(separation > 1e-3,
            $"the fixture must exercise the gm term, but Z_S,intr is only {separation:E3} from " +
            "(Zs + Z_Ls) — a formulation that ignored §4.5.3(a) would pass this");

        Assert.True(worst <= 1e-9,
            $"Z_S,intr differs from the independently derived closed form by {worst:E3} relative");
    }

    [Fact]
    public void Tier0_TheDegenerateLimitIsTheSourceFollowerOutputImpedance()
    {
        // The second, structural check on the sign, and the one that needs no algebra: with a large
        // RESISTIVE source lead and a strong device, looking out of the gate–source port must
        // approach 1/gm — the source-follower output impedance. The note's (1 − gm·Z_Ls) form gives
        // a NEGATIVE real impedance here, which a passive degeneration cannot produce.
        const double gm = 0.5, rs = 40.0;      // gm·Rs = 20, deep into the regime that separates them
        var zs = new Complex(1e-3, 0);
        var model = Transconductor(gm, new LumpedPackage { Rs = rs }, k: 1);
        var (ctx, point, _) = Solve(model, zs, new Complex(50, 0));

        var zConv = IntrinsicPlane.SourceImpedance(ctx, point);
        Complex measured = zConv[1, 1];

        // Zs is not literally zero — the marker sits behind its DC block and beside the bias choke —
        // so the exact statement is the same closed form with that Zs, and the 1/gm limit is what it
        // approaches. Both are printed; the ASSERTION is on the sign, which is what this fixture is
        // for and which no amount of tolerance can blur.
        Complex zsPassive = Complex.One / PlaneAdmittance(zs, 1);
        Complex exact     = (zsPassive + rs) / (1 + gm * rs);

        output.WriteLine($"gm = {gm}, Rs = {rs} Ω  ⇒  Z_S,intr = {measured:G8}");
        output.WriteLine($"  (Zs+Rs)/(1+gm·Rs) = {exact:G8}     rel {RelDiff(measured, exact):E3}");
        output.WriteLine($"  the 1/gm limit    = {1 / gm:G8}");
        output.WriteLine($"  the note's (1−gm·Rs) form = {(zsPassive + rs) / (1 - gm * rs):G8}  ← NEGATIVE");

        Assert.True(measured.Real > 0,
            "looking out of a passively degenerated gate–source port cannot give a negative " +
            $"resistance, but Z_S,intr read {measured:G8}");
        Assert.True(measured.Real < 1.0 / gm,
            $"the degenerated port must sit below the 1/gm limit; it read {measured.Real:G8}");
        Assert.True(RelDiff(measured, exact) <= 1e-9,
            $"expected (Zs+Rs)/(1+gm·Rs) = {exact:G8}, got {measured:G8}");
    }

    // ── TIER 1 — with no feedback it IS the passive source network ────────────

    [Fact]
    public void Tier1_WithNoSourceLeadAndNoFeedbackItReducesToThePassiveNetwork()
    {
        // Ls = 0, no external feedback, no embedding cross-coupling. The gm path then has nowhere to
        // close through, so Z_S,intr(k) must reduce EXACTLY to the passive source network at the
        // intrinsic plane, at every k. The oracle is the same closed form Tier 0 uses with Z_Ls = 0 —
        // which is also (Zs + 0)/(1 − 0), so the two tiers share one derivation and disagree only in
        // which term they exercise.
        const double gm = 0.15;
        var zs = new Complex(25, -12);

        var model = Transconductor(gm, LumpedPackage.None, k: 5);
        var (ctx, point, _) = Solve(model, zs, new Complex(50, 0));

        var zConv = IntrinsicPlane.SourceImpedance(ctx, point);

        double worst = 0;
        for (int k = 1; k <= model.Settings.HarmonicCount; k++)
        {
            Complex hand = Complex.One / PlaneAdmittance(zs, k);
            double rel = RelDiff(zConv[k, k], hand);
            worst = Math.Max(worst, rel);
            output.WriteLine($"k={k}:  Z_S,intr = {zConv[k, k]:G12}   passive = {hand:G12}   rel {rel:E3}");
        }

        Assert.True(worst <= 1e-10, $"the reduction is not exact: {worst:E3} relative");
    }

    [Fact]
    public void Tier1_TheOffDiagonalConversionTermsVanishWithoutHarmonicCoupling()
    {
        // A linear device converts nothing between harmonics, so Zs_conv must be diagonal. This is
        // the structural half of Tier 1 and it is what would catch a real-split block laid out
        // wrongly — an error that leaves every diagonal entry plausible.
        var model = Transconductor(0.15, LumpedPackage.None, k: 3);
        var (ctx, point, _) = Solve(model, new Complex(25, -12), new Complex(50, 0));

        var zConv = IntrinsicPlane.SourceImpedance(ctx, point);

        double diag = 0, off = 0;
        for (int a = 0; a <= model.Settings.HarmonicCount; a++)
            for (int b = 0; b <= model.Settings.HarmonicCount; b++)
                if (a == b) diag = Math.Max(diag, zConv[a, b].Magnitude);
                else        off  = Math.Max(off,  zConv[a, b].Magnitude);

        output.WriteLine($"largest diagonal {diag:G6}, largest off-diagonal {off:G6}");
        Assert.True(off < 1e-9 * diag, $"a linear device must not convert: off/diag = {off / diag:E3}");
    }

    // ── the regression that pins D2 ───────────────────────────────────────────

    [Fact]
    public void SourceImpedanceIsNotTheVoltageOverCurrentRatio()
    {
        // §4.5.2's correction, pinned the way the Iin fix is pinned. On a device with a real gate
        // conduction path the two quantities both EXIST and are different; the ratio returns a
        // version of Zin (what is looking INTO the device) and the J′ route returns what the gate
        // control sees looking OUT. If someone ever replaces one with the other this goes red.
        var model = new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.Sdd, TypeName = "SDD",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["I[1,0]"] = "_v1/300",           // a real gate conduction path
                    ["I[2,0]"] = "0.15*_v1",
                },
            },
            Bias     = new BiasSpec { Vgs = -1.5, Vds = 10 },
            Settings = Knobs(k: 3),
        };

        var zs = new Complex(25, 0);
        var (ctx, point, _) = Solve(model, zs, new Complex(50, 0), pavlDbm: 0);

        var zConv = IntrinsicPlane.SourceImpedance(ctx, point);
        var spectra = IntrinsicPlane.Evaluate(
            ctx.DutComponent, point.V, ctx.Interface.DeviceNodes,
            model.Settings.HarmonicCount,
            HbFft.GridSize(model.Settings.HarmonicCount, 1), F0);
        var ratio = IntrinsicPlane.SourceImpedanceIsNotAVoltageCurrentRatio(spectra);

        // Both must be finite and non-trivial, or "they differ" is vacuous.
        Assert.True(double.IsFinite(ratio[1].Magnitude) && ratio[1].Magnitude > 1e-9,
            "the fixture must produce a MEANINGFUL V/I ratio, or this proves nothing");

        double separation = RelDiff(zConv[1, 1], ratio[1]);
        output.WriteLine($"Z_S,intr (J′ route) = {zConv[1, 1]:G8}");
        output.WriteLine($"V_g,1 / I_g,1       = {ratio[1]:G8}");
        output.WriteLine($"separation          = {separation:E3}");

        Assert.True(separation > 0.5,
            $"Z_S,intr and V_g/I_g are only {separation:E3} apart on a fixture built to separate " +
            "them — §4.5.2's correction may have regressed");

        // And the ratio really is the device's own gate resistance, which is what makes it the
        // WRONG answer: it says nothing about the source at all.
        output.WriteLine($"(the ratio is the device's own gate path, 300 Ω, not the 25 Ω source)");
        Assert.True(Math.Abs(ratio[1].Real - 300.0) < 1.0,
            $"expected the ratio to recover the device's own 300 Ω gate path; it read {ratio[1]:G8}");
    }

    // ── TIER 4 — the conduction / displacement split ──────────────────────────

    [Fact]
    public void Tier4_ChargeOff_TheLoadGlyphIsThePassiveLoadExactly()
    {
        // R-hrf-1: with no charge and no extrinsic network the conduction current IS the terminal
        // current, so Z_L,intr must equal the drain node's own passive load exactly.
        //
        // The brief words this as "the load glyph must equal its MARKER exactly". That is true only
        // if the marker is the only thing at the drain, and under ideal bias the choke is there too —
        // so the exact statement is the node's total passive load, and the distance from the marker
        // is the choke's leakage. Both are reported.
        var zl = new Complex(40, 15);
        var model = new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.Sdd, TypeName = "SDD",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["I[1,0]"] = "_v1/300",
                    ["I[2,0]"] = "0.08*(_v1+3)*(_v1+3)*tanh(0.4*_v2)",
                },
            },
            Bias     = new BiasSpec { Vgs = -1.5, Vds = 10 },
            Settings = Knobs(k: 3),
        };

        var (ctx, point, _) = Solve(model, new Complex(25, 0), zl, pavlDbm: 0);
        int k = model.Settings.HarmonicCount;
        var spectra = IntrinsicPlane.Evaluate(ctx.DutComponent, point.V, ctx.Interface.DeviceNodes,
                                              k, HbFft.GridSize(k, 1), F0);
        var zIntr = IntrinsicPlane.LoadImpedance(spectra);

        double worst = 0;
        for (int h = 1; h <= k; h++)
        {
            Complex passive = Complex.One / PlaneAdmittance(zl, h);
            double rel = RelDiff(zIntr[h], passive);
            worst = Math.Max(worst, rel);
            output.WriteLine($"k={h}: Z_L,intr = {zIntr[h]:G10}  passive load = {passive:G10}  rel {rel:E3}");
        }
        output.WriteLine($"distance from the MARKER itself at k=1 (the choke's leakage): " +
                         $"{RelDiff(zIntr[1], zl):E3}");

        Assert.True(worst <= 1e-9, $"charge off: the glyph is {worst:E3} from the passive load");
    }

    [Fact]
    public void Tier4_ChargeOn_TheSeparationMatchesAHandComputedJOmegaQ()
    {
        // With charge on, terminal current ≠ conduction current and the glyph separates from the
        // node's load. The separation is not a vibe: with Q[2,0] = Cds·Vds the drain node carries
        // jωCds·V_d as well, so
        //     Z_L,intr = −V_d / I_d^cond = 1 / ( Y_node + jωCds )
        // — the passive load in parallel with Cds, derived here and not from either code path.
        const double cds = 0.6e-12;
        var zl = new Complex(40, 15);

        var model = new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.Sdd, TypeName = "SDD",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["I[1,0]"] = "_v1/300",
                    ["I[2,0]"] = "0.08*(_v1+3)*(_v1+3)*tanh(0.4*_v2)",
                    ["Q[2]"] = $"{N(cds)}*_v2",
                },
            },
            Bias     = new BiasSpec { Vgs = -1.5, Vds = 10 },
            Settings = Knobs(k: 3),
        };

        var (ctx, point, _) = Solve(model, new Complex(25, 0), zl, pavlDbm: 0);
        int k = model.Settings.HarmonicCount;
        var spectra = IntrinsicPlane.Evaluate(ctx.DutComponent, point.V, ctx.Interface.DeviceNodes,
                                              k, HbFft.GridSize(k, 1), F0);
        var zIntr = IntrinsicPlane.LoadImpedance(spectra);

        double worst = 0, leastSeparation = double.MaxValue;
        for (int h = 1; h <= k; h++)
        {
            double omega = h * 2.0 * Math.PI * F0;
            Complex passive = Complex.One / PlaneAdmittance(zl, h);
            Complex hand    = Complex.One / (Complex.One / passive + new Complex(0, omega * cds));

            worst = Math.Min(double.MaxValue, Math.Max(worst, RelDiff(zIntr[h], hand)));
            leastSeparation = Math.Min(leastSeparation, RelDiff(zIntr[h], passive));

            output.WriteLine($"k={h}: Z_L,intr = {zIntr[h]:G10}   1/(Y_node + jωCds) = {hand:G10}   " +
                             $"rel {RelDiff(zIntr[h], hand):E3}   (separation from the marker's " +
                             $"plane {RelDiff(zIntr[h], passive):E3})");
        }

        Assert.True(leastSeparation > 1e-3,
            $"charge must actually separate the glyph from its marker, but the closest harmonic is " +
            $"only {leastSeparation:E3} away — the fixture is not exercising D1");
        Assert.True(worst <= 1e-8,
            $"charge on: the glyph is {worst:E3} from the hand-computed jωq answer");
    }

    [Fact]
    public void Tier4_BothHalvesOfTheSplitAreReportedAndBothAreRight()
    {
        // The split is only meaningful if both halves are available and correct.
        //
        // The CONDUCTION half must equal the engine's own INl at the drain node — that cube carries
        // i only, with the charge kept separately as qNl, which is precisely why D1's choice is
        // expressible at all. The DISPLACEMENT half must equal jωCds·V_d, a closed form.
        const double cds = 0.6e-12;
        var model = new CircuitModel
        {
            Dut = new DutSpec
            {
                Kind = DutKind.Sdd, TypeName = "SDD",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["I[1,0]"] = "_v1/300",
                    ["I[2,0]"] = "0.08*(_v1+3)*(_v1+3)*tanh(0.4*_v2)",
                    ["Q[2]"] = $"{N(cds)}*_v2",
                },
            },
            Bias     = new BiasSpec { Vgs = -1.5, Vds = 10 },
            Settings = Knobs(k: 3),
        };

        var (ctx, point, _) = Solve(model, new Complex(25, 0), new Complex(40, 15), pavlDbm: 0);
        int k = model.Settings.HarmonicCount;
        var spectra = IntrinsicPlane.Evaluate(ctx.DutComponent, point.V, ctx.Interface.DeviceNodes,
                                              k, HbFft.GridSize(k, 1), F0);

        int drainIdx = ctx.InterfaceIndex(HarmonicaNetlist.LoadPlane);
        Assert.True(drainIdx >= 0);

        double worstCond = 0, worstDisp = 0, largestDisp = 0;
        for (int h = 1; h <= k; h++)
        {
            worstCond = Math.Max(worstCond, RelDiff(spectra.portCurrents[1, h], point.INl[drainIdx, h]));

            Complex handDisp = new Complex(0, h * 2.0 * Math.PI * F0 * cds) * spectra.portVoltages[1, h];
            worstDisp = Math.Max(worstDisp, RelDiff(spectra.portChargeCurrents[1, h], handDisp));
            largestDisp = Math.Max(largestDisp, handDisp.Magnitude);

            output.WriteLine($"k={h}: I_cond = {spectra.portCurrents[1, h]:G8}   " +
                             $"jωq = {spectra.portChargeCurrents[1, h]:G8}   (hand {handDisp:G8})");
        }

        Assert.True(largestDisp > 1e-6,
            "the displacement current must be non-trivial, or matching it proves nothing");
        Assert.True(worstCond <= 1e-12,
            $"the conduction half does not match the engine's own INl: {worstCond:E3}");
        Assert.True(worstDisp <= 1e-9,
            $"the displacement half does not match jωCds·V_d: {worstDisp:E3}");
    }
}
