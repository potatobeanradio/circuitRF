using Avalonia.Input;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Bug report 2026-08-19 (copy/paste in the schematic editor):
///
/// 1. Paste dropped the fragment at its SOURCE coordinates. Copy-all + paste inside one schematic
///    therefore put every pasted object exactly on top of its original — and in that state the
///    pasted selection could not be dragged anywhere useful.
/// 2. The reason it could not be dragged: pressing on a wire that was part of the (multi-object)
///    pasted selection collapsed the selection to a SINGLE WIRE SEGMENT, and a segment drag by
///    construction moves only perpendicular to that segment. The user's "stuck, and it only moves
///    horizontally" is exactly a vertical segment's drag axis.
///
/// Paste now lands relative to the current view, and a press on an already-selected wire moves the
/// whole selection.
/// </summary>
public class SchematicPasteAndDragTests
{
    private const double Eps = 1e-9;

    private static EditableComponent Comp(string name, double x, double y) =>
        new() { InstanceName = name, Symbol = SymbolKind.Resistor, X = x, Y = y };

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    /// <summary>A schematic with two parts joined by an L wire, plus its VM.</summary>
    private static (SchematicEditModel Model, SchematicViewModel Vm) MakeModel()
    {
        var model = new SchematicEditModel { GridSize = 100, GridSnap = true };
        model.Components.Add(Comp("R1", 0, 0));
        model.Components.Add(Comp("R2", 600, 400));
        model.Wires.Add(Wire((0, 0), (0, 400), (600, 400)));
        var vm = new SchematicViewModel(model);
        return (model, vm);
    }

    /// <summary>Round-trips the whole model through the clipboard serializer and pastes it back.</summary>
    private static void CopyAllAndPaste(SchematicEditModel model, SchematicViewModel vm)
    {
        string json = SchematicPersistence.SerializeSelection(
            model.Components.ToList(), model.Wires.ToList(), model.CanvasObjects.ToList(), model.GridSize);
        var (comps, wires, cobjs, srcGrid) = SchematicPersistence.DeserializeSelection(json);
        vm.PasteFragment(comps, wires, cobjs, srcGrid);
    }

    // ── Bug 2: dragging a multi-object selection by one of its wires ──────────

    [Fact]
    public void PressOnWireInMultiSelection_MovesTheWholeSelection()
    {
        var (model, vm) = MakeModel();
        vm.Selection.SetAll(model.Components.Select(c => c.Id).Concat(model.Wires.Select(w => w.Id)));

        // Press on the vertical run of the wire, clear of either symbol glyph — this is the segment
        // whose own drag axis is horizontal, so a collapse to segment-drag shows up as "only moves
        // horizontally".
        vm.OnPointerPressed(0, 300, KeyModifiers.None);

        Assert.Equal(3, vm.Selection.Count);                       // selection survives the press
        Assert.Empty(vm.Selection.GetSelectedSegments(model));     // …and is NOT a segment selection

        for (int i = 1; i <= 3; i++) vm.OnPointerMoved(i * 100, 300 + i * 100, leftDown: true);
        vm.OnPointerReleased(300, 600);

        // Everything moved together, including on the axis a segment drag would have forbidden.
        Assert.Equal(300.0, model.Components[0].X, Eps);
        Assert.Equal(300.0, model.Components[0].Y, Eps);
        Assert.Equal(900.0, model.Components[1].X, Eps);
        Assert.Equal(700.0, model.Components[1].Y, Eps);
        Assert.Equal((300.0, 300.0), model.Wires[0].Points[0]);
        Assert.Equal((900.0, 700.0), model.Wires[0].Points[2]);
    }

    [Fact]
    public void PressOnWire_WhenItIsTheOnlySelection_StillSelectsTheSegment()
    {
        // B1 per-segment editing is unchanged: it owns the click whenever the press is not on a
        // multi-object selection.
        var (model, vm) = MakeModel();
        vm.Selection.SelectOne(model.Wires[0].Id);

        vm.OnPointerPressed(0, 300, KeyModifiers.None);

        var segs = vm.Selection.GetSelectedSegments(model);
        Assert.Single(segs);
        Assert.Equal(0, segs[0].SegmentIndex);
    }

    [Fact]
    public void PressOnWire_WithNothingSelected_StillSelectsTheSegment()
    {
        var (model, vm) = MakeModel();

        vm.OnPointerPressed(0, 300, KeyModifiers.None);

        Assert.Single(vm.Selection.GetSelectedSegments(model));
    }

