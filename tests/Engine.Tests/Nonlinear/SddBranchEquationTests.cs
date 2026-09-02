using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Nonlinear;

/// <summary>
/// M4 — the nonlinear BRANCH row: an equation-defined device that holds a port's VOLTAGE
/// (<c>V[p]</c>) rather than stating its current.
///
/// <para><b>This is the milestone a behavioural voltage source needs, and it is the one every
/// oracle here is against a closed form rather than against another circuitRF path.</b> A device
/// that constrains a voltage is a Group-2 element: it carries a branch-current unknown and a row of
/// its own in the Newton system, and there is no combination of currents that says the same thing.
/// A large-conductance penalty (<c>I = G·(V(a,b) − f)</c>) is a DIFFERENT circuit, silently, whose
/// conditioning depends on what the source drives — so the tests below are written to fail if one
/// were ever substituted: an ideal source's answer does not move with its load.</para>
/// </summary>
public class SddBranchEquationTests(ITestOutputHelper output)
{
    private static NonlinearDcEngine.DcResult Dc(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return NonlinearDcEngine.Run(nl);
    }

    private static double NodeV(NonlinearDcEngine.DcResult r, ElaboratedNetlist nl, string node)
        => r.NodeVoltages[nl.Nodes.IndexOf(node) - 1];

    private static (ElaboratedNetlist Nl, NonlinearDcEngine.DcResult R) DcWithNetlist(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return (nl, NonlinearDcEngine.Run(nl));
    }

    private static double V(string cnl, string node)
    {
        var (nl, r) = DcWithNetlist(cnl);
        Assert.True(r.Converged, $"Did not converge. Residual={r.FinalResidual:G}");
        return NodeV(r, nl, node);
    }

    // ── T1 — an affine branch equation against a closed form ──────────────────

    /// <summary>
    /// The smallest circuit with the shape: a source holding <c>n2</c> at half of <c>n1</c>, behind
    /// a resistor that carries whatever current that takes. There is nothing to solve iteratively —
    /// the answer is algebra — which is exactly why it is the first gate.
    /// </summary>
    [Fact]
    public void T1_AnAffineVoltageConstraintSolvesToItsClosedForm()
    {
        double v = V("""
            Vdc:VS  n1 0  Vdc=1 V
            R:R1    n1 n2  R=100 Ohm
            SDD:E1  n2 0  n1 0  V[1]=0.5*_v2
            analysis DC1 type=dc
            """, "n2");

        output.WriteLine($"V(n2) = {v:G17}  (expected 0.5)");
        Assert.Equal(0.5, v, 12);
    }

    /// <summary>
    /// <b>The ideal-source property, stated as a test.</b> An ideal source holds its voltage
    /// whatever it drives, so changing the load must not move the answer by so much as a part in
    /// 1e12. A penalty formulation — a large conductance across the pair instead of a branch row —
    /// converges and passes T1, and fails HERE, because what it holds depends on what draws current
    /// through it.
    /// </summary>
    [Theory]
    [InlineData("1")]
    [InlineData("1000")]
    [InlineData("1e6")]
    public void T2_TheHeldVoltageDoesNotDependOnWhatItDrives(string load)
    {
        double v = V($"""
            Vdc:VS  n1 0  Vdc=1 V
            R:R1    n1 n2  R=100 Ohm
            R:RL    n2 0   R={load} Ohm
            SDD:E1  n2 0  n1 0  V[1]=0.5*_v2
            analysis DC1 type=dc
            """, "n2");

        output.WriteLine($"load {load} Ω → V(n2) = {v:G17}");
        Assert.Equal(0.5, v, 12);
    }

    // ── T3 — nonlinear in a port voltage ──────────────────────────────────────

