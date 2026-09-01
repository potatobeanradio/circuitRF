using System;
using System.Collections.Generic;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Jfet;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Gate tests for the built-in junction FET.
///
/// <para><b>The central tests are T2 and T3</b> — the analytic Jacobian against central finite
/// differences, per port and then again at the NODE level. The first catches a wrong derivative;
/// the second catches a right derivative written into the wrong column, which the first cannot see
/// because the port voltages are a redundant coordinate system.</para>
///
///   T1 — the drain current matches the Shichman-Hodges closed form in all three regions.
///   T2 — Dg and Dc against central finite differences, per port, over a bias grid.
///   T3 — the NODE-level Jacobian against central finite differences.
///   T4 — p-channel is the exact mirror of n-channel, term for term.
///   T5 — the device is symmetric: reversing the drain and source bias reverses the current.
///   T6 — the gate is TWO junctions: both conduct, both store depletion charge, and the
///        gate-drain half is not a fixed capacitor.
///   T7 — temperature is INERT at nominal, and each coefficient moves its own parameter in the
///        published FORM (Vto additively in V/deg, Beta in percent per degree).
///   T8 — pinch-off is genuinely off: zero current AND zero derivatives, no fudge conductance.
///   T9 — port and terminal structure follows the ohmic parasitics.
///  T10 — this is NOT the Curtice quadratic with the tanh ignored — the two laws disagree at the
///        knee by a margin no coefficient choice can absorb, which is why it is its own component.
/// </summary>
public class JfetModelTests
{
    private static JfetModel Full(JfetModel.Polarity p = JfetModel.Polarity.NChannel,
                                  double tempC = Temperature.NominalC,
                                  double tnomC = Temperature.NominalC,
                                  double rd = 8.0, double rs = 6.0)
        => new(
            polarity: p,
            vto: (double)(int)p * -2.0, beta: 1.2e-3, lambda: 0.03,
            saturationCurrent: 2e-14, emissionCoefficient: 1.05,
            recombinationCurrent: 5e-13, recombinationEmission: 2.0,
            gateSourceCapacitance: 4e-12, gateDrainCapacitance: 1.5e-12,
            junctionPotential: 0.9, gradingCoefficient: 0.5, forwardBiasCapCoeff: 0.5,
            drainResistance: rd, sourceResistance: rs,
            tempC: tempC, tnomC: tnomC);

    /// <summary>
    /// A consistent port-voltage vector from the three terminal voltages. Written out rather than
    /// indexed by magic number because the port order IS the contract with the elaborator.
    /// </summary>
    private static double[] V(double vd, double vg, double vs,
                              double vdInt = double.NaN, double vsInt = double.NaN)
    {
        double di = double.IsNaN(vdInt) ? vd : vdInt;
        double si = double.IsNaN(vsInt) ? vs : vsInt;
        var v = new List<double> { di - si, vg - si, vg - di };
        if (!double.IsNaN(vdInt)) v.Add(vd - vdInt);
        if (!double.IsNaN(vsInt)) v.Add(vs - vsInt);
        return [.. v];
    }

    private static NonlinearResult Eval(ComponentModel m, double[] v)
        => m.Evaluate(new PortVoltages(v));

    /// <summary>
    /// The node map the elaborator builds for a device with no ohmic resistance, as node INDICES
    /// into (drain, gate, source) = (0, 1, 2). Stated here rather than imported so a change to the
    /// elaborator's order has to be made in two places and noticed in one.
    /// </summary>
    private static readonly int[] IntrinsicNodes = [0, 2, 1, 2, 1, 0];

    // ── T1 ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-3.0, 2.0, "cutoff")]
    [InlineData(-1.0, 0.3, "linear")]
    [InlineData(-1.0, 4.0, "saturation")]
    [InlineData( 0.0, 4.0, "saturation, zero gate bias")]
    public void T1_MatchesTheShichmanHodgesClosedForm(double vgs, double vds, string region)
    {
        const double Vto = -2.0, Beta = 1.2e-3, Lambda = 0.03;
        var m = new JfetModel(vto: Vto, beta: Beta, lambda: Lambda);

        double vgt = vgs - Vto;
        double expected =
            vgt <= 0     ? 0.0
            : vds < vgt  ? Beta * vds * (2 * vgt - vds) * (1 + Lambda * vds)
                         : Beta * vgt * vgt * (1 + Lambda * vds);

        var r = Eval(m, V(vd: vds, vg: vgs, vs: 0.0));
        Assert.True(Math.Abs(r.I[0] - expected) <= 1e-18 + 1e-12 * Math.Abs(expected),
            $"{region}: Id = {r.I[0]:E12}, closed form {expected:E12}");
    }

