using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Gate tests for the built-in bipolar transistor.
///
/// <para><b>The central test is T2 — the whole analytic Jacobian against central finite
/// differences, over a bias grid, with every optional mechanism switched on.</b> This model has
/// four intrinsic ports, a base charge that couples both junction voltages into every one of them,
/// a bias-dependent transit time and a current-dependent base resistance. A wrong entry in any of
/// those does not produce a wrong answer: it produces a slow solve, or a converged solve at the
/// wrong operating point. Nothing else here catches that.</para>
///
///   T1 — forward-active behaviour: the current gain is Bf where the model says it should be,
///        and the collector current has the Early slope the parameters ask for.
///   T2 — the complete Jacobian (Dg and Dc) against central finite differences.
///   T3 — p-n-p is the exact mirror of n-p-n, term for term.
///   T4 — port and terminal structure follows the parasitics, and the elaborator's node order is
///        the one the model's port indices assume.
///   T5 — high-level injection and the Early effect are each individually live, and each is OFF
///        when its parameter is zero rather than defaulting to something.
///   T6 — Xcjc splits the collector capacitance and the two halves add back to Cjc.
///   T7 — the base resistance modulates from Rb down towards Rbm, monotonically, and is exactly
///        Rb at zero base current.
///   T8 — temperature is INERT at nominal: Temp == Tnom reproduces the untemperatured device bit
///        for bit. This is the one that catches a °C/K mix-up, which otherwise shifts every
///        current by orders of magnitude while still looking plausible.
///   T9 — the factory builds both polarities by name, and rejects anything else.
///  T10 — a freshly created device with the shipped defaults conducts, and its junction charges
///        are continuous in value and slope across the Fc·Vj changeover.
/// </summary>
public class BjtModelTests
{
    // A parameter set with EVERY optional mechanism live — Early in both directions, both knee
    // currents, both leakage terms, all three parasitics with base modulation, a split collector
    // capacitance and a bias-dependent transit time. A test grid over a model with half its terms
    // switched off proves half a model.
    private static BjtModel Full(BjtModel.Polarity p = BjtModel.Polarity.Npn,
                                 double tempC = Temperature.NominalC,
                                 double tnomC = Temperature.NominalC)
        => new(
            polarity: p,
            saturationCurrent: 9.57e-17, forwardBeta: 131.1, forwardEmission: 1.0,
            forwardEarlyVoltage: 71.02, forwardKneeCurrent: 0.09745,
            emitterLeakageCurrent: 1.618e-15, emitterLeakageEmission: 1.692,
            reverseBeta: 3.287, reverseEmission: 0.959,
            reverseEarlyVoltage: 4.081, reverseKneeCurrent: 0.07617,
            collectorLeakageCurrent: 5.969e-15, collectorLeakageEmission: 1.974,
            baseResistance: 9.72444, baseResistanceKneeCurrent: 3.017e-6, minimumBaseResistance: 6.94667,
            emitterResistance: 0.7979, collectorResistance: 2.089,
            emitterJunctionCap: 8.287e-14, emitterJunctionPotential: 0.8281, emitterGradingCoefficient: 0.7138,
            collectorJunctionCap: 8.781e-14, collectorJunctionPotential: 0.7715, collectorGradingCoefficient: 0.7552,
            internalBaseCapFraction: 0.6209, forwardBiasCapCoeff: 0.6275,
            forwardTransitTime: 1.72653e-11, transitTimeBiasCoeff: 0.07,
            transitTimeBiasVoltage: 0.00381019, transitTimeHighCurrent: 0.027024,
            reverseTransitTime: 1.71536e-8,
            tempC: tempC, tnomC: tnomC,
            saturationTempExponent: 6.548, betaTempExponent: 1.303, bandgapAtZeroK: 1.11);

    /// <summary>
    /// Port-voltage vector for the full device (7 ports), from the two junction voltages and the
    /// three parasitic drops. Written out rather than indexed by magic number because the port
    /// order IS the contract between this model and the elaborator.
    /// </summary>
    private static double[] V(double vbe, double vbc, double vrc = 0.0, double vrb = 0.0, double vre = 0.0)
        => [vbe, vbc, vbe - vbc, vbc, vrc, vrb, vre];

