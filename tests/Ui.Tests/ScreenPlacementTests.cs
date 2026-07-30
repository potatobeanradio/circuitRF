using System.Collections.Generic;
using CircuitRF.Ui.Docking;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// brief-dock-layout-persistence.md §4 gates 6–11 — the off-screen problem, driven against SYNTHETIC
/// screens so the whole thing is exercised with no display attached.
///
/// <para>Every assertion here checks the TITLE BAR, not "the window intersects a screen". An
/// intersects-any-screen test passes for a window whose draggable strip is off-screen, which is
/// precisely the unrecoverable failure this code exists to prevent.</para>
/// </summary>
public sealed class ScreenPlacementTests
{
    private static readonly ScreenRect Fhd = new(0, 0, 1920, 1040);      // 1080 minus a 40px taskbar
    private static readonly List<ScreenRect> SingleScreen = [Fhd];
    private static readonly List<ScreenRect> Nothing = [];

    private static void AssertTitleBarInsideAWorkingArea(ScreenRect w, IReadOnlyList<ScreenRect> screens)
    {
        var bar = ScreenPlacement.TitleBarOf(w);
        foreach (var s in screens)
            if (s.Contains(bar)) return;
        Assert.Fail($"Title bar {bar} is not inside any working area — the window cannot be dragged back.");
    }

    // ── Gate 6 — off-screen, headless ─────────────────────────────────────────

    [Fact]
    public void Gate6_WindowSavedFarRightOfTheOnlyScreen_IsRelocatedOntoIt_TitleBarReachable()
    {
        var saved  = new ScreenRect(3000, 200, 640, 480);
        var placed = ScreenPlacement.Place(saved, SingleScreen, Nothing);

        Assert.NotEqual(saved, placed);
        AssertTitleBarInsideAWorkingArea(placed, SingleScreen);
    }

    [Fact]
    public void Gate6_IntersectsAScreenButTitleBarDoesNot_IsStillRelocated()
    {
        // Most of the window overlaps the screen; only the title bar sits above the working area.
        // An "intersects any screen" test would accept this and leave it ungrabbable.
        var saved = new ScreenRect(400, -40, 640, 480);

        Assert.False(ScreenPlacement.IsTitleBarReachable(saved, SingleScreen, strict: true));

        var placed = ScreenPlacement.Place(saved, SingleScreen, Nothing);
        AssertTitleBarInsideAWorkingArea(placed, SingleScreen);
    }

    [Fact]
    public void WorkingAreaIsUsedNotFullBounds_AWindowUnderTheTaskbarIsRelocated()
    {
        // Inside the 1920×1080 physical screen, but below the 1040-tall WORKING area — i.e. under
        // the taskbar. R-dock-6 step 1: that window is effectively lost.
        var saved  = new ScreenRect(100, 1050, 400, 300);
        var placed = ScreenPlacement.Place(saved, SingleScreen, Nothing);

        Assert.NotEqual(saved, placed);
        AssertTitleBarInsideAWorkingArea(placed, SingleScreen);
    }

    // ── Gate 7 — negative coordinates (a since-removed left-hand monitor) ─────

    [Fact]
    public void Gate7_WindowSavedAtNegativeX_IsRelocated()
    {
        var saved  = new ScreenRect(-1200, 100, 500, 400);
        var placed = ScreenPlacement.Place(saved, SingleScreen, Nothing);

        Assert.NotEqual(saved, placed);
        AssertTitleBarInsideAWorkingArea(placed, SingleScreen);
        Assert.True(placed.X >= Fhd.X);
    }

    // ── Gate 8 — oversized ────────────────────────────────────────────────────

    [Fact]
    public void Gate8_OversizedWindow_IsClampedToTheWorkingArea()
    {
        var saved  = new ScreenRect(-500, -500, 3000, 2000);
        var placed = ScreenPlacement.Place(saved, SingleScreen, Nothing);

        Assert.True(placed.Width  <= Fhd.Width  + 1e-6, $"width {placed.Width} exceeds working area");
        Assert.True(placed.Height <= Fhd.Height + 1e-6, $"height {placed.Height} exceeds working area");
        Assert.True(Fhd.Contains(placed), "a clamped window should sit wholly inside the working area");
    }

    // ── Gate 9 — cascade ──────────────────────────────────────────────────────

    [Fact]
    public void Gate9_ThreeRelocatedWindows_DoNotLandAtIdenticalPositions()
    {
        var saved  = new ScreenRect(4000, 4000, 400, 300);   // all three saved at the same lost spot
        var placed = new List<ScreenRect>();

        for (int i = 0; i < 3; i++)
            placed.Add(ScreenPlacement.Place(saved, SingleScreen, placed));

        Assert.Equal(3, placed.Count);
        Assert.NotEqual((placed[0].X, placed[0].Y), (placed[1].X, placed[1].Y));
        Assert.NotEqual((placed[1].X, placed[1].Y), (placed[2].X, placed[2].Y));
        Assert.NotEqual((placed[0].X, placed[0].Y), (placed[2].X, placed[2].Y));

        foreach (var w in placed) AssertTitleBarInsideAWorkingArea(w, SingleScreen);
    }

    // ── Gate 10 — scaling (R-dock-7) ──────────────────────────────────────────

