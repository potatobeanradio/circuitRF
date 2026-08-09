using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The owner-reported round on imported kit symbols: they came out too small, and their pins were
/// drawn away from the artwork — "rendered out in white space of the symbol, not touching any lines."
///
/// <para>Two independent causes. The size was a comparison of two different quantities; the detached
/// pins are what the connection-grid snap does to a drawing that is not on that grid.</para>
/// </summary>
public sealed class KitSymbolPinAttachmentTests
{
    private const double P = 100.0;   // the connection grid pins must land on

    /// <summary>
    /// A part in the shape a real drawing-backed kit uses: a terminal with a lead drawn to it, and
    /// artwork reaching well past the pins. The reach is what separates the two quantities the size
    /// bug conflated.
    /// </summary>
    private static (IReadOnlyList<KitSymbolPin> Pins, IReadOnlyList<KitSymbolShape> Body) Part(
        int pinX, int pinY, double bodyReach)
        =>
        (
            [new KitSymbolPin("A", -pinX, 0), new KitSymbolPin("B", pinX, pinY)],
            [
                new KitSymbolLine(-pinX, 0, -bodyReach, 0),      // lead to pin A
                new KitSymbolLine(pinX, pinY, bodyReach, pinY),  // lead to pin B
                new KitSymbolLine(-bodyReach, 0, bodyReach, pinY),
            ]
        );

    // ── The size ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheKitScale_LandsTheMedianPinSpanOnTheReference_NotTheDrawingExtent()
    {
        // ReferenceSymbolExtent IS a pin span — a built-in two-terminal part measures 400 pin to pin.
        // Normalising the DRAWING extent against it compares a span to something strictly larger,
        // making every kit part smaller than the built-in beside it by however far its artwork
        // reaches past its terminals. Here the artwork reaches twice as far, so the old rule produced
        // parts at half size — which is exactly what was reported.
        var parts = new[] { Part(pinX: 30, pinY: 0, bodyReach: 60) };   // pin span 60, draw extent 120

        double scale = KitTemplateSymbol.ChooseKitScale(
            parts.Select(p => ((IReadOnlyList<KitSymbolPin>?)p.Pins, (IReadOnlyList<KitSymbolShape>?)p.Body)));

        var sym = KitTemplateSymbol.BuildFromDrawing(parts[0].Pins, parts[0].Body, scale)!;
        double placedSpan = sym.Pins.Max(p => p.LocalX) - sym.Pins.Min(p => p.LocalX);

        Assert.Equal(KitTemplateSymbol.ReferenceSymbolExtent, placedSpan, 1.0);
    }

    [Fact]
    public void OneScalePerKit_SoTheKitsOwnRelativeSizesSurvive()
    {
        // Unchanged by the fix, and worth keeping pinned: a kit draws every symbol in one coordinate
        // system, so a part twice its neighbour's size must stay twice its size.
        var small = Part(pinX: 20, pinY: 0, bodyReach: 25);
        var large = Part(pinX: 40, pinY: 0, bodyReach: 50);

        double scale = KitTemplateSymbol.ChooseKitScale(
            new[] { small, large }.Select(p => ((IReadOnlyList<KitSymbolPin>?)p.Pins,
                                                (IReadOnlyList<KitSymbolShape>?)p.Body)));

        double sSpan = Span(KitTemplateSymbol.BuildFromDrawing(small.Pins, small.Body, scale)!);
        double lSpan = Span(KitTemplateSymbol.BuildFromDrawing(large.Pins, large.Body, scale)!);

        Assert.Equal(2.0, lSpan / sSpan, 0.05);
    }

    private static double Span(Symbol s) => s.Pins.Max(p => p.LocalX) - s.Pins.Min(p => p.LocalX);

    // ── The detached pins ───────────────────────────────────────────────────────────────────

