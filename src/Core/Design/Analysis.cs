using System;
using System.Collections.Generic;
using System.Globalization;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Design;

// ── Analysis hierarchy (data-model §4) ───────────────────────────────────────
// Typed shapes exist for Phase 2; the Phase-1 .cnl reader does NOT populate them —
// it fills RawDirective records instead. Types defined here so the design-layer
// shape is correct when the directive grammar is settled.

public abstract class Analysis(string name)
{
    public string Name    { get; } = name;
    /// <summary>When false, the analysis stays configured but is skipped at run time.</summary>
    public bool   Enabled { get; set; } = true;
}

public sealed class DcAnalysis(string name) : Analysis(name);

/// <summary>
/// S-parameter analysis. Carries one or more frequency-sweep segments; at engine time all
/// segments are unioned into a single sorted/deduped flat frequency array via <see cref="Expand"/>.
/// </summary>
public sealed class SParameterAnalysis : Analysis
{
    /// <summary>Ordered list of frequency-sweep segments. At least one segment is required.</summary>
    public IReadOnlyList<FrequencySpec> Sweeps { get; }

    // ── Single-segment convenience constructor (backward compat) ──────────────
    public SParameterAnalysis(string name, FrequencySpec freq) : base(name)
        => Sweeps = [freq];

    // ── Multi-segment constructor ─────────────────────────────────────────────
    public SParameterAnalysis(string name, IReadOnlyList<FrequencySpec> sweeps) : base(name)
    {
        if (sweeps.Count < 1)
            throw new ArgumentException("SParameterAnalysis requires at least one FrequencySpec.", nameof(sweeps));
        Sweeps = sweeps;
    }

    // ── Backward-compat: single-segment via the old Freq property ─────────────
    /// <summary>Returns the first segment. Use <see cref="Sweeps"/> when there may be multiple.</summary>
    public FrequencySpec Freq => Sweeps[0];

    // ── Whole-analysis expand: union all segments into one sorted/deduped array ─
    /// <summary>
    /// Expands all <see cref="Sweeps"/> segments, unions their points, and returns a single
    /// sorted, deduplicated <c>double[]</c> — the flat frequency array the engine expects.
    /// </summary>
    public double[] Expand(IReadOnlyDictionary<string, Value>? globals = null,
                           IReadOnlyCollection<string>? globalsWithUnit = null)
    {
        var all = new SortedSet<double>();
        foreach (var seg in Sweeps)
            foreach (var f in seg.Expand(globals, globalsWithUnit))
                all.Add(f);
        return [.. all];
    }
}

/// <summary>
/// HB analysis directive (harmonic-balance.md §3.2).
/// Key=value fields store raw expression strings (resolved later via ResolvedGlobals).
///
/// Two spellings — one model (harmonic-balance.md §3.2):
///   Single-tone:  Tone=f0  MaxHarm=K
///   Multi-tone:   NumFreqs=N  Tone[1]=f1 … Tone[N]=fN  MaxMixOrder=M  MaxHarm=K
/// </summary>
public sealed class HarmonicBalanceAnalysis(string name) : Analysis(name)
{
    // Raw expression strings from the .cnl directive; resolved at engine time.

    // ── Single-tone spelling ───────────────────────────────────────────────────
    /// <summary>Single-tone fundamental raw expression (not baked to Hz). Ignored when NumFreqsExpr &gt; 1.</summary>
    public string ToneExpr          { get; init; } = "0";
    /// <summary>Unit for ToneExpr. Default "Hz" for back-compatibility (field unit × eval = Hz value).</summary>
    public string ToneUnit          { get; init; } = "Hz";

    // ── Multi-tone spelling ────────────────────────────────────────────────────
    /// <summary>Number of independent tones. "1" = single-tone (ToneExpr used). Default "1".</summary>
    public string NumFreqsExpr      { get; init; } = "1";
    /// <summary>
    /// Multi-tone frequencies: ToneExprs[i] is the raw expression for Tone[i+1] (0-based).
    /// Empty for single-tone (use ToneExpr instead).
    /// </summary>
    public string[] ToneExprs       { get; init; } = [];
    /// <summary>Units for ToneExprs (parallel). Missing/short entries default "Hz".</summary>
    public string[] ToneUnits       { get; init; } = [];
    /// <summary>Diamond mixing-order bound |k₁|+|k₂| ≤ MaxMixOrder (§6). Multi-tone only.</summary>
    public string MaxMixOrderExpr   { get; init; } = "5";

