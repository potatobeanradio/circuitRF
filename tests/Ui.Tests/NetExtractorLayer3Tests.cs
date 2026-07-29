using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer 3 gate: terminal-order emission into TestBench.
/// Verifies Reference, InstanceName, NetBindings order, params, ZPort RefNetBinding,
/// Open/Short disable, and Ground exclusion.
/// </summary>
public class NetExtractorLayer3Tests
{
    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static EditableComponent Resistor(string name, double x, double y)
        => new() { InstanceName = name, Symbol = SymbolKind.Resistor, X = x, Y = y };

    private static System.Collections.Generic.List<CircuitRF.Core.Design.Instance>
        Instances(NetExtractor.ExtractionResult r) => [.. r.TestBench.Instances];

    private static CircuitRF.Core.Design.Instance Inst(
        NetExtractor.ExtractionResult r, string name)
        => r.TestBench.Instances.First(i => i.InstanceName == name);

    // ── Test 1: small circuit — Reference, InstanceName, NetBindings ─────────

    [Fact]
    public void SmallCircuit_TermRGround_CorrectInstancesAndBindings()
    {
        var model = new SchematicEditModel();

        // Term P1 at (0,200) R0: "+" at local(0,-200)→world(0,0), "−" at local(0,+200)→world(0,400).
        model.Components.Add(new EditableComponent
            { InstanceName = "P1", Symbol = SymbolKind.Term, X = 0, Y = 200 });

        // R1 at (0,1000): port0=(0,800), port1=(0,1200).  (Placed lower to avoid "−" conflict.)
        model.Components.Add(Resistor("R1", 0, 1000));

        // GND_ref at (0,400): grounds P1."−" at (0,400).
        model.Components.Add(new EditableComponent
            { InstanceName = "GND_ref", Symbol = SymbolKind.Ground, X = 0, Y = 400 });

        // GND1 at (0,1200): grounds R1.port1 at (0,1200).
        model.Components.Add(new EditableComponent
            { InstanceName = "GND1", Symbol = SymbolKind.Ground, X = 0, Y = 1200 });

        // Wire (0,0)→(0,800) connects P1."+" to R1.port0.
        model.Wires.Add(Wire((0, 0), (0, 800)));

        var result = NetExtractor.Extract(model);

        // Ground components are NOT emitted as instances.
        Assert.DoesNotContain(result.TestBench.Instances, i => i.InstanceName == "GND_ref");
        Assert.DoesNotContain(result.TestBench.Instances, i => i.InstanceName == "GND1");

        // Term emitted with Reference="Port" (engine Reference is unchanged).
        var p1 = Inst(result, "P1");
        Assert.Equal("Port", p1.Reference);
        // Term has 2 NetBindings: ["+" net, "−" net].
        Assert.Equal(2, p1.NetBindings.Count);
        // "−" pin is grounded → NetBindings[1] == "0".
        Assert.Equal("0", p1.NetBindings[1]);

        // Resistor emitted with Reference="R".
        var r1 = Inst(result, "R1");
        Assert.Equal("R", r1.Reference);
        Assert.Equal(2, r1.NetBindings.Count);

        // P1."+" and R1.port0 are on the same net (wired together).
        Assert.Equal(p1.NetBindings[0], r1.NetBindings[0]);

        // R1 port1 is ground.
        Assert.Equal("0", r1.NetBindings[1]);
    }

    // ── Test 2: generic Sdd (3-port) terminal order is [1+,1-,2+,2-,3+,3-] ────
    // The library FET (FetSdd, gate/drain/source at fixed positions) was hard-removed —
    // brief-housekeeping-tearoff-palette-repo.md §7A: "anything relying on it can be replaced by
    // an equivalent SDD." This is that equivalent: the SAME terminal-order contract, now proven
    // against the generic N-port Sdd device every removed FetSdd instance becomes.

