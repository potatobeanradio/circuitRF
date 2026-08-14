using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using CircuitRF.Harmonica;
using RfCore.Data;
using RfCore.Loadpull;

namespace CircuitRF.Ui.Harmonica;

/// <summary>
/// Turns a solved circuit into a <see cref="HarmonicaFrame"/> — the four panels' contents and nothing
/// they have to compute themselves.
///
/// <para><b>This RECOMPUTES nothing (§0.3 item 1).</b> "The engine is DONE and every number the panels
/// need already exists. Do not recompute any of it in a view-model."
/// <c>PinSearch.Run</c> returns the whole power sweep <i>and</i> the compression point;
/// <c>ContourGrid</c> returns points, holes, contours and the extrema; <c>HarmonicaDataSet.Build</c>
/// publishes the intrinsic plane including <c>Gamma_intr</c> and the time-domain loadline;
/// <c>DcivFamily</c> supplies tier C. Everything below is a READ of those, plus formatting.</para>
///
/// <para><b>Tier C is cached here, keyed by <see cref="DcivFamily.Key"/>.</b> §6.8: the DCIV family
/// depends only on the model, its parameters and the bias sweep range — never on terminations — so a
/// termination drag must not recompute it. The key comparison is what enforces that; a
/// termination change cannot alter the key, so it cannot invalidate the cache even by accident.</para>
///
/// <para><b>Single-threaded and synchronous, on purpose.</b> M5's <c>SolvePool</c> is what makes this
/// concurrent and cancellable; putting threading in here first would mean writing it twice.</para>
/// </summary>
public sealed class HarmonicaSolver
{
    private readonly Lock                 _dcivGate = new();
    private DcivFamily.Key?               _dcivKey;
    private IReadOnlyList<DcivCurve>      _dciv = [];

    /// <summary>How many times the DCIV family has actually been computed. Tier C's own gate —
    /// a counter rather than a clock, per this repo's convention.</summary>
    public int DcivComputeCount { get; private set; }

    /// <summary>How many HB solves the last <see cref="Solve"/> cost, everything included.</summary>
    public int LastSolveCount { get; private set; }

    /// <summary>R-h7-12 — how many Γ points the last frame kept rather than re-solved.</summary>
    public int LastGridPointsReused { get; private set; }

    /// <summary>
    /// R-h9r2-19 lever 1 — the PREVIOUS frame's tier-A sweep, converged spectrum per Pin LEVEL
    /// (keyed by the level itself, rounded). A marker drag perturbs the termination only slightly
    /// between frames, so the prior frame's solution AT THE SAME Pin is a far better warm-start seed
    /// than the ladder's own neighbouring rung — this is what lets every point stay a real solve on
    /// every frame, drag included, without the frame rate collapsing. Belongs to this solver instance,
    /// never static (src/Harmonica/CLAUDE.md: "H5 gives each worker its own context").
    /// </summary>
    private Dictionary<double, Complex[,]>? _lastSweepLevelSpectra;

    /// <summary>
    /// brief-harmonicarf-r4 §5.2/§5.3 — Policy C, the hedge. HB solutions across the termination plane
    /// are NOT a smooth family: two nearby Γ can sit in different basins, so on a LARGE drag jump
    /// <see cref="_lastSweepLevelSpectra"/> is not merely a slightly-wrong seed, it can be an actively
    /// misleading one — measured directly (<c>DragSeedPolicyTests.AvsB_CrossoverPoint_WhereLever1StopsHelping</c>):
    /// reading lever 1 wins clearly for |ΔΓ| up to ~0.15, ties through ~0.20-0.25, and LOSES from ~0.30
    /// up (a single-frame spike to ~18 ms against a smooth ~10-13 ms elsewhere — a continuation-stepping
    /// fallback firing inside the ladder, not a graceful degradation). 0.20 sits just past where lever 1
    /// stops winning outright, so this only disables it where the measurement shows it stops helping.
    /// Policy B (never read it at all) was tried first and measurably LOST on small/moderate drags —
    /// ~24% slower at |ΔΓ| ≈ 0.004, still slower at the tangential-drag control's ≈0.13 — so the plain
    /// "always DC, never reuse" policy was not adopted outright; this hedge is.
    /// </summary>
    public const double LeverOneDeltaGammaThreshold = 0.20;

    /// <summary>The Γ (per marked side/band) that <see cref="_lastSweepLevelSpectra"/> was solved at —
    /// what <see cref="LeverOneDeltaGammaThreshold"/> is measured against. A band with no prior-frame
    /// entry (just marked) counts as an infinite jump, so a freshly added marker never reads a stale
    /// spectrum meant for a different termination entirely.</summary>
    private Dictionary<(TerminationSide Side, int Band), Complex> _lastTerminationGammas = [];

    /// <summary>How many frames disabled lever 1 because the termination moved past
    /// <see cref="LeverOneDeltaGammaThreshold"/> since the previous frame — a counter, not a stopwatch,
    /// this repo's own convention for making a hedge's own hit rate visible rather than inferred.</summary>
    public int Lever1DisabledCount { get; private set; }

    private static readonly TerminationSide[] Sides = [TerminationSide.Source, TerminationSide.Load];

    /// <summary>Options a frame is solved under. Defaults are the coarse ring set of §6.8, so the
    /// first frame after opening a document is fast rather than correct-and-slow.</summary>
    public sealed record Options
    {
        /// <summary>§6.8's coarse ring set is 3 × 12 = 37 points; the full user grid is 5 × 12 = 61.</summary>
        public int Rings  { get; init; } = 3;
        public int Spokes { get; init; } = 12;
        public double MaxGamma { get; init; } = 0.8;