    // ── T1 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T1_ForwardActive_GainIsBf_AndTheEarlySlopeIsThere()
    {
        // Beta is Bf only where neither leakage nor high-level injection is in play, so this is
        // stated on a device with both switched off — otherwise the test would be measuring the
        // very terms that are SUPPOSED to bend it away from Bf.
        var m = new BjtModel(saturationCurrent: 1e-16, forwardBeta: 120.0,
                             forwardEarlyVoltage: 0.0, reverseBeta: 2.0);

        // Vbc = −5 V: forward active, collector junction well reverse-biased.
        var r = m.Evaluate(new PortVoltages([0.75, -5.0, 5.75, -5.0]));
        double ib = r.I[0], ic = r.I[2] - r.I[1];

        Assert.True(ib > 0 && ic > 0, "forward active must draw positive base and collector current");
        Assert.Equal(120.0, ic / ib, 3);          // Ic/Ib = Bf exactly, with no Early and no leakage

        // With Vaf given, Ic rises with |Vbc| — the Early effect, and the reason Ict cannot be a
        // source controlled by Vbe alone.
        var e = new BjtModel(saturationCurrent: 1e-16, forwardBeta: 120.0, forwardEarlyVoltage: 30.0);
        double ic1 = Ict(e, 0.75, -2.0), ic2 = Ict(e, 0.75, -20.0);
        Assert.True(ic2 > ic1 * 1.2, $"Early effect is not present: {ic1:E3} -> {ic2:E3}");

        // …and it is the ratio the parameter states: qb = 1/(1 − Vbc/Vaf) with Var absent.
        Assert.Equal((1.0 + 20.0 / 30.0) / (1.0 + 2.0 / 30.0), ic2 / ic1, 6);
    }

    private static double Ict(BjtModel m, double vbe, double vbc)
        => m.Evaluate(new PortVoltages([vbe, vbc, vbe - vbc, vbc])).I[2];

    // ── T2 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bias points chosen to reach every branch that has one: forward active at three current
    /// decades, saturation (both junctions forward), cut-off, reverse-active, and a non-zero drop
    /// across each parasitic resistance so the base-resistance modulation's cross terms are live.
    /// Nothing sits on a changeover — a finite difference across a kink measures the kink.
    /// </summary>
    public static IEnumerable<object[]> JacobianBiases() =>
    [
        [0.60, -3.0,  0.00,  0.000,  0.00],
        [0.70, -3.0,  0.05,  0.001,  0.02],
        [0.80, -1.0,  0.20,  0.004,  0.10],
        [0.85, -0.2,  0.30,  0.006,  0.15],
        [0.75,  0.40, 0.10,  0.002,  0.05],   // saturation: both junctions forward
        [0.30, -5.0,  0.01,  0.000,  0.00],   // sub-threshold
        [-0.5, -5.0,  0.00,  0.000,  0.00],   // cut-off
        [-0.5,  0.70, 0.02,  0.001,  0.01],   // reverse active
    ];

    [Theory]
    [MemberData(nameof(JacobianBiases))]
    public void T2_TheWholeJacobianMatchesCentralFiniteDifferences(
        double vbe, double vbc, double vrc, double vrb, double vre)
    {
        foreach (var p in new[] { BjtModel.Polarity.Npn, BjtModel.Polarity.Pnp })
        {
            var m = Full(p);
            double s = p == BjtModel.Polarity.Npn ? 1.0 : -1.0;
            var v = V(vbe, vbc, vrc, vrb, vre).Select(x => s * x).ToArray();

            // The ohmic ports are stated in raw node voltages and do NOT flip with polarity, so
            // undo the sign the line above applied to them.
            v[4] = vrc; v[5] = vrb; v[6] = vre;

            var r  = m.Evaluate(new PortVoltages(v));
            int P  = m.PortCount;
            const double h = 1e-6;

            for (int q = 0; q < P; q++)
            {
                var vp = (double[])v.Clone(); vp[q] += h;
                var vm = (double[])v.Clone(); vm[q] -= h;
                var rp = m.Evaluate(new PortVoltages(vp));
                var rm = m.Evaluate(new PortVoltages(vm));

                for (int i = 0; i < P; i++)
                {
                    AssertClose(m.Kind, $"dI[{i}]/dv[{q}] at ({vbe},{vbc}) {p}",
                                (rp.I[i] - rm.I[i]) / (2 * h), r.Dg[i, q],
                                Scale(r.Dg, i, q));
                    AssertClose(m.Kind, $"dQ[{i}]/dv[{q}] at ({vbe},{vbc}) {p}",
                                (rp.Q[i] - rm.Q[i]) / (2 * h), r.Dc[i, q],
                                Scale(r.Dc, i, q));
                }
            }
        }
    }