    // ── Common ─────────────────────────────────────────────────────────────────
    public string MaxHarmonicExpr   { get; init; } = "7";
    public string FFTOverSampleExpr { get; init; } = "1";
    public string TolExpr           { get; init; } = "1e-6";
    public string DriveSteppingExpr { get; init; } = "IfNecessary";
    public string GuardHarmonicExpr { get; init; } = "0";
    /// <summary>Newton step damping factor λ ∈ (0,1]. 1 = full Newton step (default). B2.</summary>
    public string LambdaExpr        { get; init; } = "1";
    /// <summary>Max Newton iterations per HB solve before continuation backoff. Default 100.</summary>
    public string MaxIterExpr       { get; init; } = "100";

    // Sweep fields — deprecated. Use ParametricSweepAnalysis to sweep HB.
    // Retained as init-only for .cnl back-compat read; the engine ignores them.
    [Obsolete("Deprecated — wrap the HB analysis in a ParametricSweepAnalysis to sweep. " +
              "Retained for .cnl read compatibility; not used by the engine.")]
    public string? SweepVarName  { get; init; }
    [Obsolete("Deprecated — wrap the HB analysis in a ParametricSweepAnalysis to sweep. " +
              "Retained for .cnl read compatibility; not used by the engine.")]
    public string? SweepStartExpr{ get; init; }
    [Obsolete("Deprecated — wrap the HB analysis in a ParametricSweepAnalysis to sweep. " +
              "Retained for .cnl read compatibility; not used by the engine.")]
    public string? SweepStopExpr { get; init; }
    [Obsolete("Deprecated — wrap the HB analysis in a ParametricSweepAnalysis to sweep. " +
              "Retained for .cnl read compatibility; not used by the engine.")]
    public string? SweepStepExpr { get; init; }
}

/// <summary>
/// Loadpull analysis directive (loadpull.md §2.1).
/// Key=value fields store raw expression strings (resolved at engine time).
/// </summary>
public sealed class LoadpullAnalysis(string name) : Analysis(name)
{
    // ── Required ───────────────────────────────────────────────────────────────
    public string ToneExpr        { get; init; } = "0";
    /// <summary>Unit for ToneExpr. Default "Hz" for back-compatibility (field unit × eval = Hz value).
    /// Resolved via the same var-unit-wins rule HB uses (FreqUnit.ResolveHz).</summary>
    public string ToneUnit        { get; init; } = "Hz";
    public string LoadTunerName   { get; init; } = "";   // instance name of the load Tuner
    public string SourceTunerName { get; init; } = "";   // instance name of the source Tuner
    public string GridPath        { get; init; } = "";   // path to .gam grid file (required)
    public string PinStartExpr    { get; init; } = "-20";
    public string PinMaxExpr      { get; init; } = "10"; // safety cap — always required

    // ── Optional with defaults ─────────────────────────────────────────────────
    public string MaxHarmonicExpr   { get; init; } = "5";
    public string SweepExpr         { get; init; } = "Load";   // "Load" or "Source"
    public string TuneHarmExpr      { get; init; } = "1";
    public string CompressionExpr   { get; init; } = "3";
    public string GainTypeExpr      { get; init; } = "Gt";     // "Gt" or "Gp"
    public string PinStepExpr       { get; init; } = "1";
    public string TickleExpr        { get; init; } = "-50";    // dBm; "off" to disable
    public string MaxIterExpr       { get; init; } = "100";
    public string FFTOverSampleExpr { get; init; } = "1";
    public string TolExpr           { get; init; } = "1e-6";
    public string DriveSteppingExpr { get; init; } = "IfNecessary";
    public string GuardHarmonicExpr { get; init; } = "0";

    // Optional: source directory for resolving relative Grid paths (set by reader).
    public string? SourceDirectory  { get; init; }
}

