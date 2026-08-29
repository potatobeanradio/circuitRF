// L2a's real deliverable (docs/sonnet-briefs/brief-L2a-performance-harness.md §5/§7/§8) — the baseline
// table. Every [Fact]/[Theory] in this file both asserts a LOOSE (3x-headroom) wall-clock catastrophe
// gate (R-L2a-3/gate 9 — counters are the real per-commit gate, these are a flap-resistant backstop)
// AND writes its measured numbers to test output, which is how the table in this phase's completion
// note (src/Ui/CLAUDE.md) was produced — run with `dotnet test --filter FullyQualifiedName~LayoutPerf
// --logger "console;verbosity=detailed"` to reproduce it.
//
// NOTHING in this file runs in the routine default pass any more (2026-08-29). 1k joined 50k and
// 500k in Category=Benchmark when it flapped against its own loose ceiling under full-suite load —
// see Baseline_1k's own note; it is tagged for wall-clock SENSITIVITY, not for cost. 50k
// (Baseline_50k) and 500k (Baseline_500k) are [Trait("Category","Benchmark")] for cost — opt-in only
// (docs/sonnet-briefs/brief-test-default-fast.md: tag by measured per-test cost against a ~5s
// threshold, not by subject matter; 50k's CurveHeavy/Mixed cases measure 6-11s). Both still ran during
// this phase's own development to produce the baseline table; the routine default is Category!=Benchmark
// (repo-wide via circuitrf.runsettings — no flag to remember). 500k's COUNTER coverage (the part that
// actually catches an algorithmic regression) stays in the routine gate — see
// LayoutSpatialIndexPerfTests.Gated500k_CullingCountersStayCorrect.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.LayoutPerf;

[Collection(CircuitRF.Ui.Tests.LayoutTextOutlineTypefaceCollection.Name)]
public class LayoutPerformanceBaselineTests : System.IDisposable
{
    private readonly ITestOutputHelper _out;

    // SkiaFonts.PlexRegular cannot load headlessly (see LayoutPerfHarnessGateTests.cs's header note)
    // — Mixed-profile layouts carry labels, so route through SKTypeface.Default for every test here.
    public LayoutPerformanceBaselineTests(ITestOutputHelper output)
    {
        _out = output;
        LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    }
    public void Dispose() => LayoutTextOutline.TestOverrideTypeface = null;

    private readonly record struct SweepConfig(int Warmup, int Iterations, int SweepSteps);

    private static SweepConfig ConfigFor(int shapeCount) => shapeCount switch
    {
        <= 1_000  => new SweepConfig(3, 8, 8),
        <= 50_000 => new SweepConfig(2, 4, 5),
        _         => new SweepConfig(1, 2, 3),
    };

    // ── 1k — Benchmark since 2026-08-29, and NOT because it is slow ──────────────
    //
    // It is fast: ~0.8 s for all three profiles together. It is tagged for the OTHER reason the tag
    // is applied in this repo (root CLAUDE.md's own note on RfCore.Tests' Rbf2DPerfTests): a test
    // that is fast but WALL-CLOCK-SENSITIVE cannot survive the parallel-start burst of a
    // full-solution run. Measured 2026-08-29: the Mixed profile read a p95 of 128.1 ms against this
    // file's own loose 120 ms catastrophe ceiling during a full `dotnet test`, and 3 of 3 cases
    // passed when the same method was run alone seconds later.
    //
    // The ceiling it flapped against is the 3x-headroom backstop this file's header describes, not a
    // performance target — so what flapped is a diagnostic, and R-L2a-3's real per-commit gate is
    // untouched: the COUNTER coverage stays in the routine pass
    // (LayoutSpatialIndexPerfTests.Gated500k_CullingCountersStayCorrect and its 1k siblings), which
    // is the part that actually catches an algorithmic regression. Do not untag this on the grounds
    // that it runs quickly — it is tagged for the purpose the mechanism serves, not the letter of
    // the ~5 s rule.

    [Trait("Category", "Benchmark")]
    [Theory]
    [InlineData(GeneratorProfile.Manhattan, 1_000)]
    [InlineData(GeneratorProfile.CurveHeavy, 1_000)]
    [InlineData(GeneratorProfile.Mixed, 1_000)]
    public void Baseline_1k(GeneratorProfile profile, int shapeCount) => RunAndReport(profile, shapeCount);