    [Fact]
    public void ShiftPressOnWireInMultiSelection_StillTogglesTheSegment()
    {
        // Shift is explicit user intent to act on the topmost object, never a move of the selection.
        var (model, vm) = MakeModel();
        vm.Selection.SetAll(model.Components.Select(c => c.Id).Concat(model.Wires.Select(w => w.Id)));

        vm.OnPointerPressed(0, 300, KeyModifiers.Shift);

        Assert.Single(vm.Selection.GetSelectedSegments(model));
    }

    // ── Bug 1: where a paste lands ────────────────────────────────────────────

    [Fact]
    public void Paste_WithSourceOffScreen_CentresTheFragmentInTheView()
    {
        var (model, vm) = MakeModel();
        vm.ViewportProvider = () => new SchematicPasteGeometry.ViewRect(5000, 3000, 8000, 5000);

        CopyAllAndPaste(model, vm);

        Assert.Equal(4, model.Components.Count);
        var pasted = model.Components.Skip(2).ToList();
        // Fragment bbox centre (300, 200) → viewport centre (6500, 4000): delta (6200, 3800).
        Assert.Equal(6200.0, pasted[0].X, Eps);
        Assert.Equal(3800.0, pasted[0].Y, Eps);
        Assert.Equal(6800.0, pasted[1].X, Eps);
        Assert.Equal(4200.0, pasted[1].Y, Eps);
        Assert.Equal((6200.0, 3800.0), model.Wires[1].Points[0]);
    }

    [Fact]
    public void Paste_WithSourceFullyInView_OffsetsByOneGridStep()
    {
        var (model, vm) = MakeModel();
        vm.ViewportProvider = () => new SchematicPasteGeometry.ViewRect(-2000, -2000, 3000, 3000);

        CopyAllAndPaste(model, vm);

        var pasted = model.Components.Skip(2).ToList();
        Assert.Equal(100.0, pasted[0].X, Eps);
        Assert.Equal(100.0, pasted[0].Y, Eps);
        Assert.Equal(700.0, pasted[1].X, Eps);
        Assert.Equal(500.0, pasted[1].Y, Eps);
    }

    [Fact]
    public void Paste_NeverLandsAComponentExactlyOnAnExistingOne()
    {
        // The pathological state the bug report hit: with no viewport at all (headless, or a canvas
        // that has never been laid out) paste is in-place, so the anti-overlap pass is the only
        // thing standing between the user and an undraggable stack of coincident objects.
        var (model, vm) = MakeModel();
        vm.ViewportProvider = null;

        CopyAllAndPaste(model, vm);

        var originals = model.Components.Take(2).Select(c => (c.X, c.Y)).ToHashSet();
        foreach (var c in model.Components.Skip(2))
            Assert.DoesNotContain((c.X, c.Y), originals);
    }

    [Fact]
    public void Paste_IntoAnEmptySchematic_KeepsSourceCoordinates()
    {
        // Nothing to collide with and no view — the fragment keeps its own coordinates, which is
        // what a cross-schematic paste of a laid-out block wants.
        var (src, _) = MakeModel();
        var dst   = new SchematicEditModel { GridSize = 100, GridSnap = true };
        var dstVm = new SchematicViewModel(dst);

        string json = SchematicPersistence.SerializeSelection(
            src.Components.ToList(), src.Wires.ToList(), src.CanvasObjects.ToList(), src.GridSize);
        var (comps, wires, cobjs, srcGrid) = SchematicPersistence.DeserializeSelection(json);
        dstVm.PasteFragment(comps, wires, cobjs, srcGrid);

        Assert.Equal(0.0,   dst.Components[0].X, Eps);
        Assert.Equal(0.0,   dst.Components[0].Y, Eps);
        Assert.Equal(600.0, dst.Components[1].X, Eps);
    }

    [Fact]
    public void PastedObjectsStayOnTheConnectionGrid()
    {
        // The view offset must be a whole number of grid steps — a pasted pin off P would not
        // connect to anything (the R7 on-grid invariant).
        var (model, vm) = MakeModel();
        vm.ViewportProvider = () => new SchematicPasteGeometry.ViewRect(5137, 2911, 8213, 4877);

        CopyAllAndPaste(model, vm);

        foreach (var c in model.Components)
        {
            Assert.Equal(0.0, c.X % model.GridSize, 6);
            Assert.Equal(0.0, c.Y % model.GridSize, 6);
        }
        foreach (var w in model.Wires)
            foreach (var (x, y) in w.Points)
            {
                Assert.Equal(0.0, x % model.GridSize, 6);
                Assert.Equal(0.0, y % model.GridSize, 6);
            }
    }

