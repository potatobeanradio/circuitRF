// ================================================================
//  FrameSchedulerTests.cs  —  M6 of brief-harmonicarf-h4-h5
//
//  TIER 5   the scheduler, on a SYNTHETIC clock: the tiers degrade in the specified order and
//           TIER A IS NEVER DEGRADED, at every frame time from comfortable to hopeless.
//           "Tier 5 is the one that matters most. It is the only check that tests the scheduler's
//            POLICY rather than its plumbing, and a policy that degrades the wrong thing is exactly
//            the failure §6.4.1 item 6 and D6 exist to prevent."
//  D4       tier A never degrades; a model that cannot hold the target is TOLD, not stuttered at.
//  D6       fit and solve are timed separately, so the scheduler can attribute correctly.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Harmonica.Tests;

public sealed class FrameSchedulerTests(ITestOutputHelper output)
{
    /// <summary>The synthetic clock. Nothing here reads the wall clock, so every result is exact.</summary>
    private sealed class Clock
    {
        public double NowMs;
        public double Read() => NowMs;
        public void Advance(double ms) => NowMs += ms;
    }

    // ── TIER 5 — the ladder degrades in the stated order ─────────────────────

    [Fact]
    public void Tier5_TheLadderDegradesInTheStatedOrder_OneRungPerOverBudgetFrame()
    {
        var clock = new Clock();
        var s = new FrameScheduler(clock.Read, targetFrameMs: 33.3);

        // §6.8's order: full user grid → coarse raster → coarse ring set → freeze-and-snap.
        var expected = new[]
        {
            FrameQuality.CoarseRaster,
            FrameQuality.CoarseGrid,
            FrameQuality.FrozenContours,
            FrameQuality.FrozenContours,   // the bottom rung holds; there is nothing below it
            FrameQuality.FrozenContours,
        };

        Assert.Equal(FrameQuality.Full, s.Quality);

        foreach (var want in expected)
        {
            var plan = s.NextPlan(dragging: true);
            s.RecordFrame(plan, Hopeless());
            clock.Advance(100);
            Assert.Equal(want, s.Quality);
        }

        // Three rungs of travel, not four — the bottom rung is not a further degradation.
        Assert.Equal(3, s.DegradeCount);
        output.WriteLine($"5 hopeless frames → {s.Quality}, {s.DegradeCount} degradation(s)");
    }

    [Theory]
    [InlineData(5.0,    "comfortable")]
    [InlineData(15.0,   "inside target")]
    [InlineData(29.0,   "at the edge")]
    [InlineData(40.0,   "over")]
    [InlineData(120.0,  "badly over")]
    [InlineData(4000.0, "hopeless")]
    public void Tier5_TierAIsNeverDegraded_AtEveryFrameTimeFromComfortableToHopeless(
        double tierBMs, string label)
    {
        // D4, checked across the whole range rather than at one convenient point: whatever the frame
        // time, EVERY plan the scheduler emits still runs tier A, and its own parameters are never
        // touched (tier A has no rings/spokes/raster — it is one Pin drive-up).
        var clock = new Clock();
        var s = new FrameScheduler(clock.Read, targetFrameMs: 33.3);

        var seen = new List<FrameQuality>();
        for (int i = 0; i < 12; i++)
        {
            var plan = s.NextPlan(dragging: true);

            Assert.True(plan.IncludesTierA, $"{label}: tier A must be in every plan");
            Assert.False(plan.Quality == FrameQuality.FrozenContours && !plan.SkipContours);

            seen.Add(plan.Quality);
            // Tier A takes a fixed, small share; the rest is tier B, which is what may degrade.
            s.RecordFrame(plan, new FrameTiming(
                TierAMs: 3.0, GridSolveMs: tierBMs * 0.6, FitMs: tierBMs * 0.05,
                RasterMs: tierBMs * 0.3, RenderMs: tierBMs * 0.05));
            clock.Advance(50);
        }

        Assert.All(seen, q => Assert.True(Enum.IsDefined(q)));
        // Tier A stayed healthy at every frame time, because tier A itself was always 3 ms.
        Assert.True(s.TierAHealthy, $"{label}: tier A was measured at 3 ms and must never be called unhealthy");
        output.WriteLine($"{label} (tier A 3 ms + tier B {tierBMs} ms): ladder ended at {s.Quality}, " +
                         $"{s.DegradeCount} down / {s.UpgradeCount} up");
    }

    [Fact]
    public void Tier5_AReleasedDrag_AlwaysSnapsBackToTheFullGridAtTheFullRaster()
    {
        // Freeze-and-snap's second half. However far the ladder fell during the drag, letting go must
        // produce the real answer — a released drag that leaves a coarse contour on screen would make
        // the degradation permanent from the user's point of view.
        var clock = new Clock();
        var s = new FrameScheduler(clock.Read, targetFrameMs: 33.3);

        for (int i = 0; i < 5; i++) { s.RecordFrame(s.NextPlan(true), Hopeless()); clock.Advance(50); }
        Assert.Equal(FrameQuality.FrozenContours, s.Quality);

        var release = s.NextPlan(dragging: false);
        Assert.Equal(FrameQuality.Full, release.Quality);
        Assert.Equal(FrameScheduler.FullRings,  release.Rings);
        Assert.Equal(FrameScheduler.FullSpokes, release.Spokes);
        Assert.Equal(FrameScheduler.FullRaster, release.RasterResolution);
        Assert.False(release.SkipContours);
    }