        /// <summary>D8 — auto with override, defaulting to 10 levels.</summary>
        public int Levels { get; init; } = 10;

        /// <summary>D5's drag resolution. Degrading the RASTER is nearly free perceptually — the
        /// contour's shape survives; only its polyline gets a little coarser. Degrading the GRID
        /// loses information, which is why the raster is the first thing to give.</summary>
        public const int CoarseRasterResolution = 96;

        /// <summary>D5's release resolution, and the default.</summary>
        public const int FullRasterResolution = 256;

        /// <summary>D5 — 96 during a drag, 256 on release.</summary>
        public int RasterResolution { get; init; } = FullRasterResolution;

        /// <summary>
        /// D5's switch, as one call so a caller can never set the two resolutions to values that are
        /// not the two the design note names. The scheduler (M6) is what decides <c>dragging</c>;
        /// this only encodes what the two states mean.
        /// </summary>
        public Options WithRasterFor(bool dragging) => this with
        {
            RasterResolution = dragging ? CoarseRasterResolution : FullRasterResolution,
        };

        /// <summary>§7.2 — drain efficiency is the default for the right-hand chart.</summary>
        public GridMetric EfficiencyMetric { get; init; } = GridMetric.DrainEfficiency;

        /// <summary>§7.3's plane toggle — one toggle for the DCIV family AND the loadline.</summary>
        public bool IntrinsicPlane { get; init; } = true;

        /// <summary>Skip the two contour maps entirely. Tier A alone (§6.8) — one Pin drive-up, which
        /// is what a fast first frame and a mid-drag frame both want.</summary>
        public bool SkipContours { get; init; }

        /// <summary>Which ladder rung this frame is being solved at, stamped onto the frame so a
        /// consumer can tell what the user actually saw rather than what the scheduler says now.</summary>
        public FrameQuality Quality { get; init; } = FrameQuality.Full;

        /// <summary>R-h6-12's shading, when a drag has one. Passed through untouched — the sampler is
        /// framework-free and lives in <c>src/Harmonica</c>; this only carries the answer.</summary>
        public ReachableRegion? Reachable { get; init; }

        /// <summary>The drive one operating point is evaluated at when <see cref="AtPavlDbm"/> is set,
        /// instead of the compression point. R-h6-11's user-placed power-sweep cursor.</summary>
        public double? AtPavlDbm { get; init; }

        /// <summary>
        /// R-h7-11 — an explicit Γ scatter, superseding <see cref="Rings"/>/<see cref="Spokes"/>
        /// entirely. An imported <c>.gam</c> or a ring set with a point dragged is not a lattice, and
        /// §6.4 built <c>ContourGrid</c> for exactly that.
        /// </summary>
        public IReadOnlyList<Complex>? GammaGrid { get; init; }

        /// <summary>
        /// §6.5 — which termination plane the contour grid sweeps. Load by default.
        ///
        /// <para><b>Document-wide, not per chart, and that is a cost decision rather than an
        /// oversight.</b> §6.5 words the plane and harmonic selectors as per-chart; this solver builds
        /// ONE <c>ContourGrid</c> and derives both metrics from it, which is why H4–H5's own cost
        /// table shows a single grid solve per frame. Two independently-swept charts would be two
        /// grids, i.e. double the dominant term of a frame.</para>
        /// </summary>
        public TerminationSide GridSide { get; init; } = TerminationSide.Load;

        /// <summary>§6.5 — which harmonic band the contour grid sweeps. The fundamental by default.</summary>
        public int GridHarmonic { get; init; } = 1;

        /// <summary>
        /// R-h7-12 — keep the solve of every Γ point that did not move. Set during a GRID-POINT drag,
        /// where by construction exactly one point is new; left off otherwise, because an ordinary
        /// frame's grid has nothing in common with the previous one's.
        /// </summary>
        public bool ReuseUnchangedGridPoints { get; init; }
    }

