using System;
using System.Collections.Generic;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Igbt;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Gate tests for the built-in IGBT.
///
///   T1 — the analytic Jacobian against central finite differences, per port, over a bias grid.
///   T2 — the NODE-level Jacobian against central finite differences.
///   T3 — the collector current DIVIDES between the channel and the bipolar in the ratio the gain
///        asks for, and the two shares are what the branch names say they are.
///   T4 — the transport current's Jacobian entry is the OFF-DIAGONAL one. It depends on the
///        junction voltage, not on the collector-emitter voltage, and dropping that entry is the
///        classic plausible-but-unconvergent Jacobian.
///   T5 — it does NOT conduct in reverse. This is the difference from a power MOSFET that decides
///        whether a bridge needs a discrete freewheeling diode.
///   T6 — the stored base charge is Tau·I, which is what the current tail is made of, and it is
///        zero when Tau is.
///   T7 — the Miller capacitance collapses between its plateaus and its charge is the exact
///        integral of it.
///   T8 — p-channel is the exact mirror of n-channel.
///   T9 — temperature is inert at nominal.
///  T10 — port and terminal structure: the internal base node ALWAYS exists, parasitics or not.
/// </summary>
public class IgbtModelTests
{
    private static IgbtModel Full(
        IgbtModel.Polarity p = IgbtModel.Polarity.NChannel,
        double tempC = Temperature.NominalC, double tnomC = Temperature.NominalC,
        double rg = 3.0, double rc = 0.02, double re = 0.01,
        double tau = 1.2e-6, double bf = 0.6, double rce = 5e6)
        => new(
            polarity: p,
            vto: (double)(int)p * 5.4, kp: 9.0, lambda: 0.005,
            bipolarGain: bf,
            baseSaturationCurrent: 2e-12, baseEmission: 1.05,
            baseTransitTime: tau,
            baseEmitterResistance: 0.0, collectorEmitterResistance: rce,
            breakdownVoltage: 700.0, breakdownCurrent: 1e-3, breakdownEmission: 1.2,
            junctionCapacitance: 300e-12, junctionPotential: 0.85,
            gradingCoefficient: 0.45, forwardBiasCapCoeff: 0.5,
            gateEmitterCapacitance: 2500e-12,
            millerCapacitanceMax: 1200e-12, millerCapacitanceMin: 30e-12,
            millerTransitionVoltage: 1.5,
            gateResistance: rg, collectorResistance: rc, emitterResistance: re,
            tempC: tempC, tnomC: tnomC);

    /// <summary>
    /// A consistent port-voltage vector. The internal BASE node is an argument rather than derived,
    /// because it is a genuine unknown the solver finds — these tests supply it directly so they can
    /// probe the device at a stated internal state.
    /// </summary>
    private static double[] V(double vc, double vg, double ve, double vb,
                              double vgInt = double.NaN, double vcInt = double.NaN,
                              double veInt = double.NaN)
    {
        double gi = double.IsNaN(vgInt) ? vg : vgInt;
        double ci = double.IsNaN(vcInt) ? vc : vcInt;
        double ei = double.IsNaN(veInt) ? ve : veInt;
        var v = new List<double>
        {
            vb - ei,   // 0 channel + Rbe
            ci - vb,   // 1 the bipolar's junction
            gi - ei,   // 2 Cge
            gi - vb,   // 3 Miller
            ci - ei,   // 4 transport + Rce
        };
        if (!double.IsNaN(vgInt)) v.Add(vg - vgInt);
        if (!double.IsNaN(vcInt)) v.Add(vc - vcInt);
        if (!double.IsNaN(veInt)) v.Add(ve - veInt);
        return [.. v];
    }

    private static NonlinearResult Eval(ComponentModel m, double[] v)
        => m.Evaluate(new PortVoltages(v));

    /// <summary>The elaborator's node map with no ohmic parasitics, as indices into
    /// (collector, gate, emitter, base) = (0, 1, 2, 3).</summary>
    private static readonly int[] IntrinsicNodes = [3, 2, 0, 3, 1, 2, 1, 3, 0, 2];

