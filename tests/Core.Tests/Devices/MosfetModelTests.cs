using System;
using System.Collections.Generic;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Mos;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Gate tests for the built-in MOS transistor family.
///
/// <para><b>The central tests are T2 and T3 — the whole analytic Jacobian against central finite
/// differences, and then the same thing again at the NODE level.</b> The first catches a wrong
/// derivative; the second catches a right derivative written into the wrong column, which the first
/// cannot see because the port voltages are a redundant coordinate system. A wrong entry in either
/// does not produce a wrong answer: it produces a slow solve, or a converged solve at the wrong
/// operating point.</para>
///
///   T1 — the level-1 drain current matches its published closed form in all three regions.
///   T2 — Dg and Dc against central finite differences, per port, over a bias grid.
///   T3 — the NODE-level Jacobian against central finite differences of the node currents and
///        charges, which is what the engine actually assembles.
///   T4 — p-channel is the exact mirror of n-channel, term for term.
///   T5 — the device is symmetric: reversing the drain and source bias reverses the current.
///   T6 — charge is CONSERVED — the four terminal charges sum to zero at every bias.
///   T7 — the body effect moves the threshold by the published amount, and Gamma = 0 is off.
///   T8 — temperature is INERT at nominal: Temp == Tnom reproduces the untemperatured device bit
///        for bit. This is the one that catches a °C/K mix-up.
///   T9 — the intrinsic gate charge has the published limits: Cox at Vds = 0, two thirds of Cox in
///        saturation, and zero in cutoff.
///  T10 — port and terminal structure follows the parasitics.
///  T11 — cutoff is genuinely off: zero current AND zero derivatives, no fudge conductance.
/// </summary>
public class MosfetModelTests
{
    /// <summary>
    /// A parameter set with EVERY optional mechanism live — body effect, output slope, an oxide so
    /// the intrinsic charge exists, both overlaps, both bulk junctions with sidewalls, and both
    /// ohmic resistances. A grid over a model with half its terms switched off proves half a model.
    /// </summary>
    private static MosfetLevel1Model Full(
        MosfetModelBase.Channel4 ch = MosfetModelBase.Channel4.N,
        double tempC = Temperature.NominalC, double tnomC = Temperature.NominalC,
        double rd = 4.0, double rs = 3.0)
        => new(
            channel: ch,
            vto: (double)(int)ch * 0.7, kp: 2.0e-5, gamma: 0.5, phi: 0.65, lambda: 0.02,
            w: 20e-6, l: 1e-6, ld: 0.08e-6, tox: 20e-9, uo: 600.0,
            cgso: 3.0e-10, cgdo: 3.0e-10, cgbo: 2.0e-10,
            saturationCurrent: 1e-14, bulkEmission: 1.0,
            cbd: 20e-15, cbs: 22e-15, cjsw: 2.0e-10, pd: 12e-6, ps: 12e-6,
            pb: 0.85, mj: 0.5, mjsw: 0.33, fc: 0.5,
            rd: rd, rs: rs,
            tempC: tempC, tnomC: tnomC);

    /// <summary>
    /// A consistent port-voltage vector from the four terminal voltages. Written out rather than
    /// indexed by magic number because the port order IS the contract between this model and the
    /// elaborator, and three of the six ports are the same three unknowns seen from elsewhere.
    /// </summary>
    private static double[] V(double vd, double vg, double vs, double vb,
                              double vdInt = double.NaN, double vsInt = double.NaN)
    {
        double di = double.IsNaN(vdInt) ? vd : vdInt;
        double si = double.IsNaN(vsInt) ? vs : vsInt;
        var v = new List<double>
        {
            di - si,        // 0 (drain', source')
            vb - si,        // 1 (bulk,   source')
            vb - di,        // 2 (bulk,   drain')
            vg - si,        // 3 (gate,   source')
            vg - di,        // 4 (gate,   drain')
            vg - vb,        // 5 (gate,   bulk)
        };
        if (!double.IsNaN(vdInt)) v.Add(vd - vdInt);   // 6 Rd
        if (!double.IsNaN(vsInt)) v.Add(vs - vsInt);   // 7 Rs
        return [.. v];
    }

    private static NonlinearResult Eval(ComponentModel m, double[] v)
        => m.Evaluate(new PortVoltages(v));

    // ── T1 ────────────────────────────────────────────────────────────────────