    /// <summary>
    /// Solves one frame. <paramref name="terminations"/> is not mutated — the grid works on a clone,
    /// so the markers the user set survive a contour sweep.
    /// </summary>
    /// <param name="grid">
    /// R-h45-8's pooled grid. A <see cref="SolvePool{T}"/> worker passes its OWN
    /// <see cref="ContourGrid"/> so the RBF factorization it caches survives across frames; a caller
    /// with no pool passes nothing and gets a fresh one, which is what every synchronous path does.
    /// </param>
    /// <param name="ct">
    /// R-h45-9. Threaded into the grid build, whose cancellation point is between Γ points.
    /// </param>
    /// <param name="previousPower">
    /// R-h9r2-1 — the PREDECESSOR frame's power-panel data, so a grid-less frame (<c>SkipContours</c>,
    /// whatever produced it) can carry its <c>Contours</c>/<c>Levels</c>/<c>GridPoints</c>/<c>Optimum</c>
    /// forward instead of publishing empty ones. Null on the very first frame of a session. Never
    /// consulted when <c>!opt.SkipContours</c> — a frame that genuinely swept the grid needs nothing
    /// carried into it.
    /// </param>
    /// <param name="previousEfficiency">The efficiency panel's own predecessor, same rule.</param>
    public HarmonicaFrame Solve(HarmonicaContext ctx, TerminationSet terminations,
                                IReadOnlyList<HarmonicaMarker> markers, Options? options = null,
                                ContourGrid? grid = null, CancellationToken ct = default,
                                Action<int, int>? onGridProgress = null,
                                Func<string, ReadoutFormat>? readoutFormat = null,
                                SmithPanelData? previousPower = null,
                                SmithPanelData? previousEfficiency = null)
    {
        var opt = options ?? new Options();
        LastSolveCount = 0;

        // D6 / R-h6-4 — the stages are timed APART. A scheduler that lumps fit and solve together
        // cannot tell "the solver is slow" from "the fit is slow" and will degrade the wrong one.
        var stage = System.Diagnostics.Stopwatch.StartNew();
        double tierAMs, gridSolveMs = 0, fitMs = 0, rasterMs = 0;

        // ── tier A: the EXPLICIT power sweep at the current terminations (R-h9r2-17/19) ────────
        // PinSearch.Run stays the contour grid's alone (§5.1's own guardrail) — tier A always drives
        // the user's own ladder now, every point a real solve, on every frame including a drag.
        ct.ThrowIfCancellationRequested();
        var s0 = ctx.Model.Settings;

        // §5.2/§5.3 — Policy C. Judged by the LARGEST single-band Γ move, not an average across
        // bands: one band jumping while the others sit still is exactly the case a hidden average
        // would mask.
        double maxDeltaGamma = 0.0;
        var currentGammas = new Dictionary<(TerminationSide, int), Complex>();
        foreach (var side in Sides)
            foreach (int band in terminations.MarkedBands(side))
            {
                var g = HarmonicaDataSet.GammaOf(terminations.Z(side, band), s0.Z0);
                currentGammas[(side, band)] = g;
                double d = _lastTerminationGammas.TryGetValue((side, band), out var prior)
                    ? (g - prior).Magnitude
                    : double.PositiveInfinity; // a just-marked band has no prior frame to compare to
                if (d > maxDeltaGamma) maxDeltaGamma = d;
            }
        bool readLever1 = maxDeltaGamma < LeverOneDeltaGammaThreshold;
        if (!readLever1) Lever1DisabledCount++;
        _lastTerminationGammas = currentGammas;

        var sweep = PinSearch.Sweep(ctx, terminations, s0.PinStartDbm, s0.PinMaxDbm, s0.PinStepDbm,
                                    priorLevelSpectra: readLever1 ? _lastSweepLevelSpectra : null);
        LastSolveCount += sweep.Solves;
        _lastSweepLevelSpectra = sweep.Steps.Count > 0
            ? sweep.Steps.ToDictionary(st => Math.Round(st.PavlDbm, 6), st => st.Point.V)
            : _lastSweepLevelSpectra;
        tierAMs = stage.Elapsed.TotalMilliseconds;

        // The operating point the glyphs, the loadline and the readouts are all evaluated at: R-h6-11's
        // cursor — the compression point when the user has not placed it, else the nearest step to
        // where they put it. R-h9r2-17a: sweep.AtCompression already IS the right spectrum source
        // (the nearest solved ladder point, or ExactCompressionSolve's one real extra solve) — read
        // directly rather than round-tripping through Steps by index.
        var at = opt.AtPavlDbm is { } placed
            ? (IndexOfNearestPin(sweep.Steps, placed) is var pidx && pidx >= 0 ? sweep.Steps[pidx] : null)
            : sweep.AtCompression ?? (sweep.Steps.Count > 0 ? sweep.Steps[^1] : null);

        int cursor = at is null ? -1 : IndexOfNearestPin(sweep.Steps, at.PavlDbm);

        // ── the intrinsic plane, read from the published DataSet ──────────────
        //
        // R-h7-6 — the DataSet the trace picker sees must be the one the panels drew from. It is
        // built HERE, at the frame's own operating point (R-h6-11's cursor), and carried on the
        // frame; a picker that re-solved to populate itself would show a different operating point
        // from the glyphs beside it. This is the same thread-crossing rule H6 used for the
        // inverse-solve outcome — the value crosses on the frame, not through a shared field.
        DataSet? published = null;
        if (at is not null)
        {
            published = HarmonicaDataSet.Build(ctx, at.Point, terminations);
            ApplyIntrinsicGlyphs(published, markers);
        }

        // ── tier C: the DCIV family, computed once and held ───────────────────
        // The cache is guarded because ONE solver is shared across every pool worker, deliberately:
        // tier C depends only on the model, so computing it once per WORKER would be N times the
        // work for the same answer and would break Tier 6's "computed once across a drag".
        var dcivKey = DcivFamily.ResolvedKey(ctx.Model);
        lock (_dcivGate)
        {
            if (_dcivKey != dcivKey)
            {
                _dciv    = DcivFamily.Compute(ctx, dcivKey);
                _dcivKey = dcivKey;
                DcivComputeCount++;
            }
        }

        // ── the panels ────────────────────────────────────────────────────────
        var loadline = BuildLoadline(ctx, at, opt.IntrinsicPlane);
        var power    = BuildPowerSweep(sweep, cursor, opt.EfficiencyMetric, s0.PinStartDbm, s0.PinMaxDbm);

        // R-h9b-4 — both rows are built HERE, in one place, so the two charts cannot disagree about
        // how the compression setting, the metric or Z0 is spelled. Live even on a SkipContours
        // (tier-A-only) frame: the titles describe the SETTINGS, not the grid.
        double z0 = ctx.Model.Settings.Z0, compressionDb = ctx.Model.Settings.CompressionDb;
        string planeRow = HarmonicaTitles.PlaneRow(opt.GridSide, opt.GridHarmonic, z0);
        string titleP = HarmonicaTitles.MetricRow(isPowerChart: true,  opt.EfficiencyMetric, compressionDb);
        string titleE = HarmonicaTitles.MetricRow(isPowerChart: false, opt.EfficiencyMetric, compressionDb);

        SmithPanelData smithP = new() { Title = titleP, Subtitle = planeRow, Markers = markers, Z0 = z0 };
        SmithPanelData smithE = new() { Title = titleE, Subtitle = planeRow, Markers = markers, Z0 = z0 };

        if (!opt.SkipContours)
        {
            grid ??= new ContourGrid();
            stage.Restart();
            grid.Build(ctx, terminations.Clone(),
                       opt.GammaGrid ?? ContourGrid.RingGrid(opt.Rings, opt.Spokes, opt.MaxGamma),
                       opt.GridSide, opt.GridHarmonic,
                       ct: ct, reuseUnchanged: opt.ReuseUnchangedGridPoints, onProgress: onGridProgress);
            LastSolveCount += grid.SolveCount;
            LastGridPointsReused = grid.ReusedPointCount;
            gridSolveMs = stage.Elapsed.TotalMilliseconds;

            smithP = BuildSmith(titleP, planeRow, grid, GridMetric.PoutDbm,   markers, opt, z0, ref fitMs, ref rasterMs);
            smithE = BuildSmith(titleE, planeRow, grid, opt.EfficiencyMetric, markers, opt, z0, ref fitMs, ref rasterMs);

            // R-h9r2-1 — stamp what this layer was actually solved for, so a LATER grid-less frame
            // knows whether it may carry this one forward.
            smithP = smithP with { ContourGridSide = opt.GridSide, ContourGridHarmonic = opt.GridHarmonic };
            smithE = smithE with { ContourGridSide = opt.GridSide, ContourGridHarmonic = opt.GridHarmonic };

            // R-h9b-16 — the FOMs at each optimum come from ONE SOLVE there, never from N separately
            // interpolated surfaces. Not while dragging (§6.8's whole reason to have a ladder) — only
            // on a full-quality frame, so a coarse/frozen rung carries the glyph's POSITION (already
            // set above, cheap) but not stale or fabricated numbers.
            if (opt.Quality == FrameQuality.Full)
            {
                if (smithP.Optimum is { } sp)
                    smithP = smithP with { Optimum = SolveAtOptimum(ctx, terminations, opt, sp) };
                if (smithE.Optimum is { } se)
                    smithE = smithE with { Optimum = SolveAtOptimum(ctx, terminations, opt, se) };
            }
        }
        else
        {
            // R-h9r2-1 — a grid-less frame (the FrozenContours rung, a drag that now always skips the
            // grid, or an on-release skip-because-swept-band frame) carries its PREDECESSOR's contour
            // layer forward rather than publishing empty lists. "FrozenContours means the previous
            // frame's are ghosted" was the design note's own claim; before this, nothing ghosted them
            // — they vanished, because Contours/GridPoints/Optimum were only ever filled inside the
            // block above. One carry-forward path serves every grid-less rung, whatever produced it.
            smithP = CarryForwardContourLayer(smithP, previousPower, opt);
            smithE = CarryForwardContourLayer(smithE, previousEfficiency, opt);
        }

        if (opt.Reachable is not null)
        {
            smithP = smithP with { Reachable = opt.Reachable };
            smithE = smithE with { Reachable = opt.Reachable };
        }

        return new HarmonicaFrame
        {
            SmithPower      = smithP,
            SmithEfficiency = smithE,
            Loadline        = loadline,
            PowerSweep      = power,
            Markers         = markers,
            Readouts        = BuildReadouts(ctx, sweep, at, markers, opt, smithP, smithE, readoutFormat),
            Published       = published,
            Quality         = opt.Quality,
            // RenderMs is deliberately left at zero: the solver cannot know it. The view fills it in
            // from its own last draw before handing the whole thing to FrameScheduler.RecordFrame.
            Timing          = new FrameTiming(tierAMs, gridSolveMs, fitMs, rasterMs, 0),
        };
    }

