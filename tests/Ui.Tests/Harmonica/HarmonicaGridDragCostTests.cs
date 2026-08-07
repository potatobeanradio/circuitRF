// ================================================================
//  HarmonicaGridDragCostTests.cs  —  R-h7-12's measurement, brief-harmonicarf-h7
//
//  "Dragging one grid point invalidates exactly one Γ sample — ~8 solves ≈ 8 ms plus a re-fit. Live."
//  §8: at or above ~5 s ⇒ Category=Benchmark, in a NON-PARALLEL collection, best-of-N MINIMUM, and
//  every reported number measured ALONE.
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

        // §6.4's own claim: "~8 solves". Reported rather than pinned tightly — the count depends on
        // how far the point moved and on its warm-start neighbour.
        Assert.True(movedSolves <= 16,
            $"the moved point cost {movedSolves} solves; §6.4 predicts ~8");
    }
}