    /// <summary>The row's own largest entry — the natural scale for "this entry is negligible",
    /// so a genuinely zero cross term is not held to the same absolute floor as a 0.4 S diagonal.</summary>
    private static double Scale(double[,] a, int i, int q)
    {
        double s = 0;
        for (int k = 0; k < a.GetLength(1); k++) s = Math.Max(s, Math.Abs(a[i, k]));
        return s;
    }

    private static void AssertClose(ModelKind _, string what, double fd, double analytic, double scale)
    {
        double tol = Math.Max(2e-4 * Math.Max(Math.Abs(analytic), scale), 1e-13);
        Assert.True(Math.Abs(fd - analytic) <= tol,
            $"{what}: analytic {analytic:E6}, finite difference {fd:E6} (tolerance {tol:E3})");
    }

    // ── T3 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T3_PnpIsTheExactMirrorOfNpn()
    {
        var npn = Full(BjtModel.Polarity.Npn);
        var pnp = Full(BjtModel.Polarity.Pnp);

        foreach (var (vbe, vbc) in new[] { (0.75, -3.0), (0.85, 0.3), (0.2, -1.0) })
        {
            var v = V(vbe, vbc, 0.2, 0.004, 0.1);
            var a = npn.Evaluate(new PortVoltages(v));

            // Same device, every junction voltage negated — the ohmic ports keep their own sign,
            // because a resistor has no polarity.
            var w = v.Select(x => -x).ToArray();
            w[4] = v[4]; w[5] = v[5]; w[6] = v[6];
            var b = pnp.Evaluate(new PortVoltages(w));

            for (int i = 0; i < npn.PortCount; i++)
            {
                // Ohmic ports are unflipped; junction ports mirror.
                double sign = i >= 4 ? 1.0 : -1.0;
                Assert.Equal(sign * a.I[i], b.I[i], 15);
                Assert.Equal(sign * a.Q[i], b.Q[i], 15);
            }

            // The Jacobian is polarity-INVARIANT everywhere except the ohmic base port, whose
            // current does not flip while its controlling junction voltages do.
            for (int i = 0; i < npn.PortCount; i++)
                for (int j = 0; j < npn.PortCount; j++)
                {
                    double sign = (i == 5 && j < 4) ? -1.0 : 1.0;
                    Assert.Equal(sign * a.Dg[i, j], b.Dg[i, j], 12);
                    Assert.Equal(a.Dc[i, j], b.Dc[i, j], 12);
                }
        }
    }

    // ── T4 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T4_PortStructureFollowsTheParasitics()
    {
        var none = new BjtModel(baseResistance: 0, emitterResistance: 0, collectorResistance: 0);
        Assert.Equal(4, none.PortCount);
        Assert.Equal(0, none.InternalNodeCount);
        // No parasitics, so no port whose current IS an external terminal current — and therefore
        // no "collector"/"base"/"emitter" branch key. Naming an intrinsic port after a terminal
        // would be publishing a current the model never separately computes.
        Assert.Equal(["ibe", "ibc", "ic", "icx"], none.TerminalNames);

        var all = Full();
        Assert.Equal(7, all.PortCount);
        Assert.Equal(3, all.InternalNodeCount);
        Assert.Equal(["ibe", "ibc", "ic", "icx", "collector", "base", "emitter"], all.TerminalNames);

        // Only Rb: one internal node, five ports, and the parasitic port is the LAST one.
        var justRb = new BjtModel(baseResistance: 10, emitterResistance: 0, collectorResistance: 0);
        Assert.Equal(5, justRb.PortCount);
        Assert.True(justRb.HasBaseResistance);
        Assert.False(justRb.HasEmitterResistance);
        Assert.False(justRb.HasCollectorResistance);
        Assert.Equal("base", justRb.TerminalNames[^1]);

        // ONE name per port, always — that is what the branch-current key builder and harmonicaRF's
        // port axis both index by, and a list of any other length silently mislabels every trace.
        foreach (var m in new[] { none, all, justRb })
            Assert.Equal(m.PortCount, m.TerminalNames.Length);
    }

