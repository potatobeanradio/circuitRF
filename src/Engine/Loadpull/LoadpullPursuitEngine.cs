using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using RfCore;

namespace CircuitRF.Engine.Loadpull;

/// <summary>
/// Phase 4b-2: orchestrates MXP + MXE steepest-ascent searches and auto-Zsource extraction.
///
/// Wraps the 4b-1 LoadpullEngine.  Each search "query" is one RunOneTermination call
/// (one adaptive Pin drive-to-compression run).  loadpull_pursuit.md §1, §4, §6.
///
/// Data sharing (§4):
///   - Every query result is cached by termination (VSWR-dedup).
///   - MXP runs first; MXE reads efficiency from the same cached sweeps.
///   - MXE is seeded from the highest-efficiency cached point (naturally ~2-2.5 VSWR from MXP
///     for a stable FET — the Pedro coupling).
///
/// Auto-Zsource (§6):
///   Zin = V[srcIfIdx, k=1] / (-INl[srcIfIdx, k=1]) at the OBO drive level.
///   Zsource = conj(Zin).  Computed once per optimum from the cached inner sweep.
///   OBO step: linearly interpolated from the cached Pin sweep (granularity = PinStep).
/// </summary>
public sealed class LoadpullPursuitEngine
{
    private readonly LoadpullEngine _lp;

    public LoadpullPursuitEngine(LoadpullEngine lp)
    {
        _lp = lp;
    }

    // ── Result types ─────────────────────────────────────────────────────────

    public sealed class PursuitOptimum
    {
        /// <summary>Optimum termination Z (Ω).</summary>
        public Complex Z             { get; }
        /// <summary>Criterion value at the optimum (Pout W or efficiency linear).</summary>
        public double  Value         { get; }
        /// <summary>Cached inner sweep at the optimum termination.</summary>
        public GridPointResult? Sweep { get; }
        /// <summary>Zsource = conj(Zin) at ZsourceOBO dB below compression. Null if extraction failed.</summary>
        public Complex? Zsource      { get; }
        public bool    Converged     { get; }
        public string? AbortReason   { get; }

        public PursuitOptimum(Complex z, double value, GridPointResult? sweep,
            Complex? zsource, bool converged, string? abortReason = null)
        {
            Z           = z;
            Value       = value;
            Sweep       = sweep;
            Zsource     = zsource;
            Converged   = converged;
            AbortReason = abortReason;
        }
    }

    public sealed class PursuitRunResult
    {
        public PursuitOptimum MXP { get; }
        public PursuitOptimum MXE { get; }
        /// <summary>
        /// All terminations queried during both searches, keyed by Z, with their cached sweep.
        /// </summary>
        public IReadOnlyDictionary<Complex, GridPointResult> Cache { get; }
        /// <summary>Z values found unscorable (non-convergent/non-compressing).</summary>
        public IReadOnlyList<Complex> UnscorableZ { get; }
        /// <summary>Warnings accumulated during the run (e.g. non-convergent exclusions).</summary>
        public IReadOnlyList<string> Warnings { get; }

        public PursuitRunResult(PursuitOptimum mxp, PursuitOptimum mxe,
            IReadOnlyDictionary<Complex, GridPointResult> cache,
            IReadOnlyList<Complex> unscorableZ,
            IReadOnlyList<string> warnings)
        {
            MXP        = mxp;
            MXE        = mxe;
            Cache      = cache;
            UnscorableZ = unscorableZ;
            Warnings   = warnings;
        }
    }

    // ── Resolved parameters for a pursuit analysis ────────────────────────────

    public sealed record PursuitParams(
        LoadpullAnalysisParams LpParams,
        bool   UsePae,         // false = DE (default); true = PAE
        double ZsourceOBoDB,   // backoff from compression for Zsource (dB, default 5)
        // Search tuning (empirical; tuned on Hero 3B).
        double Dn             = 1.05,
        double Ds             = 1.3,
        double ConvThreshold  = 1.02,   // VSWR threshold; Fix 1: must be > 1, << DsInitial
        int    MaxAscentSteps = 40,
        SearchMethod SearchMethod = SearchMethod.SteepestAscent);

    // ── Cache ─────────────────────────────────────────────────────────────────

    // VSWR dedup tolerance: a request within this VSWR of a cached point is a cache hit.
    private const double CacheVswrTol = 1.02;

    private sealed class Cache
    {
        private readonly List<(Complex Z, GridPointResult Gpr)> _entries = new();

