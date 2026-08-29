// ================================================================
//  PanAndMarkerLabelTests.cs — three owner-reported plot bugs, 2026-08-21
//
//   1. With Lock Axes Panning OFF, dragging inside a plot made the RIGHT-Y traces
//      "glitch out". Axes.Translate converted the pointer delta once, with the PRIMARY
//      scale, and applied that world number to the secondary window too — so the right
//      axis panned by the wrong world distance whenever the two Y ranges differed.
//
//   2. A marker's glyph panned OUT of the plotting area. PlotRenderer drew marker symbols
//      after restoring the trace clip, so the triangle and its name kept going over the
//      tick labels and beyond the axes. Smith/Polar keep the SQUARE viewport clip, so a
//      marker may still sit in a corner outside the chart circle.
//
//   3. The marker readouts said "dB(S(1,1))" while the Y-axis label said "S(1,1) dB20".
//      Both now come through TraceLabeler.QuantityFor — one table, so they cannot drift.
//
//  SkiaFonts.PlexBold cannot load headlessly (src/Ui/CLAUDE.md) — TestOverrideTypeface
//  substitutes SKTypeface.Default for the render tests.
// ================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Renderers;
using RfCore;
using SkiaSharp;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PanAndMarkerLabelTests
{
    public PanAndMarkerLabelTests() => SkiaFonts.TestOverrideTypeface = SKTypeface.Default;

    private static readonly double[] Freqs = [1e9, 2e9, 3e9, 4e9, 5e9];

    private static (double W, double H) Canvas => (800.0, 500.0);

    /// <summary>A Rect plot with deliberately MISMATCHED Y ranges — the left axis spans 40 dB,
    /// the right one 360°, exactly the Match Designer shape the owner reported against.</summary>
    private static Plot TwoAxisPlot()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        plot.Axes.Window          = new Avalonia.Rect(1e9, -40.0, 4e9,  40.0);
        plot.Axes.WindowSecondary = new Avalonia.Rect(1e9, -180.0, 4e9, 360.0);
        plot.Axes.ShowSecondary   = true;
        plot.Axes.LockedPanning   = false;   // a new plot starts LOCKED; these tests pan it
        return plot;
    }

    private static void SnapshotDragStart(Axes axes)
    {
        axes.WindowState          = axes.Window;
        axes.WindowSecondaryState = axes.WindowSecondary;
    }

    // ================================================================
    //  1 — panning
    // ================================================================

    /// <summary>The point under the pointer stays under the pointer on BOTH axes.</summary>
    [Fact]
    public void Pan_BothAxesTrackThePointer_WhenYRangesDiffer()
    {
        var plot = TwoAxisPlot();
        var tf0  = PlotRenderer.BuildTransforms(plot, Canvas);

        // Two world points, one per axis, and where each sits on the canvas before the drag.
        const double xw = 3e9, yPrimary = -12.0, ySecondary = 45.0;
        var beforePrimary   = tf0.PrimaryToCanvas(xw, yPrimary);
        var beforeSecondary = tf0.SecondaryToCanvas(xw, ySecondary);

        const double dxPx = -37.0, dyPx = 61.0;
        SnapshotDragStart(plot.Axes);
        plot.Axes.TranslateFromPointer(dxPx, dyPx,
            tf0.Primary.XScale,   tf0.Primary.YScale,
            tf0.Secondary.XScale, tf0.Secondary.YScale);

        var tf1 = PlotRenderer.BuildTransforms(plot, Canvas);
        var afterPrimary   = tf1.PrimaryToCanvas(xw, yPrimary);
        var afterSecondary = tf1.SecondaryToCanvas(xw, ySecondary);

        // Each world point has moved by exactly the pointer delta — the definition of a pan
        // that tracks the cursor.
        Assert.Equal(beforePrimary.X   + dxPx, afterPrimary.X,   3);
        Assert.Equal(beforePrimary.Y   + dyPx, afterPrimary.Y,   3);
        Assert.Equal(beforeSecondary.X + dxPx, afterSecondary.X, 3);
        Assert.Equal(beforeSecondary.Y + dyPx, afterSecondary.Y, 3);
    }

    /// <summary>The bug itself: the primary scale applied to the secondary window is off by the
    /// ratio of the two Y ranges — here 9×, which is what threw the right-axis traces off the plot.
    /// Pins that the two conversions are genuinely different numbers, so a future single-scale
    /// "simplification" fails here rather than in the user's hands.</summary>
    [Fact]
    public void Pan_SecondaryUsesItsOwnScale_NotThePrimarys()
    {
        var plot = TwoAxisPlot();
        var tf   = PlotRenderer.BuildTransforms(plot, Canvas);

        const double dyPx = 50.0;
        double y2Before = plot.Axes.WindowSecondary.Y;

        SnapshotDragStart(plot.Axes);
        plot.Axes.TranslateFromPointer(0, dyPx,
            tf.Primary.XScale,   tf.Primary.YScale,
            tf.Secondary.XScale, tf.Secondary.YScale);

        double shift        = y2Before - plot.Axes.WindowSecondary.Y;
        double correct      = dyPx / tf.Secondary.YScale;
        double oldWrongWay  = dyPx / tf.Primary.YScale;

        Assert.Equal(correct, shift, 6);
        Assert.NotEqual(oldWrongWay, correct, 6);
        // 40 dB left vs 360° right — the right axis used to pan 9× too little.
        Assert.Equal(9.0, correct / oldWrongWay, 6);
    }

    [Fact]
    public void Pan_LockedAxes_MovesNeitherWindow()
    {
        var plot = TwoAxisPlot();
        plot.Axes.LockedPanning = true;
        var tf = PlotRenderer.BuildTransforms(plot, Canvas);

        var w0  = plot.Axes.Window;
        var w20 = plot.Axes.WindowSecondary;

        SnapshotDragStart(plot.Axes);
        plot.Axes.TranslateFromPointer(25, 40,
            tf.Primary.XScale,   tf.Primary.YScale,
            tf.Secondary.XScale, tf.Secondary.YScale);

        Assert.Equal(w0,  plot.Axes.Window);
        Assert.Equal(w20, plot.Axes.WindowSecondary);
    }

    /// <summary>A single-axis plot is unchanged: no secondary window is touched when the plot
    /// does not show one.</summary>
    [Fact]
    public void Pan_NoSecondaryAxis_LeavesSecondaryWindowAlone()
    {
        var plot = TwoAxisPlot();
        plot.Axes.ShowSecondary = false;
        var tf = PlotRenderer.BuildTransforms(plot, Canvas);

        var w20 = plot.Axes.WindowSecondary;
        SnapshotDragStart(plot.Axes);
        plot.Axes.TranslateFromPointer(25, 40,
            tf.Primary.XScale,   tf.Primary.YScale,
            tf.Secondary.XScale, tf.Secondary.YScale);

        Assert.Equal(w20, plot.Axes.WindowSecondary);
        Assert.NotEqual(new Avalonia.Rect(1e9, -40.0, 4e9, 40.0), plot.Axes.Window);
    }

    // ---- the sub-pixel shimmer -------------------------------------

    /// <summary>A Rect plot with two traces, one per axis — the shape whose margins the shimmer
    /// tests photograph.</summary>
    private static Plot TwoAxisPlotWithTraces()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var snp  = new SNP(Freqs, 2);
        for (int f = 0; f < Freqs.Length; f++)
        {
            snp.Matrices[f][0, 0] = Complex.FromPolarCoordinates(0.5, 0.3 * f);
            snp.Matrices[f][1, 0] = Complex.FromPolarCoordinates(2.0, 0.2 * f);
        }
        var left  = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Db);
        var right = new Trace(snp, MatrixType.S, 1, 0, DependentVarFormat.Phase) { UseSecondaryAxis = true };
        left.BuildPath(PlotType.Rect, FreqUnit.GHz);
        right.BuildPath(PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(left);
        plot.Traces.Add(right);
        plot.SetAxesViewport();
        plot.Axes.Window          = new Avalonia.Rect(1.0, -40.0, 4.0, 40.0);
        plot.Axes.WindowSecondary = new Avalonia.Rect(1.0, -180.0, 4.0, 360.0);
        plot.Axes.ShowSecondary   = true;
        plot.Axes.LockedPanning   = false;   // a new plot starts LOCKED; these tests pan it
        return plot;
    }

    /// <summary>Nobody drags along an exact axis. A mostly-horizontal drag carries a few tenths of
    /// a pixel of Y, and unrounded that repainted the whole Y tick column at a new sub-pixel phase
    /// on every pointer event — the owner's "wiggle". Both Y windows must be untouched.</summary>
    [Fact]
    public void Pan_SubPixelOrthogonalJitter_LeavesTheOtherAxisExactlyWhereItWas()
    {
        var plot = TwoAxisPlot();
        var tf   = PlotRenderer.BuildTransforms(plot, Canvas);
        double y0 = plot.Axes.Window.Y, y20 = plot.Axes.WindowSecondary.Y;

        foreach (var (dx, dy) in new[] { (3.0, 0.31), (7.0, -0.22), (11.0, 0.44), (14.0, -0.49) })
        {
            SnapshotDragStart(plot.Axes);
            plot.Axes.TranslateFromPointer(dx, dy,
                tf.Primary.XScale, tf.Primary.YScale, tf.Secondary.XScale, tf.Secondary.YScale);

            Assert.Equal(y0,  plot.Axes.Window.Y);
            Assert.Equal(y20, plot.Axes.WindowSecondary.Y);
            Assert.NotEqual(1.0, plot.Axes.Window.X);   // the intended axis DID move
        }
    }

    /// <summary>The mirror case the owner also reported: a mostly-vertical drag must not nudge X.</summary>
    [Fact]
    public void Pan_SubPixelOrthogonalJitter_LeavesTheXAxisExactlyWhereItWas()
    {
        var plot = TwoAxisPlot();
        var tf   = PlotRenderer.BuildTransforms(plot, Canvas);
        double x0 = plot.Axes.Window.X;

        foreach (var (dx, dy) in new[] { (0.29, 4.0), (-0.31, 9.0), (0.12, 15.0) })
        {
            SnapshotDragStart(plot.Axes);
            plot.Axes.TranslateFromPointer(dx, dy,
                tf.Primary.XScale, tf.Primary.YScale, tf.Secondary.XScale, tf.Secondary.YScale);

            Assert.Equal(x0, plot.Axes.Window.X);
            Assert.NotEqual(-40.0, plot.Axes.Window.Y);
        }
    }

    /// <summary>The second half of the fix: the axis that IS panning translates by an exact whole
    /// number of pixels, so every tick and glyph keeps its sub-pixel phase instead of being
    /// re-rasterized at a new one. Asserted on the canvas position of a tick, which is what the
    /// user actually sees.</summary>
    [Fact]
    public void Pan_MovesTicksByWholePixels_SoTheyKeepTheirSubPixelPhase()
    {
        var plot = TwoAxisPlot();
        var tf0  = PlotRenderer.BuildTransforms(plot, Canvas);
        const double tickWorldX = 3.0, tickWorldY = -12.0;
        var before = tf0.PrimaryToCanvas(tickWorldX, tickWorldY);

        SnapshotDragStart(plot.Axes);
        plot.Axes.TranslateFromPointer(17.62, -9.37,
            tf0.Primary.XScale, tf0.Primary.YScale, tf0.Secondary.XScale, tf0.Secondary.YScale);

        var after = PlotRenderer.BuildTransforms(plot, Canvas).PrimaryToCanvas(tickWorldX, tickWorldY);

        Assert.Equal(18.0, after.X - before.X, 3);
        Assert.Equal(-9.0, after.Y - before.Y, 3);
    }

    /// <summary>The right-button drag pans the secondary axis alone and is quantized the same way —
    /// its own numbers shimmer under a sub-pixel delta just as readily.</summary>
    [Fact]
    public void Pan_RightDragOnSecondaryAxis_IsAlsoWholePixel()
    {
        var plot = TwoAxisPlot();
        var tf   = PlotRenderer.BuildTransforms(plot, Canvas);
        double y20 = plot.Axes.WindowSecondary.Y;

        SnapshotDragStart(plot.Axes);
        plot.Axes.TranslateSecondaryFromPointer(0.4, tf.Secondary.YScale);
        Assert.Equal(y20, plot.Axes.WindowSecondary.Y);

        SnapshotDragStart(plot.Axes);
        plot.Axes.TranslateSecondaryFromPointer(6.7, tf.Secondary.YScale);
        Assert.Equal(y20 - 7.0 / tf.Secondary.YScale, plot.Axes.WindowSecondary.Y, 9);
    }

    /// <summary>The user-visible gate for everything either side of the plot box: photograph both
    /// Y-number margins across a mostly-horizontal drag with realistic sub-pixel jitter. Every
    /// frame must be pixel-identical there.
    ///
    /// <para>It catches two independent defects, and was verified to fail for each on its own.
    /// Without the whole-pixel rounding: ~700 changed pixels on the left axis and ~900 on the
    /// right, every frame — the right one worse because SecondaryShareGrid derives its tick VALUES
    /// from (y − Window.Top) / Window.Height, so a sub-pixel Y change re-NUMBERS it rather than
    /// merely re-placing it. Without the Rect grid clip: a vertical gridline landing on the axis
    /// boundary paints its outer half one pixel beyond it, and since the lattice is absolute that
    /// line appears and disappears as the window moves — a one-pixel column flickering on top of
    /// the border.</para>
    ///
    /// <para>A single-frame "is there ink outside the box" check cannot gate the clip: the escaped
    /// gridline hides inside the border's own stroke, which is deliberately unclipped, and the tick
    /// numbers out there swamp any diff. Comparing FRAMES cancels both — they are static — and
    /// leaves only what the pan is wrongly moving.</para></summary>
    [Fact]
    public void Pan_HorizontalDragWithJitter_LeavesBothYNumberColumnsPixelIdentical()
    {
        (double Dx, double Dy)[] drag = [(0, 0), (3, 0.31), (6, -0.22), (9, 0.44), (12, 0.05), (15, -0.37)];
        var clip = PlotRenderer.ViewportClipRect(TwoAxisPlotWithTraces().Axes.Viewport, Canvas);

        SKBitmap? prev = null;
        foreach (var (dx, dy) in drag)
        {
            var plot = TwoAxisPlotWithTraces();
            var tf   = PlotRenderer.BuildTransforms(plot, Canvas);
            SnapshotDragStart(plot.Axes);
            plot.Axes.TranslateFromPointer(dx, dy,
                tf.Primary.XScale, tf.Primary.YScale, tf.Secondary.XScale, tf.Secondary.YScale);

            var bmp = Render(plot);
            if (prev is not null)
            {
                int changed = 0;
                for (int y = (int)clip.Top; y <= (int)clip.Bottom; y++)
                {
                    for (int x = 0; x < (int)clip.Left; x++)
                        if (prev.GetPixel(x, y) != bmp.GetPixel(x, y)) changed++;
                    for (int x = (int)clip.Right + 1; x < bmp.Width; x++)
                        if (prev.GetPixel(x, y) != bmp.GetPixel(x, y)) changed++;
                }
                Assert.Equal(0, changed);
                prev.Dispose();
            }
            prev = bmp;
        }
        prev?.Dispose();
    }

    // ---- gridline shade flicker, and ink outside the box -----------

    /// <summary>The pixel a tick lands on, for a given axis window and scale.</summary>
    private static double TickPx(double tickWorld, double windowLeft, double scale)
        => (tickWorld - windowLeft) * scale;

    /// <summary>No minor tick may land on a major tick's pixel. The old dedup compared VALUES with
    /// exact double equality, and with a spacing of 0.2 — which CalcInterval returns constantly,
    /// and which has no exact binary form — five accumulated 0.2s are not bit-equal to one
    /// accumulated 1.0. The minor gridline was then painted over the major one in the lighter minor
    /// paint, so three of every four majors rendered in the wrong shade.</summary>
    [Fact]
    public void Ticks_NoMinorLandsOnAMajorsPixel()
    {
        var axes = new Axes { Window = new Avalonia.Rect(1.0, -40.0, 4.0, 40.0) };
        const double xScale = 630.0 / 4.0, yScale = 420.0 / 40.0;

        for (int k = 0; k <= 400; k++)
        {
            axes.WindowState = new Avalonia.Rect(1.0, -40.0, 4.0, 40.0);
            axes.Translate(k / xScale, 0);
            var t = axes.Ticks(minorTicks: true);

            foreach (var minor in t.MinorX)
                foreach (var major in t.MajorX)
                    Assert.True(Math.Abs(minor - major) * xScale >= 0.25,
                        $"minor {minor} overdraws major {major} at pan step {k}");

            foreach (var minor in t.MinorY)
                foreach (var major in t.MajorY.Select(p => p.Primary).Where(double.IsFinite))
                    Assert.True(Math.Abs(minor - major) * yScale >= 0.25,
                        $"minor {minor} overdraws major {major} at pan step {k}");
        }
    }

    /// <summary>A gridline's world value must not depend on the pan offset it was generated at.
    /// Walking the axis by repeated addition made it depend on how many additions it took to get
    /// there — and that count changes as you pan — so the same gridline drifted, sub-pixel, frame
    /// to frame. Index multiplication makes it exact.</summary>
    [Fact]
    public void Ticks_SameGridlineHasTheSameValueAtEveryPanOffset()
    {
        var axes = new Axes { Window = new Avalonia.Rect(1.0, -40.0, 4.0, 40.0) };
        const double xScale = 630.0 / 4.0;
        var seen = new Dictionary<long, double>();

        for (int k = 0; k <= 400; k++)
        {
            axes.WindowState = new Avalonia.Rect(1.0, -40.0, 4.0, 40.0);
            axes.Translate(k / xScale, 0);
            foreach (var tx in axes.Ticks(minorTicks: true).MinorX.Concat(axes.Ticks(true).MajorX))
            {
                long lattice = (long)Math.Round(tx / 0.2);
                if (seen.TryGetValue(lattice, out double first))
                    Assert.Equal(first, tx);          // bit-exact, not approximately
                else
                    seen[lattice] = tx;
            }
        }
        Assert.True(seen.Count > 20);
    }

    /// <summary>Ticks never leave the axis limits, at any pan offset or sign of the window.</summary>
    [Theory]
    [InlineData(1.0,  4.0, -40.0,  40.0)]
    [InlineData(0.0,  4.0, -40.0,  40.0)]
    [InlineData(-2.0, 5.0,  -1.0,   2.0)]
    [InlineData(1.0,  4.0, -180.0, 360.0)]
    public void Ticks_NeverLeaveTheWindow(double x0, double w, double y0, double h)
    {
        var axes = new Axes { Window = new Avalonia.Rect(x0, y0, w, h) };
        for (int k = 0; k <= 200; k++)
        {
            axes.WindowState = new Avalonia.Rect(x0, y0, w, h);
            axes.Translate(w * k / 2000.0, h * k / 2000.0);
            var t   = axes.Ticks(minorTicks: true);
            var win = axes.Window;

            foreach (var v in t.MajorX.Concat(t.MinorX))
                Assert.InRange(v, win.Left - 1e-9 * Math.Abs(win.Left),
                                  win.Right + 1e-9 * Math.Abs(win.Right));
            foreach (var v in t.MajorY.Select(p => p.Primary).Where(double.IsFinite).Concat(t.MinorY))
                Assert.InRange(v, win.Top - 1e-9 * Math.Abs(win.Top),
                                  win.Bottom + 1e-9 * Math.Abs(win.Bottom));
        }
    }

    // ---- the draw operation must render a SNAPSHOT ------------------

    /// <summary>The frame a draw operation paints must be immune to pans that happen after it was
    /// handed the plot. This is the whole defect: the compositor runs the operation later, and it
    /// was reading the LIVE axes, so tick values and the transform could come from two different
    /// windows within one frame.</summary>
    [Fact]
    public void RenderSnapshot_IsNotDisturbedByAPanTakenAfterIt()
    {
        var plot     = TwoAxisPlotWithTraces();
        var snapshot = plot.RenderSnapshot();
        var before   = Render(snapshot);

        // Everything a fast drag does between recording the operation and executing it.
        var tf = PlotRenderer.BuildTransforms(plot, Canvas);
        for (int i = 1; i <= 12; i++)
        {
            SnapshotDragStart(plot.Axes);
            plot.Axes.TranslateFromPointer(23.0 * i, -11.0 * i,
                tf.Primary.XScale, tf.Primary.YScale, tf.Secondary.XScale, tf.Secondary.YScale);
        }
        Assert.NotEqual(1.0, plot.Axes.Window.X);       // the live plot really did move

        var after = Render(snapshot);
        int changed = 0;
        for (int y = 0; y < before.Height; y++)
        for (int x = 0; x < before.Width;  x++)
            if (before.GetPixel(x, y) != after.GetPixel(x, y)) changed++;

        Assert.Equal(0, changed);
        before.Dispose(); after.Dispose();
    }

    /// <summary>The snapshot must be a WHOLE plot, not just its window — the renderers read a long
    /// tail of plot state, and a copy that forgets one of them renders the frame wrong rather than
    /// failing. Traces are shared on purpose: their geometry is rebuilt only on a structural
    /// change, never during a pan.</summary>
    [Fact]
    public void RenderSnapshot_CarriesEveryRenderedProperty_AndSharesTraces()
    {
        var plot = TwoAxisPlotWithTraces();
        plot.ShowWatermark    = false;
        plot.CustomXLabel     = "freq";
        plot.CustomXLabelOn   = true;
        plot.CustomTitle      = "Custom";
        plot.CustomTitleOn    = true;
        plot.CustomTitleBold  = true;
        plot.ColumnWidth      = 77;
        plot.FontSize         = 13;

        var snap = plot.RenderSnapshot();

        Assert.Equal(plot.Title,           snap.Title);
        Assert.Equal(plot.PlotType,        snap.PlotType);
        Assert.Equal(plot.FreqUnits,       snap.FreqUnits);
        Assert.Equal(plot.ShowWatermark,   snap.ShowWatermark);
        Assert.Equal(plot.CustomXLabel,    snap.CustomXLabel);
        Assert.Equal(plot.CustomXLabelOn,  snap.CustomXLabelOn);
        Assert.Equal(plot.CustomTitle,     snap.CustomTitle);
        Assert.Equal(plot.CustomTitleOn,   snap.CustomTitleOn);
        Assert.Equal(plot.CustomTitleBold, snap.CustomTitleBold);
        Assert.Equal(plot.ColumnWidth,     snap.ColumnWidth);
        Assert.Equal(plot.FontSize,        snap.FontSize);

        Assert.Equal(plot.Traces.Count, snap.Traces.Count);
        for (int i = 0; i < plot.Traces.Count; i++)
            Assert.Same(plot.Traces[i], snap.Traces[i]);

        // The axes are the one thing that is NOT shared.
        Assert.NotSame(plot.Axes, snap.Axes);
        Assert.Equal(plot.Axes.Window,          snap.Axes.Window);
        Assert.Equal(plot.Axes.WindowSecondary, snap.Axes.WindowSecondary);
        Assert.Equal(plot.Axes.Viewport,        snap.Axes.Viewport);
        Assert.Equal(plot.Axes.XTick,           snap.Axes.XTick);
        Assert.Equal(plot.Axes.YTick,           snap.Axes.YTick);
        Assert.Equal(plot.Axes.Y2Tick,          snap.Axes.Y2Tick);
        Assert.Equal(plot.Axes.MajorX,          snap.Axes.MajorX);
        Assert.Equal(plot.Axes.ShowSecondary,   snap.Axes.ShowSecondary);
    }

    /// <summary>Taking a snapshot must not perturb the live plot — the trace collection is shared,
    /// and its CollectionChanged subscription belongs to the live plot, so a snapshot that touched
    /// it would fire an autoscale and move the window the user is dragging.</summary>
    [Fact]
    public void RenderSnapshot_DoesNotDisturbTheLivePlot()
    {
        var plot = TwoAxisPlotWithTraces();
        var window = plot.Axes.Window;
        var windowSecondary = plot.Axes.WindowSecondary;
        var axes = plot.Axes;

        for (int i = 0; i < 5; i++) plot.RenderSnapshot();

        Assert.Same(axes, plot.Axes);
        Assert.Equal(window, plot.Axes.Window);
        Assert.Equal(windowSecondary, plot.Axes.WindowSecondary);
        Assert.Equal(2, plot.Traces.Count);
    }

    /// <summary>PlotControl must hand the draw operation a snapshot. The compositor thread cannot
    /// be driven headlessly, so this is asserted against the source — comments stripped, so the
    /// explanatory comment beside the call cannot satisfy it.</summary>
    [Fact]
    public void PlotControl_HandsTheDrawOperationASnapshot()
    {
        string path = SourceFile("src/Ui/DataDisplay/Controls/PlotControl.cs");
        string code = StripComments(File.ReadAllText(path));
        int at = code.IndexOf("new PlotDrawOperation(", StringComparison.Ordinal);
        Assert.True(at >= 0, "PlotDrawOperation construction not found");

        string args = code.Substring(at, Math.Min(400, code.Length - at));
        Assert.Contains("RenderSnapshot()", args);
        Assert.DoesNotMatch(@"new PlotDrawOperation\(\s*new Rect\(Bounds\.Size\),\s*_plot\s*,", args);
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
        src = System.Text.RegularExpressions.Regex.Replace(src, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        return System.Text.RegularExpressions.Regex.Replace(src, @"//[^\n]*", "");
    }

    // ================================================================
    //  2 — marker glyph clipping
    // ================================================================

    private static Trace SweptTrace(PlotType plotType)
    {
        var snp = new SNP(Freqs, 2);
        for (int f = 0; f < Freqs.Length; f++)
            snp.Matrices[f][0, 0] = Complex.FromPolarCoordinates(0.5, 0.3 * f);
        var t = new Trace(snp, MatrixType.S, 0, 0,
                          plotType.IsComplex() ? DependentVarFormat.Complex : DependentVarFormat.Db);
        t.BuildPath(plotType, FreqUnit.GHz);
        return t;
    }

    private static SKBitmap Render(Plot plot)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)Canvas.W, (int)Canvas.H));
        surface.Canvas.Clear(SKColors.White);
        PlotRenderer.Draw(surface.Canvas, Canvas, plot, PlotDetail.Full, RenderTheme.Light,
                          watermarkOpacity: 0f);
        return SKBitmap.FromImage(surface.Snapshot());
    }

    /// <summary>Counts pixels that differ between two renders OUTSIDE the plot box. A differential
    /// render is the only honest oracle here — the margins already carry tick labels and axis
    /// text, so "is there ink out there" cannot separate the marker from the chrome.</summary>
    private static int PixelsDifferingOutsideViewport(SKBitmap a, SKBitmap b, Avalonia.Rect viewport)
    {
        var clip = PlotRenderer.ViewportClipRect(viewport, Canvas);
        int n = 0;
        for (int y = 0; y < a.Height; y++)
        for (int x = 0; x < a.Width;  x++)
        {
            if (x >= clip.Left && x <= clip.Right && y >= clip.Top && y <= clip.Bottom) continue;
            if (a.GetPixel(x, y) != b.GetPixel(x, y)) n++;
        }
        return n;
    }

    /// <summary>Pan the window until the marker's data point sits in the LEFT margin — beside the
    /// tick numbers, still well inside the canvas — and the glyph must contribute nothing there.
    ///
    /// <para>The margin has to be chosen, not guessed: pan far enough and the glyph lands off the
    /// BITMAP, where an unclipped renderer also writes no pixels and the test passes for the wrong
    /// reason. 3.15–5 GHz puts the 3 GHz marker at canvas x ≈ 68 px against a plot box that starts
    /// at 120 px — outside the axes, inside the image. Verified to fail with the clip removed.</para>
    /// </summary>
    [Fact]
    public void MarkerGlyph_PannedOutOfThePlotBox_DrawsNothingOutsideIt()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var trace = SweptTrace(PlotType.Rect);
        plot.Traces.Add(trace);

        // The X axis is in the plot's own FreqUnit (GHz), so the window is in GHz — not Hz.
        plot.Axes.Window = new Avalonia.Rect(3.15, -20.0, 1.85, 40.0);

        var marker = new Marker(trace, Freqs[2], isMulti: false, isDelta: false, index: 1);
        var glyphAt = PlotRenderer.BuildTransforms(plot, Canvas)
                                  .PrimaryToCanvas(trace.GetMarkerDataLocation(marker).X,
                                                   trace.GetMarkerDataLocation(marker).Y);
        var box = PlotRenderer.ViewportClipRect(plot.Axes.Viewport, Canvas);
        Assert.True(glyphAt.X < box.Left, "fixture must put the glyph LEFT of the plot box");
        Assert.InRange(glyphAt.X, 0f, (float)Canvas.W);
        Assert.InRange(glyphAt.Y, 0f, (float)Canvas.H);

        var withoutMarker = Render(plot);
        trace.Markers.Add(marker);
        var withMarker = Render(plot);

        Assert.Equal(0, PixelsDifferingOutsideViewport(withoutMarker, withMarker, plot.Axes.Viewport));
        withoutMarker.Dispose(); withMarker.Dispose();
    }

    /// <summary>The same clip must not silently delete a marker that IS in view — otherwise the
    /// test above would pass with the marker renderer removed entirely.</summary>
    [Fact]
    public void MarkerGlyph_InsideThePlotBox_StillDraws()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);
        var trace = SweptTrace(PlotType.Rect);
        plot.Traces.Add(trace);
        plot.Axes.Window = new Avalonia.Rect(1.0, -20.0, 4.0, 40.0);   // 1..5 GHz, -6 dB mid-box

        using var withoutMarker = Render(plot);
        trace.Markers.Add(new Marker(trace, Freqs[2], isMulti: false, isDelta: false, index: 1));
        using var withMarker = Render(plot);

        var clip = PlotRenderer.ViewportClipRect(plot.Axes.Viewport, Canvas);
        int inside = 0;
        for (int y = (int)clip.Top; y <= (int)clip.Bottom; y++)
        for (int x = (int)clip.Left; x <= (int)clip.Right; x++)
            if (withoutMarker.GetPixel(x, y) != withMarker.GetPixel(x, y)) inside++;

        Assert.True(inside > 0, "the marker glyph must still be drawn when its data point is in view");
    }

    /// <summary>Smith/Polar exception: the clip is the SQUARE viewport that bounds the chart
    /// circle, so a marker outside the unit circle but inside that square still renders — the
    /// owner's "allowed to pan into the corners of the circle".</summary>
    [Fact]
    public void MarkerGlyph_OnSmith_MayOccupyACornerOutsideTheCircle()
    {
        var plot  = new Plot(PlotType.Smith, FreqUnit.GHz);
        var trace = SweptTrace(PlotType.Smith);
        plot.Traces.Add(trace);

        var vp   = plot.Axes.Viewport;
        var clip = PlotRenderer.ViewportClipRect(vp, Canvas);
        var tf   = PlotRenderer.BuildTransforms(plot, Canvas);

        // The Γ-plane point at the top-left corner of the square window: |Γ| = √2 > 1, so it is
        // outside the chart circle — yet inside the square the clip is built from.
        var corner = tf.PrimaryToCanvas(plot.Axes.Window.Left, plot.Axes.Window.Top);
        Assert.True(Math.Sqrt(2.0) > 1.0);
        Assert.InRange(corner.X, clip.Left, clip.Right);
        Assert.InRange(corner.Y, clip.Top,  clip.Bottom);
    }

    // ================================================================
    //  3 — marker readout language
    // ================================================================

    private static Trace NetworkTrace(DependentVarFormat fmt, MatrixType mt = MatrixType.S,
                                      int row = 0, int col = 0)
    {
        var snp = new SNP(Freqs, 2);
        for (int f = 0; f < Freqs.Length; f++)
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    snp.Matrices[f][i, j] = Complex.FromPolarCoordinates(0.5, 0.3 * f);
        return new Trace(snp, mt, row, col, fmt);
    }

    /// <summary>The reported case, end to end: axis label and marker readout for one S(1,1) dB
    /// trace must be the SAME string, and it must be the "S(1,1) dB20" spelling.</summary>
    [Fact]
    public void MarkerReadout_MatchesTheYAxisLabel_ForSParamDb()
    {
        var trace  = NetworkTrace(DependentVarFormat.Db);
        var marker = new Marker(trace, Freqs[1], isMulti: false, isDelta: false, index: 1);

        string axisLabel = TraceLabeler.ComputeMinimalLabels([trace])[0];
        string readout   = trace.GetMarkerValString(marker, showFilePrefix: false);

        Assert.Equal("S(1,1) dB20", axisLabel);
        Assert.StartsWith(axisLabel + "=", readout);
        Assert.DoesNotContain("dB(S(1,1))", readout);
    }

    /// <summary>Every dependent-variable format, not just dB — one table means one answer.</summary>
    [Theory]
    [InlineData(DependentVarFormat.Db,        "S(2,1) dB20")]
    [InlineData(DependentVarFormat.Mag,       "S(2,1) Mag")]
    [InlineData(DependentVarFormat.Phase,     "S(2,1) Phase")]
    [InlineData(DependentVarFormat.Real,      "S(2,1) Real")]
    [InlineData(DependentVarFormat.Imaginary, "S(2,1) Imag")]
    [InlineData(DependentVarFormat.Complex,   "S(2,1)")]
    public void MarkerReadout_MatchesTheYAxisLabel_ForEveryFormat(DependentVarFormat fmt, string expected)
    {
        var trace  = NetworkTrace(fmt, MatrixType.S, row: 1, col: 0);
        var marker = new Marker(trace, Freqs[1], isMulti: false, isDelta: false, index: 1);

        Assert.Equal(expected, TraceLabeler.ComputeMinimalLabels([trace])[0]);
        Assert.Equal(expected, trace.ReadoutDescription(showFilePrefix: false));
        Assert.StartsWith(expected + "=", trace.GetMarkerValString(marker, showFilePrefix: false));
    }

    /// <summary>The info box, the marker editor's own data line, and a multi-marker row are three
    /// separate call sites — all three had the old spelling, so all three are gated.</summary>
    [Fact]
    public void MarkerReadout_InfoBoxEditorAndMultiMarkerRow_AllUseTheAxisLanguage()
    {
        var t1 = NetworkTrace(DependentVarFormat.Db, MatrixType.S, 0, 0);
        var t2 = NetworkTrace(DependentVarFormat.Db, MatrixType.S, 1, 0);
        var marker = new Marker(t1, Freqs[1], isMulti: true, isDelta: false, index: 1);
        t1.Markers.Add(marker);

        var boxLines = t1.BuildMarkerBoxLines(marker, FreqUnit.GHz, showFilePrefix: false, plotTraces: [t1, t2]);
        Assert.Contains(boxLines, l => l.Text.StartsWith("S(1,1) dB20="));
        Assert.Contains(boxLines, l => l.Text.StartsWith("S(2,1) dB20="));
        Assert.DoesNotContain(boxLines, l => l.Text.Contains("dB(S("));

        Assert.StartsWith("S(1,1) dB20=", t1.GetEditorDataLine(marker, showFilePrefix: false));
        Assert.StartsWith("S(2,1) dB20=", t1.GetMultiMarkerLine(marker, t2));
    }

    /// <summary>The source-file prefix still works, and reads the same way Description does.</summary>
    [Fact]
    public void ReadoutDescription_WithFilePrefix_KeepsTheSourceStem()
    {
        var trace = NetworkTrace(DependentVarFormat.Db);
        trace.SourcePath = "/results/amp_tuned.s2p";

        Assert.Equal("amp_tuned..S(1,1) dB20", trace.ReadoutDescription(showFilePrefix: true));
        Assert.Equal("S(1,1) dB20",            trace.ReadoutDescription(showFilePrefix: false));
    }

    /// <summary>ShortDescription is deliberately NOT changed — BuildPickerYExpression reads it as
    /// an EXPRESSION fallback, where a trailing " dB20" suffix would not parse.</summary>
    [Fact]
    public void ShortDescription_StaysTheExpressionForm()
    {
        var trace = NetworkTrace(DependentVarFormat.Db);
        Assert.Equal("dB(S(1,1))", trace.ShortDescription);
    }

    // ================================================================
    //  4 — the live VSWR drag readout is theme-coloured (2026-08-23)
    // ================================================================

    /// <summary>
    /// The transient readout drawn beside the pointer while a VSWR locus is dragged used a hardcoded
    /// black, which is unreadable on a dark-theme plot. It must use the SAME colour MarkerInfoBox
    /// draws its own lines in — <c>RenderTheme.TextColor</c>.
    ///
    /// <para>Oracle is a DIFFERENTIAL render: the readout is the only thing that changes between the
    /// two draws, so every differing pixel belongs to it. "Is there light ink somewhere" cannot
    /// separate the readout from the axis chrome, which is already drawn in the same colour.</para>
    /// </summary>
    [Fact]
    public void VswrDragReadout_IsDrawnInTheThemeTextColour_NotBlack()
    {
        var theme = RenderTheme.Dark;

        SKBitmap RenderWith(VswrReadout? readout)
        {
            using var surface = SKSurface.Create(new SKImageInfo((int)Canvas.W, (int)Canvas.H));
            surface.Canvas.Clear(theme.BackgroundColor);
            PlotRenderer.Draw(surface.Canvas, Canvas, TwoAxisPlot(), PlotDetail.Full, theme,
                              watermarkOpacity: 0f, vswrReadout: readout);
            return SKBitmap.FromImage(surface.Snapshot());
        }

        using var without = RenderWith(null);
        using var with    = RenderWith(new VswrReadout("VSWR 2.35", new SKPoint(300f, 250f)));

        var changed = new List<SKColor>();
        for (int y = 0; y < with.Height; y++)
        for (int x = 0; x < with.Width;  x++)
            if (with.GetPixel(x, y) != without.GetPixel(x, y))
                changed.Add(with.GetPixel(x, y));

        Assert.NotEmpty(changed);

        // The darkest pixel the readout paints is its own core colour; antialiased edges blend
        // toward the background, which in this theme is DARKER, so take the brightest instead.
        static int Lum(SKColor c) => (299 * c.Red + 587 * c.Green + 114 * c.Blue) / 1000;
        var core = changed.MaxBy(Lum);

        Assert.Equal(theme.TextColor, core);
        Assert.True(Lum(core) > Lum(theme.BackgroundColor) + 60,
            $"readout colour {core} is not legible against the dark background {theme.BackgroundColor}");
    }
}
