// Owner report, 2026-09-05: move a port to another face of the square pad it sits on and you cannot
// rotate it — the only way out is to delete it and place it again.
//
// A port's DIRECTION names the conductor end it sits at, and it was stamped exactly once, by the Port
// tool, at placement. Everything the port then IS follows from that stamp: LayoutPortDirection.PlaneOf
// puts the reference-plane bar and the arrow at the face the direction names, and a port is PICKED and
// highlighted by that mark rather than by its anchor (LayoutEditorViewModel.PortMarkerRegion). So a
// drag to another face of a square pad moved the name and left the mark behind — a click where the port
// now appeared selected the PAD underneath it, and Rotate, which acts on the selection, turned the pad.
// Nothing was wrong with Rotate; the port was never selected.
//
// The move now re-asks the artwork the same question placement asks, and only when the artwork's own
// answer has changed — so an explicit rotation still survives a nudge along the face it already names.

using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Layout;

public class LayoutPortMoveReseatsDirectionTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    /// <summary>A 1 mm SQUARE pad — the reported shape, and the one where all four faces are equally
    /// plausible — with a port on its low-x face facing +x̂ into the metal.</summary>
    private static (LayoutEditorViewModel Vm, LayoutView View) Fixture(LayoutRotation dir = LayoutRotation.R0)
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 1 * Dbu };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 1000 * Dbu, Y2 = 1000 * Dbu });
        view.Shapes.Add(new LabelShape
        {
            Layer = TopCopper, X = 0, Y = 500 * Dbu, Text = "P1", Height = 100 * Dbu,
            IsPort = true, PortDirection = dir,
        });
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        return (vm, view);
    }

    private static LabelShape PortOf(LayoutView v) => v.Shapes.OfType<LabelShape>().First();

    /// <summary>One press-drag-release gesture, the way the report describes it. Snap tolerance is
    /// zero deliberately: a pad's own edge midpoint IS a geometry-snap feature, and a marker showing
    /// at the press point consumes the click for the shape that owns it, which would select the pad
    /// for reasons that have nothing to do with what is under test here.</summary>
    private static void Drag(LayoutEditorViewModel vm, long fromX, long fromY, long toX, long toY)
    {
        vm.OnPointerPressed(fromX, fromY, KeyModifiers.None, 1, 0, 0, 0);
        vm.OnPointerMoved(toX, toY, leftDown: true, KeyModifiers.None, 0, 0, 0);
        vm.OnPointerReleased(toX, toY, KeyModifiers.None);
    }

    private static void Click(LayoutEditorViewModel vm, long x, long y)
    {
        vm.OnPointerPressed(x, y, KeyModifiers.None, 1, 0, 0, 0);
        vm.OnPointerReleased(x, y, KeyModifiers.None);
    }

    // ── The report ───────────────────────────────────────────────────────────

    [Fact]
    public void PortDraggedToAnotherFaceOfAPad_NamesTheFaceItLandedOn()
    {
        var (vm, view) = Fixture();

        // Low-x face -> high-y face. Current flows out of the high-y end into the metal, i.e. -ŷ.
        Drag(vm, 0, 500 * Dbu, 500 * Dbu, 1000 * Dbu);

        var port = PortOf(view);
        Assert.Equal(500 * Dbu, port.X);
        Assert.Equal(1000 * Dbu, port.Y);
        Assert.Equal(LayoutRotation.R270, port.PortDirection);

        // …and the mark it draws — the bar, the arrow, the pick region and the reference plane the
        // EM extractor reads — moved with it, which is the half the user could actually see.
        var hint = Assert.NotNull(LayoutPortDirection.Resolve(view.Shapes, port));
        Assert.Equal(500 * Dbu, hint.PlaneX);
        Assert.Equal(1000 * Dbu, hint.PlaneY);
    }

    [Fact]
    public void AMovedPortIsStillPickable_WhereItNowIs_SoRotateReachesIt()
    {
        var (vm, view) = Fixture();
        Drag(vm, 0, 500 * Dbu, 500 * Dbu, 1000 * Dbu);

        // Click somewhere else first, so this is a fresh pick and not the drag's own leftover
        // selection — the reported gesture is "move it, then try to rotate it".
        Click(vm, 5000 * Dbu, 5000 * Dbu);
        Click(vm, 500 * Dbu, 1000 * Dbu);

        int portIndex = view.Shapes.IndexOf(PortOf(view));
        Assert.Equal([portIndex], vm.SelectedIndices);

        // And Rotate now advances the PORT rather than turning the pad underneath it.
        vm.RotateSelection();
        Assert.Equal(LayoutRotation.R0, PortOf(view).PortDirection);
        Assert.IsType<RectShape>(view.Shapes[0]);
        Assert.Equal(1000 * Dbu, ((RectShape)view.Shapes[0]).X2);
    }

    [Fact]
    public void Undo_PutsTheDirectionBackTogetherWithThePosition_OneEntry()
    {
        var (vm, view) = Fixture();
        Drag(vm, 0, 500 * Dbu, 500 * Dbu, 1000 * Dbu);
        Assert.Equal(LayoutRotation.R270, PortOf(view).PortDirection);

        vm.UndoCommand.Execute(null);

        var port = PortOf(view);
        Assert.Equal(0, port.X);
        Assert.Equal(500 * Dbu, port.Y);
        Assert.Equal(LayoutRotation.R0, port.PortDirection);
    }

    // ── …without eating a rotation the user asked for ────────────────────────

    [Fact]
    public void SlidingAlongTheFaceItAlreadyNames_LeavesAnExplicitRotationAlone()
    {
        // R180 on the low-x face is a direction the artwork would never infer — the user rotated it.
        var (vm, view) = Fixture(LayoutRotation.R180);

        // Grabbed by its MARK, which an R180 port draws at the high-x face — a port is picked by the
        // mark, not by its anchor. Along the same face: the nearest side to the ANCHOR is unchanged,
        // so the artwork's own answer is unchanged and there is nothing for the move to re-seat.
        Drag(vm, 1000 * Dbu, 500 * Dbu, 1000 * Dbu, 700 * Dbu);

        var port = PortOf(view);
        Assert.Equal(700 * Dbu, port.Y);
        Assert.Equal(LayoutRotation.R180, port.PortDirection);
    }

    [Fact]
    public void APortDraggedOffTheMetalKeepsItsDirection_ThereIsNoFaceToAdopt()
    {
        var (vm, view) = Fixture();

        Drag(vm, 0, 500 * Dbu, -3000 * Dbu, 500 * Dbu);

        Assert.Equal(LayoutRotation.R0, PortOf(view).PortDirection);
    }

    [Fact]
    public void MovingThePadAndItsPortTogether_ChangesNothingAboutTheDirection()
    {
        var (vm, view) = Fixture();

        vm.SelectAllCommand.Execute(null);
        // Press well clear of the port's own mark so this is a plain grab of the existing selection.
        Drag(vm, 800 * Dbu, 200 * Dbu, 2800 * Dbu, 200 * Dbu);

        var rect = Assert.IsType<RectShape>(view.Shapes[0]);
        Assert.Equal(2000 * Dbu, rect.X1);              // the pad really did move…
        Assert.Equal(LayoutRotation.R0, PortOf(view).PortDirection);   // …and the port still names its own end
    }
}
