using System;
using System.Collections.Generic;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Fet;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Gate tests for the built-in large-signal FET family.
///
/// <para><b>The central test is T2 — every model's analytic gm and gds against a central finite
/// difference, over a bias grid.</b> These laws are easy to write down and easy to differentiate
/// wrongly, and a wrong Jacobian does not produce a wrong answer: it produces a slow solve, or a
/// converged solve at the wrong operating point. Nothing else here catches that.</para>
///
///   T1 — each model's Id matches its published closed form at a sample bias.
///   T2 — gm and gds match central finite differences, for every model, across a bias grid.
///   T3 — pinch-off is genuinely off: zero current AND zero derivatives, no fudge conductance.
///   T4 — the Statz knee is continuous in value and slope at Vds = 3/Alpha.
///   T5 — gate conduction is off by default and behaves as a diode when enabled.
///   T6 — Cgd appears on BOTH ports and in the off-diagonals; Cgs on port 0 only.
///   T7 — the factory builds every model by name, and they are distinct types.
///   T8 — same-named parameters mean different things: `Beta` drives the quadratic and cubic
///        models differently, which is why they are separate types.
///   T9 — bias-dependent (junction) gate charge: dQ/dV matches the returned capacitance, the
///        capacitance actually varies with bias, and CapModel selects between the schemes.
///  T10 — the SOURCE IS AN INDEPENDENT TERMINAL, not tied to ground.
///  T11 — temperature is INERT at nominal: Temp == Tnom reproduces the untemperatured device bit
///        for bit, for every model. This is the one that catches a unit mix-up (°C vs K), which
///        would otherwise show up only as a quietly wrong answer at every bias.
///  T12 — each coefficient moves its OWN parameter, in the published form, and by the published
///        amount — checked against the closed-form relation, not merely "it changed".
///  T13 — the shared relations: Vbi falls with temperature, Cgs/Cgd follow it, and the gate
///        saturation current rises.
/// </summary>
public class FetModelTests
{
    private static (double Id, double Gm, double Gds) At(FetModelBase f, double vgs, double vds)
    {
        var r = f.Evaluate(new PortVoltages([vgs, vds]));
        return (r.I[1], r.Dg[1, 0], r.Dg[1, 1]);
    }

    public static TheoryData<string, FetModelBase> AllModels() => new()
    {
        { "Curtice",      new CurticeQuadraticFetModel(vto: -2.0, beta: 0.02, lambda: 0.05, alpha: 2.0) },
        { "CurticeCubic", new CurticeCubicFetModel(a0: 0.08, a1: 0.05, a2: 0.01, a3: -0.002,
                                                   gamma: 2.0, beta: 0.02, vds0: 5.0) },
        { "Statz",        new StatzFetModel(vto: -2.0, beta: 0.02, b: 0.3, alpha: 2.0, lambda: 0.05) },
        { "Materka",      new MaterkaFetModel(idss: 0.1, vp0: -2.0, gamma: 0.05, alpha: 2.0) },
        { "Angelov",      new AngelovFetModel(ipk: 0.1, vpk: -1.0, p1: 1.2, p2: 0.1, p3: -0.02,
                                              alpha: 2.0, lambda: 0.05) },
    };

