using System.Linq;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Three owner reports about the dynamically-generated symbols (VerilogA, SnP, SDD/ZPort):
/// a VerilogA drew no leads to most of its pins and its box never grew past two terminals (with a
/// spurious extra lead at one terminal); an S4P at Tight pitch drew a box far taller than its own
/// pins; and every port label was too small to read.
/// </summary>
public sealed class DynamicSymbolGeometryTests
{
    // ── VerilogA: the renderer was halving the terminal count ─────────────────

    /// <summary>
    /// The defect itself. SDD/ZPort expose 2 pins per port, so the renderer derived the symbol's
    /// port count as <c>Ports.Count / 2</c> — for every kind. A VerilogA's terminals are 1:1, so a
    /// 4-terminal model was drawn with the 2-terminal symbol.
    /// </summary>
    [Theory]
    [InlineData(SymbolKind.VerilogA, 4, 4)]
    [InlineData(SymbolKind.VerilogA, 1, 1)]
    [InlineData(SymbolKind.VerilogA, 8, 8)]
    [InlineData(SymbolKind.Sdd,      4, 2)]   // 2 pins per port — the one kind that IS halved
    [InlineData(SymbolKind.ZPort,    6, 3)]
    [InlineData(SymbolKind.Combiner, 3, 2)]   // N paths plus the common port S
    [InlineData(SymbolKind.Resistor, 2, 2)]   // ignored by Primitives, but must not be halved either
    public void PortCountOf_MapsPinCountToPortCount_PerKind(SymbolKind kind, int pins, int expectedPorts)
        => Assert.Equal(expectedPorts, SchematicRenderer.PortCountOf(kind, pins));

    [Fact]
    public void EveryVerilogATerminal_GetsALead_AtItsOwnPinPosition()
    {
        for (int n = 1; n <= 8; n++)
        {
            var sym = BuiltInSymbols.Primitives(SymbolKind.VerilogA, n);
            Assert.Equal(n, sym.Pins.Count);

            var lines = sym.Primitives.OfType<LinePrimitive>().ToList();
            Assert.Equal(n, lines.Count);   // exactly one lead per pin — no more, no fewer

            foreach (var pin in sym.Pins)
                Assert.Contains(lines, l =>
                    (Near(l.X2, pin.LocalX) && Near(l.Y2, pin.LocalY)) ||
                    (Near(l.X1, pin.LocalX) && Near(l.Y1, pin.LocalY)));
        }
    }

    [Fact]
    public void AOneTerminalVerilogA_HasExactlyOneLead()
    {
        // The reported "extra line": Ports.Count / 2 was 0 for one pin, which fell through to the
        // two-port default and drew a lead to a pin that does not exist.
        var sym = BuiltInSymbols.Primitives(SymbolKind.VerilogA, 1);

        Assert.Single(sym.Pins);
        Assert.Single(sym.Primitives.OfType<LinePrimitive>());
    }

    [Fact]
    public void TheVerilogABody_GrowsWithTerminalCount()
    {
        double Height(int n)
        {
            var r = Assert.Single(BuiltInSymbols.Primitives(SymbolKind.VerilogA, n)
                .Primitives.OfType<RoundedRectPrimitive>());
            return r.H;
        }

        double h2 = Height(2), h4 = Height(4), h8 = Height(8);
        Assert.True(h4 > h2, $"4-terminal body ({h4}) must be taller than 2-terminal ({h2}).");
        Assert.True(h8 > h4, $"8-terminal body ({h8}) must be taller than 4-terminal ({h4}).");
    }

    [Fact]
    public void EveryVerilogATerminal_IsNumberedOnTheBody()
    {
        var sym = BuiltInSymbols.Primitives(SymbolKind.VerilogA, 5);
        var texts = sym.Primitives.OfType<TextPrimitive>().Select(t => t.Content).ToList();

        for (int i = 1; i <= 5; i++)
            Assert.Contains(i.ToString(), texts);
    }

