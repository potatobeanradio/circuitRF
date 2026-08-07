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
    public HarmonicaFrame Solve(HarmonicaContext ctx, TerminationSet terminations,
                                IReadOnlyList<HarmonicaMarker> markers, Options? options = null,
                                ContourGrid? grid = null, CancellationToken ct = default)
    {
        var opt = options ?? new Options();
        LastSolveCount = 0;

        // D6 / R-h6-4 — the stages are timed APART. A scheduler that lumps fit and solve together
        // cannot tell "the solver is slow" from "the fit is slow" and will degrade the wrong one.
        var stage = System.Diagnostics.Stopwatch.StartNew();
        double tierAMs, gridSolveMs = 0, fitMs = 0, rasterMs = 0;

        // ── tier A: one Pin drive-up at the current terminations ──────────────
        ct.ThrowIfCancellationRequested();
        var sweep = PinSearch.Run(ctx, terminations);
        LastSolveCount += sweep.Solves;
        tierAMs = stage.Elapsed.TotalMilliseconds;

        // The operating point the glyphs, the loadline and the readouts are all evaluated at: R-h6-11's
        // cursor — the compression point when the user has not placed it, else the nearest step to
        // where they put it.
        int cursor = opt.AtPavlDbm is { } placed
            ? IndexOfNearestPin(sweep.Steps, placed)
            : sweep.AtCompression is null
                ? sweep.Steps.Count - 1
                : IndexOfNearestPin(sweep.Steps, sweep.AtCompression.PavlDbm);

        var at = cursor >= 0 && cursor < sweep.Steps.Count ? sweep.Steps[cursor] : null;

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
        var dcivKey = DcivFamily.DefaultKey(ctx.Model);
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
        var power    = BuildPowerSweep(sweep, cursor);

        SmithPanelData smithP = new() { Title = "Power",      Markers = markers };
        SmithPanelData smithE = new() { Title = "Efficiency", Markers = markers };

        if (!opt.SkipContours)
        {
            grid ??= new ContourGrid();
            stage.Restart();
            grid.Build(ctx, terminations.Clone(),
                       opt.GammaGrid ?? ContourGrid.RingGrid(opt.Rings, opt.Spokes, opt.MaxGamma),
                       opt.GridSide, opt.GridHarmonic,
                       ct: ct, reuseUnchanged: opt.ReuseUnchangedGridPoints);
            LastSolveCount += grid.SolveCount;
            LastGridPointsReused = grid.ReusedPointCount;
            gridSolveMs = stage.Elapsed.TotalMilliseconds;

            smithP = BuildSmith("Power",      grid, GridMetric.PoutDbm,   markers, opt, ref fitMs, ref rasterMs);
            smithE = BuildSmith("Efficiency", grid, opt.EfficiencyMetric, markers, opt, ref fitMs, ref rasterMs);
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
            Readouts        = BuildReadouts(ctx, sweep, at, markers),
            Published       = published,
            Quality         = opt.Quality,
            // RenderMs is deliberately left at zero: the solver cannot know it. The view fills it in
            // from its own last draw before handing the whole thing to FrameScheduler.RecordFrame.
            Timing          = new FrameTiming(tierAMs, gridSolveMs, fitMs, rasterMs, 0),
        };
    }

    // ── the Smith panels ─────────────────────────────────────────────────────

    private static SmithPanelData BuildSmith(string title, ContourGrid grid, GridMetric metric,
                                             IReadOnlyList<HarmonicaMarker> markers, Options opt,
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

        return new SmithPanelData
        {
            Title      = title,
            Contours   = polys,
            Levels     = [.. levels.Levels.OrderBy(v => v)],
            GridPoints = [.. grid.Points.Select(p => new HarmonicaGridPoint(p.Gamma, p.IsHole))],
            Markers    = markers,
            Mxp        = grid.Mxp?.Point.Gamma,
            Mxe        = grid.Mxe?.Point.Gamma,
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
                CircuitRF.Engine.HarmonicBalance.HbFft.GridSize(
                    ctx.Model.Settings.HarmonicCount, ctx.Model.Settings.FftOverSample),
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

    private static PowerSweepPanelData BuildPowerSweep(PinSearchResult sweep, int cursor)
    {
        // The steps come back in the order they were SOLVED — a doubling bracket then a secant — so
        // they are not monotone in Pin. A plot of them unsorted would zig-zag back on itself.
        var ordered = sweep.Steps.OrderBy(s => s.PavlDbm).ToList();
        int orderedCursor = cursor >= 0 && cursor < sweep.Steps.Count
            ? ordered.IndexOf(sweep.Steps[cursor])
            : -1;

        return new PowerSweepPanelData
        {
            PinAvailDbm   = [.. ordered.Select(s => s.PavlDbm)],
            PoutDbm       = [.. ordered.Select(s => s.PoutW > 0 ? 10 * Math.Log10(s.PoutW) + 30 : double.NaN)],
            GainDb        = [.. ordered.Select(s => s.GainDb)],
            EfficiencyPct = [.. ordered.Select(s => s.De * 100.0)],
            CursorIndex   = orderedCursor,
            ReachedCompression = sweep.Compressed,
        };
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

    // ── §7.5 — the readouts ──────────────────────────────────────────────────

    private static IReadOnlyList<(string, string, string)> BuildReadouts(
        HarmonicaContext ctx, PinSearchResult sweep, PinStep? at,
        IReadOnlyList<HarmonicaMarker> markers)
    {
        var r = new List<(string, string, string)>();

        void Add(string label, string value, string tip) => r.Add((label, value, tip));

        Add("f₀", $"{ctx.Model.Settings.FrequencyHz / 1e9:0.###} GHz", "Fundamental drive frequency.");
        Add("K",  ctx.Model.Settings.HarmonicCount.ToString(),
            "Harmonic order — how many bands the HB solve carries.");
        Add("Vds", $"{ctx.Model.Bias.Vds:0.##} V", "Drain supply.");
        Add("Vgs", ctx.Model.Bias.Vgs is double vg ? $"{vg:0.###} V" : "(from Idq)",
            "Gate bias. Idq solves Vgs by a 1-D secant on the DC solve.");

        Add("Gss", $"{sweep.SmallSignalGainDb:0.##} dB",
            "Small-signal gain from the tickle. Termination-dependent, so it is re-measured at every Γ.");
        Add("stop", sweep.Reason.ToString(),
            "Why the Pin search stopped. Anything but Compression means this point is a hole.");

        if (at is not null)
        {
            Add("Pin", $"{at.PavlDbm:0.##} dBm", "Available power at the operating point.");
            Add("Pout", at.PoutW > 0 ? $"{10 * Math.Log10(at.PoutW) + 30:0.##} dBm" : "—",
                "Output power at the operating point.");
            Add("Gain", $"{at.GainDb:0.##} dB", "Transducer gain (Gt) — D9's default criterion.");
            Add("compr", $"{at.Compression:0.##} dB", "Gmax − G(Pin).");
            Add("DE",  $"{at.De * 100:0.#} %",  "Drain efficiency, Pout / Pdc.");
            Add("PAE", $"{at.Pae * 100:0.#} %", "Power-added efficiency, (Pout − Pin_delivered) / Pdc.");
            Add("Pdc", $"{at.PdcW:0.###} W", "DC power drawn at the operating point.");
        }

        foreach (var m in markers)
        {
            Add($"{m.Name} Γ", Fmt(m.Gamma), $"{m.Name}'s extrinsic termination, as Γ against 50 Ω.");
            Add($"{m.Name} Γᵢ", Fmt(m.GammaIntrinsic)
                    + (m.IntrinsicIsOutsideUnitCircle ? "  (|Γ|>1)" : ""),
                $"{m.Name}'s INTRINSIC reflection (§4.5). |Γ| > 1 is ordinary with conduction-only " +
                "current, not an error — the glyph is drawn outside the chart on a compressed scale.");
        }

        Add("solves", sweep.Solves.ToString(),
            "HB solves this drive-up cost, tickle included.");

        // R-h8-3 — "empty" and "broken" look identical on a panel, so the strip SAYS which it is. A
        // located plane adds no row at all: a permanent "intrinsic plane: fine" is noise.
        if (ctx.IntrinsicPorts.Reason is { Length: > 0 } why)
            Add("intrinsic", "not located", why);

        return r;
    }

    private static string Fmt(Complex z)
        => double.IsNaN(z.Real) || double.IsNaN(z.Imaginary)
            ? "—"
            : $"{z.Magnitude:0.###}∠{z.Phase * 180.0 / Math.PI:0.#}°";
}
