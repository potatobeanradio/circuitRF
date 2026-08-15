// ================================================================
//  HarmonicaGridDragCostTests.cs  —  R-h7-12's measurement, brief-harmonicarf-h7
//
//  "Dragging one grid point invalidates exactly one Γ sample — ~8 solves ≈ 8 ms plus a re-fit. Live."
//  §8: at or above ~5 s ⇒ Category=Benchmark, in a NON-PARALLEL collection, best-of-N MINIMUM, and
//  every reported number measured ALONE.
//
//  R9C §5.2 re-measurement (2026-08-15) — the FIRST half changed, the SECOND did not, exactly as the
//  brief predicted: the per-point search became PinSearch.Sweep's ladder (R9C §3), which walks EVERY
//  rung from PinStart to PinMax rather than a ~5-solve secant, so both the full-rebuild and the
//  one-point counts rose. R-h7-12's own reuse mechanism (keyed on Γ, search-independent) is UNCHANGED —
//  still exactly 60 of 61 points reused. New numbers: full rebuild 1319 HB solves / 476.2 ms (was 272 /
//  547.8 ms); one dragged point 23 HB solves / 7.3 ms (was 3 / 3.3 ms) with 60 points reused (unchanged).
//  Wall-clock for a drag stayed well under budget (7.3 ms, still ~65× faster than a full rebuild) even
//  though the solve COUNT per point rose ~7.7×, because each ladder rung is a cheap, well-warm-started
//  solve — the accuracy/robustness R9C bought is not free in solve count, but is still free in the
//  frame-rate sense that actually matters for a live drag.
// ================================================================

using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

[Collection("HarmonicaUiBenchmarks")]
[Trait("Category", "Benchmark")]
public sealed class HarmonicaGridDragCostTests(ITestOutputHelper output)
{
    private static (HarmonicaContext Ctx, TerminationSet Terms) Fixture()
    {
        // The shipping default document's own device, with a realistic package — the same fixture
        // shape H6's inverse-solve measurements used.
        var model = HarmonicaViewModel.DefaultModel() with
        {
            Embedding = new EmbeddingStack
            {
                Package = new LumpedPackage { Rd = 4.0, Rs = 0.8, Ls = 0.05e-9 },
            },
        };
        var terms = new TerminationSet(model.Settings.HarmonicCount);
        terms.Set(TerminationSide.Source, 1, new Complex(25, 0));
        terms.Set(TerminationSide.Load,   1, new Complex(80, 10));
        return (HarmonicaContext.Create(model), terms);
    }

    /// <summary>Best of N — a MINIMUM, never a mean or a median. This repo has been bitten three
    /// times by a mean, and once by a median.</summary>
    private static double BestOf(int n, Action body)
    {
        double best = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            var sw = Stopwatch.StartNew();
            body();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public void OneDraggedGridPoint_CostsOneGammaSample_MeasuredAgainstAFullRebuild()
    {
        var (ctx, terms) = Fixture();
        var scatter = ContourGrid.RingGrid(5, 12).ToArray();      // §6.8's full user grid, 61 points

        // ── a FULL rebuild, warm (the caches are what a drag would have) ──────
        var grid = new ContourGrid();
        grid.Build(ctx, terms, scatter, reuseUnchanged: false);   // prime
        int fullSolves = grid.SolveCount;

        double fullMs = BestOf(3, () =>
        {
            grid.Build(ctx, terms, scatter, reuseUnchanged: false);
            grid.Fit(GridMetric.PoutDbm);
            grid.Fit(GridMetric.DrainEfficiency);
        });

        // ── ONE point moved, with the rest kept ──────────────────────────────
        var reuse = new ContourGrid();
        reuse.Build(ctx, terms, scatter, reuseUnchanged: true);

        // ONE index, moved a little further each iteration. Deliberately not a different index each
        // time: the cache holds the PREVIOUS iteration's grid, so moving a fresh index would leave
        // TWO points differing from it and the run would measure two Γ samples, not one.
        const int At = 7;
        int i = 0;
        int movedSolves = 0;
        double dragMs = BestOf(5, () =>
        {
            var moved = scatter.ToArray();
            moved[At] = scatter[At] * (0.97 - 0.01 * i++);
            reuse.Build(ctx, terms, moved, reuseUnchanged: true);
            reuse.Fit(GridMetric.PoutDbm);
            reuse.Fit(GridMetric.DrainEfficiency);
            movedSolves = reuse.Points[At].Result.Solves;
        });

        output.WriteLine($"grid: {scatter.Length} Γ points");
        output.WriteLine($"  full rebuild        {fullSolves,4} HB solves   {fullMs,8:F1} ms");
        output.WriteLine($"  one point dragged   {movedSolves,4} HB solves   {dragMs,8:F1} ms" +
                         $"   ({reuse.ReusedPointCount} points reused)");
        output.WriteLine($"  ratio               {(double)fullSolves / Math.Max(1, movedSolves),8:F1}× solves" +
                         $"   {fullMs / Math.Max(1e-9, dragMs),8:F1}× wall");

        Assert.Equal(scatter.Length - 1, reuse.ReusedPointCount);
        Assert.True(dragMs < fullMs / 4,
            $"a dragged point took {dragMs:F1} ms against a full rebuild's {fullMs:F1} ms — that is " +
            "not one Γ sample's worth of work");

        // §6.4's own original claim was "~8 solves", against PinSearch.Run's secant. R9C §3 replaced
        // the per-point search with PinSearch.Sweep's ladder (walking every 2 dB rung from PinStart to
        // PinMax, rather than a ~5-solve secant), so the honest budget is now the ladder's own rung
        // count — measured 23 on this fixture, comfortably under PinMax(50)−PinStart(−10) / 2 dB = 30
        // rungs (the tickle plus the SweepOverdriveDb early-stop trims it below the theoretical max).
        Assert.True(movedSolves <= 30,
            $"the moved point cost {movedSolves} solves; R9C's ladder predicts at most ~30 rungs " +
            "(PinStart to PinMax at ContourLadderStepDbm, plus the tickle)");
    }
}
