// ================================================================
//  HarmonicaEditDisplayTests.cs  —  M3's gate, brief-harmonicarf-h7
//
//  R-h7-8   unlocking flips CharmLayout.Locked and writes the SAME field R-h45-1 created for it.
//  R-h7-9   the undo stack is .cdd's own, and ONE gesture is ONE entry.
//  R-h7-10  a degenerate placement cannot be CREATED, because the next load would discard it.
// ================================================================

using System;
using System.Linq;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaEditDisplayTests(ITestOutputHelper output)
{
    // ══ R-h7-8 — unlock, move, save, reload ═════════════════════════════════

    [Fact]
    public void UnlockMovePanelSaveReload_ComesBackWhereItWasPut_AndStaysUnlocked()
    {
        var vm = new HarmonicaViewModel();
        Assert.True(vm.Layout.Locked);

        vm.EditDisplay.Unlocked = true;
        Assert.False(vm.Layout.Locked);

        vm.EditDisplay.BeginGesture();
        Assert.True(vm.EditDisplay.MovePanel(HarmonicaPanelId.Loadline, dx: -0.10, dy: 0.05));
        vm.EditDisplay.EndGesture();

        var moved = vm.Layout.PlacementOf(HarmonicaPanelId.Loadline);
        string json = vm.ToCharmJson();

        var reloaded = new HarmonicaViewModel();
        reloaded.LoadCharm(json, baseDirectory: null);

        var back = reloaded.Layout.PlacementOf(HarmonicaPanelId.Loadline);
        output.WriteLine($"saved   ({moved.X:F4}, {moved.Y:F4}, {moved.W:F4}, {moved.H:F4})");
        output.WriteLine($"reloaded({back.X:F4}, {back.Y:F4}, {back.W:F4}, {back.H:F4})");

        Assert.Equal(moved, back);
        Assert.False(reloaded.Layout.Locked);
        Assert.True(reloaded.EditDisplay.Unlocked);
    }

    [Fact]
    public void FlippingTheLockAlone_ChangesNothingButTheFlag()
    {
        var vm = new HarmonicaViewModel();
        var before = vm.Layout.Panels.ToArray();

        vm.EditDisplay.Unlocked = true;

        Assert.Equal(before, vm.Layout.Panels.ToArray());
        Assert.False(vm.Layout.Locked);
    }

    // ══ R-h7-9 — ONE undo entry per gesture ═════════════════════════════════

    [Fact]
    public void AWholeDragIsOneUndoEntry_NotOnePerPointerMove()
    {
        var vm = new HarmonicaViewModel();
        vm.EditDisplay.Unlocked = true;
        var start = vm.Layout.PlacementOf(HarmonicaPanelId.PowerSweep);

        var gesture = new HarmonicaGesture(vm);
        // A drag of forty moves across the panel — the counter shape the PCell drags already use.
        gesture.PointerDown(700, 600, 1000, 800);
        for (int i = 1; i <= 40; i++) gesture.PointerMoved(700 - i, 600 - i, 1000, 800);
        gesture.PointerUp(660, 560, 1000, 800);

        Assert.Equal(40, gesture.MoveCount);
        output.WriteLine($"{gesture.MoveCount} pointer moves");

        // One entry: one undo puts it all the way back.
        Assert.True(vm.EditDisplay.Undo.CanUndo);
        vm.EditDisplay.Undo.Undo();
        Assert.Equal(start, vm.Layout.PlacementOf(HarmonicaPanelId.PowerSweep));
        Assert.False(vm.EditDisplay.Undo.CanUndo);

        // …and redo puts it back where the drag left it.
        vm.EditDisplay.Undo.Redo();
        Assert.NotEqual(start, vm.Layout.PlacementOf(HarmonicaPanelId.PowerSweep));
    }

    [Fact]
    public void AGestureThatMovedNothing_PushesNoUndoEntry()
    {
        var vm = new HarmonicaViewModel();
        vm.EditDisplay.Unlocked = true;

        vm.EditDisplay.BeginGesture();
        vm.EditDisplay.EndGesture();
        Assert.False(vm.EditDisplay.Undo.CanUndo);

        // …and one that moved and came back is still nothing. SmithPower, not Loadline: the loadline
        // panel is already against the right edge, so its outward move is clamped away and the
        // return leg would be a real net move — the test would then pass for the wrong reason.
        vm.EditDisplay.BeginGesture();
        Assert.True(vm.EditDisplay.MovePanel(HarmonicaPanelId.SmithPower, 0.05, 0));
        Assert.True(vm.EditDisplay.MovePanel(HarmonicaPanelId.SmithPower, -0.05, 0));
        Assert.False(vm.EditDisplay.EndGesture());
        Assert.False(vm.EditDisplay.Undo.CanUndo);
    }

    [Fact]
    public void ACancelledGesture_RollsBackAndPushesNothing()
    {
        var vm = new HarmonicaViewModel();
        vm.EditDisplay.Unlocked = true;
        var start = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);

        var gesture = new HarmonicaGesture(vm);
        gesture.PointerDown(100, 100, 1000, 800);
        gesture.PointerMoved(300, 300, 1000, 800);
        Assert.NotEqual(start, vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower));

        gesture.Cancel();

        Assert.Equal(start, vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower));
        Assert.False(vm.EditDisplay.Undo.CanUndo);
    }

    // ══ R-h7-10 — a degenerate placement cannot be created ══════════════════

    [Fact]
    public void APanelDraggedToZeroWidth_CannotBeCommitted_ItIsClampedAtTheMinimum()
    {
        var vm = new HarmonicaViewModel();
        vm.EditDisplay.Unlocked = true;

        // Ask for zero, and for negative — a resize drag past the panel's own left edge.
        vm.EditDisplay.ResizePanel(HarmonicaPanelId.SmithPower, 0.0, 0.0);
        var p = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        output.WriteLine($"after resize-to-zero: W={p.W:F4} H={p.H:F4} (minimum {HarmonicaEditDisplay.MinimumSpan})");

        Assert.True(p.W >= HarmonicaEditDisplay.MinimumSpan);
        Assert.True(p.H >= HarmonicaEditDisplay.MinimumSpan);

        vm.EditDisplay.ResizePanel(HarmonicaPanelId.SmithPower, -3.0, -3.0);
        p = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        Assert.True(p.W >= HarmonicaEditDisplay.MinimumSpan);
        Assert.True(p.H >= HarmonicaEditDisplay.MinimumSpan);
    }

    [Fact]
    public void NothingEditModeCanCommit_IsDroppedByTheNextLoad()
    {
        // The point of R-h7-10 stated as the round trip it protects: a drag to the extreme, saved,
        // must come back as itself rather than silently falling to the §7.1 default.
        var vm = new HarmonicaViewModel();
        vm.EditDisplay.Unlocked = true;
        vm.EditDisplay.BeginGesture();
        vm.EditDisplay.ResizePanel(HarmonicaPanelId.Loadline, 0.0, 0.0);
        vm.EditDisplay.EndGesture();

        var committed = vm.Layout.PlacementOf(HarmonicaPanelId.Loadline);

        var reloaded = new HarmonicaViewModel();
        reloaded.LoadCharm(vm.ToCharmJson(), baseDirectory: null);
        var back = reloaded.Layout.PlacementOf(HarmonicaPanelId.Loadline);

        output.WriteLine($"committed W={committed.W:F4}, reloaded W={back.W:F4}");
        Assert.Equal(committed, back);
        Assert.NotEqual(CharmLayout.DefaultPanels.Single(x => x.PanelId == HarmonicaPanelId.Loadline), back);
    }

    // ══ re-locking, and the untouched document ══════════════════════════════

    [Fact]
    public void ReLocking_RestoresSection71sDefaultPlacement()
    {
        var vm = new HarmonicaViewModel();
        vm.EditDisplay.Unlocked = true;
        vm.EditDisplay.MovePanel(HarmonicaPanelId.SmithEfficiency, 0.2, 0.1);
        Assert.NotEqual(CharmLayout.Default.Panels, vm.Layout.Panels);

        vm.EditDisplay.Unlocked = false;

        Assert.True(vm.Layout.Locked);
        Assert.True(vm.Layout.IsDefault);
        Assert.Equal(CharmLayout.DefaultPanels, vm.Layout.Panels);
    }

    [Fact]
    public void AnUntouchedDocument_StillWritesNoLayoutBlock()
    {
        // H4–H5 pinned this; M3 must not break it.
        var vm = new HarmonicaViewModel();
        string json = vm.ToCharmJson();
        Assert.DoesNotContain("\"Layout\"", json, StringComparison.Ordinal);

        // …and it survives an unlock-then-relock, because re-locking restores the default exactly.
        vm.EditDisplay.Unlocked = true;
        vm.EditDisplay.MovePanel(HarmonicaPanelId.Loadline, 0.1, 0);
        vm.EditDisplay.Unlocked = false;
        Assert.DoesNotContain("\"Layout\"", vm.ToCharmJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void WhileLocked_NothingCanMove()
    {
        var vm = new HarmonicaViewModel();
        var before = vm.Layout.Panels.ToArray();

        Assert.False(vm.EditDisplay.MovePanel(HarmonicaPanelId.Loadline, 0.2, 0.2));
        Assert.False(vm.EditDisplay.ResizePanel(HarmonicaPanelId.Loadline, 0.2, 0.2));
        Assert.False(vm.EditDisplay.RemovePanel(HarmonicaPanelId.Loadline));

        Assert.Equal(before, vm.Layout.Panels.ToArray());
    }

    [Fact]
    public void WhileLocked_APointerDownDragsAMarker_NotThePanelUnderIt()
    {
        var vm = new HarmonicaViewModel();
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        var gesture = new HarmonicaGesture(vm);
        var before  = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);

        gesture.PointerDown(120, 120, 1000, 800);
        gesture.PointerMoved(160, 140, 1000, 800);
        gesture.PointerUp(160, 140, 1000, 800);

        Assert.Equal(before, vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower));
        Assert.Equal(HarmonicaEditGrab.None, gesture.EditGrab);
    }

    // ══ the edit hit test ═══════════════════════════════════════════════════

    [Fact]
    public void TheResizeGrip_IsInTheBottomRightCorner_AndIsDeviceSized()
    {
        var vm = new HarmonicaViewModel();
        var layout = vm.Layout;
        var p = layout.PlacementOf(HarmonicaPanelId.SmithPower);

        double w = 1000, h = 800;
        double right  = (p.X + p.W) * w, bottom = (p.Y + p.H) * h;

        var grip = HarmonicaEditTarget.Resolve(layout, [], right - 3, bottom - 3, w, h);
        var body = HarmonicaEditTarget.Resolve(layout, [], right / 2, bottom / 2, w, h);

        Assert.Equal(HarmonicaEditGrab.Resize, grip.Kind);
        Assert.Equal(HarmonicaEditGrab.Move,   body.Kind);
        Assert.Equal(HarmonicaPanelId.SmithPower, grip.PanelId);

        // R-h6-2's rule, applied here for the same reason: the grip is DEVICE pixels, so at 2×
        // scaling a point 10 DIPs in from the corner is outside it where 5 DIPs is inside.
        var at2xInside  = HarmonicaEditTarget.Resolve(layout, [], right - 4, bottom - 4, w, h, 2.0);
        var at2xOutside = HarmonicaEditTarget.Resolve(layout, [], right - 10, bottom - 10, w, h, 2.0);
        Assert.Equal(HarmonicaEditGrab.Resize, at2xInside.Kind);
        Assert.Equal(HarmonicaEditGrab.Move,   at2xOutside.Kind);
    }

    [Fact]
    public void APickedTracePanel_IsHitTestedABOVETheSection71Panels()
    {
        var vm = new HarmonicaViewModel();
        var picked = vm.AddPickedTrace("Gamma_intr[1, :]");

        // It lands at (0.30, 0.30, 0.40, 0.35), over the two Smith charts.
        var hit = HarmonicaEditTarget.Resolve(vm.Layout, [picked], 500, 320, 1000, 800);
        Assert.Equal(picked.PanelId, hit.PanelId);
    }
}
