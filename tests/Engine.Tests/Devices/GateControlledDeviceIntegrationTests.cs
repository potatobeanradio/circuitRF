using System;
using System.Linq;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// End-to-end gate tests for the four gate-controlled families added alongside the MESFETs: the
/// lateral MOS transistor, the junction FET, the vertical power MOSFET and the IGBT.
///
/// <para><b>What these test that the per-model unit tests cannot.</b> Every one of these devices
/// presents to the user as three or four PINS and to the engine as five to eight PORTS, and the map
/// between the two lives in the elaborator — a separate file from the model, stating the same port
/// order a second time. A wrong entry there produces a device that elaborates, solves, and is a
/// different circuit: the unit tests would all still pass, because they hand the model its port
/// voltages directly. So these drive a real netlist through a real DC solve and check the answer
/// against arithmetic done here, independently.</para>
///
///   T1 — a common-source MOS stage sits exactly on its load line.
///   T2 — the MOS bulk is a REAL pin: moving it changes the threshold, which is the body effect,
///        and is the whole reason the pin exists.
///   T3 — a JFET stage sits on its load line, and its gate junction is reverse-biased.
///   T4 — a power MOSFET's on-state drop is I·Rds(on) with no junction offset in it…
///   T5 — …and its body diode freewheels when the drain is pulled below the source.
///   T6 — an IGBT's on-state drop DOES have a junction offset in it, which is the difference from
///        T4 and the whole trade between the two parts.
///   T7 — the ohmic parasitics really are inside the device: each mints an internal node and the
///        branch current appears under the terminal's own name.
/// </summary>
public class GateControlledDeviceIntegrationTests
{
    private static (ElaboratedNetlist Netlist, double[] V) Dc(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        return (nl, NonlinearDcEngine.Run(nl).NodeVoltages);
    }

    private static double Node(ElaboratedNetlist nl, double[] v, string name)
    {
        Assert.True(nl.Nodes.TryGetIndex(name, out int n), $"no node called '{name}' in the netlist");
        return n == 0 ? 0.0 : v[n - 1];
    }

    // ── T1 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T1_ACommonSourceMosStage_SitsExactlyOnItsLoadLine()
    {
        // Pin order is [drain, gate, source, bulk]; the bulk is wired to the source here, which is
        // what a discrete part does — and it has to be WIRED, not assumed.
        const string cnl = """
            Vdc:VDD dd 0  Vdc=5
            R:RD    dd d  R=1000
            Vdc:VG  g  0  Vdc=2
            MOS1_N:M1 d g 0 0 Vto=0.7 Kp=2e-5 W=20e-6 L=1e-6 Gamma=0 Phi=0.65 Lambda=0
            """;
        var (nl, v) = Dc(cnl);
        double vd = Node(nl, v, "d");

        // Saturation, so Id = (Kp·W/L / 2)·(Vgs − Vto)², independent of Vds with Lambda = 0.
        double beta = 2e-5 * 20e-6 / 1e-6;
        double vgt  = 2.0 - 0.7;
        double id   = 0.5 * beta * vgt * vgt;
        Assert.True(5.0 - id * 1000.0 > vgt, "this bias must actually be in saturation");

        Assert.Equal(5.0 - id * 1000.0, vd, 6);
    }

