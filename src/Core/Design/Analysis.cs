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
/// </summary>
public sealed class HarmonicBalanceAnalysis(string name) : Analysis(name)
{
    // Raw expression strings from the .cnl directive; resolved at engine time.
    public string ToneExpr          { get; init; } = "0";
    public string MaxHarmonicExpr   { get; init; } = "7";
    public string FFTOverSampleExpr { get; init; } = "1";
    public string TolExpr           { get; init; } = "1e-6";
    public string DriveSteppingExpr { get; init; } = "IfNecessary";
    public string GuardHarmonicExpr { get; init; } = "0";
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
