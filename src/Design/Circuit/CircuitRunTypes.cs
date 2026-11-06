using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Design.Circuit;

/// <summary>
/// Outcome of a <see cref="CircuitEvaluation"/> run.
/// </summary>
public enum RunStatus { Success, NoAnalysis, EngineError, Cancelled }

/// <summary>
/// A named DataSet produced by one analysis in a run.
/// </summary>
public sealed record AnalysisResult(string Name, DataSet Data);

/// <summary>
/// Whether one dispatched analysis converged. <paramref name="Converged"/> is true for a linear
/// analysis, the solver's own verdict for a single DC or HB solve, false for an analysis that failed,
/// and null where convergence is a per-point fact carried in the data (a sweep, a loadpull grid).
/// </summary>
public sealed record AnalysisConvergence(string Name, bool? Converged, string? Detail = null);

/// <summary>
/// brief-sar-9 — a fields analysis's recombined result, beside the field set: the 3D view it belongs to (the <c>.c3d</c> whose
/// run kept the fields; null when that run recorded none), the setup and frequency, and the folder. What the GUI's "Open in 3D
/// view" opens.
/// </summary>
public sealed record FieldsArtifact(string Analysis, string? C3dPath, string Setup, double FrequencyHz, string Folder, bool HasB1);

/// <summary>
/// brief-sar-10 R-sar10-2 — what a fields analysis does beyond its circuit solve, by who asked for the evaluation.
/// </summary>
public enum FieldsOutput
{
    /// <summary>Simulate's: the whole recombination, averaged SAR when the analysis asks, every volume and record written
    /// beside the field set for the 3D view; a far field's full cubes and metrics, which write nothing.</summary>
    Written,

    /// <summary>A tuning knob moving (R-sar10-3): local SAR and B1 written for the 3D view, no average — the averaged
    /// readouts wait for the knob's release, which is a <see cref="Written"/> evaluation. A far field's full cubes and metrics.</summary>
    Local,

    /// <summary>An optimizer, yield or DOE evaluation: nothing recombined and nothing written; the circuit's waves, the
    /// absorbed power from the tissue matrix, and every field measurement computed when an expression asks for it —
    /// B1 and SARwb over their region's cells only, psSAR always as the full average. A far field forms no cube: a pattern
    /// function sums it when it reads it, and an open plot keeps the file's cubes, dimmed, until the final point (brief-cosim-pattern-4).</summary>
    OnDemand,
}

/// <summary>
/// Result returned by <see cref="CircuitEvaluation.Execute"/>: status, message, and the collected
/// per-analysis results.
/// </summary>
/// <summary>A fields analysis's block that is a plain SnP: the instance and its Touchstone.</summary>
public sealed record PlainSnpFieldsBlock(string Block, string SnpPath);

public sealed class RunResult(
    RunStatus                        status,
    string                           statusMessage,
    IReadOnlyList<AnalysisResult>?   results  = null,
    IReadOnlyList<string>?           warnings = null,
    DataSet?                         grouped  = null,
    IReadOnlyList<string>?           notes    = null,
    IReadOnlyList<AnalysisConvergence>? convergence = null,
    IReadOnlyList<ExpressionOutcome>?   expressions = null,
    IReadOnlyList<FieldsArtifact>?      fields = null)
{
    /// <summary>brief-cosim-pattern-2 R-cp2-8 — each plain SnP a fields analysis was refused on, so a window can offer the change beside
    /// the sentence the run already reports.</summary>
    public IReadOnlyList<PlainSnpFieldsBlock> PlainSnpFieldsBlocks { get; init; } = [];

    /// <summary>The analyses that failed in a run where others finished, one sentence each. Such a run is still
    /// <see cref="RunStatus.Success"/>, since it has results to publish; <see cref="StatusMessage"/> carries these too.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>What the run's own row says: <see cref="StatusMessage"/> without the sentences of <see cref="Errors"/>, which a
    /// window posts on rows of their own.</summary>
    public string Summary { get => _summary ?? StatusMessage; init => _summary = value; }
    private readonly string? _summary;

    /// <summary>brief-sar-9 — every fields analysis's recombined result, in run order.</summary>
    public IReadOnlyList<FieldsArtifact> Fields { get; } = fields ?? [];

    public RunStatus                       Status        { get; } = status;
    public string                          StatusMessage { get; } = statusMessage;
    public IReadOnlyList<AnalysisResult>   Results       { get; } = results ?? [];

    /// <summary>
    /// Elaboration and engine run-time warnings drained from
    /// <see cref="ElaboratedNetlist.Warnings"/>.
    /// Non-empty even on <see cref="RunStatus.EngineError"/> when the run partially succeeded.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; } = warnings ?? [];

    /// <summary>
    /// What the run WORKED OUT and is reporting — drained from
    /// <see cref="ElaboratedNetlist.Notes"/>. Rendered at Info, because a
    /// resolution is not a complaint: a run that resolved everything correctly must not read as a
    /// run with problems, or the warnings that do need attention are harder to pick out for it.
    /// </summary>
    public IReadOnlyList<string> Notes { get; } = notes ?? [];

    /// <summary>
    /// One grouped DataSet for the whole run (one group per analysis + "measurements" group).
    /// Null when no analyses produced results.
    /// </summary>
    public DataSet? GroupedResults { get; } = grouped;

    /// <summary>One entry per dispatched analysis, in run order.</summary>
    public IReadOnlyList<AnalysisConvergence> Convergence { get; } = convergence ?? [];

    /// <summary>The request's extra expressions, evaluated in the measurement scope, in the order
    /// asked. Empty when none were asked for, or when the run produced no results to evaluate
    /// them against.</summary>
    public IReadOnlyList<ExpressionOutcome> Expressions { get; } = expressions ?? [];

    /// <summary>This result with <paramref name="leading"/> ahead of its own notes.</summary>
    public RunResult WithLeadingNotes(IReadOnlyList<string> leading)
        => new(Status, StatusMessage, Results, Warnings, GroupedResults, [.. leading, .. Notes],
               Convergence, Expressions, Fields) { Errors = Errors, Summary = Summary };

    // Convenience: callers that only need the DataSets (unchanged from Phase 6e).
    public IReadOnlyList<DataSet> DataSets => Results.Select(r => r.Data).ToList();
}

