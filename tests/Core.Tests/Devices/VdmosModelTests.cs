using System;
using System.Collections.Generic;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Mos;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Gate tests for the built-in vertical power MOSFET.
///
///   T1 — the analytic Jacobian against central finite differences, per port, over a bias grid.
///   T2 — the NODE-level Jacobian against central finite differences.
///   T3 — the BODY DIODE is a real element: it blocks one way, conducts the other, and carries the
///        current in the third quadrant even with the gate off.
///   T4 — third-quadrant conduction with the gate ON: the channel carries the current, and the body
///        diode does not have to. This is synchronous rectification, which is what the part is for.
///   T5 — avalanche breakdown is modelled when Bv is stated and NOT modelled when it is zero.
///   T6 — the gate-drain capacitance collapses between its two plateaus, monotonically, and the
///        charge is its exact integral — which is what makes it usable in harmonic balance.
///   T7 — p-channel is the exact mirror of n-channel.
///   T8 — temperature: inert at nominal; Vtotc additive, Kptc in percent per degree; and with no
///        Kptc the mobility falls as T^−1.5, so on-resistance RISES with temperature.
///   T9 — port and terminal structure follows the three ohmic parasitics.
///  T10 — Cgdmin above Cgdmax is read as a constant capacitance, not as a rising one.
/// </summary>
public class VdmosModelTests
{
    private static VdmosModel Full(
        VdmosModel.Channel ch = VdmosModel.Channel.N,
        double tempC = Temperature.NominalC, double tnomC = Temperature.NominalC,
        double rg = 2.0, double rd = 0.03, double rs = 0.01,
        double bv = 60.0, double kptc = 0.0)
        => new(
            channel: ch,
            vto: (double)(int)ch * 3.2, kp: 12.0, lambda: 0.01,
            drainSourceResistance: 1e7,
            bodySaturationCurrent: 5e-13, bodyEmission: 1.05,
            breakdownVoltage: bv, breakdownCurrent: 1e-3, breakdownEmission: 1.2,
            transitTime: 8e-8,
            bodyZeroBiasCapacitance: 900e-12, bodyJunctionPotential: 0.85,
            bodyGradingCoefficient: 0.45, forwardBiasCapCoeff: 0.5,
            gateSourceCapacitance: 1800e-12,
            gateDrainCapacitanceMax: 1500e-12, gateDrainCapacitanceMin: 25e-12,
            gateDrainTransitionVoltage: 1.5,
            gateResistance: rg, drainResistance: rd, sourceResistance: rs,
            tempC: tempC, tnomC: tnomC, kpTempCoefficient: kptc);

    /// <summary>
    /// A consistent port-voltage vector from the three terminal voltages. Port order IS the contract
    /// with the elaborator, so it is written out rather than indexed by magic number.
    /// </summary>
    private static double[] V(double vd, double vg, double vs,
                              double vgInt = double.NaN, double vdInt = double.NaN,
                              double vsInt = double.NaN)
    {
        double gi = double.IsNaN(vgInt) ? vg : vgInt;
        double di = double.IsNaN(vdInt) ? vd : vdInt;
        double si = double.IsNaN(vsInt) ? vs : vsInt;
        var v = new List<double>
        {
            di - si,   // 0 channel + Rds
            si - di,   // 1 body diode
            gi - si,   // 2 Cgs
            gi - di,   // 3 Cgd
        };
        if (!double.IsNaN(vgInt)) v.Add(vg - vgInt);
        if (!double.IsNaN(vdInt)) v.Add(vd - vdInt);
        if (!double.IsNaN(vsInt)) v.Add(vs - vsInt);
        return [.. v];
    }

    private static NonlinearResult Eval(ComponentModel m, double[] v)
        => m.Evaluate(new PortVoltages(v));