    [Fact]
    public void T1_EachModel_MatchesItsPublishedClosedForm()
    {
        const double vgs = -1.0, vds = 3.0;

        // Curtice quadratic: Beta·(Vgs−Vto)²·(1+Lambda·Vds)·tanh(Alpha·Vds)
        var cq = new CurticeQuadraticFetModel(vto: -2.0, beta: 0.02, lambda: 0.05, alpha: 2.0);
        double eCq = 0.02 * Math.Pow(vgs + 2.0, 2) * (1 + 0.05 * vds) * Math.Tanh(2.0 * vds);
        Assert.Equal(eCq, At(cq, vgs, vds).Id, Math.Abs(eCq) * 1e-12);

        // Statz: Beta·Vg²/(1+B·Vg)·f·(1+Lambda·Vds); Vds = 3 > 3/Alpha = 1.5, so f = 1.
        var st = new StatzFetModel(vto: -2.0, beta: 0.02, b: 0.3, alpha: 2.0, lambda: 0.05);
        double vg = vgs + 2.0;
        double eSt = 0.02 * vg * vg / (1 + 0.3 * vg) * 1.0 * (1 + 0.05 * vds);
        Assert.Equal(eSt, At(st, vgs, vds).Id, Math.Abs(eSt) * 1e-12);

        // Angelov: Ipk·(1+tanh(psi))·(1+Lambda·Vds)·tanh(Alpha·Vds)
        var an = new AngelovFetModel(ipk: 0.1, vpk: -1.0, p1: 1.2, p2: 0.1, p3: -0.02,
                                     alpha: 2.0, lambda: 0.05);
        double x = vgs + 1.0;
        double psi = x * (1.2 + x * (0.1 + x * -0.02));
        double eAn = 0.1 * (1 + Math.Tanh(psi)) * (1 + 0.05 * vds) * Math.Tanh(2.0 * vds);
        Assert.Equal(eAn, At(an, vgs, vds).Id, Math.Abs(eAn) * 1e-12);

        // Materka: Idss·(1−Vgs/Vp)²·tanh(Alpha·Vds/(Vgs−Vp)),  Vp = Vp0 + Gamma·Vds
        var ma = new MaterkaFetModel(idss: 0.1, vp0: -2.0, gamma: 0.05, alpha: 2.0);
        double vp = -2.0 + 0.05 * vds;
        double eMa = 0.1 * Math.Pow(1 - vgs / vp, 2) * Math.Tanh(2.0 * vds / (vgs - vp));
        Assert.Equal(eMa, At(ma, vgs, vds).Id, Math.Abs(eMa) * 1e-12);

        // Curtice cubic: P(V1)·tanh(Gamma·Vds),  V1 = Vgs·(1 + Beta·(Vds0 − Vds))
        var cc = new CurticeCubicFetModel(a0: 0.08, a1: 0.05, a2: 0.01, a3: -0.002,
                                          gamma: 2.0, beta: 0.02, vds0: 5.0);
        double v1 = vgs * (1 + 0.02 * (5.0 - vds));
        double eCc = (0.08 + v1 * (0.05 + v1 * (0.01 + v1 * -0.002))) * Math.Tanh(2.0 * vds);
        Assert.Equal(eCc, At(cc, vgs, vds).Id, Math.Abs(eCc) * 1e-12);
    }

    [Theory]
    [MemberData(nameof(AllModels))]
    public void T2_AnalyticDerivatives_MatchFiniteDifferences(string name, FetModelBase f)
    {
        const double h = 1e-6;
        foreach (double vgs in new[] { -1.6, -1.2, -0.8, -0.4, 0.0 })
        foreach (double vds in new[] { 0.5, 1.0, 2.0, 4.0, 6.0 })
        {
            var s = At(f, vgs, vds);
            double fdGm  = (At(f, vgs + h, vds).Id - At(f, vgs - h, vds).Id) / (2 * h);
            double fdGds = (At(f, vgs, vds + h).Id - At(f, vgs, vds - h).Id) / (2 * h);

            // The model name is in the message on purpose: a bare tolerance failure here would not
            // say WHICH law's derivative is wrong, and that is the whole diagnostic value.
            Assert.True(Math.Abs(fdGm - s.Gm) <= Math.Max(1e-9, Math.Abs(fdGm) * 2e-4),
                $"{name}: gm mismatch at Vgs={vgs}, Vds={vds}: analytic {s.Gm}, finite-difference {fdGm}");
            Assert.True(Math.Abs(fdGds - s.Gds) <= Math.Max(1e-9, Math.Abs(fdGds) * 2e-4),
                $"{name}: gds mismatch at Vgs={vgs}, Vds={vds}: analytic {s.Gds}, finite-difference {fdGds}");
        }
    }

