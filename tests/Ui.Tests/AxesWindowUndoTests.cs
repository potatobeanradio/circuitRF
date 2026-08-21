// ================================================================
//  AxesWindowUndoTests.cs — a pan is an edit (2026-08-21)
//
//  Owner: "changing the axes panning does not dirty the .cdd document; also an
//  axis panning change needs to be undoable."
//
//  The axis windows are saved state (AxesConfig.Window*), but the only signal
//  PlotControl raised was PlotChanged — and that path (PlotContainerViewModel
//  .OnPlotChanged) rebuilds marker info boxes without ever reaching ContentChanged.
//  So a pan changed the file and the document still looked saved.
//
//  Recording an undo entry fixes both halves at once: UndoRedoManager.StateChanged
//  IS the dirty channel. Wheel zoom and Autoscale move the same state and get the
//  same treatment; the wheel coalesces, having no gesture end of its own.
// ================================================================

using System;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class AxesWindowUndoTests
{
    private static (DataDisplayViewModel Display, PlotContainerViewModel Plot) Fixture()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        display.CanvasSizeProvider = () => (800.0, 600.0);
        var container = display.AddPlot(PlotType.Rect);
        container.PlotVM.Plot.Axes.Window          = new Rect(1.0, -40.0, 4.0, 40.0);
        container.PlotVM.Plot.Axes.WindowSecondary = new Rect(1.0, -180.0, 4.0, 360.0);
        display.UndoRedo.Clear();                 // discard the AddPlot entry
        return (display, container);
    }

    // ---- dirty -------------------------------------------------------

    [Fact]
    public void APan_DirtiesTheDocument()
    {
        var (display, plot) = Fixture();
        int dirtied = 0;
        display.ContentChanged += (_, _) => dirtied++;

        var before = plot.PlotVM.Plot.Axes.Window;
        var beforeSecondary = plot.PlotVM.Plot.Axes.WindowSecondary;
        plot.PlotVM.Plot.Axes.Window = new Rect(2.0, -40.0, 4.0, 40.0);
        plot.PushAxesWindowChange(before, beforeSecondary);

        Assert.True(dirtied > 0);
    }

    /// <summary>A click that never became a drag must leave nothing behind — no undo entry, and no
    /// dirty mark on a document the user did not change.</summary>
    [Fact]
    public void APressThatNeverMoved_RecordsNothing()
    {
        var (display, plot) = Fixture();
        int dirtied = 0;
        display.ContentChanged += (_, _) => dirtied++;

        var axes = plot.PlotVM.Plot.Axes;
        plot.PushAxesWindowChange(axes.Window, axes.WindowSecondary);

        Assert.False(display.UndoRedo.CanUndo);
        Assert.Equal(0, dirtied);
    }

    // ---- undo --------------------------------------------------------

    [Fact]
    public void APan_IsUndoableAndRedoable()
    {
        var (display, plot) = Fixture();
        var axes = plot.PlotVM.Plot.Axes;

        var before = axes.Window;
        var panned = new Rect(2.5, -37.0, 4.0, 40.0);
        axes.Window = panned;
        plot.PushAxesWindowChange(before, axes.WindowSecondary);

        Assert.True(display.UndoRedo.CanUndo);

        display.UndoRedo.Undo();
        Assert.Equal(before, axes.Window);

        display.UndoRedo.Redo();
        Assert.Equal(panned, axes.Window);
    }

    /// <summary>Both windows are restored together. A Rect plot's right axis has its own window, and
    /// undoing a pan that moved only one of them still has to put both back — recording just the
    /// primary would silently ratchet the secondary.</summary>
    [Fact]
    public void Undo_RestoresBothWindows()
    {
        var (display, plot) = Fixture();
        var axes = plot.PlotVM.Plot.Axes;

        var beforePrimary   = axes.Window;
        var beforeSecondary = axes.WindowSecondary;

        axes.Window          = new Rect(2.0, -38.0, 4.0, 40.0);
        axes.WindowSecondary = new Rect(2.0, -150.0, 4.0, 360.0);
        plot.PushAxesWindowChange(beforePrimary, beforeSecondary);

        display.UndoRedo.Undo();

        Assert.Equal(beforePrimary,   axes.Window);
        Assert.Equal(beforeSecondary, axes.WindowSecondary);
    }

    /// <summary>The drag-start snapshots must follow the window, or the first pan after an undo
    /// would translate from where the window used to be and jump.</summary>
    [Fact]
    public void Undo_AlsoResetsTheDragStartSnapshots()
    {
        var (display, plot) = Fixture();
        var axes = plot.PlotVM.Plot.Axes;

        var before = axes.Window;
        axes.Window = new Rect(2.5, -37.0, 4.0, 40.0);
        plot.PushAxesWindowChange(before, axes.WindowSecondary);

        display.UndoRedo.Undo();

        Assert.Equal(axes.Window,          axes.WindowState);
        Assert.Equal(axes.WindowSecondary, axes.WindowSecondaryState);
    }

    // ---- coalescing --------------------------------------------------

    /// <summary>The wheel has no gesture end, so a run of notches folds into ONE entry — a single
    /// undo returns to where the run started rather than unwinding it a notch at a time.</summary>
    [Fact]
    public void ConsecutiveZoomSteps_CoalesceIntoOneUndoEntry()
    {
        var (display, plot) = Fixture();
        var axes  = plot.PlotVM.Plot.Axes;
        var start = axes.Window;

        for (int i = 1; i <= 5; i++)
        {
            var before = axes.Window;
            axes.Window = new Rect(before.X + 0.1, before.Y + 1.0, before.Width, before.Height);
            plot.PushAxesWindowChange(before, axes.WindowSecondary, coalesce: true);
        }
        var afterRun = axes.Window;

        display.UndoRedo.Undo();
        Assert.Equal(start, axes.Window);
        Assert.False(display.UndoRedo.CanUndo);      // the whole run was one entry

        display.UndoRedo.Redo();
        Assert.Equal(afterRun, axes.Window);
    }

    /// <summary>Coalescing must not reach across an intervening entry, or a zoom would swallow the
    /// pan before it.</summary>
    [Fact]
    public void AZoomAfterAPan_DoesNotSwallowThePan()
    {
        var (display, plot) = Fixture();
        var axes  = plot.PlotVM.Plot.Axes;
        var start = axes.Window;

        var beforePan = axes.Window;
        axes.Window = new Rect(2.0, -40.0, 4.0, 40.0);
        plot.PushAxesWindowChange(beforePan, axes.WindowSecondary);      // a pan — never coalesces
        var afterPan = axes.Window;

        var beforeZoom = axes.Window;
        axes.Window = new Rect(2.2, -39.0, 3.6, 36.0);
        plot.PushAxesWindowChange(beforeZoom, axes.WindowSecondary, coalesce: true);

        display.UndoRedo.Undo();
        Assert.Equal(afterPan, axes.Window);        // back to the end of the pan, not past it

        display.UndoRedo.Undo();
        Assert.Equal(start, axes.Window);
    }

    /// <summary>A pan is one entry per drag: it never coalesces, even straight after another pan.</summary>
    [Fact]
    public void ConsecutivePans_StayIndependentEntries()
    {
        var (display, plot) = Fixture();
        var axes  = plot.PlotVM.Plot.Axes;
        var start = axes.Window;

        var first = axes.Window;
        axes.Window = new Rect(2.0, -40.0, 4.0, 40.0);
        plot.PushAxesWindowChange(first, axes.WindowSecondary);
        var afterFirst = axes.Window;

        var second = axes.Window;
        axes.Window = new Rect(3.0, -40.0, 4.0, 40.0);
        plot.PushAxesWindowChange(second, axes.WindowSecondary);

        display.UndoRedo.Undo();
        Assert.Equal(afterFirst, axes.Window);
        display.UndoRedo.Undo();
        Assert.Equal(start, axes.Window);
    }

    /// <summary>Coalescing is per plot — a zoom on one plot must not extend another plot's entry.</summary>
    [Fact]
    public void CoalescingIsScopedToOnePlot()
    {
        var (display, first) = Fixture();
        var second = display.AddPlot(PlotType.Rect);
        second.PlotVM.Plot.Axes.Window = new Rect(1.0, -40.0, 4.0, 40.0);
        display.UndoRedo.Clear();

        var a = first.PlotVM.Plot.Axes;
        var b = second.PlotVM.Plot.Axes;
        var aStart = a.Window;
        var bStart = b.Window;

        var beforeA = a.Window;
        a.Window = new Rect(2.0, -40.0, 4.0, 40.0);
        first.PushAxesWindowChange(beforeA, a.WindowSecondary, coalesce: true);

        var beforeB = b.Window;
        b.Window = new Rect(2.0, -40.0, 4.0, 40.0);
        second.PushAxesWindowChange(beforeB, b.WindowSecondary, coalesce: true);

        display.UndoRedo.Undo();
        Assert.Equal(bStart, b.Window);
        Assert.NotEqual(aStart, a.Window);       // the first plot's entry is still on the stack

        display.UndoRedo.Undo();
        Assert.Equal(aStart, a.Window);
    }

    // ---- the wiring --------------------------------------------------

    /// <summary>PlotControl records the pan on release, the wheel zoom coalesced, and Autoscale.
    /// The control is not instantiated by this suite, so this is asserted against the source with
    /// comments stripped.</summary>
    [Fact]
    public void PlotControl_RecordsPanZoomAndAutoscale()
    {
        string code = StripComments(File.ReadAllText(SourceFile("src/Ui/DataDisplay/Controls/PlotControl.cs")));

        Assert.Contains("PushAxesWindowChange(_panUndoWindow, _panUndoSecondary)", code);
        Assert.Contains("PushAxesWindowChange(beforeWindow, beforeSecondary, coalesce: true)", code);
        Assert.Contains("PushAxesWindowChange(beforeWindow, beforeSecondary)", code);

        // The "before" windows must be captured at press, not read back off WindowState — the pan
        // rewrites that as it goes, so it no longer holds where the gesture started.
        Assert.Contains("_panUndoWindow                   = _plot.Axes.Window;", code);
    }

    private static string SourceFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }
}
