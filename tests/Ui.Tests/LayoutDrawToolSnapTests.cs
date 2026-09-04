using System;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// Owner report, 2026-09-04: arming a drawing tool from the layout toolbar (the Circle tool was the
// example) gave no geometry snapping and no snap glyphs, with geometry snap switched ON.
//
// It was never a snap failure — the drawing tools did not ask. Every draw point went through
// LayoutSnapping.SnapPoint / ConstrainAndSnap, which are GRID snap alone, and UpdateSnapMarker — the
// one method that both resolves a candidate and populates Overlay.SnapMarker — was reached only from
// the Select, Ruler, Port and Instance paths. These tests drive the tools through
// OnPointerPressed/Moved/Released exactly as LayoutCanvas would, mirroring LayoutSnapGestureTests.

public sealed class LayoutDrawToolSnapTests
{
    // Deliberately COARSE relative to the offsets below, so a passing assertion cannot be grid snap
    // wearing geometry snap's clothes: the grid here is 1000 and the features are at multiples of
    // 7 that no grid rounding lands on.
    private const long SnapTol = 3000;

    private static LayoutView FreshModel(long snapDbu = 1000) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
    };

    /// <summary>A rect whose corners sit OFF the grid, so landing on one proves geometry snap ran.</summary>
    private static RectShape OffGridRect() => new()
    {
        Layer = new LayerKey(1, 0), X1 = 20_007, Y1 = 30_003, X2 = 60_007, Y2 = 70_003,
    };

    private static LayoutEditorViewModel VmWith(LayoutEditorViewModel.Tool tool, LayoutView model) =>
        new(model) { ActiveTool = tool };

    // ── The glyph, BEFORE the first click ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LayoutEditorViewModel.Tool.Circle)]
    [InlineData(LayoutEditorViewModel.Tool.Rect)]
    [InlineData(LayoutEditorViewModel.Tool.RoundedRect)]
    [InlineData(LayoutEditorViewModel.Tool.Polygon)]
    [InlineData(LayoutEditorViewModel.Tool.Path)]
    [InlineData(LayoutEditorViewModel.Tool.Label)]
    public void ArmedDrawTool_HoveringNearACorner_ShowsTheSnapMarker(LayoutEditorViewModel.Tool tool)
    {
        var model = FreshModel();
        model.Shapes.Add(OffGridRect());
        var vm = VmWith(tool, model);

        vm.OnPointerMoved(20_500, 30_400, leftDown: false, KeyModifiers.None,
                          hitTolDbu: 40, pixelDbu: 0, snapTolDbu: SnapTol);

        Assert.NotNull(vm.Overlay.SnapMarker);
        Assert.Equal(SnapFeatureKind.CornerEndpoint, vm.Overlay.SnapMarker!.Value.Kind);
        Assert.Equal(20_007, vm.Overlay.SnapMarker.Value.X);
        Assert.Equal(30_003, vm.Overlay.SnapMarker.Value.Y);
    }

    [Fact]
    public void ArmedDrawTool_WithGeometrySnapOff_ShowsNoMarker()
    {
        var model = FreshModel();
        model.Shapes.Add(OffGridRect());
        var vm = VmWith(LayoutEditorViewModel.Tool.Circle, model);
        vm.GeometrySnapEnabled = false;

        vm.OnPointerMoved(20_500, 30_400, leftDown: false, KeyModifiers.None,
                          hitTolDbu: 40, pixelDbu: 0, snapTolDbu: SnapTol);

        Assert.Null(vm.Overlay.SnapMarker);
    }

    // ── The point actually placed ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CircleTool_FirstPress_LandsOnTheSnapCandidate_NotTheGrid()
    {
        var model = FreshModel();
        model.Shapes.Add(OffGridRect());
        var vm = VmWith(LayoutEditorViewModel.Tool.Circle, model);

        // Press near the (20_007, 30_003) corner, then drag out and release to commit a circle.
        vm.OnPointerPressed(20_500, 30_400, KeyModifiers.None, 1, 40, 0, SnapTol);
        vm.OnPointerMoved(25_000, 30_400, leftDown: true, KeyModifiers.None, 40, 0, SnapTol);
        vm.OnPointerReleased(25_000, 30_400, KeyModifiers.None);

        var circle = Assert.Single(model.Shapes.OfType<CircleShape>());
        Assert.Equal(20_007, circle.Cx);
        Assert.Equal(30_003, circle.Cy);
    }

    [Fact]
    public void RectTool_BothCorners_LandOnTheirSnapCandidates()
    {
        var model = FreshModel();
        model.Shapes.Add(OffGridRect());
        var vm = VmWith(LayoutEditorViewModel.Tool.Rect, model);

        vm.OnPointerPressed(20_500, 30_400, KeyModifiers.None, 1, 40, 0, SnapTol);
        vm.OnPointerMoved(59_600, 69_600, leftDown: true, KeyModifiers.None, 40, 0, SnapTol);
        vm.OnPointerReleased(59_600, 69_600, KeyModifiers.None);

        // Shapes[0] is the reference rect; the drawn one is the new arrival.
        var drawn = model.Shapes.OfType<RectShape>().Last();
        Assert.Equal(20_007, Math.Min(drawn.X1, drawn.X2));
        Assert.Equal(30_003, Math.Min(drawn.Y1, drawn.Y2));
        Assert.Equal(60_007, Math.Max(drawn.X1, drawn.X2));
        Assert.Equal(70_003, Math.Max(drawn.Y1, drawn.Y2));
    }

    [Fact]
    public void PolygonTool_EveryVertex_TakesTheSnapCandidateOverTheGrid()
    {
        var model = FreshModel();
        model.Shapes.Add(OffGridRect());
        var vm = VmWith(LayoutEditorViewModel.Tool.Polygon, model);

        vm.OnPointerPressed(20_500, 30_400, KeyModifiers.None, 1, 40, 0, SnapTol);   // corner (X1,Y1)
        vm.OnPointerPressed(59_600, 30_400, KeyModifiers.None, 1, 40, 0, SnapTol);   // corner (X2,Y1)
        vm.OnPointerPressed(59_600, 69_600, KeyModifiers.None, 1, 40, 0, SnapTol);   // corner (X2,Y2)
        vm.OnKeyDown(Key.Enter, KeyModifiers.None);

        var poly = Assert.Single(model.Shapes.OfType<PolygonShape>());
        Assert.Equal([20_007, 30_003, 60_007, 30_003, 60_007, 70_003], poly.Xy);
    }

    [Fact]
    public void DrawToolWithNothingInRange_StillFallsBackToGridSnap()
    {
        var model = FreshModel();
        model.Shapes.Add(OffGridRect());
        var vm = VmWith(LayoutEditorViewModel.Tool.Circle, model);

        // Far from every feature: grid snap alone, exactly as before this change.
        vm.OnPointerPressed(200_400, 300_400, KeyModifiers.None, 1, 40, 0, SnapTol);
        vm.OnPointerMoved(205_000, 300_400, leftDown: true, KeyModifiers.None, 40, 0, SnapTol);
        vm.OnPointerReleased(205_000, 300_400, KeyModifiers.None);

        var circle = Assert.Single(model.Shapes.OfType<CircleShape>());
        Assert.Equal(200_000, circle.Cx);
        Assert.Equal(300_000, circle.Cy);
    }
}
