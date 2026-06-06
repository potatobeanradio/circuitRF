namespace CircuitRF.Core.Design;

// ── Analysis hierarchy (data-model §4) ───────────────────────────────────────
// Typed shapes exist for Phase 2; the Phase-1 .cnl reader does NOT populate them —
// it fills RawDirective records instead. Types defined here so the design-layer
// shape is correct when the directive grammar is settled.

public abstract class Analysis(string name)
{
    public string          Name   { get; } = name;
    public List<SweepSpec> Sweeps { get; } = [];
}

public sealed class DcAnalysis(string name) : Analysis(name);

public sealed class SParameterAnalysis(string name, FrequencySpec freq) : Analysis(name)
{
    public FrequencySpec Freq { get; } = freq;
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

// ── Supporting types ──────────────────────────────────────────────────────────

public enum SweepKind { Linear, Log }

public sealed class SweepSpec(string variable, double start, double stop, double step, SweepKind kind = SweepKind.Linear)
{
    public string    Variable { get; } = variable;
    public double    Start    { get; } = start;
    public double    Stop     { get; } = stop;
    public double    Step     { get; } = step;
    public SweepKind Kind     { get; } = kind;
}

public sealed class FrequencySpec(double start, double stop, double step, SweepKind kind = SweepKind.Linear)
{
    public double    Start { get; } = start;
    public double    Stop  { get; } = stop;
    public double    Step  { get; } = step;
    public SweepKind Kind  { get; } = kind;
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
