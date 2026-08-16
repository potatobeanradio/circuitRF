// ================================================================
//  FrameScheduler.cs  —  M6 of brief-harmonicarf-h4-h5
//
//  R-h45-10  §6.8. Measures actual completion times and adapts grid density to hold the frame
//            target. Tier A NEVER degrades (D4). Tier B degrades in a stated order:
//            full user grid → coarse ring set (3 × 12 = 37) → freeze-and-snap. Tier C is computed
//            once and held. Fed a clock, so it is deterministic and testable headless (D1).
//  D6        Fit and solve are timed SEPARATELY, because a scheduler that lumps them cannot tell
//            "the solver is slow" from "the fit is slow" and will degrade the wrong one.
// ================================================================

using System;

namespace CircuitRF.Harmonica;

/// <summary>
/// §6.8's tier-B ladder, in the order it degrades. The order is the design note's, not a preference:
/// the raster gives first because degrading it is nearly free perceptually, then the grid, and only
/// then the contours themselves.
/// </summary>
public enum FrameQuality
{
    /// <summary>The full user grid at the full raster. What a released drag always gets.</summary>
    Full,

    /// <summary>Full grid, coarse raster (D5). The contour's shape survives; its polyline coarsens.</summary>
    CoarseRaster,

    /// <summary>§6.8's coarse ring set, 3 × 12 = 37 points, at the coarse raster.</summary>
    CoarseGrid,

    /// <summary>
    /// Freeze-and-snap: contours are not solved at all during the drag (the previous frame's are
    /// ghosted) and are computed once on release. Tier A still runs — that is the point.
    /// </summary>
    FrozenContours,
}

/// <summary>Which stage dominated a frame. D6's whole reason for timing them apart.</summary>
public enum FrameStage { TierA, GridSolve, Fit, Raster, Render }

/// <summary>
/// One frame's measured cost, by stage. <paramref name="TierAMs"/> is the single Pin drive-up and is
/// counted separately from <paramref name="GridSolveMs"/> so D4 can be checked: if tier A ALONE
/// cannot hold the target, no amount of tier-B degradation will help and the scheduler must say so
/// rather than stutter quietly.
/// </summary>
public readonly record struct FrameTiming(
    double TierAMs,
    double GridSolveMs,
    double FitMs,
    double RasterMs,
    double RenderMs)
{
    public double TotalMs => TierAMs + GridSolveMs + FitMs + RasterMs + RenderMs;

    /// <summary>The most expensive stage. Reported, not acted on directly — the ladder's ORDER is
    /// fixed by §6.8; this is what lets the status strip name what is actually costing.</summary>
    public FrameStage Dominant
    {
        get
        {
            var best = FrameStage.TierA;
            double bestMs = TierAMs;
            if (GridSolveMs > bestMs) { best = FrameStage.GridSolve; bestMs = GridSolveMs; }
            if (FitMs       > bestMs) { best = FrameStage.Fit;       bestMs = FitMs; }
            if (RasterMs    > bestMs) { best = FrameStage.Raster;    bestMs = RasterMs; }
            if (RenderMs    > bestMs) { best = FrameStage.Render; }
            return best;
        }
    }
}

/// <summary>What to solve for the next frame.</summary>
/// <param name="Quality">Where on the tier-B ladder this frame sits.</param>
/// <param name="Rings">Γ grid rings. Tier B only — tier A does not have a grid.</param>
/// <param name="Spokes">Γ grid spokes.</param>
/// <param name="RasterResolution">D5's 96 or 256.</param>
/// <param name="SkipContours">Freeze-and-snap: solve tier A only and ghost the previous contours.</param>
public readonly record struct FramePlan(
    FrameQuality Quality,
    int Rings,
    int Spokes,
    int RasterResolution,
    bool SkipContours)
{
    /// <summary>
    /// D4, stated as a property so it can be asserted rather than assumed: tier A is in EVERY plan,
    /// at every quality. There is no ladder rung that turns it off.
    /// </summary>
    public bool IncludesTierA => true;
}