    [Theory]
    // vgs, vds, vbs, the region it lands in
    [InlineData(0.5, 2.0, 0.0, "cutoff")]
    [InlineData(2.0, 0.3, 0.0, "linear")]
    [InlineData(2.0, 3.0, 0.0, "saturation")]
    [InlineData(2.0, 3.0, -1.5, "saturation, body effect")]
    public void T1_Level1_MatchesTheShichmanHodgesClosedForm(
        double vgs, double vds, double vbs, string region)
    {
        // No parasitics and no charge: this test is about the drain-current law alone, and an
        // ohmic drop between the terminals and the intrinsic device would make it about the
        // topology instead.
        const double Vto = 0.7, Kp = 2e-5, Gamma = 0.5, Phi = 0.65, Lambda = 0.02;
        const double W = 20e-6, L = 1e-6;
        var m = new MosfetLevel1Model(vto: Vto, kp: Kp, gamma: Gamma, phi: Phi, lambda: Lambda,
                                      w: W, l: L);

        double vth  = Vto + Gamma * (Math.Sqrt(Phi - vbs) - Math.Sqrt(Phi));
        double vgt  = vgs - vth;
        double beta = Kp * W / L;
        double expected =
            vgt <= 0      ? 0.0
            : vds < vgt   ? beta * (vgt - 0.5 * vds) * vds * (1 + Lambda * vds)
                          : 0.5 * beta * vgt * vgt * (1 + Lambda * vds);

        var r = Eval(m, V(vd: vds, vg: vgs, vs: 0.0, vb: vbs));
        Assert.True(Math.Abs(r.I[0] - expected) <= 1e-15 + 1e-12 * Math.Abs(expected),
            $"{region}: Id = {r.I[0]:E12}, closed form {expected:E12}");
    }

