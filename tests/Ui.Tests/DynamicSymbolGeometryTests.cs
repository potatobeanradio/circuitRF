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

    [Fact]
    public void SnpLabels_UseTheSharedSize_AndStayInsideTheBody()
    {
        var sym = BuiltInSymbols.PrimitivesForSnp(4, refNode: true, SnpPinConfig.Standard, SnpPitch.Tight);
        var (w, halfH) = SymbolPortDefs.SnpBodyRect(4, SnpPinConfig.Standard, SnpPitch.Tight);
        float cy = SymbolPortDefs.SnpBodyCenterYPublic(4, SnpPinConfig.Standard, SnpPitch.Tight);

        var texts = sym.Primitives.OfType<TextPrimitive>().ToList();
        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.Equal(BuiltInSymbols.SddPortLabelFontSize, t.FontSize, 3));

        foreach (var t in texts)
        {
            Assert.InRange(t.AnchorX, -w * 0.5, w * 0.5);
            Assert.InRange(t.AnchorY, cy - halfH, cy + halfH);
        }
    }

    private static bool Near(double a, double b) => System.Math.Abs(a - b) < 1e-6;
}
