// ================================================================
//  PlotPanLockDefaultTests.cs — who owns a left-drag inside a plot (2026-08-21)
//
//  A drag inside a Data Display plot is EITHER an axis pan OR the container's
//  move/select gesture. It is one gesture, and only one of them can have it —
//  which is why panning was unreachable: PlotContainerView hard-disabled it and
//  always moved the plot instead.
//
//  Axes.LockedPanning is now the switch, every new plot starts LOCKED (so moving
//  and selecting behave exactly as before), and unlocking a plot hands its drag —
//  and its plain scroll wheel — to the axes.
//
//  PlotControl/PlotContainerView are Avalonia controls that this suite does not
//  instantiate, so the wiring is gated against the source with comments stripped;
//  everything expressible on the model is gated on the model.
// ================================================================

using System;
using System.IO;
using System.Text.RegularExpressions;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PlotPanLockDefaultTests
{
    // ---- the default -------------------------------------------------

    [Theory]
    [InlineData(PlotType.Rect)]
    [InlineData(PlotType.Smith)]
    [InlineData(PlotType.Polar)]
    [InlineData(PlotType.Table)]
    public void NewPlot_StartsWithAxisPanningLocked(PlotType type)
    {
        var plot = new Plot(type, FreqUnit.GHz);
        Assert.True(plot.Axes.LockedPanning);
    }

    /// <summary>The route the user actually takes — Add Plot — not just the constructor.</summary>
    [Fact]
    public void AddPlot_ProducesALockedPlot()
    {
        var display = new DataDisplayViewModel(new DataSourceLibraryViewModel(), addEmptyPlot: false);
        display.CanvasSizeProvider = () => (800.0, 600.0);

        var container = display.AddPlot(PlotType.Rect);
        Assert.True(container.PlotVM.Plot.Axes.LockedPanning);
    }

    /// <summary>A bare <c>new Plot()</c> is NOT given the default: it is the deserialization/
    /// placeholder shape, and the Data Display never creates one. Pinned so the default is
    /// understood to belong to the "new plot" constructor rather than to Axes itself, where it
    /// would be asserting something untrue about every other consumer of Axes (harmonicaRF's
    /// panels never read LockedPanning at all).</summary>
    [Fact]
    public void AxesItself_DoesNotDefaultToLocked()
    {
        Assert.False(new Axes().LockedPanning);
    }

    // ---- the lock survives a plot-type change ------------------------

    /// <summary>Each plot type keeps its own Axes, so a type change swaps the whole object — and a
    /// type visited for the first time gets a brand-new one. Locked/unlocked is a property of the
    /// PLOT, not of the type it is being viewed as; without carrying it, a locked plot became
    /// undraggable the moment its type changed.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PlotTypeChange_KeepsTheLockState(bool locked)
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Axes.LockedPanning = locked;

        plot.SetPlotType(PlotType.Smith);            // a type never visited → fresh Axes
        Assert.Equal(locked, plot.Axes.LockedPanning);

        plot.SetPlotType(PlotType.Rect);             // a type already in storage
        Assert.Equal(locked, plot.Axes.LockedPanning);

        plot.SetPlotType(PlotType.Polar);
        Assert.Equal(locked, plot.Axes.LockedPanning);
    }

    /// <summary>Toggling the lock and changing type in either order lands in the same place.</summary>
    [Fact]
    public void PlotTypeChange_ThenUnlock_ThenChangeAgain_StaysUnlocked()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.SetPlotType(PlotType.Smith);
        plot.Axes.LockedPanning = false;
        plot.SetPlotType(PlotType.Rect);
        Assert.False(plot.Axes.LockedPanning);
    }

    // ---- the wiring --------------------------------------------------

    /// <summary>PlotContainerView must not hard-disable panning any more — that override is what
    /// made Lock Axes Panning inert, since the container took every left-drag regardless.</summary>
    [Fact]
    public void PlotContainerView_DoesNotHardDisablePanning()
    {
        string xaml = File.ReadAllText(SourceFile("src/Ui/Views/DataDisplay/PlotContainerView.axaml"));
        xaml = Regex.Replace(xaml, "<!--.*?-->", "", RegexOptions.Singleline);
        Assert.DoesNotContain("EnablePanning=\"False\"", xaml);
    }

    /// <summary>PlotControl takes a left-drag only when the plot is UNLOCKED, and marks the event
    /// handled when it does — the handled flag is what stops the container from also moving the
    /// plot out from under the pan, and what turns selection off while unlocked.</summary>
    [Fact]
    public void PlotControl_StartsAPanOnlyWhenUnlocked_AndMarksItHandled()
    {
        string code = StripComments(File.ReadAllText(SourceFile("src/Ui/DataDisplay/Controls/PlotControl.cs")));

        var m = Regex.Match(code, @"if \(EnablePanning && !_plot\.Axes\.LockedPanning\)\s*\{(?<body>[^}]*)\}");
        Assert.True(m.Success, "the pan gesture must be gated on both EnablePanning and !LockedPanning");
        Assert.Contains("_isDragging", m.Groups["body"].Value);
        Assert.Contains("e.Handled", m.Groups["body"].Value);
    }

    /// <summary>A plain scroll zooms the plot's own axes while it is unlocked; a locked plot returns
    /// unhandled so the wheel reaches the Data Display canvas, which is what "when mouse is outside
    /// the plot, the scroll wheel zooms the data display instead" relies on.</summary>
    [Fact]
    public void PlotControl_PlainScrollZoomsTheAxesOnlyWhenUnlocked()
    {
        string code = StripComments(File.ReadAllText(SourceFile("src/Ui/DataDisplay/Controls/PlotControl.cs")));
        Assert.Contains("if (!ctrl && _plot.Axes.LockedPanning) return;", code);
        Assert.DoesNotContain("if (!ctrl) return;", code);
    }

    /// <summary>A plot is selectable in BOTH lock states. When locked, PlotContainerView's own
    /// click handler does it (this control leaves the press unhandled). When unlocked, the press is
    /// handled here to keep the pan — and the pointer capture with it — so the click half is
    /// reproduced here rather than handed back, at the same 4 px stillness threshold the container
    /// uses. Only the DRAG differs between the two states.</summary>
    [Fact]
    public void PlotControl_ClickWithoutDragging_SelectsThePlot()
    {
        string code = StripComments(File.ReadAllText(SourceFile("src/Ui/DataDisplay/Controls/PlotControl.cs")));

        int at = code.IndexOf("if (_isDragging || _isDraggingSecondary)", StringComparison.Ordinal);
        Assert.True(at >= 0);
        string body = code.Substring(at, Math.Min(1800, code.Length - at));

        Assert.Contains("ClickSelectThreshold", body);
        Assert.Contains("RequestSelectOnly()", body);
        Assert.Contains("RequestToggleSelect()", body);

        // The right-button (secondary-axis) drag must NOT select — matching the container, which
        // ignores anything but the left button.
        Assert.Contains("bool wasLeftDrag = _isDragging;", body);
        Assert.Contains("if (wasLeftDrag &&", body);
    }

    /// <summary>The two thresholds answer the same question about the same gesture and must agree,
    /// or a plot's click-to-select would need a different amount of stillness than the one beside
    /// it.</summary>
    [Fact]
    public void ClickSelectThreshold_MatchesTheContainersDragThreshold()
    {
        string control   = File.ReadAllText(SourceFile("src/Ui/DataDisplay/Controls/PlotControl.cs"));
        string container = File.ReadAllText(SourceFile("src/Ui/Views/DataDisplay/PlotContainerView.axaml.cs"));

        var a = Regex.Match(control,   @"ClickSelectThreshold\s*=\s*([0-9.]+)");
        var b = Regex.Match(container, @"DragThreshold\s*=\s*([0-9.]+)");
        Assert.True(a.Success && b.Success);
        Assert.Equal(double.Parse(b.Groups[1].Value), double.Parse(a.Groups[1].Value));
    }

    // ---- helpers ------------------------------------------------------

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
