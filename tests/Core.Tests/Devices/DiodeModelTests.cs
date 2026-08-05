using System;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using Xunit;

namespace CircuitRF.Core.Tests.Devices;

/// <summary>
/// Gate tests for <see cref="DiodeModel"/>.
///
/// <para><b>The oracles are the closed-form equations and finite differences</b>, not stored
/// numbers from another simulator: the equations are what has to be right, and a golden file from
/// elsewhere would only test that two implementations agree.</para>
///
///   T1  — forward I(V) matches Is·(exp(V/(N·Vt))−1) exactly.
///   T2  — reverse current saturates at −Is.
///   T3  — dI/dV matches a central finite difference, forward and reverse.
///   T4  — the exponential's tangent continuation is continuous in VALUE and SLOPE.
///   T5  — depletion charge: dQ/dV equals the returned capacitance (finite difference).
///   T6  — the depletion tangent continuation is continuous in VALUE and SLOPE at Fc·Vj.
///   T7  — Cj0 = 0 gives no charge at all; Q(0) = 0 always.
///   T8  — diffusion charge is Tt·I, and its capacitance is Tt·dI/dV.
///   T9  — breakdown conducts below −Bv, and Bv = 0 means NO breakdown (not breakdown at 0 V).
///   T10 — the factory builds one from parameters, and omitted parameters take their defaults.
///   T11 — Rs = 0 keeps the device a one-port; Rs > 0 makes it a two-port over three nets.
///   T12 — with Rs, port 0 is the resistor and port 1 the junction, and neither cross-couples.
///   T13 — the series resistance actually limits current: the composite I(V) is below the
///         bare-junction I(V) at the same external bias, by the amount Rs implies.
///   T14 — recombination is a SECOND exponential with its own ideality, not a correction.
///   T15 — breakdown carries its OWN emission coefficient, not N.
///   T16 — area scales the currents and the capacitance up and Rs down.
///   T17 — the saturation current moves with temperature, against the closed form.
///   T18 — the junction potential and Cj0 move with temperature.
///   T19 — Temp == Tnom collapses every temperature relation to the identity, EXACTLY.
/// </summary>
public class DiodeModelTests
{
    // The model's own default nominal, shared with the FET family: 26.85 °C = 300.00 K exactly.
    private const double Vt300 = 1.380649e-23 * 300.0 / 1.602176634e-19;   // ≈ 25.85 mV

