using System.Linq;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Core.Tests.Elaboration;

/// <summary>
/// Node expansion for the two built-in devices whose SCHEMATIC pin count differs from their MODEL
/// port count. Both are placed with the pins a user expects and are silently widened here.
///
/// <para>This is the seam the palette work rests on. A FET drawn with three pins reaches the engine
/// as a two-port over four node slots, and the diode's <c>Rs</c> is an internal node the user never
/// draws. If the expansion is wrong the device still elaborates and still solves — to the wrong
/// answer, at the wrong operating point, with no error anywhere.</para>
///
///   E1 — a 3-pin FET becomes [gate, source, drain, source]; the SHARED net is the source.
///   E2 — the source is an ordinary net, not ground: a FET with a lifted source keeps that net.
///   E3 — Rs = 0 diode stays two nets; Rs > 0 mints ONE internal node used by both ports.
///   E4 — two devices in one design get their own internal nodes, not a shared one.
/// </summary>
public class DeviceNodeExpansionTests
{
    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static ElaboratedComponent Only(ElaboratedNetlist n, string type)
        => n.Components.Single(c => c.ComponentType.Equals(type, System.StringComparison.OrdinalIgnoreCase));

    // ── E1 ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FET_Curtice")]
    [InlineData("FET_CurticeCubic")]
    [InlineData("FET_Statz")]
    [InlineData("FET_Materka")]
    [InlineData("FET_Angelov")]
    public void E1_ThreePinFetExpandsToGateSourceDrainSource(string reference)
    {
        // Nets in schematic pin order: gate, drain, source.
        var net = Elaborate($"{reference}:Q1  g d s\nR:R1 d 0 R=1000");
        var q   = Only(net, reference);

        int g = net.Nodes.GetOrAssign("g"), d = net.Nodes.GetOrAssign("d"), s = net.Nodes.GetOrAssign("s");

        // Port 0 is (gate, source); port 1 is (drain, source). The source appears TWICE — that is
        // the whole point of the expansion, and it is what puts Vgs and Vds in the model's hands.
        Assert.Equal([g, s, d, s], q.Nodes);
        Assert.Equal(2, q.Model.PortCount);
        Assert.True(q.IsNonlinear);
    }

    // ── E2 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void E2_SourceIsAnOrdinaryNet_NotGround()
    {
        // A source-degenerated stage: the source sits on its own net through a resistor, which is
        // the configuration a common-source-only device could not represent at all.
        var net = Elaborate("FET_Curtice:Q1  g d s\nR:Rs s 0 R=10\nR:Rd d 0 R=1000");
        var q   = Only(net, "FET_Curtice");

        int s = net.Nodes.GetOrAssign("s");
        Assert.NotEqual(0, s);                 // not collapsed onto ground
        Assert.Equal(s, q.Nodes[1]);
        Assert.Equal(s, q.Nodes[3]);

        // Grounding it is of course still allowed, and then — and only then — it IS node 0.
        var grounded = Elaborate("FET_Curtice:Q2  g d 0\nR:Rd d 0 R=1000");
        Assert.Equal(0, Only(grounded, "FET_Curtice").Nodes[1]);
    }

    // ── E3 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void E3_DiodeSeriesResistanceMintsExactlyOneInternalNode()
    {
        // Rs = 0: one port, two nets, no extra unknown. A diode without series resistance must not
        // cost a matrix row it does not need.
        var plain = Elaborate("Diode:D1 a 0 Is=1e-14");
        var dp    = Only(plain, "Diode");
        Assert.Equal(1, dp.Model.PortCount);
        Assert.Equal(2, dp.Nodes.Length);

        // Rs > 0: two ports over three distinct nets — anode, internal, cathode — with the internal
        // node SHARED between the resistor port and the junction port. Two different internal nodes
        // would leave the junction floating.
        var withRs = Elaborate("Diode:D1 a 0 Is=1e-14 Rs=12");
        var d      = Only(withRs, "Diode");
        Assert.Equal(2, d.Model.PortCount);
        Assert.Equal(4, d.Nodes.Length);

        int a = withRs.Nodes.GetOrAssign("a");
        Assert.Equal(a, d.Nodes[0]);
        Assert.Equal(d.Nodes[1], d.Nodes[2]);          // the shared internal node
        Assert.Equal(0, d.Nodes[3]);                   // cathode
        Assert.NotEqual(a, d.Nodes[1]);
        Assert.NotEqual(0, d.Nodes[1]);
    }

    // ── E4 ────────────────────────────────────────────────────────────────────

    [Fact]
    public void E4_TwoDiodesGetSeparateInternalNodes()
    {
        // The internal node is keyed by instance path. If it were not, two diodes in the same
        // design would short their junctions together through one shared node — a circuit that
        // solves cleanly and is not the one the user drew.
        var net = Elaborate("Diode:D1 a 0 Is=1e-14 Rs=12\nDiode:D2 b 0 Is=1e-14 Rs=12");
        var ds  = net.Components.Where(c => c.ComponentType == "Diode").ToList();

        Assert.Equal(2, ds.Count);
        Assert.NotEqual(ds[0].Nodes[1], ds[1].Nodes[1]);
    }
}
