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
/// A nonlinear device port that spans two LIVE nets — neither one ground — must contribute its
/// current at BOTH nets, entering the + net and leaving the − net.
///
/// <para>This went wrong for a long time without showing, because every circuit in the suite
/// referenced its device ports to ground: an SDD written <c>SDD:M1 n_gate 0 n_drain 0</c> has both
/// port references at node 0, which is not an interface node, so "accumulate only at +" and
/// "accumulate at + and −" give the same answer. A diode floating across two nets — the ring quad
/// of a passive mixer, a bridge, a series-connected device — is the case that separates them. With
/// the current injected and never removed, KCL at the − net is wrong and the solve converges
/// cleanly to the wrong answer, which is the expensive kind of bug.</para>
///
/// <para>The oracle is a closed-form solution of the same circuit, not another circuitRF path, so
/// these tests can fail as a group only if the model equations themselves are wrong.
/// <see cref="NonlinearDcEngine"/> has always stamped both signs; T2 pins HB to it.</para>
/// </summary>
public class FloatingPortHbTests(ITestOutputHelper output)
{
    // Series chain Vs — R1 — na — D — nb — R2 — gnd. Both diode nets are live interface nodes.
    // The netlists below are written out rather than interpolated, so what the reader parses is
    // exactly what a user would type; these constants are the oracle's copy of the same numbers.
    private const double Vs = 2.0, R1 = 100.0, R2 = 100.0, Is = 1e-12, NEmission = 1.0;

    private const string FloatingCnl = """
        V_1Tone:VS  n1 0   Vdc=2.0
        R:R1  n1 na  R=100
        Diode:D1  na nb  Is=1e-12 N=1.0
        R:R2  nb 0   R=100
        analysis HB1 type=hb  Tone=1e9  MaxHarm=1  Tol=1e-12
        """;

    // Same chain with the diode's cathode grounded and R2 removed — the case that already worked.
    private const string GroundedCnl = """
        V_1Tone:VS  n1 0   Vdc=2.0
        R:R1  n1 na  R=100
        Diode:D1  na 0  Is=1e-12 N=1.0
        analysis HB1 type=hb  Tone=1e9  MaxHarm=1  Tol=1e-12
        """;

    /// <summary>
    /// The closed-form answer: Vs = I·(R1+R2) + N·Vt·ln(I/Is + 1), solved by fixed-point iteration.
    /// Vt = kT/q at the diode's own nominal temperature.
    /// </summary>
    private static double SeriesCurrent(double rTotal)
    {
        const double vt = 1.380649e-23 * (CircuitRF.Core.Devices.Fet.FetModelBase.NominalTemperatureC + 273.15)
                          / 1.602176634e-19;
        double i = 5e-3;
        for (int n = 0; n < 500; n++) i = (Vs - NEmission * vt * Math.Log(i / Is + 1.0)) / rTotal;
        return i;
    }