    /// <summary>
    /// R-h9r2-1 — carries a PREDECESSOR panel's contour layer onto <paramref name="current"/>, which
    /// already carries this frame's own live Title/Subtitle/Markers (built above, before this is
    /// called) and nothing else. <b>Refused, not defaulted, when the identity does not match</b> —
    /// compared on <see cref="SmithPanelData.ContourGridSide"/>/<see cref="SmithPanelData.
    /// ContourGridHarmonic"/> against THIS frame's own <c>opt.GridSide</c>/<c>GridHarmonic</c>, which
    /// are the two fields <see cref="ContourGrid"/> itself keys reuse on (§6.5's document-wide plane
    /// and harmonic selectors). A predecessor with no layer at all (never solved, e.g. the very first
    /// frame of a session) has <c>ContourGridSide == null</c>, which can never equal a real
    /// <see cref="TerminationSide"/> — so it never carries, and the panel is published empty exactly
    /// as before this brief.
    ///
    /// <para><b>The whole layer or none of it</b> — Contours, Levels, GridPoints and Optimum (plus
    /// Mxp/Mxe, the plain grid-sample argmax those numbers are drawn from) move together. Contours
    /// without their own grid points, or an optimum whose surface is gone, is the half-updated
    /// picture report (3) is about.</para>
    /// </summary>
    private static SmithPanelData CarryForwardContourLayer(
        SmithPanelData current, SmithPanelData? previous, Options opt)
    {
        if (previous is null) return current;
        if (previous.ContourGridSide != opt.GridSide || previous.ContourGridHarmonic != opt.GridHarmonic)
            return current;

        return current with
        {
            Contours            = previous.Contours,
            Levels               = previous.Levels,
            GridPoints           = previous.GridPoints,
            Optimum              = previous.Optimum,
            Mxp                  = previous.Mxp,
            Mxe                  = previous.Mxe,
            ContourGridSide      = previous.ContourGridSide,
            ContourGridHarmonic  = previous.ContourGridHarmonic,
        };
    }

