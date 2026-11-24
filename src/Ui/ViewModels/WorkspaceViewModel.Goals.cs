using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Design.Optimization;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>"Add as goal…" from a Data Display trace (brief-tuneopt-9 R-to9-4).</summary>
public partial class WorkspaceViewModel
{
    /// <summary>
    /// Receives the goal a trace pre-filled for the schematic whose run the trace is drawn from, and opens
    /// it in the Optimizer's goal editor (brief-tuneopt-10) on that schematic. Where no editor can be
    /// shown, the goal goes straight into the document as one undo step — and a goal the trace gave no
    /// limit (no visible marker) is added DISABLED, since an enabled goal with no limit is a check error.
    /// </summary>
    internal void AddGoalFromTrace(OptimizationGoal pre, string? sourcePath)
    {
        if (SchematicForResults(sourcePath) is not { } sd)
        {
            Messages.Warning("Add as goal: open the schematic these results came from, then add the goal again.");
            return;
        }

        ShowToolPanelCore(DockPanelIds.Optimizer);
        if (_factory.OptimizerTool?.Panel is { } optimizer)
        {
            var bench = sd.NavFrames[0].Session;
            if (!ReferenceEquals(optimizer.Tuned, bench)) optimizer.SetActiveSchematic(bench, InstancesRootHeaderOf(sd));
            var prefilled = pre.Clone();
            prefilled.Analysis ??= GoalTemplates.AnalysisReferencedBy(prefilled.Expression, bench.EditModel.Analyses, bench.EditModel.Measurements);
            if (optimizer.EditNewGoal(prefilled)) return;
        }

        var model = sd.ViewModel.EditModel;
        var goal  = pre.Clone();
        goal.Analysis ??= GoalTemplates.AnalysisReferencedBy(goal.Expression, model.Analyses, model.Measurements);
        var setup = model.Tuning?.Clone() ?? new TuningSetup();
        goal.Name = UniqueGoalName(goal.Name, setup.Goals);
        if (goal.Limit.Length == 0) goal.Enabled = false;
        setup.Goals.Add(goal);
        sd.ViewModel.Execute(new SetTuningSetupCommand(model, setup, $"Add goal {goal.Name}"));

        Messages.Info($"Added goal {goal.Name}: {goal.Expression}" +
                      (goal.Analysis is { } a ? $" · {a}" : "") +
                      (goal.Range is { } r ? $" · {r.Lo} to {r.Hi}" : "") +
                      (goal.Enabled ? $" · {goal.Type.ToString().ToLowerInvariant()} {goal.Limit}"
                                    : " · disabled until it has a limit"));
    }

    /// <summary>The open schematic whose run wrote <paramref name="sourcePath"/> — a results file is
    /// named by <see cref="RunResultsWriter.SchematicKey"/> — or null.</summary>
    private SchematicDocument? SchematicForResults(string? sourcePath)
    {
        if (sourcePath is null) return null;
        string key = Path.GetFileNameWithoutExtension(sourcePath);
        return _openDocsByPath.Values.OfType<SchematicDocument>().Concat(_scratchDocs)
            .FirstOrDefault(sd => string.Equals(RunResultsWriter.SchematicKey(sd.FilePath, sd.Id), key,
                                                StringComparison.OrdinalIgnoreCase));
    }

