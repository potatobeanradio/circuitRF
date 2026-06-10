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
    public string          Name       { get; } = name;
    /// <summary>When false, the analysis stays configured but is skipped at run time.</summary>
    public bool            Enabled    { get; set; } = true;
    // Parametric sweep axes wrapping this analysis (unused stubs; reserved for ParametricSweepAnalysis).
    // Renamed from "Sweeps" to "SweepAxes" so SParameterAnalysis.Sweeps is unambiguous.
    public List<SweepSpec> SweepAxes  { get; } = [];
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
    public double[] Expand(IReadOnlyDictionary<string, Value>? globals = null)
    {
        var all = new SortedSet<double>();
        foreach (var seg in Sweeps)
            foreach (var f in seg.Expand(globals))
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
    /// <summary>Single-tone fundamental (Hz). Ignored when NumFreqsExpr &gt; 1.</summary>
    public string ToneExpr          { get; init; } = "0";

    // ── Multi-tone spelling ────────────────────────────────────────────────────
    /// <summary>Number of independent tones. "1" = single-tone (ToneExpr used). Default "1".</summary>
    public string NumFreqsExpr      { get; init; } = "1";
    /// <summary>
    /// Multi-tone frequencies: ToneExprs[i] is the expression for Tone[i+1] (0-based).
    /// Empty for single-tone (use ToneExpr instead).
    /// </summary>
    public string[] ToneExprs       { get; init; } = [];
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

    // Sweep: null = single point.
    public string? SweepVarName  { get; init; }
    public string? SweepStartExpr{ get; init; }
    public string? SweepStopExpr { get; init; }
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
public sealed class ParametricSweepAnalysis(
    string name,
    string sweepVarName,
    double[] sweepValues,
    string innerAnalysisName) : Analysis(name)
{
    /// <summary>The global variable to override at each sweep point.</summary>
    public string   SweepVarName      { get; } = sweepVarName;
    /// <summary>Explicit list of values to sweep over (ordered, outer→inner).</summary>
    public double[] SweepValues       { get; } = (double[])sweepValues.Clone();
    /// <summary>Name of the inner analysis (HarmonicBalanceAnalysis or another ParametricSweepAnalysis).</summary>
    public string   InnerAnalysisName { get; } = innerAnalysisName;
}

// ── Supporting types ──────────────────────────────────────────────────────────

public enum SweepKind { Linear, Log }

/// <summary>Whether a <see cref="FrequencySpec"/> segment is defined by step size or point count.</summary>
public enum FreqSpecMode { StepSize, PointCount }

public sealed class SweepSpec(string variable, double start, double stop, double step, SweepKind kind = SweepKind.Linear)
{
    public string    Variable { get; } = variable;
    public double    Start    { get; } = start;
    public double    Stop     { get; } = stop;
    public double    Step     { get; } = step;
    public SweepKind Kind     { get; } = kind;
}

/// <summary>
/// One frequency-sweep segment: Start/Stop expressed as raw expression strings (so
/// <c>stop = "2*f0"</c> works), plus either a step-size expression or a point count.
/// Call <see cref="Expand"/> to resolve expressions and produce the concrete double[] array.
/// </summary>
public sealed class FrequencySpec
{
    // ── Stored intent (what the user typed) ───────────────────────────────────
    public string       StartExpr { get; }
    public string       StopExpr  { get; }
    /// <summary>Step-size expression (Hz). Non-empty in <see cref="FreqSpecMode.StepSize"/> mode only.</summary>
    public string       StepExpr  { get; }
    /// <summary>Number of points. Non-null in <see cref="FreqSpecMode.PointCount"/> mode only. ≥ 1.</summary>
    public int?         NumPoints { get; }
    public FreqSpecMode Mode      { get; }
    public SweepKind    Kind      { get; }

    // ── StepSize constructor (expression strings) ─────────────────────────────
    public FrequencySpec(string startExpr, string stopExpr, string stepExpr,
                         SweepKind kind = SweepKind.Linear)
    {
        StartExpr = startExpr;
        StopExpr  = stopExpr;
        StepExpr  = stepExpr;
        Mode      = FreqSpecMode.StepSize;
        Kind      = kind;
    }

    // ── PointCount constructor ────────────────────────────────────────────────
    public FrequencySpec(string startExpr, string stopExpr, int numPoints,
                         SweepKind kind = SweepKind.Linear)
    {
        if (numPoints < 1)
            throw new ArgumentOutOfRangeException(nameof(numPoints), "NumPoints must be ≥ 1");
        StartExpr = startExpr;
        StopExpr  = stopExpr;
        StepExpr  = "";
        NumPoints = numPoints;
        Mode      = FreqSpecMode.PointCount;
        Kind      = kind;
    }

    // ── Backward-compat (doubles → expression strings, StepSize) ─────────────
    public FrequencySpec(double start, double stop, double step,
                         SweepKind kind = SweepKind.Linear)
        : this(start.ToString("R", CultureInfo.InvariantCulture),
               stop.ToString("R", CultureInfo.InvariantCulture),
               step.ToString("R", CultureInfo.InvariantCulture),
               kind) { }

    // ── Expand: resolve expressions → concrete freq-point array ──────────────

    /// <summary>
    /// Resolves <see cref="StartExpr"/>/<see cref="StopExpr"/>/<see cref="StepExpr"/> against
    /// <paramref name="globals"/> (may be null for pure-literal expressions) and returns the
    /// concrete double[] of frequency points this segment covers.
    /// </summary>
    public double[] Expand(IReadOnlyDictionary<string, Value>? globals = null)
    {
        double start = ResolveExpr(StartExpr, globals);
        double stop  = ResolveExpr(StopExpr,  globals);

        if (Mode == FreqSpecMode.PointCount)
        {
            int n = NumPoints!.Value;
            return Kind == SweepKind.Log
                ? LogSpace(start, stop, n)
                : LinSpace(start, stop, n);
        }

        // StepSize mode
        double step = ResolveExpr(StepExpr, globals);
        return Kind == SweepKind.Log
            ? LogStepSpace(start, stop, step)
            : LinearStepSpace(start, stop, step);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