    /// <summary>
    /// Off, at threshold, in the active region and hard on, with the internal base node placed
    /// where a real solve would put it (a junction drop below the collector) and also away from
    /// there, so the Jacobian is checked off the solution manifold as well as on it — which is
    /// where Newton actually spends its time.
    /// </summary>
    public static TheoryData<double, double, double> BiasGrid()
    {
        var d = new TheoryData<double, double, double>();
        foreach (double vc in new[] { -2.0, 0.6, 2.3, 15.0, 300.0 })
        foreach (double vg in new[] { 0.0, 4.7, 8.3, 15.0 })
        foreach (double drop in new[] { 0.35, 0.75 })
            d.Add(vc, vg, Math.Max(vc - drop, -1.0));
        return d;
    }

    // ── T1 ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T1_EveryJacobianEntry_MatchesCentralFiniteDifferences(double vc, double vg, double vb)
    {
        var m = Full(rg: 0, rc: 0, re: 0);
        double[] v0 = V(vc, vg, 0.0, vb);
        var r0 = Eval(m, v0);
        int P = v0.Length;

        const double H = 1e-6;
        for (int q = 0; q < P; q++)
        {
            var vp = (double[])v0.Clone(); vp[q] += H;
            var vm = (double[])v0.Clone(); vm[q] -= H;
            var rp = Eval(m, vp);
            var rn = Eval(m, vm);
            for (int p = 0; p < P; p++)
            {
                AssertClose((rp.I[p] - rn.I[p]) / (2 * H), r0.Dg[p, q], $"Dg[{p},{q}] at ({vc},{vg},{vb})", 1e-8);
                AssertClose((rp.Q[p] - rn.Q[p]) / (2 * H), r0.Dc[p, q], $"Dc[{p},{q}] at ({vc},{vg},{vb})", 1e-14);
            }
        }
    }

    // ── T2 ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T2_TheNodeLevelJacobian_MatchesCentralFiniteDifferences(double vc, double vg, double vb)
    {
        var m = Full(rg: 0, rc: 0, re: 0);

        (double[] I, double[] Q) Node(double[] t)
        {
            var r = Eval(m, V(t[0], t[1], t[2], t[3]));
            var ni = new double[4];
            var nq = new double[4];
            for (int p = 0; p < r.I.Length; p++)
            {
                ni[IntrinsicNodes[2 * p]] += r.I[p]; ni[IntrinsicNodes[2 * p + 1]] -= r.I[p];
                nq[IntrinsicNodes[2 * p]] += r.Q[p]; nq[IntrinsicNodes[2 * p + 1]] -= r.Q[p];
            }
            return (ni, nq);
        }

        var r0 = Eval(m, V(vc, vg, 0.0, vb));
        int P = r0.I.Length;
        var jg = new double[4, 4];
        var jc = new double[4, 4];
        for (int p = 0; p < P; p++)
        for (int q = 0; q < P; q++)
        {
            int np = IntrinsicNodes[2 * p], nm = IntrinsicNodes[2 * p + 1];
            int qp = IntrinsicNodes[2 * q], qm = IntrinsicNodes[2 * q + 1];
            foreach (var (row, sr) in new[] { (np, 1.0), (nm, -1.0) })
            foreach (var (col, sc) in new[] { (qp, 1.0), (qm, -1.0) })
            {
                jg[row, col] += sr * sc * r0.Dg[p, q];
                jc[row, col] += sr * sc * r0.Dc[p, q];
            }
        }

        double[] t0 = [vc, vg, 0.0, vb];
        const double H = 1e-6;
        for (int c = 0; c < 4; c++)
        {
            var tp = (double[])t0.Clone(); tp[c] += H;
            var tm = (double[])t0.Clone(); tm[c] -= H;
            var (ip, qp2) = Node(tp);
            var (im, qm2) = Node(tm);
            for (int r = 0; r < 4; r++)
            {
                // A node current is a SUM of port currents, and at the far end of this grid the
                // channel is carrying a kiloamp while the junction carries a microamp. Subtracting
                // two such sums throws away about log10(f/h) digits before the difference quotient
                // starts, so the floor below is what a central difference can actually resolve
                // here: eps*|f|/h. It is a property of the arithmetic, not of the model — the
                // per-port check in T1 passes at every one of these points — and stating it is
                // better than shrinking the grid until the device is no longer being exercised.
                AssertClose((ip[r] - im[r]) / (2 * H), jg[r, c], $"node dI[{r}]/dV[{c}]",
                            1e-8 + 1e-15 * Math.Max(Math.Abs(ip[r]), Math.Abs(im[r])) / H);
                AssertClose((qp2[r] - qm2[r]) / (2 * H), jc[r, c], $"node dQ[{r}]/dV[{c}]",
                            1e-14 + 1e-15 * Math.Max(Math.Abs(qp2[r]), Math.Abs(qm2[r])) / H);
            }
        }
    }

