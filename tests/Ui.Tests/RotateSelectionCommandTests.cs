using CircuitRF.Ui.Commands.Symbol;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

// ── Layer 7 gate: RotateSelectionCommand — bbox-center anchor + 4×=identity + exact-restore undo ──

public class RotateSelectionCommandTests
{
    // ── 4× rotate returns to start ──────────────────────────────────────────────

    [Fact]
    public void FourExecutions_ReturnToStart_Line()
    {
        var sym  = new EditableSymbol();
        var line = new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                     50, -100, 150, -100);
        sym.Primitives.Add(line);

        // anchor = (MinX=50, MaxY=-100)
        var cmd = new RotateSelectionCommand(sym, sym.Primitives);

        cmd.Execute(); cmd.Execute(); cmd.Execute(); cmd.Execute();

        Assert.Equal(50.0,  line.X1);
        Assert.Equal(-100.0, line.Y1);
        Assert.Equal(150.0, line.X2);
        Assert.Equal(-100.0, line.Y2);
    }

    [Fact]
    public void FourExecutions_ReturnToStart_CircleAndLine()
    {
        var sym  = new EditableSymbol();
        sym.Primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                              -100, 0, 100, 0));
        var circle = new CirclePrimitive { Cx = 0, Cy = 150, R = 30, Filled = true };
        sym.Primitives.Add(circle);

        double origCx = circle.Cx, origCy = circle.Cy, origR = circle.R;
        var cmd = new RotateSelectionCommand(sym, sym.Primitives);

        cmd.Execute(); cmd.Execute(); cmd.Execute(); cmd.Execute();

        Assert.Equal(origCx, circle.Cx);
        Assert.Equal(origCy, circle.Cy);
        Assert.Equal(origR,  circle.R);
    }

    [Fact]
    public void FourExecutions_ReturnToStart_WithPins()
    {
        var sym = new EditableSymbol();
        sym.Primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                              0, 0, 100, 0));
        var pin = new SymbolPin(200, 100, 0);
        sym.Pins.Add(pin);

        var cmd = new RotateSelectionCommand(sym, sym.Primitives, sym.Pins);

        cmd.Execute(); cmd.Execute(); cmd.Execute(); cmd.Execute();

        Assert.Equal(200.0, pin.LocalX);
        Assert.Equal(100.0, pin.LocalY);
    }

    // ── Undo restores exact original coordinates ─────────────────────────────────

    [Fact]
    public void Undo_RestoresExactCoords_Line()
    {
        var sym  = new EditableSymbol();
        var line = new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                     33, 77, 123, 456);
        sym.Primitives.Add(line);

        double x1 = line.X1, y1 = line.Y1, x2 = line.X2, y2 = line.Y2;

        var cmd = new RotateSelectionCommand(sym, sym.Primitives);
        cmd.Execute();
        cmd.Undo();

        Assert.Equal(x1, line.X1);
        Assert.Equal(y1, line.Y1);
        Assert.Equal(x2, line.X2);
        Assert.Equal(y2, line.Y2);
    }

    [Fact]
    public void Undo_RestoresExactCoords_WithPins()
    {
        var sym = new EditableSymbol();
        sym.Primitives.Add(new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                              0, 0, 100, 0));
        var pin = new SymbolPin(200, 100, 0);
        sym.Pins.Add(pin);

        double oldX = pin.LocalX, oldY = pin.LocalY;

        var cmd = new RotateSelectionCommand(sym, sym.Primitives, sym.Pins);
        cmd.Execute();
        cmd.Undo();

        Assert.Equal(oldX, pin.LocalX);
        Assert.Equal(oldY, pin.LocalY);
    }

    [Fact]
    public void Undo_RestoresExactCoords_Rect()
    {
        var sym  = new EditableSymbol();
        var rect = new RectPrimitive { Cx = 50, Cy = 80, W = 120, H = 60 };
        sym.Primitives.Add(rect);

        double cx = rect.Cx, cy = rect.Cy, w = rect.W, h = rect.H;

        var cmd = new RotateSelectionCommand(sym, sym.Primitives);
        cmd.Execute();
        cmd.Undo();

        Assert.Equal(cx, rect.Cx);
        Assert.Equal(cy, rect.Cy);
        Assert.Equal(w,  rect.W);
        Assert.Equal(h,  rect.H);
    }

    // ── Center stays fixed (not corner) ─────────────────────────────────────────

    [Fact]
    public void Execute_CenterStaysFixed()
    {
        // Line: (50, -100) to (150, -100).
        // Bbox center = ((50+150)/2, (-100+-100)/2) = (100, -100).
        // Each point rotates 90° CW about (100, -100); the center itself stays fixed.
        var sym  = new EditableSymbol();
        var line = new LinePrimitive(SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                                     50, -100, 150, -100);
        sym.Primitives.Add(line);

        var cmd = new RotateSelectionCommand(sym, sym.Primitives);
        cmd.Execute();

        // (50, -100) rotated 90° CW about center (100, -100):
        // x' = 100 + (-100) − (−100) = 100,  y' = -100 − 100 + 50 = -150
        Assert.Equal(100.0,  line.X1);
        Assert.Equal(-150.0, line.Y1);
        // (150, -100) rotated 90° CW about center (100, -100):
        // x' = 100 + (-100) − (−100) = 100,  y' = -100 − 100 + 150 = -50
        Assert.Equal(100.0, line.X2);
        Assert.Equal(-50.0, line.Y2);
    }

    // ── Non-symmetric selection: 4× identity (would drift with corner anchor) ─────

    [Fact]
    public void FourExecutions_NonSymmetric_ReturnToStart_Rect()
    {
        // A Rect with W≠H — a corner-based anchor shifts after each rotation, causing
        // net translation over 4 presses. The center is rotation-invariant, giving 4×=identity.
        var sym  = new EditableSymbol();
        var rect = new RectPrimitive { Cx = 0, Cy = 0, W = 200, H = 100 };
        sym.Primitives.Add(rect);

        double origCx = rect.Cx, origCy = rect.Cy, origW = rect.W, origH = rect.H;
        var cmd = new RotateSelectionCommand(sym, sym.Primitives);

        cmd.Execute(); cmd.Execute(); cmd.Execute(); cmd.Execute();

        Assert.Equal(origCx, rect.Cx);
        Assert.Equal(origCy, rect.Cy);
        Assert.Equal(origW,  rect.W);
        Assert.Equal(origH,  rect.H);
    }
}
