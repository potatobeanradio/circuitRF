using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A kit that states its symbols one-per-file gives the terminals AND the drawing, so both are the
/// kit's own. Before this, only the terminals were read and every part of such a kit arrived as the
/// same plain box — the palette full of components that all looked alike.
///
/// <para><b>Every fixture is synthetic</b>: the repository commits no third-party kit data, so
/// nothing here names a supplier, a kit, a part or a model family.</para>
/// </summary>
public sealed class KitDrawnSymbolTests
{
    private static IReadOnlyList<KitSymbolPin> Pins(params (string Name, int X, int Y)[] pins) =>
        [.. pins.Select(p => new KitSymbolPin(p.Name, p.X, p.Y))];

    // ── the axis, which is the one that silently mirrors everything ───────────

    /// <summary>
    /// The drawing format is y-down, the same sense circuitRF's own symbol coordinates use — so a
    /// terminal the file puts BELOW the origin must draw below it.
    ///
    /// <para>This is the failure the whole split between the two builders exists for. Running a
    /// drawing through the symbol library's y-up flip mirrors every part vertically: it still places,
    /// still connects, still looks like a symbol, and is upside down everywhere it is drawn. It was
    /// invisible for as long as the body was a symmetric box.</para>
    /// </summary>
    [Fact]
    public void D1_ADrawingIsNotFlipped_TheWayASymbolLibraryIs()
    {
        var pins = Pins(("top", 0, -30), ("bottom", 0, 30));
        var body = new KitSymbolShape[] { new KitSymbolLine(0, -30, 0, 30) };

        var drawn = KitTemplateSymbol.BuildFromDrawing(pins, body, scale: 10)!;
        Assert.True(drawn.Pins[0].LocalY < 0, "the terminal the file puts above the origin must draw above it");
        Assert.True(drawn.Pins[1].LocalY > 0, "the terminal the file puts below the origin must draw below it");

        // The library path keeps its own convention, unchanged — the two formats genuinely differ.
        var fromLibrary = KitTemplateSymbol.Build(pins)!;
        Assert.Equal(-drawn.Pins[0].LocalY, fromLibrary.Pins[0].LocalY);
    }

    /// <summary>The pins and the artwork must land in the same frame, or the leads miss the body.</summary>
    [Fact]
    public void D2_ThePinsAndTheDrawingShareOneTransform()
    {
        var s = KitTemplateSymbol.BuildFromDrawing(
            Pins(("a", 0, -30)),
            [new KitSymbolLine(0, -30, 0, 0)],
            scale: 10)!;

        var line = s.Primitives.OfType<LinePrimitive>().Single();
        Assert.Equal(s.Pins[0].LocalX, line.X1);
        Assert.Equal(s.Pins[0].LocalY, line.Y1);
    }

    [Fact]
    public void D3_EveryPinStillLandsExactlyOnTheConnectionGrid()
    {
        var s = KitTemplateSymbol.BuildFromDrawing(
            Pins(("a", 7, -13), ("b", 21, 44)),
            [new KitSymbolLine(0, 0, 10, 10)],
            scale: 10)!;

        Assert.All(s.Pins, p =>
        {
            Assert.Equal(0, p.LocalX % 100);
            Assert.Equal(0, p.LocalY % 100);
        });
    }

    // ── shape conversion ──────────────────────────────────────────────────────

    [Fact]
    public void D4_EachDrawnShapeBecomesItsOwnPrimitive()
    {
        var s = KitTemplateSymbol.BuildFromDrawing(
            Pins(("a", 0, 0)),
            [
                new KitSymbolLine(0, 0, 10, 0),
                new KitSymbolRectangle(-10, -10, 10, 10, Filled: false),
                new KitSymbolPath([0, 0, 10, 0, 5, 10], Closed: true,  Filled: true),
                new KitSymbolPath([0, 0, 10, 0, 10, 10], Closed: false, Filled: false),
                new KitSymbolArc(0, 0, 5, 90, 180),
            ],
            scale: 10)!;

        Assert.Single(s.Primitives.OfType<LinePrimitive>());
        Assert.Single(s.Primitives.OfType<RectPrimitive>());
        Assert.Single(s.Primitives.OfType<PolygonPrimitive>());
        Assert.Single(s.Primitives.OfType<PolylinePrimitive>());
        Assert.Single(s.Primitives.OfType<ArcPrimitive>());

        Assert.True(s.Primitives.OfType<PolygonPrimitive>().Single().Filled);
    }

