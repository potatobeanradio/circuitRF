using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md — the 12 acceptance gates ──────
// BitmapShape's model/geometry/hit-test/persistence support, R-bmp-2 (always-behind render),
// R-bmp-3 (excluded from geometric ops, full participation in select/move/scale/clipboard/undo),
// R-bmp-4 (viewport-relative placement sizing), R-bmp-5 (Insert Bitmap), and Locked.

public class LayoutBitmapTests : IDisposable
{
    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static readonly LayerKey LayerA = new(1, 0);
    private static readonly LayerKey LayerB = new(2, 0);

    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    // A real, decodable 4x4 solid-color PNG on disk — needed so BitmapCache.Load actually succeeds
    // and the renderer/pixel-size gates exercise the real decode path, not just a path string.
    private readonly string _redPngPath = WriteSolidPng(SKColors.Red, 4, 8); // 4 wide x 8 tall (2:1)
    private readonly string _brokenPath = Path.Combine(Path.GetTempPath(), $"circuitrf-bitmap-missing-{Guid.NewGuid():N}.png");

    private static string WriteSolidPng(SKColor color, int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(color);
        string path = Path.Combine(Path.GetTempPath(), $"circuitrf-bitmap-test-{Guid.NewGuid():N}.png");
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.OpenWrite(path);
        data.SaveTo(fs);
        return path;
    }

    public void Dispose()
    {
        BitmapCache.Invalidate(_redPngPath);
        try { File.Delete(_redPngPath); } catch { /* best-effort cleanup */ }
    }

    // ── Persistence round-trip ────────────────────────────────────────────────────────────────────

    [Fact]
    public void BitmapShape_RoundTrips_ThroughPersistence_ByteIdentical()
    {
        var view = FreshModel();
        view.Shapes.Add(new BitmapShape
        {
            Layer = LayerA, Net = null, ImagePathRef = _redPngPath,
            X = 1000, Y = 2000, W = 40_000, H = 20_000, Opacity = 0.75, Locked = true,
        });

        var json = LayoutPersistence.Serialize(view);
        Assert.Contains("\"$type\": \"Bitmap\"", json);

        var restored = LayoutPersistence.Deserialize(json);
        Assert.Equal(json, LayoutPersistence.Serialize(restored));

        var bmp = Assert.IsType<BitmapShape>(restored.Shapes[0]);
        Assert.Equal(_redPngPath, bmp.ImagePathRef);
        Assert.Equal(1000, bmp.X); Assert.Equal(2000, bmp.Y);
        Assert.Equal(40_000, bmp.W); Assert.Equal(20_000, bmp.H);
        Assert.Equal(0.75, bmp.Opacity);
        Assert.True(bmp.Locked);
    }

    // ── Geometry: Bbox / Clone / TranslateBy / coordinate walk ──────────────────────────────────────

    [Fact]
    public void BitmapShape_BboxOf_IsExactPlacementRect()
    {
        var bmp = new BitmapShape { Layer = LayerA, X = 1000, Y = 2000, W = 5000, H = 3000 };
        var bb = LayoutGeometry.BboxOf(bmp);
        Assert.Equal(1000, bb.MinX); Assert.Equal(2000, bb.MinY);
        Assert.Equal(6000, bb.MaxX); Assert.Equal(5000, bb.MaxY);
    }

    [Fact]
    public void BitmapShape_Clone_IsIndependentDeepCopy()
    {
        var bmp = new BitmapShape { Layer = LayerA, ImagePathRef = _redPngPath, X = 1, Y = 2, W = 3, H = 4, Opacity = 0.5, Locked = true };
        var clone = Assert.IsType<BitmapShape>(LayoutGeometry.Clone(bmp));
        clone.X = 999;
        Assert.Equal(1, bmp.X); // original untouched
        Assert.Equal(_redPngPath, clone.ImagePathRef);
        Assert.Equal(0.5, clone.Opacity);
        Assert.True(clone.Locked);
    }