    // ── T5 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T5_HighLevelInjectionAndEarlyAreEachLive_AndEachOffAtZero()
    {
        // Ikf bends the collector current away from the ideal exponential at high injection, and
        // does nothing at low. Zero means NOT MODELLED — never "the knee is at zero amps".
        var withKnee = new BjtModel(saturationCurrent: 1e-16, forwardBeta: 100, forwardKneeCurrent: 1e-3);
        var noKnee   = new BjtModel(saturationCurrent: 1e-16, forwardBeta: 100, forwardKneeCurrent: 0.0);

        Assert.Equal(Ict(noKnee, 0.40, -3.0), Ict(withKnee, 0.40, -3.0), 12);   // far below the knee
        Assert.True(Ict(withKnee, 0.90, -3.0) < 0.5 * Ict(noKnee, 0.90, -3.0),
            "Ikf must roll the collector current off well above the knee");

        // Both Early voltages, independently. Var acts through Vbe, which is why it is the one a
        // model written only for the forward region silently drops.
        var noEarly = new BjtModel(saturationCurrent: 1e-16, forwardEarlyVoltage: 0, reverseEarlyVoltage: 0);
        var fwd     = new BjtModel(saturationCurrent: 1e-16, forwardEarlyVoltage: 30, reverseEarlyVoltage: 0);
        var rev     = new BjtModel(saturationCurrent: 1e-16, forwardEarlyVoltage: 0, reverseEarlyVoltage: 4);

        Assert.True(Ict(fwd, 0.7, -10.0) > 1.2 * Ict(noEarly, 0.7, -10.0));
        // Var pulls the OTHER way at a forward Vbe, and that is not a sign slip: q1 = 1/(1 - Vbc/Vaf
        // - Vbe/Var) sits in the DENOMINATOR of the transport current through qb, so a forward
        // emitter junction widens the base and takes current away. A model that made both Early
        // voltages raise the current would have Var's sign wrong and still look plausible.
        Assert.True(Ict(rev, 0.7, -10.0) < 0.9 * Ict(noEarly, 0.7, -10.0));
        Assert.Equal(1.0 - 0.7 / 4.0, Ict(rev, 0.7, -10.0) / Ict(noEarly, 0.7, -10.0), 6);
        Assert.Equal(Ict(noEarly, 0.7, -10.0), Ict(noEarly, 0.7, -20.0), 15);   // flat without Vaf
    }

    // ── T6 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T6_XcjcSplitsTheCollectorCapacitance_AndTheHalvesAddBackToCjc()
    {
        var m = new BjtModel(collectorJunctionCap: 1e-13, collectorJunctionPotential: 0.75,
                             collectorGradingCoefficient: 0.5, internalBaseCapFraction: 0.6,
                             baseResistance: 10);

        // Port 1 is the internal-base share, port 3 the external-base share, both across the SAME
        // junction voltage when the base resistance carries no drop.
        var r = m.Evaluate(new PortVoltages([0.0, -2.0, 2.0, -2.0, 0.0]));
        double cInternal = r.Dc[1, 1], cExternal = r.Dc[3, 3];

        Assert.True(cInternal > 0 && cExternal > 0);
        Assert.Equal(0.6 / 0.4, cInternal / cExternal, 9);

        // The two halves are exactly the whole junction — the split moves charge between nodes, it
        // does not create or destroy any.
        var whole = new BjtModel(collectorJunctionCap: 1e-13, collectorJunctionPotential: 0.75,
                                 collectorGradingCoefficient: 0.5, internalBaseCapFraction: 1.0,
                                 baseResistance: 10);
        var w = whole.Evaluate(new PortVoltages([0.0, -2.0, 2.0, -2.0, 0.0]));
        Assert.Equal(w.Dc[1, 1], cInternal + cExternal, 15);
        Assert.Equal(w.Q[1], r.Q[1] + r.Q[3], 15);
    }

    // ── T7 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T7_BaseResistanceModulatesFromRbTowardsRbm()
    {
        var m = Full();
        int p = 5;                       // the ohmic base port

        // At zero base current the resistance is Rb exactly — the small-current expansion has to
        // reproduce the unmodulated value, not merely come close to it.
        var off = m.Evaluate(new PortVoltages(V(-1.0, -3.0, 0, 0.001, 0)));
        Assert.Equal(1.0 / 9.72444, off.Dg[p, p], 9);

        // …and it falls monotonically towards Rbm as the base current rises, never past it.
        double last = 9.72444;
        foreach (double vbe in new[] { 0.55, 0.65, 0.75, 0.85 })
        {
            var r = m.Evaluate(new PortVoltages(V(vbe, -3.0, 0, 0.001, 0)));
            double rb = 1.0 / r.Dg[p, p];
            Assert.True(rb <= last + 1e-12, $"Rb rose with base current: {last:F4} -> {rb:F4}");
            Assert.True(rb >= 6.94667 - 1e-9, $"Rb fell below Rbm: {rb:F6}");
            last = rb;
        }
        Assert.True(last < 7.5, $"Rb should be near Rbm at high current, was {last:F4}");
    }

