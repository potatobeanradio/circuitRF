using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Drawing a wire and clicking onto another wire ends the draw and makes the connection there
/// (T-junction on a body, merge on an endpoint). Clicking empty space keeps drawing. Also covers
/// the live connection-dot preview during drags.
/// </summary>
public class WireDrawConnectTests
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
        var undo = new UndoRedoStack();
        var vm   = new SchematicViewModel(model, undo);
        return (model, vm, undo);
    }

    // ── Issue 1: ending a draw on another wire's body forms a connected T ──────

    [Fact]
    public void DrawWire_ClickOnWireBody_FinishesAndFormsTJunction()
    {
        var through = MakeWire((0, 0), (200, 0));
        var (model, vm, _) = MakeVm(through);
        vm.SetWireTool();

        vm.OnPointerPressed(100, 100, default);   // start in empty space
        Assert.True(vm.IsDrawingWire);

        vm.OnPointerPressed(100, 0, default);     // click on the through-wire's body (mid-span)

        // Draw ended, a second (stem) wire was committed, and the T-junction connects them.
        Assert.False(vm.IsDrawingWire);
        Assert.Equal(2, model.Wires.Count);

        var render = vm.RenderModel!;
        var dot = render.ConnectionDots.Single();   // auto-derived T-junction dot
        Assert.Equal((100.0, 0.0), (dot.X, dot.Y));
    }

    [Fact]
    public void DrawWire_ClickEmptySpace_KeepsDrawing()
    {
        var through = MakeWire((0, 0), (200, 0));
        var (model, vm, _) = MakeVm(through);
        vm.SetWireTool();

        vm.OnPointerPressed(100, 100, default);   // start
        vm.OnPointerPressed(300, 100, default);   // empty space — should NOT finish

        Assert.True(vm.IsDrawingWire);
        Assert.Single(model.Wires);               // nothing committed yet
    }

    [Fact]
    public void DrawWire_ClickOnWireEndpoint_FinishesAndMergesIntoSingleWire()
    {
        var existing = MakeWire((0, 0), (200, 0));
        var (model, vm, _) = MakeVm(existing);
        vm.SetWireTool();

        vm.OnPointerPressed(100, -100, default);  // start
        vm.OnPointerPressed(200, 0, default);     // click the existing wire's endpoint

        Assert.False(vm.IsDrawingWire);           // ended on the endpoint
        // Endpoint-to-endpoint coincidence merges the drawn wire into the existing one (§5.1).
        Assert.Single(model.Wires);
    }

    // ── Issue 2: live connection-dot preview during a drag ────────────────────

    [Fact]
    public void SegmentDrag_LiveDotPreview_FollowsStemMidDrag()
    {
        // Horizontal through-wire + a vertical stem T-ed onto its middle at (100,0).
        var through = MakeWire((0, 0), (200, 0));
        var stem    = MakeWire((100, 100), (100, 0));
        var (model, vm, _) = MakeVm(through, stem);

        // Grab the through-segment at (50,0) and drag up by 100 — DO NOT release yet.
        vm.OnPointerPressed(50, 0, default);
        vm.OnPointerMoved(50, -100, leftDown: true);

        // The live overlay carries the dot at the MOVED T point, not the stale (100,0).
        var liveDots = vm.Overlay.ConnectionDotsOverride;
        Assert.NotNull(liveDots);
        Assert.Contains(liveDots!, d => d.X == 100 && d.Y == -100);
        Assert.DoesNotContain(liveDots!, d => d.X == 100 && d.Y == 0);
    }

    [Fact]
    public void NoDrag_NoDotOverride()
    {
        var (_, vm, _) = MakeVm(MakeWire((0, 0), (200, 0)));
        Assert.Null(vm.Overlay.ConnectionDotsOverride);
    }
}