    [Fact]
    public void APinTheSnapMoves_TakesItsOwnLeadLineWithIt()
    {
        // The reported one. Pins snap to the connection grid and artwork does not, so the snap pulls
        // a pin off the lead the kit drew to it — by up to half a grid step, which is most of the way
        // to the next pin on a compact symbol. The lead's own endpoint moves with it.
        var pins = new[] { new KitSymbolPin("A", -7, 0), new KitSymbolPin("B", 7, 0) };
        var body = new KitSymbolShape[]
        {
            new KitSymbolLine(-7, 0, -3, 0),   // lead ending exactly on pin A
            new KitSymbolLine(3, 0, 7, 0),     // lead ending exactly on pin B
            new KitSymbolLine(-3, 0, 3, 0),
        };

        // A scale that deliberately does NOT put the pins on the grid: 7 * 21.5 = 150.5, half a step out.
        var sym = KitTemplateSymbol.BuildFromDrawing(pins, body, scale: 21.5)!;

        foreach (var pin in sym.Pins)
        {
            Assert.Equal(0.0, Math.IEEERemainder(pin.LocalX, P), 1e-6);   // still on the grid

            bool touched = sym.Primitives.OfType<LinePrimitive>().Any(l =>
                (Math.Abs(l.X1 - pin.LocalX) < 1e-6 && Math.Abs(l.Y1 - pin.LocalY) < 1e-6) ||
                (Math.Abs(l.X2 - pin.LocalX) < 1e-6 && Math.Abs(l.Y2 - pin.LocalY) < 1e-6));
            Assert.True(touched, $"pin {pin.Name} at ({pin.LocalX},{pin.LocalY}) touches no drawn line");
        }
    }

    [Fact]
    public void APinWithNoLeadOfItsOwn_GetsAStubRatherThanBeingLeftAdrift()
    {
        // Measured on a real open kit, 26 of 374 pins sit on the INTERIOR of a shape — the base of a
        // filled arrow, a point inside a body box — so there is no vertex to carry along. Those get a
        // lead drawn from where the snap put them back to where the drawing has them.
        var pins = new[] { new KitSymbolPin("A", -7, 0), new KitSymbolPin("BULK", 7, 0) };
        var body = new KitSymbolShape[]
        {
            new KitSymbolLine(-7, 0, -3, 0),                 // a lead for A only
            new KitSymbolRectangle(-8, -6, 8, 6, false),     // BULK sits INSIDE this, on no vertex
        };

        var sym = KitTemplateSymbol.BuildFromDrawing(pins, body, scale: 21.5)!;
        var bulk = sym.Pins.Single(p => p.Name == "BULK");

        Assert.Equal(0.0, Math.IEEERemainder(bulk.LocalX, P), 1e-6);
        Assert.Contains(sym.Primitives.OfType<LinePrimitive>(), l =>
            (Math.Abs(l.X1 - bulk.LocalX) < 1e-6 && Math.Abs(l.Y1 - bulk.LocalY) < 1e-6) ||
            (Math.Abs(l.X2 - bulk.LocalX) < 1e-6 && Math.Abs(l.Y2 - bulk.LocalY) < 1e-6));
    }

    [Fact]
    public void APinTheSnapDoesNotMove_GetsNoStub()
    {
        // The guard against the fix over-firing: with the drawing already on the connection grid
        // there is nothing to reconnect, and a stub would be a mark the kit never drew.
        var pins = new[] { new KitSymbolPin("A", -2, 0), new KitSymbolPin("B", 2, 0) };
        var body = new KitSymbolShape[] { new KitSymbolLine(-2, 0, 2, 0) };

        var sym = KitTemplateSymbol.BuildFromDrawing(pins, body, scale: 50.0)!;   // 2*50 = 100, on grid

        Assert.Single(sym.Primitives);
        Assert.Equal(-100.0, sym.Pins.Min(p => p.LocalX), 1e-6);
        Assert.Equal(100.0, sym.Pins.Max(p => p.LocalX), 1e-6);
    }
}