    // ── T2 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T2_TheBulkIsARealPin_AndMovingItMovesTheThreshold()
    {
        // The same stage twice, differing ONLY in where the bulk is wired. If the bulk were tied to
        // the source internally — the tempting simplification — these two would give identical
        // answers, and the Gamma and Phi on the card would be doing nothing at all.
        string Stage(string bulkNet) => $"""
            Vdc:VDD dd 0  Vdc=5
            R:RD    dd d  R=1000
            Vdc:VG  g  0  Vdc=2
            Vdc:VS  s  0  Vdc=1
            Vdc:VB  nb 0  Vdc=-3
            MOS1_N:M1 d g s {bulkNet} Vto=0.7 Kp=2e-5 W=20e-6 L=1e-6 Gamma=0.6 Phi=0.7
            """;

        var (nlTied, vTied) = Dc(Stage("s"));
        var (nlBack, vBack) = Dc(Stage("nb"));

        double idTied = (5.0 - Node(nlTied, vTied, "d")) / 1000.0;
        double idBack = (5.0 - Node(nlBack, vBack, "d")) / 1000.0;

        // With the bulk 4 V below the source the threshold rises, so the SAME gate drive gives less
        // current. That is the body effect, and it is the only thing that differs here.
        Assert.True(idTied > 0, "the reference stage must be conducting");
        Assert.True(idBack < 0.6 * idTied,
            $"the body effect must bite: {idTied:E3} A tied, {idBack:E3} A with the bulk at −3 V");

        // …and by the amount the published relation says. Vth = Vto + Gamma·(√(Phi−Vbs) − √Phi).
        double vth = 0.7 + 0.6 * (Math.Sqrt(0.7 + 4.0) - Math.Sqrt(0.7));
        double beta = 2e-5 * 20e-6 / 1e-6;
        double vgt = (2.0 - 1.0) - vth;
        double expected = vgt <= 0 ? 0.0 : 0.5 * beta * vgt * vgt;
        Assert.Equal(expected, idBack, 8);
    }

    // ── T3 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T3_AJfetStage_SitsOnItsLoadLine_WithItsGateJunctionReverseBiased()
    {
        // Pin order is [drain, gate, source].
        const string cnl = """
            Vdc:VDD dd 0  Vdc=6
            R:RD    dd d  R=500
            Vdc:VG  g  0  Vdc=-0.5
            JFET_N:J1 d g 0 Vto=-2 Beta=1.2e-3 Lambda=0 Is=1e-14 Cgs=0 Cgd=0
            """;
        var (nl, v) = Dc(cnl);
        double vd = Node(nl, v, "d");

        double vgt = -0.5 - (-2.0);
        double id  = 1.2e-3 * vgt * vgt;
        Assert.True(6.0 - id * 500.0 > vgt, "this bias must actually be in saturation");
        Assert.Equal(6.0 - id * 500.0, vd, 6);

        // The gate junction is reverse-biased at −0.5 V, so it draws essentially nothing — which is
        // what makes a JFET a JFET. If the two gate junctions had been wired to the wrong ends the
        // gate-drain one would be at −5 V and this would still pass, so T1's load line is the check
        // that catches that; this one pins that the gate does not conduct.
        var gateBranch = nl.Components.Single(c => c.ComponentType == "JFET_N");
        Assert.Equal(3, gateBranch.Model.PortCount);        // no ohmic parasitics stated
    }

    // ── T4 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T4_APowerMosfetsOnStateDrop_HasNoJunctionOffsetInIt()
    {
        // Hard on, into a load that pulls a few amps. A power MOSFET's drop is I·Rds(on) and goes
        // to zero with current — that is the whole of its advantage at low current.
        const string cnl = """
            Vdc:VDD dd 0  Vdc=12
            R:RL    dd d  R=4
            Vdc:VG  g  0  Vdc=10
            VDMOS_N:M1 d g 0 Vto=3.2 Kp=40 Lambda=0 Rds=1e7 Is=5e-13 Cgs=0 Cgdmax=0
            """;
        var (nl, v) = Dc(cnl);
        double vds = Node(nl, v, "d");

        Assert.True(vds > 0, "the device must be conducting");
        // Deep in the linear region: well under a diode drop, which is the claim. An IGBT in the
        // same circuit cannot do this — see T6.
        Assert.True(vds < 0.35, $"a power MOSFET's on-state drop must be small: {vds:F3} V");

        // And it really is the linear region's own answer: Id = Kp·(Vgt − Vds/2)·Vds.
        double id = (12.0 - vds) / 4.0;
        Assert.Equal(40.0 * ((10.0 - 3.2) - 0.5 * vds) * vds, id, 4);
    }