/// <summary>
/// The frame pump's policy. Holds no threads, no timers and no dispatcher — it is fed a clock and a
/// measurement per frame and answers "what should the next frame be". That is what makes §6.8
/// testable on a synthetic clock (D1) instead of only by moving a mouse.
/// </summary>
public sealed class FrameScheduler
{
    /// <summary>
    /// §6.8's full user grid. brief-harmonicarf-r6a §5.1 — owner request: a new document now starts at
    /// 3 × 12 (was 5 × 12). Chosen as option (b) over (a): <see cref="CoarseRings"/> moved DOWN to
    /// 2 × 12 rather than colliding with this at 3 × 12, so the ladder keeps its two distinct rungs
    /// rather than flattening to one — the ladder exists to make a drag cheap, and collapsing it would
    /// silently push drag cost up to the full-quality solve on every frame.
    /// </summary>
    public const int FullRings = 3, FullSpokes = 12;

    /// <summary>§6.8's coarse ring set: 2 × 12 = 25 points (moved down from 3 × 12 by §5.1 above, to
    /// stay strictly below the new <see cref="FullRings"/>).</summary>
    public const int CoarseRings = 2, CoarseSpokes = 12;

    /// <summary>D5's two raster resolutions.</summary>
    public const int FullRaster = 256, CoarseRaster = 96;

    private readonly Func<double> _nowMs;

    private FrameQuality _quality = FrameQuality.Full;
    private double _healthySince = double.NaN;

    /// <param name="nowMs">
    /// The clock, in milliseconds. Injected so a test can step it deterministically; production
    /// passes a stopwatch. A scheduler that reads the wall clock directly cannot be tested.
    /// </param>
    /// <param name="targetFrameMs">
    /// §6.8's frame target. 33.3 ms is 30 fps, which is what the design note asks a drag to hold.
    /// </param>
    public FrameScheduler(Func<double> nowMs, double targetFrameMs = 1000.0 / 30.0)
    {
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
        TargetFrameMs = targetFrameMs;
    }

    public double TargetFrameMs { get; }

    /// <summary>
    /// How far under target a frame must land before the scheduler considers upgrading. Deliberately
    /// well under 1.0: upgrading at the target's edge would oscillate between two rungs, which reads
    /// worse than staying one rung low.
    /// </summary>
    public double UpgradeHeadroom { get; init; } = 0.6;

    /// <summary>
    /// How long frames must stay comfortable before an upgrade. Degradation is IMMEDIATE (one
    /// over-budget frame is enough — responsiveness is the whole point); recovery is patient.
    /// </summary>
    public double UpgradeQuietMs { get; init; } = 400.0;

    /// <summary>Where the ladder currently stands.</summary>
    public FrameQuality Quality => _quality;

    /// <summary>
    /// D4. False once tier A ALONE has been measured over the frame target: no tier-B degradation
    /// can recover that. Latching is deliberate — a model that cannot hold 30 fps on one drive-up
    /// does not become able to between two frames. The status-strip message this used to drive
    /// ("running the coarsest contour grid to keep up") is retired; this flag is the signal a
    /// caller reads instead.
    /// </summary>
    public bool TierAHealthy { get; private set; } = true;

    /// <summary>What the status strip should say, or null when there is nothing to report.</summary>
    public string? StatusMessage { get; private set; }

    /// <summary>The last frame's measurement, for whoever wants to report the cost breakdown.</summary>
    public FrameTiming LastTiming { get; private set; }

    /// <summary>How many times the ladder has stepped down, and up. Tier 5 reads these.</summary>
    public int DegradeCount { get; private set; }
    public int UpgradeCount { get; private set; }

    /// <summary>
    /// What to solve next. <paramref name="dragging"/> false is the "snap" of freeze-and-snap: a
    /// released drag always gets the full grid at the full raster, whatever the ladder said mid-drag.
    /// </summary>
    public FramePlan NextPlan(bool dragging)
    {
        if (!dragging) return PlanFor(FrameQuality.Full);
        return PlanFor(_quality);
    }

