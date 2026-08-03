using CircuitRF.Core.Pdk;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A kit whose symbols live in a library gives terminals, not a drawing: several parts share one
/// template. The pins are the kit's, where it put them; the body is circuitRF's own.
///
/// <para>Pins must land on exact multiples of the connection grid or a wire will not attach — which
/// is why placement goes through the same scale, snap and axis flip the drawing reader uses, and why
/// that is asserted here rather than assumed.</para>
/// </summary>
public class KitTemplateSymbolTests
{
    private static Symbol Build(params (string Name, int X, int Y)[] pins) =>
        KitTemplateSymbol.Build([.. pins.Select(p => new KitSymbolPin(p.Name, p.X, p.Y))])!;

    [Fact]
    public void EveryPinLandsExactlyOnTheConnectionGrid()
    {
        // Off-grid by any amount and a wire silently will not attach to it.
        var s = Build(("1", 1000, 0), ("2", 537, 491), ("3", 0, 0));

        Assert.All(s.Pins, p =>
        {
            Assert.Equal(0, p.LocalX % 100);
            Assert.Equal(0, p.LocalY % 100);
        });
    }

    [Fact]
    public void TheAxisIsFlipped_BecauseTheLibraryIsYUp()
    {
        // Getting this wrong mirrors every symbol vertically — which still places, still connects,
        // and is wrong everywhere it is drawn.
        var s = Build(("1", 0, 0), ("2", 0, 1000));

        Assert.Equal(0, s.Pins[0].LocalY);
        Assert.True(s.Pins[1].LocalY < 0, "a pin the library puts ABOVE the origin must draw above it");
    }

    [Fact]
    public void PinOrderAndNamesAreTheKitsOwn()
    {
        var s = Build(("RF", 0, 0), ("LO", 500, 500), ("IF", 1000, 0));

        Assert.Equal(["RF", "LO", "IF"], s.Pins.Select(p => p.Name));
        Assert.Equal([1, 2, 3], s.Pins.Select(p => p.PortIndex));
        Assert.Equal(3, s.PortCount);
    }

    [Fact]
    public void ATwoTerminalPartStillGetsABody()
    {
        // Its pins are colinear, so one dimension of their bounding box is zero. Without a floor the
        // body collapses to a line the user can neither see nor click.
        var s = Build(("1", 0, 0), ("2", 1000, 0));

        var xs = s.Primitives.OfType<LinePrimitive>().SelectMany(l => new[] { l.Y1, l.Y2 }).ToList();
        Assert.True(xs.Max() - xs.Min() >= 100, "the body has no height");
    }

    [Fact]
    public void EveryPinIsJoinedToTheBody()
    {
        // A pin drawn with nothing leading to it looks like a stray dot and gives the user nothing
        // to aim at.
        var s = Build(("1", 0, 0), ("2", 500, 500), ("3", 1000, 0));
        var lines = s.Primitives.OfType<LinePrimitive>().ToList();

        Assert.All(s.Pins, p => Assert.Contains(lines, l =>
            (Math.Abs(l.X1 - p.LocalX) < 0.5 && Math.Abs(l.Y1 - p.LocalY) < 0.5) ||
            (Math.Abs(l.X2 - p.LocalX) < 0.5 && Math.Abs(l.Y2 - p.LocalY) < 0.5)));
    }

    [Fact]
    public void ScaleIsChosenSoAHugeLibraryUnitIsStillLegible()
    {
        // A library may be authored in any drawing unit; nothing here knows which.
        var small = Build(("1", 0, 0), ("2", 10, 0));
        var large = Build(("1", 0, 0), ("2", 1_000_000, 0));

        foreach (var s in new[] { small, large })
        {
            double span = s.Pins.Max(p => p.LocalX) - s.Pins.Min(p => p.LocalX);
            Assert.InRange(span, 100, 100_000);
        }
    }

    [Fact]
    public void ATemplateWithNoTerminalsProducesNoSymbol()
    {
        Assert.Null(KitTemplateSymbol.Build([]));
        Assert.Null(KitTemplateSymbol.Build(null));
    }
}