    // ── T3 ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.6)]
    [InlineData(3.0)]
    public void T3_TheCollectorCurrent_DividesInTheRatioTheGainAsks(double bf)
    {
        // No Rce: the leakage is a stated parallel resistance and would otherwise be counted into
        // the transport share, which is a claim about the GAIN and nothing else.
        var m = Full(rg: 0, rc: 0, re: 0, bf: bf, rce: 0.0);
        var r = Eval(m, V(vc: 15.0, vg: 15.0, ve: 0.0, vb: 14.25));

        double ib = r.I[1], ic = r.I[4];
        Assert.True(ib > 1e-6, $"the junction must be conducting here: {ib:E3} A");

        // ic/ib is the gain, by construction: the emitter current splits alpha/(1-alpha) and the
        // two fractions are formed once. If they were ever swapped the device would still solve.
        AssertRel(bf, ic / ib, "the transport share over the base share is the gain", 1e-9);

        // The collector terminal takes the sum — which is the emitter current, the thing the
        // junction actually passes.
        AssertRel(1.0 + bf, (ib + ic) / ib, "the collector carries the whole emitter current", 1e-9);
    }

    // ── T4 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T4_TheTransportCurrent_DependsOnTheJunctionVoltage_NotOnVce()
    {
        var m = Full(rg: 0, rc: 0, re: 0);
        var r = Eval(m, V(vc: 15.0, vg: 15.0, ve: 0.0, vb: 14.25));

        // The gain lives in the OFF-DIAGONAL entry. dg[4,4] is only the stated Rce leakage; drop
        // dg[4,1] and the device has no gain in the Jacobian while still having one in the current,
        // which is a solve that wanders rather than a wrong answer.
        Assert.True(r.Dg[4, 1] > 1e-6, "the transport current must depend on the junction voltage");
        AssertRel(1.0 / 5e6, r.Dg[4, 4], "dg[4,4] is the Rce leakage and nothing else", 1e-9);
        AssertRel(0.6, r.Dg[4, 1] / r.Dg[1, 1], "…and it carries the gain", 1e-9);
    }

    // ── T5 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T5_ItDoesNotConductInReverse_WhichIsTheDifferenceFromAPowerMosfet()
    {
        // Collector below the emitter, gate hard on. The bipolar's junction is reverse-biased and
        // there is no other path, so essentially nothing flows — which is exactly why an IGBT
        // half-bridge needs a discrete anti-parallel diode and a MOSFET one does not.
        var m = Full(rg: 0, rc: 0, re: 0);
        var r = Eval(m, V(vc: -2.0, vg: 15.0, ve: 0.0, vb: -1.0));

        double collector = r.I[1] + r.I[4];
        // The only current is the stated Rce leakage, which is 2 V over 5 MΩ.
        Assert.True(Math.Abs(collector) < 1e-6,
            $"reverse conduction must be negligible: {collector:E3} A");
    }

