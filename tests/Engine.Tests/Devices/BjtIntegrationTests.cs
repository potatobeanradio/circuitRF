using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// The bipolar transistor through the WHOLE path — a <c>.cnl</c>, elaborated and solved by the real
/// engines — rather than against <c>BjtModel</c> in isolation, which <c>Core.Tests</c> already does.
///
/// <para><b>What only this level can catch.</b> The model exposes four intrinsic ports plus one per
/// non-zero parasitic resistance, and the elaborator has to mint an internal net for each and lay the
/// node pairs out in the order the model's port indices assume. Get that mapping wrong — swap two
/// nets, or emit the parasitic ports in the other order — and every unit test on the model still
/// passes, while the circuit solves to a different transistor. Nothing but a netlist-level solve
/// reads that mapping at all.</para>
///
///   B1 — a common-emitter stage biases up, and Kirchhoff holds at the device's own three terminals.
///   B2 — the internal nodes are real and carry the ohmic drops the parasitics imply.
///   B3 — a device with no parasitics is a three-net device, and the two agree in the limit.
///   B4 — the small-signal path works: an amplifier linearised at its bias has gain, and the same
///        stage with the transistor cut off does not.
///   B5 — the p-n-p is the mirror image of the n-p-n, through the engine and not just the model.
/// </summary>
public sealed class BjtIntegrationTests
{
    private static ElaboratedNetlist Elaborate(string cnlText)
    {
        var (lib, tb) = new CnlReader().Read(cnlText);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static double NodeV(ElaboratedNetlist n, NonlinearDcEngine.DcResult r, string net)
    {
        int idx = n.Nodes.IndexOf(net);
        return idx == 0 ? 0.0 : r.NodeVoltages[idx - 1];
    }

    /// <summary>The shipped defaults, minus the parasitics unless a caller wants them. Written out
    /// so a change to the palette defaults cannot silently move what these tests are measuring.</summary>
    private const string Card =
        "Is=9.57e-17 Bf=131.1 Nf=1 Vaf=71.02 Ikf=0.09745 Ise=1.618e-15 Ne=1.692 " +
        "Br=3.287 Nr=0.959 Var=4.081 Ikr=0.07617 Isc=5.969e-15 Nc=1.974 " +
        "Cje=8.287e-14 Vje=0.8281 Mje=0.7138 Cjc=8.781e-14 Vjc=0.7715 Mjc=0.7552 " +
        "Xcjc=0.6209 Fc=0.6275 Tf=1.72653e-11 Xtf=0.07 Vtf=0.00381019 Itf=0.027024 Tr=1.71536e-8";

    private const string Parasitics = "Rb=9.72444 Irb=3.017e-6 Rbm=6.94667 Re=0.7979 Rc=2.089";

    // ── B1 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A base-resistor-biased common-emitter stage. The oracle is Kirchhoff at the transistor's own
    /// three terminals, computed from the RESISTOR currents — quantities the engine solves for
    /// independently of the device — so a port mapping that put a current on the wrong net shows up
    /// as charge appearing from nowhere rather than as a slightly different answer.
    /// </summary>
    [Fact]
    public void B1_ACommonEmitterStageBiasesUp_AndKirchhoffHoldsAtTheTerminals()
    {
        string cnl = $"""
            Vdc:VCC  vcc 0  Vdc=3
            R:RB     vcc b   R=470e3
            R:RC     vcc c   R=1e3
            BJT_NPN:Q1  c b 0  {Card} {Parasitics}
            """;

        var n = Elaborate(cnl);
        var r = NonlinearDcEngine.Run(n);
        Assert.True(r.Converged, "the stage must find a DC operating point");

        double vb = NodeV(n, r, "b"), vc = NodeV(n, r, "c"), vcc = NodeV(n, r, "vcc");

        // A silicon junction, conducting: nothing else lands between 0.5 and 1.0 V.
        Assert.InRange(vb, 0.5, 1.0);
        // …and the collector is pulled down but not into the rail, i.e. genuinely in the active region.
        Assert.InRange(vc, 0.2, vcc - 0.1);

        double ib = (vcc - vb) / 470e3;      // into the base, through RB
        double ic = (vcc - vc) / 1e3;        // into the collector, through RC

        Assert.True(ib > 0 && ic > 0);
        // Current gain of the right ORDER — the exact value bends with Ikf, Ise and the Early
        // effect, so pinning it to Bf here would be pinning the bias point, not the device.
        Assert.InRange(ic / ib, 40.0, 200.0);

        // Kirchhoff at the transistor: everything in through the base and collector leaves through
        // the emitter, which is the grounded net. The emitter current is not measured by any
        // resistor here, so it is read off the device's own ports.
        double ie = CurrentIntoNet(n, r, "Q1", 0);
        Assert.Equal(ib + ic, -ie, 6);
    }

    /// <summary>
    /// Total current the device pushes into one NET, summed over every port that touches it. Read
    /// from the node list rather than from terminal names on purpose: the node list IS the mapping
    /// under test, so a port pair laid out in the wrong order shows up here as charge appearing
    /// from nowhere.
    /// </summary>
    private static double CurrentIntoNet(ElaboratedNetlist n, NonlinearDcEngine.DcResult r,
                                         string instance, int net)
    {
        var ec = n.Components.Single(c => c.InstancePath == instance);

        var v = new double[ec.Model!.PortCount];
        for (int p = 0; p < v.Length; p++)
            v[p] = NodeVoltage(r, ec.Nodes[2 * p]) - NodeVoltage(r, ec.Nodes[2 * p + 1]);

        var res = ec.Model.Evaluate(new PortVoltages(v));

        double total = 0.0;
        for (int p = 0; p < v.Length; p++)
        {
            if (ec.Nodes[2 * p]     == net) total += res.I[p];    // current enters at the + net
            if (ec.Nodes[2 * p + 1] == net) total -= res.I[p];    // and leaves at the −
        }
        return total;
    }

    private static double NodeVoltage(NonlinearDcEngine.DcResult r, int node)
        => node == 0 ? 0.0 : r.NodeVoltages[node - 1];

    // ── B2 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The internal nodes are genuine unknowns with ordinary matrix rows, and they carry the drops
    /// the parasitic resistances imply. Rc is exaggerated to 200 ohms so the drop is a volt rather
    /// than a millivolt — a real parasitic's drop is inside the solver tolerance, which would make
    /// this test pass whether the node existed or not.
    /// </summary>
    [Fact]
    public void B2_TheInternalNodesAreRealAndCarryTheOhmicDrops()
    {
        string cnl = $"""
            Vdc:VCC  vcc 0  Vdc=5
            R:RB     vcc b   R=470e3
            R:RC     vcc c   R=1e3
            BJT_NPN:Q1  c b 0  {Card} Rb=0 Re=0 Rc=200
            """;

        var n = Elaborate(cnl);
        var r = NonlinearDcEngine.Run(n);
        Assert.True(r.Converged);

        // The elaborator minted exactly one internal net, named after the instance so two devices
        // carrying the same card cannot collide.
        var minted = n.Nodes.AllNames.Where(name => name.StartsWith("__bjt_", StringComparison.Ordinal)).ToList();
        Assert.Equal(["__bjt_Q1_ci"], minted);

        double vc  = NodeV(n, r, "c");
        double vci = NodeV(n, r, "__bjt_Q1_ci");
        double ic  = (NodeV(n, r, "vcc") - vc) / 1e3;

        // V(c) − V(c') = Ic·Rc, to the solver's own tolerance. This is the assertion that fails if
        // the internal node were collapsed away or wired to the wrong port.
        Assert.Equal(ic * 200.0, vc - vci, 6);
        Assert.True(vc - vci > 1e-3, "the fixture must produce a drop worth measuring");
    }

    // ── B3 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// With no parasitics the device is a three-net device and mints nothing. Shrinking the
    /// resistances towards zero must walk the answer back to it — which is the statement that the
    /// internal nodes are the only thing the parasitics add.
    /// </summary>
    [Fact]
    public void B3_NoParasiticsMintsNothing_AndTheTwoAgreeInTheLimit()
    {
        string Stage(string extra) => $"""
            Vdc:VCC  vcc 0  Vdc=3
            R:RB     vcc b   R=470e3
            R:RC     vcc c   R=1e3
            BJT_NPN:Q1  c b 0  {Card} {extra}
            """;

        var bare = Elaborate(Stage("Rb=0 Re=0 Rc=0"));
        Assert.DoesNotContain(bare.Nodes.AllNames, x => x.StartsWith("__bjt_", StringComparison.Ordinal));
        var bareR = NonlinearDcEngine.Run(bare);
        Assert.True(bareR.Converged);
        double vcBare = NodeV(bare, bareR, "c");

        double previous = double.MaxValue;
        foreach (double scale in new[] { 1.0, 1e-2, 1e-4 })
        {
            var n = Elaborate(Stage($"Rb={9.72444 * scale} Rbm={6.94667 * scale} Irb=3.017e-6 " +
                                    $"Re={0.7979 * scale} Rc={2.089 * scale}"));
            Assert.Equal(3, n.Nodes.AllNames.Count(x => x.StartsWith("__bjt_", StringComparison.Ordinal)));

            var r = NonlinearDcEngine.Run(n);
            Assert.True(r.Converged);

            double diff = Math.Abs(NodeV(n, r, "c") - vcBare);
            Assert.True(diff < previous, $"shrinking the parasitics must converge on the bare device; {diff:E3}");
            previous = diff;
        }
        Assert.True(previous < 1e-3, $"the limit was not reached: {previous:E3}");
    }

    // ── B4 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The small-signal path. A common-emitter stage linearised at its own DC bias has gain at
    /// 100 MHz; the SAME stage with the base pulled to ground does not. The second half is what
    /// makes the first mean something — |S21| > 1 through a network with a capacitor in it is not
    /// by itself evidence that the transistor is doing anything.
    /// </summary>
    [Fact]
    public void B4_ALinearisedStageHasGain_AndACutOffOneDoesNot()
    {
        string Stage(double rb) => $"""
            Term:T1  in 0  Num=1 Z=50
            Term:T2  c  0  Num=2 Z=50
            Vdc:VCC  vcc 0  Vdc=3
            R:RBIAS  vcc b   R={rb}
            R:RC     vcc c   R=1e3
            C:CIN    in b    C=100e-12
            BJT_NPN:Q1  c b 0  {Card} {Parasitics}
            """;

        Complex S21(double rb)
        {
            var n  = Elaborate(Stage(rb));
            var ds = SParameterEngine.Run(n, [1e8]);
            return (Complex)ds["S"][0, 1, 0];
        }

        // Biased on: real voltage gain into 50 ohms.
        double on = S21(470e3).Magnitude;
        Assert.True(on > 1.0, $"the biased stage must have gain; |S21| was {on:F3}");

        // Bias resistor a thousand times larger — base current nowhere, transistor off. What is
        // left is a capacitor into a high impedance, and it cannot amplify.
        double off = S21(470e9).Magnitude;
        Assert.True(off < 0.5 * on, $"the cut-off stage must not amplify; |S21| was {off:F4} against {on:F3}");
    }

    // ── B5 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The p-n-p through the engine: the same stage on a negative rail gives the negated node
    /// voltages, to the solver's tolerance. This is the assertion that catches a polarity applied
    /// in the model but lost in the elaborator's node order — the n-p-n alone would never show it.
    /// </summary>
    [Fact]
    public void B5_ThePnpIsTheMirrorOfTheNpn_ThroughTheEngine()
    {
        string Stage(string type, double vcc) => $"""
            Vdc:VCC  vcc 0  Vdc={vcc.ToString(System.Globalization.CultureInfo.InvariantCulture)}
            R:RB     vcc b   R=470e3
            R:RC     vcc c   R=1e3
            {type}:Q1  c b 0  {Card} {Parasitics}
            """;

        var npn = Elaborate(Stage("BJT_NPN", 3.0));
        var pnp = Elaborate(Stage("BJT_PNP", -3.0));

        var rn = NonlinearDcEngine.Run(npn);
        var rp = NonlinearDcEngine.Run(pnp);
        Assert.True(rn.Converged && rp.Converged, "both polarities must find an operating point");

        foreach (var net in new[] { "b", "c" })
            Assert.Equal(-NodeV(npn, rn, net), NodeV(pnp, rp, net), 6);

        // Not vacuous: the stage is genuinely on, so the mirrored voltages are not both zero.
        Assert.True(NodeV(npn, rn, "b") > 0.5);
    }
}
