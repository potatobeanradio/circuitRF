using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The junction-dot invariant (§5.1): a user-placed EditableDot exists IFF it sits on a genuine
/// 4-way crossing. Enforced at placement (reject off a crossing, snap onto it) and at every
/// geometry edit (auto-remove a dot whose crossing dissolved, undoably). The auto-derived
/// T-junction dot is exempt — it is not an EditableDot.
/// </summary>
public class DotInvariantTests
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
        return (model, vm, vm.UndoRedo);
    }

    // crossing of H (0,0)-(200,0) and V (100,-100)-(100,100) at (100,0)
    private static (SchematicEditModel, SchematicViewModel, UndoRedoStack) CrossingVm()
        => MakeVm(MakeWire((0, 0), (200, 0)), MakeWire((100, -100), (100, 100)));

    // ── Placement enforcement ─────────────────────────────────────────────────

    [Fact]
    public void PlaceDot_OnCrossing_CreatesDotSnappedToIntersection()
    {
        var (model, vm, _) = CrossingVm();

        vm.PlaceDot(104, 3);   // a few units off the (100,0) crossing

        var dot = model.Dots.Single();
        Assert.Equal(100.0, dot.X, 3);
        Assert.Equal(0.0, dot.Y, 3);
    }

    [Fact]
    public void PlaceDot_EmptySpace_CreatesNothing()
    {
        var (model, vm, _) = CrossingVm();

        vm.PlaceDot(500, 500);   // nowhere near a crossing

        Assert.Empty(model.Dots);
    }

    [Fact]
    public void PlaceDot_OnSingleWireBody_CreatesNothing()
    {
        var (model, vm, _) = MakeVm(MakeWire((0, 0), (200, 0)));   // lone wire, no crossing

        vm.PlaceDot(100, 0);

        Assert.Empty(model.Dots);
    }

    // ── Auto-removal when the crossing dissolves (undoable) ───────────────────

    [Fact]
    public void MovingWireAwayFromCrossing_AutoRemovesDot_AndUndoRestoresBoth()
    {
        var (model, vm, undo) = CrossingVm();
        vm.PlaceDot(100, 0);
        Assert.Single(model.Dots);
        var vertical = model.Wires[1];   // (100,-100)-(100,100)

        // Move the vertical wire sideways by 50 so it no longer crosses the horizontal one.
        var snap = new WireMoveSnapshot(vertical, vertical.Points.ToList(),
            new[] { (150.0, -100.0), (150.0, 100.0) });
        vm.Execute(new MoveCommand(model, [], [snap], []));

        // The crossing is gone → the dot was auto-removed as part of that same edit.
        Assert.Empty(model.Dots);
        Assert.Empty(vm.RenderModel!.ConnectionDots);

        // One Undo restores BOTH the wire position and the dot.
        undo.Undo();
        Assert.Single(model.Dots);
        Assert.Equal((100.0, -100.0), model.FindWire(vertical.Id)!.Points[0]);
        Assert.Single(vm.RenderModel!.ConnectionDots);
    }

    [Fact]
    public void DeletingACrossingWire_AutoRemovesDot_Undoable()
    {
        var (model, vm, undo) = CrossingVm();
        vm.PlaceDot(100, 0);
        Assert.Single(model.Dots);
        var vertical = model.Wires[1];

        vm.Execute(new DeleteCommand(model, [vertical.Id]));

        Assert.Empty(model.Dots);          // crossing gone with the wire → dot removed
        undo.Undo();
        Assert.Single(model.Dots);         // wire and dot both back
    }

    // ── Redundant dot atop a T is not retained on the next edit ───────────────

    [Fact]
    public void RedundantDotAtTJunction_RemovedOnNextGeometryEdit()
    {
        // Build a T: H (0,0)-(200,0) with a stem ending at (100,0). Manually add a (redundant)
        // user dot at the T, then run any geometry edit — the invariant removes the inert dot.
        var (model, vm, undo) = MakeVm(MakeWire((0, 0), (200, 0)), MakeWire((100, 100), (100, 0)));
        model.Dots.Add(new EditableDot { X = 100, Y = 0 });   // redundant: a T is not a crossing

        var h = model.Wires[0];
        var snap = new WireMoveSnapshot(h, h.Points.ToList(),
            new[] { (0.0, 0.0), (200.0, 0.0) });   // no-op move still triggers re-validation
        vm.Execute(new MoveCommand(model, [], [snap], []));

        Assert.Empty(model.Dots);   // the redundant T dot was removed
    }
}