    [Fact]
    public void Tier5_TheLadderRecovers_ButOnlyAfterAQuietPeriod()
    {
        // Degradation is immediate; recovery is patient. Upgrading on the first comfortable frame
        // would oscillate between two rungs, which reads worse than staying one rung low.
        var clock = new Clock();
        var s = new FrameScheduler(clock.Read, targetFrameMs: 33.3) { UpgradeQuietMs = 400 };

        s.RecordFrame(s.NextPlan(true), Hopeless());
        Assert.Equal(FrameQuality.CoarseRaster, s.Quality);

        // One comfortable frame is not enough.
        clock.Advance(50);
        s.RecordFrame(s.NextPlan(true), Comfortable());
        Assert.Equal(FrameQuality.CoarseRaster, s.Quality);

        // Nor is a second, 100 ms later.
        clock.Advance(100);
        s.RecordFrame(s.NextPlan(true), Comfortable());
        Assert.Equal(FrameQuality.CoarseRaster, s.Quality);

        // Past the quiet period, it climbs one rung.
        clock.Advance(400);
        s.RecordFrame(s.NextPlan(true), Comfortable());
        Assert.Equal(FrameQuality.Full, s.Quality);
        Assert.Equal(1, s.UpgradeCount);
    }

    [Fact]
    public void Tier5_AFrameInsideTargetButNotComfortable_HoldsItsRung_NeverClimbsIntoAStutter()
    {
        // The hysteresis band. A frame at 30 ms against a 33.3 ms target is INSIDE budget, so it must
        // not degrade — but climbing on it would put the next frame over.
        var clock = new Clock();
        var s = new FrameScheduler(clock.Read, targetFrameMs: 33.3);

        s.RecordFrame(s.NextPlan(true), Hopeless());
        Assert.Equal(FrameQuality.CoarseRaster, s.Quality);

        for (int i = 0; i < 20; i++)
        {
            clock.Advance(100);
            s.RecordFrame(s.NextPlan(true), Flat(30.0));     // inside 33.3, above 0.6 × 33.3
        }

        Assert.Equal(FrameQuality.CoarseRaster, s.Quality);
        Assert.Equal(0, s.UpgradeCount);
        Assert.Equal(1, s.DegradeCount);
    }

    // ── D4 — tier A alone over budget is REPORTED, never silently stuttered ──

    [Fact]
    public void D4_WhenTierAAloneMissesTheTarget_TheSchedulerSaysSo_RatherThanStutteringQuietly()
    {
        var clock = new Clock();
        var s = new FrameScheduler(clock.Read, targetFrameMs: 33.3);

        Assert.True(s.TierAHealthy);
        Assert.Null(s.StatusMessage);

        s.RecordFrame(s.NextPlan(true), new FrameTiming(
            TierAMs: 90.0, GridSolveMs: 0, FitMs: 0, RasterMs: 0, RenderMs: 0));

        Assert.False(s.TierAHealthy);
        Assert.NotNull(s.StatusMessage);
        Assert.Contains("90", s.StatusMessage);
        output.WriteLine(s.StatusMessage!);

        // Latched: the message must not flicker off on the next merely-comfortable tier-B frame.
        clock.Advance(1000);
        s.RecordFrame(s.NextPlan(true), Comfortable());
        Assert.False(s.TierAHealthy);
        Assert.NotNull(s.StatusMessage);
    }

    [Fact]
    public void D4_TierAStaysHealthy_WhenOnlyTierBIsSlow()
    {
        // The negative control. Without it the test above would pass against a scheduler that calls
        // tier A unhealthy whenever ANY frame runs long, which would put a false claim on screen.
        //
        // R-h9r2-2 (brief-harmonicarf-r2a) — every tier-B rung's own contour-quality message is gone;
        // DescribeLadder writes nothing once tier A is healthy. The ladder still degrades exactly as
        // before (asserted below via Quality) — only the retired "Contours frozen while dragging"
        // style string is gone, so StatusMessage stays null here.
        var clock = new Clock();
        var s = new FrameScheduler(clock.Read, targetFrameMs: 33.3);

        for (int i = 0; i < 6; i++)
        {
            s.RecordFrame(s.NextPlan(true), new FrameTiming(
                TierAMs: 4.0, GridSolveMs: 900, FitMs: 5, RasterMs: 60, RenderMs: 8));
            clock.Advance(50);
        }

        Assert.True(s.TierAHealthy);
        Assert.Equal(FrameQuality.FrozenContours, s.Quality);
        Assert.Null(s.StatusMessage);
    }

