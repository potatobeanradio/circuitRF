using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for the SDD/ZPort schematic symbol 2N-pin layout:
/// Both SDD and ZPort expose 2N differential ± pairs.
/// Pin order is the NetExtractor contract: pin[2(p-1)] = "p+", pin[2(p-1)+1] = "p-".
/// EditableComponent.PortCount remains N (signal ports); pin count = 2N.
/// </summary>
public class SddSymbolTests
{
    // ── Test 1: SDD2 produces 4 pins with correct names and geometry ─────────

    [Fact]
    public void SddSymbol_2Port_Has4Pins()
    {
        var pins = SymbolPortDefs.For(SymbolKind.Sdd, 2);

        // Port 1 on left (x=-200), port 2 on right (x=+200); + above -, centered on y=0.
        Assert.Equal(4, pins.Length);
        Assert.Equal("1+", pins[0].Name); Assert.Equal(-200f, pins[0].LocalX); Assert.Equal(-100f, pins[0].LocalY);
        Assert.Equal("1-", pins[1].Name); Assert.Equal(-200f, pins[1].LocalX); Assert.Equal(+100f, pins[1].LocalY);
        Assert.Equal("2+", pins[2].Name); Assert.Equal(+200f, pins[2].LocalX); Assert.Equal(-100f, pins[2].LocalY);
        Assert.Equal("2-", pins[3].Name); Assert.Equal(+200f, pins[3].LocalX); Assert.Equal(+100f, pins[3].LocalY);
    }

    // ── Test 2: SDD3 produces 6 pins in contract order ───────────────────────

    [Fact]
    public void SddSymbol_3Port_Has6Pins()
    {
        var pins = SymbolPortDefs.For(SymbolKind.Sdd, 3);

        Assert.Equal(6, pins.Length);
        Assert.Equal(["1+", "1-", "2+", "2-", "3+", "3-"],
                     pins.Select(p => p.Name).ToArray());
        // Ports 1+2 on left (x=-200), port 3 on right (x=+200).
        Assert.All(pins.Take(4), p => Assert.Equal(-200f, p.LocalX));
        Assert.All(pins.Skip(4), p => Assert.Equal(+200f, p.LocalX));
    }

    // ── Test 3: ZPort with N=2 returns 4 pins — 2N ± pairs, same as SDD ────────

    [Fact]
    public void ZPortSymbol_2Port_Has4Pins()
    {
        var pins = SymbolPortDefs.For(SymbolKind.ZPort, 2);

        Assert.Equal(4, pins.Length);
        Assert.Equal("1+", pins[0].Name); Assert.Equal(-200f, pins[0].LocalX); Assert.Equal(-100f, pins[0].LocalY);
        Assert.Equal("1-", pins[1].Name); Assert.Equal(-200f, pins[1].LocalX); Assert.Equal(+100f, pins[1].LocalY);
        Assert.Equal("2+", pins[2].Name); Assert.Equal(+200f, pins[2].LocalX); Assert.Equal(-100f, pins[2].LocalY);
        Assert.Equal("2-", pins[3].Name); Assert.Equal(+200f, pins[3].LocalX); Assert.Equal(+100f, pins[3].LocalY);
    }

    // ── Test 4: SDD2 net extraction yields 4 nets in pin-index order ─────────

