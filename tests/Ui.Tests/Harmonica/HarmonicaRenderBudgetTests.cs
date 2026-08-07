// ================================================================
//  HarmonicaRenderBudgetTests.cs  —  M1 of brief-harmonicarf-h4-h5
//
//  §3: "Every number in §0.2 is a SOLVE cost. A frame is a solve PLUS a render, and nothing has
//  ever measured harmonicaRF's render."  This file measures the render half, headless, through the
//  REAL DataDisplay renderers — the same PlotRenderer / AxesRenderer / ContourRenderer /
//  TraceRenderer / MarkerRenderer stack the panels will draw with — at harmonicaRF-shaped content.
//
//  The five numbers §3 asks for:
//    R1  one Smith panel: 61-point grid, 10 iso-line levels, 7 hole dots, 4 markers, 4 glyphs,
//        at 1x and at 2x device scale
//    R2  the loadline panel: a DCIV family + one loadline
//    R3  the power-sweep panel: gain + efficiency
//    R4  the whole four-panel §7.1 layout at a realistic window size
//    R5  ContourGrid.Raster at 96x96 and 256x256  — NOT here; it is framework-free and lives in
//        tests/Harmonica.Tests/RasterCostTests.cs, per the brief's own file map.
//
//  MEASUREMENT DISCIPLINE (§ "restated because this repo has now been bitten by it three times"):
//  every method is Category=Benchmark, this class is in the shared typeface-seam collection so it
//  never runs beside another class touching the same static, and every number is a best-of-N. The
//  reported figures were taken with this class run ALONE.
//
//  WHAT THIS IS NOT. There is no src/Ui/Harmonica yet — that is M3. So the fixtures below are
//  harmonicaRF-SHAPED content driven through the EXISTING display layer, which is exactly what §3
//  asks for: the panels are going to be built on this stack, so the question is what this stack
//  costs at this content. Two deliberate over-estimates are noted at their fixtures.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.Renderers;
using RfCore;
using RfCore.Loadpull;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class HarmonicaRenderBudgetTests : IDisposable
{
    private readonly ITestOutputHelper _out;

    public HarmonicaRenderBudgetTests(ITestOutputHelper output)
    {
        _out = output;
        // SkiaFonts.Load goes through Avalonia's AssetLoader, which throws
        // "Unable to locate 'Avalonia.Platform.IAssetLoader'" with no live app host (measured, not
        // assumed). SKTypeface.Default is the one face guaranteed loadable without an asset system.
        SkiaFonts.TestOverrideTypeface = SKTypeface.Default;
    }

    public void Dispose() => SkiaFonts.TestOverrideTypeface = null;

    // ── the timing primitive ─────────────────────────────────────────────────
    //
    // Best-of-N, with a discarded warm-up. The MINIMUM is the estimator: a render either does the
    // work or it does not, so the fastest observed pass is the one least polluted by a descheduled
    // slice — the same rule R-L2a-4 already fixed for every other timing measurement in this repo.

    private static (double MinMs, double MedianMs) TimeBestOf(int reps, Action body)
    {
        body();                                     // warm-up: JIT, font cache, first-touch alloc
        var ms = new double[reps];
        for (int i = 0; i < reps; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            body();
            sw.Stop();
            ms[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(ms);
        return (ms[0], ms[reps / 2]);
    }

    private const int Reps = 9;

    // ── the contour fixture — a realistic 61-point grid with 7 holes ──────────
    //
    // 5 rings x 12 spokes + centre = 61 points, |Γ| ≤ 0.8 — the shape ContourGrid.RingGrid produces.
    // Seven of them are holed (NaN), which §0.3 item 4 measured as the COMMON case on Hero 2's own
    // device, not an edge case. The metric surface is a smooth Pout-like bowl so the level set and
    // the polyline count are realistic rather than degenerate.
    //
    // This deliberately does NOT run HB — the RENDER cost depends on the geometry that comes out
    // (how many polylines, of how many points, plus dots and markers), not on where the surface came
    // from. ContourGrid's own cost is R5's business, in Harmonica.Tests.

    private sealed record ContourFixture(
        SurfaceGrid       Grid,
        ContourLevelSet   Levels,
        ScatterReduction  Scatter,
        int               PolylineCount,
        int               PolylinePointCount);

    private static Complex[] RingGrid61()
    {
        var g = new List<Complex> { Complex.Zero };
        for (int r = 1; r <= 5; r++)
        {
            double mag = 0.8 * r / 5.0;
            for (int s = 0; s < 12; s++)
                g.Add(Complex.FromPolarCoordinates(mag, 2.0 * Math.PI * s / 12.0));
        }
        return [.. g];                                     // 1 + 60 = 61
    }

    private static ContourFixture BuildContourFixture(int resolution)
    {
        var gamma = RingGrid61();

        // A Pout-like bowl peaking off-centre, in dBm — the shape a real load-pull map has.
        var values = new double[gamma.Length];
        for (int i = 0; i < gamma.Length; i++)
        {
            var d = gamma[i] - new Complex(0.28, -0.16);
            values[i] = 42.5 - 9.0 * (d.Real * d.Real + d.Imaginary * d.Imaginary);
        }

        // Seven holes, spread rather than clustered (a clustered set would understate the mask cost).
        int[] holes = [7, 15, 23, 31, 39, 47, 55];
        foreach (int h in holes) values[h] = double.NaN;

        var fit = new Rbf2D(gamma, values);

        // The support mask, exactly as ContourGrid.Raster defines it: inside the convex hull of the
        // converged points, and outside a disc of one mean-nearest-neighbour spacing around each hole.
        var converged = gamma.Where((_, i) => !double.IsNaN(values[i])).ToArray();
        var hull      = ConvexHull(converged);
        double holeR  = MeanNearestNeighbourSpacing(gamma);
        var holePts   = holes.Select(h => gamma[h]).ToArray();

        double minRe = gamma.Min(p => p.Real),      maxRe = gamma.Max(p => p.Real);
        double minIm = gamma.Min(p => p.Imaginary), maxIm = gamma.Max(p => p.Imaginary);

        var xs = new double[resolution];
        var ys = new double[resolution];
        for (int i = 0; i < resolution; i++)
        {
            double t = resolution == 1 ? 0.5 : (double)i / (resolution - 1);
            xs[i] = minRe + t * (maxRe - minRe);
            ys[i] = minIm + t * (maxIm - minIm);
        }

        var surf = new double[resolution * resolution];
        for (int yi = 0; yi < resolution; yi++)
            for (int xi = 0; xi < resolution; xi++)
                surf[yi * resolution + xi] =
                    InSupport(xs[xi], ys[yi], hull, holePts, holeR)
                        ? fit.Evaluate(xs[xi], ys[yi])
                        : double.NaN;

        var grid   = new SurfaceGrid(xs, ys, surf);
        var levels = ContourExtractor.LevelsBetween(grid, 10);       // D8's default, 10 levels
        var polys  = ContourExtractor.Extract(grid, levels);

        var scatter = new ScatterReduction(
            gamma,
            values,
            Enumerable.Range(0, gamma.Length).ToArray());

        return new ContourFixture(grid, levels, scatter,
            polys.Count, polys.Sum(p => p.Points.Count));
    }

    private static bool InSupport(double re, double im, IReadOnlyList<Complex> hull,
                                  IReadOnlyList<Complex> holes, double holeR)
    {
        if (!InsideHull(hull, re, im)) return false;
        foreach (var h in holes)
        {
            double dr = re - h.Real, di = im - h.Imaginary;
            if (dr * dr + di * di < holeR * holeR) return false;
        }
        return true;
    }

    private static double MeanNearestNeighbourSpacing(IReadOnlyList<Complex> pts)
    {
        double total = 0; int counted = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            double best = double.MaxValue;
            for (int j = 0; j < pts.Count; j++)
            {
                if (i == j) continue;
                double d = (pts[i] - pts[j]).Magnitude;
                if (d < best) best = d;
            }
            if (best < double.MaxValue) { total += best; counted++; }
        }
        return counted > 0 ? total / counted : 0.1;
    }

    private static IReadOnlyList<Complex> ConvexHull(IReadOnlyList<Complex> pts)
    {
        if (pts.Count < 3) return pts;
        var sorted = pts.OrderBy(p => p.Real).ThenBy(p => p.Imaginary).ToList();

        static List<Complex> Chain(List<Complex> seq)
        {
            var half = new List<Complex>();
            foreach (var p in seq)
            {
                while (half.Count >= 2 && Cross(half[^2], half[^1], p) <= 0)
                    half.RemoveAt(half.Count - 1);
                half.Add(p);
            }
            half.RemoveAt(half.Count - 1);
            return half;
        }

        var lower = Chain(sorted);
        sorted.Reverse();
        lower.AddRange(Chain(sorted));
        return lower;
    }

    private static double Cross(Complex o, Complex a, Complex b)
        => (a.Real - o.Real) * (b.Imaginary - o.Imaginary)
         - (a.Imaginary - o.Imaginary) * (b.Real - o.Real);

    private static bool InsideHull(IReadOnlyList<Complex> hull, double re, double im)
    {
        if (hull.Count < 3) return false;
        bool inside = false;
        for (int i = 0, j = hull.Count - 1; i < hull.Count; j = i++)
        {
            double xi = hull[i].Real, yi = hull[i].Imaginary;
            double xj = hull[j].Real, yj = hull[j].Imaginary;
            if (yi > im != yj > im && re < (xj - xi) * (im - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    // ── panel fixtures ───────────────────────────────────────────────────────

    /// <summary>
    /// A Smith panel of the shape §7.2 specifies: contours + visible grid points + MXP/MXE, with a
    /// marker/glyph carrier trace on top.
    ///
    /// <para><b>Two deliberate over-estimates, stated rather than implied.</b> (1) The 8 marker
    /// symbols stand in for 4 termination markers PLUS 4 intrinsic glyphs; a glyph is a smaller,
    /// label-free triangle, so 8 full markers is an upper bound. (2) <c>DrawGridPoints</c> draws all
    /// 61 grid dots filled, where the real panel draws 54 filled and 7 hollow — again an upper
    /// bound, and by construction never an under-count.</para>
    /// </summary>
    private static Plot BuildSmithPanel(ContourFixture fx)
    {
        var plot = new Plot(PlotType.Smith, FreqUnit.GHz);

        // The contour trace. ContourData carries the grid/levels/scatter the renderer reads.
        var carrier = new SNP(new[] { 1e9 }, 1);
        var contour = new Trace(carrier, MatrixType.S, 0, 0, DependentVarFormat.Db)
        {
            ContourData = new ContourData
            {
                Grid              = fx.Grid,
                Levels            = fx.Levels,
                Scatter           = fx.Scatter,
                MetricName        = "Pout",
                ShowIsoLines      = true,
                ShowFill          = false,         // harmonicaRF NEVER fills — see src/Harmonica/CLAUDE.md
                DisplayGridPoints = true,
                DisplayMxp        = true,
                DisplayMxe        = true,
                DrawLabels        = false,         // D11: iso-line labels default OFF
                MxpCoord          = new Complex(0.28, -0.16),
                MxeCoord          = new Complex(-0.10, 0.34),
                GammaPlane        = true,
            }
        };
        plot.Traces.Add(contour);

        // The marker/glyph carrier: a 2-port whose S11 walks a spiral so the 8 symbols land apart.
        var freqs = Enumerable.Range(0, 16).Select(i => 1e9 + i * 1e8).ToArray();
        var snp   = new SNP(freqs, 2);
        for (int i = 0; i < freqs.Length; i++)
        {
            double t = i / (double)(freqs.Length - 1);
            var g = Complex.FromPolarCoordinates(0.15 + 0.6 * t, 2.0 * Math.PI * t);
            var m = snp[i];
            m[0, 0] = g; m[1, 1] = g * 0.5;
            m[1, 0] = new Complex(0.8, 0.1); m[0, 1] = new Complex(0.02, 0.0);
        }
        var mTrace = new Trace(snp, MatrixType.S, 0, 0, DependentVarFormat.Complex);
        mTrace.BuildPath(PlotType.Smith, FreqUnit.GHz);
        for (int i = 0; i < 8; i++)
            mTrace.Markers.Add(new Marker(mTrace, freqs[i * 2], false, false, i + 1, FreqUnit.GHz));
        plot.Traces.Add(mTrace);

        return plot;
    }

    /// <summary>§7.3 — the DCIV family (9 Vgs curves x 200 points) with the time-domain loadline
    /// over it. K=5 gives a 32-sample time grid; the loadline is drawn closed, so 33 points.</summary>
    private static Plot BuildLoadlinePanel()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);

        const int NV = 200;
        var vds = Enumerable.Range(0, NV).Select(i => 60.0 * i / (NV - 1)).ToArray();
        var curves = new List<(double, string?, Complex[]?, double[]?)>();
        for (int k = 0; k < 9; k++)
        {
            double vgs = -5.0 + k * 0.5;
            var ids = new double[NV];
            for (int i = 0; i < NV; i++)
            {
                double vov = Math.Max(0, vgs + 4.0);
                ids[i] = vov * vov * 0.06 * Math.Tanh(vds[i] * 0.35);
            }
            curves.Add((vgs, $"Vgs={vgs:0.0}", null, ids));
        }
        var family = new Trace(new SNP(new[] { 1e9 }, 1), MatrixType.S, 0, 0, DependentVarFormat.Real);
        family.SetFamilyData(vds, "Vds", "V", "Vgs", curves, PlotType.Rect, FreqUnit.GHz, "V");
        plot.Traces.Add(family);

        const int NT = 33;
        var lx = new double[NT]; var ly = new double[NT];
        for (int i = 0; i < NT; i++)
        {
            double th = 2.0 * Math.PI * i / (NT - 1);
            lx[i] = 30.0 + 24.0 * Math.Cos(th);
            ly[i] =  0.9 -  0.85 * Math.Cos(th) + 0.12 * Math.Sin(2 * th);
        }
        var loadline = new Trace(new SNP(new[] { 1e9 }, 1), MatrixType.S, 0, 0, DependentVarFormat.Real);
        loadline.SetCubeData(lx, null, ly, "Vds_intr", "V", PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(loadline);

        return plot;
    }

    /// <summary>§7.4 — gain on the left axis and efficiency on the right, against output power.</summary>
    private static Plot BuildPowerSweepPanel()
    {
        var plot = new Plot(PlotType.Rect, FreqUnit.GHz);

        const int N = 33;
        var pout = new double[N]; var gain = new double[N]; var eff = new double[N];
        for (int i = 0; i < N; i++)
        {
            double pin = -10.0 + i * 1.25;
            double g   = 14.5 - 4.0 * Math.Log(1 + Math.Exp((pin - 18.0) * 0.45));
            pout[i] = pin + g;
            gain[i] = g;
            eff[i]  = 72.0 / (1.0 + Math.Exp(-(pin - 14.0) * 0.30));
        }

        var gTrace = new Trace(new SNP(new[] { 1e9 }, 1), MatrixType.S, 0, 0, DependentVarFormat.Real);
        gTrace.SetCubeData(pout, null, gain, "Pout", "dBm", PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(gTrace);

        var eTrace = new Trace(new SNP(new[] { 1e9 }, 1), MatrixType.S, 0, 0, DependentVarFormat.Real,
                               secondaryAxis: true);
        eTrace.SetCubeData(pout, null, eff, "Pout", "dBm", PlotType.Rect, FreqUnit.GHz);
        plot.Traces.Add(eTrace);

        return plot;
    }

    private static void AutoScale(Plot plot)
    {
        // Fit the window to the traces so the render is doing real clipping work, not drawing into
        // an empty window. Complex plots keep their own [-1,1] Γ window.
        if (plot.PlotType.IsComplex()) return;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        double minY2 = double.MaxValue, maxY2 = double.MinValue;
        foreach (var t in plot.Traces)
        {
            var r = t.PathBoundingRect();
            if (r.Width <= 0 && r.Height <= 0) continue;
            minX = Math.Min(minX, r.X); maxX = Math.Max(maxX, r.X + r.Width);
            if (t.UseSecondaryAxis)
            {
                minY2 = Math.Min(minY2, r.Y); maxY2 = Math.Max(maxY2, r.Y + r.Height);
            }
            else
            {
                minY = Math.Min(minY, r.Y); maxY = Math.Max(maxY, r.Y + r.Height);
            }
        }
        if (minX < maxX && minY < maxY)
            plot.Axes.Window = new Avalonia.Rect(minX, minY, maxX - minX, maxY - minY);
        if (minY2 < maxY2 && minX < maxX)
            plot.Axes.WindowSecondary = new Avalonia.Rect(minX, minY2, maxX - minX, maxY2 - minY2);
    }

    private static void DrawPanel(SKCanvas canvas, Plot plot, SKRect rect, RenderTheme theme)
    {
        canvas.Save();
        canvas.ClipRect(rect);
        canvas.Translate(rect.Left, rect.Top);
        PlotRenderer.Draw(canvas, (rect.Width, rect.Height), plot, PlotDetail.Full, theme);
        canvas.Restore();
    }

    // ── R1 — one Smith panel, 1x and 2x ──────────────────────────────────────

    [Trait("Category", "Benchmark")]
    [Fact]
    public void R1_SmithPanel_AtOneXAndTwoX()
    {
        var fx = BuildContourFixture(256);
        _out.WriteLine($"contour fixture: 61 Γ points, 7 holes, 10 levels, " +
                       $"{fx.PolylineCount} polylines / {fx.PolylinePointCount} vertices at 256x256");

        var theme = RenderTheme.Light;

        foreach (var (label, w, h) in new[] { ("1x", 520, 620), ("2x", 1040, 1240) })
        {
            var plot = BuildSmithPanel(fx);
            using var surface = SKSurface.Create(new SKImageInfo(w, h));
            var canvas = surface.Canvas;

            var (min, med) = TimeBestOf(Reps, () =>
            {
                canvas.Clear(SKColors.White);
                PlotRenderer.Draw(canvas, (w, h), plot, PlotDetail.Full, theme);
            });
            _out.WriteLine($"R1 Smith panel unfilled @{label} ({w}x{h}): min {min:F2} ms, median {med:F2} ms");
        }

        // NOTE: there is deliberately no filled measurement here. harmonicaRF NEVER fills its
        // contours and will not gain a setting to (owner ruling, 2026-08-06 — see
        // src/Harmonica/CLAUDE.md). The 73 ms figure an earlier draft of this file measured described
        // a code path harmonicaRF cannot reach, and carrying it forward would imply the decision is
        // still open.
    }

    // ── R2 — the loadline panel ──────────────────────────────────────────────

    [Trait("Category", "Benchmark")]
    [Fact]
    public void R2_LoadlinePanel()
    {
        var plot = BuildLoadlinePanel();
        AutoScale(plot);
        var theme = RenderTheme.Light;

        foreach (var (label, w, h) in new[] { ("1x", 560, 500), ("2x", 1120, 1000) })
        {
            using var surface = SKSurface.Create(new SKImageInfo(w, h));
            var canvas = surface.Canvas;
            var (min, med) = TimeBestOf(Reps, () =>
            {
                canvas.Clear(SKColors.White);
                PlotRenderer.Draw(canvas, (w, h), plot, PlotDetail.Full, theme);
            });
            _out.WriteLine($"R2 loadline panel (9-curve DCIV + loadline) @{label} ({w}x{h}): " +
                           $"min {min:F2} ms, median {med:F2} ms");
        }
    }

    // ── R3 — the power-sweep panel ───────────────────────────────────────────

    [Trait("Category", "Benchmark")]
    [Fact]
    public void R3_PowerSweepPanel()
    {
        var plot = BuildPowerSweepPanel();
        AutoScale(plot);
        var theme = RenderTheme.Light;

        foreach (var (label, w, h) in new[] { ("1x", 560, 500), ("2x", 1120, 1000) })
        {
            using var surface = SKSurface.Create(new SKImageInfo(w, h));
            var canvas = surface.Canvas;
            var (min, med) = TimeBestOf(Reps, () =>
            {
                canvas.Clear(SKColors.White);
                PlotRenderer.Draw(canvas, (w, h), plot, PlotDetail.Full, theme);
            });
            _out.WriteLine($"R3 power-sweep panel (gain + efficiency) @{label} ({w}x{h}): " +
                           $"min {min:F2} ms, median {med:F2} ms");
        }
    }

    // ── R4 — the whole four-panel §7.1 layout ────────────────────────────────

    [Trait("Category", "Benchmark")]
    [Fact]
    public void R4_FourPanelLayout_AtARealisticWindowSize()
    {
        var fx = BuildContourFixture(256);

        // §7.1: two Smith charts side by side (power left, efficiency right) with the dense readout
        // strip spanning beneath both; the right column holds the loadline above the power sweep.
        // 1600x1000 logical, right column 35%, readout strip 380 px tall.
        const int W = 1600, H = 1000;
        const int RightX = 1040, StripY = 620;

        var smithPower = BuildSmithPanel(fx);
        var smithEff   = BuildSmithPanel(fx);
        var loadline   = BuildLoadlinePanel();
        var sweep      = BuildPowerSweepPanel();
        AutoScale(loadline); AutoScale(sweep);

        var theme = RenderTheme.Light;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;

        var (min, med) = TimeBestOf(Reps, () =>
        {
            canvas.Clear(SKColors.White);
            DrawPanel(canvas, smithPower, new SKRect(0,      0,      RightX / 2, StripY), theme);
            DrawPanel(canvas, smithEff,   new SKRect(RightX / 2, 0,   RightX,     StripY), theme);
            DrawPanel(canvas, loadline,   new SKRect(RightX, 0,       W,          500),    theme);
            DrawPanel(canvas, sweep,      new SKRect(RightX, 500,     W,          H),      theme);
        });

        _out.WriteLine($"R4 four-panel layout @{W}x{H} (2 Smith + loadline + power sweep): " +
                       $"min {min:F2} ms, median {med:F2} ms");
        _out.WriteLine("R4 note: the §7.5 readout strip is Avalonia TextBlocks, not a Skia draw — it " +
                       "costs a layout pass, not a frame of this number. Measured separately or not at all.");

        // 2x device scale — a Retina panel of the same logical size.
        using var hi = SKSurface.Create(new SKImageInfo(W * 2, H * 2));
        var hc = hi.Canvas;
        var (min2, med2) = TimeBestOf(Reps, () =>
        {
            hc.Clear(SKColors.White);
            hc.Save();
            hc.Scale(2f);
            DrawPanel(hc, smithPower, new SKRect(0,      0,      RightX / 2, StripY), theme);
            DrawPanel(hc, smithEff,   new SKRect(RightX / 2, 0,   RightX,     StripY), theme);
            DrawPanel(hc, loadline,   new SKRect(RightX, 0,       W,          500),    theme);
            DrawPanel(hc, sweep,      new SKRect(RightX, 500,     W,          H),      theme);
            hc.Restore();
        });
        _out.WriteLine($"R4 four-panel layout @2x device scale ({W * 2}x{H * 2} px): " +
                       $"min {min2:F2} ms, median {med2:F2} ms");
    }
}