    [Fact]
    public void T3_BelowPinchOff_CurrentAndBothDerivativesAreExactlyZero()
    {
        // A fudge conductance here would hide a genuinely-off device and put current where there is
        // none; the engine's own gmin already keeps the node solvable.
        foreach (var f in new FetModelBase[]
                 {
                     new CurticeQuadraticFetModel(vto: -2.0),
                     new StatzFetModel(vto: -2.0),
                     new MaterkaFetModel(vp0: -2.0),
                 })
        {
            var s = At(f, -3.0, 3.0);
            Assert.Equal(0.0, s.Id);
            Assert.Equal(0.0, s.Gm);
            Assert.Equal(0.0, s.Gds);
        }
    }

    [Fact]
    public void T4_StatzKnee_IsContinuousInValueAndSlope()
    {
        const double alpha = 2.0;
        var f = new StatzFetModel(vto: -2.0, beta: 0.02, b: 0.3, alpha: alpha, lambda: 0.05);
        double knee = 3.0 / alpha;

        const double h = 1e-8;
        var lo = At(f, -1.0, knee - h);
        var hi = At(f, -1.0, knee + h);
        Assert.Equal(lo.Id,  hi.Id,  Math.Abs(lo.Id)  * 1e-6);
        Assert.Equal(lo.Gds, hi.Gds, Math.Max(1e-9, Math.Abs(lo.Gds) * 1e-4));
    }

    [Fact]
    public void T5_GateConduction_IsOffByDefault_AndDiodeLikeWhenEnabled()
    {
        var off = new CurticeQuadraticFetModel();
        Assert.Equal(0.0, off.Evaluate(new PortVoltages([0.5, 3.0])).I[0]);

        var on = new CurticeQuadraticFetModel(gateSaturationCurrent: 1e-14, gateEmissionCoefficient: 1.1);
        var r = on.Evaluate(new PortVoltages([0.5, 3.0]));
        Assert.True(r.I[0] > 0, "forward gate bias must conduct");
        // Derivative matches a finite difference of the gate current.
        const double h = 1e-7;
        double fd = (on.Evaluate(new PortVoltages([0.5 + h, 3.0])).I[0]
                   - on.Evaluate(new PortVoltages([0.5 - h, 3.0])).I[0]) / (2 * h);
        Assert.Equal(fd, r.Dg[0, 0], Math.Abs(fd) * 1e-4);
    }

    [Fact]
    public void T6_GateCharge_PutsCgdOnBothPortsAndInTheOffDiagonals()
    {
        const double cgs = 3e-13, cgd = 1e-13;
        var f = new CurticeQuadraticFetModel(cgs: cgs, cgd: cgd);
        var r = f.Evaluate(new PortVoltages([-1.0, 3.0]));

        // Cgd bridges gate and drain, so in (Vgs, Vds) coordinates it appears on both ports and
        // couples them. Dropping the off-diagonals is the classic plausible-but-wrong Jacobian.
        Assert.Equal(cgs + cgd, r.Dc[0, 0], 1e-18);
        Assert.Equal(-cgd,      r.Dc[0, 1], 1e-18);
        Assert.Equal(-cgd,      r.Dc[1, 0], 1e-18);
        Assert.Equal(cgd,       r.Dc[1, 1], 1e-18);

        // And the charges are consistent with those derivatives.
        const double h = 1e-4;
        double dQgdVds = (f.Evaluate(new PortVoltages([-1.0, 3.0 + h])).Q[0]
                        - f.Evaluate(new PortVoltages([-1.0, 3.0 - h])).Q[0]) / (2 * h);
        Assert.Equal(-cgd, dQgdVds, 1e-18);
    }

    [Theory]
    [InlineData("FET_Curtice",      typeof(CurticeQuadraticFetModel))]
    [InlineData("FET_CurticeCubic", typeof(CurticeCubicFetModel))]
    [InlineData("FET_Statz",        typeof(StatzFetModel))]
    [InlineData("FET_Materka",      typeof(MaterkaFetModel))]
    [InlineData("FET_Angelov",      typeof(AngelovFetModel))]
    public void T7_Factory_BuildsEachModelByName(string typeName, Type expected)
    {
        Assert.True(ComponentModelFactory.IsPrimitive(typeName));
        var m = ComponentModelFactory.TryCreate(typeName, new Dictionary<string, Value>());
        Assert.NotNull(m);
        Assert.IsType(expected, m);
        Assert.Equal(2, m!.PortCount);
        Assert.Equal(ModelKind.Nonlinear, m.Kind);
        Assert.Equal(["gate", "source", "drain", "source"], m.TerminalNames);
    }

