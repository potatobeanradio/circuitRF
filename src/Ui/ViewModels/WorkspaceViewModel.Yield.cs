using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CircuitRF.Design.Results;
using CircuitRF.Design.Statistics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Yield;
using Avalonia.Input.Platform;
using Dock.Model.Core;
using RfCore.Data;

namespace CircuitRF.Ui.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
//  The Yield panel (brief-yield-10). This file decides which schematic the panel works on — the Tuning
//  panel's rule — and hands it what it cannot reach itself: the bench prepared exactly as Simulate would
//  netlist it, the file a run's result is written beside, the displays a run publishes to, the Tuning
//  panel for "Send trial to Tuning", the Optimizer for "Edit goals…", the kit statistics and corner axes
//  the workspace knows, and the one-click yield display. The panel is YieldPanelViewModel.
// ─────────────────────────────────────────────────────────────────────────────

public partial class WorkspaceViewModel
{
    private YieldPanelViewModel?   _wiredYieldPanel;
    private ToleranceSurfaceRelay? _toleranceSurface;

    /// <summary>What every schematic session's Inspector and canvas ask about tolerances — forwarded to the current
    /// Yield panel, so a layout rebuild that replaces the panel does not strand older sessions.</summary>
    internal IToleranceSurface ToleranceSurface => _toleranceSurface ??= new ToleranceSurfaceRelay(
        () => _factory.YieldTool?.Panel, () => ShowToolPanelCore(DockPanelIds.Yield));

    /// <summary>The Tuning panel's rule (<see cref="RouteTuningPanel"/>): a schematic sets the panel, any other document
    /// keeps the last one, and only closing it empties the panel.</summary>
    private void RouteYieldPanel(IDockable? document)
    {
        WireYieldPanel();
        if (_factory.YieldTool?.Panel is not { } panel) return;

        if (document is SchematicDocument sd)
            panel.SetActiveSchematic(sd.NavFrames[0].Session, InstancesRootHeaderOf(sd));
        else if (!panel.HasSchematic && _lastActiveSchematicDoc is { } kept)
            panel.SetActiveSchematic(kept.NavFrames[0].Session, InstancesRootHeaderOf(kept));
    }

    private void ClearYieldPanel()
    {
        WireYieldPanel();
        _factory.YieldTool?.Panel.SetActiveSchematic(null, null);
    }

    private void WireYieldPanel()
    {
        var panel = _factory.YieldTool?.Panel;
        if (ReferenceEquals(panel, _wiredYieldPanel) || panel is null) return;
        _wiredYieldPanel = panel;

        panel.Defer          = a => Avalonia.Threading.Dispatcher.UIThread.Post(a, Avalonia.Threading.DispatcherPriority.Background);
        panel.PostToUi       = a => Avalonia.Threading.Dispatcher.UIThread.Post(a);
        panel.EditCommitted += OnAnalysesEditCommitted;   // ⌘Z with the panel focused (Tuning's rule)
        panel.PrepareCircuit = tuned => PrepareTunedCircuit(tuned)?.Circuit;
        panel.SourcePathFor  = YieldSourcePathFor;
        panel.DisplayFor     = path => new YieldDisplaySink(path, OpenDataDisplayLibraries, RefreshOpenDataDisplaysAsync);
        panel.KitStatisticsFor = YieldKitStatisticsFor;
        panel.KitAxesFor     = _ => AvailableCornerAxes;
        panel.OpenCornerPicker = () => ShowToolPanelCore(DockPanelIds.Analyses);
        panel.EditGoalInOptimizer = EditGoalInOptimizer;
        panel.SendToTuningTarget  = (values, label) => SendYieldTrialToTuning(panel, values, label);
        panel.SendToOptimizerTarget = (values, label) => SendDoeOptimumToOptimizer(panel, values, label);
        panel.SessionForDrawing   = d => SessionForTunedDrawing(d, openTab: true);   // Push of centred nominals
        panel.RerunTrial     = RerunYieldTrialAsync;
        panel.CopyText       = text => _ = CopyTextAsync(text);
        panel.SaveTrialAsCorner = (_, trial, result) =>
            panel.Tuned is { } vm ? SaveTrialAsCornerAsync(vm, trial, result) : Task.CompletedTask;
        panel.OpenYieldDisplay = OpenYieldDisplayAsync;
        panel.HasYieldDisplay  = (path, data) => YieldDisplayPath(path, data) is var cdd
                                                 && (File.Exists(cdd) || _openDocsByPath.ContainsKey(cdd));
        panel.ReportProblem     = text => Messages.Error($"Yield: {text}");   // issue #33
        panel.ReportMessages   = (summary, lines) =>
        {
            Messages.Info(summary);
            foreach (var line in lines) Messages.Info("  " + line);
        };
    }

