using Avalonia.Input;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// The two drag modifiers the owner asked for (2026-08-27), plus the snap-marker report that came out
/// of testing the first one:
///
/// <list type="bullet">
/// <item><b>R-dup-3, Shift</b> — constrains a Move drag to one axis, measured from where the drag
/// started, exactly as the schematic editor's own <c>ApplyDragAxisLock</c> already did.</item>
/// <item><b>R-dup-1, Alt</b> — turns a Move drag into a DUPLICATE drag: the selection stays visibly
/// where it is and a ghost of the copy follows the cursor.</item>
/// <item><b>R-dup-2</b> — Alt no longer suspends snapping, because it now means something else. The
/// two persistent toggles (F9 for the grid, S/F3 for geometry) are the controls that survive; the
/// tests that pinned the old spelling were re-pointed at them rather than deleted.</item>
/// </list>
/// </summary>
public class LayoutDragGesturesTests
{
    private static LayoutView FreshModel(long snapDbu = 1000) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
    };

    private static LayoutEditorViewModel SelectVm(LayoutView model) =>
        new(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

    private static RectShape Rect(long x1, long y1, long x2, long y2) =>
        new() { Layer = new LayerKey(1, 0), X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    /// <summary>One selected 1 mm square at the origin, and the view model holding it.</summary>
    private static (LayoutEditorViewModel Vm, RectShape Rect) OneSelectedSquare(long snapDbu = 1000)
    {
        var model = FreshModel(snapDbu);
        var rect = Rect(0, 0, 1000, 1000);
        model.Shapes.Add(rect);
        var vm = SelectVm(model);
        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerReleased(500, 500, KeyModifiers.None);
        return (vm, rect);
    }

    private static void Drag(LayoutEditorViewModel vm, long fromX, long fromY, long toX, long toY,
                             KeyModifiers moveMods = KeyModifiers.None, long snapTolDbu = 0)
    {
        vm.OnPointerPressed(fromX, fromY, KeyModifiers.None, 1, 40, 0, snapTolDbu);
        vm.OnPointerMoved(toX, toY, leftDown: true, moveMods, 40, 0, snapTolDbu);
        vm.OnPointerReleased(toX, toY, moveMods);
    }

    // ── R-dup-3: Shift constrains to one axis ─────────────────────────────────

    [Fact]
    public void ShiftDrag_PredominantlyHorizontal_LocksTheYAxis()
    {
        var (vm, rect) = OneSelectedSquare();

        Drag(vm, 500, 500, 500 + 5000, 500 + 2000, KeyModifiers.Shift);

        Assert.Equal(5000, rect.X1);
        Assert.Equal(0, rect.Y1);
    }

    [Fact]
    public void ShiftDrag_PredominantlyVertical_LocksTheXAxis()
    {
        var (vm, rect) = OneSelectedSquare();

        Drag(vm, 500, 500, 500 + 2000, 500 + 5000, KeyModifiers.Shift);

        Assert.Equal(0, rect.X1);
        Assert.Equal(5000, rect.Y1);
    }

    /// <summary>
    /// The claim that makes this "ortho to the START", not "ortho to the last tick": a drag that goes
    /// out along X and then far up along Y ends up locked to Y, because the dominant axis is measured
    /// against the press point every tick, not accumulated.
    /// </summary>
    [Fact]
    public void ShiftDrag_IsMeasuredFromThePressPoint_NotFromTheLastTick()
    {
        var (vm, rect) = OneSelectedSquare();

        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(500 + 4000, 500 + 1000, leftDown: true, KeyModifiers.Shift, 40);
        vm.OnPointerMoved(500 + 4000, 500 + 9000, leftDown: true, KeyModifiers.Shift, 40);
        vm.OnPointerReleased(500 + 4000, 500 + 9000, KeyModifiers.Shift);

        Assert.Equal(0, rect.X1);
        Assert.Equal(9000, rect.Y1);
    }

    /// <summary>Released mid-drag, the constraint lifts — it is read live, never latched at press.</summary>
    [Fact]
    public void ReleasingShiftMidDrag_RestoresTheFreeMove()
    {
        var (vm, rect) = OneSelectedSquare();

        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(500 + 5000, 500 + 2000, leftDown: true, KeyModifiers.Shift, 40);
        vm.OnPointerMoved(500 + 5000, 500 + 2000, leftDown: true, KeyModifiers.None, 40);
        vm.OnPointerReleased(500 + 5000, 500 + 2000, KeyModifiers.None);

        Assert.Equal(5000, rect.X1);
        Assert.Equal(2000, rect.Y1);
    }

    /// <summary>Shift still means extend-selection at PRESS; only a press that is already dragging
    /// reads it as a constraint. Otherwise adding a second shape to a selection would start a
    /// constrained move instead.</summary>
    [Fact]
    public void ShiftPress_StillExtendsTheSelection()
    {
        var model = FreshModel();
        model.Shapes.Add(Rect(0, 0, 1000, 1000));
        model.Shapes.Add(Rect(5000, 0, 6000, 1000));
        var vm = SelectVm(model);

        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerReleased(500, 500, KeyModifiers.None);
        vm.OnPointerPressed(5500, 500, KeyModifiers.Shift, 1, 40);
        vm.OnPointerReleased(5500, 500, KeyModifiers.Shift);

        Assert.Equal(2, vm.SelectedIndices.Count);
    }

    // ── The owner's snap-marker report, 2026-08-27 ────────────────────────────

    /// <summary>
    /// "Holding Shift for ortho, the snap cursor is sometimes on even though there is no geometry
    /// nearby to snap to." The marker in question is the synthetic GRAB ECHO — shown precisely WHEN
    /// NO REAL FEATURE IS IN RANGE, only during a marker-grabbed drag (which is the "sometimes"), and
    /// under an ortho constraint drawn in the wrong place besides, since it tracks the unconstrained
    /// position and so floats off the axis the geometry is travelling on.
    ///
    /// <para>The drag here grabs a real corner and then moves well away from every feature, which is
    /// the exact state the echo exists to cover.</para>
    /// </summary>
    [Fact]
    public void ShiftDrag_WithNothingInRange_ShowsNoPhantomMarker()
    {
        var model = FreshModel();
        model.Shapes.Add(Rect(0, 0, 1000, 1000));
        var vm = SelectVm(model);
        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerReleased(500, 500, KeyModifiers.None);

        // Grabbed by its CENTROID marker, not a corner: a corner of a selected shape is also an L1d
        // resize handle, and that handle wins the press — the drag under test would never start.
        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40, 0, 500);
        vm.OnPointerMoved(50_000, 700, leftDown: true, KeyModifiers.None, 40, 0, 500);
        Assert.NotNull(vm.Overlay.SnapMarker);              // control: the echo IS showing unconstrained

        vm.OnPointerMoved(50_000, 700, leftDown: true, KeyModifiers.Shift, 40, 0, 500);
        Assert.Null(vm.Overlay.SnapMarker);

        vm.OnPointerReleased(50_000, 700, KeyModifiers.Shift);
    }

    /// <summary>
    /// The owner's correction to the first version of that fix, which stood the whole snap query down
    /// and threw away the useful half: <b>real geometry still attracts under Shift.</b> The grabbed
    /// point lands exactly on the target's coordinate along the free axis — which is what "align this
    /// with that, without leaving my axis" means — and does not budge on the locked one.
    ///
    /// <para>The target sits deliberately OFF-GRID (a 1 µm grid against a corner at 20,033) so landing
    /// on it exactly is only possible if geometry snap actually ran.</para>
    /// </summary>
    [Fact]
    public void ShiftDrag_StillSnapsToGeometry_OnTheFreeAxis()
    {
        var model = FreshModel(1000);
        var moving = Rect(0, 0, 1000, 1000);
        model.Shapes.Add(moving);
        model.Shapes.Add(Rect(20_033, 7000, 21_033, 8000));   // off-grid corner, well off the X axis
        var vm = SelectVm(model);
        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerReleased(500, 500, KeyModifiers.None);

        // Grabbed by its CENTROID (500,500) — a corner press would start an L1d resize instead — then
        // dragged predominantly along X to just short of the target corner.
        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40, 0, 500);
        vm.OnPointerMoved(20_100, 6900, leftDown: true, KeyModifiers.Shift, 40, 0, 500);
        vm.OnPointerReleased(20_100, 6900, KeyModifiers.Shift);

        // The GRABBED POINT lands on the target, so the shape's own corner sits one grab-offset back.
        Assert.Equal(20_033 - 500, moving.X1);               // free axis: attracted to the feature
        Assert.Equal(0, moving.Y1);                          // locked axis: did not move at all
        Assert.NotEqual(0, moving.X1 % 1000);                // non-vacuous: not a grid-snapped landing
    }

    /// <summary>
    /// The axis is the USER'S, chosen from the cursor's own travel — never from where the snap target
    /// happens to lie. The two only disagree on a near-diagonal drag, which is what this builds: the
    /// cursor is marginally more horizontal than vertical, and the target 70 DBU away from it is
    /// marginally more vertical than horizontal. Reading the axis off the target locks the wrong one.
    /// </summary>
    [Fact]
    public void ShiftDrag_ChoosesTheAxisFromTheCursor_NotFromTheTarget()
    {
        // A FINE grid on purpose: at a 1 µm step the snapped delta would round (1000, 950) to
        // (1000, 1000) and the near-diagonal this test is built on would vanish before it was read.
        var model = FreshModel(10);
        var moving = Rect(0, 0, 1000, 1000);
        model.Shapes.Add(moving);
        model.Shapes.Add(Rect(1450, 1500, 2450, 2500));       // its lower-left corner is the target
        var vm = SelectVm(model);
        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerReleased(500, 500, KeyModifiers.None);

        // A tight snap tolerance so the press grabs the CENTROID and nothing else: the edge midpoints
        // are 500 away, and a tie between them makes the anchor — and every number below — ambiguous.
        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40, 0, 100);
        vm.OnPointerMoved(1500, 1450, leftDown: true, KeyModifiers.Shift, 40, 0, 100);
        vm.OnPointerReleased(1500, 1450, KeyModifiers.Shift);

        // Cursor delta (1000, 950) → horizontal → Y locked. Target delta (950, 1000) → vertical, which
        // is the answer this test exists to reject.
        Assert.Equal(950, moving.X1);
        Assert.Equal(0, moving.Y1);
    }

    /// <summary>The scope fence: an ordinary unconstrained drag is unaffected by any of this.</summary>
    [Fact]
    public void WithoutShift_TheSameDragStillSnapsToTheFeature()
    {
        var model = FreshModel();
        model.Shapes.Add(Rect(0, 0, 1000, 1000));
        model.Shapes.Add(Rect(20_000, 20_000, 21_000, 21_000));
        var vm = SelectVm(model);
        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerReleased(500, 500, KeyModifiers.None);

        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40, 0, 500);
        vm.OnPointerMoved(19_900, 19_900, leftDown: true, KeyModifiers.None, 40, 0, 500);

        Assert.NotNull(vm.Overlay.SnapMarker);
        vm.OnPointerReleased(19_900, 19_900, KeyModifiers.None);
    }

    // ── R-dup-1: Alt duplicates ───────────────────────────────────────────────

    [Fact]
    public void AltDrag_LeavesTheOriginalAndCommitsACopyAtTheDelta()
    {
        var (vm, rect) = OneSelectedSquare();

        Drag(vm, 500, 500, 500 + 4000, 500 + 3000, KeyModifiers.Alt);

        Assert.Equal(2, vm.Model.Shapes.Count);
        Assert.Equal(0, rect.X1);
        Assert.Equal(0, rect.Y1);
        var copy = Assert.IsType<RectShape>(vm.Model.Shapes[1]);
        Assert.Equal(4000, copy.X1);
        Assert.Equal(3000, copy.Y1);
    }

    /// <summary>
    /// The requested visual, stated as the overlay contract it is built from: the original is NOT
    /// drawn somewhere else (no drag overrides), and the copy IS drawn as an uncommitted ghost. Those
    /// two facts together are "original in place, ghost for the duplicate".
    /// </summary>
    [Fact]
    public void MidAltDrag_TheOriginalHasNoDragOverride_AndTheCopyIsAGhost()
    {
        var (vm, _) = OneSelectedSquare();

        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(500 + 4000, 500, leftDown: true, KeyModifiers.Alt, 40);

        Assert.True(vm.DuplicateDragArmed);
        Assert.Empty(vm.Overlay.DragOverrides);
        var ghost = Assert.IsType<RectShape>(Assert.Single(vm.Overlay.PastePreview!));
        Assert.Equal(4000, ghost.X1);

        vm.OnPointerReleased(500 + 4000, 500, KeyModifiers.Alt);
    }

    /// <summary>The non-vacuity partner: without Alt the same drag DOES override the original's
    /// position and draws no ghost. If both tests ever agreed, neither would be measuring anything.</summary>
    [Fact]
    public void MidPlainDrag_TheOriginalIsOverridden_AndThereIsNoGhost()
    {
        var (vm, _) = OneSelectedSquare();

        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(500 + 4000, 500, leftDown: true, KeyModifiers.None, 40);

        Assert.False(vm.DuplicateDragArmed);
        Assert.Single(vm.Overlay.DragOverrides);
        Assert.Null(vm.Overlay.PastePreview);

        vm.OnPointerReleased(500 + 4000, 500, KeyModifiers.None);
    }

    /// <summary>Pressed and released mid-drag, the decision follows the key both ways — a user who
    /// changes their mind halfway must not have to restart the gesture.</summary>
    [Fact]
    public void AltPressedAndReleasedMidDrag_SwitchesTheGestureBothWays()
    {
        var (vm, rect) = OneSelectedSquare();

        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(500 + 4000, 500, leftDown: true, KeyModifiers.Alt, 40);
        Assert.True(vm.DuplicateDragArmed);

        vm.OnPointerMoved(500 + 4000, 500, leftDown: true, KeyModifiers.None, 40);
        Assert.False(vm.DuplicateDragArmed);
        vm.OnPointerReleased(500 + 4000, 500, KeyModifiers.None);

        Assert.Single(vm.Model.Shapes);      // it ended as an ordinary move
        Assert.Equal(4000, rect.X1);
    }

    [Fact]
    public void AltAndShiftTogether_CopyOnOneAxis()
    {
        var (vm, rect) = OneSelectedSquare();

        Drag(vm, 500, 500, 500 + 5000, 500 + 2000, KeyModifiers.Alt | KeyModifiers.Shift);

        Assert.Equal(0, rect.X1);
        var copy = Assert.IsType<RectShape>(vm.Model.Shapes[1]);
        Assert.Equal(5000, copy.X1);
        Assert.Equal(0, copy.Y1);
    }

    /// <summary>One undo entry for the whole gesture, and undoing it leaves the original alone — the
    /// duplicate never moved it, so there is nothing to put back.</summary>
    [Fact]
    public void AnAltDrag_IsOneUndoEntry()
    {
        var (vm, rect) = OneSelectedSquare();

        Drag(vm, 500, 500, 500 + 4000, 500, KeyModifiers.Alt);
        Assert.Equal(2, vm.Model.Shapes.Count);

        vm.UndoCommand.Execute(null);

        Assert.Single(vm.Model.Shapes);
        Assert.Equal(0, rect.X1);
    }

    /// <summary>An Alt drag that never moves commits nothing — no copy, and no empty undo entry to
    /// press Ctrl+Z through.</summary>
    [Fact]
    public void AnAltPressThatNeverMoves_CommitsNothing()
    {
        var (vm, _) = OneSelectedSquare();

        vm.OnPointerPressed(500, 500, KeyModifiers.Alt, 1, 40);
        vm.OnPointerReleased(500, 500, KeyModifiers.Alt);

        Assert.Single(vm.Model.Shapes);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    /// <summary>The copies become the selection, matching Duplicate/Paste — the next action operates
    /// on what was just placed, not on what it came from.</summary>
    [Fact]
    public void AfterAnAltDrag_TheCopyIsWhatIsSelected()
    {
        var (vm, _) = OneSelectedSquare();

        Drag(vm, 500, 500, 500 + 4000, 500, KeyModifiers.Alt);

        Assert.Equal([1], vm.SelectedIndices);
    }

    [Fact]
    public void EscapeDuringAnAltDrag_CopiesNothing()
    {
        var (vm, rect) = OneSelectedSquare();

        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(500 + 4000, 500, leftDown: true, KeyModifiers.Alt, 40);
        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.False(vm.DuplicateDragArmed);
        Assert.Single(vm.Model.Shapes);
        Assert.Equal(0, rect.X1);
    }

    /// <summary>Drives the duplicate CURSOR, which has to answer before the press — so it must be
    /// false with nothing selected rather than promising a gesture that would do nothing.</summary>
    [Fact]
    public void HasDuplicableSelection_TracksWhetherThereIsAnythingToCopy()
    {
        var (vm, _) = OneSelectedSquare();
        Assert.True(vm.HasDuplicableSelection);

        vm.DeselectAllCommand.Execute(null);
        Assert.False(vm.HasDuplicableSelection);
    }

    /// <summary>
    /// OWNER REPORT: the copy cursor appeared while editing geometry with the handle grippers. A
    /// selection is present throughout a handle drag, so "is anything selected" was the wrong
    /// question — the right one is whether an Alt press could actually duplicate, and during a
    /// reshape it cannot.
    /// </summary>
    [Fact]
    public void DuringAHandleDrag_ThereIsNothingToDuplicate()
    {
        var (vm, _) = OneSelectedSquare();

        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40);          // the rect's own corner handle
        vm.OnPointerMoved(300, 300, leftDown: true, KeyModifiers.None, 40);

        Assert.False(vm.HasDuplicableSelection);

        vm.OnPointerReleased(300, 300, KeyModifiers.None);
        Assert.True(vm.HasDuplicableSelection);                       // …and it comes back afterwards
    }

    /// <summary>The same answer one moment earlier: HOVERING a handle already means the next press is
    /// a reshape, so the cursor must not be offering a copy while the pointer sits there.</summary>
    [Fact]
    public void HoveringAHandle_AlsoLeavesNothingToDuplicate()
    {
        var (vm, _) = OneSelectedSquare();

        vm.OnPointerMoved(0, 0, leftDown: false, KeyModifiers.None, 40);
        Assert.False(vm.HasDuplicableSelection);

        vm.OnPointerMoved(500, 500, leftDown: false, KeyModifiers.None, 40);   // back over the body
        Assert.True(vm.HasDuplicableSelection);
    }

    /// <summary>A marquee has no copy to offer either — nothing is selected yet while the rectangle is
    /// still being dragged out.</summary>
    [Fact]
    public void DuringAMarquee_ThereIsNothingToDuplicate()
    {
        var (vm, _) = OneSelectedSquare();

        vm.OnPointerPressed(40_000, 40_000, KeyModifiers.None, 1, 40);   // empty space
        vm.OnPointerMoved(45_000, 45_000, leftDown: true, KeyModifiers.None, 40);

        Assert.False(vm.HasDuplicableSelection);

        vm.OnPointerReleased(45_000, 45_000, KeyModifiers.None);
    }
}