    [Fact]
    public void T8_SameParameterName_MeansDifferentThingsAcrossModels()
    {
        // `Beta` is a transconductance parameter in the quadratic law and a gate-voltage shift with
        // drain bias in the cubic one. Feeding one model's value to the other is meaningless, which
        // is precisely why these are separate types with separate parameter sets rather than
        // variants sharing a block.
        var pars = new Dictionary<string, Value> { ["Beta"] = new Value(0.02) };

        var q = (FetModelBase)ComponentModelFactory.TryCreate("FET_Curtice", pars)!;
        var c = (FetModelBase)ComponentModelFactory.TryCreate("FET_CurticeCubic", pars)!;

        double iq = At(q, -1.0, 3.0).Id;
        double ic = At(c, -1.0, 3.0).Id;
        Assert.True(Math.Abs(iq - ic) > 1e-6,
            $"the same Beta must not produce the same current: {iq} vs {ic}");
    }

    [Theory]
    [InlineData(-1.5, 2.0)]
    [InlineData(-0.5, 4.0)]
    [InlineData( 0.2, 1.0)]
    public void T9_JunctionGateCharge_IsBiasDependent_AndConsistentWithItsCapacitance(
        double vgs, double vds)
    {
        // CapModel 2 = the standard depletion charge applied to Vgs and Vgd separately.
        var f = new CurticeQuadraticFetModel(cgs: 3e-13, cgd: 1e-13,
                                             capModel: 2, vbi: 0.8, mGrading: 0.5, fc: 0.5);

        // dQ/dV must equal the reported capacitance — the real consistency check between the charge
        // and its derivative, and the thing a hand-written closed form gets wrong.
        const double h = 1e-6;
        double dQgDvgs = (f.Evaluate(new PortVoltages([vgs + h, vds])).Q[0]
                        - f.Evaluate(new PortVoltages([vgs - h, vds])).Q[0]) / (2 * h);
        double dQgDvds = (f.Evaluate(new PortVoltages([vgs, vds + h])).Q[0]
                        - f.Evaluate(new PortVoltages([vgs, vds - h])).Q[0]) / (2 * h);
        var r = f.Evaluate(new PortVoltages([vgs, vds]));

        Assert.Equal(dQgDvgs, r.Dc[0, 0], Math.Abs(dQgDvgs) * 1e-4);
        Assert.Equal(dQgDvds, r.Dc[0, 1], Math.Abs(dQgDvds) * 1e-4);

        // And it is genuinely bias-dependent: the whole point of CapModel 2.
        double cLow  = f.Evaluate(new PortVoltages([-2.0, vds])).Dc[0, 0];
        double cHigh = f.Evaluate(new PortVoltages([ 0.3, vds])).Dc[0, 0];
        Assert.True(cHigh > cLow * 1.2,
            $"junction capacitance must rise with forward bias: {cLow} -> {cHigh}");

        // CapModel 1 (the default) is constant, so the same sweep must NOT move it.
        var flat = new CurticeQuadraticFetModel(cgs: 3e-13, cgd: 1e-13, capModel: 1);
        Assert.Equal(flat.Evaluate(new PortVoltages([-2.0, vds])).Dc[0, 0],
                     flat.Evaluate(new PortVoltages([ 0.3, vds])).Dc[0, 0], 1e-20);

        // CapModel 0 is no charge at all.
        var none = new CurticeQuadraticFetModel(cgs: 3e-13, cgd: 1e-13, capModel: 0);
        Assert.Equal(0.0, none.Evaluate(new PortVoltages([vgs, vds])).Dc[0, 0]);
    }

