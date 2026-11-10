// ================================================================
//  PlotInspectorViewModel.Trials.cs  —  this plot's share of the trial
//  selection (brief-yield-9 R-ya9-6)
//
//  The selection is a source's, held by TrialSelection.Shared above every
//  display; each plot copies it onto the traces bound to that source and
//  redraws, so the renderer reads it off the trace like any other state.
// ================================================================

using System;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

public partial class PlotInspectorViewModel : ITrialSelectionListener
{
    /// <summary>Adds a plot a preset built beside this one — set by the container that holds this inspector, so a
    /// trace card can make a contribution Pareto (brief-yield-9 R-ya9-5). Null where there is no display.</summary>
    internal Func<CircuitRF.Render.DataDisplay.PlotContainerConfig, System.Threading.Tasks.Task>? AddPresetPlot { get; set; }

    /// <summary>Re-reads the shared selection and redraws when a trace on this plot shows trials.</summary>
    public void OnTrialSelectionChanged()
    {
        if (ApplyTrialSelection()) PlotNeedsRedraw?.Invoke(this, DataRefreshEventArgs.Instance);
    }

    /// <summary>Copies each source's selection onto the traces bound to it; true when any trace's changed.</summary>
    internal bool ApplyTrialSelection()
    {
        bool changed = false;
        foreach (var t in _plot.Traces)
        {
            var selected = TrialSelection.Shared.For(t.SourcePath);
            if (ReferenceEquals(t.SelectedTrials, selected)) continue;
            t.SelectedTrials = selected;
            changed = true;
        }
        return changed;
    }
}