/// <summary>
/// Resolved directive for a loadpull_pursuit analysis (Phase 4b-2).
/// All loadpull keys except Grid, plus pursuit-specific keys.
/// loadpull_pursuit.md §3.
/// </summary>
public sealed class LoadpullPursuitAnalysis(string name) : Analysis(name)
{
    // ── Shared with LoadpullAnalysis (no Grid) ────────────────────────────────
    public string ToneExpr          { get; init; } = "0";
    /// <summary>Unit for ToneExpr. Default "Hz" for back-compatibility. Resolved via the same
    /// var-unit-wins rule HB uses (FreqUnit.ResolveHz).</summary>
    public string ToneUnit          { get; init; } = "Hz";
    public string LoadTunerName     { get; init; } = "";
    public string SourceTunerName   { get; init; } = "";
    public string PinStartExpr      { get; init; } = "-20";
    public string PinMaxExpr        { get; init; } = "10";
    public string MaxHarmonicExpr   { get; init; } = "5";
    public string SweepExpr         { get; init; } = "Load";
    public string TuneHarmExpr      { get; init; } = "1";
    public string CompressionExpr   { get; init; } = "3";
    public string GainTypeExpr      { get; init; } = "Gt";
    public string PinStepExpr       { get; init; } = "1";
    public string TickleExpr        { get; init; } = "-50";
    public string MaxIterExpr       { get; init; } = "100";
    public string FFTOverSampleExpr { get; init; } = "1";
    public string TolExpr           { get; init; } = "1e-6";
    public string DriveSteppingExpr { get; init; } = "IfNecessary";
    public string GuardHarmonicExpr { get; init; } = "0";

    // ── Pursuit-specific keys ─────────────────────────────────────────────────
    public string EffTypeExpr                { get; init; } = "DE";            // "DE" or "PAE"
    public string ZsourceOBOExpr             { get; init; } = "5";             // dB backoff
    public string SearchMethodExpr           { get; init; } = "SteepestAscent"; // SearchMethod enum name
    public string? OutputGridPath            { get; init; }             // null = no file
    public string Vswr1Expr                  { get; init; } = "1.5";
    public string Vswr1ResolutionExpr        { get; init; } = "4";
    public string Vswr2Expr                  { get; init; } = "3";
    public string Vswr2ResolutionExpr        { get; init; } = "4";
    public string KeepNonconvergingExpr      { get; init; } = "false";
    public string NonconvergentVswrExpr      { get; init; } = "1.05";
    public string CreateLoadpullResultExpr   { get; init; } = "true";  // default on
    public string LoadpullResultZsourceExpr  { get; init; } = "MXE";   // MXE | MXP | None

    public string? SourceDirectory           { get; init; }
}

/// <summary>
/// Composable parametric sweep that wraps an inner analysis (or another parametric sweep).
/// Each nesting level prepends one named axis to every cube in the resulting DataSet.
/// </summary>
public sealed class ParametricSweepAnalysis : Analysis
{
    /// <summary>The global variable to override at each sweep point.</summary>
    public string    SweepVarName      { get; }
    /// <summary>Expanded sweep values (ordered, outer→inner). Always populated.</summary>
    public double[]  SweepValues       { get; }
    /// <summary>Name of the inner analysis (HarmonicBalanceAnalysis or another ParametricSweepAnalysis).</summary>
    public string    InnerAnalysisName { get; }
    /// <summary>
    /// Compact spec used to build <see cref="SweepValues"/>. Non-null only when the sweep was
    /// defined via Start/Stop/Step or Start/Stop/Npts (not an explicit values list). Preserved so
    /// the .cnl writer can re-emit the compact form on round-trip.
    /// </summary>
    public SweepSpec? Spec             { get; }

    /// <summary>Array constructor — used when values are specified as an explicit list.</summary>
    public ParametricSweepAnalysis(string name, string sweepVarName,
                                   double[] sweepValues, string innerAnalysisName)
        : base(name)
    {
        SweepVarName      = sweepVarName;
        SweepValues       = (double[])sweepValues.Clone();
        InnerAnalysisName = innerAnalysisName;
    }

    /// <summary>
    /// Spec constructor — used when the sweep is defined via Start/Stop/Step or Start/Stop/Npts.
    /// Expands <paramref name="spec"/> into <see cref="SweepValues"/> at construction time and
    /// retains the spec for round-trip .cnl emission.
    /// </summary>
    public ParametricSweepAnalysis(string name, string sweepVarName,
                                   SweepSpec spec, string innerAnalysisName)
        : base(name)
    {
        SweepVarName      = sweepVarName;
        Spec              = spec;
        // Apply unit multiplier so SweepValues are always in base units.
        // Start and Stop are always scaled; StepOrCount is scaled only in StepSize mode
        // (in PointCount mode the count is dimensionless and must not be scaled).
        double m = Units.Scale(spec.Unit) ?? 1.0;
        SweepValues       = SweepExpander.ExpandSweep(
                                spec.Start * m,
                                spec.Stop  * m,
                                spec.Mode == SweepAxisMode.StepSize ? spec.StepOrCount * m : spec.StepOrCount,
                                spec.Mode, spec.Kind);
        InnerAnalysisName = innerAnalysisName;
    }
}

