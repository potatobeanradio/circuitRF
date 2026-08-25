// Owner request, 2026-08-25: "allow user to press 'R' to rotate port in the middle of a live drag
// (it currently rotates if not during a drag)", then "can this work for any object?"
//
// It can, and it is written that way. LayoutEditorViewModel.RotateSelection already handles shapes,
// instances and wires; nothing about it is port-specific. What swallowed the key was the guard on
// the whole selection-editing key block — `_selectDragKind == SelectDragKind.None` — so R mid-drag
// reached nothing at all. Measured before the fix: the port's direction was untouched.

using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Layout;

public class RotateDuringDragTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private static long Mm(double mm) => (long)Math.Round(mm * 1000 * Dbu);

    private static LayoutEditorViewModel Editor(LayoutView view)
    {
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.ApplyTechResolution(new TechResolution(
            StarterTechnologies.Pcb2Layer(), null, TechResolutionSource.WorkspaceDefault, []));
        return vm;
    }

    /// <summary>A 20 x 2.9 mm trace with a port on its low-x end, facing +x.</summary>
    private static LayoutView WithPort()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(20), Y2 = Mm(2.9) });
        view.Shapes.Add(new LabelShape
        {
            Layer = TopCopper, X = 0, Y = Mm(1.45), Text = "P1", Height = Mm(1),
            IsPort = true, PortDirection = LayoutRotation.R0,
        });
        return view;
    }

    // ── The report ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>R mid-drag rotates, and the live preview shows it.</b> The preview half is the part that
    /// makes the gesture usable — you are aiming the port while carrying it, so seeing the new
    /// direction before releasing is the whole point. It follows for free because
    /// <c>Overlay.DragOverrides</c> is rebuilt from <c>Model.Shapes</c> plus the live delta.
    /// </summary>
    [Fact]
    public void PressingR_MidDrag_RotatesThePort_AndThePreviewFollows()
    {
        var view = WithPort();
        var vm   = Editor(view);

        vm.OnPointerPressed(0, Mm(1.45), KeyModifiers.None, 1, 0);
        vm.OnPointerMoved(Mm(3), Mm(1.45), true, KeyModifiers.None, 0);
        Assert.NotEmpty(vm.Overlay.DragOverrides);

        vm.OnKeyDown(Key.R, KeyModifiers.None);

        // The MODEL rotated — RotateSelection replaces the shape, so this must be re-read from the
        // view rather than from a reference captured before the press.
        Assert.Equal(LayoutRotation.R90, ((LabelShape)view.Shapes[1]).PortDirection);

        // And the drag preview carries it, at the dragged position.
        var preview = (LabelShape)vm.Overlay.DragOverrides[1];
        Assert.Equal(LayoutRotation.R90, preview.PortDirection);
        Assert.Equal(Mm(3), preview.X);
    }

    /// <summary>The drag still commits its move — rotating mid-gesture must not swallow the drop.</summary>
    [Fact]
    public void TheDragStillLandsAfterRotating()
    {
        var view = WithPort();
        var vm   = Editor(view);

        vm.OnPointerPressed(0, Mm(1.45), KeyModifiers.None, 1, 0);
        vm.OnPointerMoved(Mm(4), Mm(1.45), true, KeyModifiers.None, 0);
        vm.OnKeyDown(Key.R, KeyModifiers.None);
        vm.OnPointerReleased(Mm(4), Mm(1.45), KeyModifiers.None);

        var port = (LabelShape)view.Shapes[1];
        Assert.Equal(Mm(4), port.X);
        Assert.Equal(LayoutRotation.R90, port.PortDirection);
    }

    /// <summary>Shift+R turns the other way, mid-drag as outside one.</summary>
    [Fact]
    public void ShiftR_MidDrag_TurnsTheOtherWay()
    {
        var view = WithPort();
        var vm   = Editor(view);

        vm.OnPointerPressed(0, Mm(1.45), KeyModifiers.None, 1, 0);
        vm.OnPointerMoved(Mm(3), Mm(1.45), true, KeyModifiers.None, 0);
        vm.OnKeyDown(Key.R, KeyModifiers.Shift);

        Assert.Equal(LayoutRotation.R270, ((LabelShape)view.Shapes[1]).PortDirection);
    }

    // ── "Can this work for any object?" ───────────────────────────────────────────────────────

    /// <summary>
    /// <b>Yes — it is the SELECTION that rotates, not a port.</b> Written as a general gesture rather
    /// than a port special case, so this drives an ordinary polygon: a rectangle mid-drag rotates
    /// about its own centre, which for a non-square one swaps its width and height.
    /// </summary>
    [Fact]
    public void ItRotatesAnyObject_NotOnlyPorts()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(8), Y2 = Mm(2) });
        var vm = Editor(view);

        vm.OnPointerPressed(Mm(4), Mm(1), KeyModifiers.None, 1, 0);
        vm.OnPointerMoved(Mm(6), Mm(1), true, KeyModifiers.None, 0);
        vm.OnKeyDown(Key.R, KeyModifiers.None);

        var bb = LayoutGeometry.BboxOf(view.Shapes[0]);
        Assert.Equal(Mm(2), bb.MaxX - bb.MinX);   // 8 x 2 became 2 x 8
        Assert.Equal(Mm(8), bb.MaxY - bb.MinY);
    }

    // ── Scope ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A HANDLE drag is out of scope, and deliberately: it reshapes ONE shape against a grabbed
    /// vertex or edge, and rotating the thing under that grip mid-gesture has no coherent meaning.
    /// The gesture this adds is "rotate what I am carrying".
    /// </summary>
    [Fact]
    public void AHandleDragIsNotRotated()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, SnapDbu = 0 };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = Mm(8), Y2 = Mm(2) });
        var vm = Editor(view);

        vm.OnPointerPressed(Mm(4), Mm(1), KeyModifiers.None, 1, 0);   // select it
        vm.OnPointerReleased(Mm(4), Mm(1), KeyModifiers.None);
        var before = LayoutGeometry.BboxOf(view.Shapes[0]);

        // Grab the low-x/low-y corner handle and drag it — a handle drag, not a move.
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, Mm(0.3));
        vm.OnPointerMoved(Mm(-1), Mm(-1), true, KeyModifiers.None, Mm(0.3));
        vm.OnKeyDown(Key.R, KeyModifiers.None);
        vm.OnPointerReleased(Mm(-1), Mm(-1), KeyModifiers.None);

        // Whatever the handle drag did, the shape is not a 2 x 8 — nothing rotated it.
        var after = LayoutGeometry.BboxOf(view.Shapes[0]);
        Assert.True(after.MaxX - after.MinX > after.MaxY - after.MinY,
                    "a handle drag must not have been rotated by R");
        Assert.NotEqual(before, after);   // and the handle drag itself still did something
    }

    /// <summary>Ctrl/Cmd+R stays the Run shortcut, mid-drag as anywhere else — the guard that keeps
    /// this from stealing it is one condition and is easy to drop.</summary>
    [Fact]
    public void CtrlR_IsNotStolenMidDrag()
    {
        var view = WithPort();
        var vm   = Editor(view);

        vm.OnPointerPressed(0, Mm(1.45), KeyModifiers.None, 1, 0);
        vm.OnPointerMoved(Mm(3), Mm(1.45), true, KeyModifiers.None, 0);
        vm.OnKeyDown(Key.R, KeyModifiers.Control);

        Assert.Equal(LayoutRotation.R0, ((LabelShape)view.Shapes[1]).PortDirection);
    }
}