    // ── T8 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T8_TemperatureIsInertAtNominal()
    {
        // Temp == Tnom must reproduce the device bit for bit, at the nominal AND at a temperature
        // the design as a whole is held at. If it does not, a relation is reading a Celsius value
        // as kelvin (or the reverse), which shifts every saturation current by orders of magnitude
        // while still looking like a transistor.
        var baseline = Full();
        foreach (double t in new[] { Temperature.NominalC, 85.0, -40.0 })
        {
            var m = Full(BjtModel.Polarity.Npn, tempC: t, tnomC: t);
            // kT/q genuinely moves with temperature; every PARAMETER must not.
            var a = baseline.Evaluate(new PortVoltages(V(0.75, -3.0, 0.1, 0.002, 0.05)));
            var b = m.Evaluate(new PortVoltages(V(0.75, -3.0, 0.1, 0.002, 0.05)));
            if (t == Temperature.NominalC)
                for (int i = 0; i < baseline.PortCount; i++)
                    Assert.Equal(a.I[i], b.I[i], 15);
            // Rc and Re are temperature-independent in this model, at every temperature. Rb is
            // NOT, and not because of a temperature coefficient it does not have: it modulates
            // with base current, and the base current moves. Asserting it here would be asserting
            // that the modulation is absent.
            foreach (int i in new[] { 4, 6 })
                Assert.Equal(a.I[i], b.I[i], 15);
        }

        // And a real rise DOES move it: the saturation current climbs and beta follows Xtb.
        var hot = Full(BjtModel.Polarity.Npn, tempC: 125.0, tnomC: Temperature.NominalC);
        Assert.True(hot.Evaluate(new PortVoltages(V(0.6, -3.0))).I[2] >
                    10.0 * Full().Evaluate(new PortVoltages(V(0.6, -3.0))).I[2],
            "the collector current must rise strongly with junction temperature");
    }

    // ── T9 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T9_FactoryBuildsBothPolaritiesAndNothingElse()
    {
        var empty = new Dictionary<string, Value>();
        var npn = ComponentModelFactory.TryCreate("BJT_NPN", empty);
        var pnp = ComponentModelFactory.TryCreate("BJT_PNP", empty);

        Assert.IsType<BjtModel>(npn);
        Assert.IsType<BjtModel>(pnp);
        Assert.Equal(ModelKind.Nonlinear, npn!.Kind);
        Assert.True(ComponentModelFactory.IsPrimitive("BJT_NPN"));
        Assert.True(ComponentModelFactory.IsPrimitive("bjt_pnp"));

        // Two components, one law: the polarity is the only difference, and it is genuinely there.
        // Each is biased into ITS OWN forward-active region — the same raw voltages would leave the
        // p-n-p reverse-active, which tests the bias point rather than the device.
        var v = new double[] { 0.75, -3.0, 3.75, -3.0, 0, 0, 0 };
        var w = v.Select(x => -x).ToArray();
        Assert.True(npn.Evaluate(new PortVoltages(v)).I[2] > 0);
        Assert.True(pnp!.Evaluate(new PortVoltages(w)).I[2] < 0);

        // A type name in the family that is not a polarity is not a component.
        Assert.Null(ComponentModelFactory.TryCreate("BJT_NMOS", empty));
    }

    // ── T10 ───────────────────────────────────────────────────────────────────

    [Fact]
    public void T10_ShippedDefaultsConduct_AndTheJunctionChargesAreSmoothAcrossFcVj()
    {
        var m = (BjtModel)ComponentModelFactory.TryCreate("BJT_NPN", new Dictionary<string, Value>())!;

        var r = m.Evaluate(new PortVoltages(V(0.8, -3.0)));
        Assert.True(r.I[2] > 1e-4, $"a default transistor must conduct; Ict was {r.I[2]:E3}");
        Assert.True(r.I[0] > 0 && r.I[2] / r.I[0] > 20, "and it must have usable current gain");

        // The depletion charge is continued by its TANGENT above Fc·Vj, so BOTH the charge and the
        // capacitance are continuous there. A clamp would leave a kink in the Jacobian and stall
        // Newton exactly where the device is driven hardest.
        const double vje = 0.8281, fc = 0.6275;
        double v0 = fc * vje;
        foreach (double eps in new[] { 1e-4, 1e-5, 1e-6 })
        {
            var lo = m.Evaluate(new PortVoltages(V(v0 - eps, -3.0)));
            var hi = m.Evaluate(new PortVoltages(V(v0 + eps, -3.0)));
            Assert.Equal(lo.Q[0], hi.Q[0], 10);
            Assert.Equal(lo.Dc[0, 0], hi.Dc[0, 0], 6);
        }
    }
}