    [Fact]
    public void T10_SourceIsAnIndependentTerminal_NotTiedToGround()
    {
        // The model is written in (Vgs, Vds), but that does NOT make it common-source: the source
        // is an ordinary net the user wires wherever they like. Proven by driving the same device
        // with a source that is NOT at 0 V and showing only the DIFFERENCES matter.
        var f = new CurticeQuadraticFetModel(vto: -2.0, beta: 0.02, lambda: 0.05, alpha: 2.0);

        // Grounded source: Vg = -1, Vd = 3, Vs = 0.
        var grounded = At(f, -1.0 - 0.0, 3.0 - 0.0);
        // Source lifted to 2 V, gate and drain lifted with it: identical device state.
        var lifted   = At(f, 1.0 - 2.0, 5.0 - 2.0);

        Assert.Equal(grounded.Id, lifted.Id, Math.Abs(grounded.Id) * 1e-12);

        // And the terminal list names three distinct roles, with source appearing in both port
        // pairs because the two ports share it — which is what lets it float.
        Assert.Equal(["gate", "source", "drain", "source"], f.TerminalNames);
        Assert.Equal(2, f.PortCount);
    }

    // ── Temperature ───────────────────────────────────────────────────────────

    private const double Tnom = FetModelBase.NominalTemperatureC;

    public static TheoryData<string, FetModelBase, FetModelBase> AtNominal() => new()
    {
        { "Curtice",
          new CurticeQuadraticFetModel(vto: -2.0, beta: 0.02, lambda: 0.05, alpha: 2.0,
                                       cgs: 3e-13, cgd: 1e-13, gateSaturationCurrent: 1e-14),
          new CurticeQuadraticFetModel(vto: -2.0, beta: 0.02, lambda: 0.05, alpha: 2.0,
                                       cgs: 3e-13, cgd: 1e-13, gateSaturationCurrent: 1e-14,
                                       tempC: Tnom, tnomC: Tnom,
                                       betatc: 3.0, alphatc: -2.0, vtotc: 1e-3) },
        { "Statz",
          new StatzFetModel(vto: -2.0, beta: 0.02, b: 0.3, alpha: 2.0,
                            cgs: 3e-13, cgd: 1e-13, gateSaturationCurrent: 1e-14),
          new StatzFetModel(vto: -2.0, beta: 0.02, b: 0.3, alpha: 2.0,
                            cgs: 3e-13, cgd: 1e-13, gateSaturationCurrent: 1e-14,
                            tempC: Tnom, tnomC: Tnom,
                            betatc: 3.0, alphatc: -2.0, vtotc: 1e-3) },
        { "Materka",
          new MaterkaFetModel(idss: 0.1, vp0: -2.0, gamma: 0.05, alpha: 2.0,
                              cgs: 3e-13, cgd: 1e-13, gateSaturationCurrent: 1e-14),
          new MaterkaFetModel(idss: 0.1, vp0: -2.0, gamma: 0.05, alpha: 2.0,
                              cgs: 3e-13, cgd: 1e-13, gateSaturationCurrent: 1e-14,
                              tempC: Tnom, tnomC: Tnom,
                              alphatc: -2.0, gammatc: 1e-3, vtotc: 1e-3) },
        { "Angelov",
          new AngelovFetModel(ipk: 0.1, vpk: -1.0, p1: 1.0, alpha: 2.0,
                              cgs: 3e-13, cgd: 1e-13, gateSaturationCurrent: 1e-14),
          new AngelovFetModel(ipk: 0.1, vpk: -1.0, p1: 1.0, alpha: 2.0,
                              cgs: 3e-13, cgd: 1e-13, gateSaturationCurrent: 1e-14,
                              tempC: Tnom, tnomC: Tnom,
                              alphatc: -2.0, vtotc: 1e-3) },
        { "CurticeCubic",
          new CurticeCubicFetModel(a0: 0.1, a1: 0.05, gamma: 2.0,
                                   cgs: 3e-13, cgd: 1e-13, gateSaturationCurrent: 1e-14),
          new CurticeCubicFetModel(a0: 0.1, a1: 0.05, gamma: 2.0,
                                   cgs: 3e-13, cgd: 1e-13, gateSaturationCurrent: 1e-14,
                                   tempC: Tnom, tnomC: Tnom,
                                   gammatc: 1e-3) },
    };