    private static (double I, double G, double Q, double C) At(DiodeModel d, double v)
    {
        var r = d.Evaluate(new PortVoltages([v]));
        return (r.I[0], r.Dg[0, 0], r.Q[0], r.Dc[0, 0]);
    }


    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.3)]
    [InlineData(0.5)]
    public void T1_ForwardCurrent_MatchesTheClosedForm(double v)
    {
        const double is0 = 1e-14, n = 1.05;
        var d = new DiodeModel(saturationCurrent: is0, emissionCoefficient: n, minimumConductance: 0.0);

        double expected = is0 * (Math.Exp(v / (n * Vt300)) - 1.0);
        Assert.Equal(expected, At(d, v).I, 12 * Math.Max(1e-30, Math.Abs(expected)) == 0 ? 15 : 12);
    }

    [Fact]
    public void T2_ReverseCurrent_SaturatesAtMinusIs()
    {
        const double is0 = 3e-15;
        var d = new DiodeModel(saturationCurrent: is0, minimumConductance: 0.0);

        // Several kT/q negative: exp() is negligible, so I → −Is.
        double i = At(d, -0.5).I;
        Assert.InRange(i, -is0 * 1.000001, -is0 * 0.999999);
    }

    [Theory]
    [InlineData(-0.20)]
    [InlineData(-0.02)]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.45)]
    public void T3_Conductance_MatchesFiniteDifference(double v)
    {
        var d = new DiodeModel(saturationCurrent: 1e-14, emissionCoefficient: 1.08);

        const double h = 1e-7;
        double fd = (At(d, v + h).I - At(d, v - h).I) / (2 * h);
        double g  = At(d, v).G;
        Assert.Equal(fd, g, Math.Max(1e-9, Math.Abs(fd) * 1e-5));
    }

    [Fact]
    public void T4_ExponentialTangentContinuation_IsContinuousInValueAndSlope()
    {
        // The changeover sits at V = 40·N·Vt. Straddle it: a discontinuity here is exactly what
        // stalls Newton, and it would not show up in any single-point value check.
        const double n = 1.0;
        var d = new DiodeModel(saturationCurrent: 1e-14, emissionCoefficient: n, minimumConductance: 0.0);
        double vlim = 40.0 * n * Vt300;

        const double h = 1e-9;
        var lo = At(d, vlim - h);
        var hi = At(d, vlim + h);

        Assert.Equal(lo.I, hi.I, Math.Abs(lo.I) * 1e-6);      // value continuous
        Assert.Equal(lo.G, hi.G, Math.Abs(lo.G) * 1e-6);      // slope continuous
        Assert.True(hi.G > 0, "conductance must stay positive past the changeover");

        // And it really is a tangent: far above the limit, I grows linearly with the frozen slope.
        double far = At(d, vlim + 1.0).I;
        Assert.Equal(lo.I + lo.G * 1.0, far, Math.Abs(far) * 1e-6);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-0.3)]
    [InlineData(0.0)]
    [InlineData(0.2)]
    public void T5_DepletionCharge_DerivativeEqualsReturnedCapacitance(double v)
    {
        var d = new DiodeModel(saturationCurrent: 1e-14, zeroBiasCapacitance: 2e-13,
                               junctionPotential: 0.8, gradingCoefficient: 0.42,
                               transitTime: 0.0, minimumConductance: 0.0);

        const double h = 1e-7;
        double fd = (At(d, v + h).Q - At(d, v - h).Q) / (2 * h);
        double c  = At(d, v).C;
        Assert.Equal(fd, c, Math.Abs(fd) * 1e-5);
    }

    [Fact]
    public void T6_DepletionTangentContinuation_IsContinuousInValueAndSlope()
    {
        const double cj0 = 1e-13, vj = 0.75, m = 0.5, fc = 0.5;
        var d = new DiodeModel(saturationCurrent: 1e-14, zeroBiasCapacitance: cj0,
                               junctionPotential: vj, gradingCoefficient: m,
                               forwardBiasCapCoeff: fc, minimumConductance: 0.0);

        double vx = fc * vj;
        const double h = 1e-9;
        var lo = At(d, vx - h);
        var hi = At(d, vx + h);

        Assert.Equal(lo.Q, hi.Q, Math.Abs(lo.Q) * 1e-6);
        Assert.Equal(lo.C, hi.C, Math.Abs(lo.C) * 1e-6);
    }

    [Fact]
    public void T7_NoJunctionCapacitance_MeansNoChargeAtAll()
    {
        var d = new DiodeModel(saturationCurrent: 1e-14, zeroBiasCapacitance: 0.0, transitTime: 0.0);
        foreach (double v in new[] { -1.0, -0.1, 0.0, 0.3 })
        {
            Assert.Equal(0.0, At(d, v).Q);
            Assert.Equal(0.0, At(d, v).C);
        }
        // Q(0) = 0 also holds when there IS a junction capacitance — the integral starts at zero bias.
        var withCap = new DiodeModel(zeroBiasCapacitance: 5e-13);
        Assert.Equal(0.0, At(withCap, 0.0).Q, 1e-18);
    }

    [Fact]
    public void T8_DiffusionCharge_IsTransitTimeTimesCurrent()
    {
        const double tt = 1e-11;
        var noCap = new DiodeModel(saturationCurrent: 1e-14, zeroBiasCapacitance: 0.0,
                                   transitTime: tt, minimumConductance: 0.0);

        foreach (double v in new[] { 0.1, 0.3, 0.45 })
        {
            var s = At(noCap, v);
            Assert.Equal(tt * s.I, s.Q, Math.Abs(tt * s.I) * 1e-9);
            Assert.Equal(tt * s.G, s.C, Math.Abs(tt * s.G) * 1e-9);
        }
    }

    [Fact]
    public void T9_Breakdown_ConductsBelowMinusBv_AndIsOffWhenBvIsZero()
    {
        const double bv = 4.0;
        var withBd = new DiodeModel(saturationCurrent: 1e-14, breakdownVoltage: bv,
                                    breakdownCurrent: 1e-3, minimumConductance: 0.0);

        // Just inside breakdown the current is still the reverse saturation level …
        Assert.InRange(Math.Abs(At(withBd, -(bv - 0.5)).I), 0.0, 1e-13);
        // … and past it, it is large and negative, and grows as the bias goes further negative.
        double i1 = At(withBd, -(bv + 0.05)).I;
        double i2 = At(withBd, -(bv + 0.20)).I;
        Assert.True(i2 < i1 && i1 < 0, $"breakdown current must grow negative: {i1} then {i2}");

        // Bv = 0 means "not modelled". A model that treated it as breakdown at 0 V would make every
        // reverse-biased diode conduct hugely — silently wrong, and the reason this test exists.
        var noBd = new DiodeModel(saturationCurrent: 1e-14, breakdownVoltage: 0.0, minimumConductance: 0.0);
        Assert.InRange(Math.Abs(At(noBd, -50.0).I), 0.0, 1e-13);
    }

    [Fact]
    public void T10_Factory_BuildsFromParameters_AndDefaultsTheRest()
    {
        var pars = new Dictionary<string, Value>
        {
            ["Is"]  = new Value(2.5e-15),
            ["N"]   = new Value(1.12),
            ["Cj0"] = new Value(1.4e-13),
            ["Vj"]  = new Value(0.72),
            ["M"]   = new Value(0.33),
        };

        Assert.True(ComponentModelFactory.IsPrimitive("Diode"));
        var m = ComponentModelFactory.TryCreate("Diode", pars);
        var d = Assert.IsType<DiodeModel>(m);

        Assert.Equal(1, d.PortCount);
        Assert.Equal(ModelKind.Nonlinear, d.Kind);
        Assert.Equal(["anode", "cathode"], d.TerminalNames);

        // The stated Is and N are the ones used …
        // `Temp` at this boundary is in DEGREES CELSIUS, matching the FET family and the published
        // parameter tables — the factory converts. Asserting against the °C-derived Vt is the point:
        // reading it as kelvin would put the device at 26.85 K and pass no check but this one.
        // The factory's °C default and the constructor's kelvin default are the SAME nominal,
        // so Vt300 serves both — that agreement is itself the thing being pinned here.
        double expected = 2.5e-15 * (Math.Exp(0.3 / (1.12 * Vt300)) - 1.0);
        Assert.Equal(expected, At(d, 0.3).I, Math.Abs(expected) * 1e-9);

        // And an explicit Temp is read the same way: 126.85 °C = 400 K, not 126.85 K.
        //
        // `Xti=0 Eg=0` switches the saturation current's own temperature dependence off, leaving Vt
        // as the one temperature-dependent quantity in the answer. Without it this check would have
        // to predict Is(T) using the same code it is checking. Is(T) is gated on its own, below.
        var hot = (DiodeModel)ComponentModelFactory.TryCreate("Diode",
            new Dictionary<string, Value>
            {
                ["Is"]   = new Value(2.5e-15),
                ["Temp"] = new Value(126.85),
                ["Xti"]  = new Value(0.0),
                ["Eg"]   = new Value(0.0),
            })!;
        double vtHot = 1.380649e-23 * 400.0 / 1.602176634e-19;
        double expectedHot = 2.5e-15 * (Math.Exp(0.3 / vtHot) - 1.0);
        Assert.Equal(expectedHot, At(hot, 0.3).I, Math.Abs(expectedHot) * 1e-9);

        // … and an omitted parameter takes its default rather than zero: Tt defaults to 0, so the
        // charge here is depletion only, and it is non-zero because Cj0 was given.
        Assert.True(At(d, 0.2).Q > 0);
    }

    [Fact]
    public void T11_SeriesResistance_ChangesThePortAndTerminalShape()
    {
        var bare = new DiodeModel(saturationCurrent: 1e-14);
        Assert.False(bare.HasSeriesResistance);
        Assert.Equal(1, bare.PortCount);
        Assert.Equal(["anode", "cathode"], bare.TerminalNames);

        var withRs = new DiodeModel(saturationCurrent: 1e-14, seriesResistance: 12.0);
        Assert.True(withRs.HasSeriesResistance);
        Assert.Equal(2, withRs.PortCount);
        // Three distinct nets, expressed as two +/- pairs sharing the internal node.
        Assert.Equal(["anode", "internal", "internal", "cathode"], withRs.TerminalNames);
    }

    [Fact]
    public void T12_WithSeriesResistance_PortsAreTheResistorAndTheJunction()
    {
        const double rs = 12.0;
        var d = new DiodeModel(saturationCurrent: 1e-14, emissionCoefficient: 1.05,
                               zeroBiasCapacitance: 1.1e-13, junctionPotential: 0.7,
                               seriesResistance: rs);

        // Port 0 across the resistor, port 1 across the junction, chosen independently.
        var r = d.Evaluate(new PortVoltages([0.05, 0.35]));

        // Resistor: ohmic, no charge, conductance exactly 1/Rs.
        Assert.Equal(0.05 / rs, r.I[0], Math.Abs(0.05 / rs) * 1e-12);
        Assert.Equal(1.0 / rs, r.Dg[0, 0], 1e-12);
        Assert.Equal(0.0, r.Q[0]);
        Assert.Equal(0.0, r.Dc[0, 0]);

        // Junction: exactly what the one-port model gives at the same junction voltage.
        var bare = new DiodeModel(saturationCurrent: 1e-14, emissionCoefficient: 1.05,
                                  zeroBiasCapacitance: 1.1e-13, junctionPotential: 0.7);
        var b = bare.Evaluate(new PortVoltages([0.35]));
        Assert.Equal(b.I[0],  r.I[1],  Math.Abs(b.I[0]) * 1e-12);
        Assert.Equal(b.Dg[0, 0], r.Dg[1, 1], Math.Abs(b.Dg[0, 0]) * 1e-12);
        Assert.Equal(b.Q[0],  r.Q[1],  Math.Abs(b.Q[0]) * 1e-12);

        // No cross terms: the two ports couple only through the shared internal node, which is the
        // engine's business. A non-zero off-diagonal here would double-count that coupling.
        Assert.Equal(0.0, r.Dg[0, 1]);
        Assert.Equal(0.0, r.Dg[1, 0]);
        Assert.Equal(0.0, r.Dc[0, 1]);
        Assert.Equal(0.0, r.Dc[1, 0]);
    }

    [Fact]
    public void T13_SeriesResistance_LimitsTheCurrent()
    {
        // Solve the series pair by hand at a fixed external bias and check the model agrees:
        // the same current must flow through both ports, so Vext = Vj + I(Vj)*Rs.
        const double rs = 12.0, vext = 0.8;
        var d = new DiodeModel(saturationCurrent: 1e-14, emissionCoefficient: 1.05, seriesResistance: rs);

        // Bisect for the junction voltage that balances the two ports.
        double lo = 0.0, hi = vext;
        for (int k = 0; k < 200; k++)
        {
            double mid = 0.5 * (lo + hi);
            var s2 = d.Evaluate(new PortVoltages([vext - mid, mid]));
            if (s2.I[0] > s2.I[1]) lo = mid; else hi = mid;
        }
        double vj = 0.5 * (lo + hi);
        var bal = d.Evaluate(new PortVoltages([vext - vj, vj]));

        // Both ports carry the same current at balance — that IS the internal-node KCL.
        Assert.Equal(bal.I[0], bal.I[1], Math.Abs(bal.I[0]) * 1e-6);

        // And Rs really is limiting: without it the same external bias drives far more current.
        var bare = new DiodeModel(saturationCurrent: 1e-14, emissionCoefficient: 1.05);
        double iBare = bare.Evaluate(new PortVoltages([vext])).I[0];
        Assert.True(bal.I[1] < iBare * 0.5,
            $"Rs must limit the current: {bal.I[1]} should be well below {iBare}");
        // Ohm's law across the resistor closes the loop.
        Assert.Equal((vext - vj) / rs, bal.I[1], Math.Abs(bal.I[1]) * 1e-6);
    }

    // ── T14 — recombination ───────────────────────────────────────────────────

    /// <summary>
    /// Recombination is a second exponential with its own saturation current AND its own ideality
    /// factor — conventionally near 2 where the diffusion term's is near 1. That is what lets it
    /// dominate at low bias and vanish at high bias, and it is why folding it into <c>Is</c> fits
    /// one decade of the curve and misses the rest.
    /// </summary>
    [Theory]
    [InlineData(0.10)]
    [InlineData(0.25)]
    [InlineData(0.45)]
    public void T14_RecombinationIsASecondExponentialWithItsOwnIdeality(double v)
    {
        const double isat = 1e-14, n = 1.0, isr = 1e-10, nr = 2.0;

        var d = new DiodeModel(saturationCurrent: isat, emissionCoefficient: n,
                               recombinationCurrent: isr, recombinationEmission: nr);

        double expected = isat * (Math.Exp(v / (n  * Vt300)) - 1.0)
                        + isr  * (Math.Exp(v / (nr * Vt300)) - 1.0);

        Assert.Equal(expected, At(d, v).I, Math.Abs(expected) * 1e-9);
    }

    /// <summary>
    /// The two terms must be distinguishable in the answer, or the test above would pass for a model
    /// that ignored one of them. At low bias the recombination term dominates by orders of
    /// magnitude; at high bias the diffusion term does. Asserting the CROSSOVER is what proves two
    /// different ideality factors are actually in play.
    /// </summary>
    [Fact]
    public void T14b_TheTwoTermsCrossOver_SoNeitherIsIgnorable()
    {
        const double isat = 1e-14, isr = 1e-10;

        var diffusionOnly = new DiodeModel(saturationCurrent: isat);
        var both          = new DiodeModel(saturationCurrent: isat,
                                           recombinationCurrent: isr, recombinationEmission: 2.0);

        // Low bias: recombination is the whole current.
        Assert.True(At(both, 0.1).I > 100.0 * At(diffusionOnly, 0.1).I);

        // High bias: diffusion has overtaken it, so the two are within a few per cent.
        double hi = At(both, 0.7).I, hiDiff = At(diffusionOnly, 0.7).I;
        Assert.True(hi < 1.05 * hiDiff, $"at 0.7 V diffusion should dominate: {hi} vs {hiDiff}");
    }

    /// <summary>Recombination is off unless asked for — a diode with no Isr is exactly what it was.</summary>
    [Fact]
    public void T14c_RecombinationIsOffByDefault()
        => Assert.Equal(At(new DiodeModel(saturationCurrent: 1e-14), 0.3).I,
                        At(new DiodeModel(saturationCurrent: 1e-14, recombinationCurrent: 0.0), 0.3).I);

    // ── T15 — breakdown emission ──────────────────────────────────────────────

    /// <summary>
    /// Breakdown is a different mechanism from forward conduction and carries its own emission
    /// coefficient. Before <c>Nbv</c> existed this model reused <c>N</c> there, which made the
    /// reverse knee follow the forward ideality — nothing physical requires that, and no parameter
    /// table states it.
    /// </summary>
    [Fact]
    public void T15_BreakdownUsesNbv_NotN()
    {
        const double bv = 5.0, ibv = 1e-3, n = 2.0, nbv = 1.0, v = -5.2;

        var d = new DiodeModel(saturationCurrent: 1e-14, emissionCoefficient: n,
                               breakdownVoltage: bv, breakdownCurrent: ibv, breakdownEmission: nbv);

        double expected = -ibv * Math.Exp(-(bv + v) / (nbv * Vt300));
        Assert.Equal(expected, At(d, v).I, Math.Abs(expected) * 1e-9);

        // The check cannot pass vacuously: reading N there gives a visibly different current.
        double wrong = -ibv * Math.Exp(-(bv + v) / (n * Vt300));
        Assert.True(Math.Abs(wrong - expected) > 0.1 * Math.Abs(expected),
            "the fixture must make N and Nbv distinguishable");
    }

    // ── T16 — area ────────────────────────────────────────────────────────────

    /// <summary>
    /// Area is what "several of the same junction side by side" means: the currents and the
    /// capacitance scale up with it and the series resistance scales down, because the copies are in
    /// parallel. Asserted against a unit-area device rather than a stored number.
    /// </summary>
    [Fact]
    public void T16_AreaScalesCurrentAndCapacitanceUp_AndSeriesResistanceDown()
    {
        const double a = 4.0;

        var unit = new DiodeModel(saturationCurrent: 1e-14, zeroBiasCapacitance: 2e-12,
                                  recombinationCurrent: 1e-10);
        var wide = new DiodeModel(saturationCurrent: 1e-14, zeroBiasCapacitance: 2e-12,
                                  recombinationCurrent: 1e-10, area: a);

        var u = At(unit, 0.3);
        var w = At(wide, 0.3);

        Assert.Equal(a * u.I, w.I, Math.Abs(a * u.I) * 1e-12);
        Assert.Equal(a * u.G, w.G, Math.Abs(a * u.G) * 1e-12);
        Assert.Equal(a * u.C, w.C, Math.Abs(a * u.C) * 1e-12);

        // Rs divides: the resistor port's conductance is a·(1/Rs).
        var rsWide = new DiodeModel(saturationCurrent: 1e-14, seriesResistance: 8.0, area: a);
        Assert.Equal(a / 8.0, rsWide.Evaluate(new PortVoltages([0.1, 0.0])).Dg[0, 0], 12);
    }

    // ── T17/T18 — temperature ─────────────────────────────────────────────────

    /// <summary>
    /// The saturation current is the quantity that moves furthest with temperature, and it moves
    /// exponentially in the bandgap — so an <c>Eg</c> meaning something slightly different does not
    /// produce a slightly different current. Checked against the closed form written out here,
    /// independently of the shared helper that computes it.
    /// </summary>
    [Fact]
    public void T17_SaturationCurrentMovesWithTemperature()
    {
        const double isat = 1e-14, n = 1.0, xti = 3.0, eg0 = 1.16;
        const double tnomK = 300.0, tK = 400.0, v = 0.3;

        var d = new DiodeModel(saturationCurrent: isat, emissionCoefficient: n,
                               temperatureK: tK, nominalTemperatureK: tnomK,
                               saturationTempExponent: xti, bandgapAtZeroK: eg0);

        double Eg(double t) => eg0 - 7.02e-4 * t * t / (1108.0 + t);
        const double q = 1.602176634e-19, k = 1.380649e-23;

        double tr    = tK / tnomK;
        double isAtT = isat * Math.Pow(tr, xti / n) * Math.Exp(-q * Eg(tnomK) * (1.0 - tr) / (k * tK));
        double vtT   = k * tK / q;
        double expected = isAtT * (Math.Exp(v / (n * vtT)) - 1.0);

        Assert.Equal(expected, At(d, v).I, Math.Abs(expected) * 1e-9);

        // …and it is a large effect, so a model that ignored it would not sneak past the tolerance.
        double ignoring = isat * (Math.Exp(v / (n * vtT)) - 1.0);
        Assert.True(isAtT > 100.0 * isat, "the fixture must make Is(T) unmistakable");
        Assert.True(Math.Abs(expected - ignoring) > 0.5 * expected);
    }

    /// <summary>
    /// The junction potential falls with temperature and the zero-bias capacitance follows it. Both
    /// come from the relations shared with the FET family — this asserts the DIRECTION and that they
    /// actually moved, since the magnitudes are the shared helper's own contract.
    /// </summary>
    [Fact]
    public void T18_JunctionPotentialAndCapacitanceMoveWithTemperature()
    {
        var cold = new DiodeModel(zeroBiasCapacitance: 2e-12, junctionPotential: 0.8,
                                  temperatureK: 300.0, nominalTemperatureK: 300.0);
        var hot  = new DiodeModel(zeroBiasCapacitance: 2e-12, junctionPotential: 0.8,
                                  temperatureK: 400.0, nominalTemperatureK: 300.0);

        // The junction potential falls, so the zero-bias capacitance rises.
        Assert.True(At(hot, 0.0).C > At(cold, 0.0).C,
            $"Cj0 should rise as Vj falls: {At(hot, 0.0).C} vs {At(cold, 0.0).C}");

        // And the depletion charge stays consistent with the capacitance it reports, at temperature.
        const double h = 1e-6, v = 0.2;
        double dq = (At(hot, v + h).Q - At(hot, v - h).Q) / (2.0 * h);
        Assert.Equal(dq, At(hot, v).C, Math.Abs(dq) * 1e-6);
    }

    /// <summary>
    /// The guard. With the device at its own extraction temperature every relation must collapse to
    /// the identity EXACTLY — not nearly. Large coefficients are supplied deliberately, because that
    /// is what turns a residual drift into a visible one.
    ///
    /// <para>This is the diode's half of the rule that ambient must never move <c>Tnom</c>: move
    /// both together and ΔT is zero at every ambient, every relation collapses, and the device still
    /// looks temperature-aware.</para>
    /// </summary>
    [Fact]
    public void T19_AtItsOwnNominal_EveryTemperatureRelationIsTheIdentity()
    {
        var plain = new DiodeModel(saturationCurrent: 1e-14, zeroBiasCapacitance: 2e-12,
                                   junctionPotential: 0.8, recombinationCurrent: 1e-10);

        var loaded = new DiodeModel(saturationCurrent: 1e-14, zeroBiasCapacitance: 2e-12,
                                    junctionPotential: 0.8, recombinationCurrent: 1e-10,
                                    temperatureK: 350.0, nominalTemperatureK: 350.0,
                                    saturationTempExponent: 7.0, bandgapAtZeroK: 1.16);

        // Same ΔT (zero), so the only difference left is Vt — which is a different temperature here,
        // so compare the two AT their own nominals by asking for the same one.
        var same = new DiodeModel(saturationCurrent: 1e-14, zeroBiasCapacitance: 2e-12,
                                  junctionPotential: 0.8, recombinationCurrent: 1e-10,
                                  temperatureK: Temperature.NominalK,
                                  nominalTemperatureK: Temperature.NominalK,
                                  saturationTempExponent: 7.0, bandgapAtZeroK: 1.16);

        // Bit-exact, deliberately: "collapses to the identity" is a claim about the arithmetic, and
        // a tolerance here would hide exactly the residual drift this test exists to catch.
        Assert.Equal(At(plain, 0.3).I, At(same, 0.3).I);
        Assert.Equal(At(plain, 0.0).C, At(same, 0.0).C);

        // The loaded one at 350 K is a genuinely different device, so the test above is not vacuous.
        Assert.NotEqual(At(plain, 0.3).I, At(loaded, 0.3).I);
    }
}
