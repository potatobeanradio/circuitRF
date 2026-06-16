using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Gate tests for brief-symbol-library-overhaul.
/// Tests cover the critical pin-grid bug fix, N-aware SDD/ZPort body,
/// Pin horizontal reorientation, VAR label, and +/− polarity indicators.
/// </summary>
public class SymbolLibraryOverhaulTests
{
    // ── Test 1: #8 GATE — fresh place, every pin Unconnected ─────────────────
    // Root-cause fix: GenerateSddPorts now uses portSpacing=400 so all pin Y values
    // land on odd multiples of 100 — no P-cell collision via banker's rounding.

    [Fact]
    public void FreshPlace_AllPinsUnconnected()
    {
        var cases = new[] {
            (SymbolKind.Sdd,   2), (SymbolKind.Sdd,   3),
            (SymbolKind.Sdd,   4), (SymbolKind.Sdd,   5),
            (SymbolKind.ZPort, 2), (SymbolKind.ZPort, 3),
            (SymbolKind.ZPort, 4), (SymbolKind.ZPort, 5),
        };

        foreach (var (kind, n) in cases)
        {
            var model = new SchematicEditModel();
            var comp  = new EditableComponent { InstanceName = "X1", Symbol = kind };
            comp.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = n.ToString() });
            model.Components.Add(comp);

            var (render, _) = model.BuildRenderModel();
            var rc = render.Components.First();

