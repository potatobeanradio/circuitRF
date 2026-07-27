using System.Collections.Generic;
using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── L1 fix gates: default viewport is drawable + a degenerate drag never yields nothing ────
// docs/sonnet-briefs/brief-L1-fix-clear-and-default-zoom.md Bug 2. Zoom is device pixels per DBU
// (LayoutCanvas.Zoom1To1); a fixed default of 1.0 meant 1 screen pixel per NANOMETRE at the default
// 1000 DBU/µm, which made a PCB technology's 1-mil (25,400 DBU) snap step wider than the entire
// visible canvas — every pointer position snapped to the same grid cell and no shape could ever be
// drawn. World-coordinate unit tests (feeding OnPointerPressed/Moved/Released world DBU directly)
// structurally cannot catch this class of bug — it lives entirely in the screen<->world gap those
// tests skip over — which is why the tests below route through LayoutViewport.ScreenToWorld*.

public class LayoutL1FixTests
{
    public static IEnumerable<object[]> StarterTechSnapSteps()
    {
        yield return new object[] { "Pcb2Layer", StarterTechnologies.Pcb2Layer().DefaultSnapDbu };
        yield return new object[] { "MmicGaAs",  StarterTechnologies.MmicGaAs().DefaultSnapDbu };
    }

    // ── The test that would have caught the bug ─────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(StarterTechSnapSteps))]
    public void DefaultViewport_TwoScreenPointsApart_MapToDistinctSnappedWorldCells(string techName, long snapDbu)
    {
        const double width = 1200, height = 800;
        var vp = LayoutViewport.Default(width, height, snapDbu, LayoutUnits.DefaultDbuPerMicron);

        double wx1 = vp.ScreenToWorldX(400), wy1 = vp.ScreenToWorldY(400);
        double wx2 = vp.ScreenToWorldX(700), wy2 = vp.ScreenToWorldY(400); // 300 px apart, per the brief

        var (sx1, sy1) = LayoutSnapping.SnapPoint(wx1, wy1, snapDbu, suspend: false);
        var (sx2, sy2) = LayoutSnapping.SnapPoint(wx2, wy2, snapDbu, suspend: false);

        Assert.True(sx1 != sx2 || sy1 != sy2,
            $"{techName}: two screen points 300px apart both snapped to the same world cell " +
            "at the default viewport — this is exactly the old zoom=1.0-device-pixel-per-DBU bug " +
            "(a PCB snap step several times wider than the whole visible canvas).");
    }

    [Theory]
    [MemberData(nameof(StarterTechSnapSteps))]
    public void DefaultViewport_GridIsVisible_AtOrAboveTheEightPixelThreshold(string techName, long snapDbu)
    {
        const double width = 1200, height = 800;
        var vp = LayoutViewport.Default(width, height, snapDbu, LayoutUnits.DefaultDbuPerMicron);

        long? pitch = LayoutGridMath.ComputeGridPitch(snapDbu, vp.Zoom);
        Assert.True(pitch is not null, $"{techName}: no grid pitch could be computed at the default viewport's zoom");
        double pixelSpacing = pitch!.Value * vp.Zoom;
        Assert.True(pixelSpacing >= 8.0 - 1e-6, $"{techName}: grid pixel spacing {pixelSpacing:F2}px is below the 8px threshold");
    }

    // ── End-to-end through screen coordinates (not world coordinates) ───────────────────────

    [Fact]
    public void EndToEnd_ScreenCoordinates_PcbTech_DrawsRectShape_AndClearsIsEmpty()
    {
        long snapDbu = StarterTechnologies.Pcb2Layer().DefaultSnapDbu;
        var model = new LayoutView
        {
            DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
            DisplayUnit  = LayoutUnit.Mil,
            SnapDbu      = snapDbu,
            AngleMode    = AngleMode.AnyAngle,
        };
        var vm = new LayoutEditorViewModel(model);
        vm.ActiveTool = LayoutEditorViewModel.Tool.Rect;

        const double width = 1200, height = 800;
        var vp = LayoutViewport.Default(width, height, snapDbu, model.DbuPerMicron);

        // Two screen points, exactly as LayoutCanvas would hand the VM after its own ScreenToWorld
        // conversion — the canvas never gives the VM raw screen coordinates directly.
        double wx1 = vp.ScreenToWorldX(400), wy1 = vp.ScreenToWorldY(400);
        double wx2 = vp.ScreenToWorldX(700), wy2 = vp.ScreenToWorldY(550);

        Assert.True(vm.IsEmpty);

        vm.OnPointerPressed(wx1, wy1, KeyModifiers.None);
        vm.OnPointerMoved(wx2, wy2, leftDown: true, KeyModifiers.None);
        vm.OnPointerReleased(wx2, wy2, KeyModifiers.None);

        var shape = Assert.Single(vm.Model.Shapes);
        Assert.IsType<RectShape>(shape);
        Assert.False(vm.IsEmpty);
    }

    // ── Minimum-size fallback ────────────────────────────────────────────────────────────────

    [Fact]
    public void SubSnapStepDrag_NonDegenerateRaw_YieldsOneSnapStepRect_NotNull()
    {
        const long snapDbu = 1000;
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = snapDbu, AngleMode = AngleMode.AnyAngle };
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Rect };

        // At zoom = 1 device px per DBU, a 3px drag is a 3-DBU raw drag — far under the 1000-DBU
        // snap step, so both endpoints snap to the very same grid cell. The drag is real (the
        // pointer genuinely moved); the fix must not silently produce nothing.
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(3, 3, leftDown: true, KeyModifiers.None);
        vm.OnPointerReleased(3, 3, KeyModifiers.None);

        var rect = Assert.IsType<RectShape>(Assert.Single(vm.Model.Shapes));
        Assert.Equal(snapDbu, rect.X2 - rect.X1);
        Assert.Equal(snapDbu, rect.Y2 - rect.Y1);
    }

    [Fact]
    public void ZeroLengthClick_NoRawMovement_StillYieldsNoShape()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000, AngleMode = AngleMode.AnyAngle };
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Rect };

        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(0, 0, leftDown: true, KeyModifiers.None);
        vm.OnPointerReleased(0, 0, KeyModifiers.None);

        Assert.Empty(vm.Model.Shapes);
        Assert.True(vm.IsEmpty);
    }
}