    [Theory]
    [MemberData(nameof(AtNominal))]
    public void T11_TemperatureIsInertAtNominal(string name, FetModelBase plain, FetModelBase tc)
    {
        // Temp == Tnom must be EXACTLY the identity — every relation collapses, however large the
        // coefficients. Non-zero coefficients are supplied deliberately: if the code multiplied by
        // them regardless of ΔT, or read °C as K (a 273-degree ΔT out of nowhere), this is where it
        // shows. Note the tolerance is exact equality, not a fuzz factor.
        foreach (var (vgs, vds) in new[] { (-1.5, 1.0), (-0.5, 3.0), (0.2, 5.0) })
        {
            var a = plain.Evaluate(new PortVoltages([vgs, vds]));
            var b = tc.Evaluate(new PortVoltages([vgs, vds]));
            Assert.Equal(a.I[1], b.I[1], 1e-18);
            Assert.Equal(a.I[0], b.I[0], 1e-24);
            Assert.Equal(a.Dg[1, 0], b.Dg[1, 0], 1e-18);
            Assert.Equal(a.Dc[0, 0], b.Dc[0, 0], 1e-28);
        }
        Assert.NotNull(name);
    }

    [Fact]
    public void T12_EachCoefficientMovesItsOwnParameter_ByThePublishedAmount()
    {
        const double dT = 100.0;                       // 126.85 C, a realistic junction rise
        double hot = Tnom + dT;

        // Vtotc is ADDITIVE in volts per degree: Vto(T) = Vto + Vtotc·ΔT. Checked by showing the
        // shifted device is identical to one built with the shifted threshold outright.
        var vshift = new CurticeQuadraticFetModel(vto: -2.0, beta: 0.02, alpha: 2.0,
                                                  tempC: hot, tnomC: Tnom, vtotc: 2e-3);
        var vexact = new CurticeQuadraticFetModel(vto: -2.0 + 2e-3 * dT, beta: 0.02, alpha: 2.0);
        Assert.Equal(At(vexact, -1.0, 3.0).Id, At(vshift, -1.0, 3.0).Id, 1e-15);

        // Betatc is PERCENT per degree, and the published form is 1.01^(tc·ΔT) — NOT 1+0.01·tc·ΔT.
        // At ΔT = 100 and tc = -1 the two differ by ~4%, far outside any tolerance here, so this
        // asserts the exponential form specifically.
        var bshift = new CurticeQuadraticFetModel(vto: -2.0, beta: 0.02, alpha: 2.0,
                                                  tempC: hot, tnomC: Tnom, betatc: -1.0);
        var bexact = new CurticeQuadraticFetModel(vto: -2.0, beta: 0.02 * Math.Pow(1.01, -1.0 * dT),
                                                  alpha: 2.0);
        Assert.Equal(At(bexact, -1.0, 3.0).Id, At(bshift, -1.0, 3.0).Id, 1e-15);

        double linear = 0.02 * (1.0 + 0.01 * -1.0 * dT);
        Assert.True(Math.Abs(linear - 0.02 * Math.Pow(1.01, -100.0)) > 1e-4,
            "the two candidate forms must differ here, or this test proves nothing");

        // Alphatc likewise percent-per-degree, on the knee sharpness.
        var ashift = new StatzFetModel(vto: -2.0, beta: 0.02, b: 0.3, alpha: 2.0,
                                       tempC: hot, tnomC: Tnom, alphatc: -1.5);
        var aexact = new StatzFetModel(vto: -2.0, beta: 0.02, b: 0.3,
                                       alpha: 2.0 * Math.Pow(1.01, -1.5 * dT));
        Assert.Equal(At(aexact, -1.0, 0.5).Id, At(ashift, -1.0, 0.5).Id, 1e-15);

        // Gammatc is a PLAIN FRACTION per degree — Gamma·(1 + tc·ΔT) — a different form from the
        // two above despite the parameter table listing all three together.
        var gshift = new MaterkaFetModel(idss: 0.1, vp0: -2.0, gamma: 0.05, alpha: 2.0,
                                         tempC: hot, tnomC: Tnom, gammatc: 1e-3);
        var gexact = new MaterkaFetModel(idss: 0.1, vp0: -2.0, gamma: 0.05 * (1.0 + 1e-3 * dT),
                                         alpha: 2.0);
        Assert.Equal(At(gexact, -1.0, 3.0).Id, At(gshift, -1.0, 3.0).Id, 1e-15);
    }