    // ── T5b ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>Break-over is across the DRIFT REGION, not at the bipolar's own junction.</b> An IGBT's
    /// forward blocking voltage is sustained between the internal base node and the emitter — the
    /// span the channel spans — so that is where <c>Bv</c> acts. Putting it on the bipolar's
    /// emitter-base junction instead (the first draft did) makes it trigger only with the collector
    /// far BELOW the base, which is a reverse-conduction condition the device never reaches: the
    /// parameter is then offered, carried, and unreachable at every bias a circuit produces.
    /// </summary>
    [Fact]
    public void T5b_BreakOverIsAcrossTheDriftRegion_NotAtTheBipolarsJunction()
    {
        var m = Full(rg: 0, rc: 0, re: 0);      // Bv = 700

        // Blocking well inside the rating: the gate is off and nothing conducts.
        var safe = Eval(m, V(vc: 400.0, vg: 0.0, ve: 0.0, vb: 399.5));
        Assert.True(Math.Abs(safe.I[0]) < 1e-3, $"inside the rating nothing flows: {safe.I[0]:E3} A");

        // Past it, the drift region conducts — and it is the CHANNEL's port that carries it, which
        // is the whole of the claim.
        var over = Eval(m, V(vc: 900.0, vg: 0.0, ve: 0.0, vb: 800.0));
        Assert.True(over.I[0] > 1e-3, $"past the rating the device must break over: {over.I[0]:E3} A");
        Assert.True(over.Dg[0, 0] > 0, "…and its conductance must be positive there");

        // Bv = 0 means NOT MODELLED, never "breaks over at 0 V" — the failure mode if it were read
        // the other way is a device that conducts at every bias.
        var none = new IgbtModel(vto: 5.4, kp: 9.0, breakdownVoltage: 0.0);
        Assert.Equal(0.0, Eval(none, V(vc: 900.0, vg: 0.0, ve: 0.0, vb: 800.0)).I[0]);
    }

    // ── T6 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T6_TheStoredBaseCharge_IsTauTimesTheCurrent_AndIsZeroWhenTauIs()
    {
        var bias = V(vc: 15.0, vg: 15.0, ve: 0.0, vb: 14.25);
        var with = Full(rg: 0, rc: 0, re: 0, tau: 1.2e-6);
        var without = Full(rg: 0, rc: 0, re: 0, tau: 0.0);

        var rw = Eval(with, bias);
        var rn = Eval(without, bias);

        // The difference between the two is exactly Tau·(emitter current) — the stored base charge,
        // which is what turn-off cannot remove through the gate and therefore what the current tail
        // is made of. The emitter current is the base share divided by its own fraction.
        double ie = rw.I[1] * (1.0 + 0.6);
        AssertRel(1.2e-6 * ie, rw.Q[1] - rn.Q[1], "the diffusion charge is Tau·I", 1e-9);

        // …and its capacitance is Tau·dI/dV, carried together so the two cannot disagree.
        AssertRel(1.2e-6 * ie / (1.0 + 0.6) / rw.I[1] * rw.Dg[1, 1] * 1.6,
                  rw.Dc[1, 1] - rn.Dc[1, 1], "…and so is its capacitance", 1e-6);
    }

    // ── T7 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T7_TheMillerCapacitance_CollapsesAndItsChargeIsTheExactIntegral()
    {
        var m = Full(rg: 0, rc: 0, re: 0);
        const double Max = 1200e-12, Min = 30e-12;

        // Gate above the internal base: the large plateau. Base far above the gate: the small one.
        AssertRel(Max, Eval(m, V(vc: 0.0, vg: 0.0, ve: 0.0, vb: -20.0)).Dc[3, 3], "Cgc max", 1e-6);
        AssertRel(Min, Eval(m, V(vc: 0.0, vg: 0.0, ve: 0.0, vb:  20.0)).Dc[3, 3], "Cgc min", 1e-6);

        double Q(double vgb) => Eval(m, V(0.0, 0.0, 0.0, -vgb)).Q[3];
        double C(double vgb) => Eval(m, V(0.0, 0.0, 0.0, -vgb)).Dc[3, 3];
        const int N = 20000;
        double a = -12.0, b = 12.0, h = (b - a) / N, integral = 0.5 * (C(a) + C(b));
        for (int k = 1; k < N; k++) integral += C(a + k * h);
        integral *= h;
        AssertRel(integral, Q(b) - Q(a), "Qgc is the integral of Cgc", 1e-7);
    }

