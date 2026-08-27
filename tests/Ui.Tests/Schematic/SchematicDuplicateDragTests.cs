using Avalonia.Input;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// R-dup-1 in the schematic editor: Alt during a drag copies instead of moving. The owner asked for
/// the same gesture in both editors, and for the same picture in both — the original rendered where
/// it is, a ghost for the copy.
///
/// <para>The schematic gets there differently from the layout, and that difference is what these
/// tests are mostly guarding. A schematic drag mutates the model LIVE (the overlay carries the
/// already-moved positions), so a duplicate has to actively put the originals back every tick and
/// draw the copy through a channel that adds geometry rather than relocating it. Both halves are
/// asserted separately below, because either one alone looks right in a screenshot: originals put
/// back with no ghost is "the drag stopped working", and a ghost with the originals still moving is
/// three objects for two.</para>
/// </summary>
public class SchematicDuplicateDragTests
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

    /// <summary>
    /// Two parts joined by an L wire, plus a LONE part well clear of everything.
    ///
    /// <para>The lone part is not decoration: R1 sits on the wire's own endpoint, so a press there
    /// selects the WIRE and the drag moves that instead — which is a hit-test fact about the fixture,
    /// not about duplication, and it silently turns every assertion below into a test of nothing.
    /// Component drags are exercised on R3, wire drags on the wire's vertical run.</para>
    /// </summary>
    private static (SchematicEditModel Model, SchematicViewModel Vm) MakeModel()
    {
        var model = new SchematicEditModel { GridSize = 100, GridSnap = true };
        model.Components.Add(Comp("R1", 0, 0));
        model.Components.Add(Comp("R2", 600, 400));
        model.Components.Add(Comp("R3", 2000, 2000));
        model.Wires.Add(Wire((0, 0), (0, 400), (600, 400)));
        // A FREE wire, touching nothing. The L wire above has both endpoints on unselected
        // components, so dragging it pins and re-routes rather than translating — correct for a move,
        // and a poor instrument for asking where a copy of it would land.
        model.Wires.Add(Wire((2000, 3000), (2600, 3000)));
        var vm = new SchematicViewModel(model);
        return (model, vm);
    }

    private static EditableComponent Lone(SchematicEditModel model) => model.Components[2];

    /// <summary>A press on the lone part, then a drag out by (dx, dy) in a few ticks — a real pointer
    /// emits many, and the 5-unit drag threshold means one jump is not always enough.</summary>
    private static void DragLone(SchematicViewModel vm, double dx, double dy, KeyModifiers mods)
    {
        vm.OnPointerPressed(2000, 2000, KeyModifiers.None);
        for (int i = 1; i <= 3; i++)
            vm.OnPointerMoved(2000 + dx * i / 3.0, 2000 + dy * i / 3.0, leftDown: true, modifiers: mods);
        vm.OnPointerReleased(2000 + dx, 2000 + dy, mods);
    }

    // ── The gesture ───────────────────────────────────────────────────────────

    [Fact]
    public void AltDrag_LeavesTheOriginalAndAddsACopyAtTheDelta()
    {
        var (model, vm) = MakeModel();
        vm.Selection.SetAll([Lone(model).Id]);

        DragLone(vm, 300, 200, KeyModifiers.Alt);

        Assert.Equal(4, model.Components.Count);
        Assert.Equal(2000.0, Lone(model).X, Eps);            // the original never moved
        Assert.Equal(2000.0, Lone(model).Y, Eps);
        var copy = model.Components[3];
        Assert.Equal(2300.0, copy.X, Eps);
        Assert.Equal(2200.0, copy.Y, Eps);
    }

    /// <summary>The non-vacuity partner: the identical drag without Alt moves the original and adds
    /// nothing. Without this, the test above could pass on a drag that silently did nothing at
    /// all.</summary>
    [Fact]
    public void ThatSameDragWithoutAlt_MovesTheOriginalAndCopiesNothing()
    {
        var (model, vm) = MakeModel();
        vm.Selection.SetAll([Lone(model).Id]);

        DragLone(vm, 300, 200, KeyModifiers.None);

        Assert.Equal(3, model.Components.Count);
        Assert.Equal(2300.0, Lone(model).X, Eps);
        Assert.Equal(2200.0, Lone(model).Y, Eps);
    }

    [Fact]
    public void AltDrag_CopiesWiresAndComponentsTogether()
    {
        var (model, vm) = MakeModel();
        vm.Selection.SetAll(model.Components.Take(2).Select(c => c.Id).Append(model.Wires[0].Id));

        vm.OnPointerPressed(0, 300, KeyModifiers.None);          // on the wire's vertical run
        for (int i = 1; i <= 3; i++)
            vm.OnPointerMoved(0, 300 + i * 100, leftDown: true, modifiers: KeyModifiers.Alt);
        vm.OnPointerReleased(0, 600, KeyModifiers.Alt);

        Assert.Equal(5, model.Components.Count);                 // R1+R2 copied, R3 untouched
        Assert.Equal(3, model.Wires.Count);                      // the L wire copied; the free one not
        Assert.Equal(0.0, model.Components[0].Y, Eps);           // originals all still in place
        Assert.Equal((0.0, 0.0), model.Wires[0].Points[0]);
        Assert.Equal((0.0, 300.0), model.Wires[2].Points[0]);    // the copied wire, offset
    }

    // ── The picture: original in place, ghost for the copy ────────────────────

    [Fact]
    public void MidAltDrag_TheOriginalStaysPut_AndTheCopyIsAGhost()
    {
        var (model, vm) = MakeModel();
        vm.Selection.SetAll([Lone(model).Id]);

        vm.OnPointerPressed(2000, 2000, KeyModifiers.None);
        for (int i = 1; i <= 3; i++)
            vm.OnPointerMoved(2000 + i * 100, 2000, leftDown: true, modifiers: KeyModifiers.Alt);

        Assert.True(vm.DuplicateDragArmed);
        Assert.Equal(2000.0, Lone(model).X, Eps);                // the model was NOT moved
        var ghost = Assert.Single(vm.Overlay.DuplicateGhosts!);
        Assert.Equal(2300.0, ghost.X, Eps);
        Assert.Equal(SymbolKind.Resistor, ghost.Symbol);

        vm.OnPointerReleased(2300, 2000, KeyModifiers.Alt);
    }

    [Fact]
    public void MidPlainDrag_ThereIsNoGhost_AndTheOriginalHasMoved()
    {
        var (model, vm) = MakeModel();
        vm.Selection.SetAll([Lone(model).Id]);

        vm.OnPointerPressed(2000, 2000, KeyModifiers.None);
        for (int i = 1; i <= 3; i++)
            vm.OnPointerMoved(2000 + i * 100, 2000, leftDown: true, modifiers: KeyModifiers.None);

        Assert.False(vm.DuplicateDragArmed);
        Assert.Null(vm.Overlay.DuplicateGhosts);
        Assert.Equal(2300.0, Lone(model).X, Eps);

        vm.OnPointerReleased(2300, 2000, KeyModifiers.None);
    }

    /// <summary>
    /// The wire half of the ghost. The wire is selected TOGETHER with another object on purpose: a
    /// press on a lone selected wire collapses to a SEGMENT drag, which reshapes one segment rather
    /// than moving a selection, and duplication is deliberately not part of that gesture — there is no
    /// selection there to copy.
    /// </summary>
    [Fact]
    public void MidAltDrag_AWireCopyIsGhostedToo()
    {
        var (model, vm) = MakeModel();
        vm.Selection.SetAll([model.Wires[1].Id, Lone(model).Id]);

        vm.OnPointerPressed(2300, 3000, KeyModifiers.None);      // on the free wire's body
        for (int i = 1; i <= 3; i++)
            vm.OnPointerMoved(2300, 3000 + i * 100, leftDown: true, modifiers: KeyModifiers.Alt);

        var ghostWire = Assert.Single(vm.Overlay.DuplicateGhostWires!);
        Assert.Equal((2000.0, 3300.0), ghostWire[0]);
        Assert.Equal((2000.0, 3000.0), model.Wires[1].Points[0]);  // the original wire is untouched

        vm.OnPointerReleased(2300, 3300, KeyModifiers.Alt);
    }

    /// <summary>
    /// Pressing and releasing Alt mid-drag switches the gesture both ways — and, in this editor,
    /// switching BACK has to re-apply the move to originals that were just restored. That round trip
    /// is the one a live-mutating drag can plausibly get wrong.
    /// </summary>
    [Fact]
    public void AltPressedThenReleasedMidDrag_EndsAsAnOrdinaryMove()
    {
        var (model, vm) = MakeModel();
        vm.Selection.SetAll([Lone(model).Id]);

        vm.OnPointerPressed(2000, 2000, KeyModifiers.None);
        vm.OnPointerMoved(2100, 2000, leftDown: true, modifiers: KeyModifiers.None);
        vm.OnPointerMoved(2200, 2000, leftDown: true, modifiers: KeyModifiers.Alt);
        Assert.True(vm.DuplicateDragArmed);
        Assert.Equal(2000.0, Lone(model).X, Eps);

        vm.OnPointerMoved(2300, 2000, leftDown: true, modifiers: KeyModifiers.None);
        Assert.False(vm.DuplicateDragArmed);
        vm.OnPointerReleased(2300, 2000, KeyModifiers.None);

        Assert.Equal(3, model.Components.Count);
        Assert.Equal(2300.0, Lone(model).X, Eps);
    }

    [Fact]
    public void AltAndShiftTogether_CopyOnOneAxis()
    {
        var (model, vm) = MakeModel();
        vm.Selection.SetAll([Lone(model).Id]);

        DragLone(vm, 500, 200, KeyModifiers.Alt | KeyModifiers.Shift);

        var copy = model.Components[3];
        Assert.Equal(2500.0, copy.X, Eps);
        Assert.Equal(2000.0, copy.Y, Eps);                       // Shift locked the Y axis
    }

    // ── Commit properties ─────────────────────────────────────────────────────

    [Fact]
    public void AnAltDrag_IsOneUndoEntry_AndUndoLeavesTheOriginalAlone()
    {
        var (model, vm) = MakeModel();
        vm.Selection.SetAll([Lone(model).Id]);

        DragLone(vm, 300, 0, KeyModifiers.Alt);
        Assert.Equal(4, model.Components.Count);

        vm.UndoRedo.Undo();

        Assert.Equal(3, model.Components.Count);
        Assert.Equal(2000.0, Lone(model).X, Eps);
    }

    /// <summary>The copies become the selection, matching paste — the next action operates on what
    /// was just placed, not on what it came from.</summary>
    [Fact]
    public void AfterAnAltDrag_TheCopyIsWhatIsSelected()
    {
        var (model, vm) = MakeModel();
        string originalId = Lone(model).Id;
        vm.Selection.SetAll([originalId]);

        DragLone(vm, 300, 0, KeyModifiers.Alt);

        Assert.Equal(1, vm.Selection.Count);
        Assert.DoesNotContain(originalId, vm.Selection.Ids);
    }

    [Fact]
    public void AnAltPressThatNeverMoves_CopiesNothing()
    {
        var (model, vm) = MakeModel();
        vm.Selection.SetAll([Lone(model).Id]);

        vm.OnPointerPressed(2000, 2000, KeyModifiers.Alt);
        vm.OnPointerReleased(2000, 2000, KeyModifiers.Alt);

        Assert.Equal(3, model.Components.Count);
    }

    /// <summary>Drives the copy CURSOR, which has to answer before the press.</summary>
    [Fact]
    public void HasDuplicableSelection_TracksWhetherThereIsAnythingToCopy()
    {
        var (model, vm) = MakeModel();
        Assert.False(vm.HasDuplicableSelection);

        vm.Selection.SetAll([Lone(model).Id]);
        Assert.True(vm.HasDuplicableSelection);
    }
}
