using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.HarmonicBalance;
using RfCore.Data;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// A branch whose nonlinear content is CHARGE must report its actual current.
///
/// <para>The defect these gate: a device's terminal current is
/// <c>Σ_w H[w](kω)·FT{I[p,w]}</c> — conduction PLUS <c>jkω·</c>charge — and the nonlinear injection
/// KCL balances at an interface node is the same sum. Both were formed from the w=0 term alone, so
/// a <c>NonlinearC</c> (whose <c>res.I[p]</c> is identically zero at every sample) published its
/// own port row as exactly zero, and the linear back-solve that recovers an IProbe current and a
/// linear-only node voltage was handed a zero injection — leaving the probe reading the node's
/// gmin leakage instead of its current.</para>
///
/// <para>The oracle is the closed-form series divider, not another circuitRF path: for a
/// constant-C <c>NonlinearC</c> the circuit is an ordinary R–C series branch across a 1 V tone, so
/// <c>I = jωC/(1 + jωRC)</c> exactly.</para>
///
/// T1 — SingleTone_ProbeInSeriesWithCharge_IsTheClosedFormCurrent
/// T2 — SingleTone_ChargeDevicePortRow_IsTheSameCurrent
/// T3 — SingleTone_LinearOnlyNodeBetweenProbeAndCharge_SeesNoDropAcrossTheProbe
/// T4 — SingleTone_SddChargePort_MatchesTheNonlinearC
/// T5 — TwoTone_ChargeBranch_CarriesCurrentAtEveryDrivenProduct
/// </summary>
public class HbChargeBranchCurrentTests(ITestOutputHelper output)
{
    private const double Rs   = 1.0;      // Ω, series resistance ahead of the probe
    private const double Cval = 5e-12;    // F
    private const double Ftone = 1e9;     // Hz

    // Vs — Rs — IP1 — C1 — gnd.  n_a is linear-only (the probe's own mid-node), n_c is the
    // nonlinear-facing interface node.
    private const string ChargeCnl = @"
V_1Tone:Vs    n_in 0    Vdc=0  Freq=1e9  V=1  Phase=0
R:Rs          n_in n_a  R=1
IProbe:IP1    n_a n_c
NonlinearC:C1 n_c 0     C0=5e-12

analysis HB1  type=hb  Tone=1e9  MaxHarm=3  Tol=1e-12
";

    // The same branch written as an SDD charge equation: I[1,1] = Q(v) = C0·v.
    private const string SddChargeCnl = @"
V_1Tone:Vs    n_in 0    Vdc=0  Freq=1e9  V=1  Phase=0
R:Rs          n_in n_a  R=1
IProbe:IP1    n_a n_c
SDD:X1        n_c 0     Ports=1  I[1,0]=0  I[1,1]=5e-12*_v1

analysis HB1  type=hb  Tone=1e9  MaxHarm=3  Tol=1e-12
";

    private const string TwoToneChargeCnl = @"
V_nTone:Vs    n_in 0    Vdc=0  NumFreqs=2  Freq[1]=1e9 V[1]=1 Phase[1]=0  Freq[2]=1.1e9 V[2]=0.5 Phase[2]=0
R:Rs          n_in n_a  R=1
IProbe:IP1    n_a n_c
NonlinearC:C1 n_c 0     C0=5e-12

analysis HB1  type=hb  NumFreqs=2  Tone[1]=1e9  Tone[2]=1.1e9  MaxMixOrder=3  MaxHarm=3  Tol=1e-12
";