    /// <summary>
    /// Records what a frame actually cost and moves the ladder. The plan is passed back in so the
    /// scheduler never attributes a measurement to the wrong rung — a frame that completed late at
    /// the lowest rung says something quite different from one that completed late at the top.
    /// </summary>
    public void RecordFrame(FramePlan plan, FrameTiming timing)
    {
        LastTiming = timing;
        double now = _nowMs();

        // D4 FIRST, and independent of the ladder: tier A is what cannot be degraded, so if it alone
        // misses the target the scheduler stops pretending the ladder can fix it.
        if (timing.TierAMs > TargetFrameMs)
        {
            TierAHealthy = false;
        }

        if (timing.TotalMs > TargetFrameMs)
        {
            _healthySince = double.NaN;
            if (Degrade()) DescribeLadder(timing);
            return;
        }

        // Comfortable. Start (or continue) the quiet period before considering an upgrade.
        if (timing.TotalMs <= TargetFrameMs * UpgradeHeadroom)
        {
            if (double.IsNaN(_healthySince)) _healthySince = now;
            else if (now - _healthySince >= UpgradeQuietMs)
            {
                _healthySince = now;
                if (Upgrade()) DescribeLadder(timing);
            }
        }
        else
        {
            // Inside target but not comfortably: hold this rung rather than climbing into a stutter.
            _healthySince = double.NaN;
        }

        if (_quality == FrameQuality.Full && TierAHealthy) StatusMessage = null;
    }

    /// <summary>Resets the ladder to the top. Called when the model changes structurally — the last
    /// model's cost says nothing about this one's.</summary>
    public void Reset()
    {
        _quality      = FrameQuality.Full;
        _healthySince = double.NaN;
        TierAHealthy  = true;
        StatusMessage = null;
    }

    /// <summary>
    /// §7.6's Grid menu preset, overriding the ladder's own FULL ring set. Null restores §6.8's
    /// 5 × 12.
    ///
    /// <para><b>It overrides the TOP rung only.</b> The coarse rung stays 3 × 12 unless the preset is
    /// coarser than that, in which case the two collapse — degrading a grid the user deliberately
    /// made small would take away the thing they asked for, and there is nothing below it worth
    /// having.</para>
    /// </summary>
    public void SetGridPreset(int? rings, int? spokes)
    {
        _presetRings  = rings  is > 0 ? rings  : null;
        _presetSpokes = spokes is > 0 ? spokes : null;
    }

    private int? _presetRings, _presetSpokes;

    /// <summary>The full grid this scheduler will ask for — the preset when one is set.</summary>
    public (int Rings, int Spokes) FullGrid
        => (_presetRings ?? FullRings, _presetSpokes ?? FullSpokes);

    /// <summary>The coarse grid, never coarser than the full one.</summary>
    public (int Rings, int Spokes) CoarseGridSet
        => (Math.Min(CoarseRings, FullGrid.Rings), Math.Min(CoarseSpokes, FullGrid.Spokes));

    private FramePlan PlanFor(FrameQuality q)
    {
        var (fr, fs) = FullGrid;
        var (cr, cs) = CoarseGridSet;
        return q switch
        {
            FrameQuality.Full           => new(q, fr, fs, FullRaster,   false),
            FrameQuality.CoarseRaster   => new(q, fr, fs, CoarseRaster, false),
            FrameQuality.CoarseGrid     => new(q, cr, cs, CoarseRaster, false),
            FrameQuality.FrozenContours => new(q, cr, cs, CoarseRaster, true),
            _ => throw new ArgumentOutOfRangeException(nameof(q), q, null),
        };
    }

    private bool Degrade()
    {
        if (_quality == FrameQuality.FrozenContours) return false;   // the bottom rung
        _quality = _quality + 1;
        DegradeCount++;
        return true;
    }

    private bool Upgrade()
    {
        if (_quality == FrameQuality.Full) return false;
        _quality = _quality - 1;
        UpgradeCount++;
        return true;
    }

    /// <summary>
    /// R-h9r2-2 — every tier-B rung's own contour-quality message is GONE (report 2: "live contour
    /// generation is too slow — deactivate it during drags and remove all 'Contours frozen while
    /// dragging' style messages"). The <see cref="FrameQuality"/> enum and the ladder ITSELF are
    /// unchanged — a drag still degrades the raster and the grid for whatever cost tier A alone
    /// leaves on the table — only the strings describing which rung it landed on are deleted.
    /// D4's Tier-A health message is untouched: it is the one message this brief does not touch,
    /// because a model that cannot hold the frame target on tier A ALONE is a fact the ladder cannot
    /// fix and the user still needs to be told.
    /// </summary>
    private void DescribeLadder(FrameTiming timing)
    {
        if (!TierAHealthy) return;                                    // D4's message outranks this
        StatusMessage = null;
    }
}