    [Fact]
    public void Sdd_NetExtraction_4Nets()
    {
        // SDD2 at (0,0) R0. Layout — port 1 on left (x=-200), port 2 on right (x=+200):
        //   "1+" (-200,-100), "1-" (-200,+100), "2+" (+200,-100), "2-" (+200,+100)
        // Pin symbol's connection point is at LocalX=+200 (so Pin at X,Y connects at world X+200, Y).
        // Ground's connection point is at LocalY=0 (connects at world X, Y).
        var model = new SchematicEditModel();

        var sdd = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Sdd, X = 0, Y = 0 };
        sdd.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "2" });
        model.Components.Add(sdd);

        // Pin "Vin" at (-400,-100): connection at (-400+200,-100)=(-200,-100) → SDD "1+" (pin[0]).
        var pinVin = new EditableComponent { InstanceName = "pin_vin", Symbol = SymbolKind.Pin, X = -400, Y = -100 };
        pinVin.Parameters.Add(new EditableParameter { Name = "Num",  Expression = "1" });
        pinVin.Parameters.Add(new EditableParameter { Name = "Name", Expression = "Vin" });
        model.Components.Add(pinVin);

        // Ground at (-200, 100): connection at (-200,+100) → SDD "1-" (pin[1]).
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = -200, Y = 100 });

        // Pin "Vout" at (0,-100): connection at (0+200,-100)=(+200,-100) → SDD "2+" (pin[2]).
        var pinVout = new EditableComponent { InstanceName = "pin_vout", Symbol = SymbolKind.Pin, X = 0, Y = -100 };
        pinVout.Parameters.Add(new EditableParameter { Name = "Num",  Expression = "2" });
        pinVout.Parameters.Add(new EditableParameter { Name = "Name", Expression = "Vout" });
        model.Components.Add(pinVout);

        // Ground at (+200, 100): connection at (+200,+100) → SDD "2-" (pin[3]).
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = 200, Y = 100 });


        var result = NetExtractor.Extract(model);

        var x1 = result.TestBench.Instances.First(i => i.InstanceName == "X1");
        Assert.Equal(4, x1.NetBindings.Count);
        Assert.Equal("Vin",  x1.NetBindings[0]);   // pin "1+" → Vin
        Assert.Equal("0",    x1.NetBindings[1]);   // pin "1-" → gnd
        Assert.Equal("Vout", x1.NetBindings[2]);   // pin "2+" → Vout
        Assert.Equal("0",    x1.NetBindings[3]);   // pin "2-" → gnd
    }

    // ── Test 5: 4-net SDD2 elaborates without arity error; SddPortCount == 2 ─

    [Fact]
    public void Sdd_Elaborates_NoArityError()
    {
        var model = new SchematicEditModel();

        var sdd = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Sdd, X = 0, Y = 0 };
        sdd.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "2" });
        model.Components.Add(sdd);

        // Pin at (-400,-100): connection (-400+200,-100)=(-200,-100) → "1+".
        // Ground at (-200,100): connection (-200,+100) → "1-".
        var pinVin = new EditableComponent { InstanceName = "Vin", Symbol = SymbolKind.Pin, X = -400, Y = -100 };
        pinVin.Parameters.Add(new EditableParameter { Name = "Num", Expression = "1" });
        model.Components.Add(pinVin);
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = -200, Y = 100 });

        // Pin at (0,-100): connection (0+200,-100)=(+200,-100) → "2+".
        // Ground at (+200,100): connection (+200,+100) → "2-".
        var pinVout = new EditableComponent { InstanceName = "Vout", Symbol = SymbolKind.Pin, X = 0, Y = -100 };
        pinVout.Parameters.Add(new EditableParameter { Name = "Num", Expression = "2" });
        model.Components.Add(pinVout);
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = 200, Y = 100 });

        var er = NetExtractor.Extract(model);

        // Must not throw the "expected even number of nets" error.
        var nl = new Elaborator(er.Library).Elaborate(er.TestBench);

        var ec = nl.Components.First(c => c.Model is SddModel);
        Assert.Equal(2.0, ec.Parameters["SddPortCount"].AsReal());
    }

    // ── Test 6: PortCount for SDD stays N; pin count is 2N ──────────────────

    [Fact]
    public void EditableComponent_Sdd_PortCount_IsN()
    {
        var sdd = new EditableComponent { Symbol = SymbolKind.Sdd };
        sdd.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "2" });

        int portCount = sdd.PortCount;
        int pinCount  = SymbolPortDefs.For(SymbolKind.Sdd, portCount).Length;

        Assert.Equal(2, portCount);
        Assert.Equal(4, pinCount);
    }

    // ── Test 9: ZPort 2-port net extraction yields 4 nets in ± pair order ────

    [Fact]
    public void ZPort_NetExtraction_4Nets()
    {
        // Z2P at (0,0) R0. 2N=4 pins — same geometry as SDD2:
        //   "1+" (-200,-100), "1-" (-200,+100), "2+" (+200,-100), "2-" (+200,+100)
        // Pin port is at local (200,0); Pin at (X,Y) connects at world (X+200,Y).
        // Ground's port is at its local (0,0).
        var model = new SchematicEditModel();

        var zp = new EditableComponent { InstanceName = "Z1", Symbol = SymbolKind.ZPort, X = 0, Y = 0 };
        zp.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "2" });
        zp.Parameters.Add(new EditableParameter { Name = "Z[1,1]", Expression = "50" });
        zp.Parameters.Add(new EditableParameter { Name = "Z[2,2]", Expression = "50" });
        model.Components.Add(zp);

        // Pin "a" at (-400,-100): port connects at (-400+200,-100)=(-200,-100) → "1+".
        var pinA = new EditableComponent { InstanceName = "pin_a", Symbol = SymbolKind.Pin, X = -400, Y = -100 };
        pinA.Parameters.Add(new EditableParameter { Name = "Num",  Expression = "1" });
        pinA.Parameters.Add(new EditableParameter { Name = "Name", Expression = "a" });
        model.Components.Add(pinA);

        // Ground at (-200, 100): port at (-200,+100) → "1-".
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = -200, Y = 100 });

        // Pin "b" at (0,-100): port connects at (0+200,-100)=(+200,-100) → "2+".
        var pinB = new EditableComponent { InstanceName = "pin_b", Symbol = SymbolKind.Pin, X = 0, Y = -100 };
        pinB.Parameters.Add(new EditableParameter { Name = "Num",  Expression = "2" });
        pinB.Parameters.Add(new EditableParameter { Name = "Name", Expression = "b" });
        model.Components.Add(pinB);

        // Ground at (+200, 100): port at (+200,+100) → "2-".
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = 200, Y = 100 });

        var result = NetExtractor.Extract(model);

        var z1 = result.TestBench.Instances.First(i => i.InstanceName == "Z1");
        Assert.Equal(4, z1.NetBindings.Count);
        Assert.Equal("a",  z1.NetBindings[0]);   // "1+" → a
        Assert.Equal("0",  z1.NetBindings[1]);   // "1-" → gnd
        Assert.Equal("b",  z1.NetBindings[2]);   // "2+" → b
        Assert.Equal("0",  z1.NetBindings[3]);   // "2-" → gnd
        Assert.Null(z1.RefNetBinding);
    }
}
