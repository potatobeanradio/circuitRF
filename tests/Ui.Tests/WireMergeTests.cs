using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Tests for the wire endpoint merge feature: when a drawn or dragged wire's endpoint
/// lands exactly on another wire's endpoint, the two wires are joined into one.
/// A single Undo splits them back.  Three-or-more-way junctions are left intact.
/// </summary>
public class WireMergeTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EditableWire MakeWire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static (SchematicEditModel Model, SchematicViewModel Vm) MakeVm()
    {
        var model = new SchematicEditModel { GridSnap = false };
        var vm    = new SchematicViewModel(model);
        return (model, vm);
    }

    // ── Wire draw → merge ─────────────────────────────────────────────────────

    [Fact]
    public void DrawWire_EndpointOnOtherWireEnd_MergesIntoOneWire()
    {
        var (model, vm) = MakeVm();

        // Pre-place wire A: (0,0) → (100,0)
        model.Wires.Add(MakeWire((0, 0), (100, 0)));

        // Draw wire B: (100,0) → (200,0) — collinear continuation; endpoint hits wireA's end.
        vm.SetWireTool();
        vm.OnPointerPressed(100, 0, KeyModifiers.None);
        vm.OnPointerPressed(200, 0, KeyModifiers.None);
        vm.FinishCurrentWire();

        Assert.Single(model.Wires);
        var merged = model.Wires[0];
        Assert.Equal(2, merged.Points.Count);
        Assert.Equal((0.0,   0.0), merged.Points[0]);
        Assert.Equal((200.0, 0.0), merged.Points[1]);
    }

    [Fact]
    public void DrawWire_LShapeEndOnOtherWireEnd_MergesIntoOneWire()
    {
        var (model, vm) = MakeVm();

        // Pre-place wire A: (0,0) → (100,0) (horizontal)
        model.Wires.Add(MakeWire((0, 0), (100, 0)));

        // Draw wire B: (100,100) → (100,0) (vertical, ending at wireA's end)
        vm.SetWireTool();
        vm.OnPointerPressed(100, 100, KeyModifiers.None);
        vm.OnPointerPressed(100, 0,   KeyModifiers.None);
        vm.FinishCurrentWire();

        Assert.Single(model.Wires);
        var merged = model.Wires[0];
        Assert.True(merged.Points.Count >= 2, "merged must have ≥ 2 points");
        // Both original endpoints are preserved (order may vary by orientation).
        var pts = merged.Points;
        bool hasOrigin  = pts.Any(p => Math.Abs(p.X) < 1e-6 && Math.Abs(p.Y) < 1e-6);
        bool hasFarDown = pts.Any(p => Math.Abs(p.X - 100) < 1e-6 && Math.Abs(p.Y - 100) < 1e-6);
        Assert.True(hasOrigin,  "merged wire must contain (0,0)");
        Assert.True(hasFarDown, "merged wire must contain (100,100)");
    }

    [Fact]
    public void DrawWire_EndpointOnOtherWireStart_MergesIntoOneWire()
    {
        var (model, vm) = MakeVm();

        // Wire A from (100,0) → (200,0) (start at 100)
        model.Wires.Add(MakeWire((100, 0), (200, 0)));

        // Draw wire B from (0,0) → (100,0) — B's end hits wireA's start.
        vm.SetWireTool();
        vm.OnPointerPressed(0,   0, KeyModifiers.None);
        vm.OnPointerPressed(100, 0, KeyModifiers.None);
        vm.FinishCurrentWire();

        Assert.Single(model.Wires);
        var merged = model.Wires[0];
        Assert.Equal(2, merged.Points.Count);
        Assert.Equal((0.0,   0.0), merged.Points[0]);
        Assert.Equal((200.0, 0.0), merged.Points[1]);
    }

    [Fact]
    public void DrawWire_EndpointOnOtherWireEnd_Undo_RestoresWireA()
    {
        // Draw wire B that merges with pre-placed wire A.
        // Before draw: 1 wire (wireA). After draw+merge: 1 wire (merged).
        // After undo: 1 wire (wireA) — the drawn wire disappears as if never placed.
        var model    = new SchematicEditModel { GridSnap = false };
        var vm       = new SchematicViewModel(model);
        var undoRedo = vm.UndoRedo;

        var wireA = MakeWire((0, 0), (100, 0));
        model.Wires.Add(wireA);

        vm.SetWireTool();
        vm.OnPointerPressed(100, 0, KeyModifiers.None);
        vm.OnPointerPressed(200, 0, KeyModifiers.None);
        vm.FinishCurrentWire();

        Assert.Single(model.Wires);   // merged result

        undoRedo.Undo();

        // After undo: wireA is restored, drawn wire is gone (undo == "never drew").
        Assert.Single(model.Wires);
        Assert.Same(wireA, model.Wires[0]);
        Assert.Equal((0.0,   0.0), wireA.Points[0]);
        Assert.Equal((100.0, 0.0), wireA.Points[1]);
    }

    [Fact]
    public void DragMerge_CompositeUndo_RestoresBothSeparateWires()
    {
        // Direct command test: simulate the drag-and-merge composite.
        // Before: two separate wires. After Execute: merged. After Undo: both separate again.
        var model   = new SchematicEditModel();
        var wireA   = MakeWire((0, 0), (90, 0));   // will be dragged so end reaches (100,0)
        var wireB   = MakeWire((100, 0), (200, 0)); // stationary
        model.Wires.Add(wireA);
        model.Wires.Add(wireB);

        // End state after drag: wireA endpoint moves from (90,0) to (100,0).
        var startPtsA = wireA.Points.ToList();
        var endPtsA   = new List<(double X, double Y)> { (0, 0), (100, 0) };

        // Build merged point list.
        var mergedPts = WireGeometry.TryBuildMergedPoints(endPtsA, wireB.Points, 8.0)!;
        Assert.NotNull(mergedPts);
        var merged = new EditableWire();
        merged.Points.AddRange(mergedPts);

        var moveCmd  = new MoveCommand(model, [], [new WireMoveSnapshot(wireA, startPtsA, endPtsA)], []);
        var mergeCmd = new WireMergeCommand(model, wireA, 0, endPtsA, wireB, merged);
        var composite = new CompositeCommand(moveCmd, mergeCmd);

        // Simulate pre-Execute state (restore wireA to start).
        wireA.Points.Clear();
        wireA.Points.AddRange(startPtsA);

        composite.Execute();
        Assert.Single(model.Wires);
        Assert.Same(merged, model.Wires[0]);

        composite.Undo();
        Assert.Equal(2, model.Wires.Count);
        Assert.Contains(wireA, model.Wires);
        Assert.Contains(wireB, model.Wires);
        // wireA should have its pre-drag (start) points after the full undo.
        Assert.Equal((0.0,  0.0), wireA.Points[0]);
        Assert.Equal((90.0, 0.0), wireA.Points[1]);
        // wireB should have its original points.
        Assert.Equal((100.0, 0.0), wireB.Points[0]);
        Assert.Equal((200.0, 0.0), wireB.Points[1]);
    }

    // ── Three-way junction: must not merge ────────────────────────────────────

    [Fact]
    public void DrawWire_ThreeWireJunction_DoesNotMerge()
    {
        var (model, vm) = MakeVm();

        // Two pre-placed wires sharing endpoint at (100,0).
        model.Wires.Add(MakeWire((0, 0),   (100, 0)));
        model.Wires.Add(MakeWire((100, 0), (200, 0)));

        // Draw a third wire touching the shared point — three wires now meet at (100,0).
        vm.SetWireTool();
        vm.OnPointerPressed(100,   0, KeyModifiers.None);
        vm.OnPointerPressed(100, 100, KeyModifiers.None);
        vm.FinishCurrentWire();

        // Three-way junction → no merge; all three wires preserved.
        Assert.Equal(3, model.Wires.Count);
    }

    // ── Mid-segment landing: must not merge ───────────────────────────────────

    [Fact]
    public void DrawWire_EndpointOnMidSegment_DoesNotMerge()
    {
        var (model, vm) = MakeVm();

        // Wire A: (0,0) → (200,0). The midpoint (100,0) is NOT an endpoint.
        model.Wires.Add(MakeWire((0, 0), (200, 0)));

        // Draw wire B ending at the mid-point of wire A — not a wire endpoint.
        vm.SetWireTool();
        vm.OnPointerPressed(100, 100, KeyModifiers.None);
        vm.OnPointerPressed(100,   0, KeyModifiers.None);
        vm.FinishCurrentWire();

        // No merge: mid-segment landing is a T-junction (future feature), not a merge.
        Assert.Equal(2, model.Wires.Count);
    }

    // ── Standalone TryBuildMergedPoints geometry tests ────────────────────────

    [Fact]
    public void TryBuildMergedPoints_AEndBStart_ProducesCorrectMerge()
    {
        var a = new List<(double X, double Y)> { (0, 0), (100, 0) };
        var b = new List<(double X, double Y)> { (100, 0), (200, 0) };
        var result = WireGeometry.TryBuildMergedPoints(a, b, 8.0);
        Assert.NotNull(result);
        // Collinear → normalized to 2 points.
        Assert.Equal(2, result!.Count);
        Assert.Equal((0.0, 0.0),   result[0]);
        Assert.Equal((200.0, 0.0), result[1]);
    }

    [Fact]
    public void TryBuildMergedPoints_AStartBEnd_ProducesCorrectMerge()
    {
        // B ends where A starts: B→A orientation.
        var a = new List<(double X, double Y)> { (100, 0), (200, 0) };
        var b = new List<(double X, double Y)> { (0, 0),   (100, 0) };
        var result = WireGeometry.TryBuildMergedPoints(a, b, 8.0);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Equal((0.0, 0.0),   result[0]);
        Assert.Equal((200.0, 0.0), result[1]);
    }

    [Fact]
    public void TryBuildMergedPoints_AEndBEnd_ProducesCorrectMerge()
    {
        // B is reversed: A ends where B ends.
        var a = new List<(double X, double Y)> { (0,  0), (100, 0) };
        var b = new List<(double X, double Y)> { (200, 0), (100, 0) };
        var result = WireGeometry.TryBuildMergedPoints(a, b, 8.0);
        Assert.NotNull(result);
        Assert.Equal(2, result!.Count);
        Assert.Equal((0.0, 0.0),   result[0]);
        Assert.Equal((200.0, 0.0), result[1]);
    }

    [Fact]
    public void TryBuildMergedPoints_NoCoincidence_ReturnsNull()
    {
        var a = new List<(double X, double Y)> { (0, 0),   (100, 0) };
        var b = new List<(double X, double Y)> { (200, 0), (300, 0) };
        Assert.Null(WireGeometry.TryBuildMergedPoints(a, b, 8.0));
    }

    [Fact]
    public void TryBuildMergedPoints_BothEndsCoincide_ReturnsNull()
    {
        // A closed loop / degenerate: all four endpoints coincide.
        var a = new List<(double X, double Y)> { (0, 0), (100, 0) };
        var b = new List<(double X, double Y)> { (0, 0), (100, 0) };
        Assert.Null(WireGeometry.TryBuildMergedPoints(a, b, 8.0));
    }

    [Fact]
    public void TryBuildMergedPoints_LShapeJunction_ProducesThreePoints()
    {
        // A: (0,0)→(100,0) horizontal; B: (100,0)→(100,100) vertical. L-shape result.
        var a = new List<(double X, double Y)> { (0, 0),   (100, 0)   };
        var b = new List<(double X, double Y)> { (100, 0), (100, 100) };
        var result = WireGeometry.TryBuildMergedPoints(a, b, 8.0);
        Assert.NotNull(result);
        Assert.Equal(3, result!.Count);
        Assert.Equal((0.0,   0.0),   result[0]);
        Assert.Equal((100.0, 0.0),   result[1]);
        Assert.Equal((100.0, 100.0), result[2]);
    }

    // ── WireMergeCommand direct unit tests ────────────────────────────────────

    [Fact]
    public void WireMergeCommand_ExecuteAndUndo_RestoresBothWires()
    {
        var model  = new SchematicEditModel();
        var wireA  = MakeWire((0, 0), (100, 0));
        var wireB  = MakeWire((100, 0), (200, 0));
        model.Wires.Add(wireA);
        model.Wires.Add(wireB);

        var mergedPts = WireGeometry.TryBuildMergedPoints(wireA.Points, wireB.Points, 8.0)!;
        var merged    = new EditableWire();
        merged.Points.AddRange(mergedPts);

        var cmd = new WireMergeCommand(model,
            wireA, model.Wires.IndexOf(wireA), wireA.Points.ToList(),
            wireB, merged);

        cmd.Execute();
        Assert.Single(model.Wires);
        Assert.Same(merged, model.Wires[0]);

        cmd.Undo();
        Assert.Equal(2, model.Wires.Count);
        Assert.Contains(wireA, model.Wires);
        Assert.Contains(wireB, model.Wires);
        Assert.Equal(2, wireA.Points.Count);
        Assert.Equal(2, wireB.Points.Count);
    }
}