    /// <summary>
    /// The file measures its angles counter-clockwise on screen; circuitRF measures clockwise. That
    /// is a sign flip on BOTH — flipping only the sweep draws the correct span from the wrong end,
    /// a mirrored arc that still looks like an arc.
    /// </summary>
    [Fact]
    public void D5_ArcAnglesAreConvertedToTheRenderersOwnSense()
    {
        var s = KitTemplateSymbol.BuildFromDrawing(
            Pins(("a", 0, 0)),
            [new KitSymbolArc(0, 15, 7.5, 90, 180)],
            scale: 10)!;

        var arc = s.Primitives.OfType<ArcPrimitive>().Single();
        Assert.Equal(-90,  arc.StartDeg);
        Assert.Equal(-180, arc.SweepDeg);

        // A half-circle centred above the origin, at the shared scale.
        Assert.Equal((0.0, 150.0, 75.0), (arc.Cx, arc.Cy, arc.R));
    }

    /// <summary>
    /// A symbol that declares terminals and draws nothing still places — it falls back to the box
    /// body, which is exactly what a part backed by a symbol library gets.
    /// </summary>
    [Fact]
    public void D6_ASymbolThatDrawsNothingStillGetsABody()
    {
        var s = KitTemplateSymbol.BuildFromDrawing(Pins(("a", 0, -30), ("b", 0, 30)), [], scale: 10)!;

        Assert.NotEmpty(s.Primitives);
        Assert.Equal(2, s.Pins.Count);
    }

    // ── one scale per kit ─────────────────────────────────────────────────────

    /// <summary>
    /// A kit draws every symbol in one coordinate system, so their relative sizes are a choice its
    /// author made. Scaling each part into the same band independently throws that away and lands a
    /// tiny marker on the schematic bigger than the device beside it.
    /// </summary>
    [Fact]
    public void D7_OneScaleForTheWholeKit_KeepsThePartsInProportion()
    {
        var big   = (Pins: Pins(("a", 0, -30), ("b", 0, 30)),
                     Body: (IReadOnlyList<KitSymbolShape>)[new KitSymbolLine(-30, -30, 30, 30)]);
        var small = (Pins: Pins(("a", 0, 0)),
                     Body: (IReadOnlyList<KitSymbolShape>)[new KitSymbolLine(0, 0, 0, 3)]);

        double kitScale = KitTemplateSymbol.ChooseKitScale([big, small]);

        var bigSym   = KitTemplateSymbol.BuildFromDrawing(big.Pins,   big.Body,   kitScale)!;
        var smallSym = KitTemplateSymbol.BuildFromDrawing(small.Pins, small.Body, kitScale)!;

        double BodyHeight(Symbol s)
        {
            var ys = s.Primitives.OfType<LinePrimitive>().SelectMany(l => new[] { l.Y1, l.Y2 }).ToList();
            return ys.Max() - ys.Min();
        }

        // Drawn 20x apart, so they must still be drawn 20x apart.
        Assert.Equal(20.0, BodyHeight(bigSym) / BodyHeight(smallSym), precision: 6);
    }

    /// <summary>A kit that states no drawing to measure yields no kit scale, and callers fall back.</summary>
    [Fact]
    public void D8_AKitWithNothingToMeasureYieldsNoScale()
    {
        Assert.Equal(0, KitTemplateSymbol.ChooseKitScale([]));
        Assert.Equal(0, KitTemplateSymbol.ChooseKitScale([(null, null)]));
    }

    /// <summary>
    /// The scale is measured over the artwork too. A symbol whose drawing reaches well past its
    /// terminals — a ground marker hanging below a single pin — would otherwise be scaled by the
    /// wrong decade, or by nothing at all when it has only one pin to measure.
    /// </summary>
    [Fact]
    public void D9_TheScaleIsMeasuredOverTheArtworkAsWellAsThePins()
    {
        var pins = Pins(("a", 0, 0));                       // one pin: no span of its own at all
        var body = new KitSymbolShape[] { new KitSymbolLine(0, 0, 0, 40) };

        var s = KitTemplateSymbol.BuildFromDrawing(pins, body, KitTemplateSymbol.ChooseKitScale([(pins, body)]))!;

        double drawnLength = s.Primitives.OfType<LinePrimitive>().Max(l => Math.Abs(l.Y2 - l.Y1));
        Assert.True(drawnLength >= 300, $"a legible symbol, not {drawnLength} local units");
    }
}
