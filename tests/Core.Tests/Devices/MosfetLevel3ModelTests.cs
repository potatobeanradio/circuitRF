using System;
using System.Collections.Generic;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Mos;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Gate tests for the level-3 MOS transistor.
///
/// <para><b>T1 is the one that matters and it is why the model is written the way it is.</b> The
/// level-3 channel law is a dozen stages deep and its derivatives are carried through it exactly by
/// <see cref="Grad3"/> rather than hand-derived; this checks the whole chain against central finite
/// differences with every mechanism switched on. A dropped term produces a Jacobian that is
/// plausible everywhere and right nowhere, and nothing else here would see it.</para>
///
///   T1 — Dg and Dc against central finite differences, per port, over a bias grid.
///   T2 — the NODE-level Jacobian against central finite differences.
///   T3 — with every short-channel parameter at zero, level 3 IS level 1 without its Lambda. This
///        is what says the departures are departures and not a second model.
///   T4 — each mechanism is individually live and moves the current in the published DIRECTION —
///        Eta raises it with drain bias, Theta and Vmax lower it, Kappa raises it past saturation,
///        Xj weakens the body effect, Delta strengthens it.
///   T5 — velocity saturation makes the device saturate EARLIER than pinch-off, which is the point
///        of it.
///   T6 — channel-length modulation is bounded: Δl cannot reach Leff, and the ceiling is smooth.
///   T7 — p-channel is the exact mirror of n-channel.
///   T8 — temperature relations are inert at nominal.
///   T9 — the factory builds both channels by name, and level 1 and level 3 are distinct types
///        with distinct parameter sets.
///   T10 — a bulk driven FORWARD past the surface potential still has a live, bounded body effect,
///        and both levels continue the same square root the same way.
/// </summary>
public class MosfetLevel3ModelTests
{
    /// <summary>Every mechanism live. A grid over a model with half its terms off proves half a model.</summary>
    private static MosfetLevel3Model Full(
        MosfetModelBase.Channel4 ch = MosfetModelBase.Channel4.N,
        double tempC = Temperature.NominalC, double tnomC = Temperature.NominalC,
        double eta = 0.06, double theta = 0.08, double kappa = 0.5, double vmax = 2.2e5,
        double delta = 0.7, double xj = 0.2e-6, double nsub = 1e16, double rd = 0, double rs = 0)
        => new(
            channel: ch,
            vto: (double)(int)ch * 0.68, kp: 6e-5, gamma: 0.55, phi: 0.7, nsub: nsub,
            eta: eta, theta: theta, kappa: kappa, vmax: vmax, delta: delta, xj: xj,
            w: 10e-6, l: 0.8e-6, ld: 0.05e-6, tox: 15e-9,
            cgso: 2.5e-10, cgdo: 2.5e-10, cgbo: 2e-10,
            saturationCurrent: 1e-14, cbd: 15e-15, cbs: 16e-15, cjsw: 1.5e-10, pd: 8e-6, ps: 8e-6,
            pb: 0.85, rd: rd, rs: rs,
            tempC: tempC, tnomC: tnomC);

    private static double[] V(double vd, double vg, double vs, double vb)
        => [vd - vs, vb - vs, vb - vd, vg - vs, vg - vd, vg - vb];

    private static NonlinearResult Eval(ComponentModel m, double[] v)
        => m.Evaluate(new PortVoltages(v));

    private static readonly int[] IntrinsicNodes = [0, 2, 3, 2, 3, 0, 1, 2, 1, 0, 1, 3];

    /// <summary>
    /// The bulk sits at or below the LOWER of the two channel terminals, for the reason
    /// <see cref="MosfetModelTests.BiasGrid"/> states: a forward-biased substrate junction passes a
    /// current the node sum would have to cancel eight digits of first.
    /// </summary>
    public static TheoryData<double, double, double, double> BiasGrid()
    {
        var d = new TheoryData<double, double, double, double>();
        foreach (double vg in new[] { 0.0, 0.75, 1.4, 3.3 })
        foreach (double vd in new[] { -1.1, 0.03, 0.35, 1.2, 3.3 })
        foreach (double below in new[] { 0.0, -0.9, -2.7 })
            d.Add(vd, vg, 0.0, Math.Min(0.0, vd) + below);
        return d;
    }