    // ── the Smith panels ─────────────────────────────────────────────────────

    private static SmithPanelData BuildSmith(string title, string subtitle, ContourGrid grid, GridMetric metric,
                                             IReadOnlyList<HarmonicaMarker> markers, Options opt, double z0,
                                             ref double fitMs, ref double rasterMs)
    {
        // D6's split, taken where it actually happens: Fit is the RBF back-solve (the factorization is
        // cached across frames), Raster is the per-cell evaluate plus the support-mask test plus
        // marching squares. The raster is ~76% of the pair — measured, see src/Harmonica/CLAUDE.md.
        // Calling Fit explicitly first is what makes them separable at all: ContourGrid.Raster would
        // otherwise fit lazily inside the block being attributed to the raster.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        grid.Fit(metric);
        fitMs += sw.Elapsed.TotalMilliseconds;

        // Raster once and derive both the levels and the polylines from it, rather than calling
        // ContourGrid.Contours (which rasters again) — paying it twice per panel would be the single
        // most expensive avoidable thing in a frame.
        sw.Restart();
        var raster = grid.Raster(metric, opt.RasterResolution);
        var levels = ContourExtractor.LevelsBetween(raster, opt.Levels);
        var polys  = ContourExtractor.Extract(raster, levels);
        rasterMs += sw.Elapsed.TotalMilliseconds;

        // R-h9b-15 — seeded from the SAME raster (no second one), refined on the SAME fit the
        // contours were drawn from. Cheap: no HB solve, just Rbf2D.Evaluate over a small window.
        var interp = grid.InterpolatedArgmax(metric, raster);
        var optimum = interp is { } ie ? new SmithPanelData.SmithOptimum(ie.Gamma, ie.Value, null, null) : null;

        return new SmithPanelData
        {
            Title      = title,
            Subtitle   = subtitle,
            Contours   = polys,
            Levels     = [.. levels.Levels.OrderBy(v => v)],
            GridPoints = [.. grid.Points.Select(p => new HarmonicaGridPoint(p.Gamma, p.IsHole))],
            Markers    = markers,
            Mxp        = grid.Mxp?.Point.Gamma,
            Mxe        = grid.Mxe?.Point.Gamma,
            Optimum    = optimum,
            Z0         = z0,
        };
    }

    // ── §7.3 — the loadline panel ────────────────────────────────────────────

    private LoadlinePanelData BuildLoadline(HarmonicaContext ctx, PinStep? at, bool intrinsic)
    {
        double[] vds = [], ids = [];
        // R-h8-3 — an intrinsic plane nobody has located draws NO loadline. Empty is honest; a curve
        // read at a guessed port would look exactly like a real measurement.
        if (at is not null && ctx.IntrinsicPorts.LoadAvailable)
        {
            var (v, i) = IntrinsicPlane.Loadline(
                ctx.DutComponent, at.Point.V, ctx.Interface.DeviceNodes,
                ctx.Model.Settings.HarmonicCount,
                ctx.Model.Settings.LoadlineSamples,
                ctx.IntrinsicPorts.DrainPort, ctx.IntrinsicPorts.SourcePort);

            // Closed over one RF cycle: the last sample repeats the first, so the locus reads as a
            // loop rather than a curve with two loose ends.
            vds = [.. v, v.Length > 0 ? v[0] : 0.0];
            ids = [.. i, i.Length > 0 ? i[0] : 0.0];
        }

        return new LoadlinePanelData
        {
            Dciv = [.. _dciv.Select(c => new LoadlinePanelData.Curve(c.Vgs, c.Vds, c.Ids))],
            LoadlineVds = vds,
            LoadlineIds = ids,
            Intrinsic   = intrinsic,
        };
    }

    // ── §7.4 — the power-sweep panel ─────────────────────────────────────────

    private static PowerSweepPanelData BuildPowerSweep(PinSearchResult sweep, int cursor,
                                                        GridMetric efficiencyMetric,
                                                        double pinStartDbm, double pinMaxDbm)
    {
        // The steps come back in the order they were SOLVED — a doubling bracket then a secant — so
        // they are not monotone in Pin. A plot of them unsorted would zig-zag back on itself.
        var ordered = sweep.Steps.OrderBy(s => s.PavlDbm).ToList();
        int orderedCursor = cursor >= 0 && cursor < sweep.Steps.Count
            ? ordered.IndexOf(sweep.Steps[cursor])
            : -1;

        // R-h9b-8 — the right axis follows the SAME DE/PAE setting its label now names; a "PAE (%)"
        // label over drain-efficiency values would be a wrong number under a right label, which is
        // worse than the wrong label this section was written to fix.
        bool pae = efficiencyMetric == GridMetric.Pae;

        return new PowerSweepPanelData
        {
            PinAvailDbm   = [.. ordered.Select(s => s.PavlDbm)],
            PoutDbm       = [.. ordered.Select(s => s.PoutW > 0 ? 10 * Math.Log10(s.PoutW) + 30 : double.NaN)],
            GainDb        = [.. ordered.Select(s => s.GainDb)],
            EfficiencyPct = [.. ordered.Select(s => (pae ? s.Pae : s.De) * 100.0)],
            CursorIndex   = orderedCursor,
            ReachedCompression = sweep.Compressed,
            EfficiencyMetric = efficiencyMetric,
            PinStartDbm = pinStartDbm,
            PinMaxDbm   = pinMaxDbm,
        };
    }