    /// <summary>
    /// Gated on the CONVERGED BIAS, not on the residual norm. A residual small enough to stop on
    /// says the iteration agreed with itself; only the node voltage says it agreed with the
    /// equation.
    /// </summary>
    [Theory]
    [InlineData("0.8")]
    [InlineData("-1.5")]
    [InlineData("3.0")]
    public void T3_ANonlinearVoltageConstraintReachesTheAnalyticBias(string drive)
    {
        double vin = double.Parse(drive, System.Globalization.CultureInfo.InvariantCulture);
        double v = V($"""
            Vdc:VS  n1 0  Vdc={drive} V
            R:R1    n1 n2  R=100 Ohm
            R:RL    n2 0   R=250 Ohm
            SDD:E1  n2 0  n1 0  V[1]=tanh(_v2)
            analysis DC1 type=dc
            """, "n2");

        output.WriteLine($"V(n1)={vin} → V(n2) = {v:G17}  (expected tanh = {Math.Tanh(vin):G17})");
        Assert.Equal(Math.Tanh(vin), v, 10);
    }

    // ── T4 — the branch equation reads another branch's CURRENT ───────────────

    /// <summary>
    /// <c>DBranchC</c>: ∂g/∂i of the constraint with respect to ANOTHER device's branch current.
    /// Not an edge case — a behavioural source written as a function of a sensed current is
    /// ordinary, and the first real device that works at all leans on it twice.
    ///
    /// <para>I(IP1) = 1 V / 100 Ω = 10 mA, so the source holds n3 at 50 Ω × 10 mA = 0.5 V.</para>
    /// </summary>
    [Fact]
    public void T4_AVoltageConstraintOnASensedBranchCurrent()
    {
        double v = V("""
            Vdc:VS   n1 0  Vdc=1 V
            R:R1     n1 n2  R=100 Ohm
            IProbe:IP1  n2 0

            R:RL     n3 0   R=1000 Ohm
            SDD:E1   n3 0  V[1]=50*_c1  C[1]=IP1
            analysis DC1 type=dc
            """, "n3");

        output.WriteLine($"V(n3) = {v:G17}  (expected 0.5)");
        Assert.Equal(0.5, v, 11);
    }

    /// <summary>
    /// The same circuit with the sensitivity in play: the constraint is linear in <c>_c1</c>, so
    /// with the exact <c>∂g/∂i</c> entry stamped the augmented system is linear and Newton settles
    /// immediately. A missing entry still converges — quasi-Newton does — which is why the gate is
    /// the ITERATION COUNT rather than the answer.
    /// </summary>
    [Fact]
    public void T5_TheControlCurrentSensitivityMakesALinearProblemLinear()
    {
        var (_, r) = DcWithNetlist("""
            Vdc:VS   n1 0  Vdc=1 V
            R:R1     n1 n2  R=100 Ohm
            IProbe:IP1  n2 0
            R:RL     n3 0   R=1000 Ohm
            SDD:E1   n3 0  V[1]=50*_c1  C[1]=IP1
            analysis DC1 type=dc
            """);

        Assert.True(r.Converged);
        output.WriteLine($"iterations = {r.Iterations}");
        Assert.True(r.Iterations <= 3,
            $"A problem linear in every unknown took {r.Iterations} Newton steps, which means the "
            + "branch row's control-current derivative is missing or wrong.");
    }

    // ── T6 — the interior node of the charge idiom is solvable at DC ──────────

    /// <summary>
    /// <b>The case that looks like it should fail and does not.</b> At DC the capacitor is open, so
    /// the interior node is reached ONLY by the source's branch. KCL there forces the branch current
    /// to zero and the branch row fixes the node's voltage: two equations, two unknowns,
    /// non-singular. Nothing about that is obvious from looking at the drawing, so it is asserted
    /// rather than assumed.
    /// </summary>
    [Fact]
    public void T6_TheInteriorNodeOfACharcePairIsNonSingularWithTheCapacitorOpen()
    {
        // E from p to mid holds V(p) − V(mid) = _v2 − Q(_v2)/K with Q(v) = C0·v, so V(mid) = C0·v/K.
        double v = V("""
            C0 = 1e-12
            K  = 1e-9
            Vdc:VS  p 0   Vdc=2 V
            SDD:E1  p mid  p 0  V[1]=_v2-(C0*_v2)/K
            C:C1    mid 0  C=1 nF
            analysis DC1 type=dc
            """, "mid");

        output.WriteLine($"V(mid) = {v:G17}  (expected {1e-12 * 2.0 / 1e-9:G17})");
        Assert.Equal(1e-12 * 2.0 / 1e-9, v, 12);
    }

