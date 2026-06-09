using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Collinear overlapping/abutting wires are redundant and must simplify into a single segment —
/// no junction dots where collinear wires overlap. Dragging one wire onto a collinear one merges
/// them (both d1&gt;d2 and d1&lt;d2), undoably.
/// </summary>
public class CollinearMergeTests
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

    // ── Render layer: collinear overlap shows NO junction dots ────────────────

    [Fact]
    public void CollinearOverlappingWires_ShowNoDots()
    {
        // Two horizontal wires on the same line, overlapping on [100,200].
        var d1 = MakeWire((0, 0), (200, 0));
        var d2 = MakeWire((100, 0), (300, 0));
        var (render, _) = MakeVm(d1, d2).Model.BuildRenderModel();
        Assert.Empty(render.ConnectionDots);
    }

    [Fact]
    public void OverlapEndpoint_StillReadsConnected_NoRedDot()
    {
        var d1 = MakeWire((0, 0), (200, 0));
        var d2 = MakeWire((100, 0), (300, 0));
        var (render, _) = MakeVm(d1, d2).Model.BuildRenderModel();

        // d2's endpoint at (100,0) lies on d1's body → connected (no false "unconnected" indicator),
        // even though no dot is drawn (collinear, not a branch).
        var rd2 = render.Wires.First(w => w.Points[0] == (100.0, 0.0));
        Assert.True(rd2.StartConnected);
    }

    // ── Dragging one wire onto a collinear one merges them (live → commit) ────

    [Fact]
    public void DragWireOntoCollinearWire_MergesIntoOne_LongerFirst()
    {
        // d1 (long) at y=0; d2 (shorter) above it. Drag d2 down onto d1 → one merged wire.
        var d1 = MakeWire((0, 0), (300, 0));
        var d2 = MakeWire((100, 100), (250, 100));
        var (model, vm, undo) = MakeVm(d1, d2);

        // Grab d2's segment and drag down by 100 onto d1's line.
        vm.OnPointerPressed(175, 100, default);
        vm.OnPointerMoved(175, 0, leftDown: true);
        vm.OnPointerReleased(175, 0);

        Assert.Single(model.Wires);                                  // merged into one
        var m = model.Wires[0];
        var xs = m.Points.Select(p => p.X).OrderBy(x => x).ToList();
        Assert.Equal(0.0, xs.First(), 3);                           // union span [0,300]
        Assert.Equal(300.0, xs.Last(), 3);
        Assert.Empty(vm.RenderModel!.ConnectionDots);               // no junctions

        undo.Undo();
        Assert.Equal(2, model.Wires.Count);                         // both wires back
    }

    [Fact]
    public void DragWireOntoCollinearWire_MergesIntoOne_ShorterFirst()
    {
        // d1 (short) at y=0; d2 (longer) above. Same outcome — order independent.
        var d1 = MakeWire((100, 0), (200, 0));
        var d2 = MakeWire((0, 100), (300, 100));
        var (model, vm, _) = MakeVm(d1, d2);

        vm.OnPointerPressed(150, 100, default);
        vm.OnPointerMoved(150, 0, leftDown: true);
        vm.OnPointerReleased(150, 0);

        Assert.Single(model.Wires);
        var m = model.Wires[0];
        var xs = m.Points.Select(p => p.X).OrderBy(x => x).ToList();
        Assert.Equal(0.0, xs.First(), 3);
        Assert.Equal(300.0, xs.Last(), 3);
    }

    // ── Dragging a vertical wire onto a longer collinear one (connector collapses) ──

    [Fact]
    public void DragVerticalOntoLongerCollinear_MergesUnion_ConnectorRemoved_NoStrayJunction()
    {
        // w1 short vertical at x=0; w2 longer vertical at x=200 (extends past w1's end); H joins
        // them → 2 T's. Drag w1 right onto w2: they merge to the UNION, the now-degenerate H
        // connector is removed, and NO stray junction is left.
        // (w1 extended to 200 so the T at y=100 lands on a P-multiple interior point.)
        var w1 = MakeWire((0, 0), (0, 200));
        var w2 = MakeWire((200, 0), (200, 200));
        var h  = MakeWire((0, 100), (200, 100));
        var (model, vm, undo) = MakeVm(w1, w2, h);
        Assert.Equal(2, vm.RenderModel!.ConnectionDots.Count);   // precondition: 2 T's

        vm.OnPointerPressed(0, 25, default);             // grab w1's segment (away from the T)
        vm.OnPointerMoved(200, 25, leftDown: true);      // drag right onto w2
        vm.OnPointerReleased(200, 25);

        Assert.Single(model.Wires);                              // one merged wire
        var m = model.Wires[0];
        var ys = m.Points.Select(p => p.Y).OrderBy(y => y).ToList();
        Assert.Equal(0.0, ys.First(), 3);                       // union span [0,200]
        Assert.Equal(200.0, ys.Last(), 3);
        Assert.Empty(vm.RenderModel!.ConnectionDots);           // NO stray T-junction

        undo.Undo();
        Assert.Equal(3, model.Wires.Count);                     // all three wires back
        Assert.Equal(2, vm.RenderModel!.ConnectionDots.Count);  // both T's back
    }

    // ── Non-overlapping collinear wires do NOT merge ──────────────────────────

    [Fact]
    public void CollinearButDisjointWires_DoNotMerge()
    {
        var d1 = MakeWire((0, 0), (100, 0));
        var d2 = MakeWire((200, 100), (300, 100));   // x-range [200,300] disjoint from d1's [0,100]
        var (model, vm, _) = MakeVm(d1, d2);

        // Drag d2 down onto d1's line — collinear now, but the spans don't overlap → no merge.
        vm.OnPointerPressed(250, 100, default);
        vm.OnPointerMoved(250, 0, leftDown: true);
        vm.OnPointerReleased(250, 0);

        Assert.Equal(2, model.Wires.Count);
    }
}