    // ── 50k — Benchmark (docs/sonnet-briefs/brief-test-default-fast.md: tag by measured cost, not
    // subject matter). Split out of the combined 1k/50k Theory this replaces: CurveHeavy/Mixed at 50k
    // measure ~6-11s and Manhattan ~4.9s — the whole 50k tier is over (or right at) the ~5s threshold,
    // so it moves together rather than fragmenting into a third, per-profile tier for one borderline
    // case. Still runs the identical measurement `RunAndReport` performs — nothing here is weakened,
    // only excluded from the routine pass. Run explicitly with the Benchmark opt-in path.

    [Trait("Category", "Benchmark")]
    [Theory]
    [InlineData(GeneratorProfile.Manhattan, 50_000)]
    [InlineData(GeneratorProfile.CurveHeavy, 50_000)]
    [InlineData(GeneratorProfile.Mixed, 50_000)]
    public void Baseline_50k(GeneratorProfile profile, int shapeCount) => RunAndReport(profile, shapeCount);

    // ── 500k — opt-in timed sweep only (brief-benchmark-gate-split.md R-perf-1/R-perf-3) ──────────
    // This is the MEASUREMENT exercise (median/p95 across pan/zoom/full-extent/hit-test/marquee/load,
    // warmed-up, per profile) — real value for someone actively tuning performance, but not a
    // per-commit signal: R-L2a-3 already established counters are the gate, wall-clock is the
    // diagnostic, and this test paid for the diagnostic on every routine run. `Category=Benchmark`
    // supersedes the old `Category=Nightly` tag here (consolidated to one tag per R-perf-3, not left
    // overlapping) — the 500k COUNTER coverage that actually catches an algorithmic regression stays
    // in the gate, see `LayoutSpatialIndexPerfTests.Gated500k_CullingCountersStayCorrect`.
    // Run explicitly: dotnet test --filter "Category=Benchmark"

    [Trait("Category", "Benchmark")]
    [Theory]
    [InlineData(GeneratorProfile.Manhattan, 500_000)]
    [InlineData(GeneratorProfile.CurveHeavy, 500_000)]
    [InlineData(GeneratorProfile.Mixed, 500_000)]
    public void Baseline_500k(GeneratorProfile profile, int shapeCount) => RunAndReport(profile, shapeCount);

    // ── R8b crossover experiment (§5) ────────────────────────────────────────────
    // The 9 Baseline/Baseline_500k combos above spread shapes across 200 layers (§5.1's own "200
    // layers" scenario), which dilutes visible-shapes-PER-LAYER far below anything near the design
    // doc's ~20k starting guess — so they cannot answer "where is the R8b crossover" on their own.
    // This experiment puts every shape on ONE layer and sweeps the per-layer count directly, which is
    // exactly the quantity R8b's threshold is defined over (§2.3 R8b: "above a VISIBLE-SHAPE
    // THRESHOLD... start at ~20k, tune against the benchmark").

    [Trait("Category", "Benchmark")]
    [Fact]
    public void R8bCrossoverExperiment()
    {
        var layer = new LayerKey(1, 0);
        var tech = new Technology { Layers = [new LayerDef { Key = layer, Color = new CircuitRF.Design.Theming.Rgba(80, 140, 220), FillOpacity = 0.35, Visible = true, Selectable = true }] };
        const int W = 1000, H = 700;

        int[] counts = [500, 2_000, 5_000, 10_000, 20_000, 50_000, 100_000];
        _out.WriteLine("=== R8b crossover experiment — single layer, Manhattan rects, full-extent frame ===");
        foreach (int count in counts)
        {
            var view = new LayoutView { DbuPerMicron = LayoutUnits.DefaultDbuPerMicron, SnapDbu = 1000 };
            var rng = new Random(count); // seed varies by count only — still fully deterministic per count
            const int gridSide = 4_000_000; // 4mm square packing area, dense enough to force real overlap
            for (int i = 0; i < count; i++)
            {
                long cx = rng.Next(-gridSide, gridSide);
                long cy = rng.Next(-gridSide, gridSide);
                long half = 3_000 + rng.Next(5_000);
                view.Shapes.Add(new RectShape { Layer = layer, X1 = cx - half, Y1 = cy - half, X2 = cx + half, Y2 = cy + half });
            }

            var bbox = view.Shapes.Aggregate(Bbox.Empty, (acc, s) => acc.Union(LayoutGeometry.BboxOf(s)));
            var vp = LayoutViewport.ZoomToFit(bbox, W, H);
            using var surface = SKSurface.Create(new SKImageInfo(W, H));
            var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };

            var darkening = BenchmarkHarness.Measure(2, 4, () => LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts));
            var merged = BenchmarkHarness.Measure(2, 4, () => MergedPathBenchmarkRenderer.Draw(surface.Canvas, view, tech, vp, LayoutRenderTheme.Light));