            foreach (var port in rc.Ports)
                Assert.Equal(PortConnectionState.Unconnected, port.State);
        }
    }

    // ── Test 2: pin Y on-grid — no half-grid coordinates ─────────────────────

    [Fact]
    public void PinYOnGrid()
    {
        foreach (var kind in new[] { SymbolKind.Sdd, SymbolKind.ZPort })
        {
            for (int n = 1; n <= 6; n++)
            {
                var pins = SymbolPortDefs.For(kind, n);
                foreach (var (name, _, ly) in pins)
                    Assert.True(ly % 100f == 0f,
                        $"{kind} N={n} pin '{name}' LocalY={ly} is not a multiple of 100");
            }
        }
    }

    // ── Test 3: Z1P/SDD1 special case — + left, − right, centered ────────────

    [Fact]
    public void Z1P_SpecialCase()
    {
        var pins = SymbolPortDefs.For(SymbolKind.ZPort, 1);
        Assert.Equal(2, pins.Length);
        Assert.Equal("1+", pins[0].Name); Assert.Equal(-200f, pins[0].LocalX); Assert.Equal(0f, pins[0].LocalY);
        Assert.Equal("1-", pins[1].Name); Assert.Equal(+200f, pins[1].LocalX); Assert.Equal(0f, pins[1].LocalY);

        var sddPins = SymbolPortDefs.For(SymbolKind.Sdd, 1);
        Assert.Equal("1+", sddPins[0].Name); Assert.Equal(-200f, sddPins[0].LocalX); Assert.Equal(0f, sddPins[0].LocalY);
        Assert.Equal("1-", sddPins[1].Name); Assert.Equal(+200f, sddPins[1].LocalX); Assert.Equal(0f, sddPins[1].LocalY);
    }

    // ── Test 4: pin-order contract unchanged for N=2 ─────────────────────────

    [Fact]
    public void PinOrderContract_Unchanged()
    {
        var pins = SymbolPortDefs.For(SymbolKind.Sdd, 2);
        Assert.Equal(["1+", "1-", "2+", "2-"], pins.Select(p => p.Name).ToArray());
    }

    // ── Test 5: SDD body half-height grows with N ────────────────────────────

    [Fact]
    public void SddBodyGrowsWithN()
    {
        var sym2 = BuiltInSymbols.Primitives(SymbolKind.Sdd, 2);
        var sym4 = BuiltInSymbols.Primitives(SymbolKind.Sdd, 4);

        var rr2 = sym2.Primitives.OfType<RoundedRectPrimitive>().Single();
        var rr4 = sym4.Primitives.OfType<RoundedRectPrimitive>().Single();

        Assert.True(rr4.H > rr2.H,
            $"Body H for N=4 ({rr4.H}) must exceed N=2 ({rr2.H})");
    }

    // ── Test 6: ZPort has no diagonal Z-mark line ────────────────────────────

    [Fact]
    public void ZPort_NoZMark()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.ZPort, 2);

        // No diagonal line (the old "Z" mark had a line with both Δx≠0 and Δy≠0).
        var diagonals = sym.Primitives
            .OfType<LinePrimitive>()
            .Where(lp => lp.X1 != lp.X2 && lp.Y1 != lp.Y2)
            .ToList();
        Assert.Empty(diagonals);
    }

    // ── Test 7: Pin has horizontal tip at (200,0), hexagon body ──────────────

    [Fact]
    public void Pin_HorizontalRightTip()
    {
        var portDefs = SymbolPortDefs.For(SymbolKind.Pin);
        Assert.Single(portDefs);
        Assert.Equal(200f, portDefs[0].LocalX);
        Assert.Equal(0f,   portDefs[0].LocalY);

        var sym = BuiltInSymbols.Primitives(SymbolKind.Pin);
        var hexagon = sym.Primitives.OfType<PolygonPrimitive>().FirstOrDefault();
        Assert.NotNull(hexagon);
        Assert.Equal(6, hexagon.Points.Count);
    }

    // ── Test 8: Var has "VAR" TextPrimitive ──────────────────────────────────

    [Fact]
    public void Var_HasVarText()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.Var);
        var txt = sym.Primitives.OfType<TextPrimitive>().FirstOrDefault(t => t.Content == "VAR");
        Assert.NotNull(txt);
    }

    // ── Test 9: ToneSource / Vdc / P1Tone / Term each carry + and − markers ──

    [Fact]
    public void PlusMinusIndicators()
    {
        foreach (var kind in new[] { SymbolKind.ToneSource, SymbolKind.Vdc, SymbolKind.P1Tone, SymbolKind.Term })
        {
            var sym   = BuiltInSymbols.Primitives(kind);
            var texts = sym.Primitives.OfType<TextPrimitive>().Select(t => t.Content).ToList();
            Assert.Contains("+", texts);
            Assert.Contains("−", texts);
        }
    }

    // ── Test 10: SDD3 net extraction — 6 nets in ±-pair order ────────────────
    // SDD3 layout: nLeft=2 → ports 1,2 on left; nRight=1 → port 3 on right.
    // portSpacing=400: left centers at (-200,±200), right center at (200,0).
    // Pins: 1+@(-200,-300), 1-@(-200,-100), 2+@(-200,+100), 2-@(-200,+300),
    //        3+@(+200,-100), 3-@(+200,+100).

    [Fact]
    public void NetExtraction_Sdd3_4_Correct()
    {
        var model = new SchematicEditModel();
        var sdd   = new EditableComponent { InstanceName = "X1", Symbol = SymbolKind.Sdd };
        sdd.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "3" });
        model.Components.Add(sdd);

        // Wire each "+" pin to a distinct net via a label at the pin world coord.
        model.NetLabels.Add(new EditableNetLabel { Name = "n1", X = -200, Y = -300 }); // "1+"
        model.NetLabels.Add(new EditableNetLabel { Name = "n2", X = -200, Y = +100 }); // "2+"
        model.NetLabels.Add(new EditableNetLabel { Name = "n3", X = +200, Y = -100 }); // "3+"

        // Wire each "−" pin to ground at the pin world coord.
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = -200, Y = -100 }); // "1−"
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = -200, Y = +300 }); // "2−"
        model.Components.Add(new EditableComponent { Symbol = SymbolKind.Ground, X = +200, Y = +100 }); // "3−"

        var result = NetExtractor.Extract(model);

        var x1 = result.TestBench.Instances.First(i => i.InstanceName == "X1");
        Assert.Equal(6, x1.NetBindings.Count);
        Assert.Equal("n1", x1.NetBindings[0]); // "1+"
        Assert.Equal("0",  x1.NetBindings[1]); // "1-"
        Assert.Equal("n2", x1.NetBindings[2]); // "2+"
        Assert.Equal("0",  x1.NetBindings[3]); // "2-"
        Assert.Equal("n3", x1.NetBindings[4]); // "3+"
        Assert.Equal("0",  x1.NetBindings[5]); // "3-"
    }
}