    [Fact]
    public void BitmapShape_TranslateBy_MovesXY_LeavesWH()
    {
        var bmp = new BitmapShape { Layer = LayerA, X = 100, Y = 200, W = 50, H = 60 };
        LayoutGeometry.TranslateBy(bmp, 10, -20);
        Assert.Equal(110, bmp.X); Assert.Equal(180, bmp.Y);
        Assert.Equal(50, bmp.W); Assert.Equal(60, bmp.H);
    }

    [Fact]
    public void BitmapShape_CoordinateWalk_NonUniformScale_ScalesXYAndWH_Independently()
    {
        var bmp = new BitmapShape { Layer = LayerA, X = 0, Y = 0, W = 1000, H = 1000 };
        var t = LayoutCoordinateTransform.AxisIndependent(x => x * 2, y => y * 3, m => m);
        LayoutCoordinateWalk.Transform(bmp, t);
        Assert.Equal(0, bmp.X); Assert.Equal(0, bmp.Y);
        Assert.Equal(2000, bmp.W); Assert.Equal(3000, bmp.H);
    }

    // ── Hit-test / selection ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void HitTest_ClickInsideBitmapRect_Selects_And_ShowsBitmapType()
    {
        var model = FreshModel();
        model.Shapes.Add(new BitmapShape { Layer = LayerA, ImagePathRef = _redPngPath, X = 0, Y = 0, W = 10_000, H = 10_000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        Click(vm, 5000, 5000);

        Assert.Equal([0], vm.SelectedIndices);
    }

    [Fact]
    public void ClickOutsideBitmapRect_DoesNotSelect()
    {
        var model = FreshModel();
        model.Shapes.Add(new BitmapShape { Layer = LayerA, ImagePathRef = _redPngPath, X = 0, Y = 0, W = 10_000, H = 10_000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        Click(vm, 50_000, 50_000);

        Assert.Empty(vm.SelectedIndices);
    }

    // ── R-bmp-3: excluded from geometric operations, silently, leaving it untouched ────────────────

    [Fact]
    public void Union_MixedSelection_SkipsBitmap_OnlyCombinesGeometry()
    {
        var model = FreshModel();
        var rectA = new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        var rectB = new RectShape { Layer = LayerA, X1 = 5000, Y1 = 5000, X2 = 15_000, Y2 = 15_000 };
        var bmp = new BitmapShape { Layer = LayerA, ImagePathRef = _redPngPath, X = 100_000, Y = 100_000, W = 5000, H = 5000 };
        model.Shapes.Add(rectA); model.Shapes.Add(rectB); model.Shapes.Add(bmp);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(3, vm.SelectedIndices.Count);

        vm.ApplyUnion();

        // Only the two rects combined into one shape; the bitmap survives untouched.
        Assert.Equal(2, model.Shapes.Count);
        Assert.Contains(model.Shapes, s => s is BitmapShape b && ReferenceEquals(b, bmp));
    }

    [Fact]
    public void OffsetAvailability_AllBitmapSelection_DisabledWithNotGeometryReason()
    {
        var model = FreshModel();
        model.Shapes.Add(new BitmapShape { Layer = LayerA, ImagePathRef = _redPngPath, X = 0, Y = 0, W = 1000, H = 1000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);

        var avail = vm.OffsetAvailability;
        Assert.False(avail.CanExecute);
        Assert.NotNull(avail.DisabledReason);
    }

    // ── R-bmp-2: bitmaps ALWAYS render behind every layer, regardless of the layer's own ZOrder ────

    [Fact]
    public void Draw_BitmapBehindHigherZOrderLayer_UnderneathALowerZOrderLayerShape()
    {
        var tech = new Technology { Name = "T", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000 };
        tech.Layers.Add(new LayerDef // rect's layer: LOW ZOrder — would normally paint FIRST/bottom
        {
            Key = LayerA, Name = "Rect", Color = new CircuitRF.Design.Theming.Rgba(0, 0, 255), FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true,
        });
        tech.Layers.Add(new LayerDef // bitmap's layer: HIGH ZOrder — would normally paint LAST/top
        {
            Key = LayerB, Name = "Bmp", Color = new CircuitRF.Design.Theming.Rgba(0, 255, 0), FillOpacity = 1.0, ZOrder = 1000, Visible = true, Selectable = true,
        });

        var view = FreshModel();
        view.Shapes.Add(new BitmapShape { Layer = LayerB, ImagePathRef = _redPngPath, X = 0, Y = 0, W = 100_000, H = 100_000, Opacity = 1.0 });
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });

        var vp = LayoutViewport.ZoomToFit(new Bbox(0, 0, 100_000, 100_000), 200, 200, 0.1);
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        var center = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);

        // The rect (blue, low-ZOrder layer) must be visible on top — proving the bitmap painted
        // first despite its own layer's much higher ZOrder.
        Assert.True(center.Blue > center.Red + 20 && center.Blue > center.Green + 20);
    }

    [Fact]
    public void Draw_BrokenBitmapPath_RendersPlaceholder_NoException()
    {
        var view = FreshModel();
        view.Shapes.Add(new BitmapShape { Layer = LayerA, ImagePathRef = _brokenPath, X = 0, Y = 0, W = 10_000, H = 10_000, Opacity = 1.0 });

        var vp = LayoutViewport.ZoomToFit(new Bbox(0, 0, 10_000, 10_000), 100, 100, 0.1);
        using var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };

        var ex = Record.Exception(() => LayoutRenderer.Draw(surface.Canvas, view, null, vp, opts));
        Assert.Null(ex);
    }

    // ── BitmapCache: single shared cache, load-once, invalidate ─────────────────────────────────────

    [Fact]
    public void BitmapCache_Load_CachesByPath_SameInstanceUntilInvalidated()
    {
        var first = BitmapCache.Load(_redPngPath);
        var second = BitmapCache.Load(_redPngPath);
        Assert.NotNull(first);
        Assert.Same(first, second);

        BitmapCache.Invalidate(_redPngPath);
        var third = BitmapCache.Load(_redPngPath);
        Assert.NotNull(third);
        Assert.NotSame(first, third);
    }

    [Fact]
    public void BitmapCache_Load_MissingFile_ReturnsNull_NoException()
    {
        var ex = Record.Exception(() => BitmapCache.Load(_brokenPath));
        Assert.Null(ex);
        Assert.Null(BitmapCache.Load(_brokenPath));
    }

    [Fact]
    public void BitmapCache_TryGetPixelSize_ReturnsRealDimensions()
    {
        var size = BitmapCache.TryGetPixelSize(_redPngPath);
        Assert.NotNull(size);
        Assert.Equal(4, size!.Value.Width);
        Assert.Equal(8, size.Value.Height);
    }

    // ── R-bmp-4: viewport-relative placement sizing, aspect-ratio preserved ─────────────────────────

    [Fact]
    public void DropBitmap_SizesLongEdgeToQuarterViewportWidth_PreservingAspectRatio()
    {
        var model = FreshModel();
        var vm = new LayoutEditorViewModel(model);

        vm.DropBitmap(_redPngPath, 1000, 2000, viewportWidthDbu: 400_000);

        var bmp = Assert.IsType<BitmapShape>(model.Shapes[0]);
        Assert.Equal(1000, bmp.X); Assert.Equal(2000, bmp.Y); // drop point IS the top-left corner
        // Source is 4 wide x 8 tall (portrait, 2:1) -> long edge is H; H == 25% of 400_000 = 100_000.
        Assert.Equal(100_000, bmp.H);
        Assert.Equal(50_000, bmp.W); // half of H, preserving the 1:2 aspect ratio
    }

    [Fact]
    public void InsertBitmapAtViewportCenter_CentersPlacementRectOnGivenPoint()
    {
        var model = FreshModel();
        var vm = new LayoutEditorViewModel(model);

        vm.InsertBitmapAtViewportCenter(_redPngPath, centerX: 10_000, centerY: 10_000, viewportWidthDbu: 40_000);

        var bmp = Assert.IsType<BitmapShape>(model.Shapes[0]);
        double cx = bmp.X + bmp.W / 2.0, cy = bmp.Y + bmp.H / 2.0;
        // Within one snap step (Model.SnapDbu=1000) of the requested centre — the placement point
        // itself is snapped (LayoutSnapping.SnapPoint), so an exact match isn't guaranteed.
        Assert.True(Math.Abs(cx - 10_000) <= 1000, $"cx={cx}");
        Assert.True(Math.Abs(cy - 10_000) <= 1000, $"cy={cy}");
    }

    [Fact]
    public void DropBitmap_UndecodableFile_FallsBackToFourByThreeBox_WarnsViaMessages()
    {
        var model = FreshModel();
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink);

        // A file that exists but is not a valid image — BitmapCache.Load must return null for it.
        string garbagePath = Path.Combine(Path.GetTempPath(), $"circuitrf-bitmap-garbage-{Guid.NewGuid():N}.png");
        File.WriteAllText(garbagePath, "not a real png");
        try
        {
            vm.DropBitmap(garbagePath, 0, 0, viewportWidthDbu: 100_000);

            var bmp = Assert.IsType<BitmapShape>(model.Shapes[0]);
            Assert.Equal(25_000, bmp.W); // 25% of viewport width
            Assert.Equal(18_750, bmp.H); // 4:3 -> W * 3/4
            Assert.Contains(sink.Posted, p => p.Level == MessageLevel.Warning);
        }
        finally
        {
            try { File.Delete(garbagePath); } catch { /* best-effort */ }
        }
    }