    /// <summary>The file the design is: a saved schematic's own path, or — for one never saved — its key under the
    /// run directory's results folder, so a run's <c>.yield.npy</c> lands where Simulate's results do.</summary>
    private string? YieldSourcePathFor(SchematicViewModel tuned)
    {
        if (!FindSchematicDocumentWithFrame(tuned, out var doc, out _)) return null;
        if (doc.FilePath is { } path) return path;
        string dir = ResultsWriter.ResultsDirectory(RunBaseDirectory());
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, RunResultsWriter.SchematicKey(null, doc.Id) + ".csch");
    }

    private List<DataSourceLibraryViewModel> OpenDataDisplayLibraries()
        => [.. _openDocsByPath.Values.OfType<DataDisplayDocument>().Concat(_scratchDataDisplays)
               .Select(dd => dd.ViewModel.Window.DataSourceLibrary)];

    /// <summary>
    /// The Kit statistics row (R-ya10-3): the distribution calls the nominal design reaches, which selected sections
    /// brought them, and a note per kit axis whose statistical section is not selected. Asked only of a workspace that
    /// offers kit corners — elaborating the design costs a netlist, and a design with no kit has no row to fill.
    /// </summary>
    private KitStatisticsReport? YieldKitStatisticsFor(SchematicViewModel tuned)
    {
        if (AvailableCornerAxes.Count == 0) return null;
        if (PrepareTunedCircuit(tuned) is not { Circuit.ReadError: null } p) return null;
        var run = StatisticalRun.Create(p.Circuit, new StatisticalOptions { Mode = StatisticalMode.MonteCarlo });
        var selections = tuned.EditModel.CornerSelections;
        var bound = WorkspaceCorners.Bind(AvailableCornerAxes, selections, []);
        var axes = AvailableCornerAxes.Select(a =>
            new KitCornerAxisState(a.Label, a.AbsoluteFile, selections.GetValueOrDefault(a.Key)));
        return KitStatistics.Report(run.KitCalls, bound.Sections, axes);
    }

    /// <summary>Edit goals…: goals are authored in the Optimizer (yield overview D4) — focused on the Yield panel's
    /// schematic, with the goal selected.</summary>
    private void EditGoalInOptimizer(string? goal)
    {
        var bench = _factory.YieldTool?.Panel.Tuned;
        ShowToolPanelCore(DockPanelIds.Optimizer);
        if (_factory.OptimizerTool?.Panel is not { } optimizer || bench is null) return;
        if (!ReferenceEquals(optimizer.Tuned, bench)) optimizer.SetActiveSchematic(bench, _factory.YieldTool?.Panel.HeaderLabel);
        optimizer.SelectedGoal = goal is null ? optimizer.SelectedGoal : optimizer.Goals.FirstOrDefault(g => g.Name == goal);
    }

    /// <summary>Send trial to Tuning from the trial table: the Tuning panel, on the same schematic, takes the trial's
    /// values exactly as it takes the Optimizer's best point.</summary>
    private void SendYieldTrialToTuning(YieldPanelViewModel panel, IReadOnlyDictionary<string, string> values, string label)
    {
        if (_factory.TuningTool?.Panel is not { } tuning || panel.Tuned is not { } bench) return;
        if (!ReferenceEquals(tuning.Tuned, bench)) tuning.SetActiveSchematic(bench, panel.HeaderLabel);
        ShowToolPanelCore(DockPanelIds.Tuning);
        tuning.LoadValues(values, label);
    }

    /// <summary>Send to Optimizer from the DOE mode (brief-yield-14 R-ya14-7): the Optimizer, on the same schematic,
    /// starts its next runs from the model optimum.</summary>
    private void SendDoeOptimumToOptimizer(YieldPanelViewModel panel, IReadOnlyDictionary<string, string> values, string label)
    {
        if (_factory.OptimizerTool?.Panel is not { } optimizer || panel.Tuned is not { } bench) return;
        if (!ReferenceEquals(optimizer.Tuned, bench)) optimizer.SetActiveSchematic(bench, panel.HeaderLabel);
        ShowToolPanelCore(DockPanelIds.Optimizer);
        optimizer.StartFrom(values, label);
    }

    /// <summary>Re-run trial: the snapshot of every display holding the result (YA-9's action), or — with none open —
    /// the trial run again and reported, so the button never does nothing.</summary>
    private async Task<string?> RerunYieldTrialAsync(string path, int trial)
    {
        var library = OpenDataDisplayLibraries().FirstOrDefault(l => l.DataFor(path) is not null);
        if (library is not null) return await TrialActions.RerunAsync(library, path, trial);
        var mode = _factory.YieldTool?.Panel.Mode == YieldMode.MonteCarlo ? StatisticalMode.MonteCarlo : StatisticalMode.Yield;
        var (data, refusal) = await Task.Run(() => TrialReplay.Run(path, trial, mode));
        if (data is null) return refusal?.Render() ?? $"Trial {trial} could not be run again.";
        Messages.Info($"Trial {trial} re-run — open the yield display to see it beside the run.");
        return refusal?.Render();
    }

    private async Task CopyTextAsync(string text)
    {
        if (GetClipboard() is { } clipboard) await clipboard.SetTextAsync(text);
    }

    // ── One-click yield display (R-ya10-8) ─────────────────────────────────

    /// <summary>
    /// The display beside the result: <c>&lt;design&gt;.yield.cdd</c> next to <c>&lt;design&gt;.yield.npy</c> — or
    /// <c>&lt;design&gt;.yield.corners.cdd</c> for a run at each corner. A plain yield run and a run at each corner
    /// write the same <c>.yield.npy</c>, and each display's traces are made for one shape of it, so they are two files:
    /// opening one never shows the other shape's traces as invalid.
    /// </summary>
    private static string YieldDisplayPath(string resultPath, DataSet? result)
        => Path.ChangeExtension(Path.GetFullPath(resultPath),
                                result is not null && ResultContributions.StackedCorners(result) is not null ? ".corners.cdd" : ".cdd");

    /// <summary>
    /// Focuses the yield display over <paramref name="resultPath"/>, creating it first when it does not exist —
    /// composed by <see cref="YieldDisplayPreset"/> out of YA-8/YA-9's own trace-writing code and written beside the
    /// result, so it is an ordinary <c>.cdd</c> the user can edit and keep.
    /// </summary>
    private async Task OpenYieldDisplayAsync(string resultPath, DataSet result)
    {
        string cdd = YieldDisplayPath(resultPath, result);
        if (!_openDocsByPath.ContainsKey(cdd) && !File.Exists(cdd))
        {
            // A result with no trials has nothing to plot: said by name rather than written empty.
            if (DoeDisplayPreset.Responses(result).Count == 0 && !result.ContainsGroup(StatisticalDataSet.TrialsGroup))
            {
                Messages.Warning("This result has no trials to plot, so there is no yield display of it.", resultPath);
                return;
            }
            // A design of experiments' result draws the DOE display (brief-yield-14 R-ya14-6); every other, the yield's.
            var config = DoeDisplayPreset.Responses(result).Count > 0
                ? DoeDisplayPreset.Build(result, Path.GetFullPath(resultPath))
                : YieldDisplayPreset.Build(result, Path.GetFullPath(resultPath));
            try
            {
                await File.WriteAllTextAsync(cdd, JsonSerializer.Serialize(config, DataDisplayJson.Options),
                                             new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Messages.Warning($"Yield display could not be written: {ex.Message}", cdd);
                return;
            }
            _factory.ProjectTreeTool?.Refresh();
        }
        OpenOrActivateDataDisplay(cdd);
    }
}

/// <summary>A run's frames into every open Data Display holding its result, with the Yield chip (D14).</summary>
internal sealed class YieldDisplaySink(
    string path,
    Func<List<DataSourceLibraryViewModel>> libraries,
    Func<IReadOnlyList<string>, Task> refresh) : IYieldDisplay
{
    private readonly string _path = Path.GetFullPath(path);

    public void Publish(DataSet data)
    {
        foreach (var lib in libraries()) lib.Publish(_path, data, DataSourceLibraryViewModel.YieldChip);
    }

    public void Written(string written)
    {
        Drop();
        _ = refresh([written]);
    }

    public void Drop()
    {
        foreach (var lib in libraries())
        {
            lib.Unpublish(_path);
            lib.ClearSnapshot(_path);
        }
    }
}