    [Fact]
    public void Sdd_3Port_TerminalOrder_MatchesPinIndexContract()
    {
        var model = new SchematicEditModel();

        // Sdd N=3 at (0,0) R0: pin1+ (-200,-300), pin1- (-200,-100),
        // pin2+ (-200,100), pin2- (-200,300), pin3+ (200,-100), pin3- (200,100).
        var sdd = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Sdd, X = 0, Y = 0 };
        sdd.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "3" });
        model.Components.Add(sdd);

        // Resistor's own port0 = component center + local(0,-200) (default 2-terminal port defs).
        model.Components.Add(Resistor("R_1p", -200, -100)); // port0 at (-200,-300) = pin1+
        model.Components.Add(Resistor("R_3p", 200, 100));   // port0 at (200,-100)  = pin3+

        var result = NetExtractor.Extract(model);

        var x1 = Inst(result, "X1");
        Assert.Equal("SDD", x1.Reference);
        Assert.Equal(6, x1.NetBindings.Count);

        // Terminal 0 = pin1+ = same net as R_1p.port0.
        Assert.Equal(Inst(result, "R_1p").NetBindings[0], x1.NetBindings[0]);
        // Terminal 4 = pin3+ = same net as R_3p.port0.
        Assert.Equal(Inst(result, "R_3p").NetBindings[0], x1.NetBindings[4]);
        // Distinct terminals never share a net just from index proximity.
        Assert.NotEqual(x1.NetBindings[0], x1.NetBindings[4]);
    }

    // ── Test 3: ZPort "1-" grounded → NetBindings[1]="0", RefNetBinding null ───

    [Fact]
    public void ZPort_GroundedMinus_2NetBindings()
    {
        var model = new SchematicEditModel();

        // Z1P at (0,0) N=1 special case: "1+" at world (-200,0), "1-" at world (+200,0).
        var z1 = new EditableComponent
            { InstanceName = "Z1", Symbol = SymbolKind.ZPort, X = 0, Y = 0 };
        z1.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "1" });
        model.Components.Add(z1);

        // Ground at (+200,0) — coincides with "1-" pin.
        model.Components.Add(new EditableComponent
            { InstanceName = "GND1", Symbol = SymbolKind.Ground, X = 200, Y = 0 });

        // Wire at "1+" pin so it has a net.
        model.Wires.Add(Wire((-200, 0), (-200, -200)));

        var result = NetExtractor.Extract(model);

        var zInst = Inst(result, "Z1");
        Assert.Equal("Z_Port", zInst.Reference);
        Assert.Equal(2, zInst.NetBindings.Count);    // 2N=2 nets for 1-port
        Assert.Equal("0", zInst.NetBindings[1]);      // "1-" is grounded
        Assert.Null(zInst.RefNetBinding);             // RefNetBinding always null
    }

    // ── Test 4: ZPort "1-" non-ground → NetBindings[1] is non-ground net ─────

    [Fact]
    public void ZPort_NonGroundMinus_NetBinding1IsNonGround()
    {
        var model = new SchematicEditModel();

        // Z1P at (0,0) N=1 special case: "1+" at (-200,0), "1-" at (+200,0).
        var z1 = new EditableComponent
            { InstanceName = "Z1", Symbol = SymbolKind.ZPort, X = 0, Y = 0 };
        z1.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "1" });
        model.Components.Add(z1);

        // Wire at "1+" pin.
        model.Wires.Add(Wire((-200, 0), (-200, -200)));
        // Wire at "1-" pin — connects to a non-ground net.
        model.Wires.Add(Wire((200, 0), (200, 200)));

        // Resistor with port0 coincident with "1-" net to stabilise auto-name comparison.
        // R_ref at (200,200) → port0 at (200,0) = "1-" world position.
        model.Components.Add(Resistor("R_ref", 200, 200));

        var result = NetExtractor.Extract(model);

        var zInst = Inst(result, "Z1");
        Assert.Equal(2, zInst.NetBindings.Count);
        Assert.Null(zInst.RefNetBinding);
        Assert.NotEqual("0", zInst.NetBindings[1]);
        // NetBindings[1] matches R_ref's port0 net (same node as "1-" pin).
        Assert.Equal(Inst(result, "R_ref").NetBindings[0], zInst.NetBindings[1]);
    }

    // ── Test 5: parameters carried as authored ────────────────────────────────

    [Fact]
    public void Params_CarriedAsAuthored()
    {
        var model = new SchematicEditModel();
        var r1 = Resistor("R1", 0, 200);
        r1.Parameters.Add(new EditableParameter { Name = "R", Expression = "47", Unit = "Ω" });
        model.Components.Add(r1);

        var result = NetExtractor.Extract(model);

        var inst = Inst(result, "R1");
        Assert.Single(inst.Overrides);
        var ov = inst.Overrides[0];
        Assert.Equal("R",  ov.Name);
        Assert.Equal("47", ov.Expression);
        Assert.Equal("Ohm", ov.Unit); // Ω glyph normalized to ASCII at extraction boundary
    }

    // ── Test 6: Open-disabled component not emitted ───────────────────────────

    [Fact]
    public void OpenDisabled_NotEmitted()
    {
        var model = new SchematicEditModel();
        var r1 = Resistor("R1", 0, 200);
        r1.Disable = DisableState.Open;
        model.Components.Add(r1);
        model.Components.Add(Resistor("R2", 400, 200));

        var result = NetExtractor.Extract(model);

        Assert.DoesNotContain(result.TestBench.Instances, i => i.InstanceName == "R1");
        Assert.Contains(result.TestBench.Instances,       i => i.InstanceName == "R2");
    }

    // ── Test 7: Short-disabled not emitted but terminals unioned ─────────────

    [Fact]
    public void ShortDisabled_NotEmitted_ButTerminalsUnioned()
    {
        var model = new SchematicEditModel();

        // R1 at (0,200): port0=(0,0), port1=(0,400).
        model.Components.Add(Resistor("R1", 0, 200));

        // Rsht (short) at (0,600): port0=(0,400), port1=(0,800).
        // Rsht.port0=(0,400) coincides with R1.port1 — already same P-cell.
        var rsht = Resistor("Rsht", 0, 600);
        rsht.Disable = DisableState.Short;
        model.Components.Add(rsht);

        // R2 at (0,1000): port0=(0,800). Rsht shorts (0,400)↔(0,800), so R2.port0 is on same net as R1.port1.
        model.Components.Add(Resistor("R2", 0, 1000));

        var result = NetExtractor.Extract(model);

        // Rsht not emitted.
        Assert.DoesNotContain(result.TestBench.Instances, i => i.InstanceName == "Rsht");

        // R1.port1 and R2.port0 are on the same net because Rsht shorted them.
        Assert.Equal(Inst(result, "R1").NetBindings[1], Inst(result, "R2").NetBindings[0]);
    }
}