    // ── Right-click: Resolve Path… / Refresh Cache ───────────────────────────────────────────────

    [Fact]
    public void FindBitmapForContextMenu_ReportsBrokenState()
    {
        var model = FreshModel();
        model.Shapes.Add(new BitmapShape { Layer = LayerA, ImagePathRef = _brokenPath, X = 0, Y = 0, W = 10_000, H = 10_000 });
        var vm = new LayoutEditorViewModel(model);

        var found = vm.FindBitmapForContextMenu(5000, 5000, 40);
        Assert.NotNull(found);
        Assert.Equal(0, found!.Value.ShapeIndex);
        Assert.True(found.Value.IsBroken);
    }

    [Fact]
    public void ResolveBitmapPath_UpdatesPath_AsUndoableCommand()
    {
        var model = FreshModel();
        model.Shapes.Add(new BitmapShape { Layer = LayerA, ImagePathRef = _brokenPath, X = 0, Y = 0, W = 10_000, H = 10_000 });
        var vm = new LayoutEditorViewModel(model);

        vm.ResolveBitmapPath(0, _redPngPath);

        var bmp = Assert.IsType<BitmapShape>(model.Shapes[0]);
        Assert.Equal(_redPngPath, bmp.ImagePathRef);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(_brokenPath, ((BitmapShape)model.Shapes[0]).ImagePathRef);
    }