    [Fact]
    public void Gate10_PositionSavedUnder2xScaling_RestoresToTheSameLogicalPlace_Under1x()
    {
        // The 4K/2× machine reported the window at device (2400, 600); logical is half that.
        const double savedScaling = 2.0;
        double logicalX = ScreenPlacement.DeviceToLogical(2400, savedScaling);
        double logicalY = ScreenPlacement.DeviceToLogical(600,  savedScaling);
        Assert.Equal(1200, logicalX, 6);
        Assert.Equal(300,  logicalY, 6);

        // On the 1× machine the same logical coordinates are the device coordinates.
        Assert.Equal(1200, ScreenPlacement.LogicalToDevice(logicalX, 1.0), 6);
        Assert.Equal(300,  ScreenPlacement.LogicalToDevice(logicalY, 1.0), 6);

        // Storing the raw device pixels instead would have put it at 2400 — off a 1920-wide screen.
        Assert.True(logicalX + 400 <= Fhd.Right);
    }

    [Fact]
    public void Gate10_LogicalSizeSurvivesADisplayChange_WhenItStillFits()
    {
        // Saved on a 3840×2160 physical / 2× display: 800×600 LOGICAL. Restored on a 1920×1040
        // logical screen, it fits and must come back at exactly the same apparent size.
        var saved  = new ScreenRect(100, 100, 800, 600);
        var placed = ScreenPlacement.Place(saved, SingleScreen, Nothing);

        Assert.Equal(saved, placed);
    }

    [Fact]
    public void WorkingAreaToLogical_DividesEveryComponentByTheScaling()
    {
        var device  = new ScreenRect(0, 0, 3840, 2080);
        var logical = ScreenPlacement.WorkingAreaToLogical(device, 2.0);
        Assert.Equal(new ScreenRect(0, 0, 1920, 1040), logical);
    }

    // ── Gate 11 — same-configuration fast path ────────────────────────────────

    [Fact]
    public void Gate11_UnchangedScreenConfiguration_RestoresExactly_NoNudge()
    {
        var savedScreens = new List<ScreenRect> { Fhd };
        Assert.True(ScreenPlacement.SameConfiguration(savedScreens, SingleScreen));

        var saved  = new ScreenRect(310, 220, 640, 480);
        var placed = ScreenPlacement.Place(saved, SingleScreen, Nothing, sameConfiguration: true);

        Assert.Equal(saved, placed);   // byte-identical — relocation is a repair, never a policy
    }

    [Fact]
    public void Gate11_AValidWindow_IsUnchangedEvenWhenTheConfigurationDiffers()
    {
        var saved  = new ScreenRect(310, 220, 640, 480);
        var placed = ScreenPlacement.Place(saved, SingleScreen, Nothing, sameConfiguration: false);
        Assert.Equal(saved, placed);
    }

    [Fact]
    public void Gate19_SameConfigurationDoesNotExemptAnOffScreenWindowFromValidation()
    {
        // R-dock-8 lets an unchanged setup restore verbatim — it must NOT become a licence to skip
        // validation, or a window saved off-screen on this very machine would stay lost.
        var saved  = new ScreenRect(3000, 200, 640, 480);
        var placed = ScreenPlacement.Place(saved, SingleScreen, Nothing, sameConfiguration: true);

        Assert.NotEqual(saved, placed);
        AssertTitleBarInsideAWorkingArea(placed, SingleScreen);
    }

    [Fact]
    public void SameConfiguration_DetectsCountAndGeometryChanges()
    {
        var two = new List<ScreenRect> { Fhd, new(1920, 0, 1920, 1040) };
        Assert.False(ScreenPlacement.SameConfiguration(two, SingleScreen));
        Assert.False(ScreenPlacement.SameConfiguration(SingleScreen, [new ScreenRect(0, 0, 2560, 1400)]));
        Assert.True(ScreenPlacement.SameConfiguration(two, two));
    }

    // ── Nearest-screen ordering (R-dock-6 step 3) ─────────────────────────────

    [Fact]
    public void ThreeMonitorLayoutCollapsingToOne_KeepsRelativeOrderingIntelligible()
    {
        // Left (−1920..0), centre (0..1920), right (1920..3840) collapse to the centre alone.
        // A window from the left monitor lands against the left edge, one from the right against the
        // right edge — not all stacked at the origin.
        var fromLeft  = ScreenPlacement.Place(new ScreenRect(-1500, 300, 400, 300), SingleScreen, Nothing);
        var fromRight = ScreenPlacement.Place(new ScreenRect( 3200, 300, 400, 300), SingleScreen, Nothing);

        Assert.Equal(Fhd.X, fromLeft.X, 6);
        Assert.Equal(Fhd.Right - 400, fromRight.X, 6);
        Assert.True(fromLeft.X < fromRight.X);
    }

    [Fact]
    public void TwoScreens_WindowStaysOnTheOneItWasNearest()
    {
        var screens = new List<ScreenRect> { Fhd, new(1920, 0, 1920, 1040) };
        var placed  = ScreenPlacement.Place(new ScreenRect(2000, -60, 400, 300), screens, Nothing);

        AssertTitleBarInsideAWorkingArea(placed, screens);
        Assert.True(placed.X >= 1920, "should be repaired onto the SECOND screen, the one it was on");
    }

    // ── Degenerate input ──────────────────────────────────────────────────────

    [Fact]
    public void NoScreenInformation_LeavesTheWindowAlone_RatherThanGuessing()
    {
        var saved = new ScreenRect(3000, 200, 640, 480);
        Assert.Equal(saved, ScreenPlacement.Place(saved, Nothing, Nothing));
    }

    [Fact]
    public void ZeroSizedWindow_IsRepairedToAVisibleSize()
    {
        var placed = ScreenPlacement.Place(new ScreenRect(100, 100, 0, 0), SingleScreen, Nothing);
        Assert.True(placed.Width  >= ScreenPlacement.MinWindowSize);
        Assert.True(placed.Height >= ScreenPlacement.MinWindowSize);
        AssertTitleBarInsideAWorkingArea(placed, SingleScreen);
    }
}