    /// <summary>
    /// R-h9b-16 — solves ONE Pin drive-up at <paramref name="seed"/>'s interpolated Γ, substituted
    /// into the band the contour grid swept (<c>opt.GridSide</c>/<c>GridHarmonic</c>), and publishes
    /// §5's cubes there. This is the single defensible route: every number the caller reads off the
    /// result — Pout, DE, PAE, Gain, Zin, AM-PM — is then the SAME state, consistently, rather than
    /// values interpolated off N independently-fitted surfaces that need not even satisfy the DE that
    /// a third surface reports.
    /// </summary>
    private SmithPanelData.SmithOptimum SolveAtOptimum(
        HarmonicaContext ctx, TerminationSet terminations, Options opt, SmithPanelData.SmithOptimum seed)
    {
        var t = terminations.Clone();
        t.Set(opt.GridSide, opt.GridHarmonic, HarmonicaDataSet.ImpedanceOf(seed.Gamma, ctx.Model.Settings.Z0));

        var sweep = PinSearch.Run(ctx, t);
        LastSolveCount += sweep.Solves;

        int idx = sweep.AtCompression is null
            ? sweep.Steps.Count - 1
            : IndexOfNearestPin(sweep.Steps, sweep.AtCompression.PavlDbm);
        var step = idx >= 0 && idx < sweep.Steps.Count ? sweep.Steps[idx] : null;

        var published = step is not null ? HarmonicaDataSet.Build(ctx, step.Point, t) : null;
        return seed with { Solved = step, Published = published };
    }