    /// <summary>The elaborator's node map for a device with no ohmic resistance, as indices into
    /// (drain, gate, source) = (0, 1, 2). Stated here so a change has to be made twice and noticed once.</summary>
    private static readonly int[] IntrinsicNodes = [0, 2, 2, 0, 1, 2, 1, 0];

    /// <summary>
    /// Off, at threshold, in the linear region, in saturation, and in the THIRD QUADRANT (negative
    /// Vds), which for this part is the operating point that matters most. No point sits exactly on
    /// Vds = Vgt, where the square law's second derivative steps and a central difference would be
    /// measuring the kink rather than gds.
    /// </summary>
    public static TheoryData<double, double> BiasGrid()
    {
        var d = new TheoryData<double, double>();
        foreach (double vd in new[] { -0.9, -0.15, 0.1, 1.7, 12.0, 40.0 })
        foreach (double vg in new[] { 0.0, 2.9, 4.15, 9.3 })
            d.Add(vd, vg);
        return d;
    }

    // ── T1 ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T1_EveryJacobianEntry_MatchesCentralFiniteDifferences(double vd, double vg)
    {
        var m = Full(rg: 0, rd: 0, rs: 0);
        double[] v0 = V(vd, vg, 0.0);
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
                AssertClose((rp.I[p] - rn.I[p]) / (2 * H), r0.Dg[p, q], $"Dg[{p},{q}] at ({vd},{vg})", 1e-8);
                AssertClose((rp.Q[p] - rn.Q[p]) / (2 * H), r0.Dc[p, q], $"Dc[{p},{q}] at ({vd},{vg})", 1e-14);
            }
        }
    }

    // ── T2 ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T2_TheNodeLevelJacobian_MatchesCentralFiniteDifferences(double vd, double vg)
    {
        var m = Full(rg: 0, rd: 0, rs: 0);

        (double[] I, double[] Q) Node(double[] t)
        {
            var r = Eval(m, V(t[0], t[1], t[2]));
            var ni = new double[3];
            var nq = new double[3];
            for (int p = 0; p < r.I.Length; p++)
            {
                ni[IntrinsicNodes[2 * p]] += r.I[p]; ni[IntrinsicNodes[2 * p + 1]] -= r.I[p];
                nq[IntrinsicNodes[2 * p]] += r.Q[p]; nq[IntrinsicNodes[2 * p + 1]] -= r.Q[p];
            }
            return (ni, nq);
        }

        var r0 = Eval(m, V(vd, vg, 0.0));
        int P = r0.I.Length;
        var jg = new double[3, 3];
        var jc = new double[3, 3];
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

        double[] t0 = [vd, vg, 0.0];
        const double H = 1e-6;
        for (int c = 0; c < 3; c++)
        {
            var tp = (double[])t0.Clone(); tp[c] += H;
            var tm = (double[])t0.Clone(); tm[c] -= H;
            var (ip, qp2) = Node(tp);
            var (im, qm2) = Node(tm);
            for (int r = 0; r < 3; r++)
            {
                AssertClose((ip[r] - im[r]) / (2 * H), jg[r, c], $"node dI[{r}]/dV[{c}] at ({vd},{vg})", 1e-8);
                AssertClose((qp2[r] - qm2[r]) / (2 * H), jc[r, c], $"node dQ[{r}]/dV[{c}] at ({vd},{vg})", 1e-14);
            }
        }
    }

    // ── T3 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T3_TheBodyDiode_BlocksForward_AndCarriesTheCurrentInTheThirdQuadrant()
    {
        var m = Full(rg: 0, rd: 0, rs: 0);

        // Forward blocking: drain positive, gate off. The body diode is reverse-biased and passes
        // nothing worth naming — the only current is the stated Rds leakage.
        var blocking = Eval(m, V(vd: 40.0, vg: 0.0, vs: 0.0));
        Assert.True(Math.Abs(blocking.I[1]) < 1e-9, $"body diode must block: {blocking.I[1]:E3} A");
        Assert.Equal(0.0, blocking.Dg[0, 2]);                        // channel off: no gm
        AssertClose(40.0 / 1e7, blocking.I[0], "Rds leakage", 1e-15);

        // Third quadrant, gate OFF: the body diode is forward-biased and takes the whole current.
        var freewheel = Eval(m, V(vd: -0.9, vg: 0.0, vs: 0.0));
        Assert.True(freewheel.I[1] > 1e-3, $"body diode must conduct: {freewheel.I[1]:E3} A");
        Assert.Equal(0.0, freewheel.Dg[0, 2]);
    }

    // ── T4 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T4_ThirdQuadrantWithTheGateOn_IsCarriedByTheChannel_NotOnlyTheBodyDiode()
    {
        // Synchronous rectification: the reason this part is bought. With the gate on, the channel
        // conducts in reverse and shunts the body diode, so the drop across the device is I·Rds(on)
        // rather than a diode drop. A model that evaluated its forward law at a negative Vds would
        // get this backwards while still solving.
        var m = Full(rg: 0, rd: 0, rs: 0);

        var on  = Eval(m, V(vd: -0.15, vg: 9.3, vs: 0.0));
        var off = Eval(m, V(vd: -0.15, vg: 0.0, vs: 0.0));

        Assert.True(on.I[0] < -1e-3, $"the channel must conduct in reverse when on: {on.I[0]:E3} A");
        Assert.True(Math.Abs(off.I[0]) < 1e-6, "…and not when off");

        // At this small a reverse drop the body diode is barely started, so the channel really is
        // the path — which is the whole claim.
        Assert.True(Math.Abs(on.I[0]) > 10 * Math.Abs(on.I[1]),
            $"channel {on.I[0]:E3} A should dominate the body diode {on.I[1]:E3} A here");
    }

    // ── T5 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T5_Avalanche_IsModelledWhenBvIsStated_AndAbsentWhenItIsZero()
    {
        var rated  = Full(rg: 0, rd: 0, rs: 0, bv: 60.0);
        var noneBv = Full(rg: 0, rd: 0, rs: 0, bv: 0.0);

        // Above the rating the body diode avalanches — a real, RATED mode for this part.
        var over = Eval(rated, V(vd: 80.0, vg: 0.0, vs: 0.0));
        Assert.True(over.I[1] < -1e-3, $"avalanche must draw current: {over.I[1]:E3} A");

        // Below it, nothing.
        var under = Eval(rated, V(vd: 40.0, vg: 0.0, vs: 0.0));
        Assert.True(Math.Abs(under.I[1]) < 1e-9);

        // Bv = 0 means NOT MODELLED, never "breaks down at 0 V" — the same rule the diode's own Bv
        // follows, and the failure mode if it were read the other way is a device that avalanches
        // at every bias.
        Assert.True(Math.Abs(Eval(noneBv, V(vd: 80.0, vg: 0.0, vs: 0.0)).I[1]) < 1e-9);
    }

    // ── T6 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T6_TheGateDrainCapacitance_CollapsesBetweenItsPlateaus_AndTheChargeIsItsIntegral()
    {
        var m = Full(rg: 0, rd: 0, rs: 0);
        const double Max = 1500e-12, Min = 25e-12;

        // Gate well above the drain: the drift region is accumulated, so the bare oxide value.
        double accumulated = Eval(m, V(vd: -20.0, vg: 0.0, vs: 0.0)).Dc[3, 3];
        // Drain well above the gate: the drift region is depleted, so the small value.
        double depleted = Eval(m, V(vd: 40.0, vg: 0.0, vs: 0.0)).Dc[3, 3];

        AssertRel(Max, accumulated, "Cgd accumulated", 1e-6);
        AssertRel(Min, depleted,    "Cgd depleted",    1e-6);
        Assert.True(accumulated > 50 * depleted, "the collapse is what makes this device switch");

        // Monotone in between — a non-monotone Cgd would give a gate-charge curve with a fold in it,
        // which Newton would find.
        double last = double.PositiveInfinity;
        for (double vgd = 20.0; vgd >= -20.0; vgd -= 1.0)
        {
            double c = Eval(m, V(vd: -vgd, vg: 0.0, vs: 0.0)).Dc[3, 3];
            Assert.True(c <= last + 1e-18, $"Cgd must fall monotonically with drain bias at Vgd={vgd}");
            last = c;
        }

        // And the charge really is the integral of that capacitance — which is the property that
        // makes it usable in harmonic balance at all. Trapezoid over a fine grid, against the
        // model's own Q difference.
        double Q(double vgd) => Eval(m, V(vd: -vgd, vg: 0.0, vs: 0.0)).Q[3];
        double C(double vgd) => Eval(m, V(vd: -vgd, vg: 0.0, vs: 0.0)).Dc[3, 3];
        const int N = 20000;
        double a = -12.0, b = 12.0, h = (b - a) / N, integral = 0.5 * (C(a) + C(b));
        for (int k = 1; k < N; k++) integral += C(a + k * h);
        integral *= h;
        AssertRel(integral, Q(b) - Q(a), "Qgd is the integral of Cgd", 1e-7);
    }

    // ── T7 ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T7_PChannel_IsTheExactMirrorOfNChannel(double vd, double vg)
    {
        var n = Full(VdmosModel.Channel.N, rg: 0, rd: 0, rs: 0);
        var p = Full(VdmosModel.Channel.P, rg: 0, rd: 0, rs: 0);

        var rn = Eval(n, V(vd, vg, 0.0));
        var rp = Eval(p, V(-vd, -vg, 0.0));

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
    public void T8_Temperature_IsInertAtNominal_AndOnResistanceRisesWithIt()
    {
        var baseline = Full();
        var bias = V(2.0, 9.3, 0.0, vgInt: 1.99, vdInt: 1.98, vsInt: 0.01);
        foreach (double t in new[] { Temperature.NominalC, 105.0, -40.0 })
        {
            var m = Full(tempC: t, tnomC: t);
            var a = Eval(baseline, bias);
            var b = Eval(m, bias);
            Assert.Equal(a.I[0], b.I[0]);                          // channel current
            Assert.Equal(a.Q[3], b.Q[3]);                          // gate-drain charge
        }

        // With no Kptc stated, mobility falls as T^−1.5, so the on-state current falls and the
        // on-resistance rises. That is why paralleling these parts works, and getting the sign
        // wrong would make a model that predicts current hogging where hardware self-balances.
        // Measured on the INTRINSIC device: with Rg/Rd/Rs in play the port vector would also have
        // to state three internal node voltages, and the claim here is about the channel.
        var hot  = Full(tempC: 125.0, rg: 0, rd: 0, rs: 0);
        var cold = Full(tempC: Temperature.NominalC, rg: 0, rd: 0, rs: 0);
        var onBias = V(0.5, 9.3, 0.0);
        Assert.True(Math.Abs(Eval(hot, onBias).I[0]) < Math.Abs(Eval(cold, onBias).I[0]),
            "on-state current must FALL as the device heats");

        const double DT = 100.0;
        double hotC = Temperature.NominalC + DT;

        // Vtotc is ADDITIVE in volts per degree. Both sides sit at the SAME temperature, so the
        // mobility relation applies identically to each and the only difference left is the
        // threshold — otherwise this would be measuring T^-1.5 and calling it Vtotc.
        var shifted = new VdmosModel(vto: 3.2, kp: 12.0, tempC: hotC, vtoTempCoefficient: -6e-3);
        var exact   = new VdmosModel(vto: 3.2 - 6e-3 * DT, kp: 12.0, tempC: hotC);
        AssertClose(Eval(exact, V(5.0, 6.0, 0.0)).I[0], Eval(shifted, V(5.0, 6.0, 0.0)).I[0],
            "Vtotc is additive", 1e-12);

        // Kptc is in PERCENT per degree — 1.01^(tc·ΔT), which is not 1 + 0.01·tc·ΔT once ΔT is more
        // than a few tens of degrees. A STATED coefficient replaces the T^-1.5 mobility relation
        // rather than multiplying it: the card is describing the same physics its own way, and
        // applying both would count it twice.
        var scaled  = new VdmosModel(vto: 3.2, kp: 12.0, tempC: hotC, kpTempCoefficient: -0.4);
        var exactKp = new VdmosModel(vto: 3.2, kp: 12.0 * Math.Pow(1.01, -0.4 * DT));
        Assert.NotEqual(12.0 * (1 + 0.01 * -0.4 * DT), 12.0 * Math.Pow(1.01, -0.4 * DT), 4);
        AssertClose(Eval(exactKp, V(5.0, 6.0, 0.0)).I[0], Eval(scaled, V(5.0, 6.0, 0.0)).I[0],
            "Kptc is percent per degree", 1e-12);
    }

    // ── T9 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_PortAndTerminalStructure_FollowsTheThreeOhmicParasitics()
    {
        var bare = Full(rg: 0, rd: 0, rs: 0);
        Assert.Equal(4, bare.PortCount);
        Assert.Equal(0, bare.InternalNodeCount);
        Assert.Equal(["ids", "body", "qgs", "qgd"], bare.TerminalNames);

        // The order is gate, drain, source — and only the ones that exist appear. The elaborator
        // builds its node list from the same three flags.
        Assert.Equal("gate",   Full(rg: 2, rd: 0, rs: 0).TerminalNames[4]);
        Assert.Equal("drain",  Full(rg: 0, rd: 1, rs: 0).TerminalNames[4]);
        Assert.Equal("source", Full(rg: 0, rd: 0, rs: 1).TerminalNames[4]);
        Assert.Equal(["ids", "body", "qgs", "qgd", "drain", "source"],
                     Full(rg: 0, rd: 1, rs: 1).TerminalNames);

        var both = Full();
        Assert.Equal(7, both.PortCount);
        var r = Eval(both, V(2.0, 9.3, 0.0, vgInt: 9.0, vdInt: 1.9, vsInt: 0.02));
        AssertClose(0.3 / 2.0,   r.I[4], "Rg current", 1e-12);
        AssertClose(0.1 / 0.03,  r.I[5], "Rd current", 1e-12);
        AssertClose(-0.02 / 0.01, r.I[6], "Rs current", 1e-12);
    }

    // ── T10 ───────────────────────────────────────────────────────────────────

    [Fact]
    public void T10_CgdminAboveCgdmax_IsReadAsAConstant_NotAsARisingCapacitance()
    {
        // A card stating only one of the two, or stating them the wrong way round, means a constant
        // gate-drain capacitance. Taking it literally would make Cgd RISE with drain bias — the
        // wrong direction, and it would still simulate.
        var swapped = new VdmosModel(vto: 3.2, kp: 12.0,
                                     gateDrainCapacitanceMax: 25e-12, gateDrainCapacitanceMin: 1500e-12);
        double low  = Eval(swapped, V(vd: -20.0, vg: 0.0, vs: 0.0)).Dc[3, 3];
        double high = Eval(swapped, V(vd:  40.0, vg: 0.0, vs: 0.0)).Dc[3, 3];
        Assert.Equal(low, high);
        AssertRel(25e-12, low, "the smaller of the two, held constant", 1e-9);

        var onlyMax = new VdmosModel(vto: 3.2, kp: 12.0, gateDrainCapacitanceMax: 900e-12);
        Assert.Equal(Eval(onlyMax, V(-20.0, 0.0, 0.0)).Dc[3, 3],
                     Eval(onlyMax, V( 40.0, 0.0, 0.0)).Dc[3, 3]);
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
