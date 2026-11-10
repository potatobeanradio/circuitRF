// ================================================================
//  PlotInspectorViewModel.Reload.cs  —  the panel re-read from its Plot
//
//  An undo of an edit (DataDisplayViewModel.EditHistory.cs) puts the
//  plot's settings and traces back directly, under this panel. Its cards
//  and the plot-wide values it caches are then re-read from the plot.
// ================================================================

using System;
using CircuitRF.Render.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

/// <summary>
/// What a redraw carries when the plot changed because its DATA did — a re-run, a snapshot — rather
/// than because someone edited it, so the edit recorder takes the result as it is instead of as an
/// undo step.
/// </summary>
internal sealed class DataRefreshEventArgs : EventArgs
{
    public static readonly DataRefreshEventArgs Instance = new();
}

public partial class PlotInspectorViewModel
{
    /// <summary>Re-reads every card and every plot-wide value from the plot after it was restored under the panel.</summary>
    internal void ReloadFromPlot()
    {
        foreach (var vm in Traces) vm.UnsubscribeFromLibrary();

#pragma warning disable MVVMTK0034
        _plotType      = _plot.PlotType;
        _freqUnit      = _plot.FreqUnits;
        _tableOptimum  = _plot.TableOptimum;
        _tableReadMode = _plot.TableReadMode;
#pragma warning restore MVVMTK0034

        foreach (var t in _plot.Traces) ResolveOrRebuild(t);
        RebuildTraces();
        if (IsSummaryTable) RebuildSummary();
        RebuildGhosts();
        ApplyTrialSelection();
        RefreshPolarDbUnitOptions();
        RefreshAxesEditors();
        OnPropertyChanged(string.Empty);
        RefreshAddCommand();
        PlotNeedsRedraw?.Invoke(this, EventArgs.Empty);
        PlotStructureChanged?.Invoke(this, EventArgs.Empty);
    }
}