/// <summary>
/// One analysis the run is going to dispatch, worked out by <see cref="CircuitEvaluation.Plan"/>
/// before anything runs.
/// <para/>
/// <see cref="SelfTicks"/> distinguishes an engine that reports its OWN progress (a sweep per point,
/// an s-parameter per frequency, a loadpull per grid termination) from one that does not (a single HB
/// or DC solve, a pursuit search whose query count is decided by the search). The executor ticks
/// <see cref="WorkUnits"/> itself for the latter, so a run's total is reached either way.
/// </summary>
internal sealed record PlannedAnalysis(
    Analysis? Typed,
    string?   RawLine,
    string    ResultName,
    long      WorkUnits,
    bool      SelfTicks)
{
    /// <summary>brief-sdl-6 — the bench the chain runs on when it runs by EM; null when by circuit models.</summary>
    public CircuitRF.Design.Layout.Em.ParametricEm.EmSwitched? Em { get; init; }

    /// <summary>Why a chain asked to run by EM cannot be — reported as that analysis's error when it runs.</summary>
    public string? EmRefusal { get; init; }
}

/// <summary>
/// What a run WILL do, worked out before any of it runs: the netlist read, elaborated once, and every
/// analysis that is going to be dispatched described in run order.
/// <para/>
/// <b>This exists so the user can read the plan and stop a wrong one before paying for it.</b> A
/// nested sweep can be tens of thousands of points and tens of minutes; reporting "11 pt(s) over VGS x
/// 101 pt(s) over VDS = 1,111 total pt(s)" only after those points have all been simulated is a
/// receipt, not a decision the user can act on.
/// <para/>
/// A failed plan (unreadable netlist, failed elaboration, nothing to run) carries its own status and
/// message; <see cref="CircuitEvaluation.Execute"/> passes it straight through, so a caller reports
/// exactly one failure whichever half produced it.
/// </summary>
public sealed class RunPlan
{
    public RunStatus Status        { get; }
    public string    StatusMessage { get; }

    /// <summary>One line per analysis that will run, in run order — the pre-flight description.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>
    /// What the PLAN worked out and is reporting, as opposed to what it will run: the sentence a
    /// narrowed run writes when the named card was promoted to the sweep wrapping it, and one line per
    /// tunable key that resolved to nothing. Separate from <see cref="Lines"/> because that list is
    /// one entry per dispatched analysis and a caller counts it.
    /// </summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>Total leaf work units across every planned analysis. 0 = nothing countable.</summary>
    public long TotalWorkUnits { get; }

    /// <summary>Points across every parametric sweep the plan dispatches (the whole chain of each);
    /// 0 when none. What a live tuning session warns about before each slider move costs a sweep
    /// (brief-tuneopt-3 R-to3-4).</summary>
    public long SweepPoints { get; init; }

    internal Library?          Lib           { get; init; }
    internal TestBench?        Tb            { get; init; }
    internal ElaboratedNetlist? Nl           { get; init; }
    internal string?           BaseDirectory { get; init; }
    internal IReadOnlyList<PlannedAnalysis> Analyses { get; init; } = [];

    /// <summary>brief-sdl-6 — whether any analysis this plan dispatches runs by EM (or was asked to and cannot): such a
    /// run is counted before it starts (D5).</summary>
    public bool HasEm => Analyses.Any(a => a.Em is not null || a.EmRefusal is not null);

    internal RunPlan(RunStatus status, string message,
                     IReadOnlyList<string>? lines = null, long totalWorkUnits = 0,
                     IReadOnlyList<string>? notes = null)
    {
        Status         = status;
        StatusMessage  = message;
        Lines          = lines ?? [];
        Notes          = notes ?? [];
        TotalWorkUnits = totalWorkUnits;
    }
}