    // ── T2 / T3 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Cutoff, deep linear, the knee, well into saturation, and one REVERSED point so the
    /// drain/source swap is exercised. Two things about the choice of points are load-bearing:
    ///
    /// <para><b>The gate stays at or below both channel terminals.</b> A gate driven forward past
    /// the built-in potential passes a current the node sum would have to cancel eight digits of
    /// before a difference quotient could see the channel — a property of the arithmetic, not of
    /// the model, and the fix is to test the device where it operates rather than to widen the
    /// tolerance until nothing is being asserted.</para>
    ///
    /// <para><b>No point sits exactly on Vds = Vgt.</b> The square law is continuous in value and in
    /// slope across the saturation boundary but NOT in curvature — the second derivative steps by
    /// −2·Beta·(1 + Lambda·Vds) there — so a central difference straddling it returns
    /// <c>gds + h·Δf''/4</c> rather than gds. That is the finite difference measuring the kink, not
    /// the model getting gds wrong; the offsets below (−0.1 rather than 0) keep every point off the
    /// boundary for every Vds in the list. If a later edit puts one back on it, this is what the
    /// failure will look like: a Dg[0,0] wrong by a few parts in ten thousand, shrinking with h.</para>
    /// </summary>
    public static TheoryData<double, double, double> BiasGrid()
    {
        var d = new TheoryData<double, double, double>();
        foreach (double vd in new[] { -1.2, 0.05, 0.5, 2.0, 5.0 })
        foreach (double belowChannel in new[] { -0.1, -0.6, -1.6, -3.1 })
            d.Add(vd, Math.Min(0.0, vd) + belowChannel, 0.0);
        return d;
    }

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T2_EveryJacobianEntry_MatchesCentralFiniteDifferences(double vd, double vg, double vs)
    {
        var m = Full(rd: 0, rs: 0);
        double[] v0 = V(vd, vg, vs);
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
                AssertClose((rp.I[p] - rn.I[p]) / (2 * H), r0.Dg[p, q], $"Dg[{p},{q}] at ({vd},{vg})", 1e-10);
                AssertClose((rp.Q[p] - rn.Q[p]) / (2 * H), r0.Dc[p, q], $"Dc[{p},{q}] at ({vd},{vg})", 1e-16);
            }
        }
    }

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T3_TheNodeLevelJacobian_MatchesCentralFiniteDifferences(double vd, double vg, double vs)
    {
        var m = Full(rd: 0, rs: 0);

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

        var r0 = Eval(m, V(vd, vg, vs));
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

        double[] t0 = [vd, vg, vs];
        const double H = 1e-6;
        for (int c = 0; c < 3; c++)
        {
            var tp = (double[])t0.Clone(); tp[c] += H;
            var tm = (double[])t0.Clone(); tm[c] -= H;
            var (ip, qp2) = Node(tp);
            var (im, qm2) = Node(tm);
            for (int r = 0; r < 3; r++)
            {
                AssertClose((ip[r] - im[r]) / (2 * H), jg[r, c], $"node dI[{r}]/dV[{c}] at ({vd},{vg})", 1e-10);
                AssertClose((qp2[r] - qm2[r]) / (2 * H), jc[r, c], $"node dQ[{r}]/dV[{c}] at ({vd},{vg})", 1e-16);
            }
        }
    }

    // ── T4 ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T4_PChannel_IsTheExactMirrorOfNChannel(double vd, double vg, double vs)
    {
        var n = Full(JfetModel.Polarity.NChannel, rd: 0, rs: 0);
        var p = Full(JfetModel.Polarity.PChannel, rd: 0, rs: 0);

        var rn = Eval(n, V(vd, vg, vs));
        var rp = Eval(p, V(-vd, -vg, -vs));

        for (int k = 0; k < rn.I.Length; k++)
        {
            AssertClose(-rn.I[k], rp.I[k], $"I[{k}] mirrored", 1e-20);
            AssertClose(-rn.Q[k], rp.Q[k], $"Q[{k}] mirrored", 1e-22);
            for (int j = 0; j < rn.I.Length; j++)
            {
                // The Jacobian is UNCHANGED under the mirror: the sign appears once on the current
                // and once on the voltage, and the two cancel.
                AssertClose(rn.Dg[k, j], rp.Dg[k, j], $"Dg[{k},{j}] mirrored", 1e-18);
                AssertClose(rn.Dc[k, j], rp.Dc[k, j], $"Dc[{k},{j}] mirrored", 1e-20);
            }
        }
    }

    // ── T5 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T5_TheDeviceIsSymmetric_ReversingTheBiasReversesTheCurrent()
    {
        // Cgs == Cgd, so the two orientations really are the same device seen from the other side.
        // With them unequal they are not, which is a real asymmetry the model is entitled to and
        // would make this test measure that instead.
        var m = new JfetModel(vto: -2.0, beta: 1.2e-3, gateSourceCapacitance: 3e-12,
                              gateDrainCapacitance: 3e-12, junctionPotential: 0.9);

        var fwd = Eval(m, V(vd: 2.0, vg: -0.5, vs: 0.0));
        var rev = Eval(m, V(vd: 0.0, vg: -0.5, vs: 2.0));

        Assert.True(fwd.I[0] > 1e-6, "the forward device must actually be conducting");
        AssertClose(-fwd.I[0], rev.I[0], "reverse channel current", 1e-15);

        // And the gate junctions have simply swapped ends.
        AssertClose(fwd.Q[1], rev.Q[2], "Qgs forward vs Qgd reverse", 1e-22);
        AssertClose(fwd.I[1], rev.I[2], "Igs forward vs Igd reverse", 1e-22);
    }

    // ── T6 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T6_TheGateIsTwoJunctions_BothConducting_AndBothBiasDependent()
    {
        var m = Full(rd: 0, rs: 0);

        // Forward-bias each half in turn: the OTHER half must stay reverse-biased and quiet, which
        // is what says they are two independent junctions rather than one lumped gate current.
        var gs = Eval(m, V(vd: 3.0, vg: 0.6, vs: 0.0));
        Assert.True(gs.I[1] > 1e-6, "the gate-source junction must conduct when forward-biased");
        Assert.True(Math.Abs(gs.I[2]) < 1e-11, "the gate-drain junction is reverse-biased here");

        var gd = Eval(m, V(vd: -3.0, vg: -2.4, vs: 0.0));
        Assert.True(gd.I[2] > 1e-6, "the gate-drain junction must conduct when forward-biased");

        // Depletion charge, not a fixed capacitor: the capacitance has to MOVE with bias. A
        // constant Cgd would give the same number at both biases and would pass every other test
        // in this file.
        double cgdNear = Eval(m, V(vd: 0.2, vg: -0.2, vs: 0.0)).Dc[2, 2];
        double cgdFar  = Eval(m, V(vd: 6.0, vg: -3.0, vs: 0.0)).Dc[2, 2];
        Assert.True(cgdNear > 2 * cgdFar,
            $"Cgd must fall as the junction is reverse-biased: {cgdNear:E3} near, {cgdFar:E3} far");
    }

    // ── T7 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T7_TemperatureIsInertAtNominal_AndEachCoefficientMovesItsOwnParameter()
    {
        // Temp == Tnom is EXACTLY the identity for every relation, however far the pair sits from
        // nominal. Only kT/q still moves, so the CHANNEL current — which has no kT/q in it — is
        // bit-identical, and so is every depletion charge.
        var baseline = Full();
        foreach (double t in new[] { Temperature.NominalC, 85.0, -40.0 })
        {
            var m = Full(tempC: t, tnomC: t);
            var bias = V(2.0, -0.5, 0.0, vdInt: 1.98, vsInt: 0.01);
            var a = Eval(baseline, bias);
            var b = Eval(m, bias);
            Assert.Equal(a.I[0], b.I[0]);
            for (int k = 0; k < a.Q.Length; k++) Assert.Equal(a.Q[k], b.Q[k]);
        }

        const double DT = 100.0;
        double hot = Temperature.NominalC + DT;

        // Vtotc is ADDITIVE in volts per degree. Checked by showing the shifted device is identical
        // to one built with the shifted threshold outright — not merely "it changed".
        var shifted = new JfetModel(vto: -2.0, beta: 1.2e-3, tempC: hot, vtoTempCoefficient: 2e-3);
        var exact   = new JfetModel(vto: -2.0 + 2e-3 * DT, beta: 1.2e-3);
        AssertClose(Eval(exact, V(3.0, -0.5, 0.0)).I[0], Eval(shifted, V(3.0, -0.5, 0.0)).I[0],
            "Vtotc is additive", 1e-15);

        // Betatce is in PERCENT per degree — 1.01^(tc·ΔT), NOT 1 + 0.01·tc·ΔT. The two diverge as
        // soon as ΔT is more than a few tens of degrees, which is exactly this range.
        var scaled = new JfetModel(vto: -2.0, beta: 1.2e-3, tempC: hot, betaTempCoefficient: -0.5);
        var exactB = new JfetModel(vto: -2.0, beta: 1.2e-3 * Math.Pow(1.01, -0.5 * DT));
        AssertClose(Eval(exactB, V(3.0, -0.5, 0.0)).I[0], Eval(scaled, V(3.0, -0.5, 0.0)).I[0],
            "Betatce is percent per degree", 1e-15);
        Assert.NotEqual(1.2e-3 * (1 + 0.01 * -0.5 * DT), 1.2e-3 * Math.Pow(1.01, -0.5 * DT), 8);
    }

    // ── T8 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T8_PinchOffIsGenuinelyOff_NoFudgeConductance()
    {
        var m = Full(rd: 0, rs: 0);
        var r = Eval(m, V(vd: 4.0, vg: -3.0, vs: 0.0));
        Assert.Equal(0.0, r.I[0]);
        Assert.Equal(0.0, r.Dg[0, 0]);
        Assert.Equal(0.0, r.Dg[0, 1]);
    }

    // ── T9 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_PortAndTerminalStructure_FollowsTheOhmicParasitics()
    {
        var bare = Full(rd: 0, rs: 0);
        Assert.Equal(3, bare.PortCount);
        Assert.Equal(0, bare.InternalNodeCount);
        Assert.Equal(["ids", "igs", "igd"], bare.TerminalNames);

        Assert.Equal("drain",  Full(rd: 8, rs: 0).TerminalNames[3]);
        Assert.Equal("source", Full(rd: 0, rs: 6).TerminalNames[3]);

        var both = Full();
        Assert.Equal(5, both.PortCount);
        Assert.Equal(["ids", "igs", "igd", "drain", "source"], both.TerminalNames);

        var r = Eval(both, V(2.0, -0.5, 0.0, vdInt: 1.92, vsInt: 0.03));
        AssertClose(0.08 / 8.0, r.I[3], "Rd current", 1e-14);
        AssertClose(-0.03 / 6.0, r.I[4], "Rs current", 1e-14);
    }

    // ── T10 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The JFET is not the Curtice quadratic with the tanh ignored.</b> That is the
    /// nearest-native temptation the import layer used to refuse an NJF card over, and this is the
    /// measurement that says the refusal was right: matched at pinch-off and matched deep in
    /// saturation, the two laws still disagree by tens of percent through the knee, because the
    /// knee is where they differ by construction. No choice of Alpha closes it — Alpha moves the
    /// whole tanh, which is what the sweep below shows.
    /// </summary>
    [Fact]
    public void T10_TheSquareLawKnee_IsNotAnyCurticeTanhKnee()
    {
        const double Vto = -2.0, Beta = 1.2e-3, Vgs = -0.5;
        var jfet = new JfetModel(vto: Vto, beta: Beta);
        double vgt = Vgs - Vto;

        // The Curtice quadratic matched to the same saturated current: Beta_c·Vgt² = Beta·Vgt², so
        // the two are identical in saturation for every Alpha. The knee is the whole difference.
        double Curtice(double vds, double alpha) => Beta * vgt * vgt * Math.Tanh(alpha * vds);

        double worstOverAlpha = double.PositiveInfinity;
        foreach (double alpha in new[] { 0.5, 1.0, 1.5, 2.0, 3.0, 5.0, 10.0 })
        {
            double worst = 0.0;
            foreach (double vds in new[] { 0.15, 0.3, 0.5, 0.75, 1.0, 1.25 })
            {
                double idJ = Eval(jfet, V(vd: vds, vg: Vgs, vs: 0.0)).I[0];
                worst = Math.Max(worst, Math.Abs(idJ - Curtice(vds, alpha)) / Math.Max(idJ, 1e-12));
            }
            worstOverAlpha = Math.Min(worstOverAlpha, worst);
        }

        Assert.True(worstOverAlpha > 0.10,
            $"the best Alpha still misfits the square-law knee by {worstOverAlpha:P1}; if this ever "
            + "falls below 10% the refusal that sent NJF cards here would need revisiting");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertClose(double expected, double actual, string what, double abs)
    {
        double tol = abs + 2e-5 * Math.Abs(expected);
        Assert.True(Math.Abs(expected - actual) <= tol,
            $"{what}: expected {expected:E12}, got {actual:E12} (tol {tol:E3})");
    }
}
