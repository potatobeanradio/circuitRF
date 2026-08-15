// ================================================================
//  HarmonicaR1bDragDiagnosisTests.cs — §1 of brief-harmonicarf-r1b-panels-charts-and-interaction.md
//
//  R-h9b-1  Edit Display's gesture never reached PointerMoved/PointerUp: HarmonicaCanvas gated both
//           on Gesture.IsDragging, which is false for the whole of an edit grab (EditGrab is a
//           SEPARATE field, deliberately not folded into Grab). Fixed by HarmonicaGesture.IsLive
//           (IsDragging || EditGrab != None) and re-pointing the canvas's two gates at it.
//  R-h9b-2  HarmonicaCanvas never set Focusable = true, so OnKeyDown (Escape/Delete) never fired.
//  R-h9b-3  the diagnosed cause of the marker/grid-point report: SmithPanelData carried a non-empty
//           Title, which made PlotRenderer.ComputeViewport reserve title margin for the RENDER path
//           while HitTestTransform's bare, always-untitled plot did not — so render and hit-test
//           disagreed about where the chart sat whenever a title was shown (which is always, in
//           practice: HarmonicaSolver has always set "Power"/"Efficiency"). Fixed by drawing the two
//           title rows harmonicaRF's own way (R-h9b-4) and folding the SAME reserved band into
//           GammaToCanvas/CanvasToGamma, so render and hit-test can never diverge on it again.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaR1bDragDiagnosisTests(ITestOutputHelper output)
{
    // ══ R-h9b-1 — Edit Display's gesture is now LIVE for PointerMoved/PointerUp ═══════════════

    [Fact]
    public void EditDisplayDrag_MovesThePanel_AndPushesExactlyOneUndoEntry()
    {
        var vm = new HarmonicaViewModel();
        vm.EditDisplay.Unlocked = true;

        const double W = 1200, H = 800;
        var before = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);

        // A point safely inside the panel BODY (not the resize grip in its bottom-right corner).
        double x = (before.X + before.W * 0.3) * W;
        double y = (before.Y + before.H * 0.3) * H;

        var g = new HarmonicaGesture(vm);
        Assert.True(g.PointerDown(x, y, W, H));
        Assert.Equal(HarmonicaEditGrab.Move, g.EditGrab);
        Assert.True(g.IsLive, "an active edit grab must count as a LIVE gesture");

        Assert.False(vm.EditDisplay.Undo.CanUndo);

        // The gate this section fixes: HarmonicaCanvas.OnPointerMoved only calls through when
        // Gesture.IsLive is true. Before the fix, IsDragging (Grab.IsGrab) stayed false for the whole
        // of an edit grab, so the canvas never reached this call at all.
        double dx = 0.08 * W, dy = 0.05 * H;
        g.PointerMoved(x + dx, y + dy, W, H);
        Assert.Equal(1, g.MoveCount);

        g.PointerUp(x + dx, y + dy, W, H);

        var after = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        output.WriteLine($"panel moved from ({before.X:F4},{before.Y:F4}) to ({after.X:F4},{after.Y:F4})");
        Assert.True(Math.Abs(after.X - (before.X + 0.08)) < 1e-6);
        Assert.True(Math.Abs(after.Y - (before.Y + 0.05)) < 1e-6);

        // ONE undo entry for the whole gesture (R-h7-9's shape), not one per pointer move.
        Assert.True(vm.EditDisplay.Undo.CanUndo);
        vm.EditDisplay.Undo.Undo();
        Assert.False(vm.EditDisplay.Undo.CanUndo);
        Assert.Equal(before, vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower));
    }

    [Fact]
    public void EditDisplayResize_AlsoLive_AndEscapeCancelsBackToTheStart()
    {
        var vm = new HarmonicaViewModel();
        vm.EditDisplay.Unlocked = true;

        const double W = 1000, H = 700;
        var before = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);

        // The resize grip sits in the panel's bottom-right corner (HarmonicaEditTarget.GripDevicePixels).
        double gx = (before.X + before.W) * W - 4;
        double gy = (before.Y + before.H) * H - 4;

        var g = new HarmonicaGesture(vm);
        Assert.True(g.PointerDown(gx, gy, W, H));
        Assert.Equal(HarmonicaEditGrab.Resize, g.EditGrab);

        g.PointerMoved(gx + 40, gy + 30, W, H);
        Assert.Equal(1, g.MoveCount);

        // Escape mid-drag: Cancel() restores the start layout rather than committing the resize.
        g.Cancel();
        Assert.Equal(HarmonicaEditGrab.None, g.EditGrab);
        Assert.Equal(before, vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower));
        Assert.False(vm.EditDisplay.Undo.CanUndo);
    }

    // ══ R-h9b-2 — the canvas is focusable, pinned by source scan (no live control here) ══════

    [Fact]
    public void HarmonicaCanvas_SetsFocusableTrue_InItsConstructor()
    {
        string src = ReadSource("src", "Ui", "Controls", "HarmonicaCanvas.cs");
        Assert.Contains("Focusable = true", src, StringComparison.Ordinal);
    }

    [Fact]
    public void HarmonicaCanvas_PointerGates_TestIsLive_NotIsDragging()
    {
        // Pins the actual fix: before it, OnPointerMoved/OnPointerReleased tested `IsDragging: true`,
        // which is false for the whole of an Edit Display grab. A regression back to IsDragging here
        // would silently kill Edit Display dragging again with every other test still green, because
        // HarmonicaGesture itself (driven directly, as the rest of this file does) is correct either
        // way — only the CANVAS's gate was ever wrong.
        string src = ReadSource("src", "Ui", "Controls", "HarmonicaCanvas.cs");
        Assert.Contains("Gesture is not { IsLive: true }", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Gesture is not { IsDragging: true }", src, StringComparison.Ordinal);
    }

    // ══ R-h9b-3 — LastGrabKind survives PointerUp, for next time ══════════════════════════════

    [Fact]
    public void LastGrabKind_SurvivesPointerUp_SoAReleasedGestureStillSaysWhatItGrabbed()
    {
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;

        var marker = vm.Markers[1];
        var p = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        var local = HarmonicaPanelRenderer.GammaToCanvas(marker.Gamma, (p.W * W, p.H * H));
        double x = p.X * W + local.X, y = p.Y * H + local.Y;

        var g = new HarmonicaGesture(vm);
        Assert.Equal(HarmonicaGrabKind.None, g.LastGrabKind);

        Assert.True(g.PointerDown(x, y, W, H));
        Assert.Equal(HarmonicaGrabKind.ExtrinsicMarker, g.LastGrabKind);

        g.PointerUp(x, y, W, H);
        Assert.Equal(HarmonicaGrabKind.None, g.Grab.Kind);                       // reset for the next gesture
        Assert.Equal(HarmonicaGrabKind.ExtrinsicMarker, g.LastGrabKind);         // but diagnosable after release
    }

    // ══ R-h9b-3's actual ROOT CAUSE — a titled Smith panel's render and hit-test must agree ═══

    [Fact]
    public async Task ATitledSmithPanel_MarkerDrag_StillGrabsAndMovesTheMarker_AndWritesTerminationsZ()
    {
        // Reproduces the real-world case: HarmonicaSolver has ALWAYS given every SmithPanelData a
        // non-empty Title. Before R-h9b-1's fix this made HarmonicaPanelRenderer.NewSmithPlot turn on
        // PlotRenderer's own title reservation for the RENDER path while HarmonicaHitTest's positions
        // (through GammaToCanvas/MarkerToCanvas) came from an always-untitled plot — so a marker drawn
        // under a real, solved frame's title was NOT where a click on it would be resolved. This drives
        // the whole gesture through a REAL solved frame (title included) and asserts the regression
        // gate the brief itself names: a synthetic press-move-release changes Terminations.Z(...).
        var vm = new HarmonicaViewModel();
        vm.Pool.Completed += (f, _) => vm.PublishFrame(f);
        vm.RequestScheduledFrame(dragging: false);
        await vm.Pool.DrainAsync();

        Assert.False(string.IsNullOrEmpty(vm.Frame.SmithPower.Title));
        output.WriteLine($"panel title: \"{vm.Frame.SmithPower.Title}\" / \"{vm.Frame.SmithPower.Subtitle}\"");

        const double W = 1000, H = 640;
        var marker = vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 1 });
        var zBefore = vm.Terminations.Z(TerminationSide.Load, marker.Band);

        var target = new Complex(0.5, -0.25);
        var p = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        var canvasTarget = HarmonicaPanelRenderer.GammaToCanvas(marker.Gamma, (p.W * W, p.H * H));
        double sx = p.X * W + canvasTarget.X, sy = p.Y * H + canvasTarget.Y;

        var g = new HarmonicaGesture(vm);
        Assert.True(g.PointerDown(sx, sy, W, H));
        Assert.Equal(HarmonicaGrabKind.ExtrinsicMarker, g.Grab.Kind);

        var releaseCanvas = HarmonicaPanelRenderer.GammaToCanvas(target, (p.W * W, p.H * H));
        double rx = p.X * W + releaseCanvas.X, ry = p.Y * H + releaseCanvas.Y;
        g.PointerMoved(rx, ry, W, H);
        g.PointerUp(rx, ry, W, H);

        var zAfter = vm.Terminations.Z(TerminationSide.Load, marker.Band);
        output.WriteLine($"Z before {zBefore}, Z after {zAfter}");
        Assert.NotEqual(zBefore, zAfter);
        Assert.True((marker.Gamma - target).Magnitude < 0.01,
            $"marker landed at {marker.Gamma}, not at the release target {target}");
    }

    private static string ReadSource(params string[] parts)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null &&
               !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "circuitRF.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = System.IO.Path.Combine([dir!.FullName, .. parts]);
        Assert.True(System.IO.File.Exists(path), $"source not found at {path}");
        return System.IO.File.ReadAllText(path);
    }
}