    private static DataSet RunHb(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl, "tb");
        var nl  = new Elaborator(lib).Elaborate(tb);
        var hba = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        var p   = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        return new HbEngine(nl, tb).Run(p).DataSet;
    }

    private static double DcVolts(DataSet ds, string node)
    {
        var v = ds["V"];
        int idx = Array.IndexOf(v.Axes[0].Labels!, node);
        Assert.True(idx >= 0, $"node '{node}' not on the V axis: {string.Join(", ", v.Axes[0].Labels!)}");
        return ((Complex)v[idx, 0]).Real;
    }

    [Fact]
    public void T1_FloatingDiode_SingleTone_MatchesClosedForm()
    {
        double i = SeriesCurrent(R1 + R2);
        double expectedNa = Vs - R1 * i, expectedNb = R2 * i;
        output.WriteLine($"closed form: I={i * 1e3:F6} mA  V(na)={expectedNa:F6}  V(nb)={expectedNb:F6}");

        var ds = RunHb(FloatingCnl);
        double na = DcVolts(ds, "na"), nb = DcVolts(ds, "nb");
        output.WriteLine($"HB:          V(na)={na:F6}  V(nb)={nb:F6}");

        // Before the both-signs fix this was V(na)=0.604, V(nb)=0 — the cathode net floated to
        // ground because the diode's current was injected at the anode and never taken out.
        Assert.Equal(expectedNa, na, 6);
        Assert.Equal(expectedNb, nb, 6);
    }

    [Fact]
    public void T2_FloatingDiode_HbDcTerm_AgreesWithDcEngine()
    {
        var (lib, tb) = new CnlReader().Read(FloatingCnl, "tb");
        var nl  = new Elaborator(lib).Elaborate(tb);
        var dc  = NonlinearDcEngine.Run(nl);
        Assert.True(dc.Converged);

        var ds = RunHb(FloatingCnl);
        foreach (var node in new[] { "na", "nb" })
        {
            double dcV = dc.NodeVoltages[nl.Nodes.IndexOf(node) - 1];
            output.WriteLine($"{node}: DC engine {dcV:F9}   HB k=0 {DcVolts(ds, node):F9}");
            Assert.Equal(dcV, DcVolts(ds, node), 6);
        }
    }

    [Fact]
    public void T3_GroundedDiode_Unchanged()
    {
        // The pre-existing case must be untouched by the fix: with the cathode at node 0 the
        // minus-side stamp finds no interface node and contributes nothing.
        double i = SeriesCurrent(R1);
        var ds = RunHb(GroundedCnl);
        output.WriteLine($"closed form V(na)={Vs - R1 * i:F6}   HB {DcVolts(ds, "na"):F6}");
        Assert.Equal(Vs - R1 * i, DcVolts(ds, "na"), 6);
    }

    [Fact]
    public void T4_FloatingDiode_TwoTone_MatchesClosedForm()
    {
        // The two-tone assembler is a separate code path with the same rule; it had the same gap.
        const string twoTone = """
            V_1Tone:VS  n1 0   Vdc=2.0
            R:R1  n1 na  R=100
            Diode:D1  na nb  Is=1e-12 N=1.0
            R:R2  nb 0   R=100
            analysis HB1 type=hb  NumFreqs=2  Tone[1]=1e9  Tone[2]=1.1e9  MaxHarm=2  MaxMixOrder=2  Tol=1e-12
            """;

        double i = SeriesCurrent(R1 + R2);
        var ds = RunHb(twoTone);

        // Undriven, so every mixing product but (0,0) is zero and the DC bin is the whole answer.
        Assert.Equal(Vs - R1 * i, DcVolts(ds, "na"), 6);
        Assert.Equal(R2 * i,      DcVolts(ds, "nb"), 6);
    }

    [Fact]
    public void T5_FloatingDiode_AnalyticJacobian_MatchesFiniteDifference()
    {
        // The residual and the Jacobian are stamped by different code. A current-only fix would
        // still leave the 4-corner derivative block wrong — visible as slow convergence rather
        // than a wrong answer, so it needs its own check against the FD oracle.
        const string driven = """
            V_1Tone:VS  n1 0   Vdc=2.0
            V_1Tone:VD  n2 0   V=0.3  Freq=1e9
            R:Rd  n2 na  R=50
            R:R1  n1 na  R=100
            Diode:D1  na nb  Is=1e-12 N=1.0 Cj0=0.4 pF
            R:R2  nb 0   R=100
            analysis HB1 type=hb  Tone=1e9  MaxHarm=3  Tol=1e-12
            """;

        var (lib, tb) = new CnlReader().Read(driven, "tb");
        var nl     = new Elaborator(lib).Elaborate(tb);
        var hba    = tb.Analyses.OfType<HarmonicBalanceAnalysis>().Single();
        var p      = HbEngine.Resolve(hba, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var engine = new HbEngine(nl, tb);
        var run    = engine.Run(p);
        Assert.True(run.Converged);

        var cmp = engine.RunJacobianDiagnostic(p, run.InterfaceV!, sweepVal: 0.0);
        output.WriteLine($"max |ΔJ| = {cmp.MaxAbsError:E3}   max relative = {cmp.MaxRelError:E3}");
        Assert.True(cmp.MaxRelError < 1e-5,
            $"analytic Jacobian disagrees with finite difference: relative {cmp.MaxRelError:E3}");
    }
}