    // ── S4P at Tight pitch: the box was sized from the ideal span, not the pins ──

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(10)]
    public void TheSnpBody_HugsItsOwnSidePins_AtBothPitches(int n)
    {
        foreach (var pitch in new[] { SnpPitch.Tight, SnpPitch.Loose })
        {
            var (_, halfH) = SymbolPortDefs.SnpBodyRect(n, SnpPinConfig.Standard, pitch);
            float cy = SymbolPortDefs.SnpBodyCenterYPublic(n, SnpPinConfig.Standard, pitch);

            var side = SymbolPortDefs.GenerateSnpPorts(n, refNode: false, SnpPinConfig.Standard, pitch)
                .Where(p => System.Math.Abs(p.LocalX) >= 199f)
                .ToList();

            float minY = side.Min(p => p.LocalY), maxY = side.Max(p => p.LocalY);

            // Every side pin inside the box, with the SAME 50-unit padding above and below —
            // which is what makes the box read as centred on its own pins.
            Assert.Equal(minY - 50f, cy - halfH, 3);
            Assert.Equal(maxY + 50f, cy + halfH, 3);
        }
    }

    [Fact]
    public void S4P_AtTightPitch_IsNoTallerThanItsPinSpanPlusPadding()
    {
        // The reported case, stated as the concrete number: two side pins one grid square apart,
        // so the box is 100 + 2×50 = 200 tall — not the 300 the ideal-span arithmetic produced.
        var (_, halfH) = SymbolPortDefs.SnpBodyRect(4, SnpPinConfig.Standard, SnpPitch.Tight);
        Assert.Equal(100f, halfH, 3);
    }

    [Fact]
    public void LoosePitch_IsUnchangedByTheFix()
    {
        // Loose is the default and was already correct; the fix must not move it.
        Assert.Equal(150f, SymbolPortDefs.SnpBodyRect(4, SnpPinConfig.Standard, SnpPitch.Loose).HalfH, 3);
        Assert.Equal(250f, SymbolPortDefs.SnpBodyRect(5, SnpPinConfig.Standard, SnpPitch.Loose).HalfH, 3);
        Assert.Equal(0f,   SymbolPortDefs.SnpBodyCenterYPublic(4, SnpPinConfig.Standard, SnpPitch.Loose), 3);
    }

    [Fact]
    public void TheRefPin_DoesNotMove()
    {
        // A body is drawn geometry; a pin is a connection point. Correcting the body must never
        // relocate the Ref pin, or every design that wired one silently loses that connection.
        foreach (var pitch in new[] { SnpPitch.Tight, SnpPitch.Loose })
            foreach (int n in new[] { 2, 3, 4, 5, 6, 8 })
            {
                var pins = SymbolPortDefs.GenerateSnpPorts(n, refNode: true, SnpPinConfig.Standard, pitch);
                var refPin = pins[n];
                Assert.Equal("Ref", refPin.Name);
                Assert.Equal(0f, refPin.LocalY % 100f, 3);   // still on the connection grid
            }
    }

    // ── Port labels are legible ───────────────────────────────────────────────

    [Fact]
    public void PortLabels_AreRoughlyTwiceTheirOldSize()
    {
        Assert.True(BuiltInSymbols.SddPortLabelFontSize >= 16.0,
            $"Port labels are still {BuiltInSymbols.SddPortLabelFontSize} — the owner asked for " +
            "roughly double the original 10.");
    }

    [Theory]
    [InlineData(SymbolKind.Sdd)]
    [InlineData(SymbolKind.ZPort)]
    public void SddAndZPortLabels_UseTheSharedSize(SymbolKind kind)
    {
        var texts = BuiltInSymbols.Primitives(kind, 3).Primitives.OfType<TextPrimitive>().ToList();
        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.Equal(BuiltInSymbols.SddPortLabelFontSize, t.FontSize, 3));
    }

    /// <summary>SnP's labels are a quarter larger than the shared size (read zoomed out), and still sit inside the body at
    /// Tight pitch, the tightest layout.</summary>
    [Fact]
    public void SnpLabels_UseTheSnpSize_AndStayInsideTheBody()
    {
        var sym = BuiltInSymbols.PrimitivesForSnp(4, refNode: true, SnpPinConfig.Standard, SnpPitch.Tight);
        var (w, halfH) = SymbolPortDefs.SnpBodyRect(4, SnpPinConfig.Standard, SnpPitch.Tight);
        float cy = SymbolPortDefs.SnpBodyCenterYPublic(4, SnpPinConfig.Standard, SnpPitch.Tight);

        var texts = sym.Primitives.OfType<TextPrimitive>().ToList();
        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.Equal(BuiltInSymbols.SnpPortLabelFontSize, t.FontSize, 3));
        Assert.Equal(1.25 * BuiltInSymbols.SddPortLabelFontSize, BuiltInSymbols.SnpPortLabelFontSize, 6);

        foreach (var t in texts)
        {
            Assert.InRange(t.AnchorX, -w * 0.5, w * 0.5);
            Assert.InRange(t.AnchorY, cy - halfH, cy + halfH);
        }
    }

    private static bool Near(double a, double b) => System.Math.Abs(a - b) < 1e-6;
}

/// <summary>An SnP whose file names its ports draws the names beside the leads, short, while every pin keeps its number
/// (what a net binds to) and its position.</summary>
public sealed class SnpPortLabelSymbolTests
{
    [Fact]
    public void NamedPorts_DrawShortNames_AndThePinsAreTheNumberedSymbols()
    {
        var numbered = CircuitRF.Design.Schematic.BuiltInSymbols.PrimitivesForSnp(
            3, false, CircuitRF.Design.Symbol.SnpPinConfig.Standard, CircuitRF.Design.Symbol.SnpPitch.Loose);
        var named = CircuitRF.Design.Schematic.BuiltInSymbols.PrimitivesForSnp(
            3, false, CircuitRF.Design.Symbol.SnpPinConfig.Standard, CircuitRF.Design.Symbol.SnpPitch.Loose,
            ["U1.IN", null, "a-very-long-port-name"]);

        Assert.Equal(numbered.Pins.Select(p => (p.LocalX, p.LocalY, p.Name)), named.Pins.Select(p => (p.LocalX, p.LocalY, p.Name)));
        var text = named.Primitives.OfType<CircuitRF.Design.Symbol.TextPrimitive>().Select(t => t.Content).ToList();
        Assert.Contains("U1.IN", text);
        Assert.Contains("2", text);                                        // no name: its number
        Assert.Contains("a-very-~", text);                                 // cut to 8, the cut marked
        Assert.DoesNotContain(text, t => t.Length > 8);
    }

