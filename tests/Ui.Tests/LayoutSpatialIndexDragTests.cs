// Gate 8 (docs/sonnet-briefs/brief-L2b-spatial-index.md §6/R-L2b-2): "Drags do not churn the index...
// assert the index is rebuilt/updated zero times across a 100-move drag, and once on release." Verified
// rather than assumed, per the brief's own instruction, via LayoutSpatialIndex's internal
// FullRebuildCount/IncrementalApplyCount counters (InternalsVisibleTo CircuitRF.Ui.Tests).

using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

public class LayoutSpatialIndexDragTests
{
    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 100 };

    [Fact]
    public void MoveDrag_100PointerMoves_TouchesIndexZeroTimes_ThenExactlyOnceOnRelease()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 5000, Y2 = 5000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        // Seed the index once, outside the measured window, so the drag's own eventual commit is a
        // genuine incremental Apply rather than a "never built yet" full rebuild.
        _ = model.SpatialIndex.QueryIntersecting(model.Shapes, new Bbox(-1, -1, 1, 1));
        int rebuildsBeforeDrag = model.SpatialIndex.FullRebuildCount;
        int incrementalBeforeDrag = model.SpatialIndex.IncrementalApplyCount;

        vm.OnPointerPressed(2500, 2500, KeyModifiers.None, 1, 40);
        for (int i = 1; i <= 100; i++)
            vm.OnPointerMoved(2500 + i * 10, 2500 + i * 10, true, KeyModifiers.None, 40, pixelDbu: 0);

        Assert.Equal(rebuildsBeforeDrag, model.SpatialIndex.FullRebuildCount);
        Assert.Equal(incrementalBeforeDrag, model.SpatialIndex.IncrementalApplyCount);

        vm.OnPointerReleased(2500 + 1000, 2500 + 1000, KeyModifiers.None);

        Assert.Equal(rebuildsBeforeDrag, model.SpatialIndex.FullRebuildCount);         // still no rebuild
        Assert.Equal(incrementalBeforeDrag + 1, model.SpatialIndex.IncrementalApplyCount); // exactly one incremental Apply
    }

    [Fact]
    public void MoveDrag_LiveDraggedShape_StillRendersAtItsPreviewPosition_EvenThoughIndexIsStale()
    {
        // The render-culling fix's own regression: during a drag, the index still reflects the
        // PRE-drag bbox for the whole gesture (by design — see the test above), so
        // LayoutRenderer.Draw must not rely on the index alone for a shape with a live
        // Overlay.DragOverrides entry, or a shape dragged from off-screen into view would be
        // wrongly culled mid-drag.
        var model = FreshModel();
        var layer = new LayerKey(1, 0);
        model.Shapes.Add(new RectShape { Layer = layer, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }); // starts off-screen
        var tech = new Technology
        {
            Layers = [new LayerDef { Key = layer, Color = new Rgba(255, 0, 0), Visible = true, Selectable = true, FillOpacity = 1.0 }],
        };

        // Viewport framing a region the shape's ORIGINAL position does NOT overlap.
        var vp = new LayoutViewport(100_000, 100_000, 0.5, 200, 200);
        var translated = new RectShape { Layer = layer, X1 = 100_500, Y1 = 100_500, X2 = 101_000, Y2 = 101_000 };
        var overlay = new LayoutOverlay { DragOverrides = new System.Collections.Generic.Dictionary<int, LayoutShape> { [0] = translated } };

        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(200, 200));
        var opts = new Renderers.LayoutRenderOptions { Theme = Renderers.LayoutRenderTheme.Light, ShowGrid = false, Overlay = overlay };
        var result = Renderers.LayoutRenderer.Draw(surface.Canvas, model, tech, vp, opts);

        Assert.Equal(1, result.ShapesDrawn); // the dragged shape is force-included and drawn at its LIVE position
    }
}