    // ── T1 ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T1_TheWholeDerivativeChain_MatchesCentralFiniteDifferences(
        double vd, double vg, double vs, double vb)
    {
        var m = Full();
        double[] v0 = V(vd, vg, vs, vb);
        var r0 = Eval(m, v0);
        int P = v0.Length;

        const double H = 1e-7;
        for (int q = 0; q < P; q++)
        {
            var vp = (double[])v0.Clone(); vp[q] += H;
            var vm = (double[])v0.Clone(); vm[q] -= H;
            var rp = Eval(m, vp);
            var rn = Eval(m, vm);
            for (int p = 0; p < P; p++)
            {
                AssertClose((rp.I[p] - rn.I[p]) / (2 * H), r0.Dg[p, q], $"Dg[{p},{q}] at ({vd},{vg},{vb})", 1e-8);
                AssertClose((rp.Q[p] - rn.Q[p]) / (2 * H), r0.Dc[p, q], $"Dc[{p},{q}] at ({vd},{vg},{vb})", 1e-14);
            }
        }
    }

    // ── T2 ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T2_TheNodeLevelJacobian_MatchesCentralFiniteDifferences(
        double vd, double vg, double vs, double vb)
    {
        var m = Full();

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

        var r0 = Eval(m, V(vd, vg, vs, vb));
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

        double[] t0 = [vd, vg, vs, vb];
        const double H = 1e-7;
        for (int c = 0; c < 4; c++)
        {
            var tp = (double[])t0.Clone(); tp[c] += H;
            var tm = (double[])t0.Clone(); tm[c] -= H;
            var (ip, qp2) = Node(tp);
            var (im, qm2) = Node(tm);
            for (int r = 0; r < 4; r++)
            {
                AssertClose((ip[r] - im[r]) / (2 * H), jg[r, c], $"node dI[{r}]/dV[{c}]", 1e-8);
                AssertClose((qp2[r] - qm2[r]) / (2 * H), jc[r, c], $"node dQ[{r}]/dV[{c}]", 1e-14);
            }
        }
    }

    // ── T3 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T3_WithEveryShortChannelParameterOff_Level3IsLevel1WithoutLambda()
    {
        // This is what says the level-3 terms are DEPARTURES rather than a second model: turn every
        // one of them off and the same square law comes back, exactly.
        //
        // <b>Gamma is zero here as well, and that is not cheating.</b> The BULK-CHARGE FACTOR is
        // itself a level-3 term — it replaces the square law's plain Vds/2 with (1 + fb)·Vds/2, and
        // fb is driven by Gamma rather than by any of the six short-channel parameters. So the two
        // levels genuinely differ on a device that has a body effect and states nothing else, by
        // about fifteen percent of drain current on the parameter set above. That is a real
        // difference between the published laws and not a defect in either; a test that hid it by
        // loosening a tolerance would be asserting the opposite of what is true.
        var l3 = new MosfetLevel3Model(
            vto: 0.68, kp: 6e-5, gamma: 0.0, phi: 0.7,
            eta: 0, theta: 0, kappa: 0, vmax: 0, delta: 0, xj: 0, nsub: 0,
            w: 10e-6, l: 0.8e-6, ld: 0.05e-6, tox: 15e-9);
        var l1 = new MosfetLevel1Model(
            vto: 0.68, kp: 6e-5, gamma: 0.0, phi: 0.7, lambda: 0.0,
            w: 10e-6, l: 0.8e-6, ld: 0.05e-6, tox: 15e-9);

        foreach (var (vd, vg, vb) in new[] { (2.0, 2.5, 0.0), (0.15, 1.5, -1.0), (3.0, 1.0, -2.5), (0.05, 3.3, 0.0) })
        {
            var a = Eval(l1, V(vd, vg, 0.0, vb));
            var b = Eval(l3, V(vd, vg, 0.0, vb));
            AssertClose(a.I[0], b.I[0], $"Id at ({vd},{vg},{vb})", 1e-18);
            AssertClose(a.Dg[0, 3], b.Dg[0, 3], "gm", 1e-18);
            AssertClose(a.Dg[0, 0], b.Dg[0, 0], "gds", 1e-18);
            AssertClose(a.Dg[0, 1], b.Dg[0, 1], "gmbs", 1e-18);
        }

        // …and with a body effect they do NOT agree, which is the other half of the same claim: the
        // bulk-charge factor is a real term, not a rounding difference.
        var l3b = new MosfetLevel3Model(vto: 0.68, kp: 6e-5, gamma: 0.55, phi: 0.7,
            eta: 0, theta: 0, kappa: 0, vmax: 0, delta: 0, xj: 0, nsub: 0,
            w: 10e-6, l: 0.8e-6, ld: 0.05e-6, tox: 15e-9);
        var l1b = new MosfetLevel1Model(vto: 0.68, kp: 6e-5, gamma: 0.55, phi: 0.7, lambda: 0.0,
            w: 10e-6, l: 0.8e-6, ld: 0.05e-6, tox: 15e-9);
        double a3 = Eval(l3b, V(2.0, 2.5, 0.0, 0.0)).I[0];
        double a1 = Eval(l1b, V(2.0, 2.5, 0.0, 0.0)).I[0];
        Assert.True(a3 < 0.95 * a1,
            $"the bulk-charge factor must actually bite: level 1 {a1:E3} A, level 3 {a3:E3} A");
    }

    // ── T4 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T4_EachMechanismIsIndividuallyLive_AndMovesTheCurrentTheRightWay()
    {
        var off = new Func<double, double, double, double, double, double, MosfetLevel3Model>(
            (eta, theta, kappa, vmax, delta, xj) => new MosfetLevel3Model(
                vto: 0.68, kp: 6e-5, gamma: 0.55, phi: 0.7, nsub: 1e16,
                eta: eta, theta: theta, kappa: kappa, vmax: vmax, delta: delta, xj: xj,
                w: 10e-6, l: 0.8e-6, ld: 0.05e-6, tox: 15e-9));

        var bare = off(0, 0, 0, 0, 0, 0);
        double Id(MosfetLevel3Model m, double vd, double vg, double vb)
            => Eval(m, V(vd, vg, 0.0, vb)).I[0];

        const double Vd = 2.5, Vg = 2.0;
        double reference = Id(bare, Vd, Vg, 0.0);
        Assert.True(reference > 0, "the reference device must be conducting in saturation");

        // Eta lowers the threshold with drain bias, so the current RISES.
        Assert.True(Id(off(0.1, 0, 0, 0, 0, 0), Vd, Vg, 0.0) > reference * 1.02, "Eta must raise the current");

        // Theta degrades the mobility, so it FALLS.
        Assert.True(Id(off(0, 0.3, 0, 0, 0, 0), Vd, Vg, 0.0) < reference * 0.98, "Theta must lower it");

        // Vmax caps the carrier velocity, so it FALLS.
        Assert.True(Id(off(0, 0, 0, 5e4, 0, 0), Vd, Vg, 0.0) < reference * 0.98, "Vmax must lower it");

        // Kappa shortens the channel past saturation, so it RISES — and only past saturation.
        Assert.True(Id(off(0, 0, 1.0, 0, 0, 0), Vd, Vg, 0.0) > reference * 1.02, "Kappa must raise it");

        // Xj lets the source and drain take a share of the bulk charge, so the body effect WEAKENS
        // and a back-biased device keeps more of its current.
        const double Vb = -2.5;
        double bodyBare = Id(bare, Vd, Vg, Vb) / Id(bare, Vd, Vg, 0.0);
        double bodyXj   = Id(off(0, 0, 0, 0, 0, 0.2e-6), Vd, Vg, Vb) / Id(off(0, 0, 0, 0, 0, 0.2e-6), Vd, Vg, 0.0);
        Assert.True(bodyXj > bodyBare, $"charge sharing must weaken the body effect: {bodyBare:F4} → {bodyXj:F4}");

        // Delta pushes the other way — a narrow channel needs more gate charge, so the threshold
        // rises and the current falls.
        Assert.True(Id(off(0, 0, 0, 0, 2.0, 0), Vd, Vg, 0.0) < reference * 0.98, "Delta must lower it");
    }

    // ── T5 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T5_VelocitySaturation_MakesTheDeviceSaturateEarlierThanPinchOff()
    {
        // The point of Vmax: carriers stop going faster, so the current stops rising well before the
        // channel would have pinched off. Measured as where gds collapses, which is where saturation
        // begins.
        var withVsat = new MosfetLevel3Model(vto: 0.68, kp: 6e-5, gamma: 0.0, phi: 0.7,
            vmax: 5e4, kappa: 0, w: 10e-6, l: 0.8e-6, tox: 15e-9);
        var without = new MosfetLevel3Model(vto: 0.68, kp: 6e-5, gamma: 0.0, phi: 0.7,
            vmax: 0, kappa: 0, w: 10e-6, l: 0.8e-6, tox: 15e-9);

        const double Vg = 3.0;
        double Knee(MosfetLevel3Model m)
        {
            double gds0 = Eval(m, V(0.02, Vg, 0.0, 0.0)).Dg[0, 0];
            for (double vd = 0.05; vd < 4.0; vd += 0.01)
                if (Eval(m, V(vd, Vg, 0.0, 0.0)).Dg[0, 0] < 0.02 * gds0) return vd;
            return double.NaN;
        }

        double kneeWith = Knee(withVsat), kneeWithout = Knee(without);
        Assert.True(kneeWith < kneeWithout - 0.2,
            $"velocity saturation must move the knee IN: {kneeWithout:F2} V → {kneeWith:F2} V");

        // …and the saturated current is lower for the same reason.
        Assert.True(Eval(withVsat, V(3.5, Vg, 0.0, 0.0)).I[0] < Eval(without, V(3.5, Vg, 0.0, 0.0)).I[0]);
    }

    // ── T6 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T6_ChannelLengthModulation_StaysBounded_AndItsCeilingEngages()
    {
        // Δl cannot reach Leff — the channel would have vanished. Driven hard past saturation with a
        // large Kappa, the published substitution past Leff/2 takes over, and what it leaves is a
        // current growing like √Vds: finite, monotone, with an output conductance that FALLS rather
        // than running away. That falling gds is the evidence the ceiling engaged; without it the
        // multiplier would diverge at a finite drain voltage.
        var m = new MosfetLevel3Model(vto: 0.68, kp: 6e-5, gamma: 0.0, phi: 0.7, nsub: 1e15,
            kappa: 50.0, vmax: 0, w: 10e-6, l: 0.8e-6, tox: 15e-9);

        double last = 0;
        foreach (double vd in Sweep(1.0, 40.0, 0.25))
        {
            var r = Eval(m, V(vd, 3.0, 0.0, 0.0));
            Assert.True(double.IsFinite(r.I[0]), $"the current must stay finite at Vds={vd}");
            Assert.True(r.I[0] >= last - 1e-15, $"…and monotone at Vds={vd}");
            Assert.True(double.IsFinite(r.Dg[0, 0]) && r.Dg[0, 0] >= 0,
                $"gds must stay finite and positive at Vds={vd}");
            last = r.I[0];
        }

        // Past the knee the multiplier is 4·Δl/Leff with Δl ∝ √Vds, so gds falls monotonically.
        double lastGds = double.PositiveInfinity;
        foreach (double vd in Sweep(6.0, 40.0, 0.5))
        {
            double gds = Eval(m, V(vd, 3.0, 0.0, 0.0)).Dg[0, 0];
            Assert.True(gds <= lastGds * 1.001,
                $"gds must fall once the ceiling has engaged: {lastGds:E3} → {gds:E3} at Vds={vd}");
            lastGds = gds;
        }

        // …and the growth really is square-root-like rather than divergent: quadrupling the drain
        // voltage roughly doubles the current, nowhere near an asymptote.
        double at10 = Eval(m, V(10.0, 3.0, 0.0, 0.0)).I[0];
        double at40 = Eval(m, V(40.0, 3.0, 0.0, 0.0)).I[0];
        Assert.True(at40 < 3.0 * at10, $"the current must not run away: {at10:E3} → {at40:E3} A");
    }

    private static IEnumerable<double> Sweep(double from, double to, double step)
    {
        for (double x = from; x <= to; x += step) yield return x;
    }

    // ── T7 ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T7_PChannel_IsTheExactMirrorOfNChannel(double vd, double vg, double vs, double vb)
    {
        var n = Full(MosfetModelBase.Channel4.N);
        var p = Full(MosfetModelBase.Channel4.P);

        var rn = Eval(n, V(vd, vg, vs, vb));
        var rp = Eval(p, V(-vd, -vg, -vs, -vb));

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

    // ── T8 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T8_EveryTemperatureRelationIsInertAtNominal()
    {
        var baseline = Full();
        foreach (double t in new[] { Temperature.NominalC, 90.0, -20.0 })
        {
            var m = Full(tempC: t, tnomC: t);
            foreach (var (vd, vg, vb) in new[] { (2.0, 2.5, 0.0), (0.2, 1.5, -2.0), (-1.0, 3.0, -2.0) })
            {
                var a = Eval(baseline, V(vd, vg, 0.0, vb));
                var b = Eval(m,        V(vd, vg, 0.0, vb));
                Assert.Equal(a.I[0], b.I[0]);
                for (int k = 0; k < a.Q.Length; k++) Assert.Equal(a.Q[k], b.Q[k]);
            }
        }

        Assert.NotEqual(Eval(baseline, V(2.0, 2.5, 0.0, 0.0)).I[0],
                        Eval(Full(tempC: 125.0), V(2.0, 2.5, 0.0, 0.0)).I[0]);
    }

    // ── T9 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_TheFactoryBuildsBothLevels_AndTheyAreDistinctTypes()
    {
        var empty = new Dictionary<string, CircuitRF.Core.Expressions.Value>();

        Assert.IsType<MosfetLevel1Model>(ComponentModelFactory.TryCreate("MOS1_N", empty));
        Assert.IsType<MosfetLevel1Model>(ComponentModelFactory.TryCreate("MOS1_P", empty));
        Assert.IsType<MosfetLevel3Model>(ComponentModelFactory.TryCreate("MOS3_N", empty));
        Assert.IsType<MosfetLevel3Model>(ComponentModelFactory.TryCreate("MOS3_P", empty));
        Assert.Null(ComponentModelFactory.TryCreate("MOS2_N", empty));

        // Both channels really are opposite: an n-channel default is an enhancement device with a
        // positive threshold and a p-channel one with a negative, so neither is on at zero gate.
        Assert.True(((MosfetLevel3Model)ComponentModelFactory.TryCreate("MOS3_N", empty)!).IsNChannel);
        Assert.False(((MosfetLevel3Model)ComponentModelFactory.TryCreate("MOS3_P", empty)!).IsNChannel);
    }

    // ── T10 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>A forward-biased bulk is the one region <see cref="BiasGrid"/> deliberately does not
    /// visit</b>, which is exactly why it needs its own test: every grid above keeps the bulk at or
    /// below the lower channel terminal, so nothing there ever reached the continuation that
    /// <c>√(Phi − Vbs)</c> needs once its argument goes small.
    ///
    /// <para>Level 3 used to CLAMP that square root at its own local floor. The device then froze:
    /// past <c>Vbs = Phi − 1 mV</c> the threshold stopped moving, <c>gmbs</c> read exactly zero and
    /// the drain current sat on a plateau while the transistor was plainly still conducting — and
    /// the gate charge, which the base computes from the same square root through its own
    /// continuation, went on moving, so one device held two answers about one quantity. Both levels
    /// now go through <see cref="MosfetModelBase"/>'s single continuation.</para>
    ///
    /// <para>Three things are asserted, and they are the three a continuation has to get right:
    /// the current keeps RESPONDING (no plateau, no zero derivative), it stays FINITE (level 3
    /// divides by this square root in its bulk-charge factor, so a continuation that reaches zero
    /// puts a pole at an ordinary bias), and it is CONTINUOUS across the changeover in value as
    /// well as slope.</para>
    /// </summary>
    [Fact]
    public void T10_PastTheSurfacePotential_TheBodyEffectStaysLiveAndBounded_InBothLevels()
    {
        const double Phi = 0.7;
        var l3 = new MosfetLevel3Model(vto: 0.68, kp: 6e-5, gamma: 0.55, phi: Phi,
                                       w: 10e-6, l: 0.8e-6, tox: 15e-9);
        var l1 = new MosfetLevel1Model(vto: 0.68, kp: 6e-5, gamma: 0.55, phi: Phi,
                                       w: 10e-6, l: 0.8e-6, tox: 15e-9);

        // Well past the changeover at Phi - 1e-3, and far enough past it that a clamp would have
        // flattened long before the last point.
        double[] sweep = [Phi - 0.05, Phi, Phi + 0.05, Phi + 0.3, Phi + 1.0, Phi + 2.0];

        foreach (var (name, m) in new (string, MosfetModelBase)[] { ("level 3", l3), ("level 1", l1) })
        {
            double previous = double.NaN;
            foreach (double vb in sweep)
            {
                var r = Eval(m, V(vd: 1.0, vg: 2.0, vs: 0.0, vb: vb));
                double id = r.I[0], gmbs = r.Dg[0, 1];

                Assert.True(double.IsFinite(id) && double.IsFinite(gmbs),
                    $"{name} at Vbs={vb}: a continuation must not produce a pole — Id={id}, gmbs={gmbs}");
                Assert.NotEqual(0.0, gmbs);
                if (!double.IsNaN(previous))
                    Assert.True(Math.Abs(id - previous) > 0,
                        $"{name} at Vbs={vb}: the current is on a plateau, so the body effect is frozen");
                previous = id;
            }
        }

        // Continuous in VALUE and in SLOPE across the changeover. A one-sided difference either side
        // of it must agree with the analytic gmbs there — which is what says the two branches meet
        // rather than merely both being finite.
        const double Changeover = Phi - 1e-3, H = 1e-7;
        foreach (var (name, m) in new (string, MosfetModelBase)[] { ("level 3", l3), ("level 1", l1) })
        {
            double below = Eval(m, V(1.0, 2.0, 0.0, Changeover - H)).I[0];
            double above = Eval(m, V(1.0, 2.0, 0.0, Changeover + H)).I[0];
            double gmbs  = Eval(m, V(1.0, 2.0, 0.0, Changeover)).Dg[0, 1];

            AssertClose(gmbs, (above - below) / (2 * H),
                $"{name}: gmbs across the continuation changeover", 1e-9);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertClose(double expected, double actual, string what, double abs)
    {
        double tol = abs + 5e-5 * Math.Abs(expected);
        Assert.True(Math.Abs(expected - actual) <= tol,
            $"{what}: expected {expected:E12}, got {actual:E12} (tol {tol:E3})");
    }
}
