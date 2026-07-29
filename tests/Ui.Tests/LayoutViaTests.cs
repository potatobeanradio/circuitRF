using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

/// <summary>Gates 4-6, 8 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md): the Via tool, its
/// technology-driven enablement, annulus rendering, and the Convert-to-Via recovery command.</summary>
public class LayoutViaTests
{
    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 0 };

    private static Technology TechWithViaLayer(long padDbu = 0, long drillDbu = 0) => new()
    {
        Name = "T",
        Layers =
        [
            new LayerDef { Key = new LayerKey(1, 0), Name = "Drill", Color = new Rgba(0x20, 0x20, 0x20) },
        ],
        DefaultViaPadDbu = padDbu,
        DefaultViaDrillDbu = drillDbu,
        Stackup = new Stackup
        {
            Layers = [new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [new LayerKey(1, 0)] }],
        },
    };

    private static Technology TechWithNoViaLayer() => new()
    {
        Name = "NoVia",
        Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "Copper", Color = new Rgba(0, 0, 0) }],
    };

    // ── Gate 6: tool enablement follows the technology ────────────────────────────────────────────

    [Fact]
    public void ViaToolAvailability_EnabledWhenStackupHasViaLayer()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        vm.ApplyTechResolution(new TechResolution(TechWithViaLayer(), "/t.ctech", TechResolutionSource.WorkspaceDefault, []));
        Assert.True(vm.ViaToolAvailability.CanExecute);
    }

    [Fact]
    public void ViaToolAvailability_DisabledWithReason_WhenStackupHasNoViaLayer()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        vm.ApplyTechResolution(new TechResolution(TechWithNoViaLayer(), "/t.ctech", TechResolutionSource.WorkspaceDefault, []));
        Assert.False(vm.ViaToolAvailability.CanExecute);
        Assert.NotNull(vm.ViaToolAvailability.DisabledReason);
    }

    [Fact]
    public void ViaToolAvailability_DisabledWithReason_WhenNoTechnologyAtAll()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        Assert.False(vm.ViaToolAvailability.CanExecute);
    }

    // ── Gate 4: single click places a ViaShape with technology defaults, one undo entry ───────────

    [Fact]
    public void ViaTool_SingleClick_PlacesViaShape_WithTechnologyDefaultPadAndDrill_OneUndoEntry()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        vm.ApplyTechResolution(new TechResolution(TechWithViaLayer(600_000, 300_000), "/t.ctech", TechResolutionSource.WorkspaceDefault, []));
        vm.CurrentLayerKey = new LayerKey(1, 0);
        vm.ActiveTool = LayoutEditorViewModel.Tool.Via;

        vm.OnPointerPressed(12_345, 67_890, KeyModifiers.None);

        var via = Assert.IsType<ViaShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(12_345, via.X);
        Assert.Equal(67_890, via.Y);
        Assert.Equal(600_000, via.PadSize);
        Assert.Equal(300_000, via.DrillSize);
        Assert.Equal(new LayerKey(1, 0), via.Layer);

        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();
        Assert.Empty(vm.Model.Shapes);
    }

    [Fact]
    public void ViaTool_NoTechnologyDefaults_FallsBackToHardcodedValues()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        vm.ApplyTechResolution(new TechResolution(TechWithViaLayer(), "/t.ctech", TechResolutionSource.WorkspaceDefault, [])); // pad/drill both 0 -> unset
        vm.ActiveTool = LayoutEditorViewModel.Tool.Via;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);

        var via = Assert.IsType<ViaShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(500_000, via.PadSize);   // R-via-1's own worked example: 0.5 mm
        Assert.Equal(300_000, via.DrillSize); // and 0.3 mm
    }

    [Fact]
    public void ViaTool_Disabled_ClickDoesNothing()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        vm.ApplyTechResolution(new TechResolution(TechWithNoViaLayer(), "/t.ctech", TechResolutionSource.WorkspaceDefault, []));
        vm.ActiveTool = LayoutEditorViewModel.Tool.Via;

        vm.OnPointerPressed(0, 0, KeyModifiers.None);

        Assert.Empty(vm.Model.Shapes);
    }

    // ── Gate 4 (continued): pad/drill editable in Properties Inspector, updates live ───────────────

    [Fact]
    public void PropertiesInspector_EditsViaPadAndDrill_OneUndoEntryEach()
    {
        var model = FreshModel();
        model.Shapes.Add(new ViaShape { Layer = new LayerKey(1, 0), X = 5000, Y = 5000, PadSize = 500_000, DrillSize = 300_000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        vm.OnPointerPressed(5000, 5000, KeyModifiers.None, 1, 40);
        vm.OnPointerReleased(5000, 5000, KeyModifiers.None);
        Assert.Single(vm.SelectedIndices);

        Assert.True(props.ShowVia);

        props.CommitViaPadSizeText("0.8mm");
        Assert.Equal(800_000, ((ViaShape)model.Shapes[0]).PadSize);

        props.CommitViaDrillSizeText("0.4mm");
        Assert.Equal(400_000, ((ViaShape)model.Shapes[0]).DrillSize);

        vm.UndoRedo.Undo();
        Assert.Equal(300_000, ((ViaShape)model.Shapes[0]).DrillSize); // drill edit reverted
        Assert.Equal(800_000, ((ViaShape)model.Shapes[0]).PadSize);   // pad edit (a separate, earlier undo entry) stands
        vm.UndoRedo.Undo();
        Assert.Equal(500_000, ((ViaShape)model.Shapes[0]).PadSize); // both reverted
        Assert.Equal(300_000, ((ViaShape)model.Shapes[0]).DrillSize);
    }

    [Fact]
    public void PropertiesInspector_EditsViaPosition_MovesPadAndDrillTogether_OneUndoEntry()
    {
        var model = FreshModel();
        model.Shapes.Add(new ViaShape { Layer = new LayerKey(1, 0), X = 5000, Y = 5000, PadSize = 500_000, DrillSize = 300_000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);

        vm.OnPointerPressed(5000, 5000, KeyModifiers.None, 1, 40);
        vm.OnPointerReleased(5000, 5000, KeyModifiers.None);
        Assert.True(props.ShowVia);
        Assert.Equal("5", props.ViaXText); // canonical DBU->um formatting at the default resolution (Um, no suffix)

        props.CommitViaXText("10um");
        props.CommitViaYText("-20um");
        var via = (ViaShape)model.Shapes[0];
        Assert.Equal(10_000, via.X);
        Assert.Equal(-20_000, via.Y);
        Assert.Equal(500_000, via.PadSize); // untouched
        Assert.Equal(300_000, via.DrillSize); // untouched

        vm.UndoRedo.Undo();
        Assert.Equal(10_000, ((ViaShape)model.Shapes[0]).X); // Y edit reverted, X edit (earlier entry) stands
        vm.UndoRedo.Undo();
        Assert.Equal(5000, ((ViaShape)model.Shapes[0]).X);
        Assert.Equal(5000, ((ViaShape)model.Shapes[0]).Y);
    }

    // ── Gate 5: annulus rendering — barrel distinguishable from pad, scales with zoom ──────────────

    [Fact]
    public void Via_RendersAsAnnulus_DrillHoleNotFilled_PadIsFilled()
    {
        var model = FreshModel();
        var layerKey = new LayerKey(1, 0);
        model.Shapes.Add(new ViaShape { Layer = layerKey, X = 0, Y = 0, PadSize = 200_000, DrillSize = 80_000 });

        var tech = new Technology
        {
            Name = "T",
            Layers = [new LayerDef { Key = layerKey, Name = "Drill", Color = new Rgba(255, 0, 0), FillOpacity = 1.0, Visible = true }],
        };

        var bb = new Bbox(-120_000, -120_000, 120_000, 120_000);
        var vp = LayoutViewport.ZoomToFit(bb, 200, 200, marginFrac: 0.0);
        var opts = LayoutRenderOptions.Default(LayoutRenderTheme.Light);

        using var surface = SKSurface.Create(new SKImageInfo(200, 200));
        LayoutRenderer.Draw(surface.Canvas, model, tech, vp, opts);

        var center = SamplePixel(surface, 100, 100);           // inside the drill hole
        var annulusPixel = SamplePixel(surface, 100, 100 - 55); // between drill (40k) and pad (100k) radius in world units

        Assert.False(IsRedDominant(center), "the drill hole must not be filled with the layer colour");
        Assert.True(IsRedDominant(annulusPixel), "the pad ring must be filled with the layer colour");
    }

    [Fact]
    public void Via_Annulus_ScalesWithZoom()
    {
        var model = FreshModel();
        var layerKey = new LayerKey(1, 0);
        model.Shapes.Add(new ViaShape { Layer = layerKey, X = 0, Y = 0, PadSize = 200_000, DrillSize = 80_000 });
        var tech = new Technology
        {
            Name = "T",
            Layers = [new LayerDef { Key = layerKey, Name = "Drill", Color = new Rgba(255, 0, 0), FillOpacity = 1.0, Visible = true }],
        };
        var opts = LayoutRenderOptions.Default(LayoutRenderTheme.Light);

        // A point 55k DBU above center sits inside the pad ring (between the 40k drill radius and the
        // 100k pad radius) when zoomed to fit a tight 120k-DBU-radius bbox (the gate-5 test above), but
        // must fall OUTSIDE the pad entirely once zoomed OUT to fit a 5x wider bbox at the SAME pixel
        // count — proving the annulus is drawn at world scale, not a fixed pixel size.
        var tightBb = new Bbox(-120_000, -120_000, 120_000, 120_000);
        var tightVp = LayoutViewport.ZoomToFit(tightBb, 200, 200, marginFrac: 0.0);
        using var tightSurface = SKSurface.Create(new SKImageInfo(200, 200));
        LayoutRenderer.Draw(tightSurface.Canvas, model, tech, tightVp, opts);
        Assert.True(IsRedDominant(SamplePixel(tightSurface, 100, 100 - 55)));

        var wideBb = new Bbox(-600_000, -600_000, 600_000, 600_000);
        var wideVp = LayoutViewport.ZoomToFit(wideBb, 200, 200, marginFrac: 0.0);
        using var wideSurface = SKSurface.Create(new SKImageInfo(200, 200));
        LayoutRenderer.Draw(wideSurface.Canvas, model, tech, wideVp, opts);
        Assert.False(IsRedDominant(SamplePixel(wideSurface, 100, 100 - 55)));
    }

    private static SKColor SamplePixel(SKSurface surface, int x, int y)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    private static bool IsRedDominant(SKColor c) => c.Red > 150 && c.Red > c.Green + 30 && c.Red > c.Blue + 30;

    // ── Gate 8: Convert to Via (R-via-6) ────────────────────────────────────────────────────────────

    private static (LayoutEditorViewModel Vm, LayerKey DrillLayer) SetupForConvert()
    {
        var model = FreshModel();
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var drillLayer = new LayerKey(1, 0);
        var tech = new Technology
        {
            Name = "T",
            // Both layers registered with an EXPLICIT ZOrder (drill drawn on top) so click-selection
            // at a point where a drill and pad circle overlap is deterministic for the tests below —
            // an unregistered layer falls back to FallbackPalette's own ZOrder convention, which is
            // not something a test should depend on.
            Layers =
            [
                new LayerDef { Key = drillLayer, Name = "Drill", Color = new Rgba(0x20, 0x20, 0x20), ZOrder = 2 },
                new LayerDef { Key = new LayerKey(2, 0), Name = "Pad Layer", Color = new Rgba(0x80, 0x40, 0x10), ZOrder = 1 },
            ],
            DefaultViaPadDbu = 500_000,
            Stackup = new Stackup { Layers = [new StackupLayer { Kind = StackupKind.Via, Name = "PTH", DrawingLayers = [drillLayer] }] },
        };
        vm.ApplyTechResolution(new TechResolution(tech, "/t.ctech", TechResolutionSource.WorkspaceDefault, []));
        return (vm, drillLayer);
    }

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, 40);
        vm.OnPointerReleased(wx, wy, mods);
    }

    /// <summary>Marquee-drags a box from just outside <paramref name="minX"/>,<paramref name="minY"/>
    /// to just outside <paramref name="maxX"/>,<paramref name="maxY"/> — the public-API way to select
    /// two overlapping (same-center) circles together, since a plain click can only reach the topmost.</summary>
    private static void MarqueeSelect(LayoutEditorViewModel vm, long minX, long minY, long maxX, long maxY)
    {
        vm.OnPointerPressed(minX - 10_000, minY - 10_000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(maxX + 10_000, maxY + 10_000, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(maxX + 10_000, maxY + 10_000, KeyModifiers.None);
    }

    [Fact]
    public void ConvertToVia_BareCircleOnDrillLayer_UsesDiameterAsBarrel_TechDefaultPad()
    {
        var (vm, drillLayer) = SetupForConvert();
        vm.Model.Shapes.Add(new CircleShape { Layer = drillLayer, Cx = 1000, Cy = 2000, R = 150_000 });
        Click(vm, 1000, 2000);
        Assert.Single(vm.SelectedIndices);

        Assert.True(vm.ConvertToViaAvailability.CanExecute);
        vm.CommitConvertToVia();

        var via = Assert.IsType<ViaShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(1000, via.X);
        Assert.Equal(2000, via.Y);
        Assert.Equal(300_000, via.DrillSize); // 2*R
        Assert.Equal(500_000, via.PadSize);   // technology default (no concentric pad circle selected)
    }

    [Fact]
    public void ConvertToVia_WithConcentricPadCircle_UsesItsDiameterAsPad()
    {
        var (vm, drillLayer) = SetupForConvert();
        var padLayer = new LayerKey(2, 0);
        vm.Model.Shapes.Add(new CircleShape { Layer = padLayer, Cx = 0, Cy = 0, R = 400_000 });   // index 0
        vm.Model.Shapes.Add(new CircleShape { Layer = drillLayer, Cx = 0, Cy = 0, R = 150_000 }); // index 1

        // Both circles share a center, so a click AT the center hits both, and the drill layer's
        // higher ZOrder (SetupForConvert's own explicit setup) wins. A second, shift-held click at a
        // point inside the pad but OUTSIDE the drill's radius hits the pad circle ONLY (no ambiguity —
        // drill can't reach there), adding it to the selection — a robust way to select two concentric
        // circles together without depending on
        // marquee mechanics for identical-center bboxes.
        Click(vm, 0, 0);
        Assert.Equal([1], vm.SelectedIndices);
        Click(vm, 0, 300_000, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);

        Assert.True(vm.ConvertToViaAvailability.CanExecute);
        vm.CommitConvertToVia();

        var via = Assert.IsType<ViaShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(300_000, via.DrillSize);
        Assert.Equal(800_000, via.PadSize); // 2*R of the concentric pad circle
    }

    [Fact]
    public void ConvertToVia_Undo_RestoresOriginalCircleAtOriginalIndex()
    {
        var (vm, drillLayer) = SetupForConvert();
        vm.Model.Shapes.Add(new RectShape { Layer = drillLayer, X1 = -1_000_000, Y1 = -1_000_000, X2 = -900_000, Y2 = -900_000 }); // filler at index 0, far away
        vm.Model.Shapes.Add(new CircleShape { Layer = drillLayer, Cx = 0, Cy = 0, R = 150_000 }); // index 1
        Click(vm, 0, 0);
        Assert.Equal([1], vm.SelectedIndices);

        vm.CommitConvertToVia();
        Assert.Equal(2, vm.Model.Shapes.Count);
        Assert.IsType<ViaShape>(vm.Model.Shapes[1]);

        vm.UndoRedo.Undo();
        Assert.Equal(2, vm.Model.Shapes.Count);
        var restored = Assert.IsType<CircleShape>(vm.Model.Shapes[1]);
        Assert.Equal(150_000, restored.R);
    }

    [Fact]
    public void ConvertToViaAvailability_Disabled_NonConcentricSecondCircle()
    {
        var (vm, drillLayer) = SetupForConvert();
        vm.Model.Shapes.Add(new CircleShape { Layer = drillLayer, Cx = 0, Cy = 0, R = 150_000 });
        vm.Model.Shapes.Add(new CircleShape { Layer = new LayerKey(2, 0), Cx = 900_000, Cy = 900_000, R = 400_000 });
        MarqueeSelect(vm, -500_000, -500_000, 1_400_000, 1_400_000);
        Assert.Equal(2, vm.SelectedIndices.Count);

        Assert.False(vm.ConvertToViaAvailability.CanExecute);
    }

    [Fact]
    public void ConvertToViaAvailability_Disabled_CircleNotOnDrillLayer()
    {
        var (vm, _) = SetupForConvert();
        vm.Model.Shapes.Add(new CircleShape { Layer = new LayerKey(2, 0), Cx = 0, Cy = 0, R = 150_000 });
        Click(vm, 0, 0);
        Assert.Single(vm.SelectedIndices);

        Assert.False(vm.ConvertToViaAvailability.CanExecute);
    }
}
