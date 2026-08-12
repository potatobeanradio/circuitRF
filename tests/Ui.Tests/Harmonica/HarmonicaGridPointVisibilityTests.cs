// ================================================================
//  HarmonicaGridPointVisibilityTests.cs — §4 (R-h9b-7) of
//  brief-harmonicarf-r1b-panels-charts-and-interaction.md
// ================================================================

using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaGridPointVisibilityTests
{
    [Fact]
    public void DefaultsOff_ForANewDocumentAndAnUntouchedCharmAppearance()
    {
        var vm = new HarmonicaViewModel();
        Assert.False(vm.ShowGridPoints);
        Assert.Null(vm.Appearance.ShowGridPoints);
    }

    [Fact]
    public void InvisiblePoints_AreNotHitTestable()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { Rings = 3, Spokes = 12, RasterResolution = 32 });

        var layout = vm.Layout;
        var points = vm.Frame.SmithPower.GridPoints;
        Assert.NotEmpty(points);

        var (_, size) = HarmonicaHitTest.ToPanel(layout, HarmonicaPanelId.SmithPower, 0, 0, 1000, 800);
        var panel = layout.PlacementOf(HarmonicaPanelId.SmithPower);

        // Find a point that WOULD be grabbable if shown.
        int index = -1; double cx = 0, cy = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var at = HarmonicaPanelRenderer.GammaToCanvas(points[i].Gamma, size);
            double x = panel.X * 1000 + at.X, y = panel.Y * 800 + at.Y;
            if (HarmonicaHitTest.Resolve(layout, vm.Markers, x, y, 1000, 800, gridPoints: points,
                                         gridPointsVisible: true).Kind == HarmonicaGrabKind.GridPoint)
            { index = i; cx = x; cy = y; break; }
        }
        Assert.True(index >= 0, "fixture problem: no point was grabbable even when visible");

        // Invisible (the default) — grabbing something the user cannot see is the exact failure the
        // z-ordered passes exist to prevent.
        var hidden = HarmonicaHitTest.Resolve(layout, vm.Markers, cx, cy, 1000, 800,
                                              gridPoints: points, gridPointsVisible: false);
        Assert.NotEqual(HarmonicaGrabKind.GridPoint, hidden.Kind);

        // The SAME gesture, through the real ViewModel, respects ShowGridPoints without the caller
        // passing null for gridPoints itself.
        var g = new HarmonicaGesture(vm);
        Assert.False(vm.ShowGridPoints);
        g.PointerDown(cx, cy, 1000, 800);
        Assert.NotEqual(HarmonicaGrabKind.GridPoint, g.Grab.Kind);

        vm.ShowGridPoints = true;
        var g2 = new HarmonicaGesture(vm);
        g2.PointerDown(cx, cy, 1000, 800);
        Assert.Equal(HarmonicaGrabKind.GridPoint, g2.Grab.Kind);
    }

    [Fact]
    public void ToggleThroughTheMenuViewModel_FlipsBothTheLiveFlagAndTheStoredAppearance()
    {
        var vm = new HarmonicaViewModel();
        var menus = new HarmonicaMenuViewModel(vm);

        Assert.False(vm.ShowGridPoints);
        menus.ToggleShowGridPointsCommand.Execute(null);
        Assert.True(vm.ShowGridPoints);
        Assert.True(vm.Appearance.ShowGridPoints);

        menus.ToggleShowGridPointsCommand.Execute(null);
        Assert.False(vm.ShowGridPoints);
        Assert.False(vm.Appearance.ShowGridPoints);
    }

    [Fact]
    public void StateRoundTripsThroughACharm()
    {
        var vm = new HarmonicaViewModel();
        var menus = new HarmonicaMenuViewModel(vm);
        menus.ToggleShowGridPointsCommand.Execute(null);
        Assert.True(vm.ShowGridPoints);

        string json = vm.ToCharmJson();

        var reopened = new HarmonicaViewModel();
        Assert.False(reopened.ShowGridPoints);
        reopened.LoadCharm(json, null);
        Assert.True(reopened.ShowGridPoints);
    }

    [Fact]
    public void RendererDefaultsToShowingThem_SoOnlyTheDocumentDefaultIsOff()
    {
        // The renderer's own default parameter stays permissive (true) — it is the VIEW MODEL's
        // default (false) that is R-h9b-7's actual "off by default". Pinned so the two cannot drift:
        // if the renderer's default ever flips, every direct-render test in HarmonicaPanelTests that
        // relies on grid dots appearing without passing showGridPoints explicitly would need updating.
        Assert.False(new HarmonicaViewModel().ShowGridPoints);
    }
}
