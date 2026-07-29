namespace CircuitRF.Core.Devices;

/// <summary>
/// Implemented by a <see cref="ComponentModel"/> that accumulates non-fatal validity/informational
/// messages during <see cref="ComponentModel.Stamp"/> (brief-mklopf-performance-and-messages.md
/// R-mk-7/R-mk-8).
///
/// A component model has no reference to the <c>ElaboratedNetlist</c> its own warnings need to
/// reach — <c>Stamp(IMnaContext, ElaboratedComponent, double)</c> carries neither. The engine,
/// which stamps every component and DOES hold the netlist throughout its own frequency loop, is
/// therefore the only place that can drain a model's queued messages into
/// <c>ElaboratedNetlist.AddWarningOnce</c> (and from there into the Messages UI, via the existing,
/// already-tested <c>SchematicRunService</c>/<c>WorkspaceViewModel.RunAnalysis</c> pipeline — see
/// <c>src/Ui/CLAUDE.md</c>'s "Engine diagnostics channel" note). This is called once, right after
/// every <c>ec.Model.Stamp(...)</c> call site in the engine (<c>NonlinearDcEngine</c>,
/// <c>SParameterEngine</c>, <c>HbLinearExtractor</c>) — see <c>ElaboratedNetlist.DrainModelWarnings</c>.
///
/// <b>R-mk-8's finding, recorded here since this interface exists to fix it:</b> before this brief,
/// <c>MicrostripValidityReporter</c> wrote every message directly to <c>Console.Error</c> — there
/// was no path from it (or from <c>MicrostripKlopfModel</c>'s own two direct warnings) into
/// <c>ElaboratedNetlist.Warnings</c> at all. The reporter itself was the bug, not merely these two
/// call sites; fixing <c>MicrostripValidityReporter</c> to queue instead of print, and having every
/// microstrip model that holds one implement this interface, repairs every microstrip validity
/// warning in one pass.
/// </summary>
public interface IReportsWarnings
{
    /// <summary>Returns and clears every message queued since the last drain. Each entry's
    /// <c>Key</c> is passed straight through to <c>ElaboratedNetlist.AddWarningOnce</c>'s own
    /// per-run dedup, so "once per distinct violation" holds across the whole pipeline — not only
    /// within this model's own per-instance reporter.</summary>
    IReadOnlyList<(string Key, string Message)> DrainWarnings();
}
