using System.IO;
using System.Runtime.CompilerServices;
using CircuitRF.Ui.Harmonica;
using Xunit;

namespace CircuitRF.Ui.Tests.Harmonica;

/// <summary>
/// R-h9a-3 — a DOCKED harmonicaRF document takes over the macOS app menu bar while it is the active
/// dockable, and gives it back on blur, reusing <c>WorkspaceViewModel</c>'s existing dock-level focus
/// tracking (per the brief's own instruction, rather than a second mechanism). The action-seam halves
/// (<c>HarmonicaMenuView.RecomputeAttachment</c>, <c>HarmonicaDocumentViewModel.
/// NativeMenuDockedFocusChanged</c>) are pinned by <see cref="HarmonicaMenuNativeAttachTests"/> and by
/// direct construction elsewhere; <c>WorkspaceViewModel</c> itself cannot be constructed headlessly
/// (its constructor touches <c>Dispatcher.UIThread</c> and the Avalonia app host — see this repo's own
/// "Testing without the Avalonia runtime" note), so this file pins the wiring by reading the real
/// source directly, the same fallback this codebase already uses for view-model-only logic that needs
/// a live app host (e.g. <c>CellFirstAcceleratorTests</c>).
/// </summary>
public class HarmonicaDockedFocusWiringTests
{
    private static string ReadRepoFile(string relativePath, [CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "CLAUDE.md")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "Could not locate the repo root (no CLAUDE.md found walking up from this test file).");
        return File.ReadAllText(Path.Combine(dir!, relativePath));
    }

    private static string WorkspaceViewModelSource() =>
        ReadRepoFile("src/Ui/ViewModels/WorkspaceViewModel.cs");

    [Fact]
    public void OnDocumentDockPropertyChanged_CallsUpdateHarmonicaDockedMenuFocus_WithTheActiveDockable()
    {
        string src = WorkspaceViewModelSource();
        Assert.Contains(
            "UpdateHarmonicaDockedMenuFocus(activeDockable as HarmonicaDocument);",
            src, System.StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateHarmonicaDockedMenuFocus_IsMacOsOnly_AndNoOpsWhenNothingChanged()
    {
        string src = WorkspaceViewModelSource();
        Assert.Contains(
            "private void UpdateHarmonicaDockedMenuFocus(HarmonicaDocument? nowActive)",
            src, System.StringComparison.Ordinal);
        Assert.Contains("if (!OperatingSystem.IsMacOS()) return;", src, System.StringComparison.Ordinal);
        Assert.Contains(
            "if (ReferenceEquals(_harmonicaDockedFocusDoc, nowActive)) return;",
            src, System.StringComparison.Ordinal);
    }

    // R-h9a-1's detach-before-attach rule, one level up: the OLD holder must release its NativeMenu
    // instance (Invoke(false)) before the new one claims anything (Invoke(true)) — textually first,
    // same ordering discipline HarmonicaMenuView.RecomputeAttachment already follows for the instance
    // itself.
    [Fact]
    public void UpdateHarmonicaDockedMenuFocus_ReleasesTheOldHolder_BeforeGrantingTheNewOne()
    {
        string src = WorkspaceViewModelSource();
        int releaseIdx = src.IndexOf(
            "_harmonicaDockedFocusDoc?.ViewModel.SetNativeMenuDockedFocus(false);",
            System.StringComparison.Ordinal);
        int grantIdx = src.IndexOf(
            "nowActive.ViewModel.SetNativeMenuDockedFocus(true);",
            System.StringComparison.Ordinal);

        Assert.True(releaseIdx >= 0, "Expected the old holder to be released.");
        Assert.True(grantIdx >= 0, "Expected the new holder to be granted focus.");
        Assert.True(releaseIdx < grantIdx, "The old holder must be released before the new one is granted.");
    }

    // The restore-on-blur path must read circuitRF's own NativeMenu back — the SAME instance
    // WorkspaceWindow.axaml declares, never a rebuilt or second reference.
    //
    // Since MW1 (R-mw1-13) the source of that instance is the SHELL's own captured menu rather than
    // the application-scope one. With two workspace windows the app-scope menu is whichever shell
    // activated last, so restoring from it would hand one window the other's menu — and a menu's
    // commands bind through its own DataContext, so every File item would then drive the wrong
    // workspace. The requirement is unchanged; where the instance comes from is not.
    [Fact]
    public void RestoreCircuitRfMenuBar_ReadsBackTheShellsOwnMenu_NeverRebuildsIt()
    {
        string src = WorkspaceViewModelSource();
        Assert.Contains(
            "private void RestoreCircuitRfMenuBar()",
            src, System.StringComparison.Ordinal);
        Assert.Contains(
            "shell.OwnNativeMenu is not { } ownMenu",
            src, System.StringComparison.Ordinal);
        Assert.Contains(
            "Avalonia.Controls.NativeMenu.SetMenu(shell, ownMenu);",
            src, System.StringComparison.Ordinal);
    }

    // Every workspace-lifecycle reset point that already clears _lastActiveSchematicDoc must also
    // clear the harmonicaRF docked-focus tracking and restore circuitRF's own menu — otherwise a
    // workspace switch/close while a harmonicaRF document held the takeover would leave the OLD
    // workspace's menu bar attached to a window that no longer shows that document.
    [Fact]
    public void EveryWorkspaceResetPoint_AlsoResetsHarmonicaDockedFocusTracking()
    {
        string src = WorkspaceViewModelSource();
        int resetCallCount = System.Text.RegularExpressions.Regex.Matches(
            src, @"ResetHarmonicaDockedFocusTracking\(\);").Count;
        // NewWorkspace, SwitchToWorkspace, ResetToBlankShell, plus the OnDockableClosed cleanup path
        // (guarded, so it reads "ResetHarmonicaDockedFocusTracking();" once more without a trailing
        // semicolon match issue) — four call sites in total.
        Assert.True(resetCallCount >= 4,
            $"Expected at least 4 calls to ResetHarmonicaDockedFocusTracking(), found {resetCallCount}.");
    }

    [Fact]
    public void OnDockableClosed_RestoresTheMenuBar_WhenTheClosingDocHeldTheTakeover()
    {
        string src = WorkspaceViewModelSource();
        Assert.Contains(
            "if (ReferenceEquals(dockable, _harmonicaDockedFocusDoc))\n            ResetHarmonicaDockedFocusTracking();",
            src, System.StringComparison.Ordinal);
    }

    // R-h9r2-11 — a document that is ALREADY the active dockable at the instant its view is first
    // realized must still end up with its native menu attached, even though WorkspaceViewModel's
    // SetNativeMenuDockedFocus(true) necessarily runs before HarmonicaView.OnDataContextChanged has
    // wired NativeMenuDockedFocusChanged. This drives HarmonicaDocumentViewModel directly (no Avalonia
    // runtime needed) to prove the pending state survives the race rather than being silently dropped.
    [Fact]
    public void SetNativeMenuDockedFocus_BeforeTheViewWires_IsNotLost_AndAppliesOnWireUp()
    {
        var vm = new HarmonicaDocumentViewModel();

        // WorkspaceViewModel declares this document active BEFORE any view exists to hear about it —
        // exactly the first-open ordering.
        vm.SetNativeMenuDockedFocus(true);

        bool? observed = null;
        vm.NativeMenuDockedFocusChanged = hasFocus => observed = hasFocus;

        Assert.True(observed, "Wiring the delegate must immediately re-apply the pending docked-focus state.");
    }

    [Fact]
    public void SetNativeMenuDockedFocus_WithNoPendingState_WiresToFalse()
    {
        var vm = new HarmonicaDocumentViewModel();

        bool? observed = null;
        vm.NativeMenuDockedFocusChanged = hasFocus => observed = hasFocus;

        Assert.False(observed, "A document that was never made active should wire up as unfocused, not null/unset.");
    }

    [Fact]
    public void SetNativeMenuDockedFocus_WhileWired_InvokesTheDelegateDirectly()
    {
        var vm = new HarmonicaDocumentViewModel();
        var calls = new System.Collections.Generic.List<bool>();
        vm.NativeMenuDockedFocusChanged = calls.Add;

        vm.SetNativeMenuDockedFocus(true);
        vm.SetNativeMenuDockedFocus(false);

        Assert.Equal(new[] { false, true, false }, calls);
    }
}