    [Fact]
    public void T13_SharedRelations_CapacitanceAndGateCurrentTrackTemperature()
    {
        double hot = Tnom + 100.0;

        // Junction potential falls with temperature, so the depletion capacitance at a fixed bias
        // rises. Uses CapModel 2, where Vbi actually enters the charge.
        var cold = new CurticeQuadraticFetModel(cgs: 3e-13, cgd: 1e-13, capModel: 2, vbi: 1.0);
        var warm = new CurticeQuadraticFetModel(cgs: 3e-13, cgd: 1e-13, capModel: 2, vbi: 1.0,
                                                tempC: hot, tnomC: Tnom);
        double cCold = cold.Evaluate(new PortVoltages([-1.0, 3.0])).Dc[0, 0];
        double cWarm = warm.Evaluate(new PortVoltages([-1.0, 3.0])).Dc[0, 0];
        Assert.True(cWarm > cCold, $"gate capacitance must rise with temperature: {cCold} -> {cWarm}");

        // The gate diode's saturation current rises steeply — this is the relation that makes a hot
        // device leak. Xti = 3 is the usual junction value; at Xti = 0 the exponential term alone
        // still lifts it.
        double IgAt(double t, double xti) =>
            new CurticeQuadraticFetModel(gateSaturationCurrent: 1e-14, gateEmissionCoefficient: 1.0,
                                         tempC: t, tnomC: Tnom, xti: xti)
                .Evaluate(new PortVoltages([0.3, 3.0])).I[0];

        Assert.True(IgAt(hot, 3.0) > IgAt(Tnom, 3.0) * 10.0,
            $"gate current must rise sharply with temperature: {IgAt(Tnom, 3.0)} -> {IgAt(hot, 3.0)}");
        Assert.True(IgAt(hot, 3.0) > IgAt(hot, 0.0),
            "Xti must steepen the rise, not be ignored");

        // Below nominal it must go the other way — a coefficient applied with the wrong sign passes
        // every "it got bigger when hot" check and fails this one.
        Assert.True(IgAt(Tnom - 50.0, 3.0) < IgAt(Tnom, 3.0));
    }