    // ── T7 — the small-signal charge oracle ───────────────────────────────────

    /// <summary>
    /// <b>Charge oracle 1 (spice-models.md §9.5.4).</b> A behavioural voltage source driving a
    /// linear capacitor stores <c>Q(v)</c> exactly; linearised at a bias, the port's susceptance
    /// must be <c>ω·dQ/dv</c> evaluated there — compared against the ANALYTIC derivative of the
    /// charge function, never against another circuitRF path.
    ///
    /// <para>The pair: <c>E</c> from <c>p</c> to <c>mid</c> holding
    /// <c>V(p) − V(mid) = v − Q(v)/K</c>, and a linear <c>K</c> from <c>mid</c> to ground. The
    /// stored charge is then <c>K·V(mid) = Q(v)</c> and the capacitor's value cancels exactly — it
    /// is a scaling constant, and the device's real content is the charge function.</para>
    ///
    /// <para>With <c>Q(v) = C0·v + a·v³/3</c>, <c>dQ/dv = C0 + a·v²</c>. The bias arrives through a
    /// resistor, and its value is a real constraint in both directions: the DC engine's conductance
    /// floor sits between the port node and ground, so the node settles a fraction
    /// <c>gmin·R</c> away from the source — at 10 MΩ that is 1e-5, which moves <c>dQ/dv</c> by 1e-5
    /// and is measurable here. 10 kΩ puts it at 1e-8. Its conductance adds to the REAL part of Y and
    /// leaves the susceptance alone, which is why it can be made small freely.</para>
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(-0.7)]
    public void T7_ChargeOracle_SmallSignalSusceptanceIsTheAnalyticDerivative(double bias)
    {
        const double C0 = 0.5e-12, A = 3.0e-12, K = 1e-9;
        double freq = 1e9, omega = 2 * Math.PI * freq;

        string cnl = $"""
            Port:P1  p 0  Num=1 Z=50 Ohm
            Vdc:VB   b 0  Vdc={bias.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} V
            R:RB     b p  R=1e4 Ohm
            SDD:E1   p mid  p 0  V[1]=_v2-({C0:R}*_v2+({A:R})*_v2^3/3)/{K:R}
            C:C1     mid 0  C={K:R}
            """;

        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, [freq]);

        // Y11 from S11 for a one-port referenced to Z0: Y = (1 − S)/(Z0·(1 + S)).
        var s11 = (Complex)ds["S"][0, 0, 0];
        Complex y11 = (1 - s11) / (50.0 * (1 + s11));

        double expected = omega * (C0 + A * bias * bias);
        output.WriteLine($"bias {bias} V: B = {y11.Imaginary:G8} S, ω·dQ/dv = {expected:G8} S, "
                       + $"G = {y11.Real:G3} S");

        // The bias resistor contributes 1e-7 S of real part and nothing measurable in quadrature.
        Assert.Equal(expected, y11.Imaginary, 1.0e-7 * Math.Max(expected, 1e-3));
    }

    /// <summary>
    /// The same pair with a LINEAR charge must be indistinguishable from the capacitor it is — the
    /// degenerate case, which is what catches a scale factor that happens to cancel at one bias.
    /// </summary>
    [Fact]
    public void T8_ALinearChargeIsExactlyTheCapacitorItStates()
    {
        double freq = 2.5e9;

        var (libA, tbA) = new CnlReader().Read("""
            Port:P1  p 0  Num=1 Z=50 Ohm
            SDD:E1   p mid  p 0  V[1]=_v2-(0.8e-12*_v2)/1e-9
            C:C1     mid 0  C=1 nF
            """);
        var (libB, tbB) = new CnlReader().Read("""
            Port:P1  p 0  Num=1 Z=50 Ohm
            C:C1     p 0  C=0.8 pF
            """);

        var a = SParameterEngine.Run(new Elaborator(libA).Elaborate(tbA), [freq]);
        var b = SParameterEngine.Run(new Elaborator(libB).Elaborate(tbB), [freq]);

        Complex sa = (Complex)a["S"][0, 0, 0], sb = (Complex)b["S"][0, 0, 0];
        output.WriteLine($"pair S11 = {sa}, plain capacitor S11 = {sb}");
        Assert.Equal(sb.Real,      sa.Real,      10);
        Assert.Equal(sb.Imaginary, sa.Imaginary, 10);
    }

    // ── T9 — harmonic balance refuses rather than dropping the constraint ─────

    /// <summary>
    /// A device evaluating to zero port current would let an HB run converge on a circuit in which
    /// the source is simply absent — a plausible answer with nothing wrong on the face of it. It is
    /// refused by name instead, and the message says what circuitRF does have for the circuit.
    /// </summary>
    [Fact]
    public void T9_HarmonicBalanceRefusesANonlinearVoltageConstraintByName()
    {
        var (lib, tb) = new CnlReader().Read("""
            Vdc:VS  n1 0  Vdc=1 V
            R:R1    n1 n2  R=100 Ohm
            R:RL    n2 0   R=250 Ohm
            SDD:E1  n2 0  n1 0  V[1]=tanh(_v2)
            """);
        var nl = new Elaborator(lib).Elaborate(tb);

        var ex = Assert.Throws<InvalidOperationException>(() => new HbEngine(nl, tb));
        output.WriteLine(ex.Message);
        Assert.Contains("E1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("V[p]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("S-parameter", ex.Message, StringComparison.Ordinal);
    }

    // ── T10 — a port cannot state both what it is and what it carries ─────────

    [Fact]
    public void T10_APortStatingBothAVoltageAndACurrentIsRefused()
    {
        var (lib, tb) = new CnlReader().Read("""
            R:R1    n1 0  R=100 Ohm
            SDD:E1  n1 0  V[1]=1  I[1,0]=_v1/50
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => new Elaborator(lib).Elaborate(tb));
        Assert.Contains("contradiction", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── M4b — the collapsed charge, and the two HB oracles it makes possible ──

    /// <summary>
    /// <b>The two paths must agree, and if they ever disagree the collapse is wrong.</b> The same
    /// physics written both ways: as the pair a library file writes (a behavioural voltage source
    /// holding <c>v − Q(v)/K</c> into a linear <c>K</c>), and as the one charge equation the
    /// importer rewrites that pair into. The capacitor's own value cancels, so the collapsed device
    /// states <c>Q(v)</c> and nothing else.
    ///
    /// <para>Compared over frequency AND at a bias where the charge is nonlinear, because a scale
    /// factor and a sign both survive a single-point comparison.</para>
    /// </summary>
    [Theory]
    [InlineData(0.5e9)]
    [InlineData(2.5e9)]
    [InlineData(9.0e9)]
    public void M4b1_TheCollapsedPathAgreesWithTheGeneralPathEntryByEntry(double freq)
    {
        const double C0 = 0.7e-12, A = 2.0e-12, K = 1e-9, Bias = 0.6;

        string common = $"""
            Port:P1  p 0  Num=1 Z=50 Ohm
            Vdc:VB   b 0  Vdc={Bias:R} V
            R:RB     b p  R=1e4 Ohm
            """;
        string charge = $"{C0:R}*_v1+({A:R})*_v1^3/3";

        var general = SParameterEngine.Run(Elaborate($"""
            {common}
            SDD:E1   p mid  p 0  V[1]=_v2-({C0:R}*_v2+({A:R})*_v2^3/3)/{K:R}
            C:C1     mid 0  C={K:R}
            """), [freq]);

        var collapsed = SParameterEngine.Run(Elaborate($"""
            {common}
            SDD:E1   p 0  I[1,1]={charge}
            """), [freq]);

        Complex g = (Complex)general["S"][0, 0, 0], c = (Complex)collapsed["S"][0, 0, 0];
        output.WriteLine($"{freq / 1e9:F1} GHz: general S11 = {g}, collapsed S11 = {c}");
        Assert.Equal(g.Real,      c.Real,      10);
        Assert.Equal(g.Imaginary, c.Imaginary, 10);
    }

    /// <summary>
    /// <b>Charge oracle 2 (spice-models.md §9.5.4) — charge conservation.</b> Over one period,
    /// ∮ i dt = 0 on a charge branch, which in the frequency domain is exactly: the DC component of
    /// the current a pure charge carries is zero. A resistive contamination of a charge term breaks
    /// this and nothing else catches it — the answer still converges and every harmonic still looks
    /// reasonable.
    ///
    /// <para>This is also the test that could not exist before the collapse: harmonic balance
    /// refuses the branch-row form (T9), so the pair had to become one charge equation before any
    /// large-signal question could be put to it at all.</para>
    /// </summary>
    [Fact]
    public void M4b2_ChargeOracle_APureChargeCarriesNoDcCurrent()
    {
        // The drive carries a DC OFFSET, so the question is not vacuous: a charge with any
        // resistive contamination in it would pass a DC current at 0.4 V and this would catch it.
        var ds = HbOnTheCollapsedCharge(driveVolts: 1.0, offsetVolts: 0.4);

        var i0 = DeviceCurrent(ds, 0);
        var i1 = DeviceCurrent(ds, 1);
        output.WriteLine($"|I(k=0)| = {i0.Magnitude:E3} A against |I(k=1)| = {i1.Magnitude:E3} A");

        Assert.True(i1.Magnitude > 1e-4, "the drive must actually be exercising the charge");
        Assert.True(i0.Magnitude < 1e-8 * i1.Magnitude,
                    $"a pure charge carries no DC current; got {i0.Magnitude:E3} A");
    }

    /// <summary>
    /// <b>Charge oracle 3 — harmonic content.</b> The one that fails if the charge were linearised
    /// about the DC point instead of evaluated in the time domain and transformed: a linearisation
    /// generates NO second harmonic at all, and still converges.
    ///
    /// <para>With <c>Q(v) = C0·v + a·v²/2</c> and <c>v(t) = V₀ + A·cos ωt</c>, the charge's second
    /// harmonic is <c>(a·A²/4)·cos 2ωt</c> and its fundamental <c>A(C0 + a·V₀)·cos ωt</c>, so
    /// differentiating gives an amplitude ratio of <c>a·A / 2(C0 + a·V₀)</c> — independent of ω and
    /// of whatever phasor normalisation the engine uses, which is why the RATIO is what is
    /// asserted.</para>
    ///
    /// <para>The drive reaches the device through a small resistance, so <c>v(t)</c> is a pure tone
    /// only to the extent that the device's own current does not load it; the residual distortion
    /// is what the tolerance is for, and it is reported alongside the answer.</para>
    /// </summary>
    [Fact]
    public void M4b3_ChargeOracle_TheSecondHarmonicIsTheAnalyticOne()
    {
        const double C0 = 1e-12, Aq = 1e-12, Drive = 1.0, Offset = 0.4;

        var ds   = HbOnTheQuadraticCharge(C0, Aq, Drive, Offset);
        int node = NodeIndex(ds, "p");

        var i1 = DeviceCurrent(ds, 1);
        var i2 = DeviceCurrent(ds, 2);
        var v1 = (Complex)ds["V"][node, 1];
        var v2 = (Complex)ds["V"][node, 2];

        // The drive's own purity bounds how exactly the closed form can hold.
        double distortion = v2.Magnitude / v1.Magnitude;
        double measured   = i2.Magnitude / i1.Magnitude;
        double expected   = Aq * Drive / (2.0 * (C0 + Aq * Offset));

        output.WriteLine($"|I2|/|I1| = {measured:F6} against a·A/2C0 = {expected:F6}; "
                       + $"drive distortion |V2|/|V1| = {distortion:E2}");

        Assert.True(distortion < 1e-2, $"the drive is too soft to hold the closed form: {distortion:E2}");
        Assert.Equal(expected, measured, 2);
    }

    // ── the two HB fixtures ───────────────────────────────────────────────────

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static DataSet Hb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var ds  = (DataSet)new HbEngine(nl, tb).Run(HbEngine.Resolve(hba, nl.ResolvedGlobals));

        Assert.True(ds["Converged"].RealValues[0] > 0.5, "HB did not converge");
        return ds;
    }

    private static int NodeIndex(DataSet ds, string node)
    {
        int idx = Array.IndexOf(ds["V"].Axes[0].Labels!, node);
        Assert.True(idx >= 0, $"'{node}' is not a node of the run");
        return idx;
    }

    /// <summary>
    /// The current the charge device actually carries, harmonic by harmonic — read as the drop
    /// across the series resistor that feeds it.
    ///
    /// <para><b>Taken from the node voltages on purpose, rather than from the run's own branch
    /// spectrum.</b> Node voltages ARE harmonic balance's unknowns, so a current derived from two
    /// of them and a resistor is the solved answer and nothing else. The <c>I</c> cube's own row for
    /// a series probe is not usable here: it reports <c>C0·V</c> for a charge-carrying branch —
    /// the linear charge, with neither the <c>jkω</c> nor the nonlinear part — and it does so
    /// identically for a plain <c>NonlinearC</c>, which long predates any of this. That is a
    /// REPORTING defect on a path these tests deliberately do not depend on; the solve itself is
    /// exact, which is what the numbers below are.</para>
    /// </summary>
    private static Complex DeviceCurrent(DataSet ds, int harmonic)
    {
        var v = ds["V"];
        var from = (Complex)v[NodeIndex(ds, "s"), harmonic];
        var to   = (Complex)v[NodeIndex(ds, "p"), harmonic];
        return (from - to) / SeriesFeed;
    }

    /// <summary>The resistance the drive reaches the charge through, in ohms.</summary>
    private const double SeriesFeed = 1.0;

    /// <summary>The cubic charge of T7, collapsed — the same device, in the form HB can carry.</summary>
    private static DataSet HbOnTheCollapsedCharge(double driveVolts, double offsetVolts) => Hb($"""
        V_1Tone:VT  s 0  Vdc={offsetVolts:R}  Freq=1e9  V={driveVolts:R}  Phase=0
        R:RS        s p  R=1 Ohm
        SDD:CQ      p 0  I[1,1]=0.7e-12*_v1+(2.0e-12)*_v1^3/3

        analysis DC1  type=dc
        analysis HB1  type=hb  Tone=1e9  MaxHarm=4  Tol=1e-10
        """);

    /// <summary>A QUADRATIC charge, whose second harmonic has a closed form in one term.</summary>
    private static DataSet HbOnTheQuadraticCharge(double c0, double a, double drive, double offset)
        => Hb($"""
        V_1Tone:VT  s 0  Vdc={offset:R}  Freq=1e9  V={drive:R}  Phase=0
        R:RS        s p  R=1 Ohm
        SDD:CQ      p 0  I[1,1]={c0:R}*_v1+({a:R})*_v1^2/2

        analysis DC1  type=dc
        analysis HB1  type=hb  Tone=1e9  MaxHarm=4  Tol=1e-10
        """);
}
