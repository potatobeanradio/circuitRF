using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// Owner report: "the X: Y: readout indicator is always tracing the mouse coordinates, even when
// geometry snap is on. Change it to the snapped coordinate when geometry snapping is turned on."
//
// Driven through the same SetCursorWorld-then-OnPointerMoved order LayoutCanvas.OnPointerMoved uses
// (CursorWorldChanged is raised BEFORE the view model sees the move), because that ordering is
// exactly what made the naive fix report the PREVIOUS tick's candidate.

public class LayoutCursorReadoutSnapTests
{
    private const long SnapTol = 3000;

    private static LayoutView ModelWithACornerAtOrigin() => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = 1000,
        Shapes       = { new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 50_000, Y2 = 50_000 } },
    };

    private static LayoutEditorViewModel SelectVm(LayoutView model) =>
        new(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

    /// <summary>Reproduces one canvas pointer-move tick in the real order.</summary>
    private static void MoveTo(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = KeyModifiers.None)
    {
        vm.SetCursorWorld(wx, wy);
        vm.OnPointerMoved(wx, wy, leftDown: false, mods, hitTolDbu: 40, pixelDbu: 0, snapTolDbu: SnapTol);
    }

    [Fact]
    public void HoveringNearAFeature_WithSnapOn_ReadsTheSNAPPEDPoint_NotTheCursor()
    {
        var vm = SelectVm(ModelWithACornerAtOrigin());

        // (-2000, -2000) is within SnapTol of the rect's (0,0) corner but is not itself on anything.
        MoveTo(vm, -2000, -2000);

        Assert.True(vm.CursorReadoutIsSnapped);
        // 0 DBU at 1000 DBU/µm is 0 µm; the raw cursor would have read -2 µm.
        Assert.Equal("0 µm", vm.CursorXText);
        Assert.Equal("0 µm", vm.CursorYText);
    }

    [Fact]
    public void TheSameHover_WithSnapOff_StillReadsTheRawCursor()
    {
        var vm = SelectVm(ModelWithACornerAtOrigin());
        vm.GeometrySnapEnabled = false;

        MoveTo(vm, -2000, -2000);

        Assert.False(vm.CursorReadoutIsSnapped);
        Assert.Equal("-2 µm", vm.CursorXText);
        Assert.Equal("-2 µm", vm.CursorYText);
    }

    [Fact]
    public void HoveringNowhereNearAnything_WithSnapOn_StillReadsTheRawCursor()
    {
        var vm = SelectVm(ModelWithACornerAtOrigin());

        // Far outside SnapTol of every feature of the rect.
        MoveTo(vm, -40_000, -40_000);

        Assert.False(vm.CursorReadoutIsSnapped);
        Assert.Equal("-40 µm", vm.CursorXText);
        Assert.Equal("-40 µm", vm.CursorYText);
    }

    /// <summary>R-dup-2 retired R-snp-11's Alt escape hatch — the geometry-snap toggle (S / F3) is the
    /// "place freely" control now, and it is persistent and visible in the toolbar rather than
    /// momentary. The readout claim is unchanged: with snap off it reports the raw cursor.</summary>
    [Fact]
    public void TheGeometrySnapToggle_MakesTheReadoutFallBackToTheRawCursor()
    {
        var vm = SelectVm(ModelWithACornerAtOrigin());

        MoveTo(vm, -2000, -2000);
        Assert.True(vm.CursorReadoutIsSnapped);           // control: it WAS snapping

        vm.GeometrySnapEnabled = false;
        MoveTo(vm, -2000, -2000);
        Assert.False(vm.CursorReadoutIsSnapped);
        Assert.Equal("-2 µm", vm.CursorXText);
    }

    /// <summary>The retirement itself, pinned: Alt is inert for the readout now.</summary>
    [Fact]
    public void AltNoLongerSuppressesSnap_SoTheReadoutStaysSnapped()
    {
        var vm = SelectVm(ModelWithACornerAtOrigin());

        MoveTo(vm, -2000, -2000, KeyModifiers.Alt);

        Assert.True(vm.CursorReadoutIsSnapped);
    }

    /// <summary>The ordering guard. The canvas stores the raw point first and only then hands the
    /// move to the view model, so a readout refreshed only by SetCursorWorld reports the candidate
    /// resolved for the PREVIOUS position — correct-looking while hovering still, visibly wrong the
    /// moment the pointer moves off a feature.</summary>
    [Fact]
    public void MovingOffAFeature_UpdatesTheReadoutOnTheSameTick_NotOneTickLate()
    {
        var vm = SelectVm(ModelWithACornerAtOrigin());

        MoveTo(vm, -2000, -2000);
        Assert.Equal("0 µm", vm.CursorXText);

        MoveTo(vm, -40_000, -40_000);
        Assert.Equal("-40 µm", vm.CursorXText);           // not still "0 µm" from the last candidate
    }

    [Fact]
    public void TogglingSnapOff_RelabelsTheReadoutImmediately_WithNoPointerMove()
    {
        var vm = SelectVm(ModelWithACornerAtOrigin());

        MoveTo(vm, -2000, -2000);
        Assert.Equal("0 µm", vm.CursorXText);

        vm.GeometrySnapEnabled = false;                    // RecomputeSnapStateImmediate's path
        Assert.Equal("-2 µm", vm.CursorXText);
    }

    [Fact]
    public void LeavingTheCanvas_ClearsTheReadout_EvenWhileACandidateIsStillHeld()
    {
        var vm = SelectVm(ModelWithACornerAtOrigin());

        MoveTo(vm, -2000, -2000);
        vm.SetCursorWorld(null, null);

        Assert.Equal("—", vm.CursorXText);
        Assert.Equal("—", vm.CursorYText);
        Assert.False(vm.CursorReadoutIsSnapped);
    }

    [Fact]
    public void ChangingTheDisplayUnit_RelabelsASnappedReadout_WithNoPointerMove()
    {
        var vm = SelectVm(ModelWithACornerAtOrigin());

        // Hover near the FAR corner (50 µm, 50 µm) so the value is non-zero and the unit change shows.
        MoveTo(vm, 48_000, 48_000);
        Assert.True(vm.CursorReadoutIsSnapped);
        Assert.Equal("50 µm", vm.CursorXText);

        vm.DisplayUnit = LayoutUnit.Nm;
        Assert.Equal("50000 nm", vm.CursorXText);
    }
}
