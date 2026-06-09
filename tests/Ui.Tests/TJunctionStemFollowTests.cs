using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Dragging a through-wire's segment carries its T-junction stems along (§5.1):
/// each stem endpoint stays on the moved segment, the auto-dot follows, the stem's
/// far end is anchored, and one Undo restores everything.
/// </summary>
public class TJunctionStemFollowTests
{
    private static EditableWire MakeWire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static (SchematicEditModel Model, SchematicViewModel Vm, UndoRedoStack Undo) MakeVm(
        params EditableWire[] wires)
    {
        var model = new SchematicEditModel { GridSnap = false };
        foreach (var w in wires) model.Wires.Add(w);
        var vm   = new SchematicViewModel(model);
        vm.SetSelectTool();
        return (model, vm, vm.UndoRedo);
    }

    /// <summary>Drag the segment grabbed at (pressX,pressY) to (toX,toY) — press, move, release.</summary>
    private static void DragSegment(SchematicViewModel vm, double pressX, double pressY, double toX, double toY)
    {
        vm.OnPointerPressed(pressX, pressY, default);
        vm.OnPointerMoved(toX, toY, leftDown: true);
        vm.OnPointerReleased(toX, toY);
    }

    private static IReadOnlyList<(double X, double Y)> DotPoints(SchematicViewModel vm)
        => vm.RenderModel!.ConnectionDots.Select(d => (d.X, d.Y)).ToList();

    // ── Core: stem follows, dot moves, far end anchored ───────────────────────

    [Fact]
    public void DragThroughSegment_StemFollows_DotMoves_FarEndAnchored()
    {
        // Horizontal through-wire (0,0)-(200,0); vertical stem (100,100) → (100,0) T-ed at (100,0).
        var through = MakeWire((0, 0), (200, 0));
        var stem    = MakeWire((100, 100), (100, 0));
        var (model, vm, _) = MakeVm(through, stem);

        // Sanity: the T-junction dot exists at (100,0) before the drag.
        Assert.Contains((100.0, 0.0), DotPoints(vm));

        // Grab the through-segment at (50,0) (away from vertices and the stem) and drag up by 100.
        DragSegment(vm, 50, 0, 50, -100);

        var rThrough = model.FindWire(through.Id)!;
        var rStem    = model.FindWire(stem.Id)!;

        // Through-wire translated to y = -100.
        Assert.Equal(new List<(double, double)> { (0, -100), (200, -100) }, rThrough.Points);

        // Stem's junction end followed to (100,-100); its far end (100,100) stayed put.
        Assert.Equal((100.0, 100.0), rStem.Points[0]);          // far end anchored
        Assert.Equal((100.0, -100.0), rStem.Points[^1]);        // junction followed

        // Auto-dot re-derived at the new T point; no stale dot at the old one.
        var dots = DotPoints(vm);
        Assert.Contains((100.0, -100.0), dots);
        Assert.DoesNotContain((100.0, 0.0), dots);
        Assert.Single(dots);
    }

    [Fact]
    public void DragThroughSegment_StemEndReadsConnected()
    {
        var through = MakeWire((0, 0), (200, 0));
        var stem    = MakeWire((100, 100), (100, 0));
        var (model, vm, _) = MakeVm(through, stem);

        DragSegment(vm, 50, 0, 50, -100);

        var rStem = vm.RenderModel!.Wires.First(w => w.Id == stem.Id);
        // The followed junction endpoint (Points[^1]) reads connected — no false red dot.
        Assert.True(rStem.EndConnected);
    }

    // ── One undoable commit restores through-segment AND stem ─────────────────

    [Fact]
    public void Undo_RestoresThroughAndStem_InOneStep()
    {
        var through = MakeWire((0, 0), (200, 0));
        var stem    = MakeWire((100, 100), (100, 0));
        var (model, vm, undo) = MakeVm(through, stem);

        DragSegment(vm, 50, 0, 50, -100);
        undo.Undo();

        var rThrough = model.FindWire(through.Id)!;
        var rStem    = model.FindWire(stem.Id)!;
        Assert.Equal(new List<(double, double)> { (0, 0), (200, 0) }, rThrough.Points);
        Assert.Equal(new List<(double, double)> { (100, 100), (100, 0) }, rStem.Points);

        // Dot is back at the original T point.
        Assert.Contains((100.0, 0.0), DotPoints(vm));
    }

    // ── Multiple stems on one segment all follow ──────────────────────────────

    [Fact]
    public void DragThroughSegment_MultipleStems_AllFollow()
    {
        var through = MakeWire((0, 0), (300, 0));
        var stemA   = MakeWire((100, 100), (100, 0));
        var stemB   = MakeWire((200, 100), (200, 0));
        var (model, vm, _) = MakeVm(through, stemA, stemB);

        DragSegment(vm, 50, 0, 50, -100);

        Assert.Equal((100.0, -100.0), model.FindWire(stemA.Id)!.Points[^1]);
        Assert.Equal((200.0, -100.0), model.FindWire(stemB.Id)!.Points[^1]);

        var dots = DotPoints(vm);
        Assert.Contains((100.0, -100.0), dots);
        Assert.Contains((200.0, -100.0), dots);
        Assert.Equal(2, dots.Count);
    }

    // ── Scope guard: dragging the stem's own end-segment does NOT move the through-wire ──

    [Fact]
    public void DragStemSegment_ThroughWireUnchanged()
    {
        var through = MakeWire((0, 0), (200, 0));
        var stem    = MakeWire((100, 100), (100, 0));
        var (model, vm, _) = MakeVm(through, stem);

        // Grab the stem's own vertical segment (at x=100, mid-way up) and drag it sideways.
        DragSegment(vm, 100, 60, 140, 60);

        // The through-wire is untouched — stem-follow only applies to the through-wire's segment.
        Assert.Equal(new List<(double, double)> { (0, 0), (200, 0) }, model.FindWire(through.Id)!.Points);
    }
}
