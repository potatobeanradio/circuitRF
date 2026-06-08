using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Tests for Bug 1 (wire split creates degenerate pieces) and Bug 2
/// (wire draw produces collinear interior vertices).
/// </summary>
public class WireSplitTests
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
        var model    = new SchematicEditModel { GridSnap = false };
        var undoRedo = new UndoRedoStack();
        var vm       = new SchematicViewModel(model, undoRedo);
        return (model, vm);
    }

    // ── Bug 1: wire split ─────────────────────────────────────────────────────

    [Fact]
    public void DeleteMiddleSegment_CleanWire_CreatesTwoValidWires()
    {
        // 4-point L-then-V wire: [A, B, C, D]
        // Segments: 0=A→B (H), 1=B→C (V), 2=C→D (H)
        // Delete segment 1 (B→C) — should produce [A, B] and [C, D]
        var model = new SchematicEditModel();
        var wire = MakeWire((0, 0), (100, 0), (100, 100), (200, 100));
        model.Wires.Add(wire);

        var cmd = new DeleteSegmentsCommand(model,
            [(wire.Id, 1)]);
        cmd.Execute();

        Assert.Equal(2, model.Wires.Count);

        var a = model.Wires[0];
        var b = model.Wires[1];
        Assert.Equal(2, a.Points.Count);
        Assert.Equal(2, b.Points.Count);

        // Piece A ends at the cut vertex
        Assert.Equal((0.0, 0.0),   a.Points[0]);
        Assert.Equal((100.0, 0.0), a.Points[1]);

        // Piece B starts after the cut — a gap exists (not identical endpoints)
        Assert.Equal((100.0, 100.0), b.Points[0]);
        Assert.Equal((200.0, 100.0), b.Points[1]);
    }

    [Fact]
    public void DeleteMiddleSegment_CleanWire_Undo_RestoresOriginal()
    {
        var model = new SchematicEditModel();
        var wire = MakeWire((0, 0), (100, 0), (100, 100), (200, 100));
        model.Wires.Add(wire);

        var cmd = new DeleteSegmentsCommand(model, [(wire.Id, 1)]);
        cmd.Execute();
        cmd.Undo();

        Assert.Single(model.Wires);
        Assert.Same(wire, model.Wires[0]);
        Assert.Equal(4, model.Wires[0].Points.Count);
    }

    [Fact]
    public void DeleteMiddleSegment_CollinearWire_NormalizesEachPiece()
    {
        // Wire with collinear interior point from Bug 2: [A=(0,0), B=(100,0), C=(200,0), D=(200,100)]
        // Segment 0=A→B (H), 1=B→C (H, collinear!), 2=C→D (V)
        // Delete segment 2 (C→D) → piece A = [A, B, C] which is collinear → normalized to [A, C]
        var model = new SchematicEditModel();
        var wire  = MakeWire((0, 0), (100, 0), (200, 0), (200, 100));
        model.Wires.Add(wire);

        var cmd = new DeleteSegmentsCommand(model, [(wire.Id, 2)]);
        cmd.Execute();

        // Piece B = [(200,100)] — 1 point, discarded.
        // Piece A = [(0,0), (100,0), (200,0)] → normalized → [(0,0), (200,0)]
        Assert.Single(model.Wires);
        Assert.Equal(2, model.Wires[0].Points.Count);
        Assert.Equal((0.0,   0.0), model.Wires[0].Points[0]);
        Assert.Equal((200.0, 0.0), model.Wires[0].Points[1]);
    }

    [Fact]
    public void DeleteMiddleSegment_ZeroLengthSegment_ProducesNoZeroLengthPiece()
    {
        // Wire with a zero-length interior segment (both pts identical): [A, A, B, C]
        // Deleting segment 0 (A→A, zero-length):
        //   piece A = [A] — 1 point, discarded
        //   piece B = [A, B, C] — normalized to [A, B, C] if not collinear
        var model = new SchematicEditModel();
        var wire  = MakeWire((0, 0), (0, 0), (100, 0), (100, 100));
        model.Wires.Add(wire);

        var cmd = new DeleteSegmentsCommand(model, [(wire.Id, 0)]);
        cmd.Execute();

        // Piece B = [(0,0), (100,0), (100,100)] — L-shape, no collinear interior → 3 pts
        Assert.Single(model.Wires);
        var piece = model.Wires[0];
        Assert.True(piece.Points.Count >= 2, "replacement must have ≥ 2 points");
        // No two consecutive points should be identical (zero-length segment free)
        for (int i = 0; i < piece.Points.Count - 1; i++)
        {
            var p0 = piece.Points[i];
            var p1 = piece.Points[i + 1];
            Assert.False(Math.Abs(p0.X - p1.X) < 1e-6 && Math.Abs(p0.Y - p1.Y) < 1e-6,
                $"zero-length segment at index {i}");
        }
    }

    [Fact]
    public void DeleteMiddleSegment_ZeroLengthBothEnds_ProducesNoZeroLengthWire()
    {
        // Wire where the two endpoints of the deleted segment are at the SAME position:
        // [A, B, B, C] — segment 1 is B→B (zero-length). Deleting it:
        //   piece A = [A, B] normalized → [A, B] (valid, 2 pts)
        //   piece B = [B, C] — starts at same B, but it's a separate wire
        var model = new SchematicEditModel();
        var wire  = MakeWire((0, 0), (100, 0), (100, 0), (100, 100));
        model.Wires.Add(wire);

        var cmd = new DeleteSegmentsCommand(model, [(wire.Id, 1)]);
        cmd.Execute();

        // Both pieces should be valid and non-degenerate
        foreach (var w in model.Wires)
        {
            Assert.True(w.Points.Count >= 2, "each piece must have ≥ 2 points");
            var p0 = w.Points[0];
            var pN = w.Points[^1];
            Assert.False(Math.Abs(p0.X - pN.X) < 1e-6 && Math.Abs(p0.Y - pN.Y) < 1e-6
                         && w.Points.Count == 2,
                "2-point wire with identical endpoints — zero-length, invisible");
        }
    }

    // ── Bug 2: collinear wire draw ────────────────────────────────────────────

    [Fact]
    public void WireDraw_CollinearHorizontalClicks_ProducesOneSegment()
    {
        // Three clicks on the same horizontal line should produce a 2-point wire,
        // not a 3-point wire with a redundant middle vertex.
        var (model, vm) = MakeVm();
        vm.SetWireTool();

        // Click 1: start at (0, 0)
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        // Click 2: extend horizontally to (100, 0)
        vm.OnPointerPressed(100, 0, KeyModifiers.None);
        // Click 3: continue on same horizontal to (200, 0) — collinear
        vm.OnPointerPressed(200, 0, KeyModifiers.None);
        // Finish wire
        vm.FinishCurrentWire();

        Assert.Single(model.Wires);
        var wire = model.Wires[0];
        Assert.Equal(2, wire.Points.Count);
        Assert.Equal((0.0,   0.0), wire.Points[0]);
        Assert.Equal((200.0, 0.0), wire.Points[1]);
    }

    [Fact]
    public void WireDraw_CollinearVerticalClicks_ProducesOneSegment()
    {
        var (model, vm) = MakeVm();
        vm.SetWireTool();

        vm.OnPointerPressed(0, 0,   KeyModifiers.None);
        vm.OnPointerPressed(0, 100, KeyModifiers.None);
        vm.OnPointerPressed(0, 200, KeyModifiers.None);
        vm.FinishCurrentWire();

        Assert.Single(model.Wires);
        Assert.Equal(2, model.Wires[0].Points.Count);
        Assert.Equal((0.0, 0.0),   model.Wires[0].Points[0]);
        Assert.Equal((0.0, 200.0), model.Wires[0].Points[1]);
    }

    [Fact]
    public void WireDraw_LShapeThenCollinearContinuation_TwoSegmentsNotThree()
    {
        // Draw an L-shape then continue the second segment — should remain 3 points, not 4.
        // (0,0) → (100,0) → (100,100) → (100,200): last two segments are both vertical.
        var (model, vm) = MakeVm();
        vm.SetWireTool();

        vm.OnPointerPressed(0,   0,   KeyModifiers.None);
        vm.OnPointerPressed(100, 0,   KeyModifiers.None);
        vm.OnPointerPressed(100, 100, KeyModifiers.None);
        vm.OnPointerPressed(100, 200, KeyModifiers.None);
        vm.FinishCurrentWire();

        Assert.Single(model.Wires);
        var wire = model.Wires[0];
        // L-shape: (0,0)→(100,0)→(100,200) — 3 points, not 4
        Assert.Equal(3, wire.Points.Count);
        Assert.Equal((0.0, 0.0),   wire.Points[0]);
        Assert.Equal((100.0, 0.0), wire.Points[1]);
        Assert.Equal((100.0, 200.0), wire.Points[2]);
    }
}
