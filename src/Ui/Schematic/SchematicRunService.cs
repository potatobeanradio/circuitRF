using CircuitRF.Engine;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Simulate's run: reads a netlist.cnl → Elaborator → engine(s) → DataSet(s). Called from
/// WorkspaceViewModel.RunAnalysis after WriteNetlist writes the file. Never throws — engine exceptions
/// are captured into EngineError status.
///
/// <para><b>A thin caller of <see cref="CircuitEvaluation"/></b> (brief-tuneopt-2 R-to2-1), which
/// lives below the firewall so the CLI, tuning and the optimizer run the same function. What stays
/// here is the one thing the GUI adds: each analysis leaves a breadcrumb in the crash reporter's
/// trail.</para>
/// </summary>
public static class SchematicRunService
{
    /// <inheritdoc cref="CircuitEvaluation.Prepare"/>
    public static RunPlan Prepare(string netlistPath, string? baseDirectory = null,
                                  string? onlyAnalysisName = null, string? sourcePath = null)
        => CircuitEvaluation.Prepare(netlistPath, baseDirectory, onlyAnalysisName, sourcePath);

    /// <inheritdoc cref="CircuitEvaluation.Execute(RunPlan, RunControl?, Action{string}?)"/>
    public static RunResult Execute(RunPlan plan, RunControl? control = null)
        => CircuitEvaluation.Execute(plan, control, s => Diagnostics.CrashReporter.Note(s));

    /// <summary>
    /// Plan + execute in one call — the shape every caller had before the two halves were split, kept
    /// so a caller that has no use for the plan (a test, a headless driver) needs nothing new.
    /// </summary>
    public static RunResult RunNetlist(string netlistPath, string? baseDirectory = null,
                                       RunControl? control = null)
        => Execute(Prepare(netlistPath, baseDirectory), control);
}