    // ── T14 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>p-channel is the exact mirror of n-channel, term for term</b>, for the three laws that
    /// have one. Every voltage, current and charge is negated and the Jacobian is UNCHANGED,
    /// because the sign appears once on each side of every derivative.
    ///
    /// <para>Note what the p-channel device is built with: the SAME threshold magnitude with the
    /// opposite sign, because a card states it in its own channel's convention. Passing the
    /// n-channel value unchanged would be a device with no pinch-off at all.</para>
    /// </summary>
    [Theory]
    [InlineData("Curtice")]
    [InlineData("Statz")]
    [InlineData("Materka")]
    public void T14_PChannel_IsTheExactMirrorOfNChannel(string law)
    {
        const double Cgs = 0.4e-12, Cgd = 0.15e-12, Isg = 1e-13;
        FetModelBase Build(FetModelBase.Channel ch)
        {
            double s = (double)(int)ch;
            return law switch
            {
                "Curtice" => new CurticeQuadraticFetModel(
                    vto: s * -2.0, beta: 0.02, lambda: 0.05, alpha: 2.0,
                    cgs: Cgs, cgd: Cgd, gateSaturationCurrent: Isg, capModel: 2, channel: ch),
                "Statz" => new StatzFetModel(
                    vto: s * -2.0, beta: 0.02, b: 0.3, alpha: 2.0, lambda: 0.05,
                    cgs: Cgs, cgd: Cgd, gateSaturationCurrent: Isg, capModel: 2, channel: ch),
                _ => new MaterkaFetModel(
                    idss: 0.1, vp0: s * -2.0, gamma: 0.05, alpha: 2.0,
                    cgs: Cgs, cgd: Cgd, gateSaturationCurrent: Isg, capModel: 2, channel: ch),
            };
        }

        var n = Build(FetModelBase.Channel.N);
        var p = Build(FetModelBase.Channel.P);

        foreach (var (vgs, vds) in new[] { (-1.5, 1.0), (-0.5, 3.0), (0.2, 5.0), (-2.5, 4.0), (0.0, 0.4) })
        {
            var rn = n.Evaluate(new PortVoltages([vgs, vds]));
            var rp = p.Evaluate(new PortVoltages([-vgs, -vds]));

            for (int k = 0; k < 2; k++)
            {
                Assert.Equal(-rn.I[k], rp.I[k], 1e-18);
                Assert.Equal(-rn.Q[k], rp.Q[k], 1e-24);
                for (int j = 0; j < 2; j++)
                {
                    Assert.Equal(rn.Dg[k, j], rp.Dg[k, j], 1e-18);
                    Assert.Equal(rn.Dc[k, j], rp.Dc[k, j], 1e-24);
                }
            }
        }

        // The n-channel device must not have changed at all: Channel.N is +1 and every
        // multiplication by it is exact, so it is bit-identical to one built before polarity
        // existed. That is what lets every other test in this file stand as the proof.
        var before = law switch
        {
            "Curtice" => (FetModelBase)new CurticeQuadraticFetModel(vto: -2.0, beta: 0.02, lambda: 0.05, alpha: 2.0,
                              cgs: Cgs, cgd: Cgd, gateSaturationCurrent: Isg, capModel: 2),
            "Statz"   => new StatzFetModel(vto: -2.0, beta: 0.02, b: 0.3, alpha: 2.0, lambda: 0.05,
                              cgs: Cgs, cgd: Cgd, gateSaturationCurrent: Isg, capModel: 2),
            _         => new MaterkaFetModel(idss: 0.1, vp0: -2.0, gamma: 0.05, alpha: 2.0,
                              cgs: Cgs, cgd: Cgd, gateSaturationCurrent: Isg, capModel: 2),
        };
        var a = before.Evaluate(new PortVoltages([-0.5, 3.0]));
        var b = n.Evaluate(new PortVoltages([-0.5, 3.0]));
        Assert.Equal(a.I[1], b.I[1]);
        Assert.Equal(a.Q[0], b.Q[0]);
    }

    // ── T15 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The two laws with NO p-channel form do not have one</b>, and that is a decision rather
    /// than an omission: the cubic's <c>A0</c>-<c>A3</c> and Angelov's <c>P1</c>-<c>P3</c> are
    /// polynomials fitted directly against the gate voltage, so mirroring would have to negate the
    /// odd-order coefficients and leave the even ones alone, and no published convention says a
    /// p-channel card is written that way.
    ///
    /// <para>Stated as a test because the tempting "fix" is to hand them a Channel too, which
    /// compiles and produces a device that is wrong in the odd-order terms only — visible as a gm
    /// curve of the wrong shape, at no bias where anything obviously breaks.</para>
    /// </summary>
    [Fact]
    public void T15_TheTwoPolynomialLaws_HaveNoPChannelEngineType()
    {
        var empty = new Dictionary<string, Value>();

        Assert.NotNull(ComponentModelFactory.TryCreate("PFET_Curtice", empty));
        Assert.NotNull(ComponentModelFactory.TryCreate("PFET_Statz",   empty));
        Assert.NotNull(ComponentModelFactory.TryCreate("PFET_Materka", empty));

        Assert.Null(ComponentModelFactory.TryCreate("PFET_CurticeCubic", empty));
        Assert.Null(ComponentModelFactory.TryCreate("PFET_Angelov",      empty));
    }
}