    private static string UniqueGoalName(string name, IEnumerable<OptimizationGoal> goals)
    {
        var taken = goals.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(name)) return name;
        for (int k = 2; ; k++)
            if (!taken.Contains($"{name}_{k}")) return $"{name}_{k}";
    }

    // ── LC Filter Designer: Send mask to Optimizer ──────────────────────────────────────────────

    /// <summary>Every workspace window alive, so a Designer — which belongs to none — can find the one
    /// holding the schematic it places into.</summary>
    private static readonly List<WeakReference<WorkspaceViewModel>> LiveWorkspaces = [];

    private static void RegisterLive(WorkspaceViewModel vm)
    {
        lock (LiveWorkspaces)
        {
            LiveWorkspaces.RemoveAll(w => !w.TryGetTarget(out _));
            LiveWorkspaces.Add(new WeakReference<WorkspaceViewModel>(vm));
        }
    }

    /// <summary>
    /// The LC Filter Designer's mask, sent to the Optimizer as goals on the bench <paramref name="schematic"/>
    /// belongs to (<see cref="FilterMaskGoals.For"/>): the Optimizer is shown on that bench, and the goals are
    /// added as ONE undo step. Goals an earlier send made (<c>mask_pass…</c>, <c>mask_stop…</c>) are replaced,
    /// so sending a changed mask does not leave the old one behind.
    /// </summary>
    /// <returns>What happened, for the Designer's status line.</returns>
    internal static string SendFilterMaskGoals(SchematicViewModel schematic, FilterMaskSpec mask,
                                                IReadOnlyList<TunableEntry> variables)
    {
        List<WorkspaceViewModel> live;
        lock (LiveWorkspaces)
            live = LiveWorkspaces.Select(w => w.TryGetTarget(out var t) ? t : null).OfType<WorkspaceViewModel>().ToList();
        foreach (var ws in live)
            if (ws.FindSchematicDocumentWithFrame(schematic, out var doc, out int frame))
                return ws.AddFilterMaskGoals(doc, mask, frame == 0 ? variables : [], frame == 0 ? 0 : variables.Count);
        return "The schematic is not open in a workspace window. Open it and send the mask again.";
    }

    private string AddFilterMaskGoals(SchematicDocument sd, FilterMaskSpec mask, IReadOnlyList<TunableEntry> variables,
                                      int variablesInsideACell)
    {
        var bench = sd.NavFrames[0].Session;
        var model = bench.EditModel;
        var made = FilterMaskGoals.For(mask, model.Analyses, model.Components);
        if (made.Refusal is { } refusal) { Messages.Warning("Send mask to Optimizer: " + refusal); return refusal; }
        if (made.Goals.Count == 0)
        {
            string why = "No band of the mask lies inside the analysis's sweep. " + string.Join(" ", made.Notes);
            Messages.Warning("Send mask to Optimizer: " + why);
            return why;
        }

        var setup = model.Tuning?.Clone() ?? new TuningSetup();
        int replaced = setup.Goals.RemoveAll(IsMaskGoal);
        foreach (var g in made.Goals)
        {
            g.Name = UniqueGoalName(g.Name, setup.Goals);
            setup.Goals.Add(g);
        }
        // The ladder Place filter put down: each part an optimizer variable. An entry the sheet already has keeps
        // its own settings and is only switched on for the optimizer.
        foreach (var v in variables)
        {
            if (setup.Variables.FirstOrDefault(e => e.Key == v.Key) is { } existing) existing.Opt = true;
            else setup.Variables.Add(v.Clone());
        }
        bench.Execute(new SetTuningSetupCommand(model, setup, "Add filter mask goals"));

        ShowToolPanelCore(DockPanelIds.Optimizer);
        if (_factory.OptimizerTool?.Panel is { } optimizer && !ReferenceEquals(optimizer.Tuned, bench))
            optimizer.SetActiveSchematic(bench, InstancesRootHeaderOf(sd));

        string summary = $"Sent the mask to the Optimizer as {made.Goals.Count} goal{(made.Goals.Count == 1 ? "" : "s")} on " +
                         $"dB({made.Analysis}.S({made.OutPort}, {made.InPort}))" +
                         (replaced > 0 ? $", replacing {replaced} from an earlier send" : "") + "." +
                         (variables.Count > 0 ? $" The placed ladder's {variables.Count} part{(variables.Count == 1 ? " is" : "s are")} its variables." : "") +
                         (variablesInsideACell > 0 ? " The ladder was placed inside a cell, so its parts were not made variables; add them in the Optimizer." : "");
        Messages.Info(summary + " " + string.Join("; ", made.Goals.Select(g =>
            $"{g.Name} {(g.Type == GoalType.Ge ? "≥" : "≤")} {g.Limit} dB over {g.Range!.Lo} to {g.Range.Hi}")));
        foreach (var n in made.Notes) Messages.Info("Send mask to Optimizer: " + n);
        return made.Notes.Count == 0 ? summary : summary + " " + string.Join(" ", made.Notes);
    }

    /// <summary>A goal an earlier mask send made.</summary>
    private static bool IsMaskGoal(OptimizationGoal g) =>
        g.Name.StartsWith("mask_pass", StringComparison.Ordinal) || g.Name.StartsWith("mask_stop", StringComparison.Ordinal);
}
