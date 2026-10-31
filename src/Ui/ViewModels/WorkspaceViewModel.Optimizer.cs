using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircuitRF.Core.Design;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Optimization;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Smith;
using Dock.Model.Core;
using RfCore.Data;

namespace CircuitRF.Ui.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
//  The Optimizer panel (brief-tuneopt-10). This file decides which schematic the panel optimizes —
//  the Tuning panel's rule — and hands it what it cannot reach itself: the bench prepared exactly as
//  Simulate would netlist it (the Tuning session's own preparation), the display a run publishes to
//  under the schematic's results path, the sessions a Push writes into, the Tuning panel for "Send to
//  Tuning", and the goal editor's templates and preview. The panel is OptimizerPanelViewModel.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WorkspaceViewModel
{
    private OptimizerPanelViewModel? _wiredOptimizerPanel;
    private OptimizeSurfaceRelay?    _optimizeSurface;

    /// <summary>What every schematic session's Inspector asks about optimizing — forwarded to the current Optimizer
    /// panel, so a layout rebuild that replaces the panel does not strand older sessions.</summary>
    internal IOptimizeSurface OptimizeSurface => _optimizeSurface ??= new OptimizeSurfaceRelay(() => _factory.OptimizerTool?.Panel);

    /// <summary>The Tuning panel's rule (<see cref="RouteTuningPanel"/>): a schematic sets the panel, any
    /// other document keeps the last one, and only closing it empties the panel.</summary>
    private void RouteOptimizerPanel(IDockable? document)
    {
        WireOptimizerPanel();
        if (_factory.OptimizerTool?.Panel is not { } panel) return;

        if (document is SchematicDocument sd)
            panel.SetActiveSchematic(sd.NavFrames[0].Session, InstancesRootHeaderOf(sd));
        else if (!panel.HasSchematic && _lastActiveSchematicDoc is { } kept)
            panel.SetActiveSchematic(kept.NavFrames[0].Session, InstancesRootHeaderOf(kept));
    }

    private void ClearOptimizerPanel()
    {
        WireOptimizerPanel();
        _factory.OptimizerTool?.Panel.SetActiveSchematic(null, null);
    }

    private void WireOptimizerPanel()
    {
        var panel = _factory.OptimizerTool?.Panel;
        if (ReferenceEquals(panel, _wiredOptimizerPanel) || panel is null) return;
        _wiredOptimizerPanel = panel;

        panel.Defer             = a => Avalonia.Threading.Dispatcher.UIThread.Post(a, Avalonia.Threading.DispatcherPriority.Background);
        panel.PostToUi          = a => Avalonia.Threading.Dispatcher.UIThread.Post(a);
        panel.EditCommitted    += OnAnalysesEditCommitted;   // ⌘Z with the panel focused (Tuning's rule)
        panel.PrepareCircuit    = tuned => PrepareTunedCircuit(tuned)?.Circuit;
        panel.DisplayFor        = OptimizerDisplayFor;
        panel.SessionForDrawing = d => SessionForTunedDrawing(d, openTab: true);
        panel.Ladders           = () => SmithPreferredValueStore.Ladders;
        panel.GoalContext       = GoalContextFor;
        panel.PreviewSource     = GoalPreviewFor;
        panel.SendToTuningTarget = SendToTuning;
        panel.TakingDisplay     = tuned => _factory.TuningTool?.Panel.EndSessionFor(tuned, "the Optimizer is running");
        panel.ReportProblem     = text => Messages.Error($"Optimizer: {text}");   // issue #33
        panel.ReportMessages    = (summary, lines) =>
        {
            Messages.Info(summary);
            foreach (var line in lines) Messages.Info("  " + line);
        };
    }

    /// <summary>The run's display: every open Data Display, under the schematic's own results path, with
    /// the Optimizing chip (R-to10-7).</summary>
    private IOptimizerDisplay? OptimizerDisplayFor(SchematicViewModel tuned)
    {
        if (!FindSchematicDocumentWithFrame(tuned, out var doc, out _)) return null;
        var key = RunResultsWriter.SchematicKey(doc.FilePath, doc.Id);
        return TuneSinkFor(RunBaseDirectory(), key, tuned.EditModel.ResultsFileName, DataSourceLibraryViewModel.OptimizingChip);
    }

    /// <summary>The goal editor's catalog, analyses and measurements — the bench as it netlists.</summary>
    private (IReadOnlyList<GoalTemplate>, IReadOnlyList<Analysis>, IReadOnlyList<Measurement>) GoalContextFor(SchematicViewModel tuned)
    {
        if (PrepareTunedCircuit(tuned) is { Circuit.Tb: { } tb })
            return (GoalTemplates.For(tb), [.. tb.Analyses], [.. tb.Measurements]);
        return ([], [.. tuned.EditModel.Analyses], [.. tuned.EditModel.Measurements]);
    }

    /// <summary>
    /// The goal editor's preview (R-to10-4): the goal's expression over its analysis, evaluated at the
    /// optimizer's best values (or the schematic's) — offered only when the schematic has results, as a
    /// preview of results that do not exist would be a simulation the user did not ask for.
    /// </summary>
    private Func<OptimizationGoal, CancellationToken, Task<GoalPreview?>>? GoalPreviewFor(SchematicViewModel tuned)
    {
        if (!FindSchematicDocumentWithFrame(tuned, out var doc, out _)) return null;
        var sink = TuneSinkFor(RunBaseDirectory(), RunResultsWriter.SchematicKey(doc.FilePath, doc.Id),
                               tuned.EditModel.ResultsFileName);
        if (!File.Exists(sink.ResultsPath) || PrepareTunedCircuit(tuned) is not { Circuit.ReadError: null } p) return null;
        var circuit = p.Circuit;
        var values  = _factory.OptimizerTool?.Panel.BestValues;

        return (goal, ct) => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var rr = CircuitEvaluation.Evaluate(circuit, new CircuitEvaluationRequest
            {
                Tunables    = values,
                Analyses    = goal.Analysis is { } a ? [a] : [],
                Expressions = [goal.Expression],
            });
            return GoalPreviewOf(goal, rr);
        }, ct);
    }

    /// <summary>A real 1-D cube over the goal's axis (or a single number) as preview points; null otherwise.</summary>
    internal static GoalPreview? GoalPreviewOf(OptimizationGoal goal, RunResult rr)
    {
        if (rr.Expressions.Count == 0 || rr.Expressions[0].Value is not { } v) return null;
        double? Num(string? text) => text is { } t && TunableValue.TryParse(t, out _, out _, out double si) ? si : null;
        double? lo = Num(goal.Limit), hi = goal.LimitAtHi is null ? lo : Num(goal.LimitAtHi), up = Num(goal.UpperLimit);
        // A band written high-first is scored low-first (GoalResiduals.Score), and drawn that way.
        if (goal.Type is GoalType.In or GoalType.Out && lo > up) (lo, hi, up) = (up, up, lo);

        if (v.Kind == Core.Expressions.ValueKind.Real) return new GoalPreview([0], [v.AsReal()], lo, hi, up, goal.Type);
        if (v.Kind != Core.Expressions.ValueKind.Cube) return null;
        var cube = v.AsCube();
        if (cube.Rank != 1 || cube.DataKind != DataKind.Real) return null;
        var x = cube.Axes[0].Values;
        var y = cube.RealValues;
        if (goal.Range is { } r && Num(r.Lo) is { } rlo && Num(r.Hi) is { } rhi)
        {
            if (rlo > rhi) (rlo, rhi) = (rhi, rlo);
            var keep = Enumerable.Range(0, x.Length).Where(i => x[i] >= rlo && x[i] <= rhi).ToArray();
            if (keep.Length > 0) { x = [.. keep.Select(i => x[i])]; y = [.. keep.Select(i => y[i])]; }
        }
        return new GoalPreview(x, y, lo, hi, up, goal.Type);
    }

    /// <summary>Send to Tuning (R-to10-8): the Tuning panel, on the same schematic, takes the values.</summary>
    private void SendToTuning(IReadOnlyDictionary<string, string> values)
    {
        var optimized = _factory.OptimizerTool?.Panel.Tuned;
        if (_factory.TuningTool?.Panel is not { } tuning || optimized is null) return;
        if (!ReferenceEquals(tuning.Tuned, optimized))
            tuning.SetActiveSchematic(optimized, _factory.OptimizerTool?.Panel.HeaderLabel);
        ShowToolPanelCore(DockPanelIds.Tuning);
        tuning.LoadValues(values, "Optimizer best");
    }
}
