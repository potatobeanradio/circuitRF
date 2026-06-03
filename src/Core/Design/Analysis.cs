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

    // Sweep: null = single point.
    public string? SweepVarName  { get; init; }
    public string? SweepStartExpr{ get; init; }
    public string? SweepStopExpr { get; init; }
    public string? SweepStepExpr { get; init; }
}

public sealed class LoadpullAnalysis(string name, PortRef dut, TerminationGrid grid)
    : Analysis(name)
{
    public PortRef          Dut      { get; } = dut;
    public TerminationGrid  Grid     { get; } = grid;
    public List<HarmonicTermination> Harmonics { get; } = [];
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

public sealed class ToneSpec(string frequencyExpression, string powerVariable)
{
    public string FrequencyExpression { get; } = frequencyExpression;
    public string PowerVariable       { get; } = powerVariable;
}

public sealed class PortRef(string path)
{
    public string Path { get; } = path;
}

public abstract class TerminationGrid(System.Numerics.Complex z0)
{
    public System.Numerics.Complex Z0 { get; } = z0;
}

public sealed class GammaGrid(System.Numerics.Complex z0) : TerminationGrid(z0);
public sealed class ImpedanceGrid(System.Numerics.Complex z0) : TerminationGrid(z0);

public sealed class HarmonicTermination(int harmonic, System.Numerics.Complex gamma)
{
    public int                     Harmonic { get; } = harmonic;
    public System.Numerics.Complex Gamma    { get; } = gamma;
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