    [Fact]
    public void RefreshBitmapCache_DoesNotThrow_ForKnownOrUnknownIndex()
    {
        var model = FreshModel();
        model.Shapes.Add(new BitmapShape { Layer = LayerA, ImagePathRef = _redPngPath, X = 0, Y = 0, W = 1000, H = 1000 });
        var vm = new LayoutEditorViewModel(model);

        var ex = Record.Exception(() => { vm.RefreshBitmapCache(0); vm.RefreshBitmapCache(99); });
        Assert.Null(ex);
    }

    // ── Locked: blocks move and scale, never selection ───────────────────────────────────────────

    [Fact]
    public void Move_LockedBitmapInMultiSelection_StaysPut_OthersMove()
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        var bmp = new BitmapShape { Layer = LayerA, ImagePathRef = _redPngPath, X = 5000, Y = 5000, W = 1000, H = 1000, Locked = true };
        model.Shapes.Add(rect); model.Shapes.Add(bmp);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);
        Click(vm, 5500, 5500, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);

        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(2500, 500, leftDown: true, KeyModifiers.None, 40);
        vm.OnPointerReleased(2500, 500, KeyModifiers.None);

        Assert.Equal(2000, rect.X1); // moved by +2000
        Assert.Equal(5000, bmp.X);   // locked bitmap never moved
        Assert.True(vm.UndoRedo.CanUndo);

        // Bitmap stayed selected throughout — Locked blocks the move, never the selection.
        Assert.Contains(1, vm.SelectedIndices);
    }

    [Fact]
    public void Scale_LoneLockedBitmapSelection_HandlesShow_ButDragNeverCommits()
    {
        var model = FreshModel();
        var bmp = new BitmapShape { Layer = LayerA, ImagePathRef = _redPngPath, X = 0, Y = 0, W = 1000, H = 1000, Locked = true };
        model.Shapes.Add(bmp);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);

        Assert.True(vm.ShowScaleHandles); // handles still show — Locked doesn't hide them

        vm.OnPointerPressed(1000, 1000, KeyModifiers.None, 1, 40); // top-right corner handle
        vm.OnPointerMoved(2000, 2000, leftDown: true, KeyModifiers.None, 40);
        vm.OnPointerReleased(2000, 2000, KeyModifiers.None);

        Assert.False(vm.UndoRedo.CanUndo); // nothing to scale -> the drag never committed
        Assert.Equal(1000, bmp.W); Assert.Equal(1000, bmp.H);
    }

    [Fact]
    public void ApplyScale_SelectionWithLockedBitmap_SkipsIt_ScalesOthers()
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        var bmp = new BitmapShape { Layer = LayerA, ImagePathRef = _redPngPath, X = 2000, Y = 0, W = 1000, H = 1000, Locked = true };
        model.Shapes.Add(rect); model.Shapes.Add(bmp);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        vm.ApplyScale(2.0, 2.0, 0, 0);

        Assert.True(vm.UndoRedo.CanUndo);
        // The rect scaled (or was replaced at index 0); the locked bitmap survives untouched.
        Assert.Contains(model.Shapes, s => s is BitmapShape b && b.W == 1000 && b.H == 1000);
    }

    // ── Clipboard fragment round-trip (LayoutFragment — the pure, framework-free half) ─────────────

    [Fact]
    public void LayoutFragment_BuildAndDeserialize_RoundTripsBitmap()
    {
        var bmp = new BitmapShape { Layer = LayerA, ImagePathRef = _redPngPath, X = 100, Y = 200, W = 300, H = 400, Opacity = 0.6, Locked = true };
        var payload = LayoutFragment.Build([bmp], tech: null, dbuPerMicron: 1000);
        var json = LayoutFragment.Serialize(payload);

        Assert.True(LayoutFragment.TryDeserialize(json, out var restored));
        var restoredBmp = Assert.IsType<BitmapShape>(restored!.Shapes[0]);
        Assert.Equal(_redPngPath, restoredBmp.ImagePathRef);
        Assert.Equal(300, restoredBmp.W); Assert.Equal(400, restoredBmp.H);
        Assert.Equal(0.6, restoredBmp.Opacity);
        Assert.True(restoredBmp.Locked);
    }

    [Fact]
    public void LayoutFragment_Translate_MovesBitmapPlacement()
    {
        var bmp = new BitmapShape { Layer = LayerA, ImagePathRef = _redPngPath, X = 100, Y = 200, W = 300, H = 400 };
        var translated = LayoutFragment.Translate([bmp], dx: 50, dy: -50);
        var moved = Assert.IsType<BitmapShape>(translated[0]);
        Assert.Equal(150, moved.X); Assert.Equal(150, moved.Y);
        Assert.Equal(300, moved.W); Assert.Equal(400, moved.H); // size untouched by a pure translate
    }
}
