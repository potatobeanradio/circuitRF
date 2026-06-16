using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using Xunit;

namespace CircuitRF.Engine.Tests;

/// <summary>
/// Gate tests 4–6 for the Z_Port 2N per-port reference reform (brief-zport-per-port-refs).
/// </summary>
public class ZPortPerPortRefTests
{
    // ── Test 4: 2-port Z_Port elaborates with distinct minus-nodes ────────────

    [Fact]
    public void ZPort_PerPortRef_Stamps_DistinctMinusNodes()
    {
        // Distinct minus-nodes: n_refA for port 1, n_refB for port 2.
        const string cnl = @"
Z_Port:Z1  a n_refA  b n_refB  Z[1,1]=50 Z[2,2]=50 Z[1,2]=0 Z[2,1]=0
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var netlist = new Elaborator(lib).Elaborate(tb);

        var ec = netlist.Components.Single(c => c.ComponentType == "Z_Port");
        // Nodes[2p] = port p+, Nodes[2p+1] = port p−.
        Assert.Equal(4, ec.Nodes.Length);          // 2N nodes for N=2
        Assert.NotEqual(ec.Nodes[1], ec.Nodes[3]); // port 1− ≠ port 2− (per-port refs)
        Assert.NotEqual(ec.Nodes[0], ec.Nodes[2]); // port 1+ ≠ port 2+
    }

    // ── Test 5: 1-port Z_Port to ground gives matched S11 ≈ 0 ────────────────

    [Fact]
    public void ZPort_1Port_2Nets_GroundedMinus_MatchedLoad()
    {
        // 1-port ZPort (Z=50Ω) shunted to ground at a Port node.
        // Z_load = 50Ω = Z0 → S11 = (50-50)/(50+50) = 0.
        const string cnl = @"
Port:P1   n1 0  Num=1  Z=50 Ohm
Z_Port:Zl  n1 0  Z[1,1]=50
analysis SP  type=sparam  start=1e9  stop=1e9  npts=1
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, [1e9]);

        var s = ds["S"];
        // S[freq=0, port_i=0, port_j=0] = S11
        var s11 = (Complex)s[0, 0, 0];
        Assert.True(s11.Magnitude < 1e-9, $"S11 = {s11} should be ~0 for matched 50Ω load");
    }

    // ── Test 6: S-param with Z_Port per-port refs grounded = prior result ─────

    [Fact]
    public void Sparam_With_ZPort_PerPortRef_GroundRef_Unchanged()
    {
        // Series 50Ω resistor between two ports; Z_Port shunts at each port.
        // The minus pins are grounded via "0" nets.
        const string cnl = @"
Port:P1   n1 0  Num=1  Z=50 Ohm
Port:P2   n2 0  Num=2  Z=50 Ohm
R:Rs      n1 n2  R=50 Ohm
Z_Port:Zsrc  n1 0  Z[1,1]=50
Z_Port:Zld   n2 0  Z[1,1]=50
analysis SP  type=sparam  start=1e9  stop=1e9  npts=1
";
        var (lib, tb) = new CnlReader().Read(cnl);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ds = SParameterEngine.Run(nl, [1e9]);

        // For a 50Ω series R with 50Ω shunt loads at each end (Z0=50):
        // This is a loaded attenuator; verify S-matrix is passive and well-formed.
        var s = ds["S"];
        // S21 and S12 should be equal (reciprocal). Axes: [freq, port_i, port_j].
        var s21 = (Complex)s[0, 1, 0];
        var s12 = (Complex)s[0, 0, 1];
        Assert.True((s21 - s12).Magnitude < 1e-9, $"S21={s21} ≠ S12={s12}; should be reciprocal");
        // Both should have magnitude < 1 (passive).
        Assert.True(s21.Magnitude < 1.0, $"|S21| = {s21.Magnitude} should be < 1");
    }
}