    private static int IndexOfNearestPin(IReadOnlyList<PinStep> steps, double pavlDbm)
    {
        int best = -1; double bestD = double.MaxValue;
        for (int i = 0; i < steps.Count; i++)
        {
            double d = Math.Abs(steps[i].PavlDbm - pavlDbm);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // ── §4.5 — the glyphs, READ from Gamma_intr ──────────────────────────────

    /// <summary>
    /// Stamps each marker's intrinsic Γ from the published <c>Gamma_intr</c> cube. <b>Nothing here
    /// derives it</b> — §4.5.2's warning is that a voltage-over-current ratio at the gate returns the
    /// LOAD, and this codebase has already shipped that error once. The cube is built by
    /// <c>IntrinsicPlane</c>, which takes the §4.5.3 <c>J′</c> route on the source side.
    /// </summary>
    private static void ApplyIntrinsicGlyphs(DataSet ds, IReadOnlyList<HarmonicaMarker> markers)
    {
        if (!ds.Contains("Gamma_intr")) return;
        var cube = ds["Gamma_intr"];                       // [side, harmonic]
        if (cube.Rank != 2 || cube.DataKind != DataKind.Complex) return;
        var z = cube.ComplexValues;

        int sides = cube.Axes[0].Values.Length;
        int bands = cube.Axes[1].Values.Length;

        foreach (var m in markers)
        {
            int s = m.Side == TerminationSideKind.Source ? 0 : 1;
            if (s >= sides || m.Band < 0 || m.Band >= bands) continue;
            m.GammaIntrinsic = z[s * bands + m.Band];
        }
    }

    // ── §7.5 — the readouts (R-h9c-9, R1C §5) ────────────────────────────────

    /// <summary>
    /// Builds §7.5's four-column readout set. <b>R-h9c-5:</b> <c>compr</c>/<c>stop</c>/<c>K</c>/
    /// <c>solves</c>/<c>Gss</c> are GONE from here — <c>compr</c> and <c>K</c> survive as INPUTS
    /// (<see cref="HarmonicaInputs"/>), which is a different list; the other three had no input
    /// twin and are simply removed, per the owner's own reasoning for each.
    /// </summary>
    private static IReadOnlyList<HarmonicaReadout> BuildReadouts(
        HarmonicaContext ctx, PinSearchResult sweep, PinStep? at,
        IReadOnlyList<HarmonicaMarker> markers, Options opt,
        SmithPanelData smithP, SmithPanelData smithE,
        Func<string, ReadoutFormat>? readoutFormat)
    {
        var format = readoutFormat ?? (_ => ReadoutFormat.RealImaginary);
        double z0  = ctx.Model.Settings.Z0;
        var r = new List<HarmonicaReadout>();

        void Add(string label, string value, string tip) => r.Add(new(label, value, tip, ReadoutColumn.General));

        // ── R3C §3 — f₀, Vds, Vgs are GONE from here. They were duplicated against the editable
        // Settings-column inputs (HarmonicaInputs.KeyFrequency/KeyVds/KeyVgs), which are strictly more
        // capable (editable) and, after R3C §1, render as text anyway — so the input is what survives.
        // The one thing the removed Vgs row said that the input alone could not ("(from Idq)" when the
        // bias is current-driven) is now carried by the input itself: see HarmonicaInputs.Build's own
        // Placeholder for KeyVgs.

        // ── R3C §2 — the operating-point column: Pin/Pout/Gain/DE/PAE/Pdc, headed with the SAME
        // compression-label vocabulary the Smith titles use (HarmonicaTitles), so this column and the
        // two charts can never disagree about how the compression target is spelled — never composed
        // as a new string here.
        if (at is not null)
        {
            // R-h9r2-17a — a sweep's SweepCompression carries the interpolated (or, with
            // ExactCompressionSolve on, the one-real-solve) figures AT the compression target; `at`
            // itself is only the nearest solved ladder point's SPECTRUM. Reading `at`'s own numbers
            // here would round every figure to the nearest whole dB step — precisely the error
            // interpolation exists to remove. Null for a Run() result (its own AtCompression already
            // sits exactly on target), so the fallback is exactly the old behaviour there.
            var sc = sweep.SweepCompression;
            double pinDbm  = sc?.PinDbm  ?? at.PavlDbm;
            double poutDbm = sc?.PoutDbm ?? (at.PoutW > 0 ? 10 * Math.Log10(at.PoutW) + 30 : double.NaN);
            double gainDb  = sc?.GainDb  ?? at.GainDb;
            double deFrac  = sc?.De      ?? at.De;
            double paeFrac = sc?.Pae     ?? at.Pae;
            double pdcW    = sc?.PdcW    ?? at.PdcW;

            string opHeader = HarmonicaTitles.CompressionLabel(ctx.Model.Settings.CompressionDb);
            r.Add(new HarmonicaReadout(opHeader, "", "", ReadoutColumn.OperatingPoint));

            void AddOp(string label, string value, string tip)
                => r.Add(new HarmonicaReadout(label, value, tip, ReadoutColumn.OperatingPoint));

            AddOp("Pin", $"{pinDbm:0.##} dBm", "Available power at the operating point.");
            AddOp("Pout", double.IsNaN(poutDbm) ? "—" : $"{poutDbm:0.##} dBm",
                "Output power at the operating point.");
            AddOp("Gain", $"{gainDb:0.##} dB", "Transducer gain (Gt) — D9's default criterion.");
            AddOp("DE",  $"{deFrac * 100:0.#} %",  "Drain efficiency, Pout / Pdc.");
            AddOp("PAE", $"{paeFrac * 100:0.#} %", "Power-added efficiency, (Pout − Pin_delivered) / Pdc.");
            AddOp("Pdc", $"{pdcW:0.###} W", "DC power drawn at the operating point.");
        }

        // R-h9r2-24 — the per-marker INTRINSIC Γ rows that used to sit on this General line are GONE,
        // per the owner's own literal request ("redundant because there is a Load column of data
        // showing the same thing below"). Chosen disposition: REMOVED outright rather than relocated
        // into the Source/Load columns — the simplest of the two options the brief allows, and the
        // owner's own words ask for removal, not a move. The quantity is not lost: it is still the
        // glyph on the chart (§4.5's whole intrinsic-plane machinery), and "intrinsic: not located"
        // below still reports the one case (a FAILURE to locate the plane) that is not redundant with
        // anything on screen.

        // R-h8-3 — "empty" and "broken" look identical on a panel, so the strip SAYS which it is. A
        // located plane adds no row at all: a permanent "intrinsic plane: fine" is noise.
        if (ctx.IntrinsicPorts.Reason is { Length: > 0 } why)
            Add("intrinsic", "not located", why);

        // ── Source / Load — R-h9c-6's editable termination columns ─────────────
        // Left to right: Source · Load · MXP · MXE — the editable termination columns land nearest
        // the Smith charts they belong to (§7.1's charts sit above-left); the read-only performance
        // summaries follow. The owner fixes MXP left of MXE and Source left of Load; this is the
        // remaining choice the brief leaves open.
        foreach (var side in new[] { TerminationSideKind.Source, TerminationSideKind.Load })
        {
            var column = side == TerminationSideKind.Source ? ReadoutColumn.Source : ReadoutColumn.Load;
            string sideLetter = side == TerminationSideKind.Source ? "S" : "L";

            // A plain title row, the same shape MXP/MXE's own header uses (label only, empty value) —
            // one rendering path for every column's header rather than two.
            r.Add(new HarmonicaReadout(sideLetter == "S" ? "Source" : "Load", "", "", column));

            foreach (var m in markers.Where(m => m.Side == side).OrderBy(m => m.Band))
            {
                var z = HarmonicaDataSet.ImpedanceOf(m.Gamma, z0);

                // R-h9r2-25 — RawValue carried alongside the solve-time-formatted Value, so a later
                // right-click format change repaints this row without a re-solve (ReadoutStripView
                // reads RawValue through the CURRENT format at render time; Value is the fallback for
                // a row with none).
                r.Add(new HarmonicaReadout(
                    $"Z{m.Name}", HarmonicaReadoutFormatting.FormatZ(z, format($"{sideLetter}{m.Band}.Z")),
                    $"{m.Name}'s termination, as an impedance against Z0={FormatZ0(z0)} Ω. " +
                    "Double-click to edit; right-click to switch real/imaginary ⇄ magnitude/angle.",
                    column, IsComplex: true, Editable: true, Side: side, Band: m.Band, IsGamma: false,
                    RawValue: z));

                r.Add(new HarmonicaReadout(
                    $"Γ{m.Name}", HarmonicaReadoutFormatting.FormatGamma(m.Gamma, format($"{sideLetter}{m.Band}.Gamma")),
                    $"{m.Name}'s termination, as Γ against Z0={FormatZ0(z0)} Ω. " +
                    "Double-click to edit; right-click to switch real/imaginary ⇄ magnitude/angle.",
                    column, IsComplex: true, Editable: true, Side: side, Band: m.Band, IsGamma: true,
                    RawValue: m.Gamma));
            }
        }

        // ── MXP / MXE — R-h9c-6's read-only performance summaries ──────────────
        AddMxColumn(r, ReadoutColumn.Mxp, "MXP", opt, smithP.Optimum, format);
        AddMxColumn(r, ReadoutColumn.Mxe, "MXE", opt, smithE.Optimum, format);

        return r;
    }

    /// <summary>
    /// One MXP/MXE column. <b>Reads 1B's already-solved record; computes nothing.</b> R-h9b-17's own
    /// invariant is what makes that safe: the glyph on the chart and this column's numbers come from
    /// the identical <see cref="SmithPanelData.SmithOptimum"/>, so they can never describe two
    /// different states.
    /// </summary>
    private static void AddMxColumn(List<HarmonicaReadout> r, ReadoutColumn column, string label,
                                    Options opt, SmithPanelData.SmithOptimum? optimum,
                                    Func<string, ReadoutFormat> format)
    {
        string header = HarmonicaTitles.MxHeaderRow(label, opt.GridSide, opt.GridHarmonic);

        // R-h9b-15/16/17 — "no optimum" covers all three states 1B names: every grid point a hole
        // (Optimum itself null), and a degraded ladder rung or a SkipContours frame (Optimum's
        // position is cheap and updates every frame, but Solved/Published — the figures of merit —
        // are only ever set on a full-quality frame with a real solve there).
        if (optimum is not { Solved: { } step, Published: { } ds })
        {
            r.Add(new HarmonicaReadout(header, "no optimum",
                "No optimum is available this frame — every grid point is a hole, or this is a " +
                "degraded/dragging frame with no fresh solve at the optimum yet.", column));
            return;
        }

        r.Add(new HarmonicaReadout(header, "", "", column));
        r.Add(new HarmonicaReadout("Pout",
            step.PoutW > 0 ? $"{10 * Math.Log10(step.PoutW) + 30:0.##} dBm" : "—",
            "Output power at this optimum.", column));
        r.Add(new HarmonicaReadout("Efficiency", $"{step.De * 100:0.#} %",
            "Drain efficiency at this optimum.", column));
        r.Add(new HarmonicaReadout("PAE", $"{step.Pae * 100:0.#} %",
            "Power-added efficiency at this optimum.", column));
        r.Add(new HarmonicaReadout("Gain", $"{step.GainDb:0.##} dB",
            "Transducer gain (Gt) at this optimum.", column));
        r.Add(new HarmonicaReadout("Gp", $"{step.Foms.GpDb:0.##} dB",
            "Power gain (Gp = Pout / Pin_delivered) at this optimum — free off the solved PinStep's " +
            "own FomResult, the same one Gain (Gt) reads.", column));

        var zin = ReadComplex(ds, "Zin", (int)TerminationSide.Source, 1);
        r.Add(new HarmonicaReadout("Zin", HarmonicaReadoutFormatting.FormatZ(zin, format($"{label}.Zin")),
            "Impedance looking into the DUT at the extrinsic plane, fundamental (§4.5.4) — the true " +
            "delivered current, not the device's own intrinsic gate current. Moves with the load on " +
            "a non-unilateral device, which is why it is read at THIS optimum's own termination " +
            "rather than published as one document-wide number.",
            column, IsComplex: true, RawValue: zin));

        var vOutFund = ReadComplex(ds, "V_ext", (int)TerminationSide.Load, 1);
        string amPm = double.IsNaN(vOutFund.Real) ? "—" : $"{vOutFund.Phase * 180.0 / Math.PI:0.#}°";
        r.Add(new HarmonicaReadout("AM/PM", amPm,
            "The fundamental output's phase relative to the drive, at this optimum. The drive's own " +
            "phase reference is 0 by construction (HarmonicaContext.DriveVolts is a real Thévenin " +
            "amplitude), so this is the RAW phase of V_ext at the load plane, fundamental — not a " +
            "deg/dB AM-to-PM slope, which nothing published here carries the multi-point sweep for.",
            column));
    }

    /// <summary>Reads one <c>[side, harmonic]</c> entry of a published complex cube. NaN when the
    /// cube is absent or the indices are out of range — never a substituted zero.</summary>
    private static Complex ReadComplex(DataSet ds, string cubeName, int sideIndex, int harmonic)
    {
        if (!ds.Contains(cubeName)) return new Complex(double.NaN, double.NaN);
        var cube = ds[cubeName];
        if (cube.Rank != 2 || cube.DataKind != DataKind.Complex) return new Complex(double.NaN, double.NaN);

        int harmonics = cube.Axes[1].Values.Length;
        if (sideIndex < 0 || sideIndex >= cube.Axes[0].Values.Length || harmonic < 0 || harmonic >= harmonics)
            return new Complex(double.NaN, double.NaN);
        return cube.ComplexValues[sideIndex * harmonics + harmonic];
    }

    private static string FormatZ0(double z0)
        => z0 == Math.Floor(z0) ? ((long)z0).ToString() : z0.ToString("0.##");
}
