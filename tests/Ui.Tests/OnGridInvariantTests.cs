using System;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// R7 on-grid invariant: after any edit battery, every pin world coordinate, wire
/// endpoint/bend, and junction dot must be an exact multiple of the connection grid P
/// (within float-dust ε = 1e-6 × P). Guards Layers 4–6.
/// </summary>
public class OnGridInvariantTests
{
    private const double P   = 100.0;  // default connection grid = SchematicEditModel.GridSize
    private const double Eps = 1e-6;

    // IsOnGrid: |coord/P − round(coord/P)| < ε  (per R7 in grid-and-connectivity.md)
    private static bool IsOnGrid(double coord)
        => Math.Abs(coord / P - Math.Round(coord / P)) < Eps;

    /// <summary>Asserts every component origin, pin world coord, wire vertex, and
    /// auto-derived junction dot lies exactly on the connection grid P.</summary>
    private static void AssertOnGrid(SchematicEditModel model, string step)
    {
        foreach (var comp in model.Components)
        {
            Assert.True(IsOnGrid(comp.X),
                $"[{step}] {comp.InstanceName}.X = {comp.X} not on P={P}");
            Assert.True(IsOnGrid(comp.Y),
                $"[{step}] {comp.InstanceName}.Y = {comp.Y} not on P={P}");

            for (int i = 0; i < comp.PortCount; i++)
            {
                var (wx, wy) = comp.GetPortWorldCoord(i);
                Assert.True(IsOnGrid(wx),
                    $"[{step}] {comp.InstanceName} port[{i}].WorldX = {wx} not on P={P}");
                Assert.True(IsOnGrid(wy),
                    $"[{step}] {comp.InstanceName} port[{i}].WorldY = {wy} not on P={P}");
            }
        }

        foreach (var wire in model.Wires)
            foreach (var pt in wire.Points)
            {
                Assert.True(IsOnGrid(pt.X),
                    $"[{step}] wire[{wire.Id}] vertex.X = {pt.X} not on P={P}");
                Assert.True(IsOnGrid(pt.Y),
                    $"[{step}] wire[{wire.Id}] vertex.Y = {pt.Y} not on P={P}");
            }

        var (renderModel, _) = model.BuildRenderModel();
        foreach (var dot in renderModel.ConnectionDots)
        {
            Assert.True(IsOnGrid(dot.X),
                $"[{step}] dot.X = {dot.X} not on P={P}");
            Assert.True(IsOnGrid(dot.Y),
                $"[{step}] dot.Y = {dot.Y} not on P={P}");
        }
    }

    private static void PlaceAt(SchematicViewModel vm, SymbolKind kind, double x, double y)
    {
        vm.BeginPlacement(kind);
        vm.OnPointerPressed(x, y, default);
    }