    [Fact]
    public void Paste_IsOneUndoableAction_AtItsPlacedPosition()
    {
        var (model, vm) = MakeModel();
        vm.ViewportProvider = () => new SchematicPasteGeometry.ViewRect(5000, 3000, 8000, 5000);

        CopyAllAndPaste(model, vm);
        Assert.Equal(4, model.Components.Count);

        vm.UndoRedo.Undo();
        Assert.Equal(2, model.Components.Count);

        vm.UndoRedo.Redo();
        Assert.Equal(4, model.Components.Count);
        Assert.Equal(6200.0, model.Components[2].X, Eps);   // redo restores the PLACED position
    }

    [Fact]
    public void PastedSelection_IsWhatGetsSelected()
    {
        var (model, vm) = MakeModel();
        vm.ViewportProvider = () => new SchematicPasteGeometry.ViewRect(5000, 3000, 8000, 5000);

        CopyAllAndPaste(model, vm);

        Assert.Equal(3, vm.Selection.Count);
        foreach (var c in model.Components.Skip(2)) Assert.True(vm.Selection.IsSelected(c.Id));
        Assert.False(vm.Selection.IsSelected(model.Components[0].Id));
    }

    // ── The two bugs together: the reported end-to-end workflow ──────────────

    [Fact]
    public void CopyAllPasteThenDragByAWire_MovesThePastedCopy()
    {
        var (model, vm) = MakeModel();
        vm.ViewportProvider = () => new SchematicPasteGeometry.ViewRect(-2000, -2000, 3000, 3000);
        vm.Selection.SetAll(model.Components.Select(c => c.Id).Concat(model.Wires.Select(w => w.Id)));

        CopyAllAndPaste(model, vm);

        // Grab the pasted copy by its wire (offset one grid step from the original's).
        var pastedWire = model.Wires[1];
        Assert.Equal((100.0, 100.0), pastedWire.Points[0]);
        vm.OnPointerPressed(100, 400, KeyModifiers.None);
        Assert.Equal(3, vm.Selection.Count);

        for (int i = 1; i <= 3; i++) vm.OnPointerMoved(100 + i * 200, 400 + i * 200, leftDown: true);
        vm.OnPointerReleased(700, 1000);

        // The pasted copy moved by (600, 600); the originals did not move at all.
        Assert.Equal(0.0,   model.Components[0].X, Eps);
        Assert.Equal(0.0,   model.Components[0].Y, Eps);
        Assert.Equal(700.0, model.Components[2].X, Eps);
        Assert.Equal(700.0, model.Components[2].Y, Eps);
        Assert.Equal((700.0, 700.0), model.Wires[1].Points[0]);
    }

    // ── Reported alongside: "Save Schematic As…" suggested ".csch" twice ──────

    [Fact]
    public void SaveAsPickerName_DropsTheExtensionTheDialogAppendsItself()
    {
        // doc.Id is the tab identity and carries the file name WITH extension for anything opened
        // from disk; the picker appends DefaultExtension ("csch") on its own, which is how the
        // suggested name came out as "SParamTest.csch.csch".
        var vm  = new SchematicViewModel(new SchematicEditModel());
        var doc = new SchematicDocument("SParamTest.csch", vm, "/tmp/SParamTest.csch");
        Assert.Equal("SParamTest", WorkspaceViewModel.SchematicPickerName(doc));

        // A scratch document's Id is a plain title and must pass through untouched.
        var scratch = new SchematicDocument("Untitled 1", vm);
        Assert.Equal("Untitled 1", WorkspaceViewModel.SchematicPickerName(scratch));

        // Case-insensitive, and a dotted stem survives.
        var upper = new SchematicDocument("Amp.v2.CSCH", vm, "/tmp/Amp.v2.CSCH");
        Assert.Equal("Amp.v2", WorkspaceViewModel.SchematicPickerName(upper));
    }

    [Fact]
    public void SaveAsPath_GetsAnExtensionIfThePickerDidNotAddOne()
    {
        Assert.Equal("/tmp/amp.csch", WorkspaceViewModel.EnsureCschExtension("/tmp/amp"));
        Assert.Equal("/tmp/amp.csch", WorkspaceViewModel.EnsureCschExtension("/tmp/amp.csch"));
        Assert.Equal("/tmp/amp.txt",  WorkspaceViewModel.EnsureCschExtension("/tmp/amp.txt"));
    }
}
