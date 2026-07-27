using System.Collections.Generic;
using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1c gates 6, 7, 12, 13, 14: docs/sonnet-briefs/brief-L1c-selection-and-properties.md
// "Read first" — hit-testing is a screen-to-world feature; a pixel tolerance is never a fixed DBU
// number. Gates 13/14 route through LayoutViewport.ScreenToWorldX/Y exactly as LayoutCanvas would,
// mirroring the pattern LayoutL1FixTests already established for this class of bug.

public class LayoutSelectionTests
{
    private static LayoutView FreshModel(long snapDbu = 1000) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
    };

    private static LayoutEditorViewModel SelectVm(LayoutView model)
    {
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        return vm;
    }

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    private static void Drag(LayoutEditorViewModel vm, double x1, double y1, double x2, double y2, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(x1, y1, mods, 1, tolDbu);
        vm.OnPointerMoved(x2, y2, true, mods, tolDbu);
        vm.OnPointerReleased(x2, y2, mods);
    }

    // ── Gate 6: overlap cycling ────────────────────────────────────────────────

    [Fact]
    public void FiveStackedShapes_FiveClicksVisitAllInOrder_SixthWraps()
    {
        var model = FreshModel();
        for (int i = 0; i < 5; i++)
            model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = SelectVm(model);

        var seen = new List<int>();
        for (int click = 0; click < 5; click++)
        {
            Click(vm, 5000, 5000);
            seen.Add(Assert.Single(vm.SelectedIndices));
        }
        // All five distinct shapes visited, in list order (ties broken by ascending index since
        // same layer/area/point — deterministic).
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, seen);

        Click(vm, 5000, 5000); // sixth click wraps to the first
        Assert.Equal(0, Assert.Single(vm.SelectedIndices));
    }

    [Fact]
    public void StatusReadout_ReportsPositionOfStackCount_Correctly()
    {
        var model = FreshModel();
        for (int i = 0; i < 3; i++)
            model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);
        Assert.Contains("1 of 3", vm.SelectionStatusText);
        Click(vm, 5000, 5000);
        Assert.Contains("2 of 3", vm.SelectionStatusText);
        Click(vm, 5000, 5000);
        Assert.Contains("3 of 3", vm.SelectionStatusText);
    }

    [Fact]
    public void MovingPointerBeyondThreshold_ThenClicking_RebuildsFromTop()
    {
        var model = FreshModel();
        for (int i = 0; i < 3; i++)
            model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);
        Click(vm, 5000, 5000); // now at index 1

        // Pointer moves far away (well beyond the tolerance) as a hover, no button down.
        vm.OnPointerMoved(500_000, 500_000, false, KeyModifiers.None, 40);

        Click(vm, 5000, 5000); // back at the original point — cache was invalidated, rebuilds fresh
        Assert.Equal(0, Assert.Single(vm.SelectedIndices));
    }

    [Fact]
    public void ModelMutationMidCycle_InvalidatesCache()
    {
        var model = FreshModel();
        for (int i = 0; i < 3; i++)
            model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = SelectVm(model);

        Click(vm, 5000, 5000); // index 0

        // Any model mutation — draw a new shape via the undo stack — invalidates the cycle cache.
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;
        vm.OnPointerPressed(50_000, 50_000, KeyModifiers.None);
        vm.OnPointerMoved(60_000, 60_000, true, KeyModifiers.None);
        vm.OnPointerReleased(60_000, 60_000, KeyModifiers.None);
        vm.ActiveTool = LayoutEditorViewModel.Tool.Select;

        Click(vm, 5000, 5000); // rebuilds fresh rather than continuing the old cycle
        Assert.Equal(0, Assert.Single(vm.SelectedIndices));
    }

    [Fact]
    public void ClickOnEmptySpace_Clears()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);
        Assert.Single(vm.SelectedIndices);

        Click(vm, 500_000, 500_000); // far from any shape
        Assert.Empty(vm.SelectedIndices);
    }

    // ── Escape key: activates Select, then clears selection — mirrors the Symbol Editor ────

    [Fact]
    public void Escape_WhileADrawToolIsActive_SwitchesToSelectTool()
    {
        var model = FreshModel();
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Rect };

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Equal(LayoutEditorViewModel.Tool.Select, vm.ActiveTool);
    }

    [Fact]
    public void Escape_MidPolygonDraw_CancelsTheDraw_AndSwitchesToSelectTool()
    {
        var model = FreshModel();
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Polygon };

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerPressed(1000, 0, KeyModifiers.None);
        Assert.NotNull(vm.Overlay.InProgressPrimitive);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Empty(model.Shapes);
        Assert.Null(vm.Overlay.InProgressPrimitive);
        Assert.Equal(LayoutEditorViewModel.Tool.Select, vm.ActiveTool);
    }

    [Fact]
    public void Escape_WhileSelectToolIdle_ClearsTheSelection()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);
        Assert.Single(vm.SelectedIndices);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Empty(vm.SelectedIndices);
        Assert.Equal(LayoutEditorViewModel.Tool.Select, vm.ActiveTool); // stays on Select — already was
    }

    [Fact]
    public void Escape_MidMoveDrag_CancelsTheDrag_ButKeepsTheExistingSelection()
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        model.Shapes.Add(rect);
        var vm = SelectVm(model);

        vm.OnPointerPressed(5000, 5000, KeyModifiers.None, 1, 40); // selects + arms a move drag
        vm.OnPointerMoved(5000 + 3000, 5000, true, KeyModifiers.None, 40);
        Assert.NotEmpty(vm.Overlay.DragOverrides);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Equal(0, rect.X1); // never committed
        Assert.Empty(vm.Overlay.DragOverrides);
        Assert.Single(vm.SelectedIndices); // selection survives — only the drag was cancelled
        Assert.Equal(LayoutEditorViewModel.Tool.Select, vm.ActiveTool);
    }

    [Fact]
    public void Escape_MidMarqueeDrag_CancelsTheMarquee_KeepsPriorSelectionUntouched()
    {
        var (_, vm) = MarqueeFixture();
        Click(vm, 5000, 5000); // pre-select A
        Assert.Equal(0, Assert.Single(vm.SelectedIndices));

        // Shift-drag on empty space preserves the pre-existing selection while the marquee is live
        // (a plain, unmodified press on empty space would clear it immediately at press time).
        vm.OnPointerPressed(500_000, 500_000, KeyModifiers.Shift, 1, 40);
        vm.OnPointerMoved(600_000, 600_000, true, KeyModifiers.Shift, 40);
        Assert.NotNull(vm.Overlay.Marquee);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.Null(vm.Overlay.Marquee);
        Assert.Equal(0, Assert.Single(vm.SelectedIndices)); // A is still selected — only the marquee was cancelled
        Assert.Equal(LayoutEditorViewModel.Tool.Select, vm.ActiveTool);
    }

    // ── Gate 7: marquee ────────────────────────────────────────────────────────

    private static (LayoutView Model, LayoutEditorViewModel Vm) MarqueeFixture()
    {
        var model = FreshModel();
        // A: fully enclosed by the marquee rect below.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        // B: crosses the marquee's right edge — not fully enclosed, but intersects it.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 10_500, Y1 = 0, X2 = 20_000, Y2 = 10_000 });
        // C: far away, outside both.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 30_000, Y1 = 30_000, X2 = 40_000, Y2 = 40_000 });
        return (model, SelectVm(model));
    }

    [Fact]
    public void LeftToRight_SelectsOnlyFullyEnclosedShapes()
    {
        var (_, vm) = MarqueeFixture();
        Drag(vm, -5000, -5000, 11_000, 11_000); // left-to-right: press.X < release.X

        Assert.Equal(0, Assert.Single(vm.SelectedIndices)); // only A
    }

    [Fact]
    public void RightToLeft_AlsoSelectsIntersectingShapes()
    {
        var (_, vm) = MarqueeFixture();
        Drag(vm, 11_000, 11_000, -5000, -5000); // right-to-left over the SAME physical rect

        var sel = new List<int>(vm.SelectedIndices);
        sel.Sort();
        Assert.Equal(new[] { 0, 1 }, sel); // A (enclosed) AND B (crossing)
    }

    [Fact]
    public void ShiftMarquee_AddsToExistingSelection()
    {
        var (_, vm) = MarqueeFixture();
        Click(vm, 35_000, 35_000); // select C directly
        Assert.Equal(2, Assert.Single(vm.SelectedIndices));

        Drag(vm, -5000, -5000, 11_000, 11_000, KeyModifiers.Shift); // enclose A, Shift = add

        var sel = new List<int>(vm.SelectedIndices);
        sel.Sort();
        Assert.Equal(new[] { 0, 2 }, sel);
    }

    [Fact]
    public void CtrlMarquee_TogglesEachHitShape()
    {
        var (_, vm) = MarqueeFixture();
        Click(vm, 5000, 5000); // select A directly
        Assert.Equal(0, Assert.Single(vm.SelectedIndices));

        // Ctrl-marquee enclosing BOTH A and B: A (already selected) toggles OFF, B toggles ON.
        Drag(vm, -5000, -5000, 21_000, 11_000, KeyModifiers.Control);

        Assert.Equal(1, Assert.Single(vm.SelectedIndices));
    }

    // ── Gate 12: selection survives undo/redo sensibly ────────────────────────

    [Fact]
    public void UndoingADelete_NeverLeavesAStaleSelectedIndex()
    {
        var model = FreshModel();
        for (int i = 0; i < 3; i++)
            model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = i * 20_000, Y1 = 0, X2 = i * 20_000 + 10_000, Y2 = 10_000 });
        var vm = SelectVm(model);

        vm.SelectAllCommand.Execute(null);
        Assert.Equal(3, vm.SelectedIndices.Count);

        vm.OnKeyDown(Key.Delete, KeyModifiers.None);
        Assert.Empty(model.Shapes);
        Assert.Empty(vm.SelectedIndices);

        vm.UndoRedo.Undo();
        Assert.Equal(3, model.Shapes.Count);
        Assert.Empty(vm.SelectedIndices); // deletion's selection was cleared, not restored — but never stale

        // No out-of-range access: every index (there are none) resolves inside Model.Shapes.
        foreach (var idx in vm.SelectedIndices)
            Assert.InRange(idx, 0, model.Shapes.Count - 1);
    }

    // ── Gate 13: screen-to-world coverage on both starter technologies ────────

    public static IEnumerable<object[]> StarterTechs()
    {
        yield return new object[] { "Pcb2Layer", StarterTechnologies.Pcb2Layer() };
        yield return new object[] { "MmicGaAs", StarterTechnologies.MmicGaAs() };
    }

    [Theory]
    [MemberData(nameof(StarterTechs))]
    public void ScreenPixelClick_ThroughCanvasConversion_SelectsInsideAndNearEdge_NotFarOutside(string name, Technology tech)
    {
        const double width = 1200, height = 800;
        var model = new LayoutView
        {
            DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
            DisplayUnit  = tech.DefaultDisplayUnit,
            SnapDbu      = tech.DefaultSnapDbu,
        };
        var vp = LayoutViewport.Default(width, height, model.SnapDbu, model.DbuPerMicron);

        long half = model.SnapDbu * 15;
        long cx = (long)System.Math.Round(vp.ScreenToWorldX(width / 2));
        long cy = (long)System.Math.Round(vp.ScreenToWorldY(height / 2));
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = cx - half, Y1 = cy - half, X2 = cx + half, Y2 = cy + half });

        var vm = SelectVm(model);
        long tolDbu = (long)System.Math.Round(4.0 / vp.Zoom);

        // Inside: click at screen center — through the canvas's own ScreenToWorld conversion.
        ClickScreen(vm, vp, width / 2, height / 2, tolDbu);
        Assert.True(vm.SelectedIndices.Count == 1, $"{name}: click at screen center should select");
        vm.DeselectAllCommand.Execute(null);

        // ~4 screen px past the right edge.
        double edgeSx = vp.WorldToScreenX(cx + half);
        ClickScreen(vm, vp, edgeSx + 3.0, height / 2, tolDbu);
        Assert.True(vm.SelectedIndices.Count == 1, $"{name}: click ~4px past the edge should still select");
        vm.DeselectAllCommand.Execute(null);

        // Well outside — 200 screen px past the edge.
        ClickScreen(vm, vp, edgeSx + 200, height / 2, tolDbu);
        Assert.True(vm.SelectedIndices.Count == 0, $"{name}: click well outside must not select");
    }

    private static void ClickScreen(LayoutEditorViewModel vm, LayoutViewport vp, double sx, double sy, long tolDbu)
    {
        double wx = vp.ScreenToWorldX(sx), wy = vp.ScreenToWorldY(sy);
        vm.OnPointerPressed(wx, wy, KeyModifiers.None, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, KeyModifiers.None);
    }

    // ── Gate 14: tolerance scales with zoom (never cached, never derived from SnapDbu) ────

    [Fact]
    public void HitTolerance_DiffersByOrdersOfMagnitude_BetweenLowAndHighZoom_AndBothStillHit()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = -1_000_000_000, Y1 = -1_000_000_000, X2 = 1_000_000_000, Y2 = 1_000_000_000 });

        const double lowZoom = 1e-6;   // far zoomed out -> huge DBU tolerance for the same 4 px
        const double highZoom = 1e3;   // far zoomed in -> tiny DBU tolerance for the same 4 px

        long tolLow  = (long)System.Math.Round(4.0 / lowZoom);
        long tolHigh = (long)System.Math.Round(4.0 / highZoom);
        Assert.True(tolLow / (double)tolHigh > 1000, $"tolLow={tolLow} tolHigh={tolHigh}");

        long edgeWx = 1_000_000_000;
        long clickLow  = edgeWx + (long)System.Math.Round(4.0 / lowZoom * 0.9);
        long clickHigh = edgeWx + (long)System.Math.Round(4.0 / highZoom * 0.9);

        Assert.Single(LayoutHitTest.HitStack(model, null, clickLow, 0, tolLow));
        Assert.Single(LayoutHitTest.HitStack(model, null, clickHigh, 0, tolHigh));

        // The high-zoom (tiny) tolerance must NOT also cover the low-zoom click distance — proof
        // the tolerance genuinely has to scale, not just happen to be "big enough" once.
        Assert.Empty(LayoutHitTest.HitStack(model, null, clickLow, 0, tolHigh));
    }
}