    // ── MWPD: the Wilkinson divider's pins follow its section count ──────────

    /// <summary>
    /// One section is the five pins it always had; N sections draw 3 + 2·N, the glyph's own pins the same list, each
    /// resistor pair 400 apart so a resistor dropped on it lands on both. N written as an expression is the one-section
    /// divider, as the plan cannot be asked for more pins than the symbol draws.
    /// </summary>
    [Fact]
    public void AWilkinsonDividersPins_FollowItsSections()
    {
        var one = CircuitRF.Design.Schematic.SymbolPortDefs.For(SymbolKind.Mwpd);
        Assert.Equal([("1", -400f, 0f), ("2", 400f, -400f), ("3", 400f, 400f), ("R2", 400f, -200f), ("R3", 400f, 200f)], one);

        for (int n = 1; n <= 3; n++)
        {
            var component = new CircuitRF.Design.Schematic.EditableComponent { Symbol = SymbolKind.Mwpd };
            component.Parameters.Add(new CircuitRF.Design.Schematic.EditableParameter { Name = "N", Expression = $"{n}" });
            Assert.Equal(3 + 2 * n, component.PortCount);

            var pins = CircuitRF.Design.Schematic.SymbolPortDefs.For(SymbolKind.Mwpd, component.PortCount);
            Assert.Equal(CircuitRF.Core.Devices.Microstrip.WilkinsonPlan.PinsFor(n), pins.Select(p => p.Name));
            var glyph = BuiltInSymbols.Primitives(SymbolKind.Mwpd, component.PortCount);
            Assert.Equal(pins.Select(p => ((string?)p.Name, p.LocalX, p.LocalY)), glyph.Pins.Select(p => (p.Name, (float)p.LocalX, (float)p.LocalY)));
            for (int k = 2; k <= n; k++)
            {
                var top = pins.Single(p => p.Name == $"R2_{k}");
                var bottom = pins.Single(p => p.Name == $"R3_{k}");
                Assert.Equal(top.LocalX, bottom.LocalX);
                Assert.Equal(400f, bottom.LocalY - top.LocalY);
            }
        }
        Assert.Same(BuiltInSymbols.Primitives(SymbolKind.Mwpd), BuiltInSymbols.Primitives(SymbolKind.Mwpd, 5));

        var swept = new CircuitRF.Design.Schematic.EditableComponent { Symbol = SymbolKind.Mwpd };
        swept.Parameters.Add(new CircuitRF.Design.Schematic.EditableParameter { Name = "N", Expression = "nsec" });
        Assert.Equal(5, swept.PortCount);
    }

    /// <summary>
    /// A ladder (MLAD) draws a via pin per shorted stub arm in its list, G1… along the bottom, 200 apart and centred; the
    /// glyph's pins are those. A list with none, the placed default or one that does not parse, is the two-pin ladder.
    /// </summary>
    [Theory]
    [InlineData("line(W=1mm, L=5mm)", 0)]
    [InlineData("line(W=1mm, L=5mm); stub(W=1mm, L=8mm, end=short)", 1)]
    [InlineData("stub(W=1mm, L=8mm, end=short, side=both); stub(W=1mm, L=8mm); stub(W=1mm, L=8mm, end=short, side=down)", 3)]
    [InlineData("not a list", 0)]
    public void ALaddersPins_FollowItsShortedStubs(string sections, int grounds)
    {
        var component = new CircuitRF.Design.Schematic.EditableComponent { Symbol = SymbolKind.Mlad };
        component.Parameters.Add(new CircuitRF.Design.Schematic.EditableParameter { Name = "Sections", Expression = sections });
        Assert.Equal(2 + grounds, component.PortCount);

        var pins = CircuitRF.Design.Schematic.SymbolPortDefs.For(SymbolKind.Mlad, component.PortCount);
        Assert.Equal(CircuitRF.Core.Devices.Microstrip.LadderPlan.PinsFor(grounds), pins.Select(p => p.Name));
        var glyph = BuiltInSymbols.Primitives(SymbolKind.Mlad, component.PortCount);
        Assert.Equal(pins.Select(p => ((string?)p.Name, p.LocalX, p.LocalY)), glyph.Pins.Select(p => (p.Name, (float)p.LocalX, (float)p.LocalY)));
        var g = pins.Skip(2).ToArray();
        Assert.All(g, p => Assert.Equal(300f, p.LocalY));
        Assert.Equal(0f, g.Sum(p => p.LocalX));
        for (int k = 1; k < g.Length; k++) Assert.Equal(200f, g[k].LocalX - g[k - 1].LocalX);
    }
}