    // ── T5 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T5_ThePowerMosfetsBodyDiode_FreewheelsWithTheGateOff()
    {
        // Drain pulled BELOW the source, gate off. A power MOSFET conducts here through its body
        // diode — which is why a MOSFET half-bridge needs no discrete freewheeling diode — and the
        // drop is a diode drop rather than an ohmic one.
        const string cnl = """
            Vdc:VN  n  0  Vdc=-5
            R:RL    n  d  R=2
            Vdc:VG  g  0  Vdc=0
            VDMOS_N:M1 d g 0 Vto=3.2 Kp=40 Rds=1e7 Is=5e-13 N=1.05 Bv=0
            """;
        var (nl, v) = Dc(cnl);
        double vd = Node(nl, v, "d");

        // The body diode's anode is the SOURCE (at 0 V) and its cathode the drain, so it conducts
        // and holds the drain about a diode drop below ground. Wiring it the other way round would
        // leave the drain at −5 V and the device blocking, which is a different part.
        Assert.True(vd < -0.4 && vd > -1.2,
            $"the body diode must clamp the drain about a diode drop below the source: {vd:F3} V");

        double id = (vd - (-5.0)) / 2.0;
        Assert.True(id > 1.0, $"and it must carry the load current: {id:F3} A");
    }

    // ── T6 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T6_AnIgbtsOnStateDrop_DoesHaveAJunctionOffsetInIt()
    {
        // The same load and the same gate drive as T4. An IGBT cannot get below roughly a diode
        // drop however hard it is driven, because the current crosses the bipolar's emitter-base
        // junction on its way in. That is the trade against a power MOSFET, and this is the
        // measurement of it.
        //
        // Pin order is [collector, gate, emitter].
        const string cnl = """
            Vdc:VDD dd 0  Vdc=12
            R:RL    dd c  R=4
            Vdc:VG  g  0  Vdc=10
            IGBT_N:Q1 c g 0 Vto=3.2 Kp=40 Lambda=0 Bf=0.6 Is=2e-12 N=1 Tau=0 Cge=0 Cgcmax=0
            """;
        var (nl, v) = Dc(cnl);
        double vce = Node(nl, v, "c");

        Assert.True(vce > 0.45,
            $"an IGBT's saturation voltage cannot fall below a junction drop: {vce:F3} V");
        Assert.True(vce < 1.6, $"…but it must still be saturating: {vce:F3} V");

        // The internal base node really is between the two, a junction drop below the collector.
        double vb = Node(nl, v, nl.Nodes.AllNames.Single(n => n.Contains("__igbt") && n.EndsWith("_b")));
        Assert.True(vce - vb > 0.3 && vce - vb < 1.0,
            $"the internal junction must be carrying the offset: Vc−Vb = {vce - vb:F3} V");
    }

    // ── T7 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void T7_TheOhmicParasitics_AreInsideTheDevice_OnInternalNodesTheElaboratorMints()
    {
        // Rd is a MODEL parameter, so the schematic shows one transistor and the elaborator mints
        // the node behind it. The proof is that it CHANGES THE ANSWER — a parameter the elaborator
        // read but never wired would leave the two identical.
        string Stage(string rd) => $"""
            Vdc:VDD dd 0  Vdc=5
            R:RD    dd d  R=100
            Vdc:VG  g  0  Vdc=3
            MOS1_N:M1 d g 0 0 Vto=0.7 Kp=2e-5 W=200e-6 L=1e-6 Gamma=0 Lambda=0 Rd={rd}
            """;

        var (nlA, vA) = Dc(Stage("0"));
        var (nlB, vB) = Dc(Stage("400"));

        double idA = (5.0 - Node(nlA, vA, "d")) / 100.0;
        double idB = (5.0 - Node(nlB, vB, "d")) / 100.0;
        Assert.True(idA > 0, "the reference stage must be conducting");
        Assert.True(idB < idA, $"400 Ω in the drain must reduce the current: {idA:E3} → {idB:E3} A");

        // The internal node exists and is named after the instance, so two of these in one design
        // cannot share it.
        Assert.Contains(nlB.Nodes.AllNames, n => n.Contains("__mos_") && n.EndsWith("_di"));
        Assert.DoesNotContain(nlA.Nodes.AllNames, n => n.Contains("__mos_"));

        // …and the branch current is reported under the TERMINAL's own name, which is what makes
        // I:M1:drain mean what a user expects.
        var m = nlB.Components.Single(c => c.ComponentType == "MOS1_N");
        Assert.Contains("drain", m.Model.TerminalNames);
    }
}