    [Fact]
    public void D4_AStructuralResetClearsTheLadderAndTheClaim()
    {
        // The last model's cost says nothing about this one's — including whether tier A can hold.
        var clock = new Clock();
        var s = new FrameScheduler(clock.Read, targetFrameMs: 33.3);

        s.RecordFrame(s.NextPlan(true), new FrameTiming(500, 500, 5, 50, 8));
        Assert.False(s.TierAHealthy);
        Assert.NotEqual(FrameQuality.Full, s.Quality);

        s.Reset();
        Assert.True(s.TierAHealthy);
        Assert.Equal(FrameQuality.Full, s.Quality);
        Assert.Null(s.StatusMessage);
    }

    // ── D6 — solve and fit are attributed separately ─────────────────────────

    [Theory]
    [InlineData(FrameStage.GridSolve)]
    [InlineData(FrameStage.Fit)]
    [InlineData(FrameStage.Raster)]
    [InlineData(FrameStage.Render)]
    [InlineData(FrameStage.TierA)]
    public void D6_TheDominantStageIsAttributedCorrectly(FrameStage dominant)
    {
        // The measurement D6 asks for: a scheduler that lumped fit and solve could not tell these
        // five cases apart, and would report (and eventually degrade) the wrong one.
        double big = 500, small = 1;
        var t = new FrameTiming(
            TierAMs:     dominant == FrameStage.TierA     ? big : small,
            GridSolveMs: dominant == FrameStage.GridSolve ? big : small,
            FitMs:       dominant == FrameStage.Fit       ? big : small,
            RasterMs:    dominant == FrameStage.Raster    ? big : small,
            RenderMs:    dominant == FrameStage.Render    ? big : small);

        Assert.Equal(dominant, t.Dominant);
        Assert.Equal(big + small * 4, t.TotalMs);
    }

    [Fact]
    public void D6_ADominantGridSolveFrame_DegradesWithNoStatusMessage_WhileTierAStaysHealthy()
    {
        // R-h9r2-2 (brief-harmonicarf-r2a) supersedes this test's own original name/assertion: the
        // status strip no longer names the dominant stage (that "GridSolve" string is one of the
        // retired tier-B messages). Attribution itself still exists — it is what D6_TheDominantStage-
        // AttributedCorrectly (above) pins directly against FrameTiming.Dominant — it simply no longer
        // reaches StatusMessage. Tier A stays healthy here (3 ms << target), so DescribeLadder runs
        // and leaves StatusMessage null.
        var clock = new Clock();
        var s = new FrameScheduler(clock.Read, targetFrameMs: 33.3);

        var timing = new FrameTiming(
            TierAMs: 3, GridSolveMs: 400, FitMs: 2, RasterMs: 10, RenderMs: 5);
        Assert.Equal(FrameStage.GridSolve, timing.Dominant);

        s.RecordFrame(s.NextPlan(true), timing);

        Assert.True(s.TierAHealthy);
        Assert.Null(s.StatusMessage);
    }

    // ── the plans themselves ─────────────────────────────────────────────────

    [Fact]
    public void EveryRungsPlanMatchesTheDesignNotesOwnNumbers()
    {
        var clock = new Clock();
        var s = new FrameScheduler(clock.Read, targetFrameMs: 33.3);

        var plans = new List<FramePlan> { s.NextPlan(true) };
        for (int i = 0; i < 3; i++)
        {
            s.RecordFrame(plans[^1], Hopeless());
            clock.Advance(50);
            plans.Add(s.NextPlan(true));
        }

        // brief-harmonicarf-r6a §5.1 — owner request: the default full grid moved from 5×12 to 3×12
        // (a new document now starts there), and the coarse ring set moved down with it, from 3×12 to
        // 2×12, so the ladder keeps its two distinct rungs rather than collapsing to one. D5: 256 / 96.
        Assert.Equal(new FramePlan(FrameQuality.Full,           3, 12, 256, false), plans[0]);
        Assert.Equal(new FramePlan(FrameQuality.CoarseRaster,   3, 12,  96, false), plans[1]);
        Assert.Equal(new FramePlan(FrameQuality.CoarseGrid,     2, 12,  96, false), plans[2]);
        Assert.Equal(new FramePlan(FrameQuality.FrozenContours, 2, 12,  96, true ), plans[3]);

        // The point counts the design note names, arrived at from the ring/spoke numbers rather than
        // restated: rings × spokes + the centre point.
        Assert.Equal(37, FrameScheduler.FullRings   * FrameScheduler.FullSpokes   + 1);
        Assert.Equal(25, FrameScheduler.CoarseRings * FrameScheduler.CoarseSpokes + 1);
    }

    private static FrameTiming Comfortable() => new(2, 6, 1, 4, 3);        // 16 ms
    private static FrameTiming Hopeless()    => new(4, 800, 6, 90, 10);    // 910 ms
    private static FrameTiming Flat(double totalMs) =>
        new(totalMs * 0.1, totalMs * 0.5, totalMs * 0.05, totalMs * 0.25, totalMs * 0.1);
}
