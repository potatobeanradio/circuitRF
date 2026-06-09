using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Connecting a new wire must never UNCONNECT an existing one (§5.1). Starting/finishing a wire
/// at a T-junction must not let the endpoint-merge bury the T and silently drop the connection.
/// </summary>
public class WireMergeTJunctionTests
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

    private static bool HasDotAt(SchematicViewModel vm, double x, double y)
        => vm.RenderModel!.ConnectionDots.Any(d => d.X == x && d.Y == y);

    [Fact]
    public void DrawWireFromTJunction_PreservesConnection_DoesNotMergeAwayTheT()
    {
        // B horizontal (0,0)-(200,0); A vertical ending on B's body at (100,0) → T-junction.
        var b = MakeWire((0, 0), (200, 0));
        var a = MakeWire((100, 100), (100, 0));
        var (model, vm, _) = MakeVm(b, a);
        Assert.True(HasDotAt(vm, 100, 0), "precondition: T-junction dot exists");

        // Start a new wire C at the T-junction and extend it straight down (collinear with A).
        vm.SetWireTool();
        vm.OnPointerPressed(100, 0, default);     // snaps to A's endpoint at the T
        vm.OnPointerPressed(100, -100, default);  // empty space → keeps drawing
        vm.FinishCurrentWire();

        // The merge that WOULD collapse A+C into a straight line (burying the T) is suppressed:
        // three wires remain, and the T-junction dot is still there — the connection survived.
        Assert.Equal(3, model.Wires.Count);
        Assert.True(HasDotAt(vm, 100, 0), "T-junction connection must survive drawing a new wire from it");
    }

    [Fact]
    public void DrawWireFromFreeEndpoint_StillMergesNormally()
    {
        // A lone wire with a free endpoint at (100,0) — no third wire there, so a continuation
        // should merge as before (no over-suppression of the normal merge cleanup).
        var a = MakeWire((100, 100), (100, 0));
        var (model, vm, _) = MakeVm(a);

        vm.SetWireTool();
        vm.OnPointerPressed(100, 0, default);     // start at A's free endpoint
        vm.OnPointerPressed(100, -100, default);
        vm.FinishCurrentWire();

        // Collinear continuation merges into a single wire (normal behavior, no T to protect).
        Assert.Single(model.Wires);
    }

    [Fact]
    public void DrawWireFromTJunction_AllThreeWiresShareTheNode()
    {
        var b = MakeWire((0, 0), (200, 0));
        var a = MakeWire((100, 100), (100, 0));
        var (model, vm, _) = MakeVm(b, a);

        vm.SetWireTool();
        vm.OnPointerPressed(100, 0, default);
        vm.OnPointerPressed(100, -100, default);
        vm.FinishCurrentWire();

        // Both A and the new wire keep an endpoint exactly on B's body at the junction.
        var render = vm.RenderModel!;
        int endpointsAtJunction = render.Wires
            .Count(w => (w.Points[0].X == 100 && w.Points[0].Y == 0) ||
                        (w.Points[^1].X == 100 && w.Points[^1].Y == 0));
        Assert.Equal(2, endpointsAtJunction);   // A's end + new wire's end, both at the T point
    }
}
