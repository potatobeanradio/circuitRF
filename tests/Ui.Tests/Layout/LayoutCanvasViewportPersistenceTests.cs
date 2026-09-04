using System;
using System.IO;
using System.Text.RegularExpressions;
using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests.Layout;

/// <summary>
/// Owner report, 2026-09-04: opening Technology ▾ ▸ Edit… changed the layout's zoom, and closing the
/// <c>.ctech</c> changed it again. Splitting (and re-merging) the document row re-realises the layout
/// view, which re-binds <c>LayoutCanvas.ViewModel</c> — and the setter unconditionally re-armed the
/// fit-on-bind, so the next layout pass silently zoomed to fit a document that had been mid-session.
///
/// <para>Pan/zoom is canvas-owned state and a canvas does not survive a dock rebuild, so the memory
/// lives on the view model (<see cref="LayoutEditorViewModel.LastViewport"/>), which does. This file
/// pins the view-model half directly; the canvas half is scanned, since <c>LayoutCanvas</c> is an
/// Avalonia control and this test project stands up no headless Avalonia app (see
/// <c>TechEditorLayerColumnLayoutTests</c> for the same convention).</para>
/// </summary>
public sealed class LayoutCanvasViewportPersistenceTests
{
    [Fact]
    public void AFreshViewModel_RemembersNoViewport_SoItStillGetsTheInitialFit()
    {
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um });
        Assert.Null(vm.LastViewport);
    }

    [Fact]
    public void TheRememberedViewportSurvivesOnTheViewModel_WhichOutlivesTheCanvas()
    {
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um })
        {
            LastViewport = new LayoutViewport(PanX: 12_000, PanY: -3_000, Zoom: 0.004, Width: 800, Height: 600),
        };

        // Whatever the dock does to the VIEW, the document keeps its own answer.
        Assert.Equal(0.004, vm.LastViewport!.Value.Zoom);
        Assert.Equal(12_000, vm.LastViewport.Value.PanX);
        Assert.Equal(-3_000, vm.LastViewport.Value.PanY);
    }

    // ── The canvas half ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCanvasRecordsEveryViewportChangeOnTheViewModel()
    {
        // One funnel — every pan/zoom path already goes through RaiseViewportChanged, so recording
        // there is what makes "the last viewport" actually the last one.
        Assert.Matches(@"(?s)private void RaiseViewportChanged\(\)\s*\{[^}]*LastViewport = CurrentViewport;",
                       StripComments(CanvasSource()));
    }

    [Fact]
    public void TheFitOnBindIsConditionalOnHavingNoRememberedViewport()
    {
        // _needsInitialFit is armed on bind only when there is nothing remembered. If this ever goes
        // back to an unconditional assignment, every dock rebuild re-frames the document.
        Assert.Matches(
            @"(?s)if \(_viewModel\.LastViewport is \{ \} vp\)\s*\{.*?_needsInitialFit = false;\s*\}"
            + @"\s*else\s*\{\s*_needsInitialFit = true;\s*\}",
            StripComments(CanvasSource()));
    }

    private static string CanvasSource() => File.ReadAllText(
        Path.Combine(RepoRoot(), "src", "Ui", "Controls", "LayoutCanvas.cs"));

    /// <summary>Comments describe the rule; only code can break it.</summary>
    private static string StripComments(string src) =>
        Regex.Replace(Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