        public GridPointResult? TryGet(Complex z)
        {
            foreach (var (cz, gpr) in _entries)
                if (RfHelpers.VswrFromZ(z, cz) <= CacheVswrTol) return gpr;
            return null;
        }

        public void Put(Complex z, GridPointResult gpr) => _entries.Add((z, gpr));

        public IReadOnlyList<(Complex Z, GridPointResult Gpr)> All => _entries;
    }

    // ── Directive resolution ──────────────────────────────────────────────────

    /// <summary>
    /// Resolves a <see cref="LoadpullPursuitAnalysis"/> directive into a <see cref="PursuitParams"/>.
    /// Reuses LoadpullEngine.Resolve for the shared inner-sweep keys.
    /// The <see cref="LoadpullAnalysis"/> proxy has an empty Grid path (not needed for pursuit).
    /// </summary>
    public static PursuitParams Resolve(
        LoadpullPursuitAnalysis lpa,
        IReadOnlyDictionary<string, Value> globals)
    {
        double Num(string expr, double def)
        {
            try
            {
                var scope = new Scope("pursuit-resolve");
                var ev    = new Evaluator();
                foreach (var kv in globals)
                {
                    scope.Bind(kv.Key, kv.Value.ToString()!);
                    ev.InjectResolved("pursuit-resolve", kv.Key, kv.Value);
                }
                return ev.EvalExpr(Parser.Parse(expr), scope) is { Kind: ValueKind.Real } v
                    ? v.AsReal()
                    : ev.EvalExpr(Parser.Parse(expr), scope).AsComplex().Real;
            }
            catch { return def; }
        }

        // Build a proxy LoadpullAnalysis to reuse LoadpullEngine.Resolve for inner-sweep keys.
        var proxy = new LoadpullAnalysis(lpa.Name)
        {
            ToneExpr          = lpa.ToneExpr,
            MaxHarmonicExpr   = lpa.MaxHarmonicExpr,
            LoadTunerName     = lpa.LoadTunerName,
            SourceTunerName   = lpa.SourceTunerName,
            SweepExpr         = lpa.SweepExpr,
            TuneHarmExpr      = lpa.TuneHarmExpr,
            GridPath          = "",   // pursuit has no Grid input
            CompressionExpr   = lpa.CompressionExpr,
            GainTypeExpr      = lpa.GainTypeExpr,
            PinStartExpr      = lpa.PinStartExpr,
            PinStepExpr       = lpa.PinStepExpr,
            PinMaxExpr        = lpa.PinMaxExpr,
            TickleExpr        = lpa.TickleExpr,
            MaxIterExpr       = lpa.MaxIterExpr,
            FFTOverSampleExpr = lpa.FFTOverSampleExpr,
            TolExpr           = lpa.TolExpr,
            DriveSteppingExpr = lpa.DriveSteppingExpr,
            GuardHarmonicExpr = lpa.GuardHarmonicExpr,
        };

        // Resolve the inner-sweep params — but Grid validation will fail on empty path.
        // We resolve manually to avoid that.
        double tone    = Num(lpa.ToneExpr,           1e9);
        int    maxH    = (int)Num(lpa.MaxHarmonicExpr,   5);
        int    osamp   = Math.Max(1, (int)Num(lpa.FFTOverSampleExpr, 1));
        double tol     = Num(lpa.TolExpr,             1e-6);
        int    maxIter = Math.Max(1, (int)Num(lpa.MaxIterExpr, 100));
        int    guard   = (int)Num(lpa.GuardHarmonicExpr, 0);

        var driveStepping = DcBiasSteppingMode.IfNecessary;
        var ds = lpa.DriveSteppingExpr.Trim();
        if (ds.Equals("Always", StringComparison.OrdinalIgnoreCase)) driveStepping = DcBiasSteppingMode.Always;
        else if (ds.Equals("Never", StringComparison.OrdinalIgnoreCase)) driveStepping = DcBiasSteppingMode.Never;

        bool   sweepLoad = !lpa.SweepExpr.Trim().Equals("Source", StringComparison.OrdinalIgnoreCase);
        int    tuneHarm  = (int)Num(lpa.TuneHarmExpr, 1);
        double compress  = Num(lpa.CompressionExpr, 3.0);
        bool   useGt     = !lpa.GainTypeExpr.Trim().Equals("Gp", StringComparison.OrdinalIgnoreCase);
        double pinStart  = Num(lpa.PinStartExpr, -20.0);
        double pinStep   = Num(lpa.PinStepExpr,    1.0);
        double pinMax    = Num(lpa.PinMaxExpr,     10.0);

        double? tickle = null;
        var ts = lpa.TickleExpr.Trim();
        if (!ts.Equals("off", StringComparison.OrdinalIgnoreCase) &&
            !ts.Equals("false", StringComparison.OrdinalIgnoreCase))
            tickle = Num(ts, -50.0);

        // Dummy grid (pursuit doesn't use a grid file).
        var dummyGrid = new GamReader.GamGrid(new List<GamReader.GamPoint>
        {
            new(new Complex(0, 0), new Complex(50, 0), 0)   // 50 Ω start point
        }, 50.0);

        var lpParams = new LoadpullAnalysisParams(
            tone, maxH, osamp, tol, driveStepping, guard, maxIter,
            lpa.LoadTunerName, lpa.SourceTunerName,
            sweepLoad, tuneHarm, dummyGrid,
            compress, useGt, pinStart, pinStep, pinMax, tickle);

        bool usePae  = lpa.EffTypeExpr.Trim().Equals("PAE", StringComparison.OrdinalIgnoreCase);
        double obo   = Num(lpa.ZsourceOBOExpr, 5.0);

        var searchMethod = lpa.SearchMethodExpr.Trim().Equals(
            nameof(SearchMethod.IteratedQuadratic), StringComparison.OrdinalIgnoreCase)
            ? SearchMethod.IteratedQuadratic
            : SearchMethod.SteepestAscent;

        return new PursuitParams(lpParams, usePae, obo, SearchMethod: searchMethod);
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    public PursuitRunResult Run(PursuitParams pp)
    {
        var lpp    = pp.LpParams;
        var ctx    = _lp.PrepareContext(lpp);
        var cache  = new Cache();
        var unscorable = new List<Complex>();
        var warnings   = new List<string>();

        int queryCount = 0;

        // ── Inner query function ──────────────────────────────────────────────
        // Runs one 4b-1 drive-to-compression run at the given Z, caches the result.
        // Returns: (criterion value, GridPointResult?) — null criterion = unscorable.
        (double? Criterion, GridPointResult? Gpr) Query(Complex z, bool isMxe)
        {
            // Cache hit: read criterion from cached sweep.
            var hit = cache.TryGet(z);
            if (hit is not null)
            {
                double? hitC = ExtractCriterion(hit, isMxe ? pp.UsePae : false, mxe: isMxe, xdB: lpp.Compression);
                return (hitC, hit);
            }

            // Cache miss: run a full drive-to-compression sweep.
            queryCount++;
            // B7: dead gamma lines removed; gamma now computed inside RunOneTermination from Z and grid Z0.
            Console.Error.WriteLine(
                $"[Pursuit] Query {queryCount}: Z={z.Real:F2}{(z.Imaginary >= 0 ? "+" : "")}{z.Imaginary:F2}j Ω");

            var gpr = _lp.RunOneTermination(lpp, ctx, z, gridIndex: queryCount - 1);
            cache.Put(z, gpr);

            bool compressed = gpr.StopReason == "Compression";
            if (!compressed)
            {
                unscorable.Add(z);
                Console.Error.WriteLine(
                    $"[Pursuit]   → Unscorable (Stop={gpr.StopReason})");
                return (null, gpr);
            }

            double? c = ExtractCriterion(gpr, isMxe ? pp.UsePae : false, mxe: isMxe, xdB: lpp.Compression);
            // MXP criterion is in dBm; MXE criterion is a linear efficiency ratio.
            Console.Error.WriteLine(
                $"[Pursuit]   → {(isMxe ? "Eff" : "Pout(dBm)")}={c:F4}");
            return (c, gpr);
        }

        // ── MXP search ───────────────────────────────────────────────────────
        Console.Error.WriteLine($"[Pursuit] ── MXP search ({pp.SearchMethod}) ──");
        var mxpEngine = new PursuitEngine
        {
            Dn = pp.Dn, DsInitial = pp.Ds,
            ConvergenceThreshold = pp.ConvThreshold,
            MaxAscentSteps       = pp.MaxAscentSteps,
            Method               = pp.SearchMethod,
        };

        var mxpBaylis = mxpEngine.Run(
            lpp.Grid.Points.Count > 0 ? lpp.Grid.Points[0].Z : new Complex(50, 0),
            z =>
            {
                var (crit, _) = Query(z, isMxe: false);
                return crit;
            });

        // Record unscorable from MXP search.
        foreach (var uz in mxpBaylis.UnscorableZ)
            if (!unscorable.Contains(uz)) unscorable.Add(uz);

        PursuitOptimum mxpResult;
        if (!mxpBaylis.Converged && mxpBaylis.AbortReason is not null)
        {
            // Start point unscorable — abort.
            mxpResult = new PursuitOptimum(mxpBaylis.OptimumZ, mxpBaylis.OptimumValue,
                null, null, converged: false, mxpBaylis.AbortReason);
        }
        else
        {
            var mxpSweep = cache.TryGet(mxpBaylis.OptimumZ)
                        ?? cache.All.MaxBy(e => ExtractCriterion(e.Gpr, false, mxe: false, xdB: lpp.Compression) ?? double.NegativeInfinity).Gpr;
            var mxpZ     = mxpSweep is not null
                ? cache.All.First(e => ReferenceEquals(e.Gpr, mxpSweep)).Z
                : mxpBaylis.OptimumZ;
            double mxpVal = ExtractCriterion(mxpSweep, false, mxe: false, xdB: lpp.Compression) ?? mxpBaylis.OptimumValue;
            var mxpZsrc   = mxpSweep is not null
                ? ComputeZsource(mxpSweep, ctx, pp.ZsourceOBoDB, lpp)
                : null;
            mxpResult = new PursuitOptimum(mxpZ, mxpVal, mxpSweep, mxpZsrc, converged: true);
            Console.Error.WriteLine(
                $"[Pursuit] MXP = Z={mxpZ.Real:F2}{(mxpZ.Imaginary >= 0 ? "+" : "")}{mxpZ.Imaginary:F2}j Ω  " +
                $"Pout={mxpVal:F2} dBm");
        }

        // ── MXE search — seeded from highest-efficiency cached point ──────────
        Console.Error.WriteLine($"[Pursuit] ── MXE search ({pp.SearchMethod}, seeded from MXP cache) ──");

        // Pedro coupling: seed at the cached point with highest efficiency
        // (naturally ~2-2.5 VSWR from MXP for a stable FET).
        Complex mxeStart = mxpResult.Z;
        double bestEff = double.NegativeInfinity;
        foreach (var (cz, cgpr) in cache.All)
        {
            double? eff = ExtractCriterion(cgpr, pp.UsePae, mxe: true, xdB: lpp.Compression);
            if (eff > bestEff) { bestEff = eff.Value; mxeStart = cz; }
        }
        Console.Error.WriteLine(
            $"[Pursuit] MXE seed = Z={mxeStart.Real:F2}{(mxeStart.Imaginary >= 0 ? "+" : "")}{mxeStart.Imaginary:F2}j Ω  " +
            $"Eff={bestEff*100:F1}%  " +
            $"(VSWR from MXP = {RfHelpers.VswrFromZ(mxeStart, mxpResult.Z):F2})");

        var mxeEngine = new PursuitEngine
        {
            Dn = pp.Dn, DsInitial = pp.Ds,
            ConvergenceThreshold = pp.ConvThreshold,
            MaxAscentSteps       = pp.MaxAscentSteps,
            Method               = pp.SearchMethod,
        };

        var mxeBaylis = mxeEngine.Run(mxeStart, z =>
        {
            var (crit, _) = Query(z, isMxe: true);
            return crit;
        });

        foreach (var uz in mxeBaylis.UnscorableZ)
            if (!unscorable.Contains(uz)) unscorable.Add(uz);

        PursuitOptimum mxeResult;
        if (!mxeBaylis.Converged && mxeBaylis.AbortReason is not null)
        {
            mxeResult = new PursuitOptimum(mxeBaylis.OptimumZ, mxeBaylis.OptimumValue,
                null, null, converged: false, mxeBaylis.AbortReason);
        }
        else
        {
            var mxeSweep = cache.TryGet(mxeBaylis.OptimumZ)
                        ?? cache.All.MaxBy(e => ExtractCriterion(e.Gpr, pp.UsePae, mxe: true, xdB: lpp.Compression) ?? double.NegativeInfinity).Gpr;
            var mxeZ     = mxeSweep is not null
                ? cache.All.First(e => ReferenceEquals(e.Gpr, mxeSweep)).Z
                : mxeBaylis.OptimumZ;
            double mxeVal = ExtractCriterion(mxeSweep, pp.UsePae, mxe: true, xdB: lpp.Compression) ?? mxeBaylis.OptimumValue;
            var mxeZsrc   = mxeSweep is not null
                ? ComputeZsource(mxeSweep, ctx, pp.ZsourceOBoDB, lpp)
                : null;
            mxeResult = new PursuitOptimum(mxeZ, mxeVal, mxeSweep, mxeZsrc, converged: true);
            Console.Error.WriteLine(
                $"[Pursuit] MXE = Z={mxeZ.Real:F2}{(mxeZ.Imaginary >= 0 ? "+" : "")}{mxeZ.Imaginary:F2}j Ω  " +
                $"Eff={mxeVal*100:F1}%  " +
                $"(VSWR from MXP = {RfHelpers.VswrFromZ(mxeZ, mxpResult.Z):F2})");
        }

        // Tear down.
        ctx.SweptModel.ClearHarmonicOverride();
        ctx.SrcModel.SetTone(0);
        ctx.LoadModel.SetTone(0);

        // Build flat cache dict for the result.
        var cacheDict = cache.All.ToDictionary(e => e.Z, e => e.Gpr);
        Console.Error.WriteLine(
            $"[Pursuit] Total queries={queryCount}  Cache entries={cacheDict.Count}  Unscorable={unscorable.Count}");

        return new PursuitRunResult(mxpResult, mxeResult, cacheDict, unscorable, warnings);
    }

    // ── Criterion extraction ──────────────────────────────────────────────────

    /// <summary>
    /// Extracts the criterion value from a GridPointResult by interpolating to exactly
    /// <paramref name="xdB"/> dB of gain compression.
    ///
    /// Algorithm:
    ///   1. Compute Gmax over all converged non-tickle steps.
    ///   2. Find the bracket: "below" = last step with compression &lt; xdB,
    ///      "above" = first step with compression ≥ xdB.
    ///      With Change 1, the sweep stops at compression ≥ xdB+0.1 on the regular grid,
    ///      so "below" and "above" are adjacent regular-grid steps that straddle xdB.
    ///   3. t = (xdB − comprBelow) / (comprAbove − comprBelow) — a real fraction in [0,1].
    ///      This interpolates to EXACTLY P-xdB, so the result lies strictly between the
    ///      two grid steps' values (not equal to the over-compression step).
    ///
    /// MXP criterion = Pout at P-xdB in <b>dBm</b> (consistent gradient surface across Ω).
    /// MXE criterion = DE or PAE at P-xdB (linear ratio, ≤ 1).
    /// Returns null if the sweep never reached xdB compression.
    /// </summary>
    private static double? ExtractCriterion(GridPointResult? gpr, bool usePae, bool mxe, double xdB)
    {
        if (gpr is null || gpr.StopReason != "Compression") return null;

        var converged = gpr.PinSteps.Where(s => s.Converged && !s.IsTickle).ToList();
        if (converged.Count == 0) return null;

        // need the index for maxGain so that all lower index can be ignored
        int ? maxIndex = converged.Select((item, index) => new { item.GtDb, index }).MaxBy(x => x.GtDb)?.index;
        if (maxIndex == null) return null;
        double gMax = converged[maxIndex.Value].GtDb;

        // Find the bracket straddling xdB.
        PinStepResult? below = null, above = null;
        for (int i = maxIndex.Value; i < converged.Count; i++)// only use power sweep points higher than the max Gain
        {
            double compr = gMax - converged[i].GtDb;
            if (compr < xdB)       below = converged[i];
            else if (above is null) above = converged[i];
        }
        if (above is null) return null;   // sweep reached stop but never crossed xdB — unscorable
        below ??= above;                   // xdB first reached at step 0 (no sub-compression step)

        double comprBelow = gMax - below.GtDb;
        double comprAbove = gMax - above.GtDb;
        double dCompr     = comprAbove - comprBelow;

        // True interpolation fraction to exactly xdB.  With PinStep=1 dB, t is typically
        // 0.1–0.9 — far from 1.0 (which was the pre-fix bug where xdB was set to comprAbove).
        double t = dCompr > 1e-10
            ? Math.Clamp((xdB - comprBelow) / dCompr, 0.0, 1.0)
            : 0.0;

        if (!mxe)
        {
            // MXP: interpolated Pout in dBm — must lie BETWEEN below and above grid steps.
            double poutBelowDbm = LoadpullEngine.WattsToDbm(below.PoutW);
            double poutAboveDbm = LoadpullEngine.WattsToDbm(above.PoutW);
            return poutBelowDbm + t * (poutAboveDbm - poutBelowDbm);
        }
        else
        {
            // MXE: interpolated DE or PAE (linear ratio).
            double effBelow = usePae ? below.Pae : below.De;
            double effAbove = usePae ? above.Pae : above.De;
            return effBelow + t * (effAbove - effBelow);
        }
    }

    // ── Auto-Zsource (§6) ─────────────────────────────────────────────────────

    /// <summary>
    /// Computes Zsource = conj(Zin) at ZsourceOBO dB below compression.
    /// Zin = V[srcIfIdx, k=1] / (-INl[srcIfIdx, k=1]).
    /// OBO drive level is found by linear interpolation in the cached Pin sweep.
    /// </summary>
    private static Complex? ComputeZsource(
        GridPointResult gpr,
        LoadpullEngine.PursuitContext ctx,
        double oboDb,
        LoadpullAnalysisParams lpp)
    {
        var converged = gpr.PinSteps.Where(s => s.Converged && !s.IsTickle).ToList();
        if (converged.Count < 2) return null;

        // Find Pin at exactly lpp.Compression dB using the same bracket as ExtractCriterion.
        // This is the conventional compression point (P-xdB), from which OBO is measured.
        int ? maxIndex = converged.Select((item, index) => new { item.GtDb, index }).MaxBy(x => x.GtDb)?.index;
        if (maxIndex == null) return null;
        double gMax = converged[maxIndex.Value].GtDb;

        double xdB  = lpp.Compression;

        PinStepResult? compLo = null, compHi = null;
        for (int i = maxIndex.Value; i < converged.Count; i++)
        {
            double compr = gMax - converged[i].GtDb;
            if (compr < xdB)        compLo = converged[i];
            else if (compHi is null) compHi = converged[i];
        }
        if (compHi is null) return null;   // sweep never reached lpp.Compression — can't place OBO
        compLo ??= compHi;

        // Interpolate Pin to exactly P-xdB.
        double cLo   = gMax - compLo.GtDb;
        double cHi   = gMax - compHi.GtDb;
        double dC    = cHi - cLo;
        double fC    = dC > 1e-10 ? Math.Clamp((xdB - cLo) / dC, 0.0, 1.0) : 0.0;
        double pinComp = compLo.PavlDbm + fC * (compHi.PavlDbm - compLo.PavlDbm);

        double pinObo = pinComp - oboDb;

        // Find the two steps that bracket pinObo.
        PinStepResult? lo = null, hi = null;
        for (int i = 0; i < converged.Count; i++)
        {
            if (converged[i].PavlDbm <= pinObo) lo = converged[i];
            if (converged[i].PavlDbm >= pinObo && hi is null) hi = converged[i];
        }
        if (lo is null && hi is null) return null;
        lo ??= hi;
        hi ??= lo;

        // Linear interpolation of V and INl at f0 (k=1) to pinObo.
        double pin0 = lo!.PavlDbm, pin1 = hi!.PavlDbm;
        double frac2 = pin1 > pin0 + 1e-12
            ? Math.Clamp((pinObo - pin0) / (pin1 - pin0), 0, 1)
            : 0.0;

        int K = lo.V.GetLength(1) - 1;
        if (K < 1) return null;

        Complex vSrc  = lo.V[ctx.SrcIfIdx, 1]   + frac2 * (hi.V[ctx.SrcIfIdx, 1]   - lo.V[ctx.SrcIfIdx, 1]);
        Complex iNlSrc= lo.INl[ctx.SrcIfIdx, 1] + frac2 * (hi.INl[ctx.SrcIfIdx, 1] - lo.INl[ctx.SrcIfIdx, 1]);

        // Zin = V[gate,k=1] / I_into_DUT
        // INl convention (HbEngine.cs): INl[gate,1] = current FROM node INTO device.
        // At n_gate (RF, choke open): only source + FET, so KCL gives
        //   I_from_source_into_gate = INl[gate,1]  (no negation).
        // Therefore Zin = V / INl[gate,1].  Do NOT negate — negation gives Re(Zin) < 0, non-physical.
        // See Pass A diagnostic: Zin_correct = 50 Ω, Zin_code_with_minus = −50 Ω.
        if (iNlSrc.Magnitude < 1e-30) return null;

        Complex zIn = vSrc / iNlSrc;   // B1 fix: removed the erroneous negation
        return Complex.Conjugate(zIn);   // Zsource = Zin*
    }
}
