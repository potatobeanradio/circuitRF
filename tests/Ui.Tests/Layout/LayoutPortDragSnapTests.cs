// Owner report, 2026-08-09: "the port won't snap until it's over a metal. I'd like it to snap to the
// spot even while dragging over white space (if it's within the threshold)."
//
// Target attraction during a move drag was gated on _snapDragActive, which is set only when the PRESS
// itself landed on a snap marker (TryBeginSnapMarkerDrag). A LabelShape contributes no snap features
// at all, so pressing a port that sits over empty space found no candidate, began an ordinary body
// drag, and geometry snap never engaged for the rest of the gesture. A port sitting ON metal happened
// to have the CONDUCTOR's own feature under the press, so snap worked there and nowhere else —
// exactly the reported "only over metal".

using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests.Layout;

public class LayoutPortDragSnapTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey TopCopper = new(1, 0);

    private const long Tol = 300 * Dbu;   // geometry-snap tolerance, in DBU

    /// <summary>A 20 x 2.9 mm conductor with its low-x/low-y corner at the origin, plus a port parked
    /// well clear of it in empty space.</summary>
    private static (LayoutEditorViewModel Vm, LayoutView View, LabelShape Port) Fixture()
    {
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 1_000 * Dbu };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 20_000 * Dbu, Y2 = 2_900 * Dbu });

        // Deliberately OFF-grid. The first version of this fixture parked the port at a round
        // (5 mm, 10 mm) — where an ordinary grid delta-snap ALSO lands on the corner, so every
        // assertion below passed against the unfixed code. Off-grid, the two answers differ by the
        // port's own fractional offset and the gate has teeth.
        var port = new LabelShape
        {
            Layer = TopCopper, X = 5_137 * Dbu, Y = 10_211 * Dbu, Text = "P1", Height = 500 * Dbu,
            IsPort = true, PortDirection = LayoutRotation.R0,
        };
        view.Shapes.Add(port);

        var vm = new LayoutEditorViewModel(view)
        {
            ActiveTool = LayoutEditorViewModel.Tool.Select,
            GeometrySnapEnabled = true,
        };
        return (vm, view, port);
    }

    private static void Drag(LayoutEditorViewModel vm, long fromX, long fromY, long toX, long toY)
    {
        vm.OnPointerPressed(fromX, fromY, KeyModifiers.None, 1, 0, 0, Tol);
        vm.OnPointerMoved(toX, toY, leftDown: true, KeyModifiers.None, 0, 0, Tol);
        vm.OnPointerReleased(toX, toY, KeyModifiers.None);
    }

    [Fact]
    public void APortDraggedFromEmptySpace_SnapsToACornerItIsOnlyNEAR_NotOver()
    {
        var (vm, _, port) = Fixture();

        // Land the cursor OUTSIDE the conductor — up and to the left of its (0,0) corner — but well
        // inside the snap tolerance of it. This is the case the report is about: over white space.
        long targetX = -100 * Dbu, targetY = -120 * Dbu;
        Drag(vm, port.X, port.Y, targetX, targetY);

        Assert.Equal(0, port.X);
        Assert.Equal(0, port.Y);
    }

    [Fact]
    public void ThePortsOwnAnchorLandsOnTheTarget_NotWhereverInsideItsPickRegionTheUserPressed()
    {
        // The pick region is deliberately generous and symmetric, so pressing "on" a port is usually
        // pressing somewhere NEAR it. The thing that must land on the target is the port.
        var (vm, _, port) = Fixture();

        long pressOffset = 400 * Dbu;   // inside the pick region, well away from the anchor
        Drag(vm, port.X + pressOffset, port.Y + pressOffset, -100 * Dbu, -120 * Dbu);

        Assert.Equal(0, port.X);
        Assert.Equal(0, port.Y);
    }

    [Fact]
    public void WithNothingInRange_ThePortStillFollowsTheGrid()
    {
        // The non-vacuity guard: without it, a port that simply followed the raw cursor would pass the
        // tests above for entirely the wrong reason.
        var (vm, _, port) = Fixture();

        long fromX = port.X, fromY = port.Y;
        Drag(vm, fromX, fromY, fromX + 4_400 * Dbu, fromY + 2_600 * Dbu);

        // SnapDbu is 1 mm, so the DELTA quantises to 4 mm / 3 mm and the off-grid fraction survives.
        Assert.Equal(fromX + 4_000 * Dbu, port.X);
        Assert.Equal(fromY + 3_000 * Dbu, port.Y);
    }

    [Fact]
    public void AltStillSuppressesTheSnap()
    {
        var (vm, _, port) = Fixture();

        vm.OnPointerPressed(port.X, port.Y, KeyModifiers.None, 1, 0, 0, Tol);
        vm.OnPointerMoved(-100 * Dbu, -120 * Dbu, leftDown: true, KeyModifiers.Alt, 0, 0, Tol);
        vm.OnPointerReleased(-100 * Dbu, -120 * Dbu, KeyModifiers.Alt);

        // Alt suspends snapping outright — geometry AND grid — so the port follows the raw cursor.
        // The point of the assertion is that it does NOT land on the corner: the attraction is off.
        Assert.Equal(-100 * Dbu, port.X);
        Assert.Equal(-120 * Dbu, port.Y);
    }

    [Fact]
    public void AnOrdinaryShapeBodyDrag_StillSnapsTheDeltaAndNotAnAbsolutePosition()
    {
        // The scope fence, and R-L1c-3's own rule. A port has no internal geometry to preserve, which
        // is what makes absolute attraction correct FOR IT; a rect very much does, and re-quantising
        // its vertices by moving it is exactly what snapping the delta exists to prevent.
        var view = new LayoutView { DbuPerMicron = Dbu, DisplayUnit = LayoutUnit.Um, SnapDbu = 1_000 * Dbu };
        view.Shapes.Add(new RectShape { Layer = TopCopper, X1 = 0, Y1 = 0, X2 = 20_000 * Dbu, Y2 = 2_900 * Dbu });

        // Deliberately OFF-grid, so a delta-snap and a position-snap give provably different answers,
        // and deliberately MUCH larger than the snap tolerance so a press deep inside it is not within
        // reach of its own corners, edge midpoints, edges or centroid. (The first version of this test
        // pressed the rect's centre — which IS a snap feature — and so exercised the grab-role path
        // instead of the body-drag one it is about.)
        var movable = new RectShape
        {
            Layer = TopCopper,
            X1 = 40_137 * Dbu, Y1 = 30_211 * Dbu,
            X2 = 50_137 * Dbu, Y2 = 40_211 * Dbu,
        };
        view.Shapes.Add(movable);

        var vm = new LayoutEditorViewModel(view)
        {
            ActiveTool = LayoutEditorViewModel.Tool.Select,
            GeometrySnapEnabled = true,
        };

        long px = 43_000 * Dbu, py = 32_500 * Dbu;   // interior, far from every feature
        vm.OnPointerPressed(px, py, KeyModifiers.None, 1, 0, 0, Tol);
        Assert.False(vm.SnapDragActiveForTests, "the press must NOT have grabbed a snap marker");

        vm.OnPointerMoved(px - 40_437 * Dbu, py - 31_289 * Dbu, leftDown: true, KeyModifiers.None, 0, 0, Tol);
        vm.OnPointerReleased(px - 40_437 * Dbu, py - 31_289 * Dbu, KeyModifiers.None);

        // Delta quantised to (-40 mm, -31 mm); the rect keeps its off-grid fractional position.
        Assert.Equal(40_137 * Dbu - 40_000 * Dbu, movable.X1);
        Assert.Equal(30_211 * Dbu - 31_000 * Dbu, movable.Y1);
    }
}