// ── Supporting types ──────────────────────────────────────────────────────────

public enum SweepKind { Linear, Log }

/// <summary>Whether a <see cref="FrequencySpec"/> segment is defined by step size or point count.</summary>
public enum FreqSpecMode { StepSize, PointCount }

/// <summary>
/// Compact spec for a parametric sweep defined by Start/Stop/StepOrCount (not an explicit list).
/// Stored on <see cref="ParametricSweepAnalysis.Spec"/> so the .cnl writer can re-emit the
/// compact Start/Stop/Step or Start/Stop/Npts form on round-trip.
/// </summary>
public sealed class SweepSpec(double start, double stop, double stepOrCount,
                               SweepAxisMode mode, SweepKind kind = SweepKind.Linear,
                               string unit = "")
{
    public double        Start        { get; } = start;
    public double        Stop         { get; } = stop;
    /// <summary>Step size (StepSize mode) or point count (PointCount mode).</summary>
    public double        StepOrCount  { get; } = stepOrCount;
    /// <summary>StepSize or PointCount (never List — use explicit array for list sweeps).</summary>
    public SweepAxisMode Mode         { get; } = mode;
    public SweepKind     Kind         { get; } = kind;
    /// <summary>
    /// General unit for Start/Stop/Step coefficients (empty = base units, scale 1).
    /// Applied at materialization so <see cref="ParametricSweepAnalysis.SweepValues"/> are always in base units.
    /// </summary>
    public string        Unit         { get; } = unit;
}

/// <summary>
/// One frequency-sweep segment: Start/Stop expressed as raw expression strings (so
/// <c>stop = "2*f0"</c> works), plus either a step-size expression or a point count.
/// Call <see cref="Expand"/> to resolve expressions and produce the concrete double[] array.
/// </summary>
public sealed class FrequencySpec
{
    // ── Stored intent (what the user typed) ───────────────────────────────────
    public string       StartExpr  { get; }
    public string       StopExpr   { get; }
    /// <summary>Step-size raw expression. Non-empty in <see cref="FreqSpecMode.StepSize"/> mode only.</summary>
    public string       StepExpr   { get; }
    /// <summary>Number of points. Non-null in <see cref="FreqSpecMode.PointCount"/> mode only. ≥ 1.</summary>
    public int?         NumPoints  { get; }
    public FreqSpecMode Mode       { get; }
    public SweepKind    Kind       { get; }
    /// <summary>Unit for StartExpr. Default "Hz" for back-compatibility.</summary>
    public string       StartUnit  { get; }
    /// <summary>Unit for StopExpr. Default "Hz" for back-compatibility.</summary>
    public string       StopUnit   { get; }
    /// <summary>Unit for StepExpr. Default "Hz" for back-compatibility.</summary>
    public string       StepUnit   { get; }

    // ── StepSize constructor (expression strings) ─────────────────────────────
    public FrequencySpec(string startExpr, string stopExpr, string stepExpr,
                         SweepKind kind = SweepKind.Linear,
                         string startUnit = "Hz", string stopUnit = "Hz", string stepUnit = "Hz")
    {
        StartExpr = startExpr;
        StopExpr  = stopExpr;
        StepExpr  = stepExpr;
        Mode      = FreqSpecMode.StepSize;
        Kind      = kind;
        StartUnit = startUnit;
        StopUnit  = stopUnit;
        StepUnit  = stepUnit;
    }

    // ── PointCount constructor ────────────────────────────────────────────────
    public FrequencySpec(string startExpr, string stopExpr, int numPoints,
                         SweepKind kind = SweepKind.Linear,
                         string startUnit = "Hz", string stopUnit = "Hz")
    {
        if (numPoints < 1)
            throw new ArgumentOutOfRangeException(nameof(numPoints), "NumPoints must be ≥ 1");
        StartExpr = startExpr;
        StopExpr  = stopExpr;
        StepExpr  = "";
        NumPoints = numPoints;
        Mode      = FreqSpecMode.PointCount;
        Kind      = kind;
        StartUnit = startUnit;
        StopUnit  = stopUnit;
        StepUnit  = "Hz";
    }

    // ── Backward-compat (doubles → expression strings, StepSize) ─────────────
    public FrequencySpec(double start, double stop, double step,
                         SweepKind kind = SweepKind.Linear)
        : this(start.ToString("R", CultureInfo.InvariantCulture),
               stop.ToString("R", CultureInfo.InvariantCulture),
               step.ToString("R", CultureInfo.InvariantCulture),
               kind) { }   // units default "Hz": inputs are already in Hz