    // ── T8 ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T8_PChannel_IsTheExactMirrorOfNChannel(double vc, double vg, double vb)
    {
        var n = Full(IgbtModel.Polarity.NChannel, rg: 0, rc: 0, re: 0);
        var p = Full(IgbtModel.Polarity.PChannel, rg: 0, rc: 0, re: 0);

        var rn = Eval(n, V(vc, vg, 0.0, vb));
        var rp = Eval(p, V(-vc, -vg, 0.0, -vb));

        for (int k = 0; k < rn.I.Length; k++)
        {
            AssertClose(-rn.I[k], rp.I[k], $"I[{k}] mirrored", 1e-18);
            AssertClose(-rn.Q[k], rp.Q[k], $"Q[{k}] mirrored", 1e-20);
            for (int j = 0; j < rn.I.Length; j++)
            {
                AssertClose(rn.Dg[k, j], rp.Dg[k, j], $"Dg[{k},{j}] mirrored", 1e-16);
                AssertClose(rn.Dc[k, j], rp.Dc[k, j], $"Dc[{k},{j}] mirrored", 1e-20);
            }
        }
    }

    // ── T9 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_TemperatureIsInertAtNominal()
    {
        var baseline = Full();
        var bias = V(15.0, 15.0, 0.0, 14.25, vgInt: 14.9, vcInt: 14.95, veInt: 0.01);
        foreach (double t in new[] { Temperature.NominalC, 125.0, -40.0 })
        {
            var m = Full(tempC: t, tnomC: t);
            var a = Eval(baseline, bias);
            var b = Eval(m, bias);
            Assert.Equal(a.I[0], b.I[0]);        // the channel current
            Assert.Equal(a.Q[2], b.Q[2]);        // Cge charge
            Assert.Equal(a.Q[3], b.Q[3]);        // Miller charge
        }
    }

    // ── T10 ───────────────────────────────────────────────────────────────────

    [Fact]
    public void T10_TheInternalBaseNode_AlwaysExists_ParasiticsOrNot()
    {
        // Unlike every other family here, this one's internal-node count never falls to zero: the
        // base node between the channel and the bipolar is what the model IS.
        var bare = Full(rg: 0, rc: 0, re: 0);
        Assert.Equal(5, bare.PortCount);
        Assert.Equal(1, bare.InternalNodeCount);
        Assert.Equal(["imos", "ib", "qge", "qgc", "ic"], bare.TerminalNames);

        Assert.Equal(2, Full(rg: 3, rc: 0, re: 0).InternalNodeCount);
        Assert.Equal(4, Full().InternalNodeCount);
        Assert.Equal(8, Full().PortCount);
        Assert.Equal(["imos", "ib", "qge", "qgc", "ic", "gate", "collector", "emitter"],
                     Full().TerminalNames);

        var r = Eval(Full(), V(15.0, 15.0, 0.0, 14.25, vgInt: 14.7, vcInt: 14.9, veInt: 0.02));
        AssertClose(0.3 / 3.0,    r.I[5], "Rg current", 1e-12);
        AssertClose(0.1 / 0.02,   r.I[6], "Rc current", 1e-12);
        AssertClose(-0.02 / 0.01, r.I[7], "Re current", 1e-12);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertClose(double expected, double actual, string what, double abs)
    {
        double tol = abs + 2e-5 * Math.Abs(expected);
        Assert.True(Math.Abs(expected - actual) <= tol,
            $"{what}: expected {expected:E12}, got {actual:E12} (tol {tol:E3})");
    }

    private static void AssertRel(double expected, double actual, string what, double rel)
        => Assert.True(Math.Abs(expected - actual) <= rel * Math.Abs(expected) + 1e-300,
            $"{what}: expected {expected:E12}, got {actual:E12}");
}