    private static DataSet RunHb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().First();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals);
        return (DataSet)new HbEngine(nl, tb).Run(p);
    }

    private static Complex Branch(DataSet ds, string name, int k)
    {
        var cube = ds["I"];
        int b    = Array.IndexOf(cube.Axes[0].Labels!, name);
        Assert.True(b >= 0, $"branch '{name}' must be in the I cube");
        return cube.ComplexValues[b * cube.Axes[1].Length + k];
    }

    private static Complex Node(DataSet ds, string name, int k)
    {
        var cube = ds["V"];
        int n    = Array.IndexOf(cube.Axes[0].Labels!, name);
        Assert.True(n >= 0, $"node '{name}' must be in the V cube");
        return cube.ComplexValues[n * cube.Axes[1].Length + k];
    }

    /// <summary>Closed form for the shipped fixture: <c>I = jωC / (1 + jωR C)</c> at a 1 V drive.</summary>
    private static Complex ClosedFormCurrent(double freqHz, double amplitudeV)
    {
        var jwC = new Complex(0, 2.0 * Math.PI * freqHz * Cval);
        return amplitudeV * jwC / (Complex.One + Rs * jwC);
    }

    private static void AssertClose(Complex expected, Complex actual, double rtol, string what)
    {
        double err = Complex.Abs(actual - expected) / Complex.Abs(expected);
        Assert.True(err < rtol,
            $"{what}: expected {expected}, got {actual} (relative error {err:E3} ≥ {rtol:E1})");
    }

    // ── T1 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void SingleTone_ProbeInSeriesWithCharge_IsTheClosedFormCurrent()
    {
        var ds = RunHb(ChargeCnl);

        var expected = ClosedFormCurrent(Ftone, 1.0);
        var actual   = Branch(ds, "IP1", 1);
        output.WriteLine($"closed form {expected}   IP1 {actual}");

        // The pre-fix reading was the node's gmin leakage — ~1e-12 A against ~3e-2 A here.
        AssertClose(expected, actual, 1e-7, "IP1 fundamental");

        // A pure charge branch conducts nothing at DC, and a linear C generates no harmonics.
        Assert.True(Complex.Abs(Branch(ds, "IP1", 0)) < 1e-15, "no DC current through a capacitor");
        Assert.True(Complex.Abs(Branch(ds, "IP1", 2)) < 1e-12, "a constant C generates no 2f0");
    }

    // ── T2 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void SingleTone_ChargeDevicePortRow_IsTheSameCurrent()
    {
        var ds = RunHb(ChargeCnl);

        var expected = ClosedFormCurrent(Ftone, 1.0);
        var port     = Branch(ds, "C1:0", 1);   // port-index alias for the single-port device
        output.WriteLine($"closed form {expected}   C1:0 {port}");

        // This row was exactly zero before: NonlinearC's res.I[p] is 0 at every sample.
        AssertClose(expected, port, 1e-9, "C1 port-0 fundamental");
    }

    // ── T3 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void SingleTone_LinearOnlyNodeBetweenProbeAndCharge_SeesNoDropAcrossTheProbe()
    {
        var ds = RunHb(ChargeCnl);

        // n_a is touched only by the source and the probe, so it is recovered by the linear
        // back-solve — with the wrong injection it came back as the undropped source voltage.
        var va = Node(ds, "n_a", 1);
        var vc = Node(ds, "n_c", 1);
        output.WriteLine($"V(n_a) {va}   V(n_c) {vc}");

        Assert.True(Complex.Abs(va - vc) < 1e-9 * Complex.Abs(vc),
            $"an IProbe is a short: V(n_a)={va} must equal V(n_c)={vc}");
    }

    // ── T4 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void SingleTone_SddChargePort_MatchesTheNonlinearC()
    {
        var ds = RunHb(SddChargeCnl);

        var expected = ClosedFormCurrent(Ftone, 1.0);
        output.WriteLine($"closed form {expected}   IP1 {Branch(ds, "IP1", 1)}   " +
                         $"X1:0 {Branch(ds, "X1:0", 1)}");

        AssertClose(expected, Branch(ds, "IP1",  1), 1e-7, "SDD charge: IP1 fundamental");
        AssertClose(expected, Branch(ds, "X1:0", 1), 1e-9, "SDD charge: X1 port-0 fundamental");
    }

    // ── T5 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoTone_ChargeBranch_CarriesCurrentAtEveryDrivenProduct()
    {
        var ds = RunHb(TwoToneChargeCnl);

        // The mixIndex axis VALUES are the signed product frequencies in Hz — address the two
        // carriers by frequency rather than by label spelling.
        var mixAxis = ds["I"].Axes[1];
        int Find(double fHz)
        {
            for (int m = 0; m < mixAxis.Length; m++)
                if (Math.Abs(mixAxis.Values[m] - fHz) < 1.0) return m;
            Assert.Fail($"product at {fHz:E3} Hz must be retained; " +
                        $"got {string.Join(",", mixAxis.Values)}");
            return -1;
        }

        foreach (var (f, amp) in new[] { (1.0e9, 1.0), (1.1e9, 0.5) })
        {
            int m        = Find(f);
            var expected = ClosedFormCurrent(f, amp);
            var probe    = Branch(ds, "IP1",  m);
            var port     = Branch(ds, "C1:0", m);
            output.WriteLine($"m={m} @ {f:E3} Hz: closed form {expected}  IP1 {probe}  C1:0 {port}");

            AssertClose(expected, probe, 1e-7, $"IP1 at {f:E3} Hz");
            AssertClose(expected, port,  1e-9, $"C1 port-0 at {f:E3} Hz");
        }
    }
}
