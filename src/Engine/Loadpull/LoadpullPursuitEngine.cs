using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.Loadpull;

/// <summary>Which Zsource the follow-on loadpull uses for the source match (§6.5.2).</summary>
public enum LoadpullResultZsourceMode
{
    /// <summary>Use the MXE optimum's recommended Zsource (default).</summary>
    MXE,
    /// <summary>Use the MXP optimum's recommended Zsource.</summary>
    MXP,
    /// <summary>No override — use the Source Tuner's own declared Z1.</summary>
    None,
}

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
///
/// Always produces a LoadpullPursuitResult (§6.5.1) containing: resolved params, MXP/MXE,
/// cache, unscorable, warnings, the always-built in-memory GamBuilderResult, and the
/// optional follow-on LoadpullResult (§6.5.2).
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
        /// <summary>Criterion value at the optimum (Pout dBm for MXP, efficiency linear for MXE).</summary>
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

    /// <summary>
    /// The single self-documenting result of a loadpull_pursuit run (loadpull_pursuit.md §6.5.1).
    ///
    /// Carries: resolved input Params, MXP/MXE optima, query Cache, UnscorableZ, Warnings,
    /// the always-built in-memory RecommendedTerminations (GamBuilderResult), and the optional
    /// follow-on LoadpullData (null if CreateLoadpullResult=false or neither optimum converged).
    ///
    /// (Formerly PursuitRunResult — renamed and extended.)
    /// </summary>
    public sealed class LoadpullPursuitResult
    {
        /// <summary>The resolved directive parameters that produced this result.</summary>
        public PursuitParams Params { get; }
        public PursuitOptimum MXP { get; }
        public PursuitOptimum MXE { get; }
        /// <summary>All terminations queried during both searches, keyed by Z, with cached sweep.</summary>
        public IReadOnlyDictionary<Complex, GridPointResult> Cache { get; }
        /// <summary>Z values found unscorable (non-convergent/non-compressing).</summary>
        public IReadOnlyList<Complex> UnscorableZ { get; }
        /// <summary>Warnings accumulated during the run.</summary>
        public IReadOnlyList<string> Warnings { get; }
        /// <summary>
        /// Focused+broad recommended-termination point set (always built in memory, §6.5.1).
        /// Independent of OutputGrid (file write) and CreateLoadpullResult (simulation).
        /// </summary>
        public GamWriter.GamBuilderResult RecommendedTerminations { get; }
        /// <summary>
        /// Follow-on loadpull over the recommended terminations (§6.5.2), or null if
        /// CreateLoadpullResult=false or neither optimum converged.
        /// </summary>
        public LoadpullResult? LoadpullData { get; }

        public LoadpullPursuitResult(
            PursuitParams                                    @params,
            PursuitOptimum                                   mxp,
            PursuitOptimum                                   mxe,
            IReadOnlyDictionary<Complex, GridPointResult>    cache,
            IReadOnlyList<Complex>                           unscorableZ,
            IReadOnlyList<string>                            warnings,
            GamWriter.GamBuilderResult                       recommendedTerminations,
            LoadpullResult?                                  loadpullData)
        {
            Params                 = @params;
            MXP                    = mxp;
            MXE                    = mxe;
            Cache                  = cache;
            UnscorableZ            = unscorableZ;
            Warnings               = warnings;
            RecommendedTerminations = recommendedTerminations;
            LoadpullData           = loadpullData;
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
        double ConvThreshold  = 1.02,   // VSWR threshold; must be > 1, << DsInitial
        int    MaxAscentSteps = 40,
        SearchMethod SearchMethod = SearchMethod.SteepestAscent,
        // Gam builder params (always built in memory; OutputGridPath controls file write only).
        double Vswr1              = 1.5,
        int    Vswr1Resolution    = 4,
        double Vswr2              = 3.0,
        int    Vswr2Resolution    = 4,
        bool   KeepNonconverging  = false,
        double NonconvergentVswr  = 1.05,
        string? OutputGridPath    = null,
        // Follow-on loadpull (§6.5.2).
        bool   CreateLoadpullResult   = true,
        LoadpullResultZsourceMode LoadpullResultZsource = LoadpullResultZsourceMode.MXE);

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
        IReadOnlyDictionary<string, Value> globals,
        IReadOnlyCollection<string>? globalsWithUnit = null)
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

        bool Bool(string expr, bool def)
        {
            var s = expr.Trim();
            if (s.Equals("true",  StringComparison.OrdinalIgnoreCase) ||
                s.Equals("on",    StringComparison.OrdinalIgnoreCase)  ||
                s.Equals("yes",   StringComparison.OrdinalIgnoreCase)  ||
                s == "1") return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("off",   StringComparison.OrdinalIgnoreCase)  ||
                s.Equals("no",    StringComparison.OrdinalIgnoreCase)  ||
                s == "0") return false;
            try { return (int)Num(expr, def ? 1 : 0) != 0; } catch { return def; }
        }

        // Resolve the inner-sweep params manually (avoid Grid validation on empty path).
        // Tone is the only frequency-unit-sensitive field; resolve it with HB's var-unit-wins rule.
        double tone;
        try   { tone = FreqUnit.ResolveHz(lpa.ToneExpr, lpa.ToneUnit, globals, globalsWithUnit); }
        catch { tone = 1e9; }
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

        // Gam builder params.
        double vswr1     = Num(lpa.Vswr1Expr,             1.5);
        int    vswr1Res  = Math.Max(2, (int)Num(lpa.Vswr1ResolutionExpr, 4));
        double vswr2     = Num(lpa.Vswr2Expr,             3.0);
        int    vswr2Res  = Math.Max(2, (int)Num(lpa.Vswr2ResolutionExpr, 4));
        bool   keepNonconv  = Bool(lpa.KeepNonconvergingExpr, false);
        double nonconvVswr  = Num(lpa.NonconvergentVswrExpr, 1.05);

        // Follow-on loadpull params.
        bool createLp = Bool(lpa.CreateLoadpullResultExpr, true);
        var lpZsrcMode = lpa.LoadpullResultZsourceExpr.Trim().ToUpperInvariant() switch
        {
            "MXP"  => LoadpullResultZsourceMode.MXP,
            "NONE" => LoadpullResultZsourceMode.None,
            _      => LoadpullResultZsourceMode.MXE,   // default
        };

        return new PursuitParams(
            lpParams, usePae, obo,
            SearchMethod:           searchMethod,
            Vswr1:                  vswr1,
            Vswr1Resolution:        vswr1Res,
            Vswr2:                  vswr2,
            Vswr2Resolution:        vswr2Res,
            KeepNonconverging:      keepNonconv,
            NonconvergentVswr:      nonconvVswr,
            OutputGridPath:         lpa.OutputGridPath,
            CreateLoadpullResult:   createLp,
            LoadpullResultZsource:  lpZsrcMode);
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <param name="appendOutputBlock">
    /// When true, the OutputGrid <c>.gam</c> block is appended (freq-tagged) rather than truncating the
    /// file — used by a frequency-swept pursuit to accumulate one block per frequency.
    /// </param>
    /// <param name="control">
    /// Optional cancellation. Checked once per QUERY — one cache-miss drive-to-compression run, which
    /// is both the expensive unit here and the one every search path funnels through. There is no
    /// progress tick: a pursuit's query count is decided by the search itself and is not known ahead
    /// of time, so it has no honest denominator.
    /// </param>
    public DataSet Run(PursuitParams pp, bool appendOutputBlock = false, RunControl? control = null)
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
            control?.ThrowIfCancellationRequested();
            queryCount++;
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

        // ── Tear down search context ──────────────────────────────────────────
        ctx.SweptModel.ClearHarmonicOverride();
        ctx.SrcModel.SetTone(0);
        ctx.LoadModel.SetTone(0);

        // Build flat cache dict for the result.
        var cacheDict = cache.All.ToDictionary(e => e.Z, e => e.Gpr);
        Console.Error.WriteLine(
            $"[Pursuit] Total queries={queryCount}  Cache entries={cacheDict.Count}  Unscorable={unscorable.Count}");

        // ── Always build recommended terminations in memory (§6.5.1) ─────────
        var gamParams = new GamWriter.GamBuilderParams(
            mxpResult.Z, mxeResult.Z, unscorable,
            pp.Vswr1, pp.Vswr1Resolution, pp.Vswr2, pp.Vswr2Resolution,
            pp.KeepNonconverging, pp.NonconvergentVswr);
        var recommendedTerminations = GamWriter.Build(gamParams);
        foreach (var w in recommendedTerminations.Warnings)
            warnings.Add(w);

        // Optionally write the .gam file (OutputGrid controls file only, not simulation).
        if (!string.IsNullOrEmpty(pp.OutputGridPath))
        {
            GamWriter.WriteFile(pp.OutputGridPath!, recommendedTerminations,
                freqHz: lpp.ToneHz, append: appendOutputBlock);
            Console.Error.WriteLine(
                $"[Pursuit] .gam {(appendOutputBlock ? "appended" : "written")} → {pp.OutputGridPath}  " +
                $"({recommendedTerminations.Points.Count} pts @ {lpp.ToneHz / 1e9:G4} GHz)");
        }

        // ── Optionally run follow-on loadpull (§6.5.2) ───────────────────────
        // Runs iff CreateLoadpullResult=true AND both optima converged.
        // Uses the in-memory recommended terminations (independent of OutputGrid).
        DataSet? followOnDs = null;
        if (pp.CreateLoadpullResult
            && mxpResult.Converged && mxeResult.Converged
            && recommendedTerminations.Points.Count > 0)
        {
            followOnDs = RunFollowOnLoadpull(pp, mxpResult, mxeResult, recommendedTerminations);
        }

        return BuildPursuitDataSet(mxpResult, mxeResult,
            cache.All.Count, unscorable.Count, recommendedTerminations, followOnDs,
            lpp.ToneHz);
    }

    // ── Follow-on loadpull (§6.5.2) ───────────────────────────────────────────

    /// <summary>
    /// Runs a standard loadpull over the recommended terminations using the chosen source match.
    ///
    /// Source match (LoadpullResultZsource):
    ///   MXE → override Source Tuner Z[1] with MXE.Zsource
    ///   MXP → override Source Tuner Z[1] with MXP.Zsource
    ///   None → leave Source Tuner's declared Z[1] untouched
    ///
    /// SetHarmonicOverride on the Source Tuner overrides both the Z_Port impedance and the
    /// drive-voltage calibration (SetSourceDrive calls GetZ, which respects the override), so
    /// Pavl and the presented source impedance are always in agreement.
    /// </summary>
    private DataSet RunFollowOnLoadpull(
        PursuitParams pp,
        PursuitOptimum mxp, PursuitOptimum mxe,
        GamWriter.GamBuilderResult recommendedTerminations)
    {
        // Build a GamGrid from the in-memory recommended terminations.
        const double z0 = 50.0;
        var followOnPoints = recommendedTerminations.Points
            .Select((z, i) => new GamReader.GamPoint(RfHelpers.Z2G(z / z0), z, i))
            .ToList();
        var followOnGrid   = new GamReader.GamGrid(followOnPoints, z0);
        var followOnLpParams = pp.LpParams with { Grid = followOnGrid };

        // Determine Zsource override.
        Complex? zsourceOverride = pp.LoadpullResultZsource switch
        {
            LoadpullResultZsourceMode.MXE => mxe.Zsource,
            LoadpullResultZsourceMode.MXP => mxp.Zsource,
            _                             => null,          // None: no override
        };

        Console.Error.WriteLine(
            $"[Pursuit] Follow-on loadpull: {followOnGrid.Points.Count} terminations  " +
            $"Zsource={pp.LoadpullResultZsource}" +
            (zsourceOverride.HasValue
                ? $"={zsourceOverride.Value.Real:F2}{(zsourceOverride.Value.Imaginary >= 0 ? "+" : "")}{zsourceOverride.Value.Imaginary:F2}j Ω"
                : " (Source Tuner's declared Z1)"));

        // Get a context to access the source model reference for the override.
        var followCtx = _lp.PrepareContext(pp.LpParams);
        try
        {
            if (zsourceOverride.HasValue)
                followCtx.SrcModel.SetHarmonicOverride(1, zsourceOverride.Value);

            // Enrich the follow-on with the canonical display metrics (Pout_dBm/Efficiency/Zin_*/AMPM_deg/
            // IRL_dB) — the same post-processing a standalone loadpull gets — so the follow-on surface is
            // displayable with friendly names, not raw LP_Pout(W)/LP_DE.
            return RfCore.Loadpull.LoadpullPostProcessor.Enrich(_lp.Run(followOnLpParams));
        }
        finally
        {
            // _lp.Run() clears the swept (load) model's override but not the src model's.
            if (zsourceOverride.HasValue)
                followCtx.SrcModel.ClearHarmonicOverride();
        }
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
    ///   3. t = (xdB − comprBelow) / (comprAbove − comprBelow) — a real fraction in [0,1].
    ///
    /// MXP criterion = Pout at P-xdB in <b>dBm</b>.
    /// MXE criterion = DE or PAE at P-xdB (linear ratio, ≤ 1).
    /// Returns null if the sweep never reached xdB compression.
    /// </summary>
    private static double? ExtractCriterion(GridPointResult? gpr, bool usePae, bool mxe, double xdB)
    {
        if (gpr is null || gpr.StopReason != "Compression") return null;

        var converged = gpr.PinSteps.Where(s => s.Converged && !s.IsTickle).ToList();
        if (converged.Count == 0) return null;

        int? maxIndex = converged.Select((item, index) => new { item.GtDb, index }).MaxBy(x => x.GtDb)?.index;
        if (maxIndex == null) return null;
        double gMax = converged[maxIndex.Value].GtDb;

        PinStepResult? below = null, above = null;
        for (int i = maxIndex.Value; i < converged.Count; i++)
        {
            double compr = gMax - converged[i].GtDb;
            if (compr < xdB)       below = converged[i];
            else if (above is null) above = converged[i];
        }
        if (above is null) return null;
        below ??= above;

        double comprBelow = gMax - below.GtDb;
        double comprAbove = gMax - above.GtDb;
        double dCompr     = comprAbove - comprBelow;

        double t = dCompr > 1e-10
            ? Math.Clamp((xdB - comprBelow) / dCompr, 0.0, 1.0)
            : 0.0;

        if (!mxe)
        {
            double poutBelowDbm = LoadpullEngine.WattsToDbm(below.PoutW);
            double poutAboveDbm = LoadpullEngine.WattsToDbm(above.PoutW);
            return poutBelowDbm + t * (poutAboveDbm - poutBelowDbm);
        }
        else
        {
            double effBelow = usePae ? below.Pae : below.De;
            double effAbove = usePae ? above.Pae : above.De;
            return effBelow + t * (effAbove - effBelow);
        }
    }

    // ── Auto-Zsource (§6) ─────────────────────────────────────────────────────

    /// <summary>
    /// Computes Zsource = conj(Zin) at ZsourceOBO dB below compression.
    /// Zin = V[srcIfIdx, k=1] / INl[srcIfIdx, k=1].
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

        int? maxIndex = converged.Select((item, index) => new { item.GtDb, index }).MaxBy(x => x.GtDb)?.index;
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
        if (compHi is null) return null;
        compLo ??= compHi;

        double cLo   = gMax - compLo.GtDb;
        double cHi   = gMax - compHi.GtDb;
        double dC    = cHi - cLo;
        double fC    = dC > 1e-10 ? Math.Clamp((xdB - cLo) / dC, 0.0, 1.0) : 0.0;
        double pinComp = compLo.PavlDbm + fC * (compHi.PavlDbm - compLo.PavlDbm);

        double pinObo = pinComp - oboDb;

        PinStepResult? lo = null, hi = null;
        for (int i = 0; i < converged.Count; i++)
        {
            if (converged[i].PavlDbm <= pinObo) lo = converged[i];
            if (converged[i].PavlDbm >= pinObo && hi is null) hi = converged[i];
        }
        if (lo is null && hi is null) return null;
        lo ??= hi;
        hi ??= lo;

        double pin0 = lo!.PavlDbm, pin1 = hi!.PavlDbm;
        double frac2 = pin1 > pin0 + 1e-12
            ? Math.Clamp((pinObo - pin0) / (pin1 - pin0), 0, 1)
            : 0.0;

        int K = lo.V.GetLength(1) - 1;
        if (K < 1) return null;

        Complex iLoFund = lo.ISrcIn.Length > 1 ? lo.ISrcIn[1] : Complex.Zero;
        Complex iHiFund = hi.ISrcIn.Length > 1 ? hi.ISrcIn[1] : Complex.Zero;

        Complex vSrc  = lo.V[ctx.SrcIfIdx, 1] + frac2 * (hi.V[ctx.SrcIfIdx, 1] - lo.V[ctx.SrcIfIdx, 1]);
        // I_into_DUT = the true source-delivered current at the gate node (ISrcIn), which accounts for
        // any passives wired there — NOT INl[gate] alone (see LoadpullEngine.ComputeSourceInputCurrent).
        Complex iSrcIn = iLoFund + frac2 * (iHiFund - iLoFund);

        // Zin = V[gate,k=1] / I_into_DUT (B1 fix: no negation — see CLAUDE.md §B1).
        if (iSrcIn.Magnitude < 1e-30) return null;

        Complex zIn = vSrc / iSrcIn;
        return Complex.Conjugate(zIn);   // Zsource = Zin*
    }

    // ── Pursuit DataSet builder ───────────────────────────────────────────────

    private static DataSet BuildPursuitDataSet(
        PursuitOptimum mxp,
        PursuitOptimum mxe,
        int cacheCount,
        int unscorableCount,
        GamWriter.GamBuilderResult recommendedTerminations,
        DataSet? followOnDs,
        double toneHz)
    {
        var ds = new DataSet();

        // Tone carrier (Hz) — lets a parametric freq sweep recognize this as a per-frequency point and
        // stack a "freq" axis (ParametricSweepEngine.TryBuildToneFreqAxis), mirroring the loadpull path.
        ds.Add("__Freq", new DataCube(new[] { new Axis("freq", new[] { toneHz }, "Hz") }, new[] { toneHz }));

        // MXP scalars
        ds.Add("MXP_PoutDbm",    DataCube.Scalar(mxp.Converged ? mxp.Value : 0.0));
        ds.Add("MXP_ZRe",        DataCube.Scalar(mxp.Z.Real));
        ds.Add("MXP_ZIm",        DataCube.Scalar(mxp.Z.Imaginary));
        ds.Add("MXP_Converged",  DataCube.Scalar(mxp.Converged ? 1.0 : 0.0));
        ds.Add("MXP_ZsourceRe",  DataCube.Scalar(mxp.Zsource?.Real      ?? 0.0));
        ds.Add("MXP_ZsourceIm",  DataCube.Scalar(mxp.Zsource?.Imaginary ?? 0.0));
        ds.Add("MXP_HasZsource", DataCube.Scalar(mxp.Zsource.HasValue   ? 1.0 : 0.0));

        // MXE scalars
        ds.Add("MXE_Eff",        DataCube.Scalar(mxe.Converged ? mxe.Value : 0.0));
        ds.Add("MXE_ZRe",        DataCube.Scalar(mxe.Z.Real));
        ds.Add("MXE_ZIm",        DataCube.Scalar(mxe.Z.Imaginary));
        ds.Add("MXE_Converged",  DataCube.Scalar(mxe.Converged ? 1.0 : 0.0));
        ds.Add("MXE_ZsourceRe",  DataCube.Scalar(mxe.Zsource?.Real      ?? 0.0));
        ds.Add("MXE_ZsourceIm",  DataCube.Scalar(mxe.Zsource?.Imaginary ?? 0.0));
        ds.Add("MXE_HasZsource", DataCube.Scalar(mxe.Zsource.HasValue   ? 1.0 : 0.0));

        // Metadata scalars
        ds.Add("CacheCount",      DataCube.Scalar((double)cacheCount));
        ds.Add("UnscorableCount", DataCube.Scalar((double)unscorableCount));
        ds.Add("RecommTermCount", DataCube.Scalar((double)recommendedTerminations.Points.Count));

        // Embed the follow-on loadpull cubes (if present) under their ORIGINAL names — not an "LP_"
        // prefix — so the pursuit result IS a recognizable loadpull surface (LoadpullRecognition keys on
        // GammaLoad/ZLoad + Pout_dBm/Gt_dB/…), giving the user contours + a summary table + the per-freq
        // picker over the recommended-termination grid. The follow-on names are disjoint from the pursuit's
        // own MXP_*/MXE_*/*Count scalars, so there is no collision. The metadata (__-prefixed) cubes keep
        // their __ prefix (sweep-invariant; StackSweepAxis passes them through unstacked); the follow-on
        // __Freq is skipped (the pursuit already emits a top-level __Freq tone carrier).
        if (followOnDs is not null)
        {
            foreach (var (name, cube) in followOnDs.Cubes)
            {
                if (name.StartsWith("__", StringComparison.Ordinal))
                {
                    if (name != "__Freq" && !ds.Contains(name)) ds.Add(name, cube);
                    continue;
                }
                ds.Add(name, cube);
            }
        }

        return ds;
    }
}
