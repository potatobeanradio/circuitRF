using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// Snap tracking in the layout editor: a snap point held for the dwell becomes a reference, and with a drawing tool armed
// the cursor then locks onto the horizontal and vertical lines through it — exactly where such a line crosses an outline
// edge, otherwise on the grid; two references give their lines' crossing; a point snap always wins. Driven through
// OnPointerMoved/Pressed/Released as LayoutCanvas drives them, with the tracker's clock in the test's hands.

public sealed class LayoutSnapTrackingTests
{
    private const long SnapTol = 3000;

    // A trace's corner well above a ground plane whose top edge is at y = 10_007: off the 1000 grid, so landing on it
    // proves the edge crossing ran and not the grid.
    private static LayoutView TraceOverGround()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 20_007, Y1 = 50_003, X2 = 40_007, Y2 = 52_003 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 10_007 });
        return model;
    }

    private static void Move(LayoutEditorViewModel vm, long x, long y, bool down = false)
        => vm.OnPointerMoved(x, y, leftDown: down, KeyModifiers.None, hitTolDbu: 40, pixelDbu: 0, snapTolDbu: SnapTol);

    [Fact]
    public void RectTool_HeldOnATraceCorner_ThenDown_StartsOnTheGroundEdge_AndFinishingForgetsTheReference()
    {
        long now = 0;
        var vm = new LayoutEditorViewModel(TraceOverGround()) { ActiveTool = LayoutEditorViewModel.Tool.Rect };
        vm.TrackingClock = () => now;

        Move(vm, 20_100, 50_100);                                         // on the trace's corner
        now = SnapReferences<LayoutSnapReference>.DwellMs;
        vm.TickSnapTracking();                                            // the cursor held still: acquired
        Assert.Equal((20_007L, 50_003L), Assert.Single(vm.Overlay.SnapTracking!.References));

        Move(vm, 20_900, 11_000);                                         // down, near the ground's top edge
        var marker = vm.Overlay.SnapMarker!.Value;
        Assert.Equal(SnapFeatureKind.Tracking, marker.Kind);
        Assert.Equal((20_007L, 10_007L), (marker.X, marker.Y));
        Assert.True(Assert.Single(vm.Overlay.SnapTracking!.Lines).Vertical);

        vm.OnPointerPressed(20_900, 11_000, KeyModifiers.None, 1, 40, 0, SnapTol);
        Move(vm, 60_000, 30_000, down: true);
        vm.OnPointerReleased(60_000, 30_000, KeyModifiers.None);
        var drawn = Assert.IsType<RectShape>(vm.Model.Shapes[^1]);
        Assert.Equal((20_007L, 10_007L), (Math.Min(drawn.X1, drawn.X2), Math.Min(drawn.Y1, drawn.Y2)));
        Assert.Null(vm.Overlay.SnapTracking);
    }

    [Fact]
    public void TwoReferences_GiveTheirCrossing_ElseTheGrid_AndAPointWins()
    {
        var t = new LayoutSnapTracker();
        var layer = new LayerKey(1, 0);
        void Acquire(long x, long y, long at)
        {
            t.Observe(new SnapCandidate(SnapFeatureKind.CornerEndpoint, x, y, layer, false, 0), at);
            Assert.True(t.Tick(at + SnapReferences<LayoutSnapReference>.DwellMs));
        }
        Acquire(10_007, 0, 0);
        Acquire(0, 20_003, 1000);
        t.Observe(null, 2000);
        IReadOnlyList<SnapEdge> None(Bbox _) => [];

        var cross = t.Apply(null, 10_500, 20_500, SnapTol, 1000, None, 2000)!.Value;
        Assert.Equal((SnapFeatureKind.Tracking, 10_007L, 20_003L), (cross.Kind, cross.X, cross.Y));

        var grid = t.Apply(null, 10_500, 35_400, SnapTol, 1000, None, 2000)!.Value;
        Assert.Equal((10_007L, 35_000L), (grid.X, grid.Y));

        var corner = new SnapCandidate(SnapFeatureKind.CornerEndpoint, 10_400, 35_400, layer, false, 3);
        Assert.Equal(corner, t.Apply(corner, 10_500, 35_400, SnapTol, 1000, None, 2000));
    }
}