    // ── Expand: resolve expressions → concrete freq-point array ──────────────

    /// <summary>
    /// Resolves Start/Stop/Step expressions against <paramref name="globals"/> — applying
    /// each field's unit via <see cref="FreqUnit.ResolveHz"/> with the var-unit-wins rule —
    /// and returns the concrete double[] of frequency points this segment covers.
    /// </summary>
    public double[] Expand(IReadOnlyDictionary<string, Value>? globals = null,
                           IReadOnlyCollection<string>? globalsWithUnit = null)
    {
        double start = ResolveFreqHz(StartExpr, StartUnit, globals, globalsWithUnit);
        double stop  = ResolveFreqHz(StopExpr,  StopUnit,  globals, globalsWithUnit);

        if (Mode == FreqSpecMode.PointCount)
        {
            int n = NumPoints!.Value;
            return Kind == SweepKind.Log
                ? LogSpace(start, stop, n)
                : LinSpace(start, stop, n);
        }

        double step = ResolveFreqHz(StepExpr, StepUnit, globals, globalsWithUnit);
        return Kind == SweepKind.Log
            ? LogStepSpace(start, stop, step)
            : LinearStepSpace(start, stop, step);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Resolve a frequency expression to Hz using FreqUnit when globals are available.
    // Falls back to the legacy numeric path (× unit multiplier) when globals is null or throws.
    private static double ResolveFreqHz(string expr, string unit,
        IReadOnlyDictionary<string, Value>? globals, IReadOnlyCollection<string>? globalsWithUnit)
    {
        if (globals is not null)
        {
            try { return FreqUnit.ResolveHz(expr, unit, globals, globalsWithUnit); }
            catch { }
        }
        // Legacy / fallback: resolve numerically and apply the field unit multiplier.
        return ResolveExpr(expr, globals) * FreqUnit.Multiplier(unit);
    }

    private static double ResolveExpr(string expr, IReadOnlyDictionary<string, Value>? globals)
    {
        // Fast path: plain numeric literal (covers the common "1e9", "1000000000" cases)
        if (double.TryParse(expr,
                NumberStyles.Float | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var d))
            return d;

        // Full expression path: bind Real globals into a scope and evaluate
        var scope = new Scope("freq");
        if (globals is not null)
            foreach (var (k, v) in globals)
                if (v.Kind == ValueKind.Real)
                    scope.Bind(k, v.AsReal().ToString("R", CultureInfo.InvariantCulture));

        return new Evaluator().Eval(expr, scope).AsReal();
    }

    // N points linearly spaced [start, stop]
    private static double[] LinSpace(double start, double stop, int n)
    {
        if (n == 1) return [start];
        var pts = new double[n];
        for (int i = 0; i < n; i++)
            pts[i] = start + (stop - start) * i / (n - 1);
        return pts;
    }

    // N points log-spaced [start, stop]
    private static double[] LogSpace(double start, double stop, int n)
    {
        if (n == 1) return [start];
        var pts = new double[n];
        double logRatio = Math.Log10(stop / start);
        for (int i = 0; i < n; i++)
            pts[i] = start * Math.Pow(10, logRatio * i / (n - 1));
        return pts;
    }

    // Linear additive step sweep
    private static double[] LinearStepSpace(double start, double stop, double step)
    {
        if (step <= 0) step = (stop - start) / 100.0;
        var list = new List<double>();
        for (double f = start; f <= stop + step * 1e-9; f += step)
            list.Add(f);
        return [.. list];
    }

    // Log sweep: step = multiplicative ratio per step (> 1). If ≤ 1, falls back to 100 log-pts.
    private static double[] LogStepSpace(double start, double stop, double step)
    {
        if (step <= 1.0) return LogSpace(start, stop, 100);
        var list = new List<double>();
        for (double f = start; f <= stop * (1.0 + 1e-9); f *= step)
            list.Add(f);
        return [.. list];
    }
}

// ── Measurement (declared on TestBench) ──────────────────────────────────────

/// <summary>
/// A performance expression evaluated against result cubes after simulation.
/// The grammar for measurement expressions is Phase 2+; stored as a raw string here.
/// </summary>
public sealed class Measurement(string name, string expression, string? unit = null)
{
    public string  Name       { get; } = name;
    public string  Expression { get; } = expression;
    public string? Unit       { get; } = unit;
}
