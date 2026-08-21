// ================================================================
//  PlotAutoscaleCommandTests.cs — the plot context menu's "Autoscale" (2026-08-21)
//
//  Owner: "There seems to be a bug with the Autoscale command from the plot context
//  menu. The plot doesn't render as autoscaled after I issue the command. (I need to
//  start a pan to see it render properly)"
//
//  Two defects in the one handler, both silent:
//    1. It never invalidated the visual. Autoscale mutates the axes model, which raises
//       no notification, so the new window was correct but unpainted until some later
//       gesture happened to trigger a repaint.
//    2. It called Plot.Autoscale() unforced, which is gated on the per-axis autoscale
//       flags — flags the Axes Limits panel clears as soon as a user types a limit. So
//       the command did nothing at all for exactly the user who would reach for it.
//
//  PlotControl is an Avalonia control this suite does not instantiate, so the repaint is
//  gated against the source with comments stripped; the force is gated on the model.
// ================================================================

using System;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Ui.DataDisplay;
using RfCore;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PlotAutoscaleCommandTests
{
    private static readonly double[] Freqs = [1e9, 2e9, 3e9, 4e9, 5e9];

    private static Plot PlotWithATrace()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var snp  = new SNP(Freqs, 2);
        for (int f = 0; f < Freqs.Length; f++)
            snp.Matrices[f][0, 0] = Complex.FromPolarCoordinates(0.1 + 0.15 * f, 0.3 * f);
        var trace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        trace.BuildPath(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(trace);
        return plot;
    }

    /// <summary>Asserts the window actually frames the data, rather than asserting a particular
    /// padding rule — the point is that autoscale HAPPENED, not how much margin it leaves.</summary>
    private static void AssertFramesTheData(Plot plot)
    {
        var r = plot.Traces[0].PathBoundingRect();
        var w = plot.Axes.Window;
        Assert.True(w.Left <= r.X + 1e-9 && w.Right  >= r.X + r.Width  - 1e-9,
            $"window X [{w.Left},{w.Right}] does not frame data [{r.X},{r.X + r.Width}]");
        Assert.True(w.Top  <= r.Y + 1e-9 && w.Bottom >= r.Y + r.Height - 1e-9,
            $"window Y [{w.Top},{w.Bottom}] does not frame data [{r.Y},{r.Y + r.Height}]");
    }

    /// <summary>With the autoscale flags off — the state the Axes Limits panel leaves behind — an
    /// unforced Autoscale is a no-op, and the forced one the menu now issues still works.</summary>
    [Fact]
    public void Autoscale_WithFlagsOff_IsANoOpUnlessForced()
    {
        var plot = PlotWithATrace();
        plot.AutoscaleX = plot.AutoscaleY = plot.AutoscaleRightY = false;

        var strayed = new Avalonia.Rect(90.0, 900.0, 4.0, 40.0);   // nowhere near the data
        plot.Axes.Window = strayed;

        plot.Autoscale();                       // unforced: gated off, changes nothing
        Assert.Equal(strayed, plot.Axes.Window);

        plot.Autoscale(force: true);            // what the menu command now issues
        Assert.NotEqual(strayed, plot.Axes.Window);
        AssertFramesTheData(plot);
    }

    /// <summary>Forcing must not rewrite the user's standing preference — it bypasses the gate for
    /// this one call, so a later automatic autoscale still respects what they chose.</summary>
    [Fact]
    public void Autoscale_Forced_DoesNotReEnableTheFlags()
    {
        var plot = PlotWithATrace();
        plot.AutoscaleX = plot.AutoscaleY = plot.AutoscaleRightY = false;

        plot.Autoscale(force: true);

        Assert.False(plot.AutoscaleX);
        Assert.False(plot.AutoscaleY);
        Assert.False(plot.AutoscaleRightY);
    }

    /// <summary>With the flags on, forced and unforced agree — forcing changes reachability, not
    /// the arithmetic.</summary>
    [Fact]
    public void Autoscale_WithFlagsOn_ForcedMatchesUnforced()
    {
        var a = PlotWithATrace();
        var b = PlotWithATrace();
        a.Axes.Window = b.Axes.Window = new Avalonia.Rect(90.0, 900.0, 4.0, 40.0);

        a.Autoscale();
        b.Autoscale(force: true);

        Assert.Equal(a.Axes.Window, b.Axes.Window);
        AssertFramesTheData(a);
    }

    /// <summary>The menu handler forces the autoscale AND repaints. Autoscale raises no
    /// notification, so without the explicit invalidate the new window is simply never drawn.</summary>
    [Fact]
    public void MenuHandler_ForcesTheAutoscale_AndInvalidatesTheVisual()
    {
        string code = StripComments(File.ReadAllText(SourceFile("src/Ui/DataDisplay/Controls/PlotControl.cs")));

        var m = Regex.Match(code, @"OnMenuActionAutoscale\([^)]*\)\s*\{(?<body>.*?)\n        \}",
                            RegexOptions.Singleline);
        Assert.True(m.Success, "OnMenuActionAutoscale not found");
        string body = m.Groups["body"].Value;

        Assert.Contains("Autoscale(force: true)", body);
        Assert.Contains("InvalidateVisual()", body);
        Assert.Contains("PlotChanged?.Invoke", body);
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