    [Fact]
    public void AllEditOps_KeepConnectionPointsOnGrid()
    {
        var model = new SchematicEditModel();  // GridSnap = true (default), GridSize = 100.0
        var undo  = new UndoRedoStack();
        var vm    = new SchematicViewModel(model, undo);

        // ── 1. Place every built-in component type ──────────────────────────
        PlaceAt(vm, SymbolKind.Resistor,         0,    0);
        PlaceAt(vm, SymbolKind.Capacitor,      600,    0);
        PlaceAt(vm, SymbolKind.Inductor,      1200,    0);
        PlaceAt(vm, SymbolKind.VoltageSource,    0,  600);
        PlaceAt(vm, SymbolKind.ToneSource,     600,  600);
        PlaceAt(vm, SymbolKind.Ground,        1200,  600);
        PlaceAt(vm, SymbolKind.Port,             0, 1200);
        PlaceAt(vm, SymbolKind.FetSdd,         600, 1200);
        PlaceAt(vm, SymbolKind.ZPort,         1200, 1200);
        PlaceAt(vm, SymbolKind.Sdd,              0, 1800);
        PlaceAt(vm, SymbolKind.Generic,        600, 1800);
        vm.SetSelectTool();
        AssertOnGrid(model, "after-placement");

        // ── 2. Drag a component ─────────────────────────────────────────────
        // drag delta (200, 300) → SnapToGrid(0+200)=200, SnapToGrid(0+300)=300 — both on P
        var r1 = model.Components.First(c => c.Symbol == SymbolKind.Resistor);
        vm.OnPointerPressed(r1.X, r1.Y, default);
        vm.OnPointerMoved(r1.X + 200, r1.Y + 300, leftDown: true);
        vm.OnPointerReleased(r1.X + 200, r1.Y + 300);
        AssertOnGrid(model, "after-drag");

        // ── 3. Rotate through all four steps (CCW: R0→R90→R180→R270→R0) ────
        vm.Selection.SelectOne(r1.Id);
        for (int i = 0; i < 4; i++)
        {
            vm.RotateSelection(clockwise: false);
            AssertOnGrid(model, $"after-rotate-{i + 1}");
        }

        // ── 4. Mirror horizontal then vertical ──────────────────────────────
        vm.MirrorSelection(horizontal: true);
        AssertOnGrid(model, "after-mirror-H");
        vm.MirrorSelection(horizontal: false);
        AssertOnGrid(model, "after-mirror-V");
        vm.Selection.Clear();

        // ── 5. Draw wires ────────────────────────────────────────────────────
        vm.SetWireTool();
        // Straight horizontal wire: (300, 0) → (700, 0)
        vm.OnPointerPressed(300, 0, default);
        vm.OnPointerPressed(700, 0, default);
        vm.FinishCurrentWire();
        // L-shaped wire: (300, 400) → bend at (700, 400) → (700, 800)
        vm.OnPointerPressed(300, 400, default);
        vm.OnPointerPressed(700, 800, default);
        vm.FinishCurrentWire();
        vm.SetSelectTool();
        AssertOnGrid(model, "after-wire-draw");

        // ── 6. Segment drag ──────────────────────────────────────────────────
        // Find the straight horizontal wire; press its midpoint; drag vertically +200.
        var hWire = model.Wires.FirstOrDefault(w =>
            w.Points.Count == 2 && Math.Abs(w.Points[0].Y - w.Points[1].Y) < 1.0);
        if (hWire is not null)
        {
            double midX = (hWire.Points[0].X + hWire.Points[1].X) / 2.0;
            double midY = hWire.Points[0].Y;
            vm.OnPointerPressed(midX, midY, default);
            vm.OnPointerMoved(midX, midY + 200, leftDown: true);
            vm.OnPointerReleased(midX, midY + 200);
            AssertOnGrid(model, "after-segment-drag");
        }

        // ── 7. Nudge in all four directions ──────────────────────────────────
        var cap = model.Components.First(c => c.Symbol == SymbolKind.Capacitor);
        vm.Selection.SelectOne(cap.Id);
        vm.NudgeSelection( P,  0);
        vm.NudgeSelection(-P,  0);
        vm.NudgeSelection( 0,  P);
        vm.NudgeSelection( 0, -P);
        vm.Selection.Clear();
        AssertOnGrid(model, "after-nudge");

        // ── 8. Paste (same-grid) ─────────────────────────────────────────────
        var pComp = new EditableComponent
            { InstanceName = "R_paste", Symbol = SymbolKind.Resistor, X = 2000, Y = 0 };
        var pWire = new EditableWire();
        pWire.Points.AddRange([(2000.0, 0.0), (2400.0, 0.0)]);
        vm.Execute(new SchematicPasteCommand(model, [pComp], [pWire], []));
        AssertOnGrid(model, "after-paste");

        // ── 9. Undo / Redo ────────────────────────────────────────────────────
        undo.Undo();
        AssertOnGrid(model, "after-undo");
        undo.Redo();
        AssertOnGrid(model, "after-redo");
    }
}