            _out.WriteLine($"{count,7:N0} shapes/layer  darkening median={darkening.MedianMs,8:F3}ms  merged median={merged.MedianMs,8:F3}ms  ratio(darkening/merged)={darkening.MedianMs / merged.MedianMs,5:F2}x");
        }
    }

    // ── The measurement itself ───────────────────────────────────────────────────

    private void RunAndReport(GeneratorProfile profile, int shapeCount)
    {
        const int layerCount = 200;
        var cfg = ConfigFor(shapeCount);
        var view = SyntheticLayoutGenerator.Generate(shapeCount, layerCount, seed: 2026, profile);
        var tech = SyntheticLayoutGenerator.GenerateTechnology(layerCount);
        var theme = LayoutRenderTheme.Light;

        var bbox = view.Shapes.Aggregate(Bbox.Empty, (acc, s) => acc.Union(LayoutGeometry.BboxOf(s)));
        const int W = 1000, H = 700;
        var fitVp = LayoutViewport.ZoomToFit(bbox, W, H);
        var (denseX, denseY, sparseX, sparseY) = FindDenseAndSparsePoints(view, bbox);

        _out.WriteLine($"=== {profile} @ {shapeCount:N0} shapes / {layerCount} layers ===");

        // ── Full-extent static (§3) ──────────────────────────────────────────────
        using (var surface = SKSurface.Create(new SKImageInfo(W, H)))
        {
            var plainOpts = new LayoutRenderOptions { Theme = theme, ShowGrid = false };
            var counters = LayoutRenderer.Draw(surface.Canvas, view, tech, fitVp, plainOpts); // one extra frame — counters, work-count evidence
            var darkening = BenchmarkHarness.Measure(cfg.Warmup, cfg.Iterations,
                () => LayoutRenderer.Draw(surface.Canvas, view, tech, fitVp, plainOpts));
            var merged = BenchmarkHarness.Measure(cfg.Warmup, cfg.Iterations,
                () => MergedPathBenchmarkRenderer.Draw(surface.Canvas, view, tech, fitVp, theme));

            _out.WriteLine($"full-extent  darkening: {darkening}");
            _out.WriteLine($"full-extent  merged:    {merged}");
            _out.WriteLine($"full-extent  counters:  examined={counters.ShapesExamined:N0} drawn={counters.ShapesDrawn:N0} paths={counters.PathsConstructed:N0} drawCalls={counters.DrawCalls:N0} layersVisited={counters.LayersVisited:N0}");
            AssertLooseCeiling(darkening.P95Ms, CeilingMsFor(shapeCount, baseMs: 60));
        }

        // ── Pan sweep (§3) — fixed zoom, sweep PanX across the design ────────────
        using (var surface = SKSurface.Create(new SKImageInfo(W, H)))
        {
            var panFramesDarkening = BuildPanFrames(surface, view, tech, theme, fitVp, cfg.SweepSteps, merged: false);
            var panFramesMerged = BuildPanFrames(surface, view, tech, theme, fitVp, cfg.SweepSteps, merged: true);

            var darkening = BenchmarkHarness.MeasureFrames(cfg.Warmup, panFramesDarkening);
            var merged = BenchmarkHarness.MeasureFrames(cfg.Warmup, panFramesMerged);

            _out.WriteLine($"pan          darkening: {darkening}");
            _out.WriteLine($"pan          merged:    {merged}");
            AssertLooseCeiling(darkening.P95Ms, CeilingMsFor(shapeCount, baseMs: 60));
        }

        // ── Zoom sweep (§3) — fixed centre (a dense cluster), zoom from full-extent to deep-in ──
        using (var surface = SKSurface.Create(new SKImageInfo(W, H)))
        {
            var zoomFramesDarkening = BuildZoomFrames(surface, view, tech, theme, fitVp, denseX, denseY, cfg.SweepSteps, merged: false);
            var zoomFramesMerged = BuildZoomFrames(surface, view, tech, theme, fitVp, denseX, denseY, cfg.SweepSteps, merged: true);

            var darkening = BenchmarkHarness.MeasureFrames(cfg.Warmup, zoomFramesDarkening);
            var merged = BenchmarkHarness.MeasureFrames(cfg.Warmup, zoomFramesMerged);

            _out.WriteLine($"zoom         darkening: {darkening}");
            _out.WriteLine($"zoom         merged:    {merged}");
            AssertLooseCeiling(darkening.P95Ms, CeilingMsFor(shapeCount, baseMs: 60));
        }

        // ── Hit-test (§3) — dense vs sparse region ───────────────────────────────
        long tolDbu = (long)(4.0 / fitVp.Zoom);
        var hitDense = BenchmarkHarness.Measure(5, 20, () => LayoutHitTest.HitStack(view, tech, denseX, denseY, tolDbu));
        var hitSparse = BenchmarkHarness.Measure(5, 20, () => LayoutHitTest.HitStack(view, tech, sparseX, sparseY, tolDbu));
        _out.WriteLine($"hit-test     dense:      {hitDense}");
        _out.WriteLine($"hit-test     sparse:     {hitSparse}");
        AssertLooseCeiling(hitDense.P95Ms, CeilingMsFor(shapeCount, baseMs: 15));

        // ── Marquee preview (§3) — LayoutEditorViewModel.ComputeMarqueeSelection, unthrottled ──
        var marquee = MeasureMarqueeDrag(view, bbox, iterations: System.Math.Max(2, cfg.Iterations / 2));
        _out.WriteLine($"marquee      100 moves:  {marquee}");
        AssertLooseCeiling(marquee.P95Ms, CeilingMsFor(shapeCount, baseMs: 200));

        // ── Load (§3) — LayoutPersistence parse time + .clay file size ───────────
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"l2a-bench-{profile}-{shapeCount}-{System.Guid.NewGuid():N}.clay");
        try
        {
            LayoutPersistence.SaveToFile(path, view);
            long fileBytes = new System.IO.FileInfo(path).Length;
            var load = BenchmarkHarness.Measure(1, System.Math.Max(2, cfg.Iterations / 2), () => LayoutPersistence.LoadFromFile(path));
            _out.WriteLine($"load         parse:     {load}  file={fileBytes:N0} bytes ({fileBytes / (double)shapeCount:F1} bytes/shape)");
            AssertLooseCeiling(load.P95Ms, CeilingMsFor(shapeCount, baseMs: 40));
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }

        _out.WriteLine("");
    }

    // ── Frame builders ───────────────────────────────────────────────────────────

    private static List<Action> BuildPanFrames(SKSurface surface, LayoutView view, Technology tech, LayoutRenderTheme theme,
        LayoutViewport fitVp, int steps, bool merged)
    {
        var frames = new List<Action>(steps);
        double spanX = fitVp.Width / fitVp.Zoom;
        double startX = fitVp.PanX - spanX;
        double endX = fitVp.PanX + spanX;
        var opts = new LayoutRenderOptions { Theme = theme, ShowGrid = false };
        for (int i = 0; i < steps; i++)
        {
            double t = steps == 1 ? 0 : i / (double)(steps - 1);
            double panX = startX + t * (endX - startX);
            var vp = fitVp with { PanX = panX };
            frames.Add(merged
                ? () => MergedPathBenchmarkRenderer.Draw(surface.Canvas, view, tech, vp, theme)
                : () => LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts));
        }
        return frames;
    }

    private static List<Action> BuildZoomFrames(SKSurface surface, LayoutView view, Technology tech, LayoutRenderTheme theme,
        LayoutViewport fitVp, long centerX, long centerY, int steps, bool merged)
    {
        var frames = new List<Action>(steps);
        double z0 = fitVp.Zoom;
        double z1 = fitVp.Zoom * 200.0; // "deep-in"
        var opts = new LayoutRenderOptions { Theme = theme, ShowGrid = false };
        for (int i = 0; i < steps; i++)
        {
            double t = steps == 1 ? 0 : i / (double)(steps - 1);
            double z = z0 * System.Math.Pow(z1 / z0, t); // log-spaced
            double panX = centerX - fitVp.Width / (2.0 * z);
            double panY = centerY - fitVp.Height / (2.0 * z);
            var vp = new LayoutViewport(panX, panY, z, fitVp.Width, fitVp.Height);
            frames.Add(merged
                ? () => MergedPathBenchmarkRenderer.Draw(surface.Canvas, view, tech, vp, theme)
                : () => LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts));
        }
        return frames;
    }

    private static BenchmarkHarness.Timing MeasureMarqueeDrag(LayoutView view, Bbox bbox, int iterations)
    {
        var vm = new LayoutEditorViewModel(view) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        long minX = bbox.IsEmpty ? -1000 : bbox.MinX, minY = bbox.IsEmpty ? -1000 : bbox.MinY;
        long maxX = bbox.IsEmpty ? 1000 : bbox.MaxX, maxY = bbox.IsEmpty ? 1000 : bbox.MaxY;

        void OneDrag()
        {
            vm.OnPointerPressed(minX, minY, Avalonia.Input.KeyModifiers.None, 1, 0);
            const int moves = 100;
            for (int i = 1; i <= moves; i++)
            {
                double t = i / (double)moves;
                long x = minX + (long)(t * (maxX - minX));
                long y = minY + (long)(t * (maxY - minY));
                // pixelDbu=0 forces a recompute on EVERY move — the unthrottled worst case the brief
                // explicitly calls out ("scans every shape on every pointer move").
                vm.OnPointerMoved(x, y, leftDown: true, Avalonia.Input.KeyModifiers.None, 0, pixelDbu: 0);
            }
            vm.OnPointerReleased(maxX, maxY, Avalonia.Input.KeyModifiers.None);
        }

        return BenchmarkHarness.Measure(1, iterations, OneDrag);
    }

    // ── Dense/sparse point finder — same 1%-of-extent grid histogram the distribution gate uses ────

    private static (long DenseX, long DenseY, long SparseX, long SparseY) FindDenseAndSparsePoints(LayoutView view, Bbox bbox)
    {
        const int grid = 20;
        long spanX = System.Math.Max(1, bbox.MaxX - bbox.MinX);
        long spanY = System.Math.Max(1, bbox.MaxY - bbox.MinY);
        long cellW = System.Math.Max(1, spanX / grid);
        long cellH = System.Math.Max(1, spanY / grid);

        var counts = new int[grid, grid];
        foreach (var shape in view.Shapes)
        {
            var bb = LayoutGeometry.BboxOf(shape);
            if (bb.IsEmpty) continue;
            long cx = (bb.MinX + bb.MaxX) / 2, cy = (bb.MinY + bb.MaxY) / 2;
            int ix = (int)System.Math.Clamp((cx - bbox.MinX) / cellW, 0, grid - 1);
            int iy = (int)System.Math.Clamp((cy - bbox.MinY) / cellH, 0, grid - 1);
            counts[ix, iy]++;
        }

        int maxIx = 0, maxIy = 0, maxCount = -1;
        int minIx = 0, minIy = 0, minCount = int.MaxValue;
        for (int ix = 0; ix < grid; ix++)
        for (int iy = 0; iy < grid; iy++)
        {
            if (counts[ix, iy] > maxCount) { maxCount = counts[ix, iy]; maxIx = ix; maxIy = iy; }
            if (counts[ix, iy] < minCount) { minCount = counts[ix, iy]; minIx = ix; minIy = iy; }
        }

        long denseX = bbox.MinX + maxIx * cellW + cellW / 2;
        long denseY = bbox.MinY + maxIy * cellH + cellH / 2;
        long sparseX = bbox.MinX + minIx * cellW + cellW / 2;
        long sparseY = bbox.MinY + minIy * cellH + cellH / 2;
        return (denseX, denseY, sparseX, sparseY);
    }

    // ── Loose catastrophe gates (R-L2a-3, gate 9) — 3x+ headroom, never a target ─────────────────

    private static double CeilingMsFor(int shapeCount, double baseMs) => shapeCount switch
    {
        <= 1_000  => baseMs * 3,
        <= 50_000 => baseMs * 3 * 25,   // 50x more shapes than the 1k tier
        _         => baseMs * 3 * 250,  // 500x more shapes than the 1k tier
    };

    private void AssertLooseCeiling(double p95Ms, double ceilingMs)
    {
        Assert.True(p95Ms < ceilingMs,
            $"p95 {p95Ms:F1}ms exceeded the loose (3x-headroom) catastrophe ceiling of {ceilingMs:F1}ms — " +
            "this is a flap-resistant backstop, not a performance target; the real per-commit gate is the counters.");
    }
}