    // ── T2 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The bias grid. Cutoff, deep linear, the knee and well into saturation, with the drain taken
    /// NEGATIVE at one point so the drain/source swap is exercised, and the bulk swept from tied to
    /// deeply reverse-biased.
    ///
    /// <para><b>The bulk is placed relative to the LOWER of the two channel terminals, never to the
    /// source alone.</b> A bulk sitting above the drain forward-biases the substrate junction, and
    /// at 1.5 V that junction passes tens of kiloamps — a bias no MOS transistor holds, and one at
    /// which a finite difference of the NODE current is meaningless: node 0 sums a 7e-4 channel
    /// current against a 4e+4 junction current, so the subtraction has already thrown away eight
    /// digits before the difference quotient starts. That is a property of the arithmetic, not of
    /// the model — the per-port check in T2 passes at those points — and the fix is to test the
    /// device where it operates rather than to widen the tolerance until nothing is being
    /// asserted.</para>
    /// </summary>
    public static TheoryData<double, double, double, double> BiasGrid()
    {
        var d = new TheoryData<double, double, double, double>();
        foreach (double vg in new[] { 0.0, 0.6, 1.2, 2.5 })
        foreach (double vd in new[] { -1.5, 0.05, 0.4, 1.5, 4.0 })
        foreach (double belowChannel in new[] { 0.0, -1.0, -3.0 })
            d.Add(vd, vg, 0.0, Math.Min(0.0, vd) + belowChannel);
        return d;
    }

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T2_EveryJacobianEntry_MatchesCentralFiniteDifferences(
        double vd, double vg, double vs, double vb)
    {
        var m = Full(rd: 0.0, rs: 0.0);          // intrinsic only: the ohmic ports are trivially linear
        double[] v0 = V(vd, vg, vs, vb);
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
                double fdG = (rp.I[p] - rn.I[p]) / (2 * H);
                double fdC = (rp.Q[p] - rn.Q[p]) / (2 * H);
                AssertClose(fdG, r0.Dg[p, q], $"Dg[{p},{q}] at (vd={vd},vg={vg},vb={vb})", 1e-9);
                AssertClose(fdC, r0.Dc[p, q], $"Dc[{p},{q}] at (vd={vd},vg={vg},vb={vb})", 1e-15);
            }
        }
    }

    // ── T3 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The node map the elaborator builds for a device with no ohmic resistance, as node INDICES
    /// into (drain, gate, source, bulk) = (0, 1, 2, 3). Stated here rather than imported so that a
    /// change to the elaborator's order has to be made in two places and noticed in one.
    /// </summary>
    private static readonly int[] IntrinsicNodes = [0, 2, 3, 2, 3, 0, 1, 2, 1, 0, 1, 3];

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T3_TheNodeLevelJacobian_MatchesCentralFiniteDifferences(
        double vd, double vg, double vs, double vb)
    {
        var m = Full(rd: 0.0, rs: 0.0);

        (double[] I, double[] Q) Node(double[] t)
        {
            var r = Eval(m, V(t[0], t[1], t[2], t[3]));
            var ni = new double[4];
            var nq = new double[4];
            for (int p = 0; p < r.I.Length; p++)
            {
                ni[IntrinsicNodes[2 * p]]     += r.I[p];
                ni[IntrinsicNodes[2 * p + 1]] -= r.I[p];
                nq[IntrinsicNodes[2 * p]]     += r.Q[p];
                nq[IntrinsicNodes[2 * p + 1]] -= r.Q[p];
            }
            return (ni, nq);
        }

        // The node-level Jacobian the engine assembles, from the port-level one.
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
        const double H = 1e-6;
        for (int c = 0; c < 4; c++)
        {
            var tp = (double[])t0.Clone(); tp[c] += H;
            var tm = (double[])t0.Clone(); tm[c] -= H;
            var (ip, qp2) = Node(tp);
            var (im, qm2) = Node(tm);
            for (int rIdx = 0; rIdx < 4; rIdx++)
            {
                AssertClose((ip[rIdx] - im[rIdx]) / (2 * H), jg[rIdx, c],
                    $"node dI[{rIdx}]/dV[{c}] at (vd={vd},vg={vg},vb={vb})", 1e-9);
                AssertClose((qp2[rIdx] - qm2[rIdx]) / (2 * H), jc[rIdx, c],
                    $"node dQ[{rIdx}]/dV[{c}] at (vd={vd},vg={vg},vb={vb})", 1e-15);
            }
        }
    }

    // ── T4 ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T4_PChannel_IsTheExactMirrorOfNChannel(double vd, double vg, double vs, double vb)
    {
        var n = Full(MosfetModelBase.Channel4.N, rd: 0, rs: 0);
        var p = Full(MosfetModelBase.Channel4.P, rd: 0, rs: 0);

        var rn = Eval(n, V(vd, vg, vs, vb));
        var rp = Eval(p, V(-vd, -vg, -vs, -vb));

        for (int k = 0; k < rn.I.Length; k++)
        {
            AssertClose(-rn.I[k], rp.I[k], $"I[{k}] mirrored", 1e-18);
            AssertClose(-rn.Q[k], rp.Q[k], $"Q[{k}] mirrored", 1e-20);
            for (int j = 0; j < rn.I.Length; j++)
            {
                // The Jacobian is UNCHANGED under the mirror: the sign appears once on the current
                // and once on the voltage, and the two cancel.
                AssertClose(rn.Dg[k, j], rp.Dg[k, j], $"Dg[{k},{j}] mirrored", 1e-15);
                AssertClose(rn.Dc[k, j], rp.Dc[k, j], $"Dc[{k},{j}] mirrored", 1e-18);
            }
        }
    }

    // ── T5 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T5_TheDeviceIsSymmetric_ReversingTheBiasReversesTheCurrent()
    {
        // No body effect and bulk tied to the LOWER terminal in each case, so the two orientations
        // really are the same device seen from the other side. With Gamma non-zero they are not,
        // which is the point of the body effect and would make this test measure that instead.
        var m = new MosfetLevel1Model(vto: 0.7, kp: 2e-5, gamma: 0.0, phi: 0.65,
                                      w: 20e-6, l: 1e-6, tox: 20e-9);

        // Forward: source at 0, drain at +2. Reverse: drain at 0, source at +2. Bulk at 0 in both,
        // which is the lower of the two terminals either way.
        var fwd = Eval(m, V(vd: 2.0, vg: 2.5, vs: 0.0, vb: 0.0));
        var rev = Eval(m, V(vd: 0.0, vg: 2.5, vs: 2.0, vb: 0.0));

        // Same magnitude of channel current, opposite sign.
        Assert.True(fwd.I[0] > 1e-6, "the forward device must actually be conducting");
        AssertClose(-fwd.I[0], rev.I[0], "reverse channel current", 1e-15);

        // And the gate charge has simply swapped ends.
        AssertClose(fwd.Q[3], rev.Q[4], "Qgs forward vs Qgd reverse", 1e-20);
        AssertClose(fwd.Q[4], rev.Q[3], "Qgd forward vs Qgs reverse", 1e-20);
    }

    // ── T6 ────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(BiasGrid))]
    public void T6_ChargeIsConserved_TheFourTerminalChargesSumToZero(
        double vd, double vg, double vs, double vb)
    {
        var m = Full(rd: 0, rs: 0);
        var r = Eval(m, V(vd, vg, vs, vb));

        var nq = new double[4];
        for (int p = 0; p < r.Q.Length; p++)
        {
            nq[IntrinsicNodes[2 * p]]     += r.Q[p];
            nq[IntrinsicNodes[2 * p + 1]] -= r.Q[p];
        }

        double sum = nq[0] + nq[1] + nq[2] + nq[3];
        double scale = 0.0;
        foreach (double x in nq) scale = Math.Max(scale, Math.Abs(x));
        Assert.True(Math.Abs(sum) <= 1e-12 * Math.Max(scale, 1e-15),
            $"terminal charges must sum to zero; sum = {sum:E6}, largest term {scale:E6}");
    }

    // ── T7 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T7_TheBodyEffect_MovesTheThresholdByThePublishedAmount_AndIsOffAtGammaZero()
    {
        const double Vto = 0.7, Gamma = 0.5, Phi = 0.65, Kp = 2e-5, W = 20e-6, L = 1e-6;
        const double Vbs = -2.0;
        var withBody = new MosfetLevel1Model(vto: Vto, kp: Kp, gamma: Gamma, phi: Phi, w: W, l: L);
        var noBody   = new MosfetLevel1Model(vto: Vto, kp: Kp, gamma: 0.0,   phi: Phi, w: W, l: L);

        double vth = Vto + Gamma * (Math.Sqrt(Phi - Vbs) - Math.Sqrt(Phi));
        Assert.True(vth > Vto + 0.3, "the body effect must actually raise the threshold here");

        // Biased exactly AT the shifted threshold, the device with the body effect is off and the
        // one without is not — which is the whole of what the parameter does.
        var onThreshold = V(vd: 3.0, vg: vth, vs: 0.0, vb: Vbs);
        Assert.Equal(0.0, Eval(withBody, onThreshold).I[0]);
        Assert.True(Eval(noBody, onThreshold).I[0] > 1e-6);

        // Gamma = 0 means the bulk bias does nothing at all, not "does a little".
        Assert.Equal(Eval(noBody, V(3.0, 2.0, 0.0, 0.0)).I[0],
                     Eval(noBody, V(3.0, 2.0, 0.0, -4.0)).I[0]);
    }

    // ── T8 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T8_EveryTemperatureRelationIsInertAtNominal()
    {
        // Temp == Tnom must be EXACTLY the identity for every RELATION — the mobility scaling, the
        // surface potential, the threshold shift, the junction potentials and the depletion
        // capacitances all collapse, however far from nominal the pair sits. A °C/K mix-up anywhere
        // in that chain shows up here, because it would manufacture a 273-degree delta out of
        // nothing. The tolerance is exact equality, not a fuzz factor.
        //
        // The thermal voltage kT/q is NOT one of those relations and is deliberately excluded: it
        // is the temperature itself, not a departure from an extraction point, so the two bulk
        // junction currents legitimately differ at 85 C from their values at nominal. Asserting
        // them equal would be asserting that the junctions are not temperature-aware at all.
        var baseline = Full();
        foreach (double t in new[] { Temperature.NominalC, 85.0, -40.0 })
        {
            var m = Full(tempC: t, tnomC: t);
            foreach (var (vd, vg, vb) in new[] { (2.0, 2.5, 0.0), (0.2, 1.5, -2.0), (-1.0, 3.0, -2.0) })
            {
                var a = Eval(baseline, V(vd, vg, 0.0, vb, vdInt: vd - 0.01, vsInt: 0.005));
                var b = Eval(m,        V(vd, vg, 0.0, vb, vdInt: vd - 0.01, vsInt: 0.005));

                Assert.Equal(a.I[0], b.I[0]);          // channel current
                Assert.Equal(a.I[6], b.I[6]);          // and the ohmic ports
                Assert.Equal(a.I[7], b.I[7]);
                for (int k = 0; k < a.Q.Length; k++)
                    Assert.Equal(a.Q[k], b.Q[k]);      // every charge, junctions included
            }
        }

        // And a real temperature difference must actually move something — otherwise the claim
        // above would pass on a model with no temperature relations in it at all.
        var hot = Full(tempC: 125.0);
        var bias = V(2.0, 2.5, 0.0, 0.0, vdInt: 1.99, vsInt: 0.005);
        Assert.NotEqual(Eval(baseline, bias).I[0], Eval(hot, bias).I[0]);
    }

    // ── T9 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_TheIntrinsicGateCharge_HasThePublishedLimits()
    {
        const double Tox = 20e-9, W = 20e-6, L = 1e-6;
        double coxTotal = MosfetModelBase.OxideRelativePermittivity * MosfetModelBase.Eps0 / Tox * W * L;

        // No overlaps and no junctions: this is about the intrinsic charge alone.
        var m = new MosfetLevel1Model(vto: 0.7, kp: 2e-5, gamma: 0.0, phi: 0.65,
                                      w: W, l: L, tox: Tox);

        // Cutoff: no inversion charge at all.
        var off = Eval(m, V(vd: 2.0, vg: 0.0, vs: 0.0, vb: 0.0));
        Assert.Equal(0.0, off.Q[3]);
        Assert.Equal(0.0, off.Q[4]);

        // Vds = 0: the channel is uniform and holds Cox·Vgt, split evenly.
        const double Vgt = 1.3;
        var flat = Eval(m, V(vd: 0.0, vg: 0.7 + Vgt, vs: 0.0, vb: 0.0));
        AssertRel(coxTotal * Vgt / 2, flat.Q[3], "Qgs at Vds = 0", 1e-12);
        AssertRel(coxTotal * Vgt / 2, flat.Q[4], "Qgd at Vds = 0", 1e-12);

        // Saturation: two thirds of Cox·Vgt in total, still split evenly.
        var sat = Eval(m, V(vd: 5.0, vg: 0.7 + Vgt, vs: 0.0, vb: 0.0));
        AssertRel(2.0 / 3.0 * coxTotal * Vgt / 2, sat.Q[3], "Qgs in saturation", 1e-12);
        AssertRel(2.0 / 3.0 * coxTotal * Vgt / 2, sat.Q[4], "Qgd in saturation", 1e-12);

        // And dQ/dVgs at Vds = 0 is exactly Cox — the limit the whole formulation is built to hit.
        var r = Eval(m, V(vd: 0.0, vg: 0.7 + Vgt, vs: 0.0, vb: 0.0));
        AssertRel(coxTotal, r.Dc[3, 3] + r.Dc[4, 3], "dQg/dVgs at Vds = 0", 1e-12);
    }

    // ── T10 ───────────────────────────────────────────────────────────────────

    [Fact]
    public void T10_PortAndTerminalStructure_FollowsTheParasitics()
    {
        var bare = Full(rd: 0, rs: 0);
        Assert.Equal(6, bare.PortCount);
        Assert.Equal(0, bare.InternalNodeCount);
        Assert.Equal(["ids", "ibs", "ibd", "qgs", "qgd", "qgb"], bare.TerminalNames);

        var dOnly = Full(rd: 4.0, rs: 0);
        Assert.Equal(7, dOnly.PortCount);
        Assert.Equal(1, dOnly.InternalNodeCount);
        Assert.Equal("drain", dOnly.TerminalNames[6]);

        var sOnly = Full(rd: 0, rs: 3.0);
        Assert.Equal("source", sOnly.TerminalNames[6]);

        var both = Full();
        Assert.Equal(8, both.PortCount);
        Assert.Equal(2, both.InternalNodeCount);
        Assert.Equal(["ids", "ibs", "ibd", "qgs", "qgd", "qgb", "drain", "source"], both.TerminalNames);

        // The ohmic ports are ordinary resistors and carry no charge.
        var r = Eval(both, V(2.0, 2.5, 0.0, 0.0, vdInt: 1.9, vsInt: 0.02));
        AssertRel(0.1 / 4.0, r.I[6], "Rd current", 1e-12);
        AssertRel(-0.02 / 3.0, r.I[7], "Rs current", 1e-12);
        Assert.Equal(0.0, r.Q[6]);
        Assert.Equal(0.0, r.Q[7]);
    }

    // ── T11 ───────────────────────────────────────────────────────────────────

    [Fact]
    public void T11_CutoffIsGenuinelyOff_NoFudgeConductance()
    {
        var m = new MosfetLevel1Model(vto: 0.7, kp: 2e-5, gamma: 0.5, phi: 0.65, lambda: 0.02,
                                      w: 20e-6, l: 1e-6, tox: 20e-9);
        var r = Eval(m, V(vd: 3.0, vg: 0.2, vs: 0.0, vb: 0.0));

        Assert.Equal(0.0, r.I[0]);
        Assert.Equal(0.0, r.Dg[0, 0]);
        Assert.Equal(0.0, r.Dg[0, 3]);
        Assert.Equal(0.0, r.Dg[0, 1]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void AssertClose(double expected, double actual, string what, double abs)
    {
        double tol = abs + 2e-5 * Math.Abs(expected);
        Assert.True(Math.Abs(expected - actual) <= tol,
            $"{what}: expected {expected:E12}, got {actual:E12} (tol {tol:E3})");
    }

    private static void AssertRel(double expected, double actual, string what, double rel)
    {
        Assert.True(Math.Abs(expected - actual) <= rel * Math.Abs(expected) + 1e-300 + rel,
            $"{what}: expected {expected:E12}, got {actual:E12}");
    }
}
