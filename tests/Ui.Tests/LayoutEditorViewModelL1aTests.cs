using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── Phase L1a gates: viewport readouts, unknown-layer warn-once, and the L0-closes-the-loop gate ──

public class LayoutEditorViewModelL1aTests
{
    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static LayoutView FreshModel() => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = 1000,
        AngleMode    = AngleMode.AnyAngle,
    };

    // ── IsEmpty ───────────────────────────────────────────────────────────────

    [Fact]
    public void IsEmpty_TrueForFreshLayout_FalseOnceAShapeExists()
    {
        var model = FreshModel();
        var vm = new LayoutEditorViewModel(model);
        Assert.True(vm.IsEmpty);

        model.Shapes.Add(new RectShape { X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        Assert.False(vm.IsEmpty);
    }

    // ── Cursor readout (§1 R6) ─────────────────────────────────────────────────

    [Fact]
    public void SetCursorWorld_FormatsInDisplayUnit_AndClearsOnNull()
    {
        var model = FreshModel();
        model.DisplayUnit = LayoutUnit.Mil;
        var vm = new LayoutEditorViewModel(model);

        vm.SetCursorWorld(25_400, 50_800); // 1 mil, 2 mil at 25400 dbu/mil
        Assert.Equal("1 mil", vm.CursorXText);
        Assert.Equal("2 mil", vm.CursorYText);

        vm.SetCursorWorld(null, null);
        Assert.Equal("—", vm.CursorXText);
        Assert.Equal("—", vm.CursorYText);
    }

    // ── Unknown-layer warning: once per layer, not once per shape (gate 4) ────

    [Fact]
    public void ReportUnknownLayers_WarnsOncePerLayer_EvenAcrossManyFrames()
    {
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(FreshModel(), messageSink: sink);

        var key = new LayerKey(9, 0);
        // Simulate 50 shapes on the unknown layer all surfacing the same key in one frame,
        // across several frames (e.g. repeated re-renders during pan/zoom).
        for (int frame = 0; frame < 5; frame++)
            vm.ReportUnknownLayers(Enumerable.Repeat(key, 50).ToArray());

        Assert.Single(sink.Posted);
        Assert.Equal(MessageLevel.Warning, sink.Posted[0].Level);
    }

    [Fact]
    public void ReportUnknownLayers_DifferentKeys_EachWarnsOnce()
    {
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(FreshModel(), messageSink: sink);

        vm.ReportUnknownLayers([new LayerKey(1, 0), new LayerKey(2, 0)]);
        vm.ReportUnknownLayers([new LayerKey(1, 0), new LayerKey(2, 0), new LayerKey(3, 0)]);

        Assert.Equal(3, sink.Posted.Count);
    }

    // ── Gate 10: "the L0 loop closes" — an edited layer color repaints the canvas ─────

    [Fact]
    public void ChangedLayerColor_OnReresolvedTechnology_ChangesRenderedPixel()
    {
        var model = FreshModel();
        var layerKey = new LayerKey(1, 0);
        model.Shapes.Add(new RectShape { Layer = layerKey, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });

        Technology MakeTech(Rgba color) => new()
        {
            Name = "Test",
            Layers = { new LayerDef { Key = layerKey, Name = "M1", Color = color, FillOpacity = 1.0, Visible = true } },
        };

        var vm = new LayoutEditorViewModel(model);
        vm.ApplyTechResolution(new TechResolution(MakeTech(new Rgba(255, 0, 0)), "/ws/tech/t.ctech", TechResolutionSource.WorkspaceDefault, []));

        var bb = new Bbox(0, 0, 100_000, 100_000);
        var vp = LayoutViewport.ZoomToFit(bb, 100, 100, marginFrac: 0.0);
        var opts = LayoutRenderOptions.Default(LayoutRenderTheme.Light);

        using var surfaceBefore = SKSurface.Create(new SKImageInfo(100, 100));
        LayoutRenderer.Draw(surfaceBefore.Canvas, vm.Model, vm.Technology, vp, opts);
        var before = SamplePixel(surfaceBefore, 50, 50);

        // Simulate the .ctech editor saving an edited color and TechnologyCache's live-refresh seam
        // handing the layout a freshly-resolved Technology (a NEW instance, per L0c's contract).
        vm.ApplyTechResolution(new TechResolution(MakeTech(new Rgba(0, 0, 255)), "/ws/tech/t.ctech", TechResolutionSource.WorkspaceDefault, []));

        using var surfaceAfter = SKSurface.Create(new SKImageInfo(100, 100));
        LayoutRenderer.Draw(surfaceAfter.Canvas, vm.Model, vm.Technology, vp, opts);
        var after = SamplePixel(surfaceAfter, 50, 50);

        Assert.True(before.Red > before.Blue, "before: should read red-dominant");
        Assert.True(after.Blue > after.Red, "after: should read blue-dominant once the layer color changed");
    }

    private static SKColor SamplePixel(SKSurface surface, int x, int y)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }
}
